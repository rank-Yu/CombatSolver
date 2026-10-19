using CombatSolver.Engine.InCombat.Flash;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.FlashBeam;

/// <summary>A Flash step and the existing engine's step disagreed; the whole Flash solve is discarded.</summary>
internal sealed class FlashVerificationException(int step, IReadOnlyList<string> differences)
    : Exception($"FLASH_VERIFY_MISMATCH step={step}: {string.Join(" | ", differences.Take(6))}")
{
    public int Step { get; } = step;

    public IReadOnlyList<string> Differences { get; } = differences;
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
    /// materialized route failed the post-materialization potion checks. The caller falls back to the
    /// existing solver without treating this as the existing search's potion-policy verdict.
    /// </summary>
    public string? ConstraintFailure { get; init; }
}

/// <summary>
/// Runs one Flash solve for a <see cref="CombatBeamSolver"/> request: admission and root import,
/// <see cref="FlashBeamSolver"/>, step-by-step replay of the chosen route through the existing
/// engine (each step's projection must match), then materialization of the verified route as a
/// fixed prefix through the existing solver so the result has the existing shape.
/// </summary>
internal static class FlashRouteMaterializer
{
    /// <summary>
    /// FlashBeam width and node budget as a multiple of the existing profile's, under the same soft time
    /// budget. FlashBeam retains one rank lane and one offensive lane instead of the existing routing,
    /// diversity and Pareto lanes, so it needs a wider beam for the same route quality
    /// (docs/flash-search/testing.md, width benchmark).
    /// </summary>
    internal const int ProfileWidthScale = 4;

