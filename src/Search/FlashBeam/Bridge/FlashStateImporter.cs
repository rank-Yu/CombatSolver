using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Flash;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.FlashBeam;

/// <summary>
/// Projects the existing engine's branch state into <see cref="FlashBattle"/>. The same projection
/// imports search roots and checks differential steps, so every value that affects later
/// resolution either maps into Flash or rejects the state.
/// </summary>
internal static class FlashStateImporter
{
    public static FlashBattle Import(SimulationSnapshot snapshot, Player player, FlashRules rules)
        => Import(snapshot.Simulator, player, rules, snapshot.ShufflesCrossed, snapshot.ProcessedEnemyDeaths,
            snapshot.HasRisk);

    public static FlashBattle Import(CombatPredictionSimulator simulator, Player player, FlashRules rules,
        int shufflesCrossed, IReadOnlySet<uint> processedDeaths, bool hasRisk)
    {
        // The existing engine's per-turn counter getters fill their caches from root history on first
        // read. Reading a private fork keeps the caller's state, which other workers may fork, untouched.
        simulator = simulator.Fork();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (combat.Players.Count != 1)
            throw new FlashImportException(FlashRejection.MultiPlayer, $"玩家数 {combat.Players.Count}");
        if (simulator.HasPendingChoice)
            throw new FlashImportException(FlashRejection.PendingChoice, "模拟器有待处理选择");
        if (combat.DescribeFlashUnrepresentableState() is { } unrepresentable)
            throw new FlashImportException(FlashRejection.UnrepresentableState, unrepresentable);
        FlashStateProjections projections = new(simulator.StateStore, combat);

        FlashBattle battle = default;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);

        battle.Round = combat.RoundNumber;
        battle.PlayerTurn = combat.GetPlayerTurnNumber(player);
        battle.ShuffleEventCount = simulator.ShuffleEventCount;
        battle.ShufflesCrossed = shufflesCrossed;
        battle.NextCombatId = combat.NextCreatureIdForFlash;
        battle.Energy = playerState.Energy;
        battle.Stars = playerState.Stars;
        battle.Gold = combat.GetPlayerGold(player);
        battle.Side = (FlashSide)(byte)combat.CurrentSide;
        battle.Phase = (FlashPlayerPhase)(byte)playerState.Phase;
        battle.Outcome = simulator.TerminalStamp is not { } stamp
            ? FlashOutcome.None
            : stamp.Outcome == CombatTerminalOutcome.Victory ? FlashOutcome.Victory : FlashOutcome.Defeat;
        if (simulator.IsAboutToLose)
            battle.Flags |= FlashBattleFlags.PendingLoss;
        if (playerState.HooksActive)
            battle.Flags |= FlashBattleFlags.PlayerHooksActive;
        if (combat.PlayerTurnEndRequestedForFlash)
            battle.Flags |= FlashBattleFlags.PlayerTurnEndRequested;
        // K5 compares the snapshot's uncompensated risk, not the raw history flag: compensated
        // MethodMirrorIncomplete entries (CardOnPlayCompensationCatalog and friends) leave
        // snapshot.HasRisk false. The kernel only records uncompensated UnresolvedPlayerChoice risk.
        if (hasRisk)
            battle.Flags |= FlashBattleFlags.HasRisk;
        battle.MaxHandSize = checked((byte)combat.GetMaxHandSize(player));

        battle.Player = Vitals(simulator.State.GetCreature(player.Creature), combat.HasLostHpThisTurn(player.Creature));
        if (simulator.State.GetOsty(player) != null)
            throw new FlashImportException(FlashRejection.UnrepresentableState, "Osty");

        ImportCounters(ref battle, simulator, combat, player);

