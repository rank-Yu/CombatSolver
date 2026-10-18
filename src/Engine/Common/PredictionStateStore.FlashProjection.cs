using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.Common;

internal sealed partial class PredictionStateStore
{
    /// <summary>Every stored entry (model keys are already resolved), for the Flash bridge's state projection.</summary>
    internal IEnumerable<(AbstractModel Model, object State)> EntriesForFlash()
        => _states == null
            ? []
            : _states.Select(entry => (entry.Key.Model, entry.Value));
}
