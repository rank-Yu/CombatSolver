using System.Diagnostics;
using CombatSolver.Flash.Kernel;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Flash.Search;
using CombatSolver.Flash.Search.Scoring;

namespace CombatSolver.Flash.Bridge;

/// <summary>A Flash step and the existing engine's step disagreed; the whole Flash solve is discarded.</summary>
internal sealed class FlashVerificationException(int step, IReadOnlyList<string> differences)
    : Exception($"FLASH_VERIFY_MISMATCH step={step}: {string.Join(" | ", differences.Take(6))}")
{
    public int Step { get; } = step;

    /// <summary>The bounded message pieces (action, plan, then the first differences).</summary>
    public IReadOnlyList<string> Differences { get; } = differences;

    /// <summary>The mismatch action, as it appears in the stable message.</summary>
    public string Action { get; init; } = "";

    /// <summary>The full route as it appears in the stable message (not truncated).</summary>
    public string Plan { get; init; } = "";

    /// <summary>Every field-level difference from <see cref="FlashBattleDump.Diff"/>, without the cap.</summary>
    public IReadOnlyList<string> StateDifferences { get; init; } = [];

    /// <summary>Full state before the step; equal on both sides after the previous step verified.</summary>
    public IReadOnlyList<string> StateBefore { get; init; } = [];

    public IReadOnlyList<string> OldAfter { get; init; } = [];

    public IReadOnlyList<string> FlashAfter { get; init; } = [];

    /// <summary>Request context filled by <see cref="FlashRouteMaterializer.Solve"/> for evidence.</summary>
    public string? DirectPurpose { get; set; }

    public string? ContinuationPurpose { get; set; }

    public IReadOnlyList<string> Prefix { get; set; } = [];

    public FlashVerificationFailure ToFailure()
        => new(Step, Action, Plan, StateDifferences, StateBefore, OldAfter, FlashAfter,
            DirectPurpose, ContinuationPurpose, Prefix);
}

internal sealed record FlashSolveOutcome(
    SolverResult? Result,
    FlashRejection Rejection,
    string Detail,
    FlashBeamResult? Search,
    IReadOnlyList<PlanAction> Plan,
    TimeSpan AdmissionTime,
    TimeSpan VerificationTime,
    TimeSpan MaterializationTime)
{
    /// <summary>
    /// Set when the audited Flash search found no route satisfying the request's potion policy, or the
    /// materialized route failed the post-materialization potion checks. The dispatch point maps the
    /// Flash no-route verdict to the existing search's <c>PotionPolicyUnsatisfiedException</c> shape;
    /// any other constraint is an explicit two-sided mismatch and an On-mode failure.
    /// </summary>
    public string? ConstraintFailure { get; init; }

    /// <summary>Final candidates replayed by the optional offline scan (0 when disabled).</summary>
    public int CandidateRoutes { get; init; }

    /// <summary>Final candidates whose existing-engine replay disagreed with Flash.</summary>
    public int CandidateMismatches { get; init; }

    /// <summary>First difference of each mismatching final candidate.</summary>
    public IReadOnlyList<string> CandidateMismatchDetails { get; init; } = [];
}

/// <summary>Inputs the offline route-trace factory needs after admission and root import.</summary>
internal sealed record FlashTraceContext(
    OpeningProbe Probe,
    Player Player,
    SimulationSnapshot SearchRoot,
    FlashBattle Root,
    FlashRules Rules);

/// <summary>
/// Runs one Flash solve for a <see cref="the existing search"/> request: admission and root import,
/// <see cref="FlashBeamSolver"/>, step-by-step replay of the chosen route through the existing
/// engine (each step's projection must match), then materialization of the verified route as a
/// fixed prefix through the existing solver so the result has the existing shape.
/// </summary>
internal static partial class FlashRouteMaterializer
{
    public static FlashSolveOutcome Solve(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        CancellationToken cancellationToken,
        FlashBeamOptions? flashOptions = null,
        SolverPotionPolicy? potionPolicyOverride = null,
        Action<SolverProgress>? progressCallback = null,
        DirectSearchPurpose? purpose = null,
        PrimarySearchIncumbent? primaryIncumbent = null,
        IReadOnlyList<PlanAction>? fixedPrefixActions = null,
        int? maximumPotionUses = null,
        int? minimumPotionUses = null,
        int? earliestPotionTurn = null,
        ContinuationPurpose? continuationPurpose = null,
        PotionFreePolicyBaseline? potionFreePolicyBaseline = null,
        bool? resetFixedPrefixSchedulingBaseline = null,
        PlanCommitment? planCommitment = null,
        int earlyTurnScoutDepth = 0,
        Action<int, IReadOnlyList<EarlyTurnFrontierCandidate>>? earlyTurnScoutObserver = null,
        Func<FlashTraceContext, FlashBeamTrace?>? traceFactory = null)
    {
        FlashWorkAccounting accounting = new(policy.RequestWorkTotals, purpose, continuationPurpose,
            policy.FramePressureSignal, policy.Diagnostics);
        Stopwatch workWatch = Stopwatch.StartNew();
        try
        {
            return SolveCore(root, displayNames, battleDamage, policy, profile, cancellationToken,
                accounting, flashOptions, potionPolicyOverride, progressCallback, purpose,
                primaryIncumbent, fixedPrefixActions, maximumPotionUses, minimumPotionUses,
                earliestPotionTurn, continuationPurpose, potionFreePolicyBaseline,
                resetFixedPrefixSchedulingBaseline, planCommitment, earlyTurnScoutDepth,
                earlyTurnScoutObserver, traceFactory);
        }
        catch (FlashVerificationException ex)
        {
            // Request context for the offline evidence record; the stable message and bounds stay
            // unchanged. The exception is rethrown in place so the verification stack is preserved.
            ex.DirectPurpose = purpose?.ToString();
            ex.ContinuationPurpose = continuationPurpose?.ToString();
            ex.Prefix = [.. (fixedPrefixActions ?? []).Select(action =>
                $"{action.Turn}:{action.Kind}:{action.CardId}->{action.TargetCombatId}")];
            throw;
        }
        finally
        {
            accounting.Record(workWatch.Elapsed);
        }
    }

