using CombatSolver.Engine.InCombat.Flash;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CombatSolver.FlashBeam;
using MegaCrit.Sts2.Core.Combat;

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
    DirectSearchPurpose Purpose)
{
    /// <summary>The audited potion-free baseline of a Smart potion layer (gradient audits).</summary>
    internal PotionFreePolicyBaseline? PotionFreePolicyBaseline { get; init; }

    internal int? MaximumPotionUses { get; init; }

    internal int? MinimumPotionUses { get; init; }

    /// <summary>
    /// <c>CombatBeamSolver.earlyTurnScoutDepth</c> for the coordinator's direct early-turn scout.
    /// Zero for every other member.
    /// </summary>
    internal int EarlyTurnScoutDepth { get; init; }

    /// <summary>The direct early-turn scout's per-layer observer; null for every other member.</summary>
    internal Action<int, IReadOnlyList<EarlyTurnFrontierCandidate>>? EarlyTurnScoutObserver { get; init; }
}

/// <summary>
/// The single point where a coordinator Beam solve picks its backend (docs/flash-search/search.md §4).
/// The beam-width portfolio members (<see cref="DirectSearchPurpose.PrimaryBeam"/> and
/// <see cref="DirectSearchPurpose.RefinementBeam"/>), the Smart potion gradient and the coordinator's
/// direct audit members consult <see cref="SearchPolicySnapshot.FlashMode"/>; members whose profile
/// switches Flash does not model (continuous threat ranking, contextual ranking) are rejected at
/// admission and fall back. Fixed-prefix continuations go through <see cref="SolveContinuation"/>
/// under the same mode. Every legacy search that actually runs in On mode writes one
/// <c>FLASH_LEGACY</c> line; Off and Shadow do not.
/// </summary>
internal static class BeamSolveDispatch
{
    public static SolverResult Solve(BeamSolveRequest request)
    {
        bool auditable = request.Purpose is DirectSearchPurpose.PrimaryBeam
            or DirectSearchPurpose.RefinementBeam
            or DirectSearchPurpose.SmartPotionGradient
            or DirectSearchPurpose.PotionFreeAudit
            or DirectSearchPurpose.RequiredPotionAudit
            or DirectSearchPurpose.NarrowOpeningIncumbent
            or DirectSearchPurpose.TurnBoundaryDiscovery
            or DirectSearchPurpose.EarlyTurnScout
            or DirectSearchPurpose.NoveltyExploration
            or DirectSearchPurpose.AdaptiveNoveltyRefinement;
        FlashSearchMode mode = auditable ? request.Policy.FlashMode : FlashSearchMode.Off;
        if (mode != FlashSearchMode.Off && RootPhaseBoundary(request) is { } rootPhase)
        {
            // Only the ordinary play-phase root and the captured player/Start turn-setup root are
            // modeled. Other sides/phases keep the existing solver and say so with a stable reason.
            return SolveExisting(request, $"root-phase:{rootPhase}");
        }
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
                    // The existing solve already reported to the observer; publishing the isolated
                    // Flash frontier too would report every candidate twice.
                    EarlyTurnScoutObserver = null,
                });
                request.Policy.Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_SHADOW purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} "
                    + $"existing=[{Describe(existing, existingTime)}] flash=[{Describe(flash, watch.Elapsed)}] "
                    + $"same_route={SameRoute(existing, flash.Outcome?.Result)}");
                return existing;
            }
            case FlashSearchMode.On:
            {
                // The existing solver rejects these arguments in SolveCore before any search runs.
                // Flash reproduces that exact failure instead of reporting a takeover boundary.
                ValidatePotionBounds(request.MinimumPotionUses, request.MaximumPotionUses);
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
                return SolveExisting(request, LegacyReason(flash));
            }
            default:
                throw new InvalidOperationException($"Unknown Flash search mode {mode}.");
        }
    }

    /// <summary>
    /// A fixed-prefix continuation solve. Every continuation shape FlashBeam models goes through the
    /// Flash backend: plan members (including any early-turn scout, potion baseline, earliest-turn bound,
    /// forced directive and potion bounds), the plan pass's own scout, and the Smart potion posterior.
    /// The only remaining non-Flash continuation is an unsupported root phase, which is an explicit
    /// kernel boundary; Off and Shadow keep their existing behavior.
    /// </summary>
    public static SolverResult SolveContinuation(ContinuationSearchRequest request)
    {
        SearchPolicySnapshot effectivePolicy = EffectivePolicy(request);
        string? rootPhase = RootPhaseBoundary(request);
        FlashSearchMode mode = rootPhase is null ? effectivePolicy.FlashMode : FlashSearchMode.Off;
        if (rootPhase is not null && effectivePolicy.FlashMode == FlashSearchMode.On)
        {
            // On mode must account for every legacy search it runs; off/shadow comparison modes do not log.
            return SolveExistingContinuation(request, $"root-phase:{rootPhase}");
        }
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
                effectivePolicy.Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_SHADOW purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} prefix={request.Prefix.Length} "
                    + $"existing=[{Describe(existing, existingTime)}] flash=[{Describe(flash, watch.Elapsed)}] "
                    + $"same_route={SameRoute(existing, flash.Outcome?.Result)}");
                return existing;
            }
            case FlashSearchMode.On:
            {
                // SolveCore rejects these arguments before any search runs; reproduce that failure.
                ValidatePotionBounds(request.MinimumPotionUses, request.MaximumPotionUses);
                Stopwatch watch = Stopwatch.StartNew();
                FlashAttempt flash = SolveFlashContinuation(request);
                TimeSpan flashTime = watch.Elapsed;
                if (flash.Outcome?.Result is { } adopted)
                {
                    effectivePolicy.Diagnostics.Info(
                        $"[CombatSolver/Flash] FLASH_ADOPTED purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} "
                        + $"prefix={request.Prefix.Length} flash=[{Describe(flash, flashTime)}]");
                    return adopted;
                }
                effectivePolicy.Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_FALLBACK purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} "
                    + $"prefix={request.Prefix.Length} flash=[{Describe(flash, flashTime)}]");
                return SolveExistingContinuation(request, LegacyReason(flash));
            }
            default:
                throw new InvalidOperationException($"Unknown Flash search mode {mode}.");
        }
    }

    private static SearchPolicySnapshot EffectivePolicy(ContinuationSearchRequest request)
        => request.PolicyOverride ?? request.Context.Policy;

    /// <summary>
    /// The existing solver's argument validation for the exact-use bounds (<c>CombatBeamSolver.SolveCore</c>):
    /// a negative bound or a minimum above the maximum is invalid input, not a request shape Flash can
    /// search. Throwing the same exception keeps the Flash backend's result identical instead of logging
    /// a takeover fallback for a request the existing search would reject before searching.
    /// </summary>
    private static void ValidatePotionBounds(int? minimumPotionUses, int? maximumPotionUses)
    {
        int minimum = minimumPotionUses ?? 0;
        if (minimum < 0 || maximumPotionUses is { } maximum && minimum > maximum)
        {
            throw new ArgumentOutOfRangeException(
                "_minimumPotionUses",
                "最少用药数必须非负且不能超过最多用药数。");
        }
    }

    /// <summary>
    /// The root protocol boundary. Two root shapes are modeled: an ordinary player/Play root, and the
    /// player/Start root captured before the native turn-setup page (the bridge enumerates the setup
    /// choices through the existing engine before importing the play root). Every other side/phase is
    /// refused explicitly instead of searching a state the kernel would misinterpret.
    /// </summary>
    private static string? RootPhaseBoundary(BeamSolveRequest request)
        => RootPhaseBoundary(request.Root, request.Policy);

    private static string? RootPhaseBoundary(ContinuationSearchRequest request)
        => RootPhaseBoundary(request.Context.Root, EffectivePolicy(request));

    private static string? RootPhaseBoundary(CombatRootSnapshot root, SearchPolicySnapshot policy)
    {
        if (policy.IncludeTurnSetup)
        {
            return root.CurrentSide == CombatSide.Player
                && root.PlayerPhase == PlayerTurnPhase.Start
                    ? null
                    : $"include-turn-setup:{root.CurrentSide}:{root.PlayerPhase}";
        }
        return root.CurrentSide == CombatSide.Player && root.PlayerPhase == PlayerTurnPhase.Play
            ? null
            : $"{root.CurrentSide}:{root.PlayerPhase}";
    }

    private static string StableConstraintReason(string detail)
    {
        int space = detail.IndexOf(' ');
        string head = space < 0 ? detail : detail[..space];
        return head switch
        {
            "smart-potion-bound" => "smart-potion-bound",
            "materialize-potion-policy" => "materialize-potion-policy",
            "flash-potion-policy-no-route" => "flash-potion-policy-no-route",
            _ when head.StartsWith("route-", StringComparison.Ordinal) => head,
            _ => head.Length == 0 ? "constraint" : head,
        };
    }

    private static string LegacyReason(FlashAttempt attempt)
    {
        if (attempt.Mismatch != null)
            return "flash-mismatch";
        if (attempt.Outcome?.ConstraintFailure is { } constraint)
            return $"flash-rejected:{StableConstraintReason(constraint)}";
        if (attempt.Boundary != null)
            return "flash-boundary";
        FlashRejection rejection = attempt.Outcome?.Rejection ?? FlashRejection.None;
        return rejection == FlashRejection.RootPhase
            ? "root-phase"
            : $"flash-rejected:{rejection}";
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

    private static SolverResult SolveExisting(BeamSolveRequest request, string? legacyReason = null)
    {
        SolverResult Solve()
            => new CombatBeamSolver(
                request.Root,
                request.DisplayNames,
                request.BattleDamage,
                request.Policy,
                request.CancellationToken,
                request.ProgressCallback,
                request.Profile,
                potionPolicyOverride: request.PotionPolicyOverride,
                potionFreePolicyBaseline: request.PotionFreePolicyBaseline,
                maximumPotionUses: request.MaximumPotionUses,
                minimumPotionUses: request.MinimumPotionUses,
                primaryIncumbent: request.PrimaryIncumbent,
                earlyTurnScoutDepth: request.EarlyTurnScoutDepth,
                earlyTurnScoutObserver: request.EarlyTurnScoutObserver,
                directSearchPurpose: request.Purpose).Solve();
        return legacyReason is null
            ? Solve()
            : FlashLegacyLog.Run(request.Policy, request.Purpose.ToString(), legacyReason, Solve);
    }

    /// <summary>The existing solver's fixed-prefix continuation path; Off must stay byte-identical.</summary>
    private static SolverResult SolveExistingContinuation(ContinuationSearchRequest request)
        => SolveExistingContinuation(request, null);

    private static SolverResult SolveExistingContinuation(ContinuationSearchRequest request,
        string? legacyReason)
    {
        SearchPassContext input = request.Context;
        SolverResult Solve()
            => new CombatBeamSolver(
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
        return legacyReason is null
            ? Solve()
            : FlashLegacyLog.Run(EffectivePolicy(request), request.Purpose.ToString(), legacyReason,
                Solve);
    }

    /// <summary>
    /// One Flash solve. A rejected root comes back as an outcome without a result; a verification
    /// mismatch discards the whole Flash route. Any other failure is a Flash defect and propagates.
    /// </summary>
    private static FlashAttempt SolveFlash(BeamSolveRequest request)
    {
        try
        {
            FlashSolveOutcome outcome = FlashRouteMaterializer.Solve(
                request.Root,
                request.DisplayNames,
                request.BattleDamage,
                request.Policy,
                request.Profile,
                request.CancellationToken,
                potionPolicyOverride: request.PotionPolicyOverride,
                progressCallback: request.ProgressCallback,
                purpose: request.Purpose,
                primaryIncumbent: request.PrimaryIncumbent,
                maximumPotionUses: request.MaximumPotionUses,
                minimumPotionUses: request.MinimumPotionUses,
                potionFreePolicyBaseline: request.PotionFreePolicyBaseline,
                profileScale: ProfileScale(request.Purpose),
                earlyTurnScoutDepth: request.EarlyTurnScoutDepth,
                earlyTurnScoutObserver: request.EarlyTurnScoutObserver);
            return new FlashAttempt(outcome, null, outcome.ConstraintFailure);
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

    /// <summary>
    /// The Flash profile scale of one direct member. The narrow opening incumbent deliberately
    /// searches a 1/16-width pilot; the coordinator already built the scout's wide profile and the
    /// novelty budget is the request's own node/time bound, so those three keep scale one.
    /// </summary>
    private static int ProfileScale(DirectSearchPurpose purpose)
        => purpose is DirectSearchPurpose.NarrowOpeningIncumbent
            or DirectSearchPurpose.EarlyTurnScout
            or DirectSearchPurpose.NoveltyExploration
            or DirectSearchPurpose.AdaptiveNoveltyRefinement
                ? 1
                : FlashRouteMaterializer.ProfileWidthScale;

    private static FlashAttempt SolveFlashContinuation(ContinuationSearchRequest request)
    {
        try
        {
            FlashSolveOutcome outcome = FlashRouteMaterializer.Solve(
                request.Context.Root,
                request.Context.DisplayNames,
                request.Context.BattleDamage,
                EffectivePolicy(request),
                request.Profile,
                request.Context.CancellationToken,
                potionPolicyOverride: request.PotionPolicyOverride,
                // The continuation's own callback (or the request's override) receives the Flash
                // search's progress and route-preview seed at the same cadence as the existing
                // solver. The potion audit request's scheduling reset is forwarded to the
                // materialize solver so its fixed-prefix baseline is the same.
                progressCallback: request.ProgressCallbackOverride ?? request.Context.ProgressCallback,
                primaryIncumbent: request.PrimaryIncumbent,
                fixedPrefixActions: request.Prefix,
                maximumPotionUses: request.MaximumPotionUses,
                minimumPotionUses: request.MinimumPotionUses,
                earliestPotionTurn: request.EarliestPotionTurn,
                continuationPurpose: request.Purpose,
                potionFreePolicyBaseline: request.PotionFreePolicyBaseline,
                planCommitment: request.Commitment,
                earlyTurnScoutDepth: request.EarlyTurnScoutDepth,
                earlyTurnScoutObserver: request.EarlyTurnScoutObserver,
                resetFixedPrefixSchedulingBaseline: request.Purpose == ContinuationPurpose.SmartOpeningPotionPosterior
                    ? request.ResetFixedPrefixSchedulingBaseline : null);
            return new FlashAttempt(outcome, null, outcome.ConstraintFailure);
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
            + $"flash_pruned={search.PrunedIncumbents} flash_smart_pruned={search.SmartPotionPruned} "
            + $"flash_capacity_pruned={search.CapacityPruned} "
            + $"flash_capacity_dropped={DescribeCapacityDrops(search.CapacityDroppedChildren)} "
            + $"flash_ended_compactions={search.EndedCompactions} "
            + $"flash_ended_dropped={search.EndedCompressedStates} "
            + $"flash_yields={search.WorkerYieldCount} "
            + $"flash_frame_waits={search.FrameRecoveryWaitCount} "
            + $"flash_frame_wait_ms={search.FrameRecoveryWaitDuration.TotalMilliseconds:0} "
            + $"flash_max_gc_pause_ms={search.MaxObservedGcPause.TotalMilliseconds:0}");
    }

    /// <summary>The per-region child capacity drops, e.g. <c>Cards:2,Powers:1</c>; <c>-</c> when none.</summary>
    private static string DescribeCapacityDrops(IReadOnlyDictionary<string, int> drops)
    {
        if (drops.Count == 0)
            return "-";
        StringBuilder builder = new();
        foreach ((string region, int count) in drops)
        {
            if (builder.Length > 0)
                builder.Append(',');
            builder.Append(region).Append(':').Append(count);
        }
        return builder.ToString();
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
    /// the derived kernel does not model (<see cref="FlashUnsupportedException"/>), or a capacity limit
    /// raised before the expansion loop (root import/root scoring; <see cref="FlashCapacityException"/>):
    /// like an admission rejection, the request falls back to the existing solver, which is the
    /// authoritative implementation. A child-level capacity overflow inside the expansion loop is a
    /// search boundary handled by <see cref="FlashBeamSolver"/> and does not reach this fallback.
    /// </summary>
    private sealed record FlashAttempt(FlashSolveOutcome? Outcome, FlashVerificationException? Mismatch,
        string? Boundary = null);
}
