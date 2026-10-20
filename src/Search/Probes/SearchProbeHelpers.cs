using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// Pure helpers shared by the Beam search and the opening probes. No method here owns a search
/// frontier or a solver instance; callers pass the player/policy facts explicitly.
/// </summary>
internal static class SearchProbeHelpers
{
    internal static int AccumulateEnemyHpLost(
        SearchNode parent,
        SimulationSnapshot childSnapshot)
        => checked(parent.CumulativeEnemyHpLost
            + Math.Max(0, parent.Snapshot.RawEnemyHp - childSnapshot.RawEnemyHp));

    internal static double ApplySoldHpPenalty(double score, int futureSoldHp)
        => score + futureSoldHp * SolverWeights.SoldHpPenalty;

    internal static int ExplicitPotionUseCount(SearchNode node)
        => PotionUsePolicy.ExplicitUseCount(
            node.PotionCount,
            node.Snapshot.AutomaticPotionUseCount);

    internal static bool AllowsPotionUse(
        PotionStrategySnapshot strategy,
        SolverPotionPolicy policy,
        bool forceAllPotionsDisabled,
        int slot,
        string potionId)
        => strategy.AllowsExplicitUse(slot, potionId, policy, forceAllPotionsDisabled);

    internal static ActionCandidate BuildCandidate(
        Player player,
        SimulationSnapshot before,
        SimulationSnapshot after,
        SearchNode node,
        CardType cardType,
        uint? targetCombatId)
    {
        int energy = Math.Max(0, before.Energy - after.Energy);
        int stars = Math.Max(0, before.Stars - after.Stars);
        int damage = Math.Max(0, before.EnemyHp - after.EnemyHp);
        int block = Math.Max(0, after.PlayerBlock - before.PlayerBlock);
        double resource = Math.Max(0.5d, energy + stars * 0.5d);
        double normalized = (damage + block * 0.8d) / resource;
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)after.Simulator;
        bool pure = true;
        foreach (CombatPredictionHistoryEntry entry in
                 simulator.History.EntriesFrom(before.HistoryEntryCount))
        {
            if (IsPureHistoryEntry(entry))
                continue;
            pure = false;
            break;
        }
        SimulatedCombatState beforeCombat = (SimulatedCombatState)
            ((CombatPredictionSimulator)before.Simulator).State.CombatState;
        bool declinedExtraTurn = beforeCombat.RelicsOf(player)
            .OfType<PaelsEye>()
            .Any(relic => !relic.IsMelted && beforeCombat.IsPaelsEyeUnused(relic));
        SearchRouteTraits traits = ClassifyCardTraits(
            node.Traits,
            before,
            after,
            pure,
            declinedExtraTurn);
        ActionOptionFamily optionFamilies = ClassifyActionOptionFamilies(
            cardType,
            targetCombatId,
            before,
            after,
            damage,
            block,
            pure);
        return new ActionCandidate(
            node with { Traits = traits },
            cardType,
            targetCombatId,
            energy,
            stars,
            damage,
            block,
            after.PlayerHp,
            after.PlayerMaxHp,
            after.CumulativePlayerHpLost,
            after.LongTermResourceValue,
            after.AngerCopiesGenerated,
            optionFamilies,
            pure,
            normalized);
    }

    internal static bool IsStolenResourceRecoveryTarget(ActionCandidate candidate)
    {
        if (candidate.TargetCombatId is not uint targetCombatId)
            return false;

        SearchNode parent = candidate.Node.Parent
            ?? throw new InvalidOperationException("资源追回动作缺少父节点。");
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)parent.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature? target = combat.Enemies.FirstOrDefault(enemy => enemy.CombatId == targetCombatId);
        return target != null
            && combat.ContainsCreature(target)
            && simulator.State.GetCreature(target).IsAlive
            && combat.EffectivePowers().Any(power =>
                power.Owner == target
                && (power is HeistPower { Amount: > 0 }
                    || power is SwipePower { StolenCard: not null }));
    }

    internal static ActionOptionFamily ClassifyActionOptionFamilies(
        CardType cardType,
        uint? targetCombatId,
        SimulationSnapshot before,
        SimulationSnapshot after,
        int damage,
        int block,
        bool pure)
    {
        ActionOptionFamily families = ActionOptionFamily.None;
        if (block > 0
            || after.ProjectedPlayerHp > before.ProjectedPlayerHp
            || after.PlayerHp > before.PlayerHp
            || after.StrategicEffects.PreventionPotential
                > before.StrategicEffects.PreventionPotential)
        {
            families |= ActionOptionFamily.ImmediateDefense;
        }
        if (damage > 0
            || after.StrategicEffects.DamagePotential > before.StrategicEffects.DamagePotential)
            families |= ActionOptionFamily.ImmediateOffense;
        if (after.Energy > before.Energy
            || after.Stars > before.Stars
            || after.HandCount >= before.HandCount
            || after.ReachableHandValue > before.ReachableHandValue
            || after.ZeroCostPlayableCount > before.ZeroCostPlayableCount
            || after.FutureResourceValue > before.FutureResourceValue
            || after.StrategicEffects.ResourcePotential > before.StrategicEffects.ResourcePotential
            || after.StrategicEffects.CardAccessPotential > before.StrategicEffects.CardAccessPotential
            || after.LiveDeckClutter < before.LiveDeckClutter)
        {
            families |= ActionOptionFamily.ResourceAndCycle;
        }
        if (cardType == CardType.Power
            || after.PersistentBuffValue > before.PersistentBuffValue
            || after.DelayedDamageValue > before.DelayedDamageValue
            || after.ReplayPotentialValue > before.ReplayPotentialValue
            || after.ReactiveDamageValue > before.ReactiveDamageValue
            || after.StrategicEffects.RetentionValue > before.StrategicEffects.RetentionValue
            || after.LongTermResourceValue > before.LongTermResourceValue)
        {
            families |= ActionOptionFamily.PersistentSetup;
        }
        if (after.SandpitRemaining > before.SandpitRemaining
            || after.EnemyStrengthSuppression > before.EnemyStrengthSuppression
            || after.EnemyWeakTurns > before.EnemyWeakTurns
            || after.EnemyVulnerableTurns > before.EnemyVulnerableTurns
            || after.LiveDeckClutter < before.LiveDeckClutter
            || !pure && damage == 0 && block == 0)
        {
            families |= ActionOptionFamily.Control;
        }
        if (targetCombatId.HasValue
            && (after.AliveEnemyCount < before.AliveEnemyCount
                || after.FocusTargetCurrentThreat < before.FocusTargetCurrentThreat))
        {
            families |= ActionOptionFamily.TargetRemoval;
        }
        if (after.PlayerHp < before.PlayerHp
            || after.PlayerMaxHp < before.PlayerMaxHp
            || after.CumulativePlayerHpLost > before.CumulativePlayerHpLost)
        {
            families |= ActionOptionFamily.HpInvestment;
        }
        return families;
    }

    internal static SearchRouteTraits ClassifyCardTraits(
        SearchRouteTraits current,
        SimulationSnapshot before,
        SimulationSnapshot after,
        bool pure,
        bool declinedExtraTurn)
    {
        SearchRouteTraits traits = current;
        if (declinedExtraTurn)
            traits |= SearchRouteTraits.DeclinedExtraTurn;
        if (after.PersistentBuffValue > before.PersistentBuffValue
            || after.DelayedDamageValue > before.DelayedDamageValue
            || after.ReplayPotentialValue > before.ReplayPotentialValue
            || after.LongTermResourceValue > before.LongTermResourceValue)
        {
            traits |= SearchRouteTraits.Scaling;
        }
        if (after.LongTermResourceValue > before.LongTermResourceValue)
            traits |= SearchRouteTraits.LongTermResource;
        if (after.PlayerHp < before.PlayerHp
            || after.PlayerMaxHp < before.PlayerMaxHp
            || after.CumulativePlayerHpLost > before.CumulativePlayerHpLost)
        {
            traits |= SearchRouteTraits.HpInvestment;
        }
        if (after.ReactiveDamageValue > before.ReactiveDamageValue)
            traits |= SearchRouteTraits.ReactiveDamage;
        if (after.Energy > before.Energy
            || after.Stars > before.Stars
            || after.HandCount >= before.HandCount
            || after.ReachableHandValue > before.ReachableHandValue
            || after.ZeroCostPlayableCount > before.ZeroCostPlayableCount
            || after.FutureResourceValue > before.FutureResourceValue)
        {
            traits |= SearchRouteTraits.Resource;
        }
        if (after.SandpitRemaining > before.SandpitRemaining
            || after.EnemyStrengthSuppression > before.EnemyStrengthSuppression
            || after.EnemyWeakTurns > before.EnemyWeakTurns
            || after.EnemyVulnerableTurns > before.EnemyVulnerableTurns
            || after.LiveDeckClutter < before.LiveDeckClutter
            || after.DelayedDamageValue > before.DelayedDamageValue
            || !pure)
        {
            traits |= SearchRouteTraits.Control;
        }
        if (OpensRevivalWindow(before, after))
            traits |= SearchRouteTraits.RevivalWindow;
        return traits;
    }

    internal static SearchRouteTraits ClassifyPotionTraits(
        SearchRouteTraits current,
        SimulationSnapshot before,
        SimulationSnapshot after)
    {
        SearchRouteTraits traits = current;
        if (after.PersistentBuffValue > before.PersistentBuffValue
            || after.DelayedDamageValue > before.DelayedDamageValue)
        {
            traits |= SearchRouteTraits.Scaling;
        }
        if (after.Energy > before.Energy
            || after.Stars > before.Stars
            || after.HandCount > before.HandCount
            || after.FutureResourceValue > before.FutureResourceValue)
        {
            traits |= SearchRouteTraits.Resource;
        }
        if (after.SandpitRemaining > before.SandpitRemaining
            || after.EnemyStrengthSuppression > before.EnemyStrengthSuppression
            || after.EnemyWeakTurns > before.EnemyWeakTurns
            || after.LiveDeckClutter < before.LiveDeckClutter
            || after.DelayedDamageValue > before.DelayedDamageValue
            || after.EnemyHp == before.EnemyHp && after.PlayerBlock == before.PlayerBlock)
        {
            traits |= SearchRouteTraits.Control;
        }
        if (before.Energy == 0
            && after.HandCount == 0
            && before.LiveDeckSize - after.LiveDeckSize >= 6
            && before.PocketwatchCardThreshold >= 0
            && before.PocketwatchCardsPlayedThisTurn == before.PocketwatchCardThreshold)
        {
            traits |= SearchRouteTraits.EndTurnDeckCompression;
        }
        if (OpensRevivalWindow(before, after))
            traits |= SearchRouteTraits.RevivalWindow;
        return traits;
    }

    internal static SearchRouteTraits ClassifyRoundTransitionTraits(
        SearchRouteTraits current,
        SimulationSnapshot before,
        SimulationSnapshot after)
    {
        if (OpensRevivalWindow(before, after))
            return current | SearchRouteTraits.RevivalWindow;
        return current;
    }

    internal static bool OpensRevivalWindow(
        SimulationSnapshot before,
        SimulationSnapshot after)
        => after.RevivingEnemyCount > before.RevivingEnemyCount
            || after.RawEnemyHp < before.RawEnemyHp && after.EnemyHp >= before.EnemyHp
            || (before.AliveEnemyMask & ~after.AliveEnemyMask) != 0
                && (after.AliveEnemyMask & ~before.AliveEnemyMask) != 0;

    internal static bool IsPureHistoryEntry(CombatPredictionHistoryEntry entry)
        => entry is CombatPredictionCardPlayStartedEntry
            or CombatPredictionCardPlayFinishedEntry
            or CombatPredictionCreatureAttackedEntry
            or CombatPredictionDamageReceivedEntry;

    internal static IEnumerable<(int Index, Creature? Target)> TargetsFor(
        PredictedCard card,
        CombatPredictionSimulator simulator)
    {
        if (simulator.GetTargetType(card) == TargetType.AnyEnemy)
        {
            IReadOnlyList<Creature> enemies = simulator.State.Enemies;
            for (int i = 0; i < enemies.Count; i++)
            {
                Creature target = enemies[i];
                if (simulator.State.IsHittable(target))
                    yield return (i, target);
            }
            yield break;
        }

        yield return (-1, null);
    }

    internal static IEnumerable<(int Index, Creature? Target)> TargetsForPotion(
        Player player,
        PotionModel potion,
        CombatPredictionSimulator simulator)
    {
        if (potion.TargetType == TargetType.AnyEnemy)
        {
            IReadOnlyList<Creature> enemies = simulator.State.Enemies;
            for (int index = 0; index < enemies.Count; index++)
            {
                Creature enemy = enemies[index];
                if (simulator.State.IsHittable(enemy))
                    yield return (index, enemy);
            }
            yield break;
        }

        if (potion.TargetType is TargetType.AnyPlayer or TargetType.Self)
        {
            if (simulator.State.GetCreature(player.Creature).IsAlive)
                yield return (-1, null);
            yield break;
        }

        if (potion.TargetType is TargetType.AllEnemies or TargetType.TargetedNoCreature)
            yield return (-1, null);
    }

    internal static IReadOnlyList<PlanCardChoice>? ActionChoicesForReplay(PlanAction action)
    {
        List<PlanCardChoice> choices = [.. action.GetActionChoicesInExecutionOrder()];
        // AdvanceRound owns the choices after a card requests the end of the turn.
        // This cursor ends at the card boundary, before that round transition starts.
        return choices.Count == 0 ? null : choices;
    }

    internal static CardChoiceSpec? BuildPrimaryCardChoiceSpec(
        Player player,
        SimulationSnapshot probeSnapshot)
    {
        CombatPredictionSimulator probeSimulator =
            (CombatPredictionSimulator)probeSnapshot.Simulator;
        SimulatedCombatState probeCombat =
            (SimulatedCombatState)probeSnapshot.Simulator.State.CombatState;
        return probeCombat.PendingTurnStartChoice is { } pendingChoice
            && string.IsNullOrEmpty(pendingChoice.SourceId)
                ? TurnStartChoiceSupport.BuildPendingSpec(
                    probeSimulator,
                    probeCombat,
                    player)
                : null;
    }

    internal static CardChoiceSpec? BuildRequiredEmptyChoiceSpec(PlanCardChoice? requiredEmptyChoice)
        => requiredEmptyChoice == null
            ? null
            : new CardChoiceSpec(
                requiredEmptyChoice.Effect,
                requiredEmptyChoice.SourcePile,
                0,
                0,
                [],
                [],
                ReplacementValue: 0d);

    internal static PrimaryChoiceMatch? BuildPrimaryChoiceMatch(
        CardChoiceSpec? primaryChoiceSpec)
        => primaryChoiceSpec == null
            ? null
            : new PrimaryChoiceMatch(
                primaryChoiceSpec.ContextId,
                primaryChoiceSpec.Effect,
                primaryChoiceSpec.SourcePile,
                primaryChoiceSpec.MinCount);

    internal static PlanAction AppendPendingChoice(
        PlanAction action,
        TurnStartChoiceRequest request,
        PlanCardChoice branch,
        bool turnResolution,
        PrimaryChoiceMatch? unresolvedPrimaryChoice)
    {
        bool primaryChoice = !turnResolution
            && action.Choice == null
            && string.IsNullOrEmpty(request.SourceId)
            && (unresolvedPrimaryChoice == null
                || MatchesPrimaryChoice(request, unresolvedPrimaryChoice.Value));
        IReadOnlyList<PlanCardChoice> existing = turnResolution
            ? action.TurnStartChoices ?? []
            : action.NestedChoices ?? [];
        List<PlanCardChoice> next = new(existing.Count + 1);
        next.AddRange(existing);
        PlanCardChoice resolvedBranch = branch with
        {
            SourceId = request.SourceId,
            ContextId = request.ContextId,
            Timing = request.Timing,
        };
        if (!primaryChoice)
            next.Add(resolvedBranch);
        return turnResolution
            ? action with { TurnStartChoices = next }
            : primaryChoice
                ? action with { Choice = resolvedBranch }
                : action with
                {
                    NestedChoices = next,
                    NestedChoicesBeforePrimary = action.Choice == null
                        ? action.NestedChoicesBeforePrimary + 1
                        : action.NestedChoicesBeforePrimary,
                };
    }

    private static bool MatchesPrimaryChoice(
        TurnStartChoiceRequest request,
        PrimaryChoiceMatch primaryChoice)
        => string.IsNullOrEmpty(request.SourceId)
            && string.Equals(request.ContextId, primaryChoice.ContextId, StringComparison.Ordinal)
            && request.Effect == primaryChoice.Effect
            && request.SourcePile == primaryChoice.SourcePile
            && request.Count == primaryChoice.MinCount;

    internal static PredictedCard? FindCardForReplay(
        IReadOnlyList<PredictedCard> cards,
        PlanAction action)
    {
        if (!string.IsNullOrEmpty(action.CardStateKey))
        {
            int occurrence = action.CardStateOccurrence;
            foreach (PredictedCard card in cards)
            {
                if (!string.Equals(
                        CardChoiceSupport.ChoiceCardKey(card),
                        action.CardStateKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (occurrence-- == 0)
                    return card;
            }
            return null;
        }
        return FindCardOccurrence(cards, action.CardId, action.CardOccurrence);
    }

    internal static PredictedCard? FindCardOccurrence(
        IReadOnlyList<PredictedCard> cards,
        string cardId,
        int occurrence)
    {
        foreach (PredictedCard card in cards)
        {
            if (!string.Equals(card.Preview.Id.Entry, cardId, StringComparison.Ordinal))
                continue;
            if (occurrence-- == 0)
                return card;
        }
        return null;
    }

    internal static string PolicyActionToken(PlanAction action)
        => action.Kind switch
        {
            PlanActionKind.PlayCard => action.Choice == null
                ? $"{action.Turn}:C:{action.CardId}"
                : $"{action.Turn}:C:{action.CardId}[{string.Join(',', action.Choice.Cards.Select(card => card.CardId))}]",
            PlanActionKind.UsePotion => action.Choice == null
                ? $"{action.Turn}:P:{action.PotionId}"
                : $"{action.Turn}:P:{action.PotionId}[{string.Join(',', action.Choice.Cards.Select(card => card.CardId))}]",
            PlanActionKind.EndTurn => action.TurnStartChoices is not { Count: > 0 }
                ? $"{action.Turn}:E"
                : $"{action.Turn}:E:" + string.Join(';', action.TurnStartChoices.Select(choice =>
                    $"{choice.SourceId}={string.Join(',', choice.Cards.Select(card => card.CardId))}")),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, null),
        };
    internal static int PolicyBoundaryRank(SearchBoundaryReason reason)
        => reason switch
        {
            SearchBoundaryReason.None => 0,
            SearchBoundaryReason.NoCards or SearchBoundaryReason.Shuffle
                or SearchBoundaryReason.TurnLimit or SearchBoundaryReason.NodeLimit
                or SearchBoundaryReason.TimeLimit => 1,
            SearchBoundaryReason.PendingChoice => 2,
            SearchBoundaryReason.UnsupportedEffect => 3,
            SearchBoundaryReason.EventDefeat => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        };

    internal static int ComparePotionFreePolicyBaselines(
        SearchNode left,
        SearchNode right,
        int initialPlayerHp,
        int initialPlayerMaxHp,
        BossHpRelief bossHpRelief,
        PostCombatRelicHealProfile postCombatRelicHeal,
        SolverTheftPolicy? theftPolicy)
    {
        SimulationSnapshot leftSnapshot = left.Snapshot;
        SimulationSnapshot rightSnapshot = right.Snapshot;
        bool leftWon = SolverInterimResultOrdering.IsCompleteVictory(
            left.ActionCount,
            leftSnapshot.AllEnemiesDead,
            leftSnapshot.PlayerDead,
            leftSnapshot.ProjectedPlayerHp);
        bool rightWon = SolverInterimResultOrdering.IsCompleteVictory(
            right.ActionCount,
            rightSnapshot.AllEnemiesDead,
            rightSnapshot.PlayerDead,
            rightSnapshot.ProjectedPlayerHp);
        int comparison = rightWon.CompareTo(leftWon);
        if (comparison != 0)
            return comparison;
        if (!leftWon && !rightWon)
        {
            bool leftSurvives = !leftSnapshot.PlayerDead && leftSnapshot.ProjectedPlayerHp > 0;
            bool rightSurvives = !rightSnapshot.PlayerDead && rightSnapshot.ProjectedPlayerHp > 0;
            comparison = rightSurvives.CompareTo(leftSurvives);
            if (comparison != 0)
                return comparison;
        }
        comparison = leftSnapshot.ProjectedDeathSaveUseCount.CompareTo(
            rightSnapshot.ProjectedDeathSaveUseCount);
        if (comparison != 0)
            return comparison;
        comparison = TheftEncounterStrategy.CompareRecovery(theftPolicy,
            leftWon, leftSnapshot.OutstandingStolenResource, rightWon, rightSnapshot.OutstandingStolenResource);
        if (comparison != 0)
            return comparison;
        RouteQuality leftQuality = RouteQuality.Primary(leftWon,
            ActEndingBossPolicy.StrategicHpDeficit(
                leftSnapshot.CumulativePlayerHpLost,
                Math.Max(0, initialPlayerMaxHp - leftSnapshot.PlayerMaxHp),
                leftSnapshot.RecoveredPlayerHp
                    + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                        postCombatRelicHeal, leftWon, leftSnapshot.PlayerHp, leftSnapshot.PlayerMaxHp),
                bossHpRelief, leftSnapshot.DeathSaveHpRestored) - leftSnapshot.StrategicHpCredit,
            leftWon ? leftSnapshot.CombatEndedTurn : null,
            leftSnapshot.StrategyGoalHpCredit,
            leftSnapshot.StrategyGoalCount,
            leftSnapshot.ProjectedDeathSaveUseCount);
        RouteQuality rightQuality = RouteQuality.Primary(rightWon,
            ActEndingBossPolicy.StrategicHpDeficit(
                rightSnapshot.CumulativePlayerHpLost,
                Math.Max(0, initialPlayerMaxHp - rightSnapshot.PlayerMaxHp),
                rightSnapshot.RecoveredPlayerHp
                    + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                        postCombatRelicHeal, rightWon, rightSnapshot.PlayerHp, rightSnapshot.PlayerMaxHp),
                bossHpRelief, rightSnapshot.DeathSaveHpRestored) - rightSnapshot.StrategicHpCredit,
            rightWon ? rightSnapshot.CombatEndedTurn : null,
            rightSnapshot.StrategyGoalHpCredit,
            rightSnapshot.StrategyGoalCount,
            rightSnapshot.ProjectedDeathSaveUseCount);
        comparison = RouteQualityPolicy.Compare(leftQuality, rightQuality, RouteQualityProjection.Primary);
        if (comparison != 0)
            return comparison;
        if (theftPolicy == SolverTheftPolicy.PreserveResources)
        {
            comparison = leftSnapshot.OutstandingStolenResource.CompareTo(
                rightSnapshot.OutstandingStolenResource);
            if (comparison != 0)
                return comparison;
        }
        comparison = (initialPlayerHp - leftSnapshot.PlayerHp
                + initialPlayerMaxHp - leftSnapshot.PlayerMaxHp)
            .CompareTo(initialPlayerHp - rightSnapshot.PlayerHp
                + initialPlayerMaxHp - rightSnapshot.PlayerMaxHp);
        if (comparison != 0)
            return comparison;
        comparison = rightSnapshot.LongTermResourceValue.CompareTo(leftSnapshot.LongTermResourceValue);
        if (comparison != 0)
            return comparison;
        comparison = leftSnapshot.AngerCopiesGenerated.CompareTo(rightSnapshot.AngerCopiesGenerated);
        if (comparison != 0)
            return comparison;
        comparison = PolicyBoundaryRank(leftSnapshot.BoundaryReason)
            .CompareTo(PolicyBoundaryRank(rightSnapshot.BoundaryReason));
        if (comparison != 0)
            return comparison;
        comparison = leftSnapshot.EnemyHp.CompareTo(rightSnapshot.EnemyHp);
        if (comparison != 0)
            return comparison;
        comparison = right.Score.CompareTo(left.Score);
        if (comparison != 0)
            return comparison;
        comparison = left.FutureSoldHp.CompareTo(right.FutureSoldHp);
        if (comparison != 0)
            return comparison;
        comparison = left.ActionCount.CompareTo(right.ActionCount);
        if (comparison != 0)
            return comparison;
        comparison = left.StateKey.First.CompareTo(right.StateKey.First);
        return comparison != 0
            ? comparison
            : left.StateKey.Second.CompareTo(right.StateKey.Second);
    }
    internal static bool SettleReplayActionBoundary(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
    {
        using var executionDispatch = simulator.BeginExecutionDispatch();
        simulator.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        if (simulator.HasPendingChoice)
            return false;
        combat.NormalizeAeonglassWithers(simulator);
        if (simulator.HasPendingChoice)
            return false;
        combat.NormalizeCardAfflictions(simulator);
        return !simulator.HasPendingChoice;
    }

    internal static SearchBoundaryReason PreparePlayerPlayPhase(
        SearchRoundContext context,
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        TurnStartChoiceCursor choices,
        ISet<uint> processedEnemyDeaths)
    {
        Player player = context.Player;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        if (PersistentRelicSupport.ShouldPlayerResetEnergy(simulatedCombat, player))
            playerState.LoseEnergy(playerState.Energy);
        playerState.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(simulatedCombat, player)
            + simulatedCombat.ConsumeEnergyNextTurn(player));
        if (simulatedCombat.HasPendingChoice
            || !PersistentPowerSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, player))
        {
            return SearchBoundaryReason.PendingChoice;
        }
        TurnStartRelicSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, player);
        if (simulatedCombat.HasPendingChoice)
            return SearchBoundaryReason.PendingChoice;
        TurnStartRelicSupport.TriggerAfterEnergyResetLate(simulator, simulatedCombat, player);
        if (simulatedCombat.HasPendingChoice)
            return SearchBoundaryReason.PendingChoice;
        var progress = new PlayerStartProgress(player, context.StartTurnNumber, rootSetup: true,
            takingExtraTurn: false, processedEnemyDeaths, 0, simulator.ShuffleEventCount);
        return SearchRoundExecution.ContinuePlayerStart(
            context, simulator, simulatedCombat, progress, PlayerStartStage.BeforeHand);
    }

    internal static SearchBoundaryReason ResolveRequestedPlayerTurnEnd(
        SearchRoundContext context,
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PlanAction action,
        ISet<uint> processedEnemyDeaths,
        ref int turn,
        ref int shufflesCrossed)
    {
        bool requested = combat.ConsumePlayerTurnEndRequest();
        if (!requested || !simulator.IsInProgress)
            return SearchBoundaryReason.None;
        SearchBoundaryReason boundary = SearchRoundExecution.AdvanceRound(
            context, simulator, combat, turn - context.StartTurnNumber, processedEnemyDeaths,
            ref shufflesCrossed, action.TurnStartChoices);
        if (boundary == SearchBoundaryReason.None && !SettleReplayActionBoundary(simulator, combat))
            boundary = SearchBoundaryReason.PendingChoice;
        _ = combat.ConsumePlayerTurnEndRequest();
        turn = combat.GetPlayerTurnNumber(context.Player);
        return boundary;
    }

    internal static List<ActionCandidate> SelectActionCandidates(
        SearchNode parent,
        List<ActionCandidate> candidates,
        CandidateSelectionContext context)
    {
        if (context.DevelopmentStrategy == null)
            candidates.Sort(static (left, right) =>
            {
                int byScore = right.Node.Score.CompareTo(left.Node.Score);
                return byScore != 0 ? byScore : right.NormalizedValue.CompareTo(left.NormalizedValue);
            });
        else
            candidates.Sort((left, right) =>
            {
                double leftScore = context.DevelopmentStrategy.Prioritize(left.Node,
                    left.Node.Action?.CardId ?? "", left.Damage, left.Block,
                    left.EnergySpent, left.StarsSpent, left.NormalizedValue, left.Node.Score);
                double rightScore = context.DevelopmentStrategy.Prioritize(right.Node,
                    right.Node.Action?.CardId ?? "", right.Damage, right.Block,
                    right.EnergySpent, right.StarsSpent, right.NormalizedValue, right.Node.Score);
                int byScore = rightScore.CompareTo(leftScore);
                return byScore != 0
                    ? byScore
                    : right.NormalizedValue.CompareTo(left.NormalizedValue);
            });
        int limit = Math.Min(context.Profile.MaxCardBranchesPerNode, candidates.Count);
        List<ActionCandidate> selected = new(limit + 2);

        void Add(ActionCandidate candidate, bool allowOverflow = false)
        {
            if (!allowOverflow && selected.Count >= limit)
                return;
            // A capturing predicate allocated once per admission attempt, including
            // duplicate representatives. Keep the original reference-identity test.
            for (int index = 0; index < selected.Count; index++)
                if (ReferenceEquals(selected[index].Node, candidate.Node))
                    return;
            selected.Add(candidate);
        }

        // A stolen-resource carrier can have no immediate attack threat while it is
        // preparing to flee. Preserve at least one actionable branch against it before
        // the ordinary action-family quota fills the node.
        if (context.TheftPolicy == SolverTheftPolicy.PreserveResources)
        {
            foreach (ActionCandidate candidate in candidates)
                if (IsStolenResourceRecoveryTarget(candidate))
                    Add(candidate);
        }

        // A resolved routing choice and a revival window are semantic branch boundaries. Preserve
        // the previous overflow behavior for them; the ordinary family portfolio remains inside the
        // configured per-node card branch budget.
        foreach (ActionCandidate candidate in candidates)
        {
            if (context.IsRoutingChoice?.Invoke(candidate.Node) == true)
                Add(candidate, allowOverflow: true);
        }
        ActionCandidate? revivalWindowCandidate = null;
        foreach (ActionCandidate candidate in candidates)
        {
            SimulationSnapshot snapshot = candidate.Node.Snapshot;
            if (snapshot.RevivingEnemyCount <= parent.Snapshot.RevivingEnemyCount)
                continue;
            if (revivalWindowCandidate is { } current)
            {
                SimulationSnapshot best = current.Node.Snapshot;
                int comparison = best.RevivingEnemyCount.CompareTo(snapshot.RevivingEnemyCount);
                if (comparison == 0)
                    comparison = snapshot.RawEnemyHp.CompareTo(best.RawEnemyHp);
                if (comparison == 0)
                    comparison = snapshot.MaxCurrentEnemyHp.CompareTo(best.MaxCurrentEnemyHp);
                if (comparison == 0)
                    comparison = best.ProjectedPlayerHp.CompareTo(snapshot.ProjectedPlayerHp);
                // Stable first minimum, matching OrderBy/ThenBy/FirstOrDefault.
                if (comparison >= 0)
                    continue;
            }
            revivalWindowCandidate = candidate;
        }
        if (revivalWindowCandidate is { } revivalCandidate)
            Add(revivalCandidate, allowOverflow: true);

        ReadOnlySpan<ActionOptionFamily> families =
        [
            ActionOptionFamily.ImmediateDefense,
            ActionOptionFamily.ImmediateOffense,
            ActionOptionFamily.ResourceAndCycle,
            ActionOptionFamily.PersistentSetup,
            ActionOptionFamily.Control,
            ActionOptionFamily.TargetRemoval,
            ActionOptionFamily.HpInvestment,
        ];
        foreach (ActionOptionFamily family in families)
        {
            foreach (ActionCandidate candidate in candidates)
            {
                if (!candidate.OptionFamilies.HasFlag(family))
                    continue;
                Add(candidate);
                break;
            }
        }

        // Candidates are score-sorted. GroupBy yielded the first candidate for
        // each target in first-seen order; a set retains that order without groups.
        HashSet<uint> seenDamageTargets = [];
        foreach (ActionCandidate candidate in candidates)
        {
            if (candidate.TargetCombatId is uint targetCombatId
                && candidate.Damage > 0
                && seenDamageTargets.Add(targetCombatId))
                Add(candidate);
        }

        foreach (ActionCandidate candidate in candidates)
            Add(candidate);

        int baselineCount = Math.Min(limit, candidates.Count);
        HashSet<SearchNode> baseline = new(ReferenceEqualityComparer.Instance);
        foreach (ActionCandidate candidate in candidates.Take(baselineCount))
            baseline.Add(candidate.Node);
        context.OnProtected?.Invoke(selected.Count(candidate =>
            !baseline.Contains(candidate.Node)));
        if (context.DetailedDiagnostics && parent.ActionCount <= 1)
        {
            context.Log?.Invoke(
                $"[CombatSolver/Debug] ACTION_ADMISSION prefix=" +
                $"{string.Join('>', parent.Actions.Select(PolicyActionToken))} " +
                $"candidates={candidates.Count} " +
                $"limit={context.Profile.MaxCardBranchesPerNode} selected={selected.Count} " +
                $"protected={selected.Count(candidate => !baseline.Contains(candidate.Node))} " +
                $"portfolio={string.Join(',', selected.Select(candidate =>
                    $"{candidate.Node.Action?.CardId ?? "-"}:{candidate.OptionFamilies}:" +
                    $"{candidate.Node.Score:F0}/{candidate.NormalizedValue:F1}"))} " +
                $"admissible={string.Join(',', candidates.Select(candidate =>
                    $"{candidate.Node.Action?.CardId ?? "-"}:{candidate.OptionFamilies}:" +
                    $"{candidate.Node.Score:F0}/{candidate.NormalizedValue:F1}"))}");
        }
        return selected;
    }

