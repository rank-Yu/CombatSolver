using CombatSolver.Engine.Common;
using CombatSolver.Flash.Kernel;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Flash.Search;
using CombatSolver.Flash.Search.Scoring;

namespace CombatSolver.Flash.Bridge;

/// <summary>
/// Projects the existing engine's branch state into <see cref="FlashBattle"/>. The same projection
/// imports search roots and checks differential steps, so every value that affects later
/// resolution either maps into Flash or rejects the state.
/// </summary>
internal static class FlashStateImporter
{
    public static FlashBattle Import(SimulationSnapshot snapshot, Player player, FlashRules rules,
        int skippedPotionUses, bool requirePlayPhase = false)
        => Import(snapshot.Simulator, player, rules, snapshot.ShufflesCrossed, snapshot.ProcessedEnemyDeaths,
            snapshot.HasRisk, skippedPotionUses, requirePlayPhase, snapshot.BoundaryReason);

    public static FlashBattle Import(CombatPredictionSimulator simulator, Player player, FlashRules rules,
        int shufflesCrossed, IReadOnlySet<uint> processedDeaths, bool hasRisk, int skippedPotionUses,
        bool requirePlayPhase = false, SearchBoundaryReason boundary = SearchBoundaryReason.None)
    {
        // The existing engine's per-turn counter getters fill their caches from root history on first
        // read. Reading a private fork keeps the caller's state, which other workers may fork, untouched.
        simulator = simulator.Fork();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (combat.Players.Count != 1)
            throw new FlashImportException(FlashRejection.MultiPlayer, $"玩家数 {combat.Players.Count}");
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        // FlashBattle only represents a stable play-phase state. A turn-setup request is admitted by
        // the bridge, which replays the native setup page through the existing engine and imports each
        // resulting play root, so reaching this guard with Start/other phases is a protocol error.
        if (requirePlayPhase
            && (combat.CurrentSide != CombatSide.Player || playerState.Phase != PlayerTurnPhase.Play))
        {
            throw new FlashImportException(FlashRejection.RootPhase,
                $"根不在玩家出牌阶段：side={combat.CurrentSide} phase={playerState.Phase}");
        }
        if (simulator.HasPendingChoice)
            throw new FlashImportException(FlashRejection.PendingChoice, "模拟器有待处理选择");
        if (combat.DescribeFlashUnrepresentableState() is { } unrepresentable)
            throw new FlashImportException(FlashRejection.UnrepresentableState, unrepresentable);
        // The dummy timeout flag has exactly one represented value: the EventDefeat boundary. A flag
        // with any other boundary means the caller lost the search boundary or the state is inconsistent.
        if (combat.BattlewornDummyTimedOut && boundary != SearchBoundaryReason.EventDefeat)
        {
            throw new FlashImportException(FlashRejection.UnrepresentableState,
                "战损假人已超时但导入边界不是 EventDefeat");
        }
        FlashStateProjections projections = new(simulator.StateStore, simulator.State, combat);

        FlashBattle battle = default;

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
        // EventDefeat is not a TerminalStamp in the existing engine: it is a search boundary recorded
        // after the dummy's time limit expires while the combat is still in progress.
        battle.Outcome = boundary == SearchBoundaryReason.EventDefeat
            ? FlashOutcome.EventDefeat
            : simulator.TerminalStamp is not { } stamp
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
        // snapshot.HasRisk false. Discovery/Splash are in that catalog, so the kernel never raises
        // the flag for their generated options; only the imported root's risk is represented.
        if (hasRisk)
            battle.Flags |= FlashBattleFlags.HasRisk;
        battle.MaxHandSize = checked((byte)combat.GetMaxHandSize(player));

        battle.Player = Vitals(simulator.State.GetCreature(player.Creature), combat.HasLostHpThisTurn(player.Creature));
        Creature? osty = simulator.State.GetOsty(player);
        if (osty != null)
        {
            // The pet is one of the player-side creatures. Player.Osty is the native pet field: it is
            // non-null only for a pet that already existed when the root was captured, and the existing
            // engine reads it directly for per-turn resets (BeginSideTurn).
            battle.Osty = Vitals(simulator.State.GetCreature(osty), combat.HasLostHpThisTurn(osty));
            battle.Flags |= FlashBattleFlags.OstyPresent;
            if (player.Osty != null)
                battle.Flags |= FlashBattleFlags.OstyWasRootPet;
        }

        ImportCounters(ref battle, simulator, combat, player, combat.KnownEnemies, osty);

        ImportRng(ref battle, simulator.Rng);
        IReadOnlyList<Creature> known = combat.KnownEnemies;
        ImportEnemies(ref battle, simulator, combat, player, known, rules, processedDeaths, projections);
        ImportPowers(ref battle, combat, player, known, osty, projections, rules);
        ImportHistoryCourse(ref battle, combat, player, rules);
        ImportCards(ref battle, combat, playerState, rules);
        RejectRunListenerDeckCards(player);
        ImportRelics(ref battle, combat, player, projections);
        ImportPotions(ref battle, combat, player, rules, skippedPotionUses);
        ImportOrbs(ref battle, playerState);
        if (rules.HasDecimalCardVars && !FlashDecimalVars.TryCanonicalize(ref battle, out _))
            throw new FlashImportException(FlashRejection.Capacity, "卡牌小数变量表");
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
        SimulatedCombatState combat, Player player, IReadOnlyList<Creature> known, Creature? osty)
    {
        // MagiKnight's DampenPower keeps its caster set in SimulatedCombatState._dampenCasters; the
        // kernel stores the same set as a bitmask of enemy-owner indices.
        int dampenMask = 0;
        foreach ((Creature target, Creature caster) in combat.DampenCastersForFlash())
        {
            if (!ReferenceEquals(target, player.Creature))
                throw new FlashImportException(FlashRejection.UnrepresentableState, "Dampen 目标不是玩家");
            int casterIndex = IndexOf(known, caster);
            if (casterIndex < 0 || casterIndex >= 32)
                throw new FlashImportException(FlashRejection.UnrepresentableState, "Dampen 施法者不在敌人名单");
            dampenMask |= 1 << casterIndex;
        }
        battle.SetCounter(FlashCounter.DampenCasterMask, dampenMask);
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
        battle.SetCounter(FlashCounter.StarsGainedThisTurn, combat.GetStarsGainedThisTurn(player));
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
        ImportGrowthRewards(ref battle, combat.GrowthRewards);
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
        battle.SetCounter(FlashCounter.VitalSparkNormalizedAmount,
            combat.LastNormalizedVitalSparkAmountForFlash);
        battle.SetCounter(FlashCounter.SkillPlayedThisSideTurn,
            combat.HasPlayedSkillThisTurn(owner) ? 1 : 0);
        // CalculatedVarSpecRegistry.CountFinishedCardPlays: the root history plus the predicted
        // CardPlayFinished entries (the latter are counted by the search's counter during play).
        int finishedPlays = combat.GetFinishedPlaysBeforePrediction();
        foreach (CombatPredictionHistoryEntry entry in simulator.History)
        {
            if (entry is CombatPredictionCardPlayFinishedEntry)
                finishedPlays++;
        }
        battle.SetCounter(FlashCounter.FinishedCardPlays, finishedPlays);
        battle.SetCounter(FlashCounter.CardsDrawn, CalculatedVarSpecRegistry.CountDrawnCardsForFlash(simulator, player));
        battle.SetCounter(FlashCounter.LightningChannels,
            CalculatedVarSpecRegistry.CountLightningChannelsForFlash(simulator, player));
        // SimulatedCombatState.GetCreatureAttacksThisTurn(osty): the Rattle calculated-hit multiplier.
        // The per-turn maps are filled from root history on first read.
        battle.SetCounter(FlashCounter.OstyAttacksThisTurn,
            osty != null ? combat.GetCreatureAttacksThisTurn(osty) : 0);
        battle.SetCounter(FlashCounter.DoomAppliedThisTurn,
            combat.WasDoomAppliedThisTurn(player.Creature) ? 1 : 0);
        // CalculatedVarSpecRegistry.CountEtherealPlays: the root history plus predicted finished plays
        // whose card was Ethereal when played (CardPlayFinishedEntry.WasEthereal).
        int etherealPlays = combat.GetEtherealPlaysBeforePrediction(player);
        foreach (CombatPredictionHistoryEntry entry in simulator.History)
        {
            if (entry is CombatPredictionCardPlayFinishedEntry finished
                && finished.WasEthereal
                && ReferenceEquals(finished.CardPlay.Player, player))
            {
                etherealPlays++;
            }
        }
        battle.SetCounter(FlashCounter.EtherealPlays, etherealPlays);
        battle.SetCounter(FlashCounter.CardsGeneratedThisCombat,
            CalculatedVarSpecRegistry.CountGeneratedCardsForFlash(simulator, player));
        int orbChannels = 0;
        foreach (CombatPredictionHistoryEntry entry in simulator.History)
        {
            if (entry is CombatPredictionOrbChanneledEntry)
                orbChannels = checked(orbChannels + 1);
        }
        battle.SetCounter(FlashCounter.OrbChannels, orbChannels);
        // Theft: SimulatedCombatState's outstanding-stolen-resource ledger, read without
        // materializing its lazy cache (the caller may be forked concurrently).
        (int stolenGold, int stolenCards) = combat.UnrecoveredLootForFlash(simulator);
        battle.SetCounter(FlashCounter.OutstandingStolenGold, stolenGold);
        battle.SetCounter(FlashCounter.OutstandingStolenCards, stolenCards);
    }

