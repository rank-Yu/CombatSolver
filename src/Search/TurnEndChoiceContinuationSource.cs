namespace CombatSolver;

internal sealed class TurnEndChoiceContinuationSource : IFrontierContinuationSource
{
    private readonly OpeningProbe _builder;
    private readonly PlanAction[] _beforeEndTurn;
    private readonly string _chosenKey;

    public bool DeduplicatePrefixes => false;

    internal TurnEndChoiceContinuationSource(
        OpeningProbe builder,
        PlanAction[] beforeEndTurn,
        string chosenKey)
    {
        _builder = builder;
        _beforeEndTurn = beforeEndTurn;
        _chosenKey = chosenKey;
    }

    public IEnumerable<PlanAction[]> Enumerate()
        => _builder.BuildTurnEndChoiceActionsAfterPrefix(_beforeEndTurn)
            .Where(action => OpeningProbe.TurnEndChoiceKey(action) != _chosenKey)
            .Take(2)
            .Select(action => _beforeEndTurn.Append(action).ToArray());

    public bool CanReplay(PlanAction[] prefix) => true;
}
