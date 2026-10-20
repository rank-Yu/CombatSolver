using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

/// <summary>
/// The single replay core. Both the existing Beam search and the opening probes run
/// <see cref="Replay"/>/<see cref="ReplayAction"/>/<see cref="ReplayTurnSetup"/>/
/// <see cref="ResolveTurnSetupChoices"/> here, over an explicit injected context. Beam-only
/// checkpoints, execution-continuation frames, replay evidence, performance accounting and strict
/// incremental verification are optional hooks: callers that do not supply them get the same
/// combat semantics without the cache/measurement machinery.
/// </summary>
internal sealed class SearchReplayCore(SearchReplayOptions options)
{
    private readonly SearchReplayOptions _options = options;

    public CombatRootSnapshot Root => _options.Root;
    public SolverDisplayNames DisplayNames => _options.DisplayNames;
    public SearchPolicySnapshot Policy => _options.Policy;
    public Player Player => _options.Player;
    public int StartTurnNumber => _options.StartTurnNumber;
    public IntentForecast Forecast => _options.Forecast;
    public SearchEvaluator Evaluator => _options.Evaluator;
    public SearchRoundContext RoundContext => _options.RoundContext;
    public SearchReplayAccounting Accounting => _options.Accounting;
    public CancellationToken CancellationToken { get; set; } = options.CancellationToken;
    public bool IncludeTurnSetup => _options.IncludeTurnSetup;
    public SearchDiagnosticsSink Diagnostics => _options.Diagnostics;
    public bool DetailedDiagnostics => _options.DetailedDiagnostics;
    public bool VerifyIncrementalSearch => _options.VerifyIncrementalSearch;
    public bool DisableCardChoiceContinuations => _options.DisableCardChoiceContinuations;
    public SolverSearchProfile Profile { get; init; } = SolverSearchProfile.Default;
    public SearchReplayTestingState Testing { get; } = new();

    // Replay cache/continuation ownership. Null means "do not use this optimization".
    public object? ParallelActionReplayForkGate;
    public ExecutionChoiceReplayCheckpoint? ExecutionChoiceCheckpoint;
    public PotionChoiceReplayCheckpoint? PotionChoiceCheckpoint;
    public CardChoiceReplayCheckpoint? CardChoiceCheckpoint;
    public RoundReplayCheckpoint? RoundCheckpoint;

    // Beam-only testing callbacks. Unknown continuation defects still fail explicitly.
    public Action<SearchNode, PlanAction, Exception>?
        VerifyRejectedChoiceContinuationStep;
    public Action<SearchNode, PlanAction, SimulationSnapshot>?
        VerifyChoiceContinuationStep;

    private SimulationSnapshot Snapshot(
        CombatPredictionSimulator simulator,
        int turn,
        int actionCount,
        int shufflesCrossed,
        SearchBoundaryReason boundary,
        IReadOnlySet<uint> processedEnemyDeaths)
        => Evaluator.Snapshot(
            simulator, turn, actionCount, shufflesCrossed, boundary, processedEnemyDeaths);

    private void YieldIfNeeded() => Accounting.WorkPacer?.YieldIfNeeded();

    private void ThrowIfCancellationRequested() => CancellationToken.ThrowIfCancellationRequested();

    internal IReadOnlyList<TurnSetupRootCandidate> CaptureTurnSetupRoots()
        => BuildTurnSetupRoots()
            .Select(root => new TurnSetupRootCandidate(root.Choices, root.Snapshot))
            .ToArray();

    internal SimulationSnapshot CaptureRootSnapshot()
    {
        Accounting.ReplayCount++;
        CombatPredictionSimulator simulator = Root.ForkSimulator();
        ForkableSet<uint> processedEnemyDeaths = [];
        foreach (Creature enemy in Root.Enemies)
        {
            if (enemy.CombatId is uint combatId && simulator.State.GetCreature(enemy).IsDead)
                processedEnemyDeaths.Add(combatId);
        }
        return Snapshot(
            simulator,
            StartTurnNumber,
            actionCount: 0,
            shufflesCrossed: 0,
            SearchBoundaryReason.None,
            processedEnemyDeaths);
    }

    internal SimulationSnapshot ReplayTurnSetupForFlash(IReadOnlyList<PlanCardChoice> choices)
    {
        SimulationSnapshot snapshot = ReplayTurnSetup(choices);
        if (snapshot.BoundaryReason != SearchBoundaryReason.None)
        {
            SearchBoundaryReason boundary = snapshot.BoundaryReason;
            snapshot.ReleaseSimulator();
            throw new InvalidPlannedChoiceBranchException(
                $"回合准备选择回放没有到达稳定出牌阶段：boundary={boundary}。");
        }
        return snapshot;
    }

    internal SimulationSnapshot ReplayForFlash(
        IReadOnlyList<PlanAction> actions,
        SimulationSnapshot? parent)
        => Replay(actions, parent, parent?.Turn ?? 0, countTransition: false, allowExecutionCapture: false);

    internal SimulationSnapshot? ReplayFixedPrefix(
        SimulationSnapshot rootSnapshot,
        IReadOnlyList<PlanAction> prefix,
        SearchExpansionContext expansionContext,
        bool resetSchedulingBaseline = false)
        => ReplayFixedPrefixNode(rootSnapshot, prefix, expansionContext,
            turnSetupChoices: null, turnSetupPlayState: null, resetSchedulingBaseline)?.Snapshot;

    internal SearchNode? ReplayFixedPrefixNode(
        SimulationSnapshot rootSnapshot,
        IReadOnlyList<PlanAction> prefix,
        SearchExpansionContext expansionContext,
        IReadOnlyList<PlanCardChoice>? turnSetupChoices,
        ContinuationStamp? turnSetupPlayState,
        bool resetSchedulingBaseline = false,
        bool createUncomparedOutcomes = false)
    {
        SearchNode seed = OpeningProbe.CreateOpeningSearchSeed(
            rootSnapshot, turnSetupChoices, turnSetupPlayState);
        return SearchProbeHelpers.ApplyFixedPrefix(
            seed, prefix, expansionContext,
            (actions, parent, turn, count) => Replay(actions, parent, turn, count),
            resetSchedulingBaseline,
            createUncomparedOutcome: createUncomparedOutcomes
                ? SearchRouteMaterializer.CreateUncomparedTurnOutcome
                : null);
    }

    internal int ReplayFixedPrefixPlanPayoff(
        SimulationSnapshot rootSnapshot,
        IReadOnlyList<PlanAction> prefix,
        PlanCommitment plan,
        SearchExpansionContext expansionContext)
    {
        SearchNode seed = OpeningProbe.CreateOpeningSearchSeed(rootSnapshot);
        SearchNode? node = ApplyFixedPrefix(seed, prefix, expansionContext,
            (actions, parent, turn, count) => Replay(actions, parent, turn, count));
        return node == null ? 0 : plan.CountRealizedPayoffs(node);
    }

    private static SearchNode? ApplyFixedPrefix(
        SearchNode seed,
        IReadOnlyList<PlanAction> prefix,
        SearchExpansionContext expansionContext,
        Func<IReadOnlyList<PlanAction>, SimulationSnapshot?, int, int, SimulationSnapshot> replay,
        bool resetSchedulingBaseline = false)
        => SearchProbeHelpers.ApplyFixedPrefix(
            seed, prefix, expansionContext, replay, resetSchedulingBaseline);