internal sealed class CandidateSelectionContext(
    SolverSearchProfile profile,
    SolverTheftPolicy? theftPolicy,
    DevelopmentSearchStrategy? developmentStrategy,
    Action<int>? onProtected,
    bool detailedDiagnostics,
    Action<string>? log,
    Func<SearchNode, bool>? isRoutingChoice)
{
    public SolverSearchProfile Profile { get; } = profile;
    public SolverTheftPolicy? TheftPolicy { get; } = theftPolicy;
    public DevelopmentSearchStrategy? DevelopmentStrategy { get; } = developmentStrategy;
    public Action<int>? OnProtected { get; } = onProtected;
    public bool DetailedDiagnostics { get; } = detailedDiagnostics;
    public Action<string>? Log { get; } = log;
    public Func<SearchNode, bool>? IsRoutingChoice { get; } = isRoutingChoice;
}

    internal static SearchNode? ApplyFixedPrefix(
        SearchNode seed,
        IReadOnlyList<PlanAction> prefix,
        SearchExpansionContext context,
        Func<IReadOnlyList<PlanAction>, SimulationSnapshot?, int, int, SimulationSnapshot> replay,
        bool resetSchedulingBaseline = false,
        Func<SearchNode, SearchNode>? attachLineage = null,
        Func<SearchNode, TurnOutcome>? createUncomparedOutcome = null)
    {
        SearchNode node = seed;
        try
        {
            foreach (PlanAction action in prefix)
            {
                if (action.Turn != node.Turn)
                {
                    throw new InvalidOperationException(
                        $"固定搜索前缀动作无效：kind={action.Kind} actionTurn={action.Turn} " +
                        $"nodeTurn={node.Turn} endsPlayerTurn={action.EndsPlayerTurn} " +
                        $"card={(string.IsNullOrEmpty(action.CardId) ? "-" : action.CardId)} " +
                        $"potion={(string.IsNullOrEmpty(action.PotionId) ? "-" : action.PotionId)}。");
                }

                if (!CanApplyFixedPrefixAction(context, node, action))
                {
                    node.Snapshot.ReleaseSimulator();
                    return null;
                }

                SimulationSnapshot snapshot = replay(
                    [action],
                    node.Snapshot,
                    node.Turn,
                    node.ActionCount);
                bool terminal = snapshot.PlayerDead
                    || snapshot.AllEnemiesDead
                    || snapshot.BoundaryReason != SearchBoundaryReason.None;
                // Routes freeze "this play ended the turn" on card actions. The prefix may
                // contain such an action; it must still end the turn here, otherwise the
                // prefix was built from a different state than this root.
                if (action.Kind == PlanActionKind.PlayCard
                    && action.EndsPlayerTurn
                    && snapshot.Turn <= node.Turn
                    && !snapshot.PlayerDead
                    && !snapshot.AllEnemiesDead)
                {
                    snapshot.ReleaseSimulator();
                    throw new InvalidOperationException(
                        $"固定搜索前缀动作无效：kind={action.Kind} actionTurn={action.Turn} " +
                        $"nodeTurn={node.Turn} endsPlayerTurn={action.EndsPlayerTurn} " +
                        $"turnAdvanced=False " +
                        $"card={(string.IsNullOrEmpty(action.CardId) ? "-" : action.CardId)} " +
                        $"potion={(string.IsNullOrEmpty(action.PotionId) ? "-" : action.PotionId)}。");
                }
                SearchRouteTraits traits = action.Kind == PlanActionKind.UsePotion
                    ? ClassifyPotionTraits(node.Traits, node.Snapshot, snapshot)
                    : node.Traits;
                int cumulativeEnemyHpLost = SearchProbeHelpers.AccumulateEnemyHpLost(node, snapshot);
                node = new SearchNode(
                    action,
                    node.ActionCount + 1,
                    snapshot.PotionUseCount,
                    snapshot.PotionStrategicCost,
                    snapshot.Turn,
                    traits,
                    node.FutureSoldHp,
                    SearchProbeHelpers.ApplySoldHpPenalty(snapshot.Score, node.FutureSoldHp),
                    snapshot.StateKey,
                    snapshot.HasRisk,
                    snapshot.BoundaryReason,
                    terminal,
                    node,
                    snapshot,
                    node.CombatProgress)
                {
                    CumulativeEnemyHpLost = cumulativeEnemyHpLost,
                };
                if (attachLineage != null) node = attachLineage(node);
                if (terminal || node.Turn > node.Parent!.Turn)
                {
                    // Fixed prefixes have no sibling alternatives for comparative HP investment or block.
                    if (createUncomparedOutcome != null
                    && (terminal || node.Turn > node.Parent!.Turn))
                {
                    node = node with { Outcome = createUncomparedOutcome(node) };
                }
                }
                bool combatOutcomeLocked = snapshot.PlayerDead || snapshot.AllEnemiesDead;
                node.Parent!.Snapshot.ReleaseSimulator();
                if (combatOutcomeLocked)
                {
                    // The remaining prefix actions are unreachable once the combat outcome is
                    // locked; replaying them from a stopped simulator is the reported failure.
                    break;
                }
            }
            if (resetSchedulingBaseline && prefix.Count > 0)
            {
                // 固定前缀模拟的是“玩家已经完成这些动作后重新计算”。后续搜索必须以此刻真实状态
                // 重新建立进展基线；沿用前缀之前的最低/最高值会让同一局面区别于手动动作后的新根。
                node = node with
                {
                    CombatProgress = CombatProgressState.Capture(node.Snapshot),
                    Cycle = null,
                };
                node.PowerCommitment = null;
                node.OrderedMutationLineage = null;
                node.OrderedMutationBoundaryLineage = null;
                node.OrderedMutationRetentionLease = null;
                node.OrderedMutationActivationTicket = null;
            }
            return node;
        }
        catch
        {
            node.Snapshot.ReleaseSimulator();
            throw;
        }
    }

    internal static bool CanApplyFixedPrefixAction(
        SearchExpansionContext context,
        SearchNode node,
        PlanAction action)
    {
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)node.Snapshot.Simulator;
        if (node.Snapshot.BoundaryReason != SearchBoundaryReason.None || !simulator.IsInProgress)
            return false;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (action.Kind == PlanActionKind.EndTurn)
            return true;
        if (action.Kind == PlanActionKind.UsePotion)
        {
            return SearchExpansionShared.EnumeratePlannedPotionActions(
                    context, new ExpansionPlan(node, CardNameFirst: false))
                .Any(candidate => candidate.Action.PotionSlot == action.PotionSlot
                    && string.Equals(candidate.Action.PotionId, action.PotionId, StringComparison.Ordinal)
                    && candidate.Action.TargetCombatId == action.TargetCombatId);
        }

        SimPlayerCombatState player = simulator.State.GetPlayerCombatState(context.Player);
        PredictedCard? card = FindCardForReplay(player.Hand.Cards, action);
        return card != null && combat.CanPlayCard(simulator, card)
            && TargetsFor(card, simulator).Any(target => target.Target?.CombatId == action.TargetCombatId);
    }

}

internal readonly record struct PrimaryChoiceMatch(
    string ContextId,
    PlanChoiceEffect Effect,
    PileType SourcePile,
    int MinCount);
