using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed class SearchExpansionContext(
    Player player,
    SolverDisplayNames displayNames,
    SearchEvaluator evaluator,
    int potionSlotCount,
    int? maximumPotionUses,
    int? earliestPotionTurn,
    PotionStrategySnapshot potionStrategy,
    SolverPotionPolicy potionPolicy,
    bool forceAllPotionsDisabled,
    Action? onDuplicateCard,
    Func<SearchNode, SearchNode>? decorateNode)
{
    public Player Player { get; } = player;
    public SolverDisplayNames DisplayNames { get; } = displayNames;
    public SearchEvaluator Evaluator { get; } = evaluator;
    public int PotionSlotCount { get; } = potionSlotCount;
    public int? MaximumPotionUses { get; } = maximumPotionUses;
    public int? EarliestPotionTurn { get; } = earliestPotionTurn;
    public PotionStrategySnapshot PotionStrategy { get; } = potionStrategy;
    public SolverPotionPolicy PotionPolicy { get; } = potionPolicy;
    public bool ForceAllPotionsDisabled { get; } = forceAllPotionsDisabled;
    public Action? OnDuplicateCard { get; } = onDuplicateCard;
    public Func<SearchNode, SearchNode>? DecorateNode { get; } = decorateNode;
}

internal static class SearchExpansionShared
{
    internal static IEnumerable<PreparedCardAction> EnumeratePlannedCardActions(
        SearchExpansionContext context,
        ExpansionPlan plan)
    {
        SearchNode node = plan.Parent;
        SimulationSnapshot snapshot = node.Snapshot;
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead)
            yield break;

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(context.Player);
        IReadOnlyList<PredictedCard> hand = playerState.Hand.Cards;
        HandFingerprintBuffer seenCards = default;
        int seenCardCount = 0;
        HashSet<StateFingerprint>? overflowCards = null;
        for (int handIndex = 0; handIndex < hand.Count; handIndex++)
        {
            PredictedCard card = hand[handIndex];
            string cardId = card.Preview.Id.Entry;
            int occurrence = 0;
            for (int priorIndex = 0; priorIndex < handIndex; priorIndex++)
            {
                if (string.Equals(hand[priorIndex].Preview.Id.Entry, cardId, StringComparison.Ordinal))
                    occurrence++;
            }
            if (!simulatedCombat.CanPlayCard(simulator, card))
                continue;
            StateFingerprint playableKey = context.Evaluator.BuildPlayableCardKey(card);
            bool duplicate = false;
            for (int seenIndex = 0; seenIndex < seenCardCount; seenIndex++)
            {
                if (seenCards[seenIndex] == playableKey)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate && overflowCards != null)
                duplicate = !overflowCards.Add(playableKey);
            else if (!duplicate)
            {
                if (seenCardCount < 16)
                    seenCards[seenCardCount++] = playableKey;
                else
                {
                    overflowCards = [];
                    for (int seenIndex = 0; seenIndex < seenCardCount; seenIndex++)
                        overflowCards.Add(seenCards[seenIndex]);
                    duplicate = !overflowCards.Add(playableKey);
                }
            }
            if (duplicate)
            {
                context.OnDuplicateCard?.Invoke();
                continue;
            }
            string cardStateKey = CardChoiceSupport.ChoiceCardKey(card);
            bool requiresUnsupportedExistingChoice =
                CardChoiceSupport.RequiresUnsupportedExistingChoice(card.Preview);
            PlanCardChoice? requiredEmptyChoice =
                CardChoiceSupport.BuildRequiredEmptyChoice(card.Preview);
            int cardStateOccurrence = 0;
            for (int priorIndex = 0; priorIndex < handIndex; priorIndex++)
            {
                if (string.Equals(
                        CardChoiceSupport.ChoiceCardKey(hand[priorIndex]),
                        cardStateKey,
                        StringComparison.Ordinal))
                {
                    cardStateOccurrence++;
                }
            }
            foreach ((int targetIndex, Creature? target) in
                     SearchProbeHelpers.TargetsFor(card, simulator))
            {
                if (node.ActionCount == 0 && !card.Original.CanPlayTargeting(target))
                    continue;
                string cardTitle;
                string targetName;
                if (plan.CardNameFirst)
                {
                    cardTitle = context.DisplayNames.Card(card.Preview);
                    targetName = context.DisplayNames.Creature(target, simulatedCombat.KnownEnemies);
                }
                else
                {
                    targetName = context.DisplayNames.Creature(target, simulatedCombat.KnownEnemies);
                    cardTitle = context.DisplayNames.Card(card.Preview);
                }
                PlanAction action = new(
                    PlanActionKind.PlayCard,
                    node.Turn,
                    card.Preview.Id.Entry,
                    occurrence,
                    targetIndex,
                    target?.CombatId,
                    cardTitle,
                    targetName,
                    ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                    CardStateKey: cardStateKey,
                    CardStateOccurrence: cardStateOccurrence,
                    CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? "",
                    CardUpgradeLevel: card.Preview.CurrentUpgradeLevel);
                yield return new PreparedCardAction(
                    action,
                    card.Preview.Type,
                    target?.CombatId,
                    requiresUnsupportedExistingChoice,
                    requiredEmptyChoice);
            }
        }
    }

    internal static IEnumerable<PreparedPotionAction> EnumeratePlannedPotionActions(
        SearchExpansionContext context,
        ExpansionPlan plan)
    {
        SearchNode node = plan.Parent;
        SimulationSnapshot snapshot = node.Snapshot;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead
            || context.EarliestPotionTurn is { } earliestTurn && node.Turn < earliestTurn
            || context.MaximumPotionUses != null
                && SearchProbeHelpers.ExplicitPotionUseCount(node) >= context.MaximumPotionUses.Value)
        {
            yield break;
        }

        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        for (int potionSlot = 0; potionSlot < context.PotionSlotCount; potionSlot++)
        {
            PotionModel? potion = simulatedCombat.GetPotionAtSlot(context.Player, potionSlot);
            if (potion == null
                || !simulatedCombat.IsPotionAvailable(context.Player, potionSlot)
                || !PotionOnUseSupport.CanSearch(potion)
                || !SearchProbeHelpers.AllowsPotionUse(
                    context.PotionStrategy, context.PotionPolicy,
                    context.ForceAllPotionsDisabled, potionSlot, potion.Id.Entry))
            {
                continue;
            }

            foreach ((int targetIndex, Creature? target) in
                     SearchProbeHelpers.TargetsForPotion(context.Player, potion, simulator))
            {
                PlanAction baseAction = new(
                    PlanActionKind.UsePotion,
                    node.Turn,
                    TargetIndex: targetIndex,
                    TargetCombatId: target?.CombatId,
                    TargetName: context.DisplayNames.Creature(target, simulatedCombat.KnownEnemies),
                    PotionSlot: potionSlot,
                    PotionId: potion.Id.Entry,
                    PotionTitle: context.DisplayNames.Potion(potion));
                yield return new PreparedPotionAction(baseAction, potion);
            }
        }
    }

    internal static SearchNode CreatePlannedCardChild(
        SearchExpansionContext context,
        SearchNode node,
        PlanAction finalAction,
        SimulationSnapshot finalSnapshot)
    {
        bool forcedTurnEnd = finalSnapshot.Turn > node.Turn;
        PlanAction nodeAction = finalAction with { EndsPlayerTurn = forcedTurnEnd };
        bool terminal = finalSnapshot.PlayerDead
            || finalSnapshot.AllEnemiesDead
            || finalSnapshot.BoundaryReason != SearchBoundaryReason.None;
        SearchNode child = new(
            nodeAction,
            node.ActionCount + 1,
            finalSnapshot.PotionUseCount,
            finalSnapshot.PotionStrategicCost,
            forcedTurnEnd ? node.Turn + 1 : node.Turn,
            node.Traits,
            node.FutureSoldHp,
            SearchProbeHelpers.ApplySoldHpPenalty(finalSnapshot.Score, node.FutureSoldHp),
            finalSnapshot.StateKey,
            finalSnapshot.HasRisk,
            finalSnapshot.BoundaryReason,
            terminal,
            node,
            finalSnapshot,
            forcedTurnEnd
                ? node.CombatProgress.Advance(finalSnapshot)
                : node.CombatProgress)
        {
            CumulativeEnemyHpLost = SearchProbeHelpers.AccumulateEnemyHpLost(node, finalSnapshot),
        };
        return context.DecorateNode?.Invoke(child) ?? child;
    }

    internal static SearchNode CreatePlannedPotionChild(
        SearchExpansionContext context,
        SearchNode node,
        PlanAction finalAction,
        SimulationSnapshot finalSnapshot)
    {
        bool terminal = finalSnapshot.PlayerDead
            || finalSnapshot.AllEnemiesDead
            || finalSnapshot.BoundaryReason != SearchBoundaryReason.None;
        SearchNode child = new(
            finalAction,
            node.ActionCount + 1,
            finalSnapshot.PotionUseCount,
            finalSnapshot.PotionStrategicCost,
            node.Turn,
            SearchProbeHelpers.ClassifyPotionTraits(node.Traits, node.Snapshot, finalSnapshot),
            node.FutureSoldHp,
            SearchProbeHelpers.ApplySoldHpPenalty(finalSnapshot.Score, node.FutureSoldHp),
            finalSnapshot.StateKey,
            finalSnapshot.HasRisk,
            finalSnapshot.BoundaryReason,
            terminal,
            node,
            finalSnapshot,
            node.CombatProgress)
        {
            CumulativeEnemyHpLost = SearchProbeHelpers.AccumulateEnemyHpLost(node, finalSnapshot),
        };
        return context.DecorateNode?.Invoke(child) ?? child;
    }

[InlineArray(16)]
internal struct HandFingerprintBuffer
{
    private StateFingerprint _element0;
}
}

internal readonly record struct ExpansionPlan(SearchNode Parent, bool CardNameFirst);

internal readonly record struct PreparedCardAction(
    PlanAction Action,
    CardType CardType,
    uint? TargetCombatId,
    bool RequiresUnsupportedExistingChoice,
    PlanCardChoice? RequiredEmptyChoice);

internal readonly record struct PreparedPotionAction(PlanAction Action, PotionModel Potion);
