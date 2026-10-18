using System.Diagnostics;
using System.Globalization;
using CombatSolver.FlashBeam;

namespace CombatSolver;

/// <summary>Which backend runs a coordinator's primary Beam solve.</summary>
internal enum FlashSearchMode
{
    /// <summary>Only the existing <see cref="CombatBeamSolver"/> runs.</summary>
    Off,

    /// <summary>Both run; the existing result is returned and one comparison line is logged.</summary>
    Shadow,

    /// <summary>Flash runs; a rejected root or a failed verification falls back to the existing solver.</summary>
    On,
}

/// <summary>The arguments of one coordinator Beam solve, independent of the backend that runs it.</summary>
internal sealed record BeamSolveRequest(
    CombatRootSnapshot Root,
    SolverDisplayNames DisplayNames,
    BattleDamageSnapshot BattleDamage,
    SearchPolicySnapshot Policy,
    CancellationToken CancellationToken,
    Action<SolverProgress>? ProgressCallback,
    SolverSearchProfile Profile,
    SolverPotionPolicy? PotionPolicyOverride,
    PrimarySearchIncumbent? PrimaryIncumbent,
    DirectSearchPurpose Purpose);

/// <summary>
/// The single point where a coordinator Beam solve picks its backend (docs/flash-search/search.md §4).
/// Only <see cref="DirectSearchPurpose.PrimaryBeam"/> requests consult
/// <see cref="SearchPolicySnapshot.FlashMode"/>; refinement members, audits and scouts keep the
/// existing solver because their profile switches have no Flash counterpart.
/// </summary>
internal static class BeamSolveDispatch
{
    public static SolverResult Solve(BeamSolveRequest request)
    {
        FlashSearchMode mode = request.Purpose == DirectSearchPurpose.PrimaryBeam
            ? request.Policy.FlashMode : FlashSearchMode.Off;
        switch (mode)
        {
            case FlashSearchMode.Off:
                return SolveExisting(request);
            case FlashSearchMode.Shadow:
            {
                Stopwatch watch = Stopwatch.StartNew();
                SolverResult existing = SolveExisting(request);
                TimeSpan existingTime = watch.Elapsed;
                watch.Restart();
                FlashAttempt flash = SolveFlash(request with
                {
                    Policy = Isolated(request.Policy),
                    ProgressCallback = null,
                });
                request.Policy.Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_SHADOW purpose={request.Purpose} "
                    + $"existing=[{Describe(existing, existingTime)}] flash=[{Describe(flash, watch.Elapsed)}] "
                    + $"same_route={SameRoute(existing, flash.Outcome?.Result)}");
                return existing;
            }
            case FlashSearchMode.On:
            {
                Stopwatch watch = Stopwatch.StartNew();
                FlashAttempt flash = SolveFlash(request);
                TimeSpan flashTime = watch.Elapsed;
                if (flash.Outcome?.Result is { } adopted)
                {
                    request.Policy.Diagnostics.Info(
                        $"[CombatSolver/Flash] FLASH_ADOPTED purpose={request.Purpose} flash=[{Describe(flash, flashTime)}]");
                    return adopted;
                }
                request.Policy.Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_FALLBACK purpose={request.Purpose} flash=[{Describe(flash, flashTime)}]");
                return SolveExisting(request);
            }
            default:
                throw new InvalidOperationException($"Unknown Flash search mode {mode}.");
        }
    }

    /// <summary>
    /// The policy a Shadow Flash solve runs under. Nothing it does may reach the request's shared
    /// state: work totals, the shared incumbent table, takeover requests and the replay solvers'
    /// own diagnostics stay with the existing search, so Shadow returns what Off would.
    /// </summary>
    private static SearchPolicySnapshot Isolated(SearchPolicySnapshot policy) => policy with
    {
        RequestWorkTotals = null,
        PrimaryIncumbents = null,
        Interaction = null,
        Diagnostics = new SearchDiagnosticsSink(static _ => { }, static _ => { }),
    };

    private static SolverResult SolveExisting(BeamSolveRequest request)
        => new CombatBeamSolver(
            request.Root,
            request.DisplayNames,
            request.BattleDamage,
            request.Policy,
            request.CancellationToken,
            request.ProgressCallback,
            request.Profile,
            potionPolicyOverride: request.PotionPolicyOverride,
            primaryIncumbent: request.PrimaryIncumbent,
            directSearchPurpose: request.Purpose).Solve();

    /// <summary>
    /// One Flash solve. A rejected root comes back as an outcome without a result; a verification
    /// mismatch discards the whole Flash route. Any other failure is a Flash defect and propagates.
    /// </summary>
    private static FlashAttempt SolveFlash(BeamSolveRequest request)
    {
        try
        {
            return new FlashAttempt(FlashRouteMaterializer.Solve(
                request.Root,
                request.DisplayNames,
                request.BattleDamage,
                request.Policy,
                request.Profile,
                request.CancellationToken,
                potionPolicyOverride: request.PotionPolicyOverride,
                progressCallback: request.ProgressCallback,
                purpose: request.Purpose), null);
        }
        catch (FlashVerificationException ex)
        {
            return new FlashAttempt(null, ex);
        }
    }

    private static string Describe(SolverResult result, TimeSpan elapsed)
        => string.Create(CultureInfo.InvariantCulture,
            $"ms={elapsed.TotalMilliseconds:0} won={(result.Snapshot.AllEnemiesDead && !result.Snapshot.PlayerDead).ToString().ToLowerInvariant()} "
            + $"dead={result.Snapshot.PlayerDead.ToString().ToLowerInvariant()} hp={result.Snapshot.PlayerHp} "
            + $"hp_lost={result.ProjectedBattleHpLost} end_turn={result.CombatEndedTurn} "
            + $"actions={result.BestNode.ActionCount} expanded={result.ExpandedNodes} boundary={result.BoundaryReason}");

    private static string Describe(FlashAttempt attempt, TimeSpan elapsed)
    {
        if (attempt.Mismatch is { } mismatch)
            return string.Create(CultureInfo.InvariantCulture,
                $"status=mismatch ms={elapsed.TotalMilliseconds:0} step={mismatch.Step} detail={mismatch.Message}");
        FlashSolveOutcome outcome = attempt.Outcome!;
        if (outcome.Result == null)
            return string.Create(CultureInfo.InvariantCulture,
                $"status=rejected ms={elapsed.TotalMilliseconds:0} reason={outcome.Rejection} detail={outcome.Detail}");
        FlashBeamResult search = outcome.Search!;
        return string.Create(CultureInfo.InvariantCulture,
            $"status=verified {Describe(outcome.Result, elapsed)} "
            + $"admission_ms={outcome.AdmissionTime.TotalMilliseconds:0} search_ms={search.Elapsed.TotalMilliseconds:0} "
            + $"verify_ms={outcome.VerificationTime.TotalMilliseconds:0} materialize_ms={outcome.MaterializationTime.TotalMilliseconds:0} "
            + $"flash_expanded={search.ExpandedNodes} flash_transitions={search.Transitions}");
    }

    private static string SameRoute(SolverResult existing, SolverResult? flash)
    {
        if (flash == null)
            return "n/a";
        IReadOnlyList<PlanAction> left = existing.BestNode.Actions;
        IReadOnlyList<PlanAction> right = flash.BestNode.Actions;
        if (left.Count != right.Count)
            return "false";
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index].Kind != right[index].Kind || left[index].CardId != right[index].CardId
                || left[index].TargetIndex != right[index].TargetIndex)
                return "false";
        }
        return "true";
    }

    private sealed record FlashAttempt(FlashSolveOutcome? Outcome, FlashVerificationException? Mismatch);
}
