using System.Diagnostics;
using CombatSolver.Flash.Kernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Flash.Search;
using CombatSolver.Flash.Search.Scoring;

namespace CombatSolver.Flash.Bridge;

/// <summary>
/// The bridge half of the Flash progress/takeover contract. The solver publishes route metadata at
/// the existing search's checkpoints; this adapter turns it into the same <see cref="SolverProgress"/>
/// payload the existing search publishes (route preview, current-turn preview, route-adoption seed),
/// and polls the injected <c>SearchInteractionState</c> at the same checkpoints.
/// </summary>
internal static partial class FlashRouteMaterializer
{
    /// <summary>
    /// One Flash request's work contribution. The shared ledger sees the physical Flash expanded
    /// nodes and transitions, so a Flash member contributes exactly the search work it performed.
    /// The actual route/expanded numbers also remain in the <c>FLASH_ADOPTED</c> log. Transitions
    /// additionally include every existing-engine replay this request performed (admission,
    /// turn-setup, prefix, verification, materialization) so the ledger and the diagnostics
    /// describe the same physical work.
    /// </summary>
    private sealed class FlashWorkAccounting(
        SearchRequestWorkTotals? totals,
        DirectSearchPurpose? purpose,
        ContinuationPurpose? continuationPurpose,
        SearchFramePressureSignal framePressureSignal,
        SearchDiagnosticsSink diagnostics)
    {
        private readonly SearchDiagnosticsSink _diagnostics = diagnostics;
        private readonly SearchRequestWorkTotals? _totals = totals;
        private readonly DirectSearchPurpose? _purpose = purpose;
        private readonly ContinuationPurpose? _continuationPurpose = continuationPurpose;
        private readonly long _allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        private readonly int _gen0AtStart = GC.CollectionCount(0);
        private readonly int _gen1AtStart = GC.CollectionCount(1);
        private readonly int _gen2AtStart = GC.CollectionCount(2);
        private readonly TimeSpan _gcPauseAtStart = GC.GetTotalPauseDuration();
        private OpeningProbe? _probe;
        private long _replayTransitionsAtStart = -1;

        public SearchWorkPacer Pacer { get; } = new(framePressureSignal);

        public int Expanded;
        public int Transitions;
        public FlashProgressStats ProgressStats;

        public void Attach(OpeningProbe probe)
        {
            _probe = probe;
            _replayTransitionsAtStart = probe.AccountingForBridge.TransitionCount;
        }

        public void Record(TimeSpan elapsed)
        {
            if (_totals == null)
                return;
            long replayTransitions = _probe == null || _replayTransitionsAtStart < 0
                ? 0
                : Math.Max(0,
                    _probe.AccountingForBridge.TransitionCount - _replayTransitionsAtStart);
            long physicalTransitions = Transitions + replayTransitions;
            _totals.Record(new SearchSolverWorkContribution(
                Expanded,
                (int)Math.Min(int.MaxValue, physicalTransitions),
                0,
                elapsed,
                Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - _allocatedAtStart),
                Math.Max(0, GC.CollectionCount(0) - _gen0AtStart),
                Math.Max(0, GC.CollectionCount(1) - _gen1AtStart),
                Math.Max(0, GC.CollectionCount(2) - _gen2AtStart),
                MaxZero(GC.GetTotalPauseDuration() - _gcPauseAtStart),
                Pacer.MaxObservedGcPause),
                _continuationPurpose, _purpose);
            if (ProgressStats.ProgressEvents > 0)
            {
                _diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_PROGRESS_STATS " +
                    $"events={ProgressStats.ProgressEvents} " +
                    $"previews={ProgressStats.PreviewBuilds} " +
                    $"skips={ProgressStats.PreviewSkips} " +
                    $"mismatches={ProgressStats.PreviewMismatches} " +
                    $"preview_ms={ProgressStats.PreviewMilliseconds:0.0} " +
                    $"expanded={Expanded} transitions={physicalTransitions}");
            }
        }
    }

    /// <summary>
    /// Replays one route from the search root through the existing engine and verifies every step
    /// against the Flash projection. <paramref name="ownsStartSnapshot"/> is true when the caller
    /// created <paramref name="start"/> solely for this verification; every owned snapshot is
    /// released before returning or throwing.
    /// </summary>
    private static List<PlanAction> VerifyRoute(
        OpeningProbe probe,
        Player player,
        SimulationSnapshot start,
        FlashBattle startState,
        FlashRules rules,
        IReadOnlyList<PlanAction> prefix,
        FlashAction[] route,
        int potionUseCount,
        bool ownsStartSnapshot,
        SearchDiagnosticsSink? diagnostics = null)
    {
        List<PlanAction> plan = new(prefix.Count + route.Length);
        plan.AddRange(prefix);
        SimulationSnapshot snapshot = start;
        bool owns = ownsStartSnapshot;
        FlashBattle state = startState;
        try
        {
            for (int step = 0; step < route.Length; step++)
            {
                FlashAction action = route[step];
                PlanAction planned;
                SimulationSnapshot child;
                try
                {
                    child = FlashChoiceTranslator.Replay(probe.ReplayCoreForBridge, snapshot, player,
                        action, out planned);
                }
                catch (FlashChoiceMismatchException ex)
                {
                    diagnostics?.Info(
                        $"[CombatSolver/Flash] FLASH_VERIFY_STEP step={step} kind=translate " +
                        $"action={action.Kind}:{action.HandIndex}:{action.Target} detail={ex.Message}");
                    throw new FlashVerificationException(step, [ex.Message]);
                }
                if (owns)
                    snapshot.ReleaseSimulator();
                snapshot = child;
                owns = true;
                plan.Add(planned);
                FlashBattle beforeFlash = state;
                if (FlashKernel.Step(ref state, action, rules, out _) != FlashStepResult.Completed)
                    throw new InvalidOperationException("已选路线的 Flash 动作缺少选择。");
                FlashBattle projected;
                try
                {
                    projected = FlashStateImporter.Import(snapshot, player, rules, potionUseCount);
                }
                catch (FlashImportException ex)
                {
                    throw new FlashVerificationException(step,
                        [$"旧引擎状态无法投影：{ex.Reason} {ex.Detail}"]);
                }
                if (!FlashFingerprint.Equal(projected, state))
                {
                    string actionText = $"{planned.Kind}:{planned.CardId}" +
                        $"#{planned.CardOccurrence}->{planned.TargetCombatId}";
                    string planText = string.Join(",", plan.Select(action =>
                        $"{action.Turn}:{action.Kind}:{action.CardId}->{action.TargetCombatId}"));
                    List<string> differences = FlashBattleDump.Diff(projected, state, "old", "flash");
                    diagnostics?.Info(
                        $"[CombatSolver/Flash] FLASH_VERIFY_STEP step={step} action={actionText} " +
                        $"plan={planText} differences={string.Join(" | ", differences)}");
                    if (diagnostics != null)
                    {
                        diagnostics.Info(
                            $"[CombatSolver/Flash] FLASH_VERIFY_BEFORE step={step} " +
                            string.Join(" | ", FlashBattleDump.Lines(beforeFlash)));
                    }
                    throw new FlashVerificationException(step,
                        [$"action={actionText}", $"plan={planText}", .. differences])
                    {
                        Action = actionText,
                        Plan = planText,
                        StateDifferences = differences,
                        StateBefore = [.. FlashBattleDump.Lines(beforeFlash)],
                        OldAfter = [.. FlashBattleDump.Lines(projected)],
                        FlashAfter = [.. FlashBattleDump.Lines(state)],
                    };
                }
            }
            return plan;
        }
        finally
        {
            if (owns)
                snapshot.ReleaseSimulator();
        }
    }

    private sealed class FlashProgressBridge(
        OpeningProbe probe,
        Player player,
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        IReadOnlyList<TurnSetupRootCandidate> searchRoots,
        FlashBattle[] battles,
        FlashRules rules,
        IReadOnlyList<PlanAction> prefix,
        int potionUseCount,
        Action<SolverProgress>? progressCallback,
        SearchWorkPacer? pacer,
        CancellationToken cancellationToken)
    {
        private readonly SearchWorkPacer? _pacer = pacer;
        private const int PreviewIntervalMilliseconds = SolverWeights.ProgressUiIntervalMilliseconds;
        private readonly long _startedAt = Environment.TickCount64;
        private SolverSpeculativeRoutePreview? _preview;
        private SolverCurrentTurnPreview? _currentTurnPreview;
        private SolverRouteAdoptionSeed? _seed;
        private SolverRouteAdoptionSeed? _requestedSeed;
        private int _previewCandidateVersion = int.MinValue;
        private int _currentTurnVersion;
        private long _lastPreviewAt = long.MinValue / 2;
        private SolverInterimResult? _currentBest;
        private int _currentBestVersion = int.MinValue;
        private int _progressEvents;
        private int _previewBuilds;
        private int _previewSkips;
        private int _previewMismatches;
        private bool _previewMismatchLogged;
        private long _previewBuildTicks;
        private long _lastProgressLogAt = long.MinValue / 2;
        private SearchTakeoverRequest? _loggedTakeover;

        /// <summary>The seed the player asked to adopt; the search stops and this is materialized.</summary>
        public SolverRouteAdoptionSeed? RequestedSeed => _requestedSeed;

        /// <summary>
        /// The same three-way decision the existing search's checkpoints read from
        /// <c>_interaction?.CurrentTakeoverRequest</c>. A displayed-route request without a seed (which
        /// the UI never sends) is ignored, exactly like the existing search's seed-null condition.
        /// </summary>
        public FlashTakeoverAction Poll()
        {
            SearchTakeoverRequest? request = policy.Interaction?.CurrentTakeoverRequest;
            if (request == null)
                return FlashTakeoverAction.None;
            if (request.Kind == SearchTakeoverKind.AdoptRoute)
            {
                if (request.RouteAdoptionSeed == null)
                    return FlashTakeoverAction.None;
                _requestedSeed = request.RouteAdoptionSeed;
                LogTakeover(request, "AdoptRoute");
                return FlashTakeoverAction.AdoptRoute;
            }
            LogTakeover(request, "ApplyCurrentTurn");
            return FlashTakeoverAction.ApplyCurrentTurn;
        }

        /// <summary>Publishes one existing-shape progress object, rebuilding the preview only when needed.</summary>
        public void Publish(FlashProgressSnapshot snapshot)
        {
            Action<SolverProgress>? callback = progressCallback;
            if (callback == null)
                return;
            _progressEvents++;
            if (snapshot.HasRoute)
                MaybeBuildPreview(snapshot);
            MaybeBuildInterim(snapshot);
            callback(new SolverProgress(
                root.StartTurnNumber,
                snapshot.CurrentTurnNumber,
                snapshot.CompletedTurnLayers,
                snapshot.PlayDepth,
                snapshot.ExpandedNodes,
                snapshot.ExpandedNodes,
                snapshot.MaxNodes,
                snapshot.FrontierNodes,
                snapshot.EndedNodes,
                snapshot.ElapsedMilliseconds,
                snapshot.Phase,
                CurrentBestResult: _currentBest,
                CurrentTurnPreview: _currentTurnPreview,
                SpeculativeRoutePreview: _preview,
                RouteAdoptionSeed: _seed,
                RequestBudgetMilliseconds: profile.SoftTimeBudgetMilliseconds));
        }

        /// <summary>Materializes the exact seed the player requested (route-adoption takeover).</summary>
        public SolverResult MaterializeRequestedSeed()
        {
            SolverResult result = _requestedSeed?.Materialize()
                ?? throw new InvalidOperationException("路线采纳请求没有可物化的候选种子。");
            policy.Diagnostics.Info(
                $"[CombatSolver/Flash] FLASH_TAKEOVER_MATERIALIZED kind=AdoptRoute " +
                $"scope={result.ResultScope} actions={result.BestNode.ActionCount}");
            return result;
        }

        private void LogTakeover(SearchTakeoverRequest request, string kind)
        {
            if (ReferenceEquals(_loggedTakeover, request))
                return;
            _loggedTakeover = request;
            policy.Diagnostics.Info(
                $"[CombatSolver/Flash] FLASH_TAKEOVER kind={kind} " +
                $"seed={(request.RouteAdoptionSeed == null ? "-" : request.RouteAdoptionSeed.CandidateVersion)} " +
                $"stop_after_result={request.StopAfterResult}");
        }

        /// <summary>
        /// The existing search publishes a <see cref="SolverInterimResult"/> only once a complete
        /// victory candidate exists (<c>ConsiderCompleteVictory</c>). The bridge derives the same
        /// display/ledger values from the K4-checked route features; the route itself was already
        /// replayed to build the preview.
        /// </summary>
        private void MaybeBuildInterim(FlashProgressSnapshot snapshot)
        {
            if (!snapshot.HasFeatures || snapshot.ActionCount <= 0
                || snapshot.CandidateVersion == _currentBestVersion)
            {
                return;
            }
            FlashScoreFeatures features = snapshot.Features;
            bool won = SolverInterimResultOrdering.IsCompleteVictory(
                snapshot.ActionCount, features.AllEnemiesDead, features.PlayerDead,
                features.ProjectedPlayerHp);
            if (!won)
                return;
            int relicHeal = ActEndingBossPolicy.RankedPostCombatRelicHeal(
                root.PostCombatRelicHeal, true, features.PlayerHp, features.PlayerMaxHp);
            int strategicHpDeficit = ActEndingBossPolicy.StrategicHpDeficit(
                features.CumulativePlayerHpLost,
                Math.Max(0, root.InitialPlayerMaxHp - features.PlayerMaxHp),
                features.RecoveredPlayerHp + relicHeal,
                root.BossHpRelief,
                features.DeathSaveHpRestored) - features.StrategicHpCredit;
            _currentBest = new SolverInterimResult(
                Won: true,
                OutstandingStolenResource: features.OutstandingStolenResource,
                ProjectedBattleHpLost: battleDamage.HpLostSoFar + features.CumulativePlayerHpLost,
                StrategicHpDeficit: strategicHpDeficit,
                PotionStrategicCost: PotionUsePolicy.EffectiveStrategicHpCost(
                    features.PotionStrategicCost, ambergrisCount: 0, root.InitialPlayerMaxHp),
                ProjectedBattlePotionCount: battleDamage.PotionsUsedSoFar + features.PotionUseCount,
                CombatEndedTurn: features.Turn,
                EnemyHp: features.EnemyHp,
                Score: features.Score)
            {
                GrowthHpCredit = features.StrategyGoalHpCredit,
                TheftPolicy = policy.TheftPolicy,
                GrowthRewardCount = features.StrategyGoalCount,
                Survives = !features.PlayerDead && features.ProjectedPlayerHp > 0,
                DeathSaveUseCount = features.ProjectedDeathSaveUseCount,
            };
            _currentBestVersion = snapshot.CandidateVersion;
        }

        private void MaybeBuildPreview(FlashProgressSnapshot snapshot)
        {
            if (snapshot.CandidateVersion == _previewCandidateVersion)
                return;
            long now = Environment.TickCount64;
            if (_preview != null && now - _lastPreviewAt < PreviewIntervalMilliseconds)
                return;
            long started = Stopwatch.GetTimestamp();
            bool built;
            SolverSpeculativeRoutePreview preview;
            SolverCurrentTurnPreview? currentTurn;
            List<PlanAction> actions;
            try
            {
                built = TryBuildPreview(snapshot, out preview, out currentTurn, out actions);
            }
            catch (FlashChoiceMismatchException ex)
            {
                // A speculative route that the existing engine cannot replay is simply not displayed;
                // the selected route is still verified before materialization. One diagnostic per
                // solve; non-exception skips must not suppress the first real mismatch.
                _previewMismatches++;
                if (!_previewMismatchLogged)
                {
                    _previewMismatchLogged = true;
                    int step = ex.Data.Contains("step") && ex.Data["step"] is int value ? value : -1;
                    string routeTrace = string.Join(" > ", snapshot.Route.Take(step + 1).Select((routeAction, index) =>
                        $"{index}:{routeAction.Kind}" +
                        (routeAction.Kind == FlashActionKind.PlayCard
                            ? $"(hand={routeAction.HandIndex},target={routeAction.Target},choices={routeAction.ChoiceCount})"
                            : string.Empty)));
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Flash] FLASH_PREVIEW_MISMATCH step={step} " +
                        $"detail={ex.Message} route={routeTrace} " +
                        $"oldHand={ex.Data["oldHand"] ?? "-"} flashHandIndex={ex.Data["flashHandIndex"] ?? "-"}");
                }
                built = false;
                preview = null!;
                currentTurn = null;
                actions = [];
            }
            _previewBuildTicks += Stopwatch.GetTimestamp() - started;
            if (!built)
            {
                _previewSkips++;
                return;
            }
            _preview = preview;
            _currentTurnPreview = currentTurn;
            _previewCandidateVersion = snapshot.CandidateVersion;
            _lastPreviewAt = now;
            _previewBuilds++;
            if (now - _lastProgressLogAt >= 2_000)
            {
                _lastProgressLogAt = now;
                policy.Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_PROGRESS candidate={snapshot.CandidateVersion} " +
                    $"turn={root.StartTurnNumber + snapshot.CompletedTurnLayers} " +
                    $"layer={snapshot.CompletedTurnLayers} depth={snapshot.PlayDepth} " +
                    $"expanded={snapshot.ExpandedNodes} actions={preview.Turns.Sum(turn => turn.Actions.Count)} " +
                    $"turns={preview.Turns.Count} hp_lost={preview.ProjectedBattleHpLost} " +
                    $"potions={preview.ProjectedBattlePotionCount}");
            }
            FlashAction[] route = snapshot.Route;
            int rootIndex = snapshot.RootIndex;
            _seed = new SolverRouteAdoptionSeed(preview.CandidateVersion, actions,
                () => MaterializeAdoptedRoute(
                    probe, player, root, displayNames, battleDamage, policy,
                    searchRoots, battles, rules, prefix, rootIndex, route, actions,
                    potionUseCount, SolverResultScope.RouteAdoption, cancellationToken));
        }

        private bool TryBuildPreview(FlashProgressSnapshot snapshot,
            out SolverSpeculativeRoutePreview preview,
            out SolverCurrentTurnPreview? currentTurn,
            out List<PlanAction> displayActions)
        {
            preview = null!;
            currentTurn = null;
            displayActions = [];
            List<PlanAction> actions = new(prefix.Count + snapshot.Route.Length);
            List<PreviewTurn> turns = [];
            SimulationSnapshot? current = null;
            bool ownsCurrent = false;
            try
            {
                TurnSetupRootCandidate selectedRoot = searchRoots[snapshot.RootIndex];
                current = policy.IncludeTurnSetup
                    ? probe.ReplayTurnSetupForFlash(selectedRoot.Choices)
                    : probe.ReplayForFlash([], null);
                ownsCurrent = true;
                int currentTurnNumber = -1;
                int turnStartHpLost = 0, turnStartRecovered = 0, turnStartEnemyHp = 0;
                int turnActionStart = 0;
                void FinishTurn()
                {
                    if (currentTurnNumber < 0 || !ownsCurrent || current == null)
                        return;
                    SimulationSnapshot end = current;
                    turns.Add(new PreviewTurn(
                        currentTurnNumber,
                        Math.Max(0, end.CumulativePlayerHpLost - turnStartHpLost),
                        Math.Max(0, end.RecoveredPlayerHp - turnStartRecovered),
                        Math.Max(0, turnStartEnemyHp - end.EnemyHp),
                        end.Energy,
                        end.AllEnemiesDead,
                        turnActionStart,
                        actions.Count - turnActionStart));
                }
                void Observe(PlanAction planned, SimulationSnapshot before, SimulationSnapshot after)
                {
                    if (planned.Turn != currentTurnNumber)
                    {
                        FinishTurn();
                        currentTurnNumber = planned.Turn;
                        turnStartHpLost = before.CumulativePlayerHpLost;
                        turnStartRecovered = before.RecoveredPlayerHp;
                        turnStartEnemyHp = before.EnemyHp;
                        turnActionStart = actions.Count;
                    }
                    actions.Add(planned);
                }
                foreach (PlanAction action in prefix)
                {
                    SimulationSnapshot child = probe.ReplayForFlash([action], current);
                    SimulationSnapshot before = current;
                    bool ownsBefore = ownsCurrent;
                    current = child;
                    ownsCurrent = true;
                    Observe(action, before, child);
                    if (ownsBefore)
                        before.ReleaseSimulator();
                }
                for (int step = 0; step < snapshot.Route.Length; step++)
                {
                    FlashAction action = snapshot.Route[step];
                    SimulationSnapshot child;
                    PlanAction planned;
                    try
                    {
                        child = FlashChoiceTranslator.Replay(
                            probe.ReplayCoreForBridge, current, player, action, out planned);
                    }
                    catch (FlashChoiceMismatchException ex)
                    {
                        ex.Data["step"] = step;
                        throw;
                    }
                    SimulationSnapshot before = current;
                    bool ownsBefore = ownsCurrent;
                    current = child;
                    ownsCurrent = true;
                    Observe(planned, before, child);
                    if (ownsBefore)
                        before.ReleaseSimulator();
                    _pacer?.YieldIfNeeded();
                }
                FinishTurn();
                if (currentTurnNumber < 0 || actions.Count == 0 || current == null)
                    return false;
                // Capture the final scalars before the last owned snapshot is released.
                int finalPotionUseCount = current.PotionUseCount;
                int finalHpLost = current.CumulativePlayerHpLost;
                int finalCombatEndedTurn = current.CombatEndedTurn ?? -1;
                bool finalPlayerDead = current.PlayerDead;
                int finalProjectedHp = current.ProjectedPlayerHp;
                bool finalHasRisk = current.HasRisk;
                displayActions = actions.Select(WithDisplayNames).ToList();
                IReadOnlyList<PlanCardChoice> setupChoices = selectedRoot.Choices
                    .Select(WithDisplayNames)
                    .ToArray();
                SolverFrontierTurn[] frontierTurns = new SolverFrontierTurn[turns.Count];
                for (int index = 0; index < turns.Count; index++)
                {
                    PreviewTurn turn = turns[index];
                    frontierTurns[index] = new SolverFrontierTurn(
                        turn.Turn,
                        displayActions.Skip(turn.ActionStart).Take(turn.ActionCount).ToArray(),
                        turn.HpLost,
                        turn.HpRecovered,
                        turn.EnemyHpLost,
                        turn.EnergyLeft,
                        turn.CombatEnded)
                    {
                        TurnStartChoices = SolverFrontierTurn.ChoicesForTurn(
                            turn.Turn, root.StartTurnNumber, setupChoices, displayActions)
                            .Select(WithDisplayNames)
                            .ToArray(),
                    };
                }
                bool onlyDeath = finalPlayerDead || finalProjectedHp <= 0;
                preview = new SolverSpeculativeRoutePreview(
                    snapshot.CandidateVersion,
                    root.StartTurnNumber,
                    battleDamage.PotionsUsedSoFar + finalPotionUseCount,
                    battleDamage.HpLostSoFar + finalHpLost,
                    finalCombatEndedTurn >= 0,
                    onlyDeath,
                    finalHasRisk,
                    frontierTurns);
                PreviewTurn? currentTurnStat = null;
                for (int index = 0; index < turns.Count; index++)
                {
                    if (turns[index].Turn == root.StartTurnNumber)
                    {
                        currentTurnStat = turns[index];
                        break;
                    }
                }
                if (currentTurnStat is { } stat)
                {
                    currentTurn = new SolverCurrentTurnPreview(
                        ++_currentTurnVersion,
                        root.StartTurnNumber,
                        displayActions.Skip(stat.ActionStart).Take(stat.ActionCount).ToArray(),
                        stat.HpLost,
                        stat.HpRecovered,
                        stat.EnemyHpLost,
                        stat.EnergyLeft,
                        stat.CombatEnded,
                        frontierTurns)
                    {
                        TurnStartChoices = setupChoices,
                    };
                }
                return true;
            }
            finally
            {
                if (ownsCurrent && current != null)
                    current.ReleaseSimulator();
            }
        }

        private readonly record struct PreviewTurn(
            int Turn,
            int HpLost,
            int HpRecovered,
            int EnemyHpLost,
            int EnergyLeft,
            bool CombatEnded,
            int ActionStart,
            int ActionCount);

        public FlashProgressStats Stats => new(_progressEvents, _previewBuilds, _previewSkips,
            _previewMismatches, _previewBuildTicks * 1000d / Stopwatch.Frequency);

        private PlanAction WithDisplayNames(PlanAction action)
            => action with
            {
                Choice = action.Choice == null ? null : WithDisplayNames(action.Choice),
                NestedChoices = action.NestedChoices?.Select(WithDisplayNames).ToArray(),
                TurnStartChoices = action.TurnStartChoices?.Select(WithDisplayNames).ToArray(),
            };

        private PlanCardChoice WithDisplayNames(PlanCardChoice choice)
            => choice with
            {
                Cards = choice.Cards
                    .Select(card => card with
                    {
                        Title = displayNames.Card(card.CardId, card.UpgradeLevel),
                    })
                    .ToArray(),
            };
    }

    /// <summary>
    /// Replays a candidate route from the search root through the existing engine and verifies every
    /// step against the Flash projection, then materializes the verified prefix + route on the shared
    /// materializer. Used both for the selected route and for a route-adoption seed materialized after
    /// the search has stopped.
    /// </summary>
    /// <summary>
    /// Semantic <see cref="PlanAction"/> comparison. The record's collection fields are interfaces,
    /// whose record equality is reference equality; the preview builds them as List/array instances
    /// while the verification replay allocates its own. Card identity, choice identity and timing are
    /// compared field by field instead.
    /// </summary>
    private static bool SamePlanActions(IReadOnlyList<PlanAction> left, IReadOnlyList<PlanAction> right,
        int offset)
    {
        if (left.Count != right.Count)
            return false;
        for (int index = offset; index < left.Count; index++)
        {
            if (!SamePlanAction(left[index], right[index]))
                return false;
        }
        return true;
    }

    private static bool SamePlanAction(PlanAction left, PlanAction right)
        => left.Kind == right.Kind
            && left.Turn == right.Turn
            && string.Equals(left.CardId, right.CardId, StringComparison.Ordinal)
            && left.CardOccurrence == right.CardOccurrence
            && left.TargetIndex == right.TargetIndex
            && left.TargetCombatId == right.TargetCombatId
            && string.Equals(left.CardStateKey, right.CardStateKey, StringComparison.Ordinal)
            && left.CardStateOccurrence == right.CardStateOccurrence
            && left.CardUpgradeLevel == right.CardUpgradeLevel
            && string.Equals(left.CardEnchantmentId, right.CardEnchantmentId, StringComparison.Ordinal)
            && left.PotionSlot == right.PotionSlot
            && string.Equals(left.PotionId, right.PotionId, StringComparison.Ordinal)
            && left.ReplayCount == right.ReplayCount
            && left.EndsPlayerTurn == right.EndsPlayerTurn
            && SameChoice(left.Choice, right.Choice)
            && SameChoices(left.NestedChoices, right.NestedChoices)
            && SameChoices(left.TurnStartChoices, right.TurnStartChoices);

    private static bool SameChoices(IReadOnlyList<PlanCardChoice>? left,
        IReadOnlyList<PlanCardChoice>? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left == null || right == null || left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (!SameChoice(left[index], right[index]))
                return false;
        }
        return true;
    }

    private static bool SameChoice(PlanCardChoice? left, PlanCardChoice? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left == null || right == null)
            return false;
        if (left.Effect != right.Effect
            || left.SourcePile != right.SourcePile
            || !string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal)
            || !string.Equals(left.ContextId, right.ContextId, StringComparison.Ordinal)
            || left.Timing != right.Timing
            || left.Cards.Count != right.Cards.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Cards.Count; index++)
        {
            PlanCardToken a = left.Cards[index];
            PlanCardToken b = right.Cards[index];
            if (!string.Equals(a.CardId, b.CardId, StringComparison.Ordinal)
                || a.UpgradeLevel != b.UpgradeLevel
                || !string.Equals(a.StateKey, b.StateKey, StringComparison.Ordinal)
                || a.SourceOccurrence != b.SourceOccurrence
                || a.OptionOccurrence != b.OptionOccurrence)
            {
                return false;
            }
        }
        return true;
    }

    private static SolverResult MaterializeAdoptedRoute(
        OpeningProbe probe,
        Player player,
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        IReadOnlyList<TurnSetupRootCandidate> searchRoots,
        FlashBattle[] battles,
        FlashRules rules,
        IReadOnlyList<PlanAction> prefix,
        int rootIndex,
        FlashAction[] route,
        IReadOnlyList<PlanAction> seedActions,
        int potionUseCount,
        SolverResultScope scope,
        CancellationToken cancellationToken)
    {
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        TurnSetupRootCandidate selectedRoot = searchRoots[rootIndex];
        SimulationSnapshot start = selectedRoot.Snapshot;
        List<PlanAction> plan = VerifyRoute(probe, player, start, battles[rootIndex], rules, prefix,
            route, potionUseCount, ownsStartSnapshot: false, diagnostics: policy.Diagnostics);
        if (scope == SolverResultScope.RouteAdoption)
        {
            // The published seed's route tail must be exactly the route the existing engine just
            // verified; otherwise the result would advertise actions it did not replay. The prefix
            // is displayed from the request's own actions, so only the Flash tail is compared.
            int offset = prefix.Count;
            if (plan.Count != seedActions.Count || !SamePlanActions(plan, seedActions, offset))
            {
                List<string> differences = [$"route-adoption actions old={plan.Count} seed={seedActions.Count}"];
                int shared = Math.Min(plan.Count, seedActions.Count);
                for (int index = offset; index < shared; index++)
                {
                    if (!SamePlanAction(plan[index], seedActions[index]))
                    {
                        differences.Add($"index={index} old={plan[index]} seed={seedActions[index]}");
                        break;
                    }
                }
                throw new FlashVerificationException(plan.Count, differences);
            }
        }
        return MaterializeFixedPlan(probe, root, displayNames, battleDamage, policy,
            potionPolicyOverride: null, potionFreePolicyBaseline: null,
            maximumPotionUses: null, minimumPotionUses: null, earliestPotionTurn: null,
            selectedRoot.Choices.Count == 0 ? null : selectedRoot.Choices,
            selectedRoot.Snapshot, plan, cancellationToken, scope,
            scope == SolverResultScope.RouteAdoption ? seedActions : null, out _);
    }
}

/// <summary>Preview-materialization work of one Flash request (diagnostic; never search policy).</summary>
internal readonly record struct FlashProgressStats(
    int ProgressEvents,
    int PreviewBuilds,
    int PreviewSkips,
    int PreviewMismatches,
    double PreviewMilliseconds);
