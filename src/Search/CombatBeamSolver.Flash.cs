using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>A Flash action's choices do not line up with the choices the existing engine asks for.</summary>
internal sealed class FlashChoiceMismatchException(string message) : InvalidOperationException(message);

internal sealed partial class CombatBeamSolver
{
    /// <summary>
    /// Replays <paramref name="actions"/> through the existing engine from the root or from
    /// <paramref name="parent"/>. The Flash bridge uses this as the authoritative step for
    /// differential checks and route materialization.
    /// </summary>
    internal SimulationSnapshot ReplayForFlash(IReadOnlyList<PlanAction> actions, SimulationSnapshot? parent)
        => Replay(actions, parent, parent?.Turn ?? 0, countTransition: false, allowExecutionCapture: false);

    /// <summary>
    /// The existing search's potion constraints at <paramref name="root"/>, as plain values for the Flash
    /// search: per slot whether an explicit use is offered (<see cref="PotionOnUseSupport.CanSearch"/> and
    /// <see cref="AllowsPotionUse"/>) and its strategic HP cost, the earliest search turn
    /// (<c>node.Turn</c> units) and the maximum number of explicit uses.
    /// </summary>
    internal (bool[] Allowed, int[] Costs, int? EarliestTurn, int? MaximumUses) CapturePotionPolicyForFlash(SimulationSnapshot snapshot)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        bool[] allowed = new bool[root.PotionSlotCount];
        int[] costs = new int[root.PotionSlotCount];
        for (int slot = 0; slot < allowed.Length; slot++)
        {
            MegaCrit.Sts2.Core.Models.PotionModel? potion = combat.GetPotionAtSlot(_player, slot);
            if (potion == null)
                continue;
            allowed[slot] = combat.IsPotionAvailable(_player, slot)
                && PotionOnUseSupport.CanSearch(potion)
                && AllowsPotionUse(slot, potion.Id.Entry);
            bool renewable = potion is MegaCrit.Sts2.Core.Models.Potions.PotionShapedRock
                && combat.RelicsOf(_player).OfType<MegaCrit.Sts2.Core.Models.Relics.PetrifiedToad>().Any(static relic => !relic.IsMelted);
            costs[slot] = combat.IsFreeEntropicPotionAtSlot(_player, slot) ? 0 : PotionUsePolicy.StrategicHpCost(potion, renewable);
        }
        return (allowed, costs, _earliestPotionTurn, _maximumPotionUses);
    }

    /// <summary>
    /// Replays one action from <paramref name="parent"/>, answering each card choice the existing
    /// engine asks for with the next of <paramref name="selections"/> (bit sets over the request's
    /// spec options, in option order). Each answer is placed on the action exactly as the existing
    /// search places a resolved branch (<see cref="AppendPendingChoice"/>), with the first
    /// source-less request of a card play as its primary choice. <paramref name="inspect"/> sees each
    /// request before it is answered. Returns the snapshot after the last answer.
    /// </summary>
    internal SimulationSnapshot ReplayWithChoicesForFlash(
        ref PlanAction action,
        SimulationSnapshot parent,
        IReadOnlyList<ulong> selections,
        Action<int, TurnStartChoiceRequest, CardChoiceSpec>? inspect = null)
    {
        PrimaryChoiceMatch? primary = null;
        int taken = 0;
        while (true)
        {
            SimulationSnapshot child = ReplayForFlash([action], parent);
            if (child.BoundaryReason != SearchBoundaryReason.PendingChoice)
            {
                if (taken != selections.Count)
                    throw new FlashChoiceMismatchException($"Flash 做了 {selections.Count} 次选择，旧引擎只请求了 {taken} 次。");
                return child;
            }
            SimulatedCombatState combat = (SimulatedCombatState)child.Simulator.State.CombatState;
            TurnStartChoiceRequest request = combat.PendingTurnStartChoice
                ?? throw new FlashChoiceMismatchException("旧引擎的待定选择不是卡牌选择。");
            CardChoiceSpec spec = TurnStartChoiceSupport.BuildPendingSpec(
                (CombatPredictionSimulator)child.Simulator, combat, _player);
            if (taken == 0)
            {
                primary = action.Kind == PlanActionKind.PlayCard && string.IsNullOrEmpty(request.SourceId)
                    ? BuildPrimaryChoiceMatch(spec)
                    : null;
            }
            if (taken >= selections.Count)
            {
                throw new FlashChoiceMismatchException(
                    $"旧引擎请求第 {taken + 1} 次选择（{request.SourceId}/{request.Effect}/{request.SourcePile}），Flash 没有做。");
            }
            inspect?.Invoke(taken, request, spec);
            ulong bits = selections[taken++];
            if (spec.Options.Count < 64 && bits >> spec.Options.Count != 0)
                throw new FlashChoiceMismatchException($"选择 {bits:X} 超出旧引擎的 {spec.Options.Count} 个选项。");
            List<int> indexes = [];
            for (int index = 0; index < spec.Options.Count; index++)
            {
                if ((bits & (1UL << index)) != 0)
                    indexes.Add(index);
            }
            PlanCardChoice branch = CardChoiceSupport.BuildSelectedChoiceForFlash(spec, indexes, displayNames);
            bool turnResolution = action.Kind == PlanActionKind.EndTurn
                || child.Turn > parent.Turn
                || request.Timing is PlanChoiceTiming.PlayerTurnEnd
                    or PlanChoiceTiming.EnemyTurn or PlanChoiceTiming.PlayerTurnStart;
            action = AppendPendingChoice(action, request, branch, turnResolution, primary);
            child.ReleaseSimulator();
        }
    }
}
