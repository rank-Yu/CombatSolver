using System.Text.Json;

namespace CombatSolver;

internal enum ContinuationPurpose
{
    EarlyPotionPair,
    EarlierCopyDelayedDamage,
    NoCostOpening,
    TurnEndChoice,
    TurnEndChoiceShorterOpening,
    TurnBoundaryRescue,
    TurnBoundaryFreeOpening,
    TurnBoundaryDefensiveFollowUp,
    ForcedPotionOpening,
    ForcedPotionTurnOrder,
    MidCombatRefinement,
    MidCombatFreeFollowUp,
    EarlyDiscardBeforeGeneration,
    OpeningTargetVariant,
    OpeningTargetPowerVariant,
    OpeningTargetPowerDefensiveVariant,
    DeferredOpeningPower,
    FreeAttackHandSetup,
    OpeningResourceDefense,
    PotionResourcePosterior,
    PotionPowerPosterior,
    PotionPowerDefensivePosterior,
    RequiredOpeningPotion,
    RequiredPotionPair,
    RequiredPotionPairDefensive,
    SmartOpeningPotionPosterior,
    NightmareCopyPosterior,
    OpeningPowerRouteMember,
    EarlyTurnContinuation,
    PlanCommitment,
    GeneratedPotionChain,
}

internal interface IFrontierContinuationSource
{
    bool DeduplicatePrefixes { get; }

    IEnumerable<PlanAction[]> Enumerate();

    bool CanReplay(PlanAction[] prefix);
}

internal sealed record ContinuationSearchRequest(
    SearchPassContext Context,
    ContinuationPurpose Purpose,
    PlanAction[] Prefix,
    SolverSearchProfile Profile,
    SolverPotionPolicy? PotionPolicyOverride,
    int? MaximumPotionUses,
    int? MinimumPotionUses)
{
    internal SearchPolicySnapshot? PolicyOverride { get; init; }
    internal Action<SolverProgress>? ProgressCallbackOverride { get; init; }
    internal PotionFreePolicyBaseline? PotionFreePolicyBaseline { get; init; }
    internal PrimarySearchIncumbent? PrimaryIncumbent { get; init; }
    internal PlanCommitment? Commitment { get; init; }
    internal int EarlyTurnScoutDepth { get; init; }
    internal Action<int, IReadOnlyList<EarlyTurnFrontierCandidate>>? EarlyTurnScoutObserver { get; init; }
    internal int? EarliestPotionTurn { get; init; }
    internal bool ResetFixedPrefixSchedulingBaseline { get; init; } = true;
}

internal sealed record ContinuationSearchOutcome(
    ContinuationSearchRequest Request,
    SolverResult Result);

internal sealed class FrontierContinuationScheduler(SearchPassContext context)
{
    private readonly HashSet<(ContinuationPurpose Purpose, string Prefix)> _seen = [];

    internal SolverResult Dispatch(ContinuationSearchRequest request)
        => BeamSolveDispatch.SolveContinuation(request);

    internal SolverResult? DispatchOptional(
        ContinuationSearchRequest request,
        string diagnostic)
    {
        try
        {
            return BeamSolveDispatch.SolveContinuation(request);
        }
        catch (PotionPolicyUnsatisfiedException)
        {
            request.Context.Policy.Diagnostics.Info(
                $"[CombatSolver/Test] {diagnostic} qualified=false");
            return null;
        }
    }

    internal IEnumerable<ContinuationSearchOutcome> Run(
        IFrontierContinuationSource source,
        ContinuationPurpose purpose,
        int minimumRemainingMilliseconds,
        int maximumNodes,
        int maximumMilliseconds,
        int reserveMilliseconds,
        SolverPotionPolicy? potionPolicyOverride,
        Func<(int? Maximum, int? Minimum)> potionBounds,
        string? optionalPotionDiagnostic = null)
    {
        foreach (PlanAction[] prefix in source.Enumerate())
        {
            SearchBudgetWindow window = context.Budget.RequestWindow(context.Policy.Profile);
            if (!window.CanStart(minimumRemainingMilliseconds))
                yield break;
            if (!source.CanReplay(prefix))
                continue;
            if (source.DeduplicatePrefixes
                && !_seen.Add((purpose, JsonSerializer.Serialize(prefix))))
                continue;
            (int? maximumPotionUses, int? minimumPotionUses) = potionBounds();
            ContinuationSearchRequest request = new(context, purpose, prefix,
                window.Limit(context.Policy.Profile, maximumNodes, maximumMilliseconds,
                    reserveMilliseconds), potionPolicyOverride, maximumPotionUses,
                minimumPotionUses);
            SolverResult? result;
            if (optionalPotionDiagnostic == null)
                result = Dispatch(request);
            else
                result = DispatchOptional(request, optionalPotionDiagnostic);
            if (result == null)
                continue;
            yield return new(request, result);
        }
    }
}