    private static FlashSolveOutcome SolveCore(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        CancellationToken cancellationToken,
        FlashWorkAccounting accounting,
        FlashBeamOptions? flashOptions = null,
        SolverPotionPolicy? potionPolicyOverride = null,
        Action<SolverProgress>? progressCallback = null,
        DirectSearchPurpose? purpose = null,
        PrimarySearchIncumbent? primaryIncumbent = null,
        IReadOnlyList<PlanAction>? fixedPrefixActions = null,
        int? maximumPotionUses = null,
        int? minimumPotionUses = null,
        int? earliestPotionTurn = null,
        ContinuationPurpose? continuationPurpose = null,
        PotionFreePolicyBaseline? potionFreePolicyBaseline = null,
        bool? resetFixedPrefixSchedulingBaseline = null,
        PlanCommitment? planCommitment = null,
        int earlyTurnScoutDepth = 0,
        Action<int, IReadOnlyList<EarlyTurnFrontierCandidate>>? earlyTurnScoutObserver = null,
        Func<FlashTraceContext, FlashBeamTrace?>? traceFactory = null)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        Player player = root.PlayerIdentity;
        IReadOnlyList<PlanAction> prefix = fixedPrefixActions ?? [];
        int prefixLength = prefix.Count;
        OpeningProbe probe = new(root, displayNames, battleDamage, policy, cancellationToken,
            searchProfile: profile, potionPolicyOverride: potionPolicyOverride,
            maximumPotionUses: maximumPotionUses,
            earliestPotionTurn: earliestPotionTurn,
            resetFixedPrefixSchedulingBaseline: resetFixedPrefixSchedulingBaseline ?? false);
        accounting.Attach(probe);
        bool turnSetup = policy.IncludeTurnSetup;
        if (turnSetup
            && (root.CurrentSide != MegaCrit.Sts2.Core.Combat.CombatSide.Player
                || root.PlayerPhase != MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Start))
        {
            return new FlashSolveOutcome(null, FlashRejection.RootPhase,
                $"IncludeTurnSetup 根不在玩家准备阶段：side={root.CurrentSide} phase={root.PlayerPhase}",
                null, [], watch.Elapsed, TimeSpan.Zero, TimeSpan.Zero);
        }
        // The request root: the pre-setup Start state for a turn-setup request, otherwise the play root.
        // It stays alive for the frozen scoring/bound tables even when the search starts after a setup
        // choice or a fixed prefix.
        SimulationSnapshot rootSnapshot = turnSetup
            ? probe.CaptureRootSnapshotForFlash()
            : probe.ReplayForFlash([], null);
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        FlashPruningRequest pruning = FlashBridgeFacts.CapturePruningRequest(root, policy, potionPolicyOverride, maximumPotionUses, minimumPotionUses, battleDamage);
        bool enforceForcedPotionDirectives = potionPolicyOverride == null;
        FlashForcedPotionDirectives forcedDirectives;
        try
        {
            forcedDirectives = enforceForcedPotionDirectives
                ? FlashPotionBridge.CaptureForcedDirectives(policy.PotionStrategy, prefix,
                    root.HasRenewablePotionShapedRock)
                : FlashForcedPotionDirectives.None;
        }
        catch (FlashImportException ex)
        {
            return new FlashSolveOutcome(null, ex.Reason, ex.Detail, null, [],
                watch.Elapsed, TimeSpan.Zero, TimeSpan.Zero);
        }
        // The existing final ordering's potion filter is the target for every request that carries a
        // potion bound, baseline, forced directive or an at-least-one policy. Requests without one keep
        // the prior route key so the adoption decisions they already passed do not move.
        bool noveltyRequest = policy.NoveltySearch != null;
        bool auditPotionPolicy = noveltyRequest
            || purpose == DirectSearchPurpose.SmartPotionGradient
            || continuationPurpose == ContinuationPurpose.SmartOpeningPotionPosterior
            || (continuationPurpose != null
                && (potionPolicyOverride ?? pruning.EffectivePotionPolicy) == SolverPotionPolicy.Smart)
            || minimumPotionUses is > 0
            || maximumPotionUses is > 0
            || potionFreePolicyBaseline != null
            || pruning.EffectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne
            || forcedDirectives.Count > 0;
        // The search roots: one ordinary root, or every enumerated turn-setup choice branch. A fixed
        // prefix is applied per root so an incompatible setup choice is dropped exactly like the
        // existing search's ApplyFixedPrefix and a compatible one keeps its choice vector.
        List<TurnSetupRootCandidate> searchRoots = [];
        if (!turnSetup)
        {
            SimulationSnapshot ordinaryRoot = rootSnapshot;
            if (prefixLength > 0)
            {
                // ApplyFixedPrefix releases the snapshots it walks, so replay the prefix on a second root
                // snapshot and keep rootSnapshot alive for the frozen tables.
                SimulationSnapshot prefixRoot = probe.ReplayForFlash([], null);
                SimulationSnapshot? prefixEnd;
                try
                {
                    prefixEnd = probe.ReplayFixedPrefixForFlash(prefixRoot, prefix);
                }
                catch (InvalidOperationException ex)
                {
                    return new FlashSolveOutcome(null, FlashRejection.UnsupportedRequest,
                        $"固定前缀无法回放：{ex.Message}", null, [], watch.Elapsed, TimeSpan.Zero, TimeSpan.Zero);
                }
                if (prefixEnd == null)
                {
                    return new FlashSolveOutcome(null, FlashRejection.UnsupportedRequest,
                        "固定前缀无法回放", null, [], watch.Elapsed, TimeSpan.Zero, TimeSpan.Zero);
                }
                ordinaryRoot = prefixEnd;
            }
            searchRoots.Add(new TurnSetupRootCandidate([], ordinaryRoot));
        }
        else
        {
            IReadOnlyList<TurnSetupRootCandidate> setupRoots;
            try
            {
                setupRoots = probe.CaptureTurnSetupRootsForFlash();
            }
            catch (InvalidPlannedChoiceBranchException ex)
            {
                return new FlashSolveOutcome(null, FlashRejection.UnsupportedRequest,
                    $"回合准备根无法枚举：{ex.Message}", null, [], watch.Elapsed, TimeSpan.Zero, TimeSpan.Zero);
            }
            foreach (TurnSetupRootCandidate candidate in setupRoots)
            {
                if (prefixLength == 0)
                {
                    searchRoots.Add(candidate);
                    continue;
                }
                SimulationSnapshot? prefixEnd;
                try
                {
                    prefixEnd = probe.ReplayFixedPrefixForFlash(candidate.Snapshot, prefix);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                if (prefixEnd == null)
                    continue;
                searchRoots.Add(candidate with { Snapshot = prefixEnd });
            }
            if (searchRoots.Count == 0)
            {
                return new FlashSolveOutcome(null, FlashRejection.UnsupportedRequest,
                    "固定前缀与全部回合准备根都不相容", null, [],
                    watch.Elapsed, TimeSpan.Zero, TimeSpan.Zero);
            }
        }
        SimulationSnapshot searchRootSnapshot = searchRoots[0].Snapshot;
        FlashPotionUsageForBridge potionUsage =
            OpeningProbe.CapturePotionUsageForFlash(searchRootSnapshot);
        int initialAutomaticPotionUses = potionUsage.PotionUseCount - potionUsage.ExplicitPotionUseCount;
        FlashPlanCommitment? flashPlanCommitment = null;
        int initialPlanPayoff = 0;
        FlashRules rules;
        FlashBattle[] battles = new FlashBattle[searchRoots.Count];
        FlashScorer scorer;
        FlashBoundFeatures bound;
        FlashPowerCommitmentCatalog? powerCommitments;
        try
        {
            if (planCommitment is { } pendingPlan)
            {
                flashPlanCommitment = ToFlashPlanCommitment(pendingPlan);
                SimulationSnapshot payoffRoot = probe.ReplayForFlash([], null);
                initialPlanPayoff = probe.ReplayFixedPrefixPlanPayoffForFlash(payoffRoot, prefix, pendingPlan);
            }
            // A turn-setup search imports every enumerated post-setup root. The rules are compiled from
            // the request root's content (so a card a setup choice moved out of the first root is still
            // representable in its siblings) while the root-derived values come from the first search
            // root, matching the existing search's request-root baseline for frozen powers.
            rules = turnSetup
                ? FlashRulesCompiler.Compile(searchRootSnapshot.Simulator, player, rootSnapshot.Simulator,
                    contentSimulator: rootSnapshot.Simulator, lostHpSimulator: rootSnapshot.Simulator)
                : FlashRulesCompiler.Compile(searchRootSnapshot.Simulator, player, rootSnapshot.Simulator);
            for (int index = 0; index < searchRoots.Count; index++)
            {
                battles[index] = FlashStateImporter.Import(searchRoots[index].Snapshot, player, rules,
                    potionUsage.PotionUseCount, requirePlayPhase: true);
            }
            scorer = FlashScoreBridge.Create(root, policy, profile, rootSnapshot, searchRootSnapshot,
                player, rules, battles[0], rootSetup: turnSetup);
            bound = FlashBoundBridge.Create(root, policy, rootSnapshot, player, rules, battles[0], pruning,
                potionUsage.ExplicitPotionUseCount, initialAutomaticPotionUses);
            powerCommitments = FlashPowerCommitmentBridge.Compile(root);
        }
        catch (FlashImportException ex)
        {
            return new FlashSolveOutcome(null, ex.Reason, ex.Detail, null, [], watch.Elapsed, TimeSpan.Zero, TimeSpan.Zero);
        }
        TimeSpan admission = watch.Elapsed;
        int? smartCeiling = auditPotionPolicy
            ? SmartPotionBoundPolicy.ComputeSmartPotionEligibilityHpCeiling(
                root, policy, purpose, continuationPurpose, potionFreePolicyBaseline,
                minimumPotionUses, maximumPotionUses)
            : null;
        if (smartCeiling != null && !bound.Enabled)
        {
            // The existing search keeps searching without its lower-bound prune when the exact
            // layer's bound inputs are unavailable (the existing solver's Retention.cs): it only skips
            // the incumbent / smart-potion ceiling, not the search. Mirror that here; the ceiling
            // is a pruning optimization, never a potion-policy filter.
            policy.Diagnostics.Info(
                $"[CombatSolver/Flash] SMART_POTION_BOUND_UNAVAILABLE reason={bound.DisabledReason} action=search_without_lower_bound");
            smartCeiling = null;
        }

        // Incumbent and potion turns stay in request-root units: the fixed prefix is already part of
        // the existing solver's request, so it must not shift the comparison base.
        int rootPlayerTurn = prefixLength == 0 ? battles[0].PlayerTurn : rootSnapshot.Turn;
        PrimarySearchIncumbent? incumbent = primaryIncumbent is { } seed
            ? seed with { CombatEndedTurn = seed.CombatEndedTurn - rootSnapshot.Turn + rootPlayerTurn }
            : null;
        FlashBeamOptions options = (flashOptions ?? new FlashBeamOptions(
            profile.BeamWidth,
            profile.MaxExpandedNodes,
            MaxTurns: 64,
            TimeSpan.FromMilliseconds(profile.SoftTimeBudgetMilliseconds),
            root.InitialPlayerMaxHp)) with
        {
            Potions = PotionPolicy(probe, rootSnapshot, rootPlayerTurn, policy.PotionStrategy,
                pruning.EffectivePotionPolicy, potionPolicyOverride == SolverPotionPolicy.Disabled),
            Scorer = scorer,
            Ordering = new FlashRouteOrdering(root.InitialPlayerHp, root.InitialPlayerMaxHp, root.BossHpRelief,
                root.PostCombatRelicHeal),
            HpTargetStop = HpTargetStop(policy, battleDamage, pruning.EffectivePotionPolicy,
                minimumPotionUses, enforceForcedPotionDirectives),
            Bound = bound,
            Incumbent = incumbent,
            EffectivePotionPolicy = pruning.EffectivePotionPolicy,
            TheftPolicy = policy.TheftPolicy,
            ForcedPotionDirectives = forcedDirectives,
            EnforceForcedPotionDirectives = enforceForcedPotionDirectives,
            MinimumPotionUses = pruning.MinimumPotionUses,
            InitialActionCount = prefixLength,
            InitialExplicitPotionUses = potionUsage.ExplicitPotionUseCount,
            InitialAutomaticPotionUses = initialAutomaticPotionUses,
            PotionFreeBaseline = potionFreePolicyBaseline,
            PotionReplacementHpCredit = root.PotionRewardOutlook.ReplacementHpCredit,
            SmartPotionEligibilityHpCeiling = smartCeiling,
            AmbergrisPotionId = FlashPotionBridge.AmbergrisPotionId(),
            InitialAmbergrisUses = CountAmbergrisUses(prefix),
            AuditPotionPolicy = auditPotionPolicy,
            Act3BossStrategy = policy.Act3BossStrategy,
            PlanCommitment = flashPlanCommitment,
            InitialPlanPayoffCount = initialPlanPayoff,
            EarlyTurnScoutDepth = earlyTurnScoutDepth,
            InitialPlayerTurn = rootSnapshot.Turn,
            InitialSearchTurn = battles[0].PlayerTurn,
            SecondRankBand = profile.SecondRankBand,
            SecondRankBandWidth = profile.BeamWidth,
            AggressivePowerCommitment = profile.AggressivePowerCommitment,
            PreserveReplayAllocatorOpening = root.HasUnusedCardReplayAllocator,
            PowerCommitments = powerCommitments,
            Novelty = policy.NoveltySearch is { } novelty ? FlashNoveltyOptions.From(novelty) : null,
        };
        options = options with
        {
            Trace = traceFactory?.Invoke(new FlashTraceContext(
                probe, player, searchRootSnapshot, battles[0], rules)),
        };
        FlashProgressBridge progressBridge = new(probe, player, root, displayNames, battleDamage,
            policy, profile, searchRoots, battles, rules, prefix, potionUsage.PotionUseCount,
            progressCallback, accounting.Pacer, cancellationToken);
        options = options with
        {
            Pacer = accounting.Pacer,
            Progress = progressBridge.Publish,
            PollTakeover = progressBridge.Poll,
        };
        FlashBeamSolver solver = new(rules, options);
        FlashBeamResult search;
        try
        {
            search = solver.Solve(battles, cancellationToken);
        }
        finally
        {
            // A canceled solve still reports the work it actually performed.
            accounting.Expanded = solver.ExpandedNodes;
            accounting.Transitions = solver.TransitionCount;
            accounting.ProgressStats = progressBridge.Stats;
        }
        if (search.Takeover == FlashTakeoverAction.AdoptRoute)
        {
            // The player asked for the exact seed that was displayed. Its materialization replays and
            // verifies the route on the existing engine and returns the existing RouteAdoption shape.
            SolverResult adopted = progressBridge.MaterializeRequestedSeed();
            return new FlashSolveOutcome(adopted, FlashRejection.None, "", search, [],
                admission, TimeSpan.Zero, watch.Elapsed);
        }
        if (search.Takeover == FlashTakeoverAction.ApplyCurrentTurn)
        {
            if (search.BestRootIndex < 0 || search.BestRootIndex >= searchRoots.Count)
                throw new InvalidOperationException(
                    $"Flash 当前回合采纳根下标 {search.BestRootIndex} 超出回合准备根数 {searchRoots.Count}。");
            TurnSetupRootCandidate adoptedRoot = searchRoots[search.BestRootIndex];
            watch.Restart();
            List<PlanAction> adoptedPlan = VerifyRoute(probe, player, adoptedRoot.Snapshot,
                battles[search.BestRootIndex], rules, prefix, search.BestRoute,
                potionUsage.PotionUseCount, ownsStartSnapshot: false,
                diagnostics: policy.Diagnostics);
            TimeSpan adoptedVerification = watch.Elapsed;
            try
            {
                SolverResult adopted = MaterializeFixedPlan(probe, root, displayNames, battleDamage,
                    policy, potionPolicyOverride, potionFreePolicyBaseline, maximumPotionUses,
                    minimumPotionUses, earliestPotionTurn,
                    turnSetup ? adoptedRoot.Choices : null,
                    turnSetup ? adoptedRoot.Snapshot : null, adoptedPlan, cancellationToken,
                    SolverResultScope.CurrentTurnAdoption, routeAdoptionActions: null, out _);
                policy.Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_TAKEOVER_MATERIALIZED kind=ApplyCurrentTurn " +
                    $"scope={adopted.ResultScope} actions={adopted.BestNode.ActionCount} " +
                    $"flash_expanded={search.ExpandedNodes}");
                return new FlashSolveOutcome(adopted, FlashRejection.None, "", search, adoptedPlan,
                    admission, adoptedVerification, watch.Elapsed);
            }
            catch (PotionPolicyUnsatisfiedException ex)
            {
                return new FlashSolveOutcome(null, FlashRejection.None, ex.Message, search, adoptedPlan,
                    admission, adoptedVerification, watch.Elapsed)
                {
                    ConstraintFailure = $"materialize-potion-policy {ex.Message}",
                };
            }
        }
        if (!search.PotionPolicySatisfied)
        {
            return new FlashSolveOutcome(null, FlashRejection.None,
                "flash-potion-policy-no-route", search, [], admission, TimeSpan.Zero, TimeSpan.Zero)
            {
                ConstraintFailure = "flash-potion-policy-no-route",
            };
        }
        if (search.BestRootIndex < 0 || search.BestRootIndex >= searchRoots.Count)
            throw new InvalidOperationException(
                $"Flash 路线根下标 {search.BestRootIndex} 超出回合准备根数 {searchRoots.Count}。");
        TurnSetupRootCandidate selectedRoot = searchRoots[search.BestRootIndex];

