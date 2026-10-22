using CombatSolver.Flash.Kernel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CombatSolver.Flash.Search;
using CombatSolver.Flash.Search.Scoring;
using CombatSolver.Flash.Bridge;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

/// <summary>Which backend runs a coordinator's primary Beam solve.</summary>
internal enum FlashSearchMode
{
    /// <summary>Only the existing <see cref="CombatBeamSolver"/> runs.</summary>
    Off,

    /// <summary>
    /// Flash first. A no-route result is returned in the existing search's shape; unsupported content
    /// and unrepresentable state admitted by Flash raise <see cref="FlashFallbackRequiredException"/>
    /// so the coordinator can discard and re-run the whole search with its saved regular profile, while
    /// capacity, root-phase, verification, development-strategy and other admission cases stop the
    /// search with <see cref="FlashSearchFailedException"/>. The explicit value keeps persisted
    /// <c>On</c> readable after the removed <c>Shadow</c> value (1).
    /// </summary>
    On = 2,
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
/// direct audit members consult <see cref="SearchPolicySnapshot.FlashMode"/>; fixed-prefix continuations
/// go through <see cref="SolveContinuation"/> under the same mode.
/// <para>
/// Off runs only the existing solver. On runs Flash first: a potion-policy no-route is reported with the existing
/// <see cref="PotionPolicyUnsatisfiedException"/> shape; the two admission categories for unsupported content and
/// unrepresentable state raise <see cref="FlashFallbackRequiredException"/> so <see cref="CombatSearchCoordinator.Solve"/>
/// can log one <c>FLASH_FALLBACK</c> and re-run the whole search with the saved regular profile; every other refusal
/// becomes one stable <c>FLASH_SEARCH_FAILED</c> event plus <see cref="FlashSearchFailedException"/>.
/// </para>
/// </summary>
internal static class BeamSolveDispatch
{
    public static SolverResult Solve(BeamSolveRequest request)
    {
        FlashSearchMode mode = ModeFor(request.Purpose, request.Policy);
        string? rootPhase = RootPhaseBoundary(request);
        if (rootPhase is not null)
        {
            // Only the ordinary play-phase root and the captured player/Start turn-setup root are
            // modeled. On refuses explicitly instead of searching a state the kernel would misinterpret.
            if (mode == FlashSearchMode.On)
                throw Failure(request.Policy, FlashSearchFailureReason.RootPhase, $"root-phase:{rootPhase}");
        }
        switch (mode)
        {
            case FlashSearchMode.Off:
                return SolveExisting(request);
            case FlashSearchMode.On:
            {
                // The existing solver rejects these arguments in SolveCore before any search runs.
                // Flash reproduces that exact failure instead of reporting a takeover boundary.
                ValidatePotionBounds(request.MinimumPotionUses, request.MaximumPotionUses);
                RejectDevelopmentStrategy(request.Policy);
                Stopwatch watch = Stopwatch.StartNew();
                FlashAttempt flash = SolveFlash(request);
                TimeSpan flashTime = watch.Elapsed;
                if (flash.Outcome?.Result is { } adopted)
                {
                    request.Policy.Diagnostics.Info(
                        $"[CombatSolver/Flash] FLASH_ADOPTED purpose={request.Purpose} incumbent={IncumbentText(request.PrimaryIncumbent)} flash=[{Describe(flash, flashTime)}]");
                    return adopted;
                }
                ThrowIfFallbackRequired(request.Purpose, flash);
                ThrowOnRefusal(request.Policy, request.Purpose, flash);
                throw new InvalidOperationException("Flash refusal did not throw.");
            }
            default:
                throw new InvalidOperationException($"Unknown Flash search mode {mode}.");
        }
    }