    private IReadOnlyList<(IReadOnlyList<PlanCardChoice> Choices, SimulationSnapshot Snapshot)>
        BuildTurnSetupRoots()
    {
        List<(IReadOnlyList<PlanCardChoice>, SimulationSnapshot)> roots = [];
        SimulationSnapshot? initialSnapshot = null;
        bool completed = false;
        try
        {
            initialSnapshot = ReplayTurnSetup([]);
            PendingChoiceBudgetSeed? initialSeed =
                SearchChoicePlanning.BuildCurrentPendingChoiceBudgetSeed(
                    Profile, DisplayNames, Player, initialSnapshot);
            if (initialSnapshot.BoundaryReason == SearchBoundaryReason.PendingChoice
                && initialSeed?.TurnSetupLayer == null)
            {
                throw new InvalidOperationException(
                    "回合准备阶段产生了未登记的选择类型。");
            }
            WholeActionChoiceBudget wholeActionBudget =
                SearchChoiceBudgetPolicy.CreateWholeActionChoiceBudget(
                    Profile,
                    primaryChoiceSpec: null,
                    minimumSemanticFinalQuota: 1,
                    currentPendingChoice: initialSeed);
            ChoiceOccurrenceCollector<IReadOnlyList<PlanCardChoice>> occurrenceCollector = new(
                wholeActionBudget.OccurrenceFinalReserve,
                wholeActionBudget.OccurrenceReplayAttemptQuota);
            SimulationSnapshot ownedInitialSnapshot = initialSnapshot;
            initialSnapshot = null;
            ResolveTurnSetupChoices(
                [],
                ownedInitialSnapshot,
                roots,
                wholeActionBudget.SemanticSearchBudget,
                occurrenceCollector,
                initialSeed?.TurnSetupLayer);

            IReadOnlyList<IReadOnlyList<PlanCardChoice>> occurrencePrefixes =
                occurrenceCollector.Seal();
            ChoiceSearchBudget occurrenceBudget = new(
                semanticFinalQuota: 0,
                materializedOccurrenceFinalQuota: occurrenceCollector.FinalReserve,
                replayAttemptQuota: occurrenceCollector.ReplayAttemptQuota);
            for (int index = 0; index < occurrencePrefixes.Count; index++)
            {
                ChoiceSearchBudget? branchBudget =
                    SearchChoiceBudgetPolicy.CreateChoiceBranchBudgetCore(
                        occurrenceBudget,
                        occurrencePrefixes.Count - index - 1);
                if (branchBudget == null)
                {
                    RecordChoiceBranchesDroppedByBudget(
                        occurrencePrefixes.Count - index);
                    break;
                }
                if (!TrySpendChoiceReplayAttempt(branchBudget))
                    break;

                IReadOnlyList<PlanCardChoice> prefix = occurrencePrefixes[index];
                SimulationSnapshot resolved;
                try
                {
                    resolved = ReplayTurnSetup(prefix);
                }
                catch (InvalidPlannedChoiceBranchException ex)
                {
                    if (DetailedDiagnostics)
                    {
                        Diagnostics.Debug(
                            $"[CombatSolver/Test] INITIAL_OCCURRENCE_CHOICE_REPLAY_PRUNED " +
                            $"reason={ex.Message}");
                    }
                    continue;
                }
                ResolveTurnSetupChoices(
                    prefix,
                    resolved,
                    roots,
                    branchBudget,
                    occurrenceCollector);
            }
            completed = true;
            return roots;
        }
        finally
        {
            initialSnapshot?.ReleaseSimulator();
            if (!completed)
            {
                foreach ((_, SimulationSnapshot snapshot) in roots)
                    snapshot.ReleaseSimulator();
            }
        }
    }

    private void ResolveTurnSetupChoices(IReadOnlyList<PlanCardChoice> choices,
        SimulationSnapshot snapshot,
        List<(IReadOnlyList<PlanCardChoice>, SimulationSnapshot)> roots,
        ChoiceSearchBudget searchBudget,
        ChoiceOccurrenceCollector<IReadOnlyList<PlanCardChoice>> occurrenceCollector,
        TurnSetupChoiceLayer? preparedLayer = null)
    {
        if (!GrowthCostPolicy.AllowsBrightestFlame(Policy.BrightestFlameMaxHpLossLimit,
                Root.InitialBrightestFlameMaxHpSpent, snapshot.BrightestFlameMaxHpSpent))
        {
            snapshot.ReleaseSimulator();
            return;
        }
        if (snapshot.BoundaryReason == SearchBoundaryReason.None)
        {
            if (searchBudget.TryConsumeFinal())
            {
                roots.Add((choices, snapshot));
            }
            else
            {
                RecordChoiceBranchesDroppedByBudget(1);
                snapshot.ReleaseSimulator();
            }
            return;
        }
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
        {
            snapshot.ReleaseSimulator();
            return;
        }
        if (!searchBudget.HasWork)
        {
            Accounting.ChoiceReplayBudgetExhaustions++;
            snapshot.ReleaseSimulator();
            return;
        }

        using ExecutionChoiceReplayCheckpoint? checkpoint =
            TakeExecutionChoiceCheckpoint(null, null, snapshot, choices);
        TurnStartChoiceRequest request;
        IReadOnlyList<IReadOnlyList<PlanCardChoice>> prefixes;
        int semanticBranchCount;
        try
        {
            TurnSetupChoiceLayer layer = preparedLayer
                ?? SearchChoicePlanning.BuildCurrentPendingChoiceBudgetSeed(
                    Profile, DisplayNames, Player, snapshot)?.TurnSetupLayer
                ?? throw new InvalidOperationException(
                    "回合准备阶段产生了未登记的选择类型。");
            request = layer.Request;
            CardChoiceSpec spec = layer.Spec;
            IReadOnlyList<PlanCardChoice> branches = layer.Branches;
            bool identityChangingLayer =
                CardChoiceSupport.IsIdentityChangingPersistentChoiceEffect(spec.Effect);
            branches = CardChoiceSupport.TakeChoicesWithIdentityOccurrenceReserve(
                branches,
                spec.Effect,
                Math.Max(1, searchBudget.ActiveFinalQuota),
                identityChangingLayer
                    ? occurrenceCollector.RemainingFinalReserve
                    : 0);
            semanticBranchCount = identityChangingLayer
                ? CardChoiceSupport.CountSemanticChoices(branches)
                : branches.Count;

            List<IReadOnlyList<PlanCardChoice>> resolvedPrefixes =
                new(branches.Count);
            foreach (PlanCardChoice branch in branches)
            {
                List<PlanCardChoice> next = new(choices.Count + 1);
                next.AddRange(choices);
                next.Add(branch with
                {
                    SourceId = request.SourceId,
                    ContextId = request.ContextId,
                    Timing = request.Timing,
                });
                resolvedPrefixes.Add(next);
            }
            prefixes = resolvedPrefixes;

            if (identityChangingLayer)
            {
                for (int index = semanticBranchCount; index < prefixes.Count; index++)
                    occurrenceCollector.Collect(prefixes[index]);
            }
        }
        catch
        {
            snapshot.ReleaseSimulator();
            throw;
        }
        snapshot.ReleaseSimulator();

        for (int index = 0; index < semanticBranchCount; index++)
        {
            ChoiceSearchBudget? branchBudget =
                SearchChoiceBudgetPolicy.CreateChoiceBranchBudgetCore(
                    searchBudget,
                    semanticBranchCount - index - 1);
            if (branchBudget == null)
            {
                RecordChoiceBranchesDroppedByBudget(semanticBranchCount - index);
                break;
            }
            if (!TrySpendChoiceReplayAttempt(branchBudget))
                break;

            IReadOnlyList<PlanCardChoice> prefix = prefixes[index];
            SimulationSnapshot resolved;
            try
            {
                resolved = checkpoint?.MatchesPrefix(prefix) == true
                    ? ResumeExecutionChoice(checkpoint, null, null, prefix) : ReplayTurnSetup(prefix);
            }
            catch (InvalidPlannedChoiceBranchException ex)
            {
                if (DetailedDiagnostics)
                {
                    Diagnostics.Debug(
                        $"[CombatSolver/Test] INITIAL_CHOICE_REPLAY_PRUNED " +
                        $"source={request.SourceId} reason={ex.Message}");
                }
                continue;
            }
            ResolveTurnSetupChoices(
                prefix,
                resolved,
                roots,
                branchBudget,
                occurrenceCollector);
        }
    }

