using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>One turn-setup search root: the choices answering the native setup page, and the play state.</summary>
internal sealed record TurnSetupRootCandidate(
    IReadOnlyList<PlanCardChoice> Choices,
    SimulationSnapshot Snapshot);

internal sealed partial class CombatBeamSolver
{
    /// <summary>
    /// Enumerates the turn-setup roots with the shared choice budget. The returned snapshots are
    /// owned by the caller and must be released. Only valid on a captured player/Start root with
    /// <c>IncludeTurnSetup</c>.
    /// </summary>
    internal IReadOnlyList<TurnSetupRootCandidate> CaptureTurnSetupRootsForFlash()
    {
        if (!_includeTurnSetup)
            throw new InvalidPlannedChoiceBranchException("只有 IncludeTurnSetup 搜索请求才能枚举回合准备根。");
        if (root.CurrentSide != CombatSide.Player || root.PlayerPhase != PlayerTurnPhase.Start)
        {
            throw new InvalidPlannedChoiceBranchException(
                $"回合准备根不在玩家准备阶段：side={root.CurrentSide} phase={root.PlayerPhase}。");
        }
        return ReplayCore.CaptureTurnSetupRoots();
    }

    /// <summary>
    /// A snapshot of the captured root itself (the pre-setup player/Start state). The caller owns it.
    /// </summary>
    internal SimulationSnapshot CaptureRootSnapshotForFlash() => ReplayCore.CaptureRootSnapshot();

    /// <summary>
    /// Replays one already-enumerated turn-setup choice vector to its play-phase root. The caller owns
    /// the returned snapshot. A setup vector that still leaves a pending choice, or stops at another
    /// boundary, is a protocol failure rather than a search boundary.
    /// </summary>
    internal SimulationSnapshot ReplayTurnSetupForFlash(IReadOnlyList<PlanCardChoice> choices)
        => ReplayCore.ReplayTurnSetupForFlash(choices);

    internal static bool SameTurnSetupChoices(
        IReadOnlyList<PlanCardChoice> left,
        IReadOnlyList<PlanCardChoice> right)
        => FlashBridgeFacts.SameTurnSetupChoices(left, right);
}