    /// <summary>
    /// A fixed-prefix continuation solve. Every continuation shape FlashBeam models goes through the
    /// Flash backend: plan members (including any early-turn scout, potion baseline, earliest-turn bound,
    /// forced directive and potion bounds), the plan pass's own scout, and the Smart potion posterior.
    /// Off runs only the existing solver. On runs Flash first: the two admission categories for
    /// unsupported content and unrepresentable state raise <see cref="FlashFallbackRequiredException"/>
    /// for the coordinator to discard and re-run the whole search; every other non-adopted shape returns
    /// the existing no-route result or throws <see cref="FlashSearchFailedException"/>; an unsupported
    /// root phase is one of those explicit failures.
    /// </summary>
    public static SolverResult SolveContinuation(ContinuationSearchRequest request)
    {
        SearchPolicySnapshot effectivePolicy = EffectivePolicy(request);
        string? rootPhase = RootPhaseBoundary(request);
        FlashSearchMode mode = effectivePolicy.FlashMode;
        if (rootPhase is not null)
        {
            if (mode == FlashSearchMode.On)
                throw Failure(effectivePolicy, FlashSearchFailureReason.RootPhase,
                    $"root-phase:{rootPhase} prefix={request.Prefix.Length}");
        }
        switch (mode)
        {
            case FlashSearchMode.Off:
                return SolveExistingContinuation(request);
            case FlashSearchMode.On:
            {
                // SolveCore rejects these arguments before any search runs; reproduce that failure.
                ValidatePotionBounds(request.MinimumPotionUses, request.MaximumPotionUses);
                RejectDevelopmentStrategy(effectivePolicy);
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
                ThrowIfFallbackRequired(request.Purpose, flash);
                ThrowOnRefusal(effectivePolicy, request.Purpose, flash);
                throw new InvalidOperationException("Flash refusal did not throw.");
            }
            default:
                throw new InvalidOperationException($"Unknown Flash search mode {mode}.");
        }
    }

    private static SearchPolicySnapshot EffectivePolicy(ContinuationSearchRequest request)
        => request.PolicyOverride ?? request.Context.Policy;

    /// <summary>
    /// The Flash backend is audited for the listed direct purposes only. Every other purpose is an
    /// ordinary existing-solver request in Off mode; in On mode it is an explicit failure instead of a
    /// silent Off fallback, because a new purpose that forgets this list would otherwise bypass the
    /// On-mode failure contract. This category is not one of the two admission fallbacks. Continuation
    /// requests are audited through <see cref="SolveContinuation"/>'s continuation purposes.
    /// </summary>
    private static FlashSearchMode ModeFor(DirectSearchPurpose purpose, SearchPolicySnapshot policy)
    {
        switch (purpose)
        {
            case DirectSearchPurpose.PrimaryBeam:
            case DirectSearchPurpose.RefinementBeam:
            case DirectSearchPurpose.SmartPotionGradient:
            case DirectSearchPurpose.PotionFreeAudit:
            case DirectSearchPurpose.RequiredPotionAudit:
            case DirectSearchPurpose.NarrowOpeningIncumbent:
            case DirectSearchPurpose.TurnBoundaryDiscovery:
            case DirectSearchPurpose.EarlyTurnScout:
            case DirectSearchPurpose.NoveltyExploration:
            case DirectSearchPurpose.AdaptiveNoveltyRefinement:
                return policy.FlashMode;
            default:
                if (policy.FlashMode == FlashSearchMode.On)
                {
                    throw Failure(policy, FlashSearchFailureReason.AdmissionUnsupportedRequest,
                        $"direct-purpose:{purpose}");
                }
                return FlashSearchMode.Off;
        }
    }

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

    private const string FlashPotionNoRoute = "flash-potion-policy-no-route";

    /// <summary>
    /// DevelopmentStrategy is a developer-only ordering hook of the existing solver. Off still supports
    /// it; On has no equivalent and refuses before starting a Flash search.
    /// </summary>
    private static void RejectDevelopmentStrategy(SearchPolicySnapshot policy)
    {
        if (policy.DevelopmentStrategy != null)
        {
            throw Failure(policy, FlashSearchFailureReason.DevelopmentStrategy,
                "开发策略只支持关闭模式；Flash 没有 Prioritize/Retain 的等价结构");
        }
    }

    /// <summary>
    /// The only two admission categories that abort this coordinator search with
    /// <see cref="FlashFallbackRequiredException"/>: unsupported content (card, Power, relic, potion,
    /// orb, monster, enchantment, affliction or generation closure) and an existing-engine state Flash
    /// cannot project (third-party subscriber, unregistered model and similar). The coordinator writes
    /// the one <c>FLASH_FALLBACK</c> line and re-runs the whole search; dispatch never constructs the
    /// regular solver itself. Every other rejection keeps the explicit failure contract. A verification
    /// mismatch or kernel boundary always wins over the admission category.
    /// </summary>
    private static void ThrowIfFallbackRequired(object purpose, FlashAttempt attempt)
    {
        if (attempt.Mismatch != null || attempt.BoundaryReason != null)
            return;
        FlashSearchFailureReason reason;
        string detail;
        if (attempt.AdmissionRejection is { } admission && admission != FlashRejection.None)
        {
            reason = FlashSearchFailureReasons.FromRejection(admission);
            detail = attempt.AdmissionDetail ?? "-";
        }
        else if (attempt.Outcome is { Rejection: not FlashRejection.None } outcome
            && outcome.ConstraintFailure == null)
        {
            reason = FlashSearchFailureReasons.FromRejection(outcome.Rejection);
            detail = outcome.Detail;
        }
        else
        {
            return;
        }
        if (reason is not (FlashSearchFailureReason.AdmissionUnsupportedContent
            or FlashSearchFailureReason.AdmissionUnrepresentableState))
        {
            return;
        }
        throw new FlashFallbackRequiredException(reason, detail, purpose.ToString() ?? "-");
    }