        watch.Restart();
        // The verified route is the fixed prefix plus the Flash route; the existing solver replays the
        // whole plan from the request root and packages it.
        List<PlanAction> plan = VerifyRoute(probe, player, selectedRoot.Snapshot,
            battles[search.BestRootIndex], rules, prefix, search.BestRoute,
            potionUsage.PotionUseCount, ownsStartSnapshot: false,
            diagnostics: policy.Diagnostics);
        TimeSpan verification = watch.Elapsed;

        int candidateRoutes = 0, candidateMismatches = 0;
        List<string> candidateMismatchDetails = [];
        if (search.TopRoutes.Count > 0)
        {
            for (int candidateIndex = 0; candidateIndex < search.TopRoutes.Count; candidateIndex++)
            {
                // The final ordering puts the selected route first; it was verified above.
                if (candidateIndex == 0)
                    continue;
                FlashAction[] candidateRoute = search.TopRoutes[candidateIndex];
                candidateRoutes++;
                try
                {
                    VerifyRoute(probe, player, selectedRoot.Snapshot,
                        battles[search.BestRootIndex], rules, prefix, candidateRoute,
                        potionUsage.PotionUseCount, ownsStartSnapshot: false,
                        diagnostics: policy.Diagnostics);
                }
                catch (FlashVerificationException ex)
                {
                    candidateMismatches++;
                    candidateMismatchDetails.Add(ex.Message);
                }
                catch (FlashChoiceMismatchException ex)
                {
                    candidateMismatches++;
                    candidateMismatchDetails.Add(ex.Message);
                }
            }
        }

