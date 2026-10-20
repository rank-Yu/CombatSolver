using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

/// <summary>One turn-setup search root: the choices answering the native setup page, and the play state.</summary>
internal sealed record TurnSetupRootCandidate(
    IReadOnlyList<PlanCardChoice> Choices,
    SimulationSnapshot Snapshot);

internal sealed partial class CombatBeamSolver
{
    /// <summary>
    /// Enumerates the turn-setup roots with the same choice budget the existing search uses
    /// (<see cref="BuildTurnSetupRoots"/>). The returned snapshots are owned by the caller and must be
    /// released. Only valid on a captured player/Start root with <c>IncludeTurnSetup</c>.
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
        IReadOnlyList<(IReadOnlyList<PlanCardChoice> Choices, SimulationSnapshot Snapshot)> roots =
            BuildTurnSetupRoots();
        List<TurnSetupRootCandidate> candidates = new(roots.Count);
        foreach ((IReadOnlyList<PlanCardChoice> choices, SimulationSnapshot snapshot) in roots)
            candidates.Add(new TurnSetupRootCandidate(choices, snapshot));
        return candidates;
    }

    /// <summary>
    /// A snapshot of the captured root itself (the pre-setup player/Start state). The caller owns it.
    /// The rules/scorer bridge uses it as the frozen request root for a turn-setup search.
    /// </summary>
    internal SimulationSnapshot CaptureRootSnapshotForFlash()
    {
        _run.ReplayCount++;
        CombatPredictionSimulator simulator = root.ForkSimulator();
        ForkableSet<uint> processedEnemyDeaths = [];
        foreach (MegaCrit.Sts2.Core.Entities.Creatures.Creature enemy in root.Enemies)
        {
            if (enemy.CombatId is uint combatId && simulator.State.GetCreature(enemy).IsDead)
                processedEnemyDeaths.Add(combatId);
        }
        return Snapshot(
            simulator,
            _startTurnNumber,
            actionCount: 0,
            shufflesCrossed: 0,
            SearchBoundaryReason.None,
            processedEnemyDeaths);
    }

    /// <summary>
    /// Replays one already-enumerated turn-setup choice vector to its play-phase root. The caller owns
    /// the returned snapshot. A setup vector that still leaves a pending choice, or stops at another
    /// boundary, is a protocol failure rather than a search boundary.
    /// </summary>
    internal SimulationSnapshot ReplayTurnSetupForFlash(IReadOnlyList<PlanCardChoice> choices)
    {
        SimulationSnapshot snapshot = ReplayTurnSetup(choices);
        if (snapshot.BoundaryReason != SearchBoundaryReason.None)
        {
            SearchBoundaryReason boundary = snapshot.BoundaryReason;
            snapshot.ReleaseSimulator();
            throw new InvalidPlannedChoiceBranchException(
                $"回合准备选择回放没有到达稳定出牌阶段：boundary={boundary}。");
        }
        return snapshot;
    }

    /// <summary>
    /// Structural equality of two turn-setup choice vectors. <see cref="PlanCardChoice"/> holds a card
    /// list, so record equality would compare list references.
    /// </summary>
    internal static bool SameTurnSetupChoices(
        IReadOnlyList<PlanCardChoice> left,
        IReadOnlyList<PlanCardChoice> right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            PlanCardChoice a = left[index];
            PlanCardChoice b = right[index];
            if (a.Effect != b.Effect
                || a.SourcePile != b.SourcePile
                || a.Timing != b.Timing
                || !string.Equals(a.SourceId, b.SourceId, StringComparison.Ordinal)
                || !string.Equals(a.ContextId, b.ContextId, StringComparison.Ordinal)
                || a.Cards.Count != b.Cards.Count)
            {
                return false;
            }
            for (int card = 0; card < a.Cards.Count; card++)
            {
                PlanCardToken x = a.Cards[card];
                PlanCardToken y = b.Cards[card];
                if (!string.Equals(x.CardId, y.CardId, StringComparison.Ordinal)
                    || x.UpgradeLevel != y.UpgradeLevel
                    || !string.Equals(x.StateKey, y.StateKey, StringComparison.Ordinal)
                    || x.SourceOccurrence != y.SourceOccurrence
                    || x.OptionOccurrence != y.OptionOccurrence)
                {
                    return false;
                }
            }
        }
        return true;
    }
}