    /// <summary>
    /// Attaches the Flash-only work metrics to an adopted result. Off-mode results carry no Flash state.
    /// </summary>
    private static void AttachFlashMetrics(FlashSolveOutcome outcome)
    {
        if (outcome.Result is not { } result || outcome.Search is not { } search)
            return;
        result.FlashExpandedNodes = search.ExpandedNodes;
        result.FlashSearchElapsed = search.Elapsed;
    }

    /// <summary>
    /// Turns a non-adopted Flash attempt into the caller's existing no-route shape or one stable
    /// explicit failure. Callers first call <see cref="ThrowIfFallbackRequired"/>; an admission
    /// rejection that reaches this method is a non-fallback category.
    /// </summary>
    private static void ThrowOnRefusal(SearchPolicySnapshot policy, object purpose, FlashAttempt attempt)
    {
        if (attempt.Mismatch is { } mismatch)
        {
            throw Failure(policy, FlashSearchFailureReason.VerifyMismatch, mismatch.Message,
                mismatch.ToFailure());
        }
        if (attempt.BoundaryReason is { } boundaryReason)
            throw Failure(policy, boundaryReason, attempt.BoundaryDetail ?? "内核边界");
        if (attempt.AdmissionRejection is { } admissionRejection
            && admissionRejection != FlashRejection.None)
        {
            throw Failure(policy, FlashSearchFailureReasons.FromRejection(admissionRejection),
                $"{admissionRejection}: {attempt.AdmissionDetail ?? "-"}");
        }
        if (attempt.Outcome?.ConstraintFailure is { } constraint)
        {
            // Only the Flash search's own "no eligible candidate" verdict is a no-route result.
            // route-* (post-materialization VerifyRoute) and materialize-potion-policy (the existing
            // final ordering rejecting Flash's route) mean the two sides judged the same policy
            // differently; report that as an explicit mismatch instead of pretending there is no route.
            if (string.Equals(constraint, FlashPotionNoRoute, StringComparison.Ordinal))
                throw NoRoute(policy, constraint);
            throw Failure(policy, FlashSearchFailureReason.PotionPolicyMismatch, constraint);
        }
        if (attempt.Outcome is { } outcome && outcome.Rejection != FlashRejection.None)
        {
            throw Failure(policy, FlashSearchFailureReasons.FromRejection(outcome.Rejection),
                $"{outcome.Rejection}: {outcome.Detail}");
        }
        throw new InvalidOperationException(
            $"Flash 请求 {purpose} 既没有结果也没有可报告的拒绝；这是 Flash 分发缺陷。");
    }

    /// <summary>
    /// The existing search's potion-policy no-route shape. SmartPotionGradient, the opening posterior
    /// and the continuation scheduler already catch <see cref="PotionPolicyUnsatisfiedException"/> and
    /// keep their original policy; the stable detail is logged separately so the decision stays visible.
    /// </summary>
    private static PotionPolicyUnsatisfiedException NoRoute(SearchPolicySnapshot policy, string constraint)
    {
        policy.Diagnostics.Info(
            $"[CombatSolver/Flash] FLASH_NO_ROUTE reason=flash-rejected:{FlashPotionNoRoute} detail={constraint}");
        string message = policy.PotionStrategy.HasForcedDirectives
            ? $"指定药水必须使用，但搜索没有找到可执行路线：{policy.PotionStrategy.DescribeForcedUses()}。"
            : policy.PotionStrategy.DefaultPolicy == SolverPotionPolicy.RequireAtLeastOne
                ? "本场药水策略要求至少使用一瓶，但搜索没有找到可执行的用药路线。"
                : "本场药水策略没有可执行路线。";
        return new PotionPolicyUnsatisfiedException($"{message}（flash-rejected:{FlashPotionNoRoute}）");
    }