    internal SimulationSnapshot ReplayTurnSetup(IReadOnlyList<PlanCardChoice> choices,
        bool allowExecutionCapture = true)
    {
        YieldIfNeeded();
        Accounting.ReplayCount++;
        CombatPredictionSimulator simulator = Root.ForkSimulator();
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        ForkableSet<uint> processedEnemyDeaths = [];
        foreach (Creature enemy in Root.Enemies)
        {
            if (enemy.CombatId is uint combatId && simulator.State.GetCreature(enemy).IsDead)
                processedEnemyDeaths.Add(combatId);
        }

        bool capturingExecution = allowExecutionCapture && !Testing.DisableExecutionChoiceContinuations
            && simulator.BeginExecutionContinuationCapture();
        TurnStartChoiceCursor cursor = new(choices);
        simulatedCombat.BeginActionChoices(cursor);
        simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
        SearchBoundaryReason boundary;
        try
        {
            using var executionDispatch = simulator.BeginExecutionDispatch();
            boundary = PreparePlayerPlayPhase(
                RoundContext, simulator,
                simulatedCombat,
                cursor,
                processedEnemyDeaths);
        }
        finally
        {
            try
            {
                simulatedCombat.EndActionChoices();
                if (capturingExecution && simulator.HasCapturedExecutionContinuation)
                    simulator.AppendExecutionContinuation(new ExecutionReplayTailFrame(processedEnemyDeaths,
                        StartTurnNumber, 0, simulator.ShuffleEventCount, simulator.ShuffleEventCount,
                        simulatedCombat.LastActionChoicesConsumed, simulator.CapturedExecutionFrame<PlayerStartFrame>()?.Progress,
                        RootSetup: true));
            }
            finally { if (capturingExecution) simulator.EndExecutionContinuationCapture(); }
        }
        if (boundary == SearchBoundaryReason.None
            && !SettleReplayActionBoundary(simulator, simulatedCombat))
        {
            boundary = SearchBoundaryReason.PendingChoice;
        }
        return Snapshot(
            simulator,
            StartTurnNumber,
            actionCount: 0,
            shufflesCrossed: simulator.ShuffleEventCount,
            boundary,
            processedEnemyDeaths);
    }

