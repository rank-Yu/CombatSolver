namespace CombatSolver.Engine.Common;

internal sealed partial class PredictionStateStore
{
    /// <summary>Entry types currently stored, for the Flash bridge's representability check.</summary>
    internal IEnumerable<string> DescribeEntryTypesForFlash()
        => _states == null
            ? []
            : _states.Keys.Select(key => $"{key.StateType.Name}@{key.Model.Id.Entry}").Distinct();
}
