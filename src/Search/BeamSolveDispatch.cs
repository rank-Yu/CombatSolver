using CombatSolver.Engine.InCombat.Flash;
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
/// The beam-width portfolio members (<see cref="DirectSearchPurpose.PrimaryBeam"/> and
/// <see cref="DirectSearchPurpose.RefinementBeam"/>) consult <see cref="SearchPolicySnapshot.FlashMode"/>;
/// members whose profile switches Flash does not model (continuous threat ranking, contextual ranking)
/// are rejected at admission and fall back. Fixed-prefix continuations go through
/// <see cref="SolveContinuation"/> under the same mode; only clean requests are offered to Flash.
/// Audits, plan searches and scouts do not come through here.
/// </summary>
internal static class BeamSolveDispatch
{
    public static SolverResult Solve(BeamSolveRequest request)
    {
        FlashSearchMode mode = request.Purpose is DirectSearchPurpose.PrimaryBeam or DirectSearchPurpose.RefinementBeam
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
                    $"[CombatSolver/Flash] FLASH_SHADOW purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} "
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
                        $"[CombatSolver/Flash] FLASH_ADOPTED purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} flash=[{Describe(flash, flashTime)}]");
                    return adopted;
                }
                request.Policy.Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_FALLBACK purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} flash=[{Describe(flash, flashTime)}]");
                return SolveExisting(request);
            }
            default:
                throw new InvalidOperationException($"Unknown Flash search mode {mode}.");
        }
    }

    /// <summary>
    /// A fixed-prefix continuation solve. Only a "clean" request (no explicit potion policy, no potion
    /// baseline, no plan commitment, no early-turn scout, no earliest-potion restriction) is offered to
    /// Flash; anything else goes straight to the existing solver without a Flash log line.
    /// </summary>
    public static SolverResult SolveContinuation(ContinuationSearchRequest request)
    {
        FlashSearchMode mode = CanTakeOver(request) ? EffectivePolicy(request).FlashMode : FlashSearchMode.Off;
        switch (mode)
        {
            case FlashSearchMode.Off:
                return SolveExistingContinuation(request);
            case FlashSearchMode.Shadow:
            {
                Stopwatch watch = Stopwatch.StartNew();
                SolverResult existing = SolveExistingContinuation(request);
                TimeSpan existingTime = watch.Elapsed;
                watch.Restart();
                FlashAttempt flash = SolveFlashContinuation(request with
                {
                    PolicyOverride = Isolated(EffectivePolicy(request)),
                });
                EffectivePolicy(request).Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_SHADOW purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} prefix={request.Prefix.Length} "
                    + $"existing=[{Describe(existing, existingTime)}] flash=[{Describe(flash, watch.Elapsed)}] "
                    + $"same_route={SameRoute(existing, flash.Outcome?.Result)}");
                return existing;
            }
            case FlashSearchMode.On:
            {
                Stopwatch watch = Stopwatch.StartNew();
                FlashAttempt flash = SolveFlashContinuation(request);
                TimeSpan flashTime = watch.Elapsed;
                if (flash.Outcome?.Result is { } adopted)
                {
                    EffectivePolicy(request).Diagnostics.Info(
                        $"[CombatSolver/Flash] FLASH_ADOPTED purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} "
                        + $"prefix={request.Prefix.Length} flash=[{Describe(flash, flashTime)}]");
                    return adopted;
                }
                EffectivePolicy(request).Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_FALLBACK purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} "
                    + $"prefix={request.Prefix.Length} flash=[{Describe(flash, flashTime)}]");
                return SolveExistingContinuation(request);
            }
            default:
                throw new InvalidOperationException($"Unknown Flash search mode {mode}.");
        }
    }

    private static SearchPolicySnapshot EffectivePolicy(ContinuationSearchRequest request)
        => request.PolicyOverride ?? request.Context.Policy;

    /// <summary>
    /// The "clean request" gate. The existing solver's other potion and continuation switches change
    /// its policy, ordering or scheduling in ways Flash does not model, so those requests are never
    /// offered to Flash and never produce a Flash log line.
    /// </summary>
    private static bool CanTakeOver(ContinuationSearchRequest request)
    {
        if (request.MaximumPotionUses is not (null or 0)
            || request.MinimumPotionUses is not (null or 0)
            || request.PotionFreePolicyBaseline != null
            || request.Commitment != null
            || request.EarlyTurnScoutDepth != 0
            || request.EarlyTurnScoutObserver != null
            || request.EarliestPotionTurn != null)
        {
            return false;
        }
        return PotionFree(request);
    }

    /// <summary>
    /// Whether the request cannot spend an explicit potion. A forced directive can require one even
    /// under a disabled default policy, and a RequireAtLeastOne policy is never a potion-free request.
    /// </summary>
    private static bool PotionFree(ContinuationSearchRequest request)
    {
        if (request.PotionPolicyOverride == SolverPotionPolicy.Disabled)
            return true;
        SearchPolicySnapshot policy = EffectivePolicy(request);
        if (policy.PotionStrategy.HasForcedDirectives)
            return false;
        if (request.PotionPolicyOverride == SolverPotionPolicy.RequireAtLeastOne
            || policy.PotionPolicy == SolverPotionPolicy.RequireAtLeastOne)
        {
            return false;
        }
        // A zero maximum means no explicit potion can be enumerated, matching the existing solver.
        return request.MaximumPotionUses == 0
            || policy.PotionPolicy == SolverPotionPolicy.Disabled;
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

    /// <summary>The existing solver's fixed-prefix continuation path; Off must stay byte-identical.</summary>
    private static SolverResult SolveExistingContinuation(ContinuationSearchRequest request)
    {
        SearchPassContext input = request.Context;
        return new CombatBeamSolver(
            input.Root, input.DisplayNames, input.BattleDamage,
            request.PolicyOverride ?? input.Policy,
            input.CancellationToken,
            request.ProgressCallbackOverride ?? input.ProgressCallback,
            request.Profile,
            potionPolicyOverride: request.PotionPolicyOverride,
            potionFreePolicyBaseline: request.PotionFreePolicyBaseline,
            maximumPotionUses: request.MaximumPotionUses,
            fixedPrefixActions: request.Prefix,
            resetFixedPrefixSchedulingBaseline: request.ResetFixedPrefixSchedulingBaseline,
            minimumPotionUses: request.MinimumPotionUses,
            primaryIncumbent: request.PrimaryIncumbent,
            earliestPotionTurn: request.EarliestPotionTurn,
            planCommitment: request.Commitment,
            earlyTurnScoutDepth: request.EarlyTurnScoutDepth,
            earlyTurnScoutObserver: request.EarlyTurnScoutObserver,
            attributionPurpose: request.Purpose).Solve();
    }

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
                purpose: request.Purpose,
                primaryIncumbent: request.PrimaryIncumbent), null);
        }
        catch (FlashVerificationException ex)
        {
            return new FlashAttempt(null, ex);
        }
        catch (FlashUnsupportedException ex)
        {
            return new FlashAttempt(null, null, $"unsupported {ex.Message}");
        }
        catch (FlashCapacityException ex)
        {
            return new FlashAttempt(null, null, $"capacity {ex.Message}");
        }
    }

    private static FlashAttempt SolveFlashContinuation(ContinuationSearchRequest request)
    {
        try
        {
            return new FlashAttempt(FlashRouteMaterializer.Solve(
                request.Context.Root,
                request.Context.DisplayNames,
                request.Context.BattleDamage,
                EffectivePolicy(request),
                request.Profile,
                request.Context.CancellationToken,
                potionPolicyOverride: request.PotionPolicyOverride,
                // ProgressCallbackOverride and ResetFixedPrefixSchedulingBaseline stay with the
                // existing solver; the Flash search never reports progress or reads the reset flag.
                progressCallback: null,
                primaryIncumbent: request.PrimaryIncumbent,
                fixedPrefixActions: request.Prefix,
                maximumPotionUses: request.MaximumPotionUses,
                minimumPotionUses: request.MinimumPotionUses,
                earliestPotionTurn: request.EarliestPotionTurn,
                continuationPurpose: request.Purpose), null);
        }
        catch (FlashVerificationException ex)
        {
            return new FlashAttempt(null, ex);
        }
        catch (FlashUnsupportedException ex)
        {
            return new FlashAttempt(null, null, $"unsupported {ex.Message}");
        }
        catch (FlashCapacityException ex)
        {
            return new FlashAttempt(null, null, $"capacity {ex.Message}");
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
        if (attempt.Boundary is { } boundary)
            return string.Create(CultureInfo.InvariantCulture,
                $"status=boundary ms={elapsed.TotalMilliseconds:0} detail={boundary}");
        FlashSolveOutcome outcome = attempt.Outcome!;
        if (outcome.Result == null)
            return string.Create(CultureInfo.InvariantCulture,
                $"status=rejected ms={elapsed.TotalMilliseconds:0} reason={outcome.Rejection} detail={outcome.Detail}");
        FlashBeamResult search = outcome.Search!;
        return string.Create(CultureInfo.InvariantCulture,
            $"status=verified {Describe(outcome.Result, elapsed)} "
            + $"admission_ms={outcome.AdmissionTime.TotalMilliseconds:0} search_ms={search.Elapsed.TotalMilliseconds:0} "
            + $"verify_ms={outcome.VerificationTime.TotalMilliseconds:0} materialize_ms={outcome.MaterializationTime.TotalMilliseconds:0} "
            + $"flash_expanded={search.ExpandedNodes} flash_transitions={search.Transitions} "
            + $"flash_pruned={search.PrunedIncumbents} flash_capacity_pruned={search.CapacityPruned}");
    }

    private static string IncumbentText(PrimarySearchIncumbent? incumbent)
        => incumbent is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"{value.StrategicHpDeficit}@{value.CombatEndedTurn}")
            : "-";

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

    /// <summary>
    /// One Flash attempt. <paramref name="Boundary"/> is set when the Flash search itself reached a state
    /// the derived kernel does not model (<see cref="FlashUnsupportedException"/>) or a capacity limit
    /// (<see cref="FlashCapacityException"/>): like an admission rejection, the request falls back to the
    /// existing solver, which is the authoritative implementation.
    /// </summary>
    private sealed record FlashAttempt(FlashSolveOutcome? Outcome, FlashVerificationException? Mismatch,
        string? Boundary = null);
}
