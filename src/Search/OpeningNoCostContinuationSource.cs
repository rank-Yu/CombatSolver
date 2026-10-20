namespace CombatSolver;

internal sealed class OpeningNoCostContinuationSource : IFrontierContinuationSource
{
    private readonly OpeningProbe _builder;

    public bool DeduplicatePrefixes => false;

    internal OpeningNoCostContinuationSource(SearchPassContext context)
    {
        _builder = new OpeningProbe(context.Root, context.DisplayNames,
            context.BattleDamage, context.Policy, context.CancellationToken,
            searchProfile: context.Policy.Profile,
            potionPolicyOverride: SolverPotionPolicy.Disabled,
            maximumPotionUses: 0);
    }

    public IEnumerable<PlanAction[]> Enumerate()
        => _builder.BuildOpeningNoCostPrefixes();

    public bool CanReplay(PlanAction[] prefix) => true;
}
