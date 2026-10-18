namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    /// <summary>
    /// Replays <paramref name="actions"/> through the existing engine from the root or from
    /// <paramref name="parent"/>. The Flash bridge uses this as the authoritative step for
    /// differential checks and route materialization.
    /// </summary>
    internal SimulationSnapshot ReplayForFlash(IReadOnlyList<PlanAction> actions, SimulationSnapshot? parent)
        => Replay(actions, parent, parent?.Turn ?? 0, countTransition: false, allowExecutionCapture: false);
}
