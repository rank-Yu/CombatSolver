using CombatSolver.Engine.InCombat.Flash;
using System.Diagnostics;
using System.Globalization;
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
            or DirectSearchPurpose.TurnBoundaryDiscovery;
        FlashSearchMode mode = auditable ? request.Policy.FlashMode : FlashSearchMode.Off;
        if (mode == FlashSearchMode.On && TurnSetupBoundary(request) is { } turnSetup)
        {
            // The turn-setup root protocol is not part of this batch; admit the root explicitly and
            // keep the existing search rather than searching a phase Flash does not model.
            return SolveExisting(request, turnSetup);
        }
        if (mode != FlashSearchMode.Off
            && request.Purpose == DirectSearchPurpose.SmartPotionGradient
            && PotionAuditBoundary(request) is { } boundary)
        {
            if (mode == FlashSearchMode.Shadow)
            {
                Stopwatch watch = Stopwatch.StartNew();
                SolverResult existing = SolveExisting(request);
                request.Policy.Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_SHADOW purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} "
                    + $"existing=[{Describe(existing, watch.Elapsed)}] flash=[status=boundary ms=0 detail={boundary}] same_route=n/a");
                return existing;
            }
            request.Policy.Diagnostics.Info(
                $"[CombatSolver/Flash] FLASH_FALLBACK purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} "
                + $"flash=[status=boundary ms=0 detail={boundary}]");
            return SolveExisting(request, $"potion-audit-boundary:{boundary}");
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
                return SolveExisting(request, LegacyReason(flash));
            }
            default:
                throw new InvalidOperationException($"Unknown Flash search mode {mode}.");
        }
    }

    /// <summary>
    /// A fixed-prefix continuation solve. The gate (<see cref="TakeOverBoundary"/>) offers plan members,
    /// the plan pass's depth-one scout and the potion-constrained requests to Flash; requests with a
    /// feature Flash does not model are refused with a stable reason and go straight to the existing solver.
    /// </summary>
    public static SolverResult SolveContinuation(ContinuationSearchRequest request)
    {
        bool potionAudit = request.Purpose == ContinuationPurpose.SmartOpeningPotionPosterior;
        SearchPolicySnapshot effectivePolicy = EffectivePolicy(request);
        string? turnSetup = TurnSetupBoundary(request);
        string? gate = turnSetup ?? (potionAudit ? null : TakeOverBoundary(request));
        FlashSearchMode mode = gate is null ? effectivePolicy.FlashMode : FlashSearchMode.Off;
        if (gate is not null && effectivePolicy.FlashMode == FlashSearchMode.On)
        {
            // On mode must account for every legacy search it runs; off/shadow comparison modes do not log.
            return SolveExistingContinuation(request,
                turnSetup is not null ? turnSetup : $"can-take-over:{gate}");
        }
        if (potionAudit && mode != FlashSearchMode.Off
            && PotionAuditBoundary(request) is { } boundary)
        {
            if (mode == FlashSearchMode.Shadow)
            {
                Stopwatch watch = Stopwatch.StartNew();
                SolverResult existing = SolveExistingContinuation(request);
                effectivePolicy.Diagnostics.Info(
                    $"[CombatSolver/Flash] FLASH_SHADOW purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} "
                    + $"prefix={request.Prefix.Length} existing=[{Describe(existing, watch.Elapsed)}] "
                    + $"flash=[status=boundary ms=0 detail={boundary}] same_route=n/a");
                return existing;
            }
            effectivePolicy.Diagnostics.Info(
                $"[CombatSolver/Flash] FLASH_FALLBACK purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} "
                + $"prefix={request.Prefix.Length} flash=[status=boundary ms=0 detail={boundary}]");
            return SolveExistingContinuation(request, $"potion-audit-boundary:{boundary}");
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
    /// The explicit fallback reasons of a Smart potion-gradient request. The gradient is only defined for
    /// a Smart policy with a searchable potion; every other request feature now goes through the same
    /// audited final ordering as the existing solver.
    /// </summary>
    private static string? PotionAuditBoundary(BeamSolveRequest request)
    {
        if (request.Policy.PotionPolicy != SolverPotionPolicy.Smart)
            return "potion-policy";
        if (request.Root.SearchablePotions.Count == 0)
            return "no-searchable-potion";
        return null;
    }

    /// <summary>
    /// The explicit fallback reasons of a Smart opening-potion posterior request. The audit itself is
    /// only defined for a Smart policy with a searchable potion; plan commitments and early-turn scouts
    /// are separate kernel structures and stay out of this batch.
    /// </summary>
    private static string? PotionAuditBoundary(ContinuationSearchRequest request)
    {
        if (request.Purpose != ContinuationPurpose.SmartOpeningPotionPosterior)
            return "purpose";
        if (request.Commitment != null)
            return "plan-commitment";
        if (request.EarlyTurnScoutDepth != 0 || request.EarlyTurnScoutObserver != null)
            return "early-turn-scout";
        SearchPolicySnapshot policy = EffectivePolicy(request);
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return "potion-policy";
        if (request.Context.Root.SearchablePotions.Count == 0)
            return "no-searchable-potion";
        if (request.MinimumPotionUses is < 0 || request.MaximumPotionUses is < 0)
            return "potion-bounds";
        if (request.MinimumPotionUses is { } minimum
            && request.MaximumPotionUses is { } maximum && minimum > maximum)
        {
            return "potion-bounds";
        }
        return null;
    }

    /// <summary>
    /// The continuation gate. Plan members and the plan pass's own depth-one scout are admitted by the
    /// two plan helpers below; other early-turn scouts and invalid potion bounds keep the existing
    /// solver (the latter for its own argument validation); potion-constrained requests must not
    /// silently drop a retention switch Flash does not model.
    /// </summary>
    private static string? TakeOverBoundary(ContinuationSearchRequest request)
    {
        if (request.Commitment != null)
            return CanTakeOverPlanMember(request) ? null : "commitment";
        if (request.EarlyTurnScoutDepth != 0 || request.EarlyTurnScoutObserver != null)
            return CanTakeOverPlanScout(request) ? null : "early-turn-scout";
        if (request.MinimumPotionUses is < 0 || request.MaximumPotionUses is < 0)
            return "potion-bounds";
        if (request.MinimumPotionUses is { } minimum
            && request.MaximumPotionUses is { } maximum && minimum > maximum)
        {
            return "potion-bounds";
        }
        // Requests opened by this batch must not silently drop a retention switch Flash does not
        // model. Clean requests keep their prior behavior (their profile was already offered to Flash).
        if (HasPotionConstraint(request))
        {
            if (request.Profile.SecondRankBand)
                return "second-rank-band";
            if (request.Profile.AggressivePowerCommitment)
                return "aggressive-power-commitment";
        }
        return null;
    }

    /// <summary>
    /// A <c>ContinuationPurpose.PlanCommitment</c> member. The plan prefix is replayed by the existing
    /// engine before the Flash root; the commitment adds route payoff counting and the CopyPower
    /// representative seats (<c>CombatBeamSolver.Expansion</c> / <c>BeamRetentionPolicy</c>). Potion
    /// bounds on a plan are not offered yet: only the potion-free form and the deferred plan's
    /// no-override Smart form (the primary member's existing Flash potion path) are admitted.
    /// </summary>
    private static bool CanTakeOverPlanMember(ContinuationSearchRequest request)
    {
        if (request.Purpose != ContinuationPurpose.PlanCommitment
            || request.Commitment is not { } plan
            || request.EarlyTurnScoutDepth != 0
            || request.EarlyTurnScoutObserver != null
            || request.PotionFreePolicyBaseline != null
            || request.EarliestPotionTurn != null
            || request.MinimumPotionUses is not (null or 0)
            || request.MaximumPotionUses is not (null or 0)
            || plan.Payoff.Kind != PlanPayoffEvidenceKind.CardPlayed)
        {
            return false;
        }
        SearchPolicySnapshot policy = EffectivePolicy(request);
        if (policy.PotionStrategy.HasForcedDirectives)
            return false;
        if (PlanPotionFree(request))
            return true;
        return request.PotionPolicyOverride == null
            && request.MaximumPotionUses == null
            && request.MinimumPotionUses == null
            && policy.PotionPolicy == SolverPotionPolicy.Smart;
    }

    /// <summary>
    /// The deferred-power plan's depth-one scout. Only the plan pass's own scout
    /// (<c>RunDeferredPowerPlanSearchPass</c>, empty prefix, depth one) is offered to Flash; the other
    /// early-turn exploration requests stay with the existing solver. The bridge translates the reported
    /// Flash routes back to <see cref="EarlyTurnFrontierCandidate"/>s.
    /// </summary>
    private static bool CanTakeOverPlanScout(ContinuationSearchRequest request)
    {
        if (request.Purpose != ContinuationPurpose.PlanCommitment
            || request.Commitment != null
            || request.EarlyTurnScoutDepth != 1
            || request.EarlyTurnScoutObserver == null
            || request.Prefix.Length != 0
            || request.PotionFreePolicyBaseline != null
            || request.EarliestPotionTurn != null
            || request.MaximumPotionUses is not (null or 0)
            || request.MinimumPotionUses is not (null or 0))
        {
            return false;
        }
        return PlanPotionFree(request);
    }

    /// <summary>Whether a plan member or plan scout explicitly searches without potions.</summary>
    private static bool PlanPotionFree(ContinuationSearchRequest request)
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

    /// <summary>Whether the request carries one of the potion features opened by this batch.</summary>
    private static bool HasPotionConstraint(ContinuationSearchRequest request)
    {
        SearchPolicySnapshot policy = EffectivePolicy(request);
        SolverPotionPolicy effectivePolicy = request.PotionPolicyOverride ?? policy.PotionPolicy;
        return request.MaximumPotionUses is > 0
            || request.MinimumPotionUses is > 0
            || request.PotionFreePolicyBaseline != null
            || request.EarliestPotionTurn != null
            || effectivePolicy == SolverPotionPolicy.RequireAtLeastOne
            || request.PotionPolicyOverride == null && policy.PotionStrategy.HasForcedDirectives;
    }

    /// <summary>
    /// The root protocol boundary. For now only ordinary play-phase roots are admitted; turn-setup
    /// roots need a kernel/protocol port and are refused explicitly before Flash runs.
    /// </summary>
    private static string? TurnSetupBoundary(BeamSolveRequest request)
    {
        if (request.Policy.IncludeTurnSetup
            || request.Root.CurrentSide != CombatSide.Player
            || request.Root.PlayerPhase != PlayerTurnPhase.Play)
        {
            return "turn-setup";
        }
        return null;
    }

    private static string? TurnSetupBoundary(ContinuationSearchRequest request)
    {
        SearchPolicySnapshot policy = EffectivePolicy(request);
        if (policy.IncludeTurnSetup
            || request.Context.Root.CurrentSide != CombatSide.Player
            || request.Context.Root.PlayerPhase != PlayerTurnPhase.Play)
        {
            return "turn-setup";
        }
        return null;
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
            ? "turn-setup"
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
                // The narrow opening incumbent deliberately searches a 1/16-width pilot. Keep that
                // intent instead of applying Flash's usual 4x profile scale to it.
                profileScale: request.Purpose == DirectSearchPurpose.NarrowOpeningIncumbent
                    ? 1 : FlashRouteMaterializer.ProfileWidthScale);
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
                // ProgressCallbackOverride stays with the existing solver; the Flash search never
                // reports progress. The potion audit request's scheduling reset is forwarded to the
                // materialize solver so its fixed-prefix baseline is the same.
                progressCallback: null,
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
            + $"flash_capacity_pruned={search.CapacityPruned}");
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
