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
    public static FlashSolveOutcome Solve(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        CancellationToken cancellationToken,
        FlashBeamOptions? flashOptions = null)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        Player player = root.PlayerIdentity;
        CombatBeamSolver replay = new(root, displayNames, battleDamage, policy, cancellationToken, searchProfile: profile);
        SimulationSnapshot rootSnapshot = replay.ReplayForFlash([], null);
        FlashRules rules;
        FlashBattle battle;
        try
        {
            rules = FlashRulesCompiler.Compile(rootSnapshot.Simulator, player);
            battle = FlashStateImporter.Import(rootSnapshot, player, rules);
        }
        catch (FlashImportException ex)
        {
            return new FlashSolveOutcome(null, ex.Reason, ex.Detail, null, [], watch.Elapsed, TimeSpan.Zero, TimeSpan.Zero);
        }
        TimeSpan admission = watch.Elapsed;

        FlashBeamOptions options = (flashOptions ?? new FlashBeamOptions(
            profile.BeamWidth,
            profile.MaxExpandedNodes,
            MaxTurns: 64,
            TimeSpan.FromMilliseconds(profile.SoftTimeBudgetMilliseconds),
            root.InitialPlayerMaxHp)) with { Potions = PotionPolicy(replay, rootSnapshot, battle) };
        FlashBeamResult search = new FlashBeamSolver(rules, options).Solve(battle, cancellationToken);

        watch.Restart();
        List<PlanAction> plan = new(search.BestRoute.Length);
        SimulationSnapshot snapshot = rootSnapshot;
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
            FlashBattle projected = FlashStateImporter.Import(snapshot, player, rules);
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
            searchProfile: materializeProfile, fixedPrefixActions: plan).Solve();
        return new FlashSolveOutcome(result, FlashRejection.None, "", search, plan, admission, verification, watch.Elapsed);
    }

    /// <summary>The existing search's potion constraints at the root, in Flash player-turn units.</summary>
    private static FlashPotionPolicy PotionPolicy(CombatBeamSolver replay, SimulationSnapshot rootSnapshot, in FlashBattle battle)
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
        // node.Turn and FlashBattle.PlayerTurn advance together from the root.
        int earliest = earliestTurn is { } turn ? turn - rootSnapshot.Turn + battle.PlayerTurn : int.MinValue;
        return new FlashPotionPolicy(mask, earliest, maximumUses, slotCosts);
    }
}