    /// <summary>
    /// Requests at or below this soft budget keep the 25% offensive diversity lane. The 10 s
    /// natural-root benchmark and short player budgets use it; the 120 s Medium coordinator path
    /// ranks the whole beam by the existing rank order.
    /// </summary>
    internal const int ShortSearchOffensiveShareBudgetMs = 30_000;

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
        int profileScale = ProfileWidthScale,
        PlanCommitment? planCommitment = null,
        int earlyTurnScoutDepth = 0,
        Action<int, IReadOnlyList<EarlyTurnFrontierCandidate>>? earlyTurnScoutObserver = null)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        Player player = root.PlayerIdentity;
        IReadOnlyList<PlanAction> prefix = fixedPrefixActions ?? [];
        int prefixLength = prefix.Count;
        CombatBeamSolver replay = new(root, displayNames, battleDamage, policy, cancellationToken,
            searchProfile: profile, potionPolicyOverride: potionPolicyOverride,
            maximumPotionUses: maximumPotionUses,
            fixedPrefixActions: prefix,
            minimumPotionUses: minimumPotionUses,
            earliestPotionTurn: earliestPotionTurn);
        SimulationSnapshot rootSnapshot = replay.ReplayForFlash([], null);
        FlashPruningRequest pruning = replay.CapturePruningRequestForFlash();
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
        bool auditPotionPolicy = purpose == DirectSearchPurpose.SmartPotionGradient
            || continuationPurpose == ContinuationPurpose.SmartOpeningPotionPosterior
            || (continuationPurpose != null
                && (potionPolicyOverride ?? pruning.EffectivePotionPolicy) == SolverPotionPolicy.Smart)
            || minimumPotionUses is > 0
            || maximumPotionUses is > 0
            || potionFreePolicyBaseline != null
            || pruning.EffectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne
            || forcedDirectives.Count > 0;
        SimulationSnapshot searchRootSnapshot = rootSnapshot;
        if (prefixLength > 0)
        {
            // ApplyFixedPrefix releases the snapshots it walks, so replay the prefix on a second root
            // snapshot and keep rootSnapshot alive for the frozen tables.
            SimulationSnapshot prefixRoot = replay.ReplayForFlash([], null);
            SimulationSnapshot? prefixEnd;
            try
            {
                prefixEnd = replay.ReplayFixedPrefixForFlash(prefixRoot);
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
            searchRootSnapshot = prefixEnd;
        }
        FlashPotionUsageForBridge potionUsage =
            CombatBeamSolver.CapturePotionUsageForFlash(searchRootSnapshot);
        int initialAutomaticPotionUses = potionUsage.PotionUseCount - potionUsage.ExplicitPotionUseCount;
        FlashPlanCommitment? flashPlanCommitment = null;
        int initialPlanPayoff = 0;
        FlashRules rules;
        FlashBattle battle;
        FlashScorer scorer;
        FlashBoundFeatures bound;
        try
        {
            if (planCommitment is { } pendingPlan)
            {
                flashPlanCommitment = ToFlashPlanCommitment(pendingPlan);
                initialPlanPayoff = CountPlanPayoffs(prefix, pendingPlan);
            }
            rules = FlashRulesCompiler.Compile(searchRootSnapshot.Simulator, player);
            battle = FlashStateImporter.Import(searchRootSnapshot, player, rules,
                potionUsage.PotionUseCount, requirePlayPhase: true);
            scorer = FlashScoreBridge.Create(root, policy, profile, rootSnapshot, searchRootSnapshot,
                player, rules, battle);
            bound = FlashBoundBridge.Create(root, policy, rootSnapshot, player, rules, battle, pruning,
                potionUsage.ExplicitPotionUseCount, initialAutomaticPotionUses);
        }
        catch (FlashImportException ex)
        {
            return new FlashSolveOutcome(null, ex.Reason, ex.Detail, null, [], watch.Elapsed, TimeSpan.Zero, TimeSpan.Zero);
        }
        TimeSpan admission = watch.Elapsed;
        int? smartCeiling = auditPotionPolicy
            ? CombatBeamSolver.ComputeSmartPotionEligibilityHpCeiling(
                root, policy, purpose, continuationPurpose, potionFreePolicyBaseline,
                minimumPotionUses, maximumPotionUses)
            : null;
        if (smartCeiling != null && !bound.Enabled)
        {
            return new FlashSolveOutcome(null, FlashRejection.None,
                $"smart-potion-bound unavailable ({bound.DisabledReason})", null, [],
                admission, TimeSpan.Zero, TimeSpan.Zero)
            {
                ConstraintFailure = $"smart-potion-bound unavailable ({bound.DisabledReason})",
            };
        }

        // Incumbent and potion turns stay in request-root units: the fixed prefix is already part of
        // the existing solver's request, so it must not shift the comparison base.
        int rootPlayerTurn = prefixLength == 0 ? battle.PlayerTurn : rootSnapshot.Turn;
        PrimarySearchIncumbent? incumbent = primaryIncumbent is { } seed
            ? seed with { CombatEndedTurn = seed.CombatEndedTurn - rootSnapshot.Turn + rootPlayerTurn }
            : null;
        FlashBeamOptions options = (flashOptions ?? new FlashBeamOptions(
            checked(profile.BeamWidth * profileScale),
            checked(profile.MaxExpandedNodes * profileScale),
            MaxTurns: 64,
            TimeSpan.FromMilliseconds(profile.SoftTimeBudgetMilliseconds),
            root.InitialPlayerMaxHp)) with
        {
            Potions = PotionPolicy(replay, rootSnapshot, rootPlayerTurn),
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
            InitialExplicitPotionStrategicCost = potionUsage.ExplicitPotionStrategicCost,
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
        };
        // The offensive-reserved lane is a Flash-specific diversity seat (the existing RankBest has
        // no such lane). Short searches rely on it to keep aggressive lines; the production Medium
        // coordinator budget has enough rank seats without reserving a quarter of the beam.
        options = options with
        {
            OffensiveShare = policy.Profile.SoftTimeBudgetMilliseconds
                <= ShortSearchOffensiveShareBudgetMs
                    ? 0.25d
                    : 0d,
        };
        FlashBeamResult search = new FlashBeamSolver(rules, options).Solve(battle, cancellationToken);
        if (!search.PotionPolicySatisfied)
        {
            return new FlashSolveOutcome(null, FlashRejection.None,
                "flash-potion-policy-no-route", search, [], admission, TimeSpan.Zero, TimeSpan.Zero)
            {
                ConstraintFailure = "flash-potion-policy-no-route",
            };
        }

        watch.Restart();
        // The verified route is the fixed prefix plus the Flash route; the existing solver replays the
        // whole plan from the request root and packages it.
        List<PlanAction> plan = new(prefixLength + search.BestRoute.Length);
        plan.AddRange(prefix);
        SimulationSnapshot snapshot = searchRootSnapshot;
        FlashBattle state = battle;
        for (int step = 0; step < search.BestRoute.Length; step++)
        {
            FlashAction action = search.BestRoute[step];
            PlanAction planned;
            try
            {
                snapshot = FlashChoiceTranslator.Replay(replay, snapshot, player, action, out planned);
            }
            catch (FlashChoiceMismatchException ex)
            {
                throw new FlashVerificationException(step, [ex.Message]);
            }
            plan.Add(planned);
            if (FlashKernel.Step(ref state, action, rules, out _) != FlashStepResult.Completed)
                throw new InvalidOperationException("已选路线的 Flash 动作缺少选择。");
            FlashBattle projected;
            try
            {
                projected = FlashStateImporter.Import(snapshot, player, rules, potionUsage.PotionUseCount);
            }
            catch (FlashImportException ex)
            {
                // The existing engine reached a state Flash cannot represent, so this step cannot be verified.
                throw new FlashVerificationException(step, [$"旧引擎状态无法投影：{ex.Reason} {ex.Detail}"]);
            }
            if (!FlashFingerprint.Equal(projected, state))
                throw new FlashVerificationException(step, FlashBattleDump.Diff(projected, state, "old", "flash"));
        }
        TimeSpan verification = watch.Elapsed;

        watch.Restart();
        // The verified route is the whole plan; the existing solver only replays and packages it.
        SolverSearchProfile materializeProfile = profile with
        {
            BeamWidth = 1,
            MaxExpandedNodes = 1,
            SoftTimeBudgetMilliseconds = Math.Max(1, Math.Min(profile.SoftTimeBudgetMilliseconds, 2_000)),
        };
        SolverResult result;
        try
        {
            result = new CombatBeamSolver(root, displayNames, battleDamage, policy, cancellationToken,
                progressCallback, materializeProfile, potionPolicyOverride: potionPolicyOverride,
                potionFreePolicyBaseline: potionFreePolicyBaseline,
                maximumPotionUses: maximumPotionUses,
                fixedPrefixActions: plan,
                resetFixedPrefixSchedulingBaseline: resetFixedPrefixSchedulingBaseline ?? false,
                minimumPotionUses: minimumPotionUses,
                // The primary-member materializer did not seed the incumbent; keep that path unchanged.
                primaryIncumbent: continuationPurpose == null ? null : primaryIncumbent,
                earliestPotionTurn: earliestPotionTurn,
                attributionPurpose: continuationPurpose,
                directSearchPurpose: purpose).Solve();
        }
        catch (PotionPolicyUnsatisfiedException ex)
        {
            // The existing final ordering rejected the materialized route under the request's own
            // potion policy. That is a fallback reason, not a Flash verification failure.
            return new FlashSolveOutcome(null, FlashRejection.None, ex.Message, search, plan,
                admission, verification, watch.Elapsed)
            {
                ConstraintFailure = $"materialize-potion-policy {ex.Message}",
            };
        }
        if (planCommitment is { } commitment)
        {
            int realized = CountPlanPayoffs(plan, commitment);
            if (realized != search.BestPayoffCount)
            {
                throw new FlashVerificationException(plan.Count,
                    [$"plan payoff old={realized} flash={search.BestPayoffCount}"]);
            }
        }
        bool verifyPotion = auditPotionPolicy
            || minimumPotionUses != null
            || maximumPotionUses != null
            || potionPolicyOverride != null
            || earliestPotionTurn != null
            || enforceForcedPotionDirectives && policy.PotionStrategy.HasForcedDirectives;
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
        // succeeded. A later failure falls back to the existing solver, whose observer must be the only
        // one that reports to the coordinator.
        if (earlyTurnScoutObserver != null && search.ScoutLayers.Count > 0)
        {
            List<(int Turns, IReadOnlyList<EarlyTurnFrontierCandidate> Candidates)> layers = new(
                search.ScoutLayers.Count);
            foreach (FlashScoutLayer layer in search.ScoutLayers)
            {
                List<EarlyTurnFrontierCandidate> candidates = new(layer.Candidates.Count);
                foreach (FlashScoutCandidate candidate in layer.Candidates)
                {
                    PlanAction[] actions = TranslateScoutRoute(
                        replay, player, prefix, candidate.Actions, out StateFingerprint stateKey);
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
        return new FlashSolveOutcome(result, FlashRejection.None, "", search, plan, admission, verification, watch.Elapsed);
    }

    /// <summary>
    /// The Flash form of a <see cref="PlanCommitment"/>. <c>RegisteredPowerBenefit</c> reads the
    /// existing <c>PowerCommitment.RealizedEvidence</c>, which FlashBeam does not model; the request is
    /// rejected with the normal unsupported-request result instead of searching with a partial policy.
    /// </summary>
    private static FlashPlanCommitment ToFlashPlanCommitment(PlanCommitment plan)
    {
        if (plan.Payoff.Kind != PlanPayoffEvidenceKind.CardPlayed)
        {
            throw new FlashImportException(FlashRejection.UnsupportedRequest,
                $"计划收益证据 {plan.Payoff.Kind} 未移植");
        }
        if (!FlashRegistry.TryCard(plan.Payoff.SourceId, out ushort cardId))
        {
            throw new FlashImportException(FlashRejection.UnsupportedRequest,
                $"计划收益卡 {plan.Payoff.SourceId} 没有 Flash 定义");
        }
        return new FlashPlanCommitment(
            plan.Kind == PlanCommitmentKind.CopyPower,
            plan.OpenedTurn,
            plan.Payoff.EarliestTurn,
            FlashPlanPayoffKind.CardPlayed,
            cardId);
    }

    /// <summary>The existing <c>PlanCommitment.CountRealizedPayoffs</c> over materialized actions.</summary>
    private static int CountPlanPayoffs(IReadOnlyList<PlanAction> actions, PlanCommitment plan)
    {
        if (plan.Payoff.Kind != PlanPayoffEvidenceKind.CardPlayed)
            throw new ArgumentOutOfRangeException(nameof(plan), "只有 CardPlayed 收益证据可计数。");
        int count = 0;
        foreach (PlanAction action in actions)
        {
            if (action.Kind == PlanActionKind.PlayCard
                && action.Turn >= plan.Payoff.EarliestTurn
                && string.Equals(action.CardId, plan.Payoff.SourceId, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Replays a Flash scout route through the existing engine from the request root and returns the
    /// translator's <see cref="PlanAction"/>s plus the end state's fingerprint. The scout is admitted
    /// only for an empty fixed prefix, so the route starts at the request root.
    /// </summary>
    private static PlanAction[] TranslateScoutRoute(CombatBeamSolver replay, Player player,
        IReadOnlyList<PlanAction> prefix, FlashAction[] route, out StateFingerprint stateKey)
    {
        SimulationSnapshot snapshot = replay.ReplayForFlash(prefix, null);
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
                    next = FlashChoiceTranslator.Replay(replay, snapshot, player, route[step],
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

    private static FlashPotionPolicy PotionPolicy(CombatBeamSolver replay, SimulationSnapshot rootSnapshot,
        int rootPlayerTurn)
    {
        (bool[] allowed, int[] costs, int? earliestTurn, int? maximumUses) = replay.CapturePotionPolicyForFlash(rootSnapshot);
        byte mask = 0;
        int[] slotCosts = new int[FlashLimits.PotionSlots];
        for (int slot = 0; slot < allowed.Length && slot < FlashLimits.PotionSlots; slot++)
        {
            if (allowed[slot])
                mask |= (byte)(1 << slot);
            slotCosts[slot] = costs[slot];
        }
        // The earliest turn is in request-root units; a fixed prefix must not shift it.
        int earliest = earliestTurn is { } turn ? turn - rootSnapshot.Turn + rootPlayerTurn : int.MinValue;
        return new FlashPotionPolicy(mask, earliest, maximumUses, slotCosts);
    }
}
