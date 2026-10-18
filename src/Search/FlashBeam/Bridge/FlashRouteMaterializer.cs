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
    TimeSpan MaterializationTime);

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
        ContinuationPurpose? continuationPurpose = null)
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
        // The existing solver's _run.Initial* values come from the request root, so the evaluation
        // and bound frozen tables keep reading rootSnapshot even when the search starts at a prefix end.
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
        FlashRules rules;
        FlashBattle battle;
        FlashScorer scorer;
        FlashBoundFeatures bound;
        try
        {
            rules = FlashRulesCompiler.Compile(searchRootSnapshot.Simulator, player);
            battle = FlashStateImporter.Import(searchRootSnapshot, player, rules);
            scorer = FlashScoreBridge.Create(root, policy, profile, rootSnapshot, player, rules, battle);
            bound = FlashBoundBridge.Create(root, policy, rootSnapshot, player, rules, battle, pruning);
        }
        catch (FlashImportException ex)
        {
            return new FlashSolveOutcome(null, ex.Reason, ex.Detail, null, [], watch.Elapsed, TimeSpan.Zero, TimeSpan.Zero);
        }
        TimeSpan admission = watch.Elapsed;

        // Incumbent and potion turns stay in request-root units: the fixed prefix is already part of
        // the existing solver's request, so it must not shift the comparison base.
        int rootPlayerTurn = prefixLength == 0 ? battle.PlayerTurn : rootSnapshot.Turn;
        PrimarySearchIncumbent? incumbent = primaryIncumbent is { } seed
            ? seed with { CombatEndedTurn = seed.CombatEndedTurn - rootSnapshot.Turn + rootPlayerTurn }
            : null;
        FlashBeamOptions options = (flashOptions ?? new FlashBeamOptions(
            checked(profile.BeamWidth * ProfileWidthScale),
            checked(profile.MaxExpandedNodes * ProfileWidthScale),
            MaxTurns: 64,
            TimeSpan.FromMilliseconds(profile.SoftTimeBudgetMilliseconds),
            root.InitialPlayerMaxHp)) with
        {
            Potions = PotionPolicy(replay, rootSnapshot, rootPlayerTurn),
            Scorer = scorer,
            Ordering = new FlashRouteOrdering(root.InitialPlayerHp, root.InitialPlayerMaxHp, root.BossHpRelief,
                root.PostCombatRelicHeal),
            HpTargetStop = HpTargetStop(policy, battleDamage, potionPolicyOverride),
            Bound = bound,
            Incumbent = incumbent,
            EffectivePotionPolicy = pruning.EffectivePotionPolicy,
            MinimumPotionUses = pruning.MinimumPotionUses,
            InitialActionCount = prefixLength,
        };
        FlashBeamResult search = new FlashBeamSolver(rules, options).Solve(battle, cancellationToken);

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
                projected = FlashStateImporter.Import(snapshot, player, rules);
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
        SolverResult result = new CombatBeamSolver(root, displayNames, battleDamage, policy, cancellationToken,
            progressCallback, materializeProfile, potionPolicyOverride: potionPolicyOverride,
            maximumPotionUses: maximumPotionUses,
            fixedPrefixActions: plan,
            minimumPotionUses: minimumPotionUses,
            // The primary-member materializer did not seed the incumbent; keep that path unchanged.
            primaryIncumbent: continuationPurpose == null ? null : primaryIncumbent,
            earliestPotionTurn: earliestPotionTurn,
            attributionPurpose: continuationPurpose,
            directSearchPurpose: purpose).Solve();
        return new FlashSolveOutcome(result, FlashRejection.None, "", search, plan, admission, verification, watch.Elapsed);
    }

    /// <summary>The existing search's potion constraints at the root, in Flash player-turn units.</summary>
    /// <summary>
    /// The existing search stops at the HP target only for victories it counts as eligible
    /// (<c>IsEligibleCompleteVictory</c>). Requests that require explicit potion uses add conditions
    /// FlashBeam does not track, so they keep searching instead.
    /// </summary>
    private static FlashHpTargetStop? HpTargetStop(SearchPolicySnapshot policy, BattleDamageSnapshot battleDamage,
        SolverPotionPolicy? potionPolicyOverride)
    {
        if (!policy.CanStopAtHpTarget
            || (potionPolicyOverride ?? policy.PotionPolicy) == SolverPotionPolicy.RequireAtLeastOne
            || policy.PotionStrategy.HasForcedDirectives)
        {
            return null;
        }
        return new FlashHpTargetStop(policy.AcceptableBattleHpLoss, battleDamage.HpLostSoFar,
            policy.MinimumRequiredPotionUses(battleDamage.PotionsUsedSoFar));
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
