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

    /// <summary>Both run; the existing result is returned and one comparison line is logged.</summary>
    Shadow,

    /// <summary>
    /// Flash only. A no-route result is returned in the existing search's shape; unsupported content,
    /// capacity, root-phase, verification and development-strategy cases stop the search with
    /// <see cref="FlashSearchFailedException"/>. The existing solver is never constructed in this mode.
    /// </summary>
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
/// direct audit members consult <see cref="SearchPolicySnapshot.FlashMode"/>; fixed-prefix continuations
/// go through <see cref="SolveContinuation"/> under the same mode.
/// <para>
/// Off runs only the existing solver. Shadow runs both, returns the existing result and logs one
/// comparison. On runs only Flash: a potion-policy no-route is reported with the existing
/// <see cref="PotionPolicyUnsatisfiedException"/> shape, and every other refusal becomes one stable
/// <c>FLASH_SEARCH_FAILED</c> event plus <see cref="FlashSearchFailedException"/>. On mode never
/// constructs <see cref="CombatBeamSolver"/> and never writes a legacy search line.
/// </para>
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
        string? rootPhase = RootPhaseBoundary(request);
        if (rootPhase is not null)
        {
            // Only the ordinary play-phase root and the captured player/Start turn-setup root are
            // modeled. Shadow keeps its comparison behavior; On refuses explicitly instead of
            // searching a state the kernel would misinterpret.
            if (mode == FlashSearchMode.Shadow)
                return SolveExisting(request);
            if (mode == FlashSearchMode.On)
                throw Failure(request.Policy, FlashSearchFailureReason.RootPhase, $"root-phase:{rootPhase}");
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
    /// Off runs only the existing solver; Shadow runs both and returns the existing result. On runs only
    /// Flash and either returns the existing no-route shape or throws
    /// <see cref="FlashSearchFailedException"/>; an unsupported root phase is one of those explicit
    /// failures.
    /// </summary>
    public static SolverResult SolveContinuation(ContinuationSearchRequest request)
    {
        SearchPolicySnapshot effectivePolicy = EffectivePolicy(request);
        string? rootPhase = RootPhaseBoundary(request);
        FlashSearchMode mode = effectivePolicy.FlashMode;
        if (rootPhase is not null)
        {
            // Shadow keeps the prior comparison boundary: the existing solver runs alone here.
            if (mode == FlashSearchMode.Shadow)
                return SolveExistingContinuation(request);
            if (mode == FlashSearchMode.On)
                throw Failure(effectivePolicy, FlashSearchFailureReason.RootPhase,
                    $"root-phase:{rootPhase} prefix={request.Prefix.Length}");
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
    /// Turns a non-adopted Flash attempt into the caller's existing no-route shape or one stable
    /// explicit failure. Never falls back to the existing solver.
    /// </summary>
    private static void AttachFlashMetrics(FlashSolveOutcome outcome)
    {
        if (outcome.Result is not { } result || outcome.Search is not { } search)
            return;
        result.FlashExpandedNodes = search.ExpandedNodes;
        result.FlashSearchElapsed = search.Elapsed;
    }

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

    /// <summary>The existing solver, used only by Off mode and by Shadow's existing-side comparison.</summary>
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
    /// The existing solver's fixed-prefix continuation path, used only by Off mode and by Shadow's
    /// existing-side comparison. Off must stay byte-identical.
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
            if (TakeInjectedAttempt(request.Purpose) is { } injected)
                return injected;
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

    /// <summary>
    /// Test-only single-shot fault. It throws the real exception types inside the same try block as a
    /// real Flash solve so the production catch/mapping paths are what the contract exercises.
    /// </summary>
    private static FlashAttempt? TakeInjectedAttempt(DirectSearchPurpose? purpose = null)
    {
        FlashFailureInjection injection = FlashFailureInjectionForTesting.Take(purpose);
        if (injection == FlashFailureInjection.PotionPolicyMismatch)
        {
            const string detail = "route-minimum-potions explicit_uses=0 minimum=1";
            return new FlashAttempt(new FlashSolveOutcome(
                null, FlashRejection.None, detail,
                null, [], TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)
            {
                ConstraintFailure = detail,
            }, null);
        }
        Apply(injection);
        return null;
    }

    private static void Apply(FlashFailureInjection injection)
    {
        switch (injection)
        {
            case FlashFailureInjection.None:
                return;
            case FlashFailureInjection.VerificationMismatch:
                throw new FlashVerificationException(0, ["测试注入：强制核对不一致"]);
            case FlashFailureInjection.KernelUnsupported:
                throw new FlashUnsupportedException("测试注入：强制内核不支持");
            case FlashFailureInjection.KernelCapacity:
                throw new FlashCapacityException("Cards", 1);
            case FlashFailureInjection.AdmissionMultiPlayer:
                throw new FlashImportException(FlashRejection.MultiPlayer, "测试注入：强制多人专用拒绝");
            default:
                throw new ArgumentOutOfRangeException(nameof(injection), injection, null);
        }
    }

    private static FlashAttempt SolveFlashContinuation(ContinuationSearchRequest request)
    {
        try
        {
            if (TakeInjectedAttempt() is { } injected)
                return injected;
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
    /// One Flash attempt. <paramref name="BoundaryReason"/> is set when the Flash search itself reached
    /// a state the derived kernel does not model (<see cref="FlashUnsupportedException"/>) or a capacity
    /// limit raised before the expansion loop (root import/root scoring;
    /// <see cref="FlashCapacityException"/>). A child-level capacity overflow inside the expansion loop
    /// is a search boundary handled by <see cref="FlashBeamSolver"/> and does not reach this record.
    /// Either kind is an explicit On-mode failure, never a fallback. <paramref name="AdmissionRejection"/>
    /// carries an admission exception that escaped the materializer's own admission boundary.
    /// </summary>
    private sealed record FlashAttempt(
        FlashSolveOutcome? Outcome,
        FlashVerificationException? Mismatch,
        FlashSearchFailureReason? BoundaryReason = null,
        string? BoundaryDetail = null,
        FlashRejection? AdmissionRejection = null,
        string? AdmissionDetail = null);
}
