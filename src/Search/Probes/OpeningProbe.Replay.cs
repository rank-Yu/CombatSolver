using MegaCrit.Sts2.Core.Entities.Cards;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class OpeningProbe
{
    private SearchReplayCore? _replayCore;
    private SearchReplayCore ReplayCore => _replayCore ??= CreateReplayCore();

    private SearchReplayCore CreateReplayCore()
        => new(new SearchReplayOptions(
            root,
            displayNames,
            policy,
            _player,
            _startTurnNumber,
            _forecast,
            Evaluator,
            RoundContext,
            _run.Replay,
            cancellationToken,
            _includeTurnSetup,
            policy.Diagnostics,
            detailedDiagnostics: false))
        {
            Profile = _profile,
        };

    private IReadOnlyList<TurnSetupRootCandidate> BuildTurnSetupRoots()
        => ReplayCore.CaptureTurnSetupRoots();

    private SimulationSnapshot ReplayTurnSetup(IReadOnlyList<PlanCardChoice> choices)
        => ReplayCore.ReplayTurnSetup(choices);

    private SimulationSnapshot Replay(IReadOnlyList<PlanAction> actions,
        SimulationSnapshot? parentSnapshot = null,
        int startingTurn = 0,
        int priorActionCount = 0,
        bool countTransition = true)
        => ReplayCore.Replay(actions, parentSnapshot, startingTurn, priorActionCount,
            countTransition: countTransition);

    private SimulationSnapshot ReplayAction(SearchNode parent, PlanAction action)
        => ReplayCore.ReplayAction(parent, action);

    internal SimulationSnapshot CaptureRootSnapshotForFlash()
        => ReplayCore.CaptureRootSnapshot();

    internal IReadOnlyList<TurnSetupRootCandidate> CaptureTurnSetupRootsForFlash()
    {
        if (!_includeTurnSetup)
            throw new InvalidPlannedChoiceBranchException("只有 IncludeTurnSetup 搜索请求才能枚举回合准备根。");
        if (root.CurrentSide != MegaCrit.Sts2.Core.Combat.CombatSide.Player
            || root.PlayerPhase != MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Start)
        {
            throw new InvalidPlannedChoiceBranchException(
                $"回合准备根不在玩家准备阶段：side={root.CurrentSide} phase={root.PlayerPhase}。");
        }
        return ReplayCore.CaptureTurnSetupRoots();
    }

    internal SimulationSnapshot ReplayTurnSetupForFlash(IReadOnlyList<PlanCardChoice> choices)
        => ReplayCore.ReplayTurnSetupForFlash(choices);

    internal SimulationSnapshot ReplayForFlash(
        IReadOnlyList<PlanAction> actions,
        SimulationSnapshot? parent)
        => ReplayCore.ReplayForFlash(actions, parent);

    internal SimulationSnapshot? ReplayFixedPrefixForFlash(
        SimulationSnapshot rootSnapshot,
        IReadOnlyList<PlanAction> prefix)
        => ReplayCore.ReplayFixedPrefix(rootSnapshot, prefix, ExpansionContext,
            _resetFixedPrefixSchedulingBaseline);

    internal SearchNode? BuildFixedPrefixNodeForFlash(
        SimulationSnapshot rootSnapshot,
        IReadOnlyList<PlanAction> prefix,
        IReadOnlyList<PlanCardChoice>? turnSetupChoices,
        ContinuationStamp? turnSetupPlayState)
        => ReplayCore.ReplayFixedPrefixNode(rootSnapshot, prefix, ExpansionContext,
            turnSetupChoices, turnSetupPlayState, _resetFixedPrefixSchedulingBaseline,
            createUncomparedOutcomes: true);

    internal int ReplayFixedPrefixPlanPayoffForFlash(
        SimulationSnapshot rootSnapshot,
        IReadOnlyList<PlanAction> prefix,
        PlanCommitment plan)
        => ReplayCore.ReplayFixedPrefixPlanPayoff(
            rootSnapshot, prefix, plan, ExpansionContext);

    internal SimulationSnapshot ReplayWithChoicesForFlash(
        ref PlanAction action,
        SimulationSnapshot parent,
        IReadOnlyList<ulong> selections,
        Action<int, TurnStartChoiceRequest, CardChoiceSpec>? inspect = null,
        Action<int, KnowledgeDemonChoiceRequest>? inspectKnowledge = null)
        => ReplayCore.ReplayWithChoicesForFlash(
            ref action, parent, selections, inspect, inspectKnowledge);

    internal (bool[] Allowed, int[] Costs, int? EarliestTurn, int? MaximumUses)
        CapturePotionPolicyForFlash(SimulationSnapshot snapshot)
        => FlashBridgeFacts.CapturePotionPolicy(
            root, _player, _potionStrategy, _potionPolicy, _forceAllPotionsDisabled,
            snapshot, _earliestPotionTurn, _maximumPotionUses);

    internal static FlashPotionUsageForBridge CapturePotionUsageForFlash(SimulationSnapshot snapshot)
        => FlashBridgeFacts.CapturePotionUsage(snapshot);

    /// <summary>The turn-setup solve freezes the four rank baselines at zero.</summary>
    internal void SetFlashRankBaselineForTurnSetup()
    {
        _run.Replay.InitialPersistentBuffValue = 0;
        _run.Replay.InitialEnemyStrengthSuppression = 0;
        _run.Replay.InitialEnemyWeakTurns = 0;
        _run.Replay.InitialRetainedAttackValue = 0;
    }

    internal void SetFlashRankBaseline(SimulationSnapshot rootSnapshot)
    {
        _run.Replay.InitialPersistentBuffValue = rootSnapshot.PersistentBuffValue;
        _run.Replay.InitialEnemyStrengthSuppression = rootSnapshot.EnemyStrengthSuppression;
        _run.Replay.InitialEnemyWeakTurns = rootSnapshot.EnemyWeakTurns;
        _run.Replay.InitialRetainedAttackValue = rootSnapshot.RetainedAttackValue;
    }
}