        watch.Restart();
        SolverResult result;
        SearchNode materializedSelectionNode;
        try
        {
            result = MaterializeFixedPlan(probe, root, displayNames, battleDamage, policy,
                potionPolicyOverride, potionFreePolicyBaseline, maximumPotionUses, minimumPotionUses,
                earliestPotionTurn, turnSetup ? selectedRoot.Choices : null,
                turnSetup ? selectedRoot.Snapshot : null, plan, cancellationToken,
                SolverResultScope.SearchCompletion, routeAdoptionActions: null,
                out materializedSelectionNode);
        }
        catch (PotionPolicyUnsatisfiedException ex)
        {
            // The existing final ordering rejected the materialized route under the request's own
            // potion policy. That is a two-sided potion-policy mismatch, not a Flash verification
            // failure.
            return new FlashSolveOutcome(null, FlashRejection.None, ex.Message, search, plan,
                admission, verification, watch.Elapsed)
            {
                ConstraintFailure = $"materialize-potion-policy {ex.Message}",
            };
        }
        if (planCommitment is { } commitment)
        {
            int realized = commitment.Payoff.Kind == PlanPayoffEvidenceKind.RegisteredPowerBenefit
                ? search.BestPayoffCount
                : commitment.CountRealizedPayoffs(materializedSelectionNode);
            if (realized != search.BestPayoffCount)
            {
                throw new FlashVerificationException(plan.Count,
                    [$"plan payoff old={realized} flash={search.BestPayoffCount}"]);
            }
        }
        if (turnSetup && !FlashBridgeFacts.SameTurnSetupChoices(result.TurnSetupChoices, selectedRoot.Choices))
        {
            throw new FlashVerificationException(plan.Count,
                [$"turn-setup choices old={result.TurnSetupChoices.Count} flash={selectedRoot.Choices.Count}"]);
        }
        bool verifyPotion = auditPotionPolicy
            || minimumPotionUses != null
            || maximumPotionUses != null
            || potionPolicyOverride != null
            || earliestPotionTurn != null
            || enforceForcedPotionDirectives && policy.PotionStrategy.HasForcedDirectives;
        if (search.NoveltyTelemetry is { } noveltyTelemetry)
            result.NoveltySearch = noveltyTelemetry;
        if (verifyPotion
            && FlashPotionBridge.VerifyRoute(result, policy, enforceForcedPotionDirectives,
                root.HasRenewablePotionShapedRock, minimumPotionUses, maximumPotionUses,
                potionPolicyOverride, earliestPotionTurn) is { } failure)
        {
            return new FlashSolveOutcome(null, FlashRejection.None, failure, search, plan,
                admission, verification, watch.Elapsed)
            {
                ConstraintFailure = failure,
            };
        }
        // Publish the scout frontier only after the whole Flash attempt, including materialization,
        // succeeded. A later failure discards the whole Flash attempt, so no candidate is published.
        if (earlyTurnScoutObserver != null && search.ScoutLayers.Count > 0)
        {
            List<(int Turns, IReadOnlyList<EarlyTurnFrontierCandidate> Candidates)> layers = new(
                search.ScoutLayers.Count);
            foreach (FlashScoutLayer layer in search.ScoutLayers)
            {
                List<EarlyTurnFrontierCandidate> candidates = new(layer.Candidates.Count);
                foreach (FlashScoutCandidate candidate in layer.Candidates)
                {
                    IReadOnlyList<PlanCardChoice>? scoutSetupChoices = turnSetup
                        ? searchRoots[candidate.RootIndex].Choices : null;
                    PlanAction[] actions = TranslateScoutRoute(
                        probe, player, prefix, scoutSetupChoices, candidate.Actions, out StateFingerprint stateKey);
                    candidates.Add(new EarlyTurnFrontierCandidate(
                        layer.Turns, actions, stateKey, candidate.Score));
                }
                layers.Add((layer.Turns, candidates));
            }
            // Translate every layer before invoking the observer, so a mismatch cannot leave the
            // coordinator with a partially published frontier.
            foreach ((int turns, IReadOnlyList<EarlyTurnFrontierCandidate> candidates) in layers)
                earlyTurnScoutObserver(turns, candidates);
        }
        return new FlashSolveOutcome(result, FlashRejection.None, "", search, plan, admission, verification, watch.Elapsed)
        {
            CandidateRoutes = candidateRoutes,
            CandidateMismatches = candidateMismatches,
            CandidateMismatchDetails = candidateMismatchDetails,
        };
    }

    /// <summary>
    /// Builds the existing-shape result for an already verified fixed-prefix plan. The prefix is
    /// replayed on the shared replay core, the shared final ordering selects the single fixed route,
    /// and the shared route materializer builds the plan/snapshot/result exactly as the existing
    /// search does for a completed route.
    /// </summary>
    private static SolverResult MaterializeFixedPlan(
        OpeningProbe probe,
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        SolverPotionPolicy? potionPolicyOverride,
        PotionFreePolicyBaseline? potionFreePolicyBaseline,
        int? maximumPotionUses,
        int? minimumPotionUses,
        int? earliestPotionTurn,
        IReadOnlyList<PlanCardChoice>? turnSetupChoices,
        SimulationSnapshot? turnSetupSnapshot,
        IReadOnlyList<PlanAction> plan,
        CancellationToken cancellationToken,
        SolverResultScope scope,
        IReadOnlyList<PlanAction>? routeAdoptionActions,
        out SearchNode selectedNode)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        FlashPruningRequest pruning = FlashBridgeFacts.CapturePruningRequest(
            root, policy, potionPolicyOverride, maximumPotionUses, minimumPotionUses, battleDamage);
        ContinuationStamp? turnSetupPlayState = turnSetupSnapshot is null
            ? null
            : ContinuationStamp.CapturePredicted(
                root.PlayerIdentity,
                turnSetupSnapshot.Simulator,
                root.StartTurnNumber,
                root.Forecast,
                root.StartTurnNumber);
        SimulationSnapshot materializationRoot = turnSetupChoices is null
            ? probe.ReplayForFlash([], null)
            : probe.ReplayTurnSetupForFlash(turnSetupChoices);
        SearchNode? node;
        try
        {
            node = probe.BuildFixedPrefixNodeForFlash(
                materializationRoot, plan, turnSetupChoices, turnSetupPlayState);
        }
        catch
        {
            if (materializationRoot.HasSimulator)
                materializationRoot.ReleaseSimulator();
            throw;
        }
        if (node is null)
            throw new PotionPolicyUnsatisfiedException("固定前缀无法在物化根上回放。");
        // ApplyFixedPrefix released the root snapshot; the node owns the final snapshot.
        FinalPlanOrdering ordering = new(
            pruning.EffectivePotionPolicy,
            policy.PotionStrategy,
            potionPolicyOverride == null,
            root.PotionRewardOutlook.ReplacementHpCredit,
            policy.TheftPolicy,
            pruning.StrategicBossHpRelief,
            root.PostCombatRelicHeal,
            potionFreePolicyBaseline,
            root.InitialPlayerMaxHp,
            pruning.MinimumPotionUses,
            policy.Diagnostics,
            policy.DetailedDiagnostics,
            battleDamage);
        FinalPlanSelection selection = ordering.Select(
            [(node, node.Snapshot)], root.InitialPlayerHp, emitDiagnostics: true);
        selectedNode = selection.Candidate.Node;
        bool onlyDeathRoutes = node.Snapshot.PlayerDead || node.Snapshot.ProjectedPlayerHp <= 0;
        SearchMaterializationMetrics metrics = new(probe.AccountingForBridge.Performance)
        {
            Expanded = 0,
            TransitionCount = plan.Count,
            ReplayCount = 1,
            ForkCount = plan.Count,
        };
        long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        TimeSpan gcPause = GC.GetTotalPauseDuration();
        SearchRouteMaterializer materializer = new(
            root, displayNames, battleDamage, policy, probe.ReplayCoreForBridge,
            probe.ExpansionContextForBridge, ordering, metrics,
            pruning.ForceAllPotionsDisabled, policy.TheftPolicy, root.IsActEndingBoss,
            root.BossHpRelief, watch, allocatedAtStart, gen0, gen1, gen2, gcPause);
        try
        {
            return materializer.MaterializeSelectedRoute(
                selection, onlyDeathRoutes, scope,
                candidateSearchedTurnLayers: 0, candidateTimeBudgetReached: false,
                routeAdoptionActions: routeAdoptionActions);
        }
        catch
        {
            if (node.Snapshot.HasSimulator)
                node.Snapshot.ReleaseSimulator();
            throw;
        }
    }

    /// <summary>
    /// The Flash form of a <see cref="PlanCommitment"/>. <c>RegisteredPowerBenefit</c> reads the
    /// existing <c>PowerCommitment.RealizedEvidence</c>, which FlashBeam does not model; the request is
    /// rejected with the normal unsupported-request result instead of searching with a partial policy.
    /// </summary>
    private static FlashPlanCommitment ToFlashPlanCommitment(PlanCommitment plan)
    {
        ushort cardId = ushort.MaxValue;
        ushort potionId = ushort.MaxValue;
        FlashPlanPayoffKind kind = plan.Payoff.Kind switch
        {
            PlanPayoffEvidenceKind.CardPlayed => FlashPlanPayoffKind.CardPlayed,
            PlanPayoffEvidenceKind.FreePotionUsed => FlashPlanPayoffKind.FreePotionUsed,
            PlanPayoffEvidenceKind.RegisteredPowerBenefit => FlashPlanPayoffKind.RegisteredPowerBenefit,
            _ => throw new ArgumentOutOfRangeException(nameof(plan), "未知的计划收益证据类型。"),
        };
        if (kind == FlashPlanPayoffKind.FreePotionUsed)
        {
            if (!FlashRegistry.TryPotion(plan.Payoff.SourceId, out potionId))
            {
                throw new FlashImportException(FlashRejection.UnsupportedRequest,
                    $"计划收益药水 {plan.Payoff.SourceId} 没有 Flash 定义");
            }
        }
        else if (!FlashRegistry.TryCard(plan.Payoff.SourceId, out cardId))
        {
            throw new FlashImportException(FlashRejection.UnsupportedRequest,
                $"计划收益卡 {plan.Payoff.SourceId} 没有 Flash 定义");
        }
        return new FlashPlanCommitment(
            plan.Kind == PlanCommitmentKind.CopyPower,
            plan.OpenedTurn,
            plan.Payoff.EarliestTurn,
            kind,
            cardId,
            potionId);
    }

    /// <summary>
    /// Replays a Flash scout route through the existing engine from the request root and returns the
    /// translator's <see cref="PlanAction"/>s plus the end state's fingerprint. The route starts at the
    /// search root; a non-empty fixed prefix is replayed first.
    /// </summary>
    private static PlanAction[] TranslateScoutRoute(OpeningProbe probe, Player player,
        IReadOnlyList<PlanAction> prefix, IReadOnlyList<PlanCardChoice>? setupChoices,
        FlashAction[] route, out StateFingerprint stateKey)
    {
        SimulationSnapshot snapshot;
        if (setupChoices is null)
        {
            snapshot = probe.ReplayForFlash(prefix, null);
        }
        else
        {
            snapshot = probe.ReplayTurnSetupForFlash(setupChoices);
            if (prefix.Count > 0)
            {
                SimulationSnapshot? prefixEnd = probe.ReplayFixedPrefixForFlash(snapshot, prefix);
                snapshot = prefixEnd
                    ?? throw new FlashVerificationException(0, ["侦察路线的前缀无法在回合准备根上回放"]);
            }
        }
        List<PlanAction> actions = new(prefix.Count + route.Length);
        actions.AddRange(prefix);
        try
        {
            for (int step = 0; step < route.Length; step++)
            {
                PlanAction planned;
                SimulationSnapshot next;
                try
                {
                    next = FlashChoiceTranslator.Replay(probe.ReplayCoreForBridge, snapshot, player, route[step],
                        out planned);
                }
                catch (FlashChoiceMismatchException ex)
                {
                    throw new FlashVerificationException(step, [ex.Message]);
                }
                snapshot.ReleaseSimulator();
                snapshot = next;
                actions.Add(planned);
            }
            stateKey = snapshot.StateKey;
            return [.. actions];
        }
        finally
        {
            snapshot.ReleaseSimulator();
        }
    }

    private static TimeSpan MaxZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static int CountAmbergrisUses(IReadOnlyList<PlanAction> prefix)
    {
        int uses = 0;
        foreach (PlanAction action in prefix)
        {
            if (action.Kind == PlanActionKind.UsePotion
                && string.Equals(action.PotionId, FlashPotionBridge.AmbergrisPotionNativeId(),
                    StringComparison.Ordinal))
            {
                uses++;
            }
        }
        return uses;
    }

    /// <summary>
    /// The existing search stops at the HP target only for victories it counts as eligible
    /// (<c>IsEligibleCompleteVictory</c>): at least the request's minimum/at-least-one uses, at most its
    /// early-stop count, and every enforced forced directive satisfied.
    /// </summary>
    private static FlashHpTargetStop? HpTargetStop(SearchPolicySnapshot policy, BattleDamageSnapshot battleDamage,
        SolverPotionPolicy effectivePolicy, int? minimumPotionUses, bool enforceForcedPotionDirectives)
    {
        if (!policy.CanStopAtHpTarget)
            return null;
        int requiredPotionUses = Math.Max(minimumPotionUses ?? 0,
            effectivePolicy == SolverPotionPolicy.RequireAtLeastOne
                && battleDamage.PotionsUsedSoFar == 0 ? 1 : 0);
        return new FlashHpTargetStop(policy.AcceptableBattleHpLoss, battleDamage.HpLostSoFar,
            policy.MinimumRequiredPotionUses(battleDamage.PotionsUsedSoFar), requiredPotionUses,
            enforceForcedPotionDirectives && policy.PotionStrategy.HasForcedDirectives);
    }

    private static FlashPotionPolicy PotionPolicy(OpeningProbe probe, SimulationSnapshot rootSnapshot,
        int rootPlayerTurn, PotionStrategySnapshot strategy, SolverPotionPolicy effectivePolicy,
        bool forceAllDisabled)
    {
        (bool[] _, int[] _, int? earliestTurn, int? maximumUses) =
            probe.CapturePotionPolicyForFlash(rootSnapshot);
        // The earliest turn is in request-root units; a fixed prefix must not shift it.
        int earliest = earliestTurn is { } turn ? turn - rootSnapshot.Turn + rootPlayerTurn : int.MinValue;
        // The acceptance gate is the same (slot, potion identity) strategy check the existing search
        // runs on the potion currently in the slot, so a generated potion is offered without widening
        // a disabled identity.
        return new FlashPotionPolicy(earliest, maximumUses, strategy, effectivePolicy, forceAllDisabled);
    }
}