    internal SimulationSnapshot Replay(IReadOnlyList<PlanAction> actions,
        SimulationSnapshot? parentSnapshot = null,
        int startingTurn = 0,
        int priorActionCount = 0,
        ActionRelicTriggerRecorder? triggerRecorder = null,
        ReplayForkSeed? replayForkSeed = null,
        SearchReplayEvidence? replayEvidence = null,
        RoundReplayCheckpoint? roundCheckpoint = null,
        RoundReplayCheckpointCapture? roundCheckpointCapture = null,
        CardChoiceReplayCapture? cardChoiceCapture = null,
        ManualCardChoiceFrame? cardChoiceFrame = null,
        PotionChoiceFrame? potionChoiceFrame = null,
        bool countTransition = true,
        bool allowExecutionCapture = true,
        ContinuationCapture? continuationCapture = null)
    {
        YieldIfNeeded();
        CombatPredictionSimulator simulator;
        SimulatedCombatState simulatedCombat;
        int turn;
        int shufflesCrossed;
        SearchBoundaryReason boundary = SearchBoundaryReason.None;
        ForkableSet<uint> processedEnemyDeaths;
        if (parentSnapshot is null)
        {
            if (replayForkSeed != null)
                throw new InvalidOperationException("根回放不能消费父节点 Fork seed。");
            Accounting.ReplayCount++;
            simulator = Root.ForkSimulator();
            simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
            turn = StartTurnNumber;
            shufflesCrossed = 0;
            processedEnemyDeaths = [];
            foreach (Creature enemy in Root.Enemies)
            {
                if (enemy.CombatId is uint combatId && simulator.State.GetCreature(enemy).IsDead)
                    processedEnemyDeaths.Add(combatId);
            }
        }
        else
        {
            if (parentSnapshot.BoundaryReason != SearchBoundaryReason.None)
                throw new InvalidOperationException("不能从已抵达搜索边界的模拟状态继续分叉。");
            if (countTransition) Accounting.TransitionCount += actions.Count;
            if (replayForkSeed == null)
            {
                Accounting.ForkCount++;
                SearchMeasurement forkMeasurement = Accounting.Performance.Begin();
                try
                {
                    simulator = ((CombatPredictionSimulator)parentSnapshot.Simulator).Fork();
                }
                finally
                {
                    Accounting.Performance.End(SearchMetricPhase.Fork, forkMeasurement);
                }
                processedEnemyDeaths =
                    ((ForkableSet<uint>)parentSnapshot.ProcessedEnemyDeaths).Fork();
            }
            else
            {
                (simulator, processedEnemyDeaths) = replayForkSeed.Take();
            }
            simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
            turn = startingTurn;
            shufflesCrossed = roundCheckpoint?.ShufflesCrossed ?? parentSnapshot.ShufflesCrossed;
        }
        if (triggerRecorder != null)
            simulator.ActionRelicTriggers = triggerRecorder;

        bool capturingExecution = allowExecutionCapture && parentSnapshot != null && actions.Count == 1
            && cardChoiceFrame is null && potionChoiceFrame is null && triggerRecorder is null
            && ShouldCaptureExecution(simulator, actions[0]) && simulator.BeginExecutionContinuationCapture();
        int actionShuffleEventsBefore = simulator.ShuffleEventCount;
        try
        {
        SearchMeasurement actionMeasurement = Accounting.Performance.Begin();
        try
        {
        for (int actionOffset = 0; actionOffset < actions.Count; actionOffset++)
        {
            if (!simulator.IsInProgress)
                throw new InvalidOperationException("回放包含已锁定战斗终局之后的动作。");
            PlanAction action = actions[actionOffset];
            triggerRecorder?.BeginAction(priorActionCount + actionOffset);
            ThrowIfCancellationRequested();
            if (action.Kind == PlanActionKind.EndTurn)
            {
                SearchMeasurement roundMeasurement = Accounting.Performance.Begin();
                try
                {
                    using var executionDispatch = simulator.BeginExecutionDispatch();
                    boundary = roundCheckpoint != null
                        ? ResumeRoundPlayerStart(simulator, simulatedCombat, turn - StartTurnNumber,
                            processedEnemyDeaths, ref shufflesCrossed, action.TurnStartChoices, roundCheckpoint)
                        : AdvanceRound(simulator, simulatedCombat, turn - StartTurnNumber,
                            processedEnemyDeaths, ref shufflesCrossed, action.TurnStartChoices,
                            roundCheckpointCapture);
                }
                finally
                {
                    Accounting.Performance.End(SearchMetricPhase.RoundAdvance, roundMeasurement);
                }
                _ = simulatedCombat.ConsumePlayerTurnEndRequest();
                if (boundary == SearchBoundaryReason.None
                    && !SettleReplayActionBoundary(simulator, simulatedCombat))
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                }
                turn = simulatedCombat.GetPlayerTurnNumber(Player);
                LogAnnotatedReplayState(simulator, action, priorActionCount + actionOffset, turn, replayEvidence);
                continuationCapture?.Observe(this, simulator, priorActionCount + actionOffset, turn, boundary);
                continue;
            }

            if (action.Kind == PlanActionKind.UsePotion)
            {
                PotionModel potion = potionChoiceFrame?.Potion ?? simulatedCombat.GetPotionAtSlot(Player, action.PotionSlot)
                    ?? throw new InvalidOperationException($"回放时药水槽位 {action.PotionSlot} 为空。");
                if (!string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"回放时药水槽位 {action.PotionSlot} 为 {potion.Id.Entry}，预期 {action.PotionId}。");
                }
                if (potionChoiceFrame == null && !simulatedCombat.IsPotionAvailable(Player, action.PotionSlot))
                    throw new InvalidOperationException($"回放时药水 {action.PotionId} 已被消耗。");
                Creature? potionTarget = simulatedCombat.GetCreature(action.TargetCombatId);
                int potionShuffleEvents = potionChoiceFrame?.ShuffleEventsBefore ?? simulator.ShuffleEventCount;
                int potionHistoryEntryStart = potionChoiceFrame?.HistoryStart ?? simulator.History.Entries.Count;
                SearchMeasurement potionMeasurement = Accounting.Performance.Begin();
                simulatedCombat.BeginActionChoices(action.NestedChoices);
                try
                {
                    if (potionChoiceFrame == null && !PotionExecutionSupport.Prepare(
                            simulator, simulatedCombat, potion, action.PotionSlot, potionTarget))
                    {
                        boundary = SearchBoundaryReason.PendingChoice;
                        break;
                    }
                    if (!PotionExecutionSupport.Complete(simulator, simulatedCombat, potion, potionTarget,
                            action.Choice, potionHistoryEntryStart, processedEnemyDeaths))
                    {
                        boundary = SearchBoundaryReason.PendingChoice;
                        break;
                    }
                    if (!SettleReplayActionBoundary(simulator, simulatedCombat))
                    {
                        boundary = SearchBoundaryReason.PendingChoice;
                        break;
                    }
                }
                finally
                {
                    Accounting.Performance.End(SearchMetricPhase.PotionExecution, potionMeasurement);
                    simulatedCombat.EndActionChoices();
                }
                if (simulator.ShuffleEventCount != potionShuffleEvents)
                {
                    shufflesCrossed++;
                }
                simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(Player));
                boundary = ResolveRequestedPlayerTurnEnd(
                    RoundContext, simulator, simulatedCombat, action, processedEnemyDeaths, ref turn, ref shufflesCrossed);
                LogAnnotatedReplayState(simulator, action, priorActionCount + actionOffset, turn, replayEvidence);
                continuationCapture?.Observe(this, simulator, priorActionCount + actionOffset, turn, boundary);
                continue;
            }

            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(Player);
            PredictedCard? card = cardChoiceFrame?.Card ?? FindCardForReplay(playerState.Hand.Cards, action);
            if (card is null)
            {
                string hand = string.Join(',', playerState.Hand.Cards.Select(candidate =>
                    $"{candidate.Preview.Id.Entry}#{candidate.Preview.CurrentUpgradeLevel}:{CardChoiceSupport.ChoiceCardKey(candidate)}"));
                throw new InvalidOperationException(
                    $"回放时找不到手牌 {action.CardId}#{action.CardOccurrence}：turn={turn} " +
                    $"action_index={priorActionCount + actionOffset} " +
                    $"state_occurrence={action.CardStateOccurrence} state_key={action.CardStateKey} hand={hand}。");
            }
            Creature? target = simulatedCombat.GetCreature(action.TargetCombatId);
            if (cardChoiceFrame is null && !simulatedCombat.CanPlayCard(simulator, card))
            {
                int energyCost = card.GetEnergyCostWithModifiers(simulator, playerState);
                int starCost = card.GetStarCostWithModifiers(simulator, playerState);
                string hand = string.Join(',', playerState.Hand.Cards.Select(candidate =>
                    $"{candidate.Preview.Id.Entry}#{candidate.Preview.CurrentUpgradeLevel}"));
                string choiceCards = action.Choice == null
                    ? "-"
                    : string.Join(',', action.Choice.Cards.Select(token =>
                        $"{token.CardId}#{token.UpgradeLevel}@{token.SourceOccurrence}"));
                throw new InvalidOperationException(
                    $"回放时 {action.CardId} 已不可打出：turn={turn} " +
                    $"action_index={priorActionCount + actionOffset} occurrence={action.CardOccurrence} " +
                    $"energy={playerState.Energy} cost={energyCost} stars={playerState.Stars} star_cost={starCost} " +
                    $"choice={action.Choice?.Effect.ToString() ?? "-"}:{choiceCards} " +
                    $"hand={hand}。");
            }
            int shuffleEvents = cardChoiceFrame?.ShuffleEventsBefore ?? simulator.ShuffleEventCount;
            bool capturingChoice = cardChoiceCapture != null && simulator.BeginManualCardChoiceCapture(card);
            SearchMeasurement cardExecutionMeasurement = Accounting.Performance.Begin();
            simulatedCombat.BeginActionChoices(ActionChoicesForReplay(action));
            using IDisposable cardExecutionScope =
                simulatedCombat.BeginCardExecutionScope(processedEnemyDeaths);
            bool cardPlayCompleted;
            try
            {
                cardPlayCompleted = cardChoiceFrame is null
                    ? simulator.ManualPlay(card, target, out _)
                    : simulator.ResumeManualCardChoice(cardChoiceFrame);
            }
            finally
            {
                Accounting.Performance.End(SearchMetricPhase.CardExecution, cardExecutionMeasurement);
                if (capturingChoice) simulator.EndManualCardChoiceCapture();
            }
            SearchMeasurement cardPostMeasurement = Accounting.Performance.Begin();
            try
            {
                if (!cardPlayCompleted)
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                    break;
                }
                bool deathsCompleted;
                using (simulator.BeginExecutionDispatch())
                    deathsCompleted = CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator, simulatedCombat, simulatedCombat.KnownEnemies, processedEnemyDeaths);
                if (!deathsCompleted)
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                    break;
                }
                if (!SettleReplayActionBoundary(simulator, simulatedCombat))
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                    break;
                }
            }
            finally
            {
                Accounting.Performance.End(SearchMetricPhase.CardPostProcessing, cardPostMeasurement);
                simulatedCombat.EndActionChoices();
            }
            if (simulator.ShuffleEventCount != shuffleEvents)
            {
                shufflesCrossed++;
            }
            // The native action executor checks after the complete card effect, before
            // servicing a requested turn end. A lethal forced-end card never starts a round.
            simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(Player));
            boundary = ResolveRequestedPlayerTurnEnd(
                RoundContext, simulator, simulatedCombat, action, processedEnemyDeaths, ref turn, ref shufflesCrossed);
            LogAnnotatedReplayState(simulator, action, priorActionCount + actionOffset, turn, replayEvidence);
            if (continuationCapture != null)
            {
                // Continuation stamps describe stable post-action state, outside the card scope.
                cardExecutionScope.Dispose();
                continuationCapture.Observe(this, simulator, priorActionCount + actionOffset, turn, boundary);
            }
        }
        }
        finally { Accounting.Performance.End(SearchMetricPhase.Action, actionMeasurement); }
        if (capturingExecution && simulator.HasCapturedExecutionContinuation)
            simulator.AppendExecutionContinuation(new ExecutionReplayTailFrame(processedEnemyDeaths, turn,
                priorActionCount + actions.Count, shufflesCrossed, actionShuffleEventsBefore,
                simulatedCombat.LastActionChoicesConsumed,
                simulator.CapturedExecutionFrame<PlayerStartFrame>()?.Progress));
        }
        finally { if (capturingExecution) simulator.EndExecutionContinuationCapture(); }
        if ((cardChoiceFrame != null || potionChoiceFrame != null) && boundary == SearchBoundaryReason.PendingChoice)
        {
            // This is the same logical choice attempt. The extra physical fork is observable,
            // but cannot spend a second transition or branch-budget lease. All child scopes have
            // unwound before replaying from the retained parent with the complete original action.
            if (cardChoiceFrame != null) Accounting.CardChoicePrefixFallbacks++;
            else Accounting.PotionChoicePrefixFallbacks++;
            using ReplayForkSeed? fallbackSeed = ParallelActionReplayForkGate is null ? null
                : PrepareReplayForkSeed(parentSnapshot!, ParallelActionReplayForkGate);
            return Replay(actions, parentSnapshot, startingTurn, priorActionCount,
                triggerRecorder, replayForkSeed: fallbackSeed, replayEvidence: replayEvidence, countTransition: false);
        }

        SearchMeasurement snapshotMeasurement = Accounting.Performance.Begin();
        SimulationSnapshot snapshot = Snapshot(
            simulator,
            turn,
            priorActionCount + actions.Count,
            shufflesCrossed,
            boundary,
            processedEnemyDeaths);
        Accounting.Performance.End(SearchMetricPhase.Snapshot, snapshotMeasurement);
        try { cardChoiceCapture?.Receive(this, simulator, processedEnemyDeaths); }
        catch { snapshot.ReleaseSimulator(); throw; }
        return snapshot;
    }

    private void LogAnnotatedReplayState(
        CombatPredictionSimulator simulator,
        PlanAction action,
        int actionIndex,
        int turn,
        SearchReplayEvidence? replayEvidence = null)
    {
        if (replayEvidence != null && replayEvidence.Observe(simulator, Player, action, actionIndex))
            replayEvidence.FirstActualState = ContinuationStamp.CapturePredicted(
                Player, simulator, turn, Forecast, StartTurnNumber).StateText;
        if (!DetailedDiagnostics || simulator.ActionRelicTriggers == null)
            return;

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(Player);
        string actionToken = action.Kind switch
        {
            PlanActionKind.PlayCard => action.CardId,
            PlanActionKind.UsePotion => action.PotionId,
            _ => action.Kind.ToString(),
        };
        Diagnostics.Info(
            $"[CombatSolver/Debug] PLAN_REPLAY_STATE turn={turn} action_index={actionIndex} " +
            $"action={actionToken} " +
            $"energy={playerState.Energy} hand={string.Join(',', playerState.Hand.Cards.Select(card => card.Preview.Id.Entry))} " +
            $"draw={string.Join(',', playerState.DrawPile.Cards.Select(card => card.Preview.Id.Entry))} " +
            $"discard={string.Join(',', playerState.DiscardPile.Cards.Select(card => card.Preview.Id.Entry))} " +
            $"exhaust={string.Join(',', playerState.ExhaustPile.Cards.Select(card => card.Preview.Id.Entry))} " +
            $"enemies={string.Join(',', Root.Enemies.Select(enemy =>
                $"{enemy.Monster?.Id.Entry ?? "null"}:{simulator.State.GetCreature(enemy).CurrentHp}/{simulator.State.GetCreature(enemy).Block}"))}");
    }

    private ReplayForkSeed PrepareReplayForkSeed(
        SimulationSnapshot parentSnapshot,
        object? forkGate = null)
    {
        if (parentSnapshot.BoundaryReason != SearchBoundaryReason.None)
            throw new InvalidOperationException("不能从已抵达搜索边界的模拟状态准备 Fork seed。");
        ThrowIfCancellationRequested();
        if (forkGate != null)
        {
            lock (forkGate)
            {
                ThrowIfCancellationRequested();
                return PrepareReplayForkSeedCore(parentSnapshot);
            }
        }
        return PrepareReplayForkSeedCore(parentSnapshot);
    }

    private ReplayForkSeed PrepareReplayForkSeedCore(SimulationSnapshot parentSnapshot)
    {
        Accounting.ForkCount++;
        SearchMeasurement forkMeasurement = Accounting.Performance.Begin();
        try
        {
            CombatPredictionSimulator simulator =
                ((CombatPredictionSimulator)parentSnapshot.Simulator).Fork();
            ForkableSet<uint> processedEnemyDeaths =
                ((ForkableSet<uint>)parentSnapshot.ProcessedEnemyDeaths).Fork();
            return new ReplayForkSeed(simulator, processedEnemyDeaths);
        }
        finally
        {
            Accounting.Performance.End(SearchMetricPhase.Fork, forkMeasurement);
        }
    }

    internal SimulationSnapshot ResumeExecutionChoiceForCaller(
        ExecutionChoiceReplayCheckpoint checkpoint, SearchNode? parent, PlanAction? action,
        IReadOnlyList<PlanCardChoice>? setupChoices = null)
        => ResumeExecutionChoice(checkpoint, parent, action, setupChoices);

    internal ReplayForkSeed PrepareReplayForkSeedForCaller(
        SimulationSnapshot parentSnapshot, object? forkGate)
        => PrepareReplayForkSeed(parentSnapshot, forkGate);

    internal SimulationSnapshot ReplayAction(
        SearchNode parent,
        PlanAction action,
        ReplayForkSeed? replayForkSeed = null,
        RoundReplayCheckpointCapture? roundCheckpointCapture = null,
        CardChoiceReplayCapture? cardChoiceCapture = null)
    {
        if (replayForkSeed != null && VerifyIncrementalSearch)
            throw new InvalidOperationException("严格增量回放不能消费并行 Fork seed。");
        ExecutionChoiceReplayCheckpoint? executionCheckpoint =
            ExecutionChoiceCheckpoint?.Matches(parent, action) == true
                ? ExecutionChoiceCheckpoint : null;
        ReplayForkSeed? gatedSeed = null;
        ManualCardChoiceFrame? cardChoiceFrame = null;
        PotionChoiceFrame? potionChoiceFrame = null;
        PotionChoiceReplayCheckpoint? potionCheckpoint =
            PotionChoiceCheckpoint?.Matches(parent, action) == true
                ? PotionChoiceCheckpoint : null;
        CardChoiceReplayCheckpoint? cardCheckpoint =
            CardChoiceCheckpoint?.Matches(parent, action) == true
                ? CardChoiceCheckpoint : null;
        RoundReplayCheckpoint? roundCheckpoint = !VerifyIncrementalSearch
            && RoundCheckpoint?.Matches(parent, action) == true ? RoundCheckpoint : null;
        try
        {
            if (executionCheckpoint != null)
            {
                if (replayForkSeed != null || cardChoiceCapture != null)
                    throw new InvalidOperationException("Execution continuation cannot consume another replay seed or capture.");
                roundCheckpoint = null;
            }
            else if (cardCheckpoint != null)
            {
                if (replayForkSeed != null || roundCheckpoint != null || cardChoiceCapture != null)
                    throw new InvalidOperationException("Card continuation cannot consume another replay seed or capture.");
                gatedSeed = cardCheckpoint.Fork(this, out cardChoiceFrame);
                replayForkSeed = gatedSeed;
            }
            else if (potionCheckpoint != null)
            {
                if (replayForkSeed != null || roundCheckpoint != null || cardChoiceCapture != null)
                    throw new InvalidOperationException("Potion continuation cannot consume another replay seed or capture.");
                gatedSeed = potionCheckpoint.Fork(this, out potionChoiceFrame);
                replayForkSeed = gatedSeed;
            }
            else if (roundCheckpoint != null)
            {
                if (replayForkSeed != null || ParallelActionReplayForkGate == null)
                    throw new InvalidOperationException("Round checkpoint requires its owning replay gate.");
                gatedSeed = roundCheckpoint.Fork(this, ParallelActionReplayForkGate, CancellationToken);
                replayForkSeed = gatedSeed;
            }
            else if (replayForkSeed == null && ParallelActionReplayForkGate != null)
            {
                gatedSeed = PrepareReplayForkSeed(
                    parent.Snapshot,
                    ParallelActionReplayForkGate);
                replayForkSeed = gatedSeed;
            }
            return SearchTransitionGuard.Execute(
                action,
                parent.StateKey,
                parent.ActionCount,
                () =>
            {
                SimulationSnapshot incremental;
                try
                {
                    incremental = executionCheckpoint != null
                    ? ResumeExecutionChoice(executionCheckpoint, parent, action)
                    : Replay(
                    [action],
                    parent.Snapshot,
                    parent.Turn,
                    parent.ActionCount,
                    replayForkSeed: replayForkSeed,
                    roundCheckpoint: roundCheckpoint,
                    roundCheckpointCapture: roundCheckpointCapture,
                    cardChoiceCapture: cardChoiceCapture,
                    cardChoiceFrame: cardChoiceFrame,
                    potionChoiceFrame: potionChoiceFrame);
                }
                catch (InvalidPlannedChoiceBranchException error) when (Testing.VerifyChoiceContinuationSteps
                    && (executionCheckpoint != null || cardCheckpoint != null || potionCheckpoint != null))
                {
                    VerifyRejectedChoiceContinuationStep?.Invoke(parent, action, error);
                    throw;
                }
                if (Testing.VerifyChoiceContinuationSteps
                    && (executionCheckpoint != null || cardCheckpoint != null || potionCheckpoint != null))
                    VerifyChoiceContinuationStep?.Invoke(parent, action, incremental);
                if (!VerifyIncrementalSearch)
                    return incremental;

                List<PlanAction> fullActions = new(parent.ActionCount + 1);
                fullActions.AddRange(parent.Actions);
                fullActions.Add(action);
                SimulationSnapshot? fullReplayRoot = IncludeTurnSetup
                    ? ReplayTurnSetup(parent.GetTurnSetupChoices(), allowExecutionCapture: false)
                    : null;
                SimulationSnapshot replayed;
                try
                {
                    replayed = Replay(
                        fullActions,
                        fullReplayRoot,
                        StartTurnNumber,
                        priorActionCount: 0, allowExecutionCapture: false);
                }
                finally
                {
                    fullReplayRoot?.ReleaseSimulator();
                }
                try
                {
                    AssertIncrementalEquivalent(action, fullActions, incremental, replayed);
                }
                finally
                {
                    replayed.ReleaseSimulator();
                }
                return incremental;
            });
        }
        catch (SearchTransitionException error)
        {
            SearchReplayEvidence.PublishCandidateFailure(Diagnostics, parent,
                "action_replay:" + error.Message, action);
            throw;
        }
        finally
        {
            gatedSeed?.Dispose();
        }
    }

    internal void AssertIncrementalEquivalentForCaller(
        PlanAction action,
        IReadOnlyList<PlanAction> fullActions,
        SimulationSnapshot incremental,
        SimulationSnapshot replayed)
        => AssertIncrementalEquivalent(action, fullActions, incremental, replayed);

    internal void ResolveTurnSetupChoicesForCaller(IReadOnlyList<PlanCardChoice> choices,
        SimulationSnapshot snapshot,
        List<(IReadOnlyList<PlanCardChoice>, SimulationSnapshot)> roots,
        ChoiceSearchBudget searchBudget,
        ChoiceOccurrenceCollector<IReadOnlyList<PlanCardChoice>> occurrenceCollector,
        TurnSetupChoiceLayer? preparedLayer)
        => ResolveTurnSetupChoices(choices, snapshot, roots, searchBudget, occurrenceCollector, preparedLayer);

    private void AssertIncrementalEquivalent(
        PlanAction action,
        IReadOnlyList<PlanAction> fullActions,
        SimulationSnapshot incremental,
        SimulationSnapshot replayed)
    {
        ContinuationStamp incrementalStamp = ContinuationStamp.CapturePredicted(
            Player,
            incremental.Simulator,
            incremental.Turn,
            Forecast,
            StartTurnNumber);
        ContinuationStamp replayedStamp = ContinuationStamp.CapturePredicted(
            Player,
            replayed.Simulator,
            replayed.Turn,
            Forecast,
            StartTurnNumber);
        bool equal = incremental.StateKey == replayed.StateKey
            && incremental.Turn == replayed.Turn
            && incremental.Score.Equals(replayed.Score)
            && incremental.DefensiveBlockValue == replayed.DefensiveBlockValue
            && incremental.BoundaryReason == replayed.BoundaryReason
            && incremental.HasRisk == replayed.HasRisk
            && incremental.PlayerDead == replayed.PlayerDead
            && incremental.AllEnemiesDead == replayed.AllEnemiesDead
            && incremental.TerminalStamp == replayed.TerminalStamp
            && incrementalStamp == replayedStamp
            && incremental.ProcessedEnemyDeaths.SetEquals(replayed.ProcessedEnemyDeaths)
            && incremental.PredictionGaps.SequenceEqual(replayed.PredictionGaps);
        if (equal)
            return;

        string stateDifferences = string.Join(" || ",
            incrementalStamp.DescribeDifferences(replayedStamp).Take(12));
        throw new InvalidOperationException(
            $"增量分叉与完整回放不一致：action={PolicyActionToken(action)} " +
            $"prefix={string.Join('|', fullActions.Select(PolicyActionToken))} " +
            $"state_diffs={stateDifferences} " +
            $"incremental_terminal={incremental.TerminalStamp} replayed_terminal={replayed.TerminalStamp} " +
            $"incremental_boundary={incremental.BoundaryReason} replayed_boundary={replayed.BoundaryReason} " +
            $"incremental_key={incremental.StateKey} replayed_key={replayed.StateKey}");
    }

    private SearchBoundaryReason AdvanceRound(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        ref int shufflesCrossed,
        IReadOnlyList<PlanCardChoice>? turnStartChoices,
        RoundReplayCheckpointCapture? roundCheckpointCapture = null)
        => SearchRoundExecution.AdvanceRound(
            RoundContext, simulator, simulatedCombat, roundIndex, processedEnemyDeaths,
            ref shufflesCrossed, turnStartChoices, roundCheckpointCapture);

    private SearchBoundaryReason ResumeRoundPlayerStart(
        CombatPredictionSimulator simulator, SimulatedCombatState combat, int roundIndex,
        ISet<uint> deaths, ref int shufflesCrossed,
        IReadOnlyList<PlanCardChoice>? choices, RoundReplayCheckpoint checkpoint)
    {
        TurnStartChoiceCursor cursor = new(choices);
        combat.BeginActionChoices(cursor);
        combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
        try
        {
            using var measure = Accounting.Performance.Measure(SearchMetricPhase.RoundPlayerStart);
            var progress = new PlayerStartProgress(Player, StartTurnNumber + roundIndex + 1,
                rootSetup: false, checkpoint.TakingExtraTurn, deaths, shufflesCrossed, simulator.ShuffleEventCount)
            {
                SideStarted = checkpoint.SideTurnStartTriggeredEarly,
                DrawCount = checkpoint.HandDrawCount ?? 0,
                WillShuffle = checkpoint.HandDrawCount.HasValue,
            };
            SearchBoundaryReason result = SearchRoundExecution.ContinuePlayerStart(
                RoundContext, simulator, combat, progress,
                checkpoint.HandDrawCount.HasValue ? PlayerStartStage.Draw : PlayerStartStage.AfterPlayer);
            shufflesCrossed = progress.ShufflesCrossed;
            return result;
        }
        finally
        {
            combat.EndActionChoices();
        }
    }

    internal ExecutionChoiceReplayCheckpoint? TakeExecutionChoiceCheckpointForTesting(
        SearchNode? parent, PlanAction? action, SimulationSnapshot snapshot,
        IReadOnlyList<PlanCardChoice>? setupChoices = null)
        => TakeExecutionChoiceCheckpoint(parent, action, snapshot, setupChoices);

    private ExecutionChoiceReplayCheckpoint? TakeExecutionChoiceCheckpoint(SearchNode? parent,
        PlanAction? action, SimulationSnapshot snapshot, IReadOnlyList<PlanCardChoice>? setupChoices = null)
    {
        var simulator = snapshot.Simulator;
        var tail = simulator.CapturedExecutionFrame<ExecutionReplayTailFrame>();
        if (tail is null) return null;
        IReadOnlyList<PlanCardChoice> prefix = tail.RootSetup ? setupChoices ?? []
            : tail.PlayerStart != null ? action!.TurnStartChoices ?? [] : ActionChoicesForReplay(action!) ?? [];
        // A continuation owns only a fully consumed explicit prefix. Plans whose next
        // preselected choice precedes this request keep the complete replay path.
        if (tail.ConsumedChoices != prefix.Count) return null;
        PredictionExecutionContinuation? plan = simulator.TakeExecutionContinuation();
        if (plan is null) return null;
        Accounting.ExecutionChoiceCaptures++;
        return new(parent, action, prefix.ToArray(), simulator, plan, tail);
    }

    private SimulationSnapshot ResumeExecutionChoice(ExecutionChoiceReplayCheckpoint checkpoint,
        SearchNode? parent, PlanAction? action, IReadOnlyList<PlanCardChoice>? setupChoices = null)
    {
        YieldIfNeeded();
        var (simulator, plan, tail) = checkpoint.Fork(this);
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        IReadOnlyList<PlanCardChoice> remaining = tail.RootSetup
            ? setupChoices!.Skip(checkpoint.PrefixCount).ToArray() : checkpoint.RemainingChoices(action!);
        int turn = tail.Turn, shuffles = tail.ShufflesCrossed;
        combat.BeginActionChoices(remaining);
        SearchBoundaryReason boundary;
        try
        {
            using var measure = Accounting.Performance.Measure(SearchMetricPhase.ExecutionChoiceResume);
            boundary = simulator.ResumeExecutionContinuation(plan) ? SearchBoundaryReason.None : SearchBoundaryReason.PendingChoice;
            if (boundary == SearchBoundaryReason.None)
            {
                using var dispatch = simulator.BeginExecutionDispatch();
                if (tail.PlayerStart is null && !tail.RootSetup
                    && !CorePowerSupport.ApplyEnemyDeathPowers(simulator, combat, combat.KnownEnemies, tail.Deaths))
                    boundary = SearchBoundaryReason.PendingChoice;
                if (boundary == SearchBoundaryReason.None && !SettleReplayActionBoundary(simulator, combat))
                    boundary = SearchBoundaryReason.PendingChoice;
            }
        }
        finally { combat.EndActionChoices(); }
        if (tail.PlayerStart != null)
        {
            shuffles = tail.PlayerStart.ShufflesCrossed;
            turn = combat.GetPlayerTurnNumber(Player);
        }
        if (boundary == SearchBoundaryReason.None && tail.PlayerStart is null && !tail.RootSetup)
        {
            if (simulator.ShuffleEventCount != tail.ShuffleEventsBefore) shuffles++;
            simulator.CheckWinCondition(combat.GetPlayerTurnNumber(Player));
            boundary = ResolveRequestedPlayerTurnEnd(RoundContext, simulator, combat, action!, tail.Deaths, ref turn, ref shuffles);
        }
        if (tail.PlayerStart != null && !tail.RootSetup) _ = combat.ConsumePlayerTurnEndRequest();
        // The cursor on a resumed child contains only the newly appended choices. Store
        // their cumulative count before another branch takes ownership of this seed.
        tail.ConsumedChoices = checkpoint.PrefixCount + combat.LastActionChoicesConsumed;
        return Snapshot(simulator, turn, tail.ActionCount, tail.RootSetup ? simulator.ShuffleEventCount : shuffles, boundary, tail.Deaths);
    }

    private bool ShouldCaptureExecution(CombatPredictionSimulator simulator, PlanAction action)
    {
        if (Testing.DisableExecutionChoiceContinuations) return false;
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        if (action.Kind == PlanActionKind.EndTurn)
        {
            foreach (var power in combat.EffectivePowers())
                if (power.Amount > 0 && ReferenceEquals(power.Owner, Player.Creature)
                    && power is ForegoneConclusionPower or EntropyPower or ToolsOfTheTradePower or TyrannyPower or StratagemPower or MayhemPower)
                    return true;
            foreach (var relic in combat.RelicsOf(Player))
                if (!relic.IsMelted && relic.Id.Entry is "TOOLBOX" or "CHOICES_PARADOX" or "GAMBLING_CHIP" or "TOASTY_MITTENS" or "HISTORY_COURSE")
                    return true;
            return false;
        }
        if (action.Kind != PlanActionKind.PlayCard) return false;
        if (action.NestedChoices is { Count: > 0 } || action.CardId is "HAVOC" or "CASCADE" or "DECISIONS_DECISIONS"
            || action.ReplayCount != 0 || combat.GetAmount<StratagemPower>(Player.Creature) > 0) return true;
        PredictedCard? card = FindCardForReplay(simulator.State.GetPlayerCombatState(Player).Hand.Cards, action);
        return card?.Preview.BaseReplayCount > 0;
    }

    private bool TrySpendChoiceReplayAttempt(ChoiceSearchBudget budget)
    {
        ThrowIfCancellationRequested();
        YieldIfNeeded();
        if (budget.TrySpendReplayAttempt())
        {
            Accounting.ChoiceReplayAttempts++;
            Accounting.ChoiceBranchesEvaluated++;
            return true;
        }
        Accounting.ChoiceReplayBudgetExhaustions++;
        return false;
    }

    private void RecordChoiceBranchesDroppedByBudget(int count)
    {
        if (count <= 0)
            return;
        Accounting.ChoiceBranchesDroppedByBudget += count;
        Accounting.ChoiceReplayBudgetExhaustions++;
    }

    /// <summary>
    /// Replays one action from <paramref name="parent"/>, answering each card choice the existing
    /// engine asks for with the next of <paramref name="selections"/> (bit sets over the request's
    /// spec options, in option order). Each answer is placed on the action exactly as the existing
    /// search places a resolved branch (<see cref="AppendPendingChoice"/>), with the first
    /// source-less request of a card play as its primary choice.
    /// </summary>
    internal SimulationSnapshot ReplayWithChoicesForFlash(
        ref PlanAction action,
        SimulationSnapshot parent,
        IReadOnlyList<ulong> selections,
        Action<int, TurnStartChoiceRequest, CardChoiceSpec>? inspect = null,
        Action<int, KnowledgeDemonChoiceRequest>? inspectKnowledge = null)
    {
        PrimaryChoiceMatch? primary = null;
        int taken = 0;
        while (true)
        {
            // Potion card-generation choices are not pending turn-start choices: the existing engine
            // stores the generated options in History during Prepare and resolves the action's
            // PlanAction.Choice in PotionExecutionSupport.Complete (PotionChoiceSupport.GetSpec/Apply).
            // Probe once to read the same options, then attach the selection as the action's choice.
            if (action.Kind == PlanActionKind.UsePotion && action.Choice == null && taken < selections.Count
                && TryBuildPotionChoice(action, parent, selections[taken], out CardChoiceSpec potionSpec,
                    out PlanAction potionAction))
            {
                inspect?.Invoke(taken, new TurnStartChoiceRequest(action.PotionId ?? potionSpec.Effect.ToString(),
                        potionSpec.Effect, potionSpec.SourcePile,
                        Math.Min(potionSpec.MaxCount, potionSpec.Options.Count), potionSpec,
                        Timing: PlanChoiceTiming.Action), potionSpec);
                action = potionAction;
                taken++;
                continue;
            }
            SimulationSnapshot child = ReplayForFlash([action], parent);
            if (child.BoundaryReason != SearchBoundaryReason.PendingChoice)
            {
                if (taken != selections.Count)
                    throw new FlashChoiceMismatchException($"Flash 做了 {selections.Count} 次选择，旧引擎只请求了 {taken} 次。");
                // The existing expansion freezes "this play ended the turn" on the card action
                // (SearchExpansionShared.CreatePlannedCardChild). Flash actions carry no such flag, so
                // the translator records it from the replayed turn. Without it a continuation stamp
                // on a forced-turn-end card (for example VOID_FORM) is never captured.
                if (action.Kind == PlanActionKind.PlayCard && child.Turn > parent.Turn)
                    action = action with { EndsPlayerTurn = true };
                return child;
            }
            SimulatedCombatState combat = (SimulatedCombatState)child.Simulator.State.CombatState;
            // Knowledge Demon's curse choice is a fixed two-card option list, not a CardChoiceSpec:
            // KnowledgeDemonChoiceSupport.Resolve consumes a PlanCardChoice with SourcePile None and
            // one card token, and the existing search appends it to TurnStartChoices.
            if (combat.PendingKnowledgeDemonChoice is { } knowledge)
            {
                IReadOnlyList<PlanCardChoice> knowledgeBranches =
                    KnowledgeDemonChoiceSupport.BuildChoices(knowledge, DisplayNames);
                if (taken >= selections.Count)
                {
                    throw new FlashChoiceMismatchException(
                        $"旧引擎请求第 {taken + 1} 次选择（知识恶魔 {knowledge.SourceId}），Flash 没有做。");
                }
                inspectKnowledge?.Invoke(taken, knowledge);
                ulong knowledgeBits = selections[taken++];
                if (knowledgeBits == 0 || (knowledgeBits & (knowledgeBits - 1)) != 0
                    || knowledgeBits >> knowledgeBranches.Count != 0)
                {
                    throw new FlashChoiceMismatchException(
                        $"知识恶魔选择 {knowledgeBits:X} 不是单个合法选项（{knowledgeBranches.Count} 项）。");
                }
                List<PlanCardChoice> nextKnowledge = new((action.TurnStartChoices ?? []).Count + 1);
                nextKnowledge.AddRange(action.TurnStartChoices ?? []);
                nextKnowledge.Add(knowledgeBranches[System.Numerics.BitOperations.TrailingZeroCount(knowledgeBits)]);
                action = action with { TurnStartChoices = nextKnowledge };
                child.ReleaseSimulator();
                continue;
            }
            TurnStartChoiceRequest request = combat.PendingTurnStartChoice
                ?? throw new FlashChoiceMismatchException("旧引擎的待定选择不是卡牌选择。");
            CardChoiceSpec spec = TurnStartChoiceSupport.BuildPendingSpec(
                (CombatPredictionSimulator)child.Simulator, combat, Player);
            if (taken == 0)
            {
                primary = action.Kind == PlanActionKind.PlayCard && string.IsNullOrEmpty(request.SourceId)
                    ? SearchProbeHelpers.BuildPrimaryChoiceMatch(spec)
                    : null;
            }
            if (taken >= selections.Count)
            {
                throw new FlashChoiceMismatchException(
                    $"旧引擎请求第 {taken + 1} 次选择（{request.SourceId}/{request.Effect}/{request.SourcePile}），Flash 没有做。");
            }
            inspect?.Invoke(taken, request, spec);
            ulong bits = selections[taken++];
            if (spec.Options.Count < 64 && bits >> spec.Options.Count != 0)
                throw new FlashChoiceMismatchException($"选择 {bits:X} 超出旧引擎的 {spec.Options.Count} 个选项。");
            List<int> indexes = [];
            for (int index = 0; index < spec.Options.Count; index++)
            {
                if ((bits & (1UL << index)) != 0)
                    indexes.Add(index);
            }
            PlanCardChoice branch = CardChoiceSupport.BuildSelectedChoiceForFlash(spec, indexes, DisplayNames);
            bool turnResolution = action.Kind == PlanActionKind.EndTurn
                || child.Turn > parent.Turn
                || request.Timing is PlanChoiceTiming.PlayerTurnEnd
                    or PlanChoiceTiming.EnemyTurn or PlanChoiceTiming.PlayerTurnStart;
            action = AppendPendingChoice(action, request, branch, turnResolution, primary);
            child.ReleaseSimulator();
        }
    }

    /// <summary>
    /// Builds the <see cref="PlanAction.Choice"/> of a potion whose existing-engine effect asks a card
    /// selection (generation potions, DropletOfPrecognition, GamblersBrew, …) from a probe replay.
    /// </summary>
    private bool TryBuildPotionChoice(PlanAction action, SimulationSnapshot parent, ulong bits,
        out CardChoiceSpec spec, out PlanAction resolved)
    {
        spec = null!;
        resolved = action;
        int slot = action.PotionSlot;
        if (slot < 0)
            return false;
        SimulatedCombatState combat = (SimulatedCombatState)parent.Simulator.State.CombatState;
        PotionModel? potion = combat.GetPotionAtSlot(Player, slot);
        if (potion == null || !PotionChoiceSupport.RequiresChoice(potion))
            return false;
        SimulationSnapshot probe = ReplayForFlash([action], parent);
        try
        {
            if (probe.BoundaryReason != SearchBoundaryReason.None)
                throw new FlashChoiceMismatchException(
                    $"药水 {action.PotionId} 的选择探测停在了 {probe.BoundaryReason}。");
            spec = PotionChoiceSupport.GetSpec((CombatPredictionSimulator)probe.Simulator, potion);
        }
        finally
        {
            probe.ReleaseSimulator();
        }
        if (spec.Options.Count < 64 && bits >> spec.Options.Count != 0)
            throw new FlashChoiceMismatchException($"选择 {bits:X} 超出旧引擎的 {spec.Options.Count} 个选项。");
        List<int> indexes = [];
        for (int index = 0; index < spec.Options.Count; index++)
        {
            if ((bits & (1UL << index)) != 0)
                indexes.Add(index);
        }
        resolved = action with
        {
            Choice = CardChoiceSupport.BuildSelectedChoiceForFlash(spec, indexes, DisplayNames),
        };
        return true;
    }
}