    /// <summary>
    /// Writes the one stable event line through the injected diagnostics sink and returns the failure
    /// for the caller to throw. Search never references Runtime, UI or the logger directly.
    /// </summary>
    private static FlashSearchFailedException Failure(SearchPolicySnapshot policy,
        FlashSearchFailureReason reason, string detail, FlashVerificationFailure? verification = null)
    {
        FlashSearchFailedException failure = new(reason, detail, verification);
        policy.Diagnostics.Info(failure.DiagnosticLine);
        return failure;
    }

    /// <summary>The existing solver, used only by Off mode.</summary>
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
                potionFreePolicyBaseline: request.PotionFreePolicyBaseline,
                maximumPotionUses: request.MaximumPotionUses,
                minimumPotionUses: request.MinimumPotionUses,
                primaryIncumbent: request.PrimaryIncumbent,
                earlyTurnScoutDepth: request.EarlyTurnScoutDepth,
                earlyTurnScoutObserver: request.EarlyTurnScoutObserver,
                directSearchPurpose: request.Purpose).Solve();

    /// <summary>
    /// The existing solver's fixed-prefix continuation path, used only by Off mode. Off must stay
    /// byte-identical.
    /// </summary>
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
                earlyTurnScoutDepth: request.EarlyTurnScoutDepth,
                earlyTurnScoutObserver: request.EarlyTurnScoutObserver);
            AttachFlashMetrics(outcome);
            return new FlashAttempt(outcome, null);
        }
        catch (FlashVerificationException ex)
        {
            return new FlashAttempt(null, ex);
        }
        catch (FlashUnsupportedException ex)
        {
            return new FlashAttempt(null, null, FlashSearchFailureReason.KernelUnsupported, ex.Message);
        }
        catch (FlashCapacityException ex)
        {
            return new FlashAttempt(null, null, FlashSearchFailureReason.KernelCapacity,
                $"{ex.Region} capacity={ex.Capacity}");
        }
        catch (FlashImportException ex)
        {
            return new FlashAttempt(null, null, AdmissionRejection: ex.Reason, AdmissionDetail: ex.Detail);
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
            AttachFlashMetrics(outcome);
            return new FlashAttempt(outcome, null);
        }
        catch (FlashVerificationException ex)
        {
            return new FlashAttempt(null, ex);
        }
        catch (FlashUnsupportedException ex)
        {
            return new FlashAttempt(null, null, FlashSearchFailureReason.KernelUnsupported, ex.Message);
        }
        catch (FlashCapacityException ex)
        {
            return new FlashAttempt(null, null, FlashSearchFailureReason.KernelCapacity,
                $"{ex.Region} capacity={ex.Capacity}");
        }
        catch (FlashImportException ex)
        {
            return new FlashAttempt(null, null, AdmissionRejection: ex.Reason, AdmissionDetail: ex.Detail);
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
        if (attempt.BoundaryReason is { } boundaryReason)
            return string.Create(CultureInfo.InvariantCulture,
                $"status=boundary reason={boundaryReason.Token()} ms={elapsed.TotalMilliseconds:0} detail={attempt.BoundaryDetail}");
        if (attempt.AdmissionRejection is { } admission)
            return string.Create(CultureInfo.InvariantCulture,
                $"status=rejected ms={elapsed.TotalMilliseconds:0} reason={admission} detail={attempt.AdmissionDetail}");
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

    /// <summary>
    /// One Flash attempt. <paramref name="BoundaryReason"/> is set when the Flash search itself reached
    /// a state the derived kernel does not model (<see cref="FlashUnsupportedException"/>) or a capacity
    /// limit raised before the expansion loop (root import/root scoring;
    /// <see cref="FlashCapacityException"/>). A child-level capacity overflow inside the expansion loop
    /// is a search boundary handled by <see cref="FlashBeamSolver"/> and does not reach this record.
    /// Either kind is an explicit On-mode failure, never a fallback. <paramref name="AdmissionRejection"/>
    /// carries an admission exception that escaped the materializer's own admission boundary; the
    /// unsupported-content and unrepresentable-state members of that collection are the two fallback
    /// categories handled before <see cref="ThrowOnRefusal"/>.
    /// </summary>
    private sealed record FlashAttempt(
        FlashSolveOutcome? Outcome,
        FlashVerificationException? Mismatch,
        FlashSearchFailureReason? BoundaryReason = null,
        string? BoundaryDetail = null,
        FlashRejection? AdmissionRejection = null,
        string? AdmissionDetail = null);
}