        ImportRng(ref battle, simulator.Rng);
        IReadOnlyList<Creature> known = combat.KnownEnemies;
        ImportEnemies(ref battle, simulator, combat, known, rules, processedDeaths, projections);
        ImportPowers(ref battle, combat, player, known, projections);
        ImportCards(ref battle, playerState, rules);
        RejectRunListenerDeckCards(player);
        ImportRelics(ref battle, combat, player, projections);
        ImportPotions(ref battle, combat, player);
        ImportOrbs(ref battle, playerState);
        projections.RequireAllMonsterIntsClaimed();
        projections.RequireAllClaimed();
        return battle;
    }

    public static FlashKeywords MapKeywords(IEnumerable<CardKeyword> keywords)
    {
        FlashKeywords result = FlashKeywords.None;
        foreach (CardKeyword keyword in keywords)
        {
            result |= keyword switch
            {
                CardKeyword.Exhaust => FlashKeywords.Exhaust,
                CardKeyword.Ethereal => FlashKeywords.Ethereal,
                CardKeyword.Innate => FlashKeywords.Innate,
                CardKeyword.Unplayable => FlashKeywords.Unplayable,
                CardKeyword.Retain => FlashKeywords.Retain,
                CardKeyword.Sly => FlashKeywords.Sly,
                CardKeyword.Eternal => FlashKeywords.Eternal,
                _ => throw new FlashImportException(FlashRejection.UnsupportedCard, $"关键字 {keyword}"),
            };
        }
        return result;
    }

    private static FlashCreature Vitals(SimCreatureState creature, bool lostHpThisTurn) => new()
    {
        CurrentHp = creature.CurrentHp,
        MaxHp = creature.MaxHp,
        Block = creature.Block,
        Flags = FlashCreatureFlags.Present | (lostHpThisTurn ? FlashCreatureFlags.LostHpThisTurn : FlashCreatureFlags.None),
    };

    private static void ImportCounters(ref FlashBattle battle, CombatPredictionSimulator simulator,
        SimulatedCombatState combat, Player player)
    {
        Creature owner = player.Creature;
        battle.SetCounter(FlashCounter.CardsPlayedThisTurn, combat.GetCardsPlayedThisTurn(owner));
        battle.SetCounter(FlashCounter.ManualCardsPlayedThisTurn, combat.GetManualCardsPlayedThisTurn(owner));
        battle.SetCounter(FlashCounter.CardPlayStartsThisTurn, combat.GetCardPlayStartsThisTurn(owner));
        battle.SetCounter(FlashCounter.AttackPlayStartsThisTurn, combat.GetAttackPlayStartsThisTurn(owner));
        battle.SetCounter(FlashCounter.AttackSkillStartsThisTurn, combat.GetAttackSkillStartsThisTurn(owner));
        battle.SetCounter(FlashCounter.ZeroCostAttackStartsThisTurn, combat.GetZeroCostAttackStartsThisTurn(owner));
        battle.SetCounter(FlashCounter.CardPlaySeriesStartedThisTurn, combat.GetCardPlaySeriesStartedThisTurn(owner));
        battle.SetCounter(FlashCounter.AttacksPlayedThisTurn, combat.GetAttacksPlayedThisTurn(owner));
        battle.SetCounter(FlashCounter.ShivsPlayedThisTurn, combat.GetShivsPlayedThisTurn(owner));
        battle.SetCounter(FlashCounter.SkillCardsPlayedThisTurn, combat.GetSkillCardsPlayedThisTurn(owner));
        battle.SetCounter(FlashCounter.BlockCardsPlayedThisTurn, combat.GetBlockCardsPlayedThisTurn(owner));
        battle.SetCounter(FlashCounter.CardsExhaustedThisTurn, combat.CardsExhaustedThisTurnForFlash(owner));
        battle.SetCounter(FlashCounter.CardsDiscardedThisTurn, combat.GetCardsDiscardedThisTurn(owner));
        battle.SetCounter(FlashCounter.PlayerAttacksThisTurn, combat.GetCreatureAttacksThisTurn(owner));
        battle.SetCounter(FlashCounter.NonHandDrawsThisTurn, combat.GetNonHandDrawsThisTurn(player));
        battle.SetCounter(FlashCounter.StatusCardsDrawnThisTurn, combat.GetStatusCardsDrawnThisTurn(player));
        battle.SetCounter(FlashCounter.EnergySpentThisTurn, combat.GetEnergySpentThisTurn(player));
        battle.SetCounter(FlashCounter.CumulativeHpLost, combat.GetCumulativeHpLost(owner));
        battle.SetCounter(FlashCounter.RecoveredHp, combat.GetRecoveredHp(owner));
        // CalculatedVarSpecRegistry.CountUnblockedDamageEvents: root history plus the predicted
        // DamageReceived entries for this player.
        int unblockedEvents = combat.GetUnblockedHitsBeforePrediction(owner);
        foreach (CombatPredictionHistoryEntry entry in simulator.History)
        {
            if (entry is CombatPredictionDamageReceivedEntry damage
                && damage.Receiver == owner
                && damage.Result.UnblockedDamage > 0)
            {
                unblockedEvents++;
            }
        }
        battle.SetCounter(FlashCounter.PlayerUnblockedDamageEvents, unblockedEvents);
        battle.SetCounter(FlashCounter.LongTermResourceValue, combat.LongTermResourceValue);
        battle.SetCounter(FlashCounter.AngerCopiesGenerated, combat.AngerCopiesGenerated);
        battle.SetCounter(FlashCounter.DeathSaveRelicHpRestored, combat.DeathSaveRelicHpRestored);
        battle.SetCounter(FlashCounter.DeathSavePotionHpRestored, combat.DeathSavePotionHpRestored);
        battle.SetCounter(FlashCounter.DeathSaveUseCount, combat.DeathSaveUseCount);
        int automaticPotionUseCount = 0;
        foreach (PredictedPotionUse use in combat.PotionUses)
        {
            if (use.Automatic)
                automaticPotionUseCount = checked(automaticPotionUseCount + 1);
        }
        battle.SetCounter(FlashCounter.AutomaticPotionUseCount, automaticPotionUseCount);
    }

    private static void ImportRng(ref FlashBattle battle, CombatPredictionRngSet rng)
    {
        Set(ref battle, FlashRngStream.Shuffle, rng.ShuffleState);
        Set(ref battle, FlashRngStream.CombatCardGeneration, rng.CombatCardGenerationState);
        Set(ref battle, FlashRngStream.CombatPotionGeneration, rng.CombatPotionGenerationState);
        Set(ref battle, FlashRngStream.CombatCardSelection, rng.CombatCardSelectionState);
        Set(ref battle, FlashRngStream.CombatEnergyCosts, rng.CombatEnergyCostsState);
        Set(ref battle, FlashRngStream.CombatTargets, rng.CombatTargetsState);
        Set(ref battle, FlashRngStream.CombatOrbGeneration, rng.CombatOrbGenerationState);
        Set(ref battle, FlashRngStream.MonsterAi, rng.MonsterAiState);
        Set(ref battle, FlashRngStream.Niche, rng.NicheState);

        static void Set(ref FlashBattle battle, FlashRngStream stream, PredictionRngState state)
            => battle.RngStream(stream) = FlashRngState.FromSerializable(state.Counter, state.State0, state.State1, state.State2, state.State3);
    }

    private static void ImportEnemies(ref FlashBattle battle, CombatPredictionSimulator simulator,
        SimulatedCombatState combat, IReadOnlyList<Creature> known, FlashRules rules, IReadOnlySet<uint> processedDeaths,
        FlashStateProjections projections)
    {
        if (known.Count > FlashLimits.Enemies)
            throw new FlashImportException(FlashRejection.Capacity, $"敌人数 {known.Count}");
        battle.EnemyCount = (byte)known.Count;
        for (int index = 0; index < known.Count; index++)
        {
            Creature creature = known[index];
            MonsterModel monster = creature.Monster
                ?? throw new FlashImportException(FlashRejection.UnsupportedMonster, "敌人没有怪物模型");
            if (!FlashRegistry.TryMonster(monster.Id.Entry, out ushort monsterId))
                throw new FlashImportException(FlashRejection.UnsupportedMonster, monster.Id.Entry);
            // Known enemies are append-only: the root's enemies occupy the first rules entries in
            // order, every later known enemy is a summoned type and shares its type's entry.
            int rulesId = index < rules.RootEnemyCount
                ? index
                : rules.TrySpawnRulesId(monsterId, out int spawnRulesId)
                    ? spawnRulesId
                    : throw new FlashImportException(FlashRejection.UnrepresentableState,
                        $"{monster.Id.Entry} 不在根的可生成怪物闭包中");
            FlashMonsterRules machine = rules.MonsterRule(checked((byte)rulesId));
            BranchMonsterAiState ai = combat.GetMonsterAiStateForFlash(creature);
            ref FlashEnemy enemy = ref battle.Enemies[index];
            enemy.Creature = Vitals(simulator.State.GetCreature(creature), combat.HasLostHpThisTurn(creature));
            enemy.MonsterId = monsterId;
            enemy.Rules = checked((byte)rulesId);
            enemy.Slot = SlotForFlash(rules, creature);
            enemy.DeathPhase = (FlashDeathPhase)(byte)combat.DeathPhaseForFlash(creature);
            enemy.CombatId = creature.CombatId ?? throw new FlashImportException(FlashRejection.UnrepresentableState, "敌人没有 CombatId");
            if (ai.Current.Id == FlashKernel.StunnedNodeId)
            {
                // SimulatedCombatState.ForceStunnedMove builds a fresh MoveState outside the machine's
                // States table (MonsterAi.cs:76-86). Its dynamic follow-up becomes ForcedMove.
                string followUp = ai.Current.FollowUpState?.Id ?? ai.Current.FollowUpStateId
                    ?? throw new FlashImportException(FlashRejection.UnrepresentableState,
                        $"{monster.Id.Entry} 的 STUNNED 没有后继行动");
                if (followUp == FlashKernel.StunnedNodeId)
                    throw new FlashImportException(FlashRejection.UnrepresentableState,
                        $"{monster.Id.Entry} 的 STUNNED 后继仍是 STUNNED");
                enemy.Move = checked((byte)machine.StunnedNode);
                enemy.ForcedMove = checked((byte)NodeIndex(machine, followUp, monster));
            }
            else if (ai.Current.Id == FlashKernel.ReviveNodeId)
            {
                // SimulatedCombatState.BeginIllusionRevive builds a fresh MoveState outside the
                // machine's States table (DeathLifecycle.cs:100-119). Its dynamic follow-up becomes
                // ForcedMove, exactly like STUNNED.
                if (machine.ReviveNode < 0)
                    throw new FlashImportException(FlashRejection.UnrepresentableState,
                        $"{monster.Id.Entry} 的复活行动没有 Flash 节点");
                string followUp = ai.Current.FollowUpState?.Id ?? ai.Current.FollowUpStateId
                    ?? throw new FlashImportException(FlashRejection.UnrepresentableState,
                        $"{monster.Id.Entry} 的 REVIVE_MOVE 没有后继行动");
                if (followUp is FlashKernel.StunnedNodeId or FlashKernel.ReviveNodeId)
                    throw new FlashImportException(FlashRejection.UnrepresentableState,
                        $"{monster.Id.Entry} 的 REVIVE_MOVE 后继是合成节点");
                enemy.Move = checked((byte)machine.ReviveNode);
                enemy.ForcedMove = checked((byte)NodeIndex(machine, followUp, monster));
            }
            else
            {
                if (enemy.DeathPhase == FlashDeathPhase.Reviving && machine.ReviveNode < 0)
                    throw new FlashImportException(FlashRejection.UnrepresentableState,
                        $"{monster.Id.Entry} 处于复活状态但没有复活节点");
                enemy.Move = checked((byte)machine.NodeIndex(ai.Current.Id));
                enemy.ForcedMove = byte.MaxValue;
            }
            if (ai.NeedsInitialRoll)
                enemy.Flags |= FlashEnemyFlags.NeedsInitialRoll;
            if (combat.WillSkipNextMoveForFlash(creature))
                enemy.Flags |= FlashEnemyFlags.SkipNextMove;
            if (processedDeaths.Contains(enemy.CombatId))
                enemy.Flags |= FlashEnemyFlags.DeathProcessed;
            foreach (string logged in ai.StateLog)
                FlashKernel.LogMonsterMove(ref enemy, machine.NodeIndex(logged));
            projections.ProjectMonster(creature, FlashRegistry.Monster(monsterId), ref enemy);
        }
        IReadOnlyList<Creature> roster = combat.Enemies;
        battle.RosterCount = (byte)roster.Count;
        for (int position = 0; position < roster.Count; position++)
        {
            int index = IndexOf(known, roster[position]);
            if (index < 0)
                throw new FlashImportException(FlashRejection.UnrepresentableState, "在场敌人不在已知列表中");
            battle.Roster[position] = (byte)index;
            battle.Enemies[index].Flags |= FlashEnemyFlags.InCombat;
        }
    }

    private static int NodeIndex(FlashMonsterRules machine, string nativeId, MonsterModel monster)
    {
        for (int node = 0; node < machine.Nodes.Length; node++)
        {
            if (machine.Nodes[node].NativeId == nativeId)
                return node;
        }
        throw new FlashImportException(FlashRejection.UnrepresentableState,
            $"{monster.Id.Entry} 的行动 {nativeId} 不在状态机中");
    }

    private static byte SlotForFlash(FlashRules rules, Creature creature)
    {
        if (creature.SlotName == null)
            return checked((byte)FlashKernel.NoSlot);
        for (int slot = 0; slot < rules.EncounterSlots.Count; slot++)
        {
            if (string.Equals(rules.EncounterSlots[slot], creature.SlotName, StringComparison.Ordinal))
                return checked((byte)slot);
        }
        throw new FlashImportException(FlashRejection.UnrepresentableState,
            $"遭遇没有槽位 {creature.SlotName}");
    }

    private static void ImportPowers(ref FlashBattle battle, SimulatedCombatState combat, Player player, IReadOnlyList<Creature> known,
        FlashStateProjections projections)
    {
        IReadOnlyList<PowerModel> powers = combat.EffectivePowers();
        // Group by owner index while keeping the existing listener order within each owner.
        List<(byte Owner, int Order, PowerModel Power)> ordered = [];
        for (int order = 0; order < powers.Count; order++)
        {
            PowerModel power = powers[order];
            if (power.Amount == 0)
                continue;
            ordered.Add((Owner(power.Owner, player, known), order, power));
        }
        ordered.Sort((left, right) => left.Owner != right.Owner ? left.Owner.CompareTo(right.Owner) : left.Order.CompareTo(right.Order));
        if (ordered.Count > FlashLimits.Powers)
            throw new FlashImportException(FlashRejection.Capacity, $"Power 数 {ordered.Count}");
        battle.PowerCount = (byte)ordered.Count;
        for (int index = 0; index < ordered.Count; index++)
        {
            (byte owner, _, PowerModel power) = ordered[index];
            if (!FlashRegistry.TryPower(power.Id.Entry, out ushort id))
                throw new FlashImportException(FlashRejection.UnsupportedPower, power.Id.Entry);
            FlashPowerDef def = FlashRegistry.Power(id);
            FlashPowerFlags flags = FlashPowerFlags.None;
            if (def.UsesNativeDurationSkip && power.SkipNextDurationTick)
                flags |= FlashPowerFlags.SkipNextDurationTick;
            if (combat.HasMonsterDurationSkipForFlash(power.Owner, power.GetType()))
                flags |= FlashPowerFlags.SkipNextTickFromMonster;
            if (def.MarksJustAppliedByEnemy && power is MegaCrit.Sts2.Core.Models.Powers.RitualPower { _wasJustAppliedByEnemy: true })
                flags |= FlashPowerFlags.WasJustAppliedByEnemy;
            battle.Powers[index] = new FlashPower
            {
                Id = id,
                Owner = owner,
                Applier = power.Applier == null ? FlashLimits.NoOwner : Owner(power.Applier, player, known),
                Target = power._target == null ? FlashLimits.NoOwner : Owner(power._target, player, known),
                Flags = flags,
                Amount = power.Amount,
                AmountOnTurnStart = power.AmountOnTurnStart,
            };
            projections.ProjectPower(power, ref battle.Powers[index]);
        }
    }

    /// <summary>
    /// The existing engine dispatches some hooks over the run listeners, which include the deck versions of cards;
    /// Flash dispatches over the combat listeners only, so a deck card implementing one of those hooks rejects the root.
    /// </summary>
    private static void RejectRunListenerDeckCards(Player player)
    {
        foreach (CardModel card in player.Deck.Cards)
        {
            if (FlashRegistry.TryCard(card.Id.Entry, out ushort id)
                && (FlashRegistry.Card(id).Hooks & FlashHookMask.RunListenerHooks) != 0)
                throw new FlashImportException(FlashRejection.UnsupportedCard, $"牌组卡 {card.Id.Entry} 带有局外监听 Hook");
        }
    }

    private static void ImportCards(ref FlashBattle battle, SimPlayerCombatState playerState, FlashRules rules)
    {
        (SimCardPile Pile, FlashPile Flash)[] piles =
        [
            (playerState.Hand, FlashPile.Hand),
            (playerState.DrawPile, FlashPile.Draw),
            (playerState.DiscardPile, FlashPile.Discard),
            (playerState.ExhaustPile, FlashPile.Exhaust),
            (playerState.PlayPile, FlashPile.Play),
        ];
        foreach ((SimCardPile pile, FlashPile target) in piles)
        {
            foreach (PredictedCard card in pile.Cards)
                FlashPiles.Append(ref battle, target, Card(card.Preview, rules));
        }
    }

    private static FlashCard Card(CardModel card, FlashRules rules)
    {
        ushort id = FlashRulesCompiler.CardId(card);
        int upgrade = card.CurrentUpgradeLevel;
        FlashCardStats stats = rules.Card(id, upgrade);
        if (card.Enchantment != null)
            throw new FlashImportException(FlashRejection.UnsupportedEnchantment, card.Enchantment.Id.Entry);
        if (card.Affliction != null)
            throw new FlashImportException(FlashRejection.UnsupportedAffliction, card.Affliction.Id.Entry);
        if (card.IsDupe)
            throw new FlashImportException(FlashRejection.UnsupportedCard, $"{card.Id.Entry} 复制品");

        FlashCard result = new()
        {
            Id = id,
            Upgrade = checked((byte)upgrade),
            Keywords = MapKeywords(card.Keywords),
            BaseCost = checked((sbyte)card.EnergyCost._base),
            BaseStarCost = checked((sbyte)card.BaseStarCost),
            ReplayCount = checked((byte)card.BaseReplayCount),
        };
        if (card.ExhaustOnNextPlay) result.Flags |= FlashCardFlags.ExhaustOnNextPlay;
        if (card.HasSingleTurnSly) result.Flags |= FlashCardFlags.SlyThisTurn;
        if (card.HasSingleTurnRetain) result.Flags |= FlashCardFlags.RetainThisTurn;
        if (card.DeckVersion != null) result.Flags |= FlashCardFlags.DeckCard;
        if (card.HasBeenRemovedFromState) result.Flags |= FlashCardFlags.RemovedFromState;
        if (card.EnergyCost.CostsX) result.Flags |= FlashCardFlags.CostsX;
        if (card.HasStarCostX) result.Flags |= FlashCardFlags.StarCostsX;

        var modifiers = card.EnergyCost._localModifiers;
        if (modifiers.Count > FlashLimits.CardEnergyCostModifiers)
            throw new FlashImportException(FlashRejection.Capacity, $"{card.Id.Entry} 费用修饰 {modifiers.Count}");
        for (int index = 0; index < modifiers.Count; index++)
        {
            LocalCostModifier modifier = modifiers[index];
            result.EnergyModifiers[index] = FlashCostModifier.Create(modifier.Amount, (FlashCostType)(byte)modifier.Type,
                (FlashCostExpiration)(byte)modifier.Expiration, modifier.IsReduceOnly);
        }
        result.EnergyModifierCount = modifiers.Count;
        var starCosts = card._temporaryStarCosts;
        if (starCosts.Count > 1)
            throw new FlashImportException(FlashRejection.Capacity, $"{card.Id.Entry} 临时星能费用 {starCosts.Count}");
        if (starCosts.Count == 1)
            result.SetTemporaryStarCost(starCosts[0].Cost, starCosts[0].ClearsWhenTurnEnds, starCosts[0].ClearsWhenCardIsPlayed);

        FlashCardDef def = FlashRegistry.Card(id);
        for (int index = 0; index < def.VarNames.Count; index++)
        {
            decimal current = card.DynamicVars[def.VarNames[index]].BaseValue;
            decimal delta = current - stats.Vars[index];
            if (delta == 0)
                continue;
            if (index > 1 || delta != decimal.Truncate(delta) || delta < short.MinValue || delta > short.MaxValue)
                throw new FlashImportException(FlashRejection.UnrepresentableState, $"{card.Id.Entry} 变量 {def.VarNames[index]} 偏移 {delta}");
            if (index == 0) result.Var0 = (short)delta;
            else result.Var1 = (short)delta;
        }
        return result;
    }

    private static void ImportRelics(ref FlashBattle battle, SimulatedCombatState combat, Player player,
        FlashStateProjections projections)
    {
        IReadOnlyList<RelicModel> relics = combat.RelicsOf(player);
        if (relics.Count > FlashLimits.Relics)
            throw new FlashImportException(FlashRejection.Capacity, $"遗物数 {relics.Count}");
        battle.RelicCount = (byte)relics.Count;
        for (int index = 0; index < relics.Count; index++)
        {
            RelicModel relic = relics[index];
            if (!FlashRegistry.TryRelic(relic.Id.Entry, out ushort id))
                throw new FlashImportException(FlashRejection.UnsupportedRelic, relic.Id.Entry);
            battle.Relics[index] = new FlashRelic
            {
                Id = id,
                Flags = relic.IsMelted ? FlashRelicFlags.Melted : FlashRelicFlags.None,
            };
            projections.ProjectRelic(relic, ref battle.Relics[index]);
        }
    }

    private static void ImportPotions(ref FlashBattle battle, SimulatedCombatState combat, Player player)
    {
        int slots = combat.PotionSlotCountForFlash(player);
        if (slots > FlashLimits.PotionSlots)
            throw new FlashImportException(FlashRejection.Capacity, $"药水槽 {slots}");
        battle.PotionSlotCount = (byte)slots;
        for (int slot = 0; slot < slots; slot++)
        {
            PotionModel? potion = combat.GetPotionAtSlot(player, slot);
            if (potion == null)
                continue;
            if (!FlashRegistry.TryPotion(potion.Id.Entry, out ushort id))
                throw new FlashImportException(FlashRejection.UnsupportedPotion, potion.Id.Entry);
            battle.Potions[slot] = (ushort)(id + 1);
        }
    }

    private static void ImportOrbs(ref FlashBattle battle, SimPlayerCombatState playerState)
    {
        SimOrbQueue orbs = playerState.OrbQueue;
        if (orbs.Orbs.Count > 0)
            throw new FlashImportException(FlashRejection.UnsupportedOrb, orbs.Orbs[0].Id.Entry);
        battle.OrbCapacity = checked((byte)orbs.Capacity);
    }

    private static byte Owner(Creature creature, Player player, IReadOnlyList<Creature> known)
    {
        if (ReferenceEquals(creature, player.Creature))
            return FlashLimits.PlayerOwner;
        int index = IndexOf(known, creature);
        return index >= 0
            ? FlashKernel.EnemyOwner(index)
            : throw new FlashImportException(FlashRejection.UnrepresentableState, $"未知生物 {creature.Name}");
    }

    private static int IndexOf(IReadOnlyList<Creature> creatures, Creature creature)
    {
        for (int index = 0; index < creatures.Count; index++)
        {
            if (ReferenceEquals(creatures[index], creature))
                return index;
        }
        return -1;
    }
}
