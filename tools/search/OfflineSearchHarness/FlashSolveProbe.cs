using System.Diagnostics;
using CombatSolver;
using CombatSolver.FlashBeam;
using MegaCrit.Sts2.Core.Combat;

namespace OfflineSearchHarness;

/// <summary>
/// Flash end-to-end comparison (OFFLINE_HARNESS_FLASH_SOLVE=1): solves the harness root once with
/// the existing <c>CombatBeamSolver</c> and once through the Flash backend (search, verification
/// replay, fixed-prefix materialization) under the same profile, and reports time and route quality.
/// </summary>
internal static class FlashSolveProbe
{
    public static Dictionary<string, object?> Run(CombatState combat, HarnessOptions options, MainLoopContext loop)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings, combat, includeTurnSetup: false, theftPolicy: SolverController.ResolveTheftPolicy(combat));
        SolverSearchProfile profile = policy.Profile with { SoftTimeBudgetMilliseconds = options.BudgetMilliseconds };

        Stopwatch watch = Stopwatch.StartNew();
        Task<SolverResult> existingTask = Task.Run(() => new CombatBeamSolver(
            root, names, damage, policy with { Profile = profile }, searchProfile: profile).Solve());
        loop.RunUntilCompleted(existingTask, TimeSpan.FromSeconds(660), "CombatBeamSolver.Solve");
        SolverResult existing = existingTask.GetAwaiter().GetResult();
        double existingMs = watch.Elapsed.TotalMilliseconds;

        double widthScale = double.TryParse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FLASH_WIDTH_SCALE"), out double scale) ? scale : FlashRouteMaterializer.ProfileWidthScale;
        double offensiveShare = double.TryParse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FLASH_OFFENSIVE_SHARE"), out double share) ? share : 0.25d;
        FlashBeamOptions flashOptions = new(
            (int)(profile.BeamWidth * widthScale),
            (int)(profile.MaxExpandedNodes * widthScale),
            MaxTurns: 64,
            TimeSpan.FromMilliseconds(profile.SoftTimeBudgetMilliseconds),
            root.InitialPlayerMaxHp) { OffensiveShare = offensiveShare };
        FlashSolveOutcome flash = default!;
        double flashMs = 0;
        List<double> warmRuns = [];
        List<long> warmAllocatedBytes = [];
        for (int run = 0; run < 3; run++)
        {
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            watch.Restart();
            Task<FlashSolveOutcome> flashTask = Task.Run(() => FlashRouteMaterializer.Solve(
                root, names, damage, policy with { Profile = profile }, profile, CancellationToken.None, flashOptions));
            loop.RunUntilCompleted(flashTask, TimeSpan.FromSeconds(660), "FlashRouteMaterializer.Solve");
            flash = flashTask.GetAwaiter().GetResult();
            if (run == 0)
            {
                flashMs = watch.Elapsed.TotalMilliseconds;
            }
            else
            {
                warmRuns.Add(watch.Elapsed.TotalMilliseconds);
                warmAllocatedBytes.Add(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
            }
        }

        Dictionary<string, object?> summary = new()
        {
            ["profile"] = new { profile.BeamWidth, profile.MaxExpandedNodes, profile.SoftTimeBudgetMilliseconds },
            ["existing"] = Describe(existing, existingMs),
            ["flashAdmitted"] = flash.Rejection == FlashRejection.None,
            ["flashOptions"] = new { flashOptions.BeamWidth, flashOptions.MaxExpandedNodes, flashOptions.OffensiveShare },
            ["flashWarmWallMs"] = warmRuns,
            ["flashWarmAllocatedBytes"] = warmAllocatedBytes,
        };
        if (flash.Rejection != FlashRejection.None)
        {
            summary["flashRejection"] = $"{flash.Rejection}: {flash.Detail}";
            return summary;
        }
        summary["flash"] = Describe(flash.Result!, flashMs);
        summary["flashSearch"] = new
        {
            flash.Search!.ExpandedNodes,
            flash.Search.Transitions,
            flash.Search.Duplicates,
            flash.Search.TurnsSearched,
            searchMs = flash.Search.Elapsed.TotalMilliseconds,
            admissionMs = flash.AdmissionTime.TotalMilliseconds,
            verificationMs = flash.VerificationTime.TotalMilliseconds,
            materializationMs = flash.MaterializationTime.TotalMilliseconds,
            routeLength = flash.Plan.Count,
            flash.Search.BestIsVictory,
            flash.Search.PrunedIncumbents,
        };
        return summary;
    }

    private static object Describe(SolverResult result, double wallMs) => new
    {
        wallMs,
        won = result.Snapshot.AllEnemiesDead && !result.Snapshot.PlayerDead,
        dead = result.Snapshot.PlayerDead,
        playerHp = result.Snapshot.PlayerHp,
        projectedBattleHpLost = result.ProjectedBattleHpLost,
        combatEndedTurn = result.CombatEndedTurn,
        actions = result.BestNode.ActionCount,
        boundary = result.BoundaryReason.ToString(),
        expanded = result.ExpandedNodes,
        route = string.Join(" > ", result.BestNode.Actions.Select(action =>
            action.Kind == PlanActionKind.EndTurn ? "End" : $"{action.CardId}@{action.TargetIndex}")),
    };
}
