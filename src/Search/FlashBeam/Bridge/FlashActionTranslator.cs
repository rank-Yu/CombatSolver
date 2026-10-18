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
                Creature? target = action.Target == FlashLimits.NoOwner
                    ? null
                    : combat.KnownEnemies[action.Target - FlashLimits.FirstEnemyOwner];
                int targetIndex = -1;
                if (target != null)
                {
                    IReadOnlyList<Creature> enemies = simulator.State.Enemies;
                    for (int index = 0; index < enemies.Count; index++)
                    {
                        if (ReferenceEquals(enemies[index], target))
                            targetIndex = index;
                    }
                }
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
            default:
                throw new FlashUnsupportedException($"动作 {action.Kind} 的翻译尚未实现。");
        }
    }
}