    /// <summary>
    /// Projects <c>SimulatedCombatState._growthRewards</c> into the packed growth counters. Every
    /// built-in source fits two 16-bit slots; a larger root count or a third-party source cannot be
    /// represented and rejects the state instead of truncating.
    /// </summary>
    private static void ImportGrowthRewards(ref FlashBattle battle, GrowthValues rewards)
    {
        if (!rewards.Extras.IsEmpty)
            throw new FlashImportException(FlashRejection.UnrepresentableState,
                "第三方成长来源的累计收益");
        Set(ref battle, FlashGrowthSource.HandOfGreed, rewards.HandOfGreed);
        Set(ref battle, FlashGrowthSource.TheHunt, rewards.TheHunt);
        Set(ref battle, FlashGrowthSource.Feed, rewards.Feed);
        Set(ref battle, FlashGrowthSource.Royalties, rewards.Royalties);
        Set(ref battle, FlashGrowthSource.Alchemize, rewards.Alchemize);
        Set(ref battle, FlashGrowthSource.GeneticAlgorithm, rewards.GeneticAlgorithm);
        Set(ref battle, FlashGrowthSource.TheScythe, rewards.TheScythe);
        Set(ref battle, FlashGrowthSource.Goopy, rewards.Goopy);
        Set(ref battle, FlashGrowthSource.ForbiddenGrimoire, rewards.ForbiddenGrimoire);
        Set(ref battle, FlashGrowthSource.MadScience, rewards.MadScience);

        static void Set(ref FlashBattle target, FlashGrowthSource source, int value)
        {
            if ((uint)value > ushort.MaxValue)
                throw new FlashImportException(FlashRejection.UnrepresentableState,
                    $"成长来源 {source} 的累计收益 {value}");
            FlashGrowthRewards.Set(ref target, source, value);
        }
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
        SimulatedCombatState combat, Player player, IReadOnlyList<Creature> known, FlashRules rules,
        IReadOnlySet<uint> processedDeaths, FlashStateProjections projections)
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
                // A reviving Illusion is always in its synthetic REVIVE_MOVE (handled above). A
                // Reattach segment instead revives from the native DEAD_MOVE/REATTACH_MOVE nodes, so a
                // non-illusion reviving phase is a normal node.
                if (enemy.DeathPhase == FlashDeathPhase.Reviving && FlashRegistry.Monster(monsterId).IllusionRevive)
                    throw new FlashImportException(FlashRejection.UnrepresentableState,
                        $"{monster.Id.Entry} 处于复活状态但不在复活行动中");
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
            // SimulatedCombatState.GetPoweredAttackHitsThisTurn(player, enemy): BeatIntoShape's
            // calculated-forge multiplier. The per-turn map is filled from root history on first read
            // on the importer's private fork, so the caller's simulator is not mutated.
            int poweredHits = combat.GetPoweredAttackHitsThisTurn(player.Creature, creature);
            if (poweredHits > byte.MaxValue)
                throw new FlashImportException(FlashRejection.UnrepresentableState,
                    $"{monster.Id.Entry} 的本回合强化攻击命中数 {poweredHits} 超过 255");
            enemy.PoweredAttackHitsThisTurn = checked((byte)poweredHits);
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
        // SimulatedCombatState.GetSlotIndex returns -1 for a slot name outside _encounterSlots; an
        // EncounterModel.Slots list can be empty while GenerateMonsters still names slots (Bowlbugs,
        // SlumberingBeetle), and no slot order may be invented for them.
        return checked((byte)FlashKernel.NoSlot);
    }

    private static void ImportPowers(ref FlashBattle battle, SimulatedCombatState combat, Player player,
        IReadOnlyList<Creature> known, Creature? osty, FlashStateProjections projections, FlashRules rules)
    {
        IReadOnlyList<PowerModel> powers = combat.EffectivePowers();
        // Group by owner index while keeping the existing listener order within each owner.
        List<(byte Owner, int Order, PowerModel Power)> ordered = [];
        for (int order = 0; order < powers.Count; order++)
        {
            PowerModel power = powers[order];
            if (power.Amount == 0)
                continue;
            ordered.Add((Owner(power.Owner, player, known, osty), order, power));
        }
        ordered.Sort((left, right) => left.Owner != right.Owner ? left.Owner.CompareTo(right.Owner) : left.Order.CompareTo(right.Order));
        if (ordered.Count > FlashLimits.Powers)
            throw new FlashImportException(FlashRejection.Capacity, $"Power 数 {ordered.Count}");
        battle.PowerCount = (byte)ordered.Count;
        for (int index = 0; index < ordered.Count; index++)
        {
            (byte owner, _, PowerModel power) = ordered[index];
            if (!FlashRegistry.TryPower(power.Id.Entry, out ushort id))
                throw FlashUnsupportedContent.Power(power);
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
                Applier = power.Applier == null ? FlashLimits.NoOwner : Owner(power.Applier, player, known, osty),
                Target = power._target == null ? FlashLimits.NoOwner : Owner(power._target, player, known, osty),
                Flags = flags,
                Amount = power.Amount,
                AmountOnTurnStart = def.UsesAmountOnTurnStart ? power.AmountOnTurnStart : 0,
            };
            projections.ProjectPower(power, ref battle.Powers[index]);
            if (power is NightmarePower nightmare)
                ImportNightmareSelection(ref battle, combat, nightmare, rules, index);
            if (power is SwipePower swipe)
                ImportSwipeSelection(ref battle, swipe, rules, index);
        }
    }

    /// <summary>
    /// NightmarePower's selected card is a detached clone (SetNightmareSelection uses
    /// <c>PredictedCard.CreateClone</c> + ClearAffliction). Flash stores its full value in the battle's
    /// detached-card table and references it from Aux0.
    /// </summary>
    private static void ImportNightmareSelection(ref FlashBattle battle, SimulatedCombatState combat,
        NightmarePower power, FlashRules rules, int row)
    {
        PredictedCard? selected = combat.NightmareSelectionForFlash(power);
        if (selected == null)
            throw new FlashImportException(FlashRejection.UnrepresentableState, "Nightmare 选定卡缺失");
        FlashCard value = Card(ref battle, selected, rules, rootCard: false, dampenOriginalUpgrade: -1);
        for (int slot = 0; slot < FlashLimits.DetachedCards; slot++)
        {
            if (battle.DetachedCards[slot].Id != 0)
                continue;
            battle.DetachedCards[slot] = value;
            battle.Powers[row].Aux0 = slot + 1;
            return;
        }
        throw new FlashImportException(FlashRejection.Capacity, $"Nightmare 选定卡 {FlashLimits.DetachedCards}");
    }

    /// <summary>
    /// SwipePower.StolenCard is the removed card's full value. Flash keeps it in the detached-card
    /// table, referenced by Aux0 = slot + 1; a missing card leaves Aux0 zero, matching the native
    /// null check in RecoverStolenResources and KnownNativeHealingPotential.
    /// </summary>
    private static void ImportSwipeSelection(ref FlashBattle battle, SwipePower power, FlashRules rules, int row)
    {
        if (power.StolenCard == null)
            return;
        FlashCard value = Card(ref battle, new PredictedCard(power.StolenCard), rules, rootCard: true, dampenOriginalUpgrade: -1);
        for (int slot = 0; slot < FlashLimits.DetachedCards; slot++)
        {
            if (battle.DetachedCards[slot].Id != 0)
                continue;
            battle.DetachedCards[slot] = value;
            battle.Powers[row].Aux0 = slot + 1;
            return;
        }
        throw new FlashImportException(FlashRejection.Capacity, $"SwipePower 被偷卡 {FlashLimits.DetachedCards}");
    }

    /// <summary>
    /// The existing engine dispatches some hooks over the run listeners, which include the deck versions of cards;
    /// Flash dispatches over the combat listeners only, so a deck card implementing one of those hooks rejects the root.
    /// A definition may declare its out-of-combat deck copy inert when the existing handler provably does nothing
    /// for a card that is not in a combat pile (Melancholy's <c>AfterDeath</c>).
    /// </summary>
    private static void RejectRunListenerDeckCards(Player player)
    {
        foreach (CardModel card in player.Deck.Cards)
        {
            if (!FlashRegistry.TryCard(card.Id.Entry, out ushort id))
                continue;
            FlashCardDef def = FlashRegistry.Card(id);
            if ((def.Hooks & FlashHookMask.RunListenerHooks) != 0 && !def.DeckCopyOutsideCombatIsInert)
                throw new FlashImportException(FlashRejection.UnsupportedCard, $"牌组卡 {card.Id.Entry} 带有局外监听 Hook");
        }
    }

    /// <summary>
    /// The HistoryCourse cross-turn slot: <c>SimulatedCombatState._lastAttackThisTurn</c> projected as
    /// one full card value. The retired previous-turn dictionary is deliberately not read: AutoPrePlay
    /// consumes it before every supported root (Play phase or the post-setup Play root), and the kernel
    /// clears it when it consumes the slot. Called before <see cref="ImportCards"/> so the decimal-var
    /// canonicalization order is detached cards, this slot, then the card storage order.
    /// </summary>
    private static void ImportHistoryCourse(ref FlashBattle battle, SimulatedCombatState combat,
        Player player, FlashRules rules)
    {
        if (!rules.HasHistoryCourse)
            return;
        if (combat.LastAttackThisTurnForFlash(player) is not { } last)
            return;
        if (last.Preview.IsDupe)
        {
            throw new FlashImportException(FlashRejection.UnrepresentableState,
                $"HistoryCourse 的上一张攻击是复制品 {last.Preview.Id.Entry}");
        }
        int dampenLevel = -1;
        foreach ((PredictedCard card, int level) in combat.DampenOriginalUpgradesForFlash())
        {
            if (!ReferenceEquals(card, last))
                continue;
            dampenLevel = level;
            break;
        }
        FlashCard value = Card(ref battle, last, rules, combat.IsRootCardForFlash(last),
            dampenLevel);
        if (last.Preview is MegaCrit.Sts2.Core.Models.Cards.SovereignBlade)
        {
            int? applied = combat.SwordSageAppliedForFlash(last);
            if (applied == null)
                throw new FlashImportException(FlashRejection.UnrepresentableState,
                    $"SwordSage 账本缺少 HistoryCourse 的 {last.Preview.Id.Entry}");
            value.Private = checked((short)applied.Value);
        }
        else if (combat.FetchPlayedThisTurnForFlash(last))
        {
            value.Private |= FlashKernel.FetchPlayedBit;
        }
        if (combat.PendingReturningCards.Any(card => ReferenceEquals(card, last)))
            value.ReturnToHandNextTurn = true;
        battle.HistoryCourseCard = value;
        battle.Flags |= FlashBattleFlags.HistoryCourseCardPresent;
    }

    private static void ImportCards(ref FlashBattle battle, SimulatedCombatState combat,
        SimPlayerCombatState playerState, FlashRules rules)
    {
        Dictionary<PredictedCard, int> dampen = new(ReferenceEqualityComparer.Instance);
        foreach ((PredictedCard card, int level) in combat.DampenOriginalUpgradesForFlash())
            dampen[card] = level;
        HashSet<PredictedCard> imported = new(ReferenceEqualityComparer.Instance);
        (SimCardPile Pile, FlashPile Flash)[] piles =
        [
            (playerState.Hand, FlashPile.Hand),
            (playerState.DrawPile, FlashPile.Draw),
            (playerState.DiscardPile, FlashPile.Discard),
            (playerState.ExhaustPile, FlashPile.Exhaust),
            (playerState.PlayPile, FlashPile.Play),
        ];
        // SimulatedCombatState._returnToHandNextTurn: the exact card objects that return at this
        // turn's BeforeHandDraw (Bolas/ThrummingHatchet, including a Start root captured before the
        // return). Flash carries the membership on the card value.
        HashSet<PredictedCard> returning = new(combat.PendingReturningCards, ReferenceEqualityComparer.Instance);
        foreach ((SimCardPile pile, FlashPile target) in piles)
        {
            foreach (PredictedCard card in pile.Cards)
            {
                imported.Add(card);
                int originalUpgrade = dampen.TryGetValue(card, out int level) ? level : -1;
                FlashCard value = Card(ref battle, card, rules, combat.IsRootCardForFlash(card), originalUpgrade);
                if (card.Preview is MegaCrit.Sts2.Core.Models.Cards.SovereignBlade)
                {
                    int? applied = combat.SwordSageAppliedForFlash(card);
                    if (applied == null)
                        throw new FlashImportException(FlashRejection.UnrepresentableState,
                            $"SwordSage 账本缺少 {card.Preview.Id.Entry}");
                    value.Private = checked((short)applied.Value);
                }
                else if (combat.FetchPlayedThisTurnForFlash(card))
                {
                    value.Private |= FlashKernel.FetchPlayedBit;
                }
                if (returning.Contains(card))
                    value.ReturnToHandNextTurn = true;
                FlashPiles.Append(ref battle, target, value);
            }
        }
        foreach (PredictedCard card in dampen.Keys)
        {
            if (imported.Contains(card))
                continue;
            // A transformed/removed card leaves the combat piles but its downgraded model stays in
            // SimulatedCombatState._dampenOriginalUpgrades. RestoreDampenedCards later upgrades that
            // object and drops the record; HasBeenRemovedFromState makes CardPileCmd.Add refuse to put
            // it back in a pile, so the model value has no further settlement effect. Anything else
            // outside the piles could re-enter combat with a downgraded level and is rejected.
            if (card.Preview.HasBeenRemovedFromState)
                continue;
            throw new FlashImportException(FlashRejection.UnrepresentableState,
                $"Dampen 记录不在牌堆中：{card.Preview.Id.Entry}");
        }
    }

    private static FlashCard Card(ref FlashBattle battle, PredictedCard predicted, FlashRules rules, bool rootCard,
        int dampenOriginalUpgrade)
    {
        CardModel card = predicted.Preview;
        ushort id = FlashRulesCompiler.CardId(card);
        int upgrade = card.CurrentUpgradeLevel;
        FlashCardStats stats = rules.Card(id, upgrade);
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
            IsClone = card.IsClone,
        };
        if (FlashRegistry.Card(id).UsesIdentityLedger)
            result.Private = FlashLimits.UninitializedLedger;
        if (dampenOriginalUpgrade > 3)
            throw new FlashImportException(FlashRejection.UnrepresentableState,
                $"{card.Id.Entry} 压制前升级等级 {dampenOriginalUpgrade}");
        if (dampenOriginalUpgrade >= 0)
            result.DampenOriginalUpgrade = checked((byte)(dampenOriginalUpgrade + 1));
        // Root cards were present at _liveCardsAtSnapshot, so the one-time check can never apply to
        // them; marking them checked is equivalent to the existing engine's first normalization and
        // keeps their bytes stable while an auto-play queue holds them. Generated cards copy the
        // actual flag and are distinguished by CreatedAfterRoot.
        if (rootCard || predicted.HasCheckedPowerAfflictionEntry)
            result.Flags |= FlashCardFlags.PowerAfflictionChecked;
        if (!rootCard)
            result.Keywords |= FlashKeywords.CreatedAfterRoot;
        if (card.Enchantment != null)
        {
            EnchantmentModel enchantment = card.Enchantment;
            if (!FlashRegistry.TryEnchantment(enchantment.Id.Entry, out ushort enchantmentId))
                throw new FlashImportException(FlashRejection.UnsupportedEnchantment, enchantment.Id.Entry);
            if ((uint)enchantment.Amount > short.MaxValue || enchantment.Amount < short.MinValue)
                throw new FlashImportException(FlashRejection.UnrepresentableState,
                    $"{card.Id.Entry} 附魔层数 {enchantment.Amount}");
            if ((uint)enchantment.Status > 1)
                throw new FlashImportException(FlashRejection.UnrepresentableState,
                    $"{card.Id.Entry} 附魔状态 {(int)enchantment.Status}");
            result.EnchantmentId = checked((byte)(enchantmentId + 1));
            result.EnchantmentAmount = (short)enchantment.Amount;
            result.EnchantmentStatus = (byte)enchantment.Status;
            if (EnchantmentStateSupport.TryGetPrivateValue(enchantment, out int privateValue))
            {
                if (privateValue is < short.MinValue or > short.MaxValue)
                    throw new FlashImportException(FlashRejection.UnrepresentableState,
                        $"{card.Id.Entry} 附魔私有值 {privateValue}");
                result.EnchantmentPrivate = (short)privateValue;
            }
        }
        if (card.Affliction != null)
        {
            AfflictionModel affliction = card.Affliction;
            if (!FlashRegistry.TryAffliction(affliction.Id.Entry, out ushort afflictionId))
                throw new FlashImportException(FlashRejection.UnsupportedAffliction, affliction.Id.Entry);
            if (affliction.Amount is < short.MinValue or > short.MaxValue)
                throw new FlashImportException(FlashRejection.UnrepresentableState,
                    $"{card.Id.Entry} 苦难层数 {affliction.Amount}");
            result.AfflictionId = checked((byte)(afflictionId + 1));
            result.AfflictionAmount = (short)affliction.Amount;
        }
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
        if (starCosts.Count > 0)
        {
            // Flash keeps the current entry plus up to two older entries with the same cost,
            // preserving their clear flags. CurrentStarCost reads the last entry; cleanup removes
            // whole expiration classes and exposes the next older entry. Different costs are not
            // representable (the single TemporaryStarCost value is shared by all entries).
            int cost = starCosts[0].Cost;
            bool allSame = true;
            for (int index = 1; index < starCosts.Count; index++)
            {
                TemporaryCardCost entry = starCosts[index];
                if (entry.Cost != cost
                    || entry.ClearsWhenTurnEnds != starCosts[0].ClearsWhenTurnEnds
                    || entry.ClearsWhenCardIsPlayed != starCosts[0].ClearsWhenCardIsPlayed)
                {
                    allSame = false;
                    break;
                }
            }
            if (starCosts.Count > 3 && !allSame)
            {
                throw new FlashImportException(FlashRejection.Capacity,
                    $"{card.Id.Entry} 临时星能费用条目 {starCosts.Count} 条");
            }
            if (allSame)
            {
                result.SetTemporaryStarCost(cost, starCosts[0].ClearsWhenTurnEnds,
                    starCosts[0].ClearsWhenCardIsPlayed);
            }
            else
            {
                for (int index = 0; index < starCosts.Count; index++)
                {
                    TemporaryCardCost entry = starCosts[index];
                    if (entry.Cost != cost)
                    {
                        throw new FlashImportException(FlashRejection.Capacity,
                            $"{card.Id.Entry} 临时星能费用条目成本不一致（{starCosts.Count} 条）");
                    }
                    result.SetTemporaryStarCost(entry.Cost, entry.ClearsWhenTurnEnds,
                        entry.ClearsWhenCardIsPlayed);
                }
            }
        }

        FlashCardDef def = FlashRegistry.Card(id);
        for (int index = 0; index < def.VarNames.Count; index++)
        {
            decimal current = card.DynamicVars[def.VarNames[index]].BaseValue;
            decimal delta = current - stats.Vars[index];
            if (delta == 0)
                continue;
            if (index == 0 && def.UsesDecimalDamageVar)
            {
                // At the native clamp the private ExtraDamage may exceed the observable BaseValue
                // offset, so downgrade/upgrade cannot be reproduced from the projection.
                if (current >= FlashLimits.ValueClamp)
                    throw new FlashImportException(FlashRejection.UnrepresentableState,
                        $"{card.Id.Entry} 的小数伤害变量已到 DynamicVar.BaseValue 的 999999999 钳制上限");
                if (!FlashDecimalVars.TryAllocate(ref battle, delta, out short encoded))
                    throw new FlashImportException(FlashRejection.Capacity,
                        $"{card.Id.Entry} 小数变量 {def.VarNames[index]}（需要更多同时并存的值）");
                result.Private = encoded;
                continue;
            }
            if (index > 1 || delta != decimal.Truncate(delta) || delta < short.MinValue || delta > short.MaxValue)
                throw new FlashImportException(FlashRejection.UnrepresentableState, $"{card.Id.Entry} 变量 {def.VarNames[index]} 偏移 {delta}");
            if (index == 0) result.Var0 = (short)delta;
            else result.Var1 = (short)delta;
        }
        def.ProjectPrivate(ref result);
        // MadScience's native Type and rider are saved properties, not dynamic variables; they select
        // the card's whole effect and its result location (Power removes the card from combat).
        if (card is MegaCrit.Sts2.Core.Models.Cards.MadScience madScience)
        {
            if (madScience.TinkerTimeType is not (CardType.Attack or CardType.Skill or CardType.Power))
                throw new FlashImportException(FlashRejection.UnrepresentableState,
                    $"{card.Id.Entry} 的 TinkerTimeType {(int)madScience.TinkerTimeType}");
            result.Private = CombatSolver.Flash.Content.Cards.Event.MadScience.Encode(
                (int)madScience.TinkerTimeType, (int)madScience.TinkerTimeRider);
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

    private static void ImportPotions(ref FlashBattle battle, SimulatedCombatState combat, Player player,
        FlashRules rules, int skippedPotionUses)
    {
        int slots = combat.PotionSlotCountForFlash(player);
        if (slots > FlashLimits.PotionSlots)
            throw new FlashImportException(FlashRejection.Capacity, $"药水槽 {slots}");
        battle.PotionSlotCount = (byte)slots;
        int freeMask = 0;
        for (int slot = 0; slot < slots; slot++)
        {
            PotionModel? potion = combat.GetPotionAtSlot(player, slot);
            if (potion == null)
                continue;
            if (!FlashRegistry.TryPotion(potion.Id.Entry, out ushort id))
                throw new FlashImportException(FlashRejection.UnsupportedPotion, potion.Id.Entry);
            battle.Potions[slot] = (ushort)(id + 1);
            if (combat.IsFreeEntropicPotionAtSlot(player, slot))
                freeMask |= 1 << slot;
        }
        battle.SetCounter(FlashCounter.EntropicFreePotionMask, freeMask);
        // PotionUses records every consumed potion. After skipping the uses a fixed prefix already
        // spent, a use is generated when its slot does not hold the search root's potion (the same
        // rule the kernel uses); freeFromEntropic uses already carry cost 0.
        int generatedUses = 0, generatedExplicitCost = 0, generatedAutomaticCost = 0;
        int useIndex = 0;
        foreach (PredictedPotionUse use in combat.PotionUses)
        {
            // A fixed prefix uses potions before the Flash search root. Those uses are already
            // frozen in Initial*Potion* by the scorer; only uses after the root can be "generated"
            // relative to the search root's slots.
            if (useIndex++ < skippedPotionUses)
                continue;
            if (!FlashRegistry.TryPotion(use.PotionId, out ushort usedId))
                throw new FlashImportException(FlashRejection.UnsupportedPotion, use.PotionId);
            if (rules.RootPotion(use.Slot) == usedId + 1)
                continue;
            generatedUses++;
            int cost = Math.Max(0, use.StrategicHpCost);
            if (use.Automatic)
                generatedAutomaticCost += cost;
            else
                generatedExplicitCost += cost;
        }
        if (generatedExplicitCost > 0xFFFF || generatedAutomaticCost > 0xFFFF)
            throw new FlashImportException(FlashRejection.UnrepresentableState, "生成药水战略成本超出打包范围");
        battle.SetCounter(FlashCounter.GeneratedPotionUseCount, generatedUses);
        battle.SetCounter(FlashCounter.GeneratedPotionStrategicCost,
            generatedExplicitCost | (generatedAutomaticCost << 16));
    }

    private static void ImportOrbs(ref FlashBattle battle, SimPlayerCombatState playerState)
    {
        SimOrbQueue orbs = playerState.OrbQueue;
        if (orbs.Capacity < 0 || orbs.Capacity > FlashLimits.Orbs)
            throw new FlashImportException(FlashRejection.Capacity, $"球槽 {orbs.Capacity}");
        if (orbs.Orbs.Count > orbs.Capacity)
            throw new FlashImportException(FlashRejection.UnrepresentableState,
                $"球数 {orbs.Orbs.Count} 超过容量 {orbs.Capacity}");
        if (orbs.Orbs.Count > FlashLimits.Orbs)
            throw new FlashImportException(FlashRejection.Capacity, $"球数 {orbs.Orbs.Count}");
        battle.OrbCapacity = checked((byte)orbs.Capacity);
        battle.OrbCount = checked((byte)orbs.Orbs.Count);
        for (int index = 0; index < orbs.Orbs.Count; index++)
        {
            OrbModel orb = orbs.Orbs[index];
            if (!FlashRegistry.TryOrb(orb.Id.Entry, out ushort id))
                throw new FlashImportException(FlashRejection.UnsupportedOrb, orb.Id.Entry);
            // DarkOrb._evokeVal / GlassOrb._passiveVal are the only private orb decimals. The
            // simulator clones the orb model and mutates the clone, so the projection reads the model
            // field through OrbMirrors (no PredictionStateStore entry exists for them). Flash stores
            // integers; a fractional value cannot be represented and rejects instead of rounding.
            decimal stored = OrbMirrors.GetStoredValueForFlash(orb);
            if (stored != decimal.Truncate(stored) || stored < int.MinValue || stored > int.MaxValue)
                throw new FlashImportException(FlashRejection.UnrepresentableState,
                    $"{orb.Id.Entry} 私有值 {stored}");
            battle.Orbs[index] = new FlashOrb { Id = checked((byte)id), Aux = (int)stored };
        }
    }

    private static byte Owner(Creature creature, Player player, IReadOnlyList<Creature> known, Creature? osty)
    {
        if (ReferenceEquals(creature, player.Creature))
            return FlashLimits.PlayerOwner;
        if (osty != null && ReferenceEquals(creature, osty))
            return FlashLimits.OstyOwner;
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
