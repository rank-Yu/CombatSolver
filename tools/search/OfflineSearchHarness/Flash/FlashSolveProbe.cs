using System.Diagnostics;
using CombatSolver;
using CombatSolver.Flash.Search;
using CombatSolver.Flash.Search.Scoring;
using CombatSolver.Flash.Bridge;
using MegaCrit.Sts2.Core.Combat;

namespace OfflineSearchHarness;

/// <summary>
/// Flash end-to-end scan (OFFLINE_HARNESS_FLASH_SOLVE=1): solves the harness root once through the
/// Flash backend (search, verification replay, fixed-prefix materialization) under the requested
/// profile, then replays at most N final candidates on the existing engine. It reports admission,
/// time, route quality and candidate mismatches; it does not run the existing search side.
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

        int candidateScan = int.TryParse(
            Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FLASH_CANDIDATE_SCAN"), out int scan) ? scan : 0;
        FlashBeamOptions flashOptions = new(
            profile.BeamWidth,
            profile.MaxExpandedNodes,
            MaxTurns: 64,
            TimeSpan.FromMilliseconds(profile.SoftTimeBudgetMilliseconds),
            root.InitialPlayerMaxHp)
        {
            CandidateScanCount = Math.Max(0, candidateScan),
        };
        Stopwatch watch = Stopwatch.StartNew();
        Task<FlashSolveOutcome> flashTask = Task.Run(() => FlashRouteMaterializer.Solve(
            root, names, damage, policy with { Profile = profile }, profile, CancellationToken.None, flashOptions));
        loop.RunUntilCompleted(flashTask, TimeSpan.FromSeconds(660), "FlashRouteMaterializer.Solve");
        FlashSolveOutcome flash = flashTask.GetAwaiter().GetResult();
        double flashMs = watch.Elapsed.TotalMilliseconds;

        Dictionary<string, object?> summary = new()
        {
            ["profile"] = new { profile.BeamWidth, profile.MaxExpandedNodes, profile.SoftTimeBudgetMilliseconds },
            ["flashAdmitted"] = flash.Rejection == FlashRejection.None,
            ["flashOptions"] = new { flashOptions.BeamWidth, flashOptions.MaxExpandedNodes },
        };
        if (flash.Rejection != FlashRejection.None)
        {
            summary["flashRejection"] = $"{flash.Rejection}: {flash.Detail}";
            return summary;
        }
        summary["flash"] = Describe(flash.Result!, flashMs);
        summary["flashCandidateScan"] = new
        {
            routes = flash.CandidateRoutes,
            mismatches = flash.CandidateMismatches,
            details = flash.CandidateMismatchDetails.Take(8).ToArray(),
        };
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

    private static object Describe(SolverResult? result, double wallMs)
    {
        if (result?.Snapshot is null)
            return new { wallMs, error = "no-result-snapshot" };
        return new
        {
        wallMs,
        won = result.Snapshot.AllEnemiesDead && !result.Snapshot.PlayerDead,
        dead = result.Snapshot.PlayerDead,
        playerHp = result.Snapshot.PlayerHp,
        projectedBattleHpLost = result.ProjectedBattleHpLost,
        combatEndedTurn = result.CombatEndedTurn,
        actions = result.BestNode?.ActionCount ?? 0,
        boundary = result.BoundaryReason.ToString(),
        expanded = result.ExpandedNodes,
        route = result.BestNode == null
            ? string.Empty
            : string.Join(" > ", result.BestNode.Actions.Select(action =>
                action.Kind == PlanActionKind.EndTurn ? "End" : $"{action.CardId}@{action.TargetIndex}")),
        };
    }
}
