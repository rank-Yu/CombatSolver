using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Flash;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.FlashBeam;

/// <summary>
/// Turns a Flash action into the existing engine's <see cref="PlanAction"/> at the same state.
/// Cards are located by hand position in the existing engine and keyed exactly as
/// <c>EnumeratePlannedCardActions</c> keys them. Choices are not included: <see cref="FlashChoiceTranslator"/>
/// appends them while replaying the action through the existing engine.
/// </summary>
internal static class FlashActionTranslator
{
    public static PlanAction ToPlanAction(SimulationSnapshot snapshot, Player player, in FlashAction action)
    {
        var simulator = snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        int turn = snapshot.Turn;
        switch (action.Kind)
        {
            case FlashActionKind.EndTurn:
                return new PlanAction(PlanActionKind.EndTurn, turn);
            case FlashActionKind.PlayCard:
                IReadOnlyList<PredictedCard> hand = simulator.State.GetPlayerCombatState(player).Hand.Cards;
                PredictedCard card = hand[action.HandIndex];
                string cardId = card.Preview.Id.Entry;
                string stateKey = CardChoiceSupport.ChoiceCardKey(card);
                int occurrence = 0;
                int stateOccurrence = 0;
                for (int prior = 0; prior < action.HandIndex; prior++)
                {
                    if (string.Equals(hand[prior].Preview.Id.Entry, cardId, StringComparison.Ordinal))
                        occurrence++;
                    if (string.Equals(CardChoiceSupport.ChoiceCardKey(hand[prior]), stateKey, StringComparison.Ordinal))
                        stateOccurrence++;
                }
                (Creature? target, int targetIndex) = Target(snapshot, action.Target);
                return new PlanAction(
                    PlanActionKind.PlayCard,
                    turn,
                    cardId,
                    occurrence,
                    targetIndex,
                    target?.CombatId,
                    ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                    CardStateKey: stateKey,
                    CardStateOccurrence: stateOccurrence,
                    CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? "",
                    CardUpgradeLevel: card.Preview.CurrentUpgradeLevel);
            case FlashActionKind.UsePotion:
            {
                // EnumeratePlannedPotionActions keys a potion by slot, id and target.
                var potion = combat.GetPotionAtSlot(player, action.PotionSlot)
                    ?? throw new InvalidOperationException($"药水槽位 {action.PotionSlot} 在旧引擎中为空。");
                (Creature? potionTarget, int potionTargetIndex) = Target(snapshot, action.Target);
                return new PlanAction(
                    PlanActionKind.UsePotion,
                    turn,
                    TargetIndex: potionTargetIndex,
                    TargetCombatId: potionTarget?.CombatId,
                    PotionSlot: action.PotionSlot,
                    PotionId: potion.Id.Entry);
            }
            default:
                throw new FlashUnsupportedException($"动作 {action.Kind} 的翻译尚未实现。");
        }
    }

    /// <summary>The enemy behind a Flash owner and its index in the existing engine's enemy list (-1 for none).</summary>
    private static (Creature? Target, int Index) Target(SimulationSnapshot snapshot, byte owner)
    {
        if (owner == FlashLimits.NoOwner)
            return (null, -1);
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        Creature target = combat.KnownEnemies[owner - FlashLimits.FirstEnemyOwner];
        IReadOnlyList<Creature> enemies = snapshot.Simulator.State.Enemies;
        for (int index = 0; index < enemies.Count; index++)
        {
            if (ReferenceEquals(enemies[index], target))
                return (target, index);
        }
        return (target, -1);
    }
}
