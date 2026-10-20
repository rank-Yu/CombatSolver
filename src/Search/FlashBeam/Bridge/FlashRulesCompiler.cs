using MegaCrit.Sts2.Core.Localization.DynamicVars;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Flash;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace CombatSolver.FlashBeam;

/// <summary>
/// Freezes root data into <see cref="FlashRules"/>: canonical card values per upgrade level, power
/// variables, and each known enemy's compiled move state machine.
/// </summary>
internal static class FlashRulesCompiler
{
    private static readonly Lazy<Dictionary<string, RelicModel>> CanonicalRelics = new(() =>
        ModelDb.All.OfType<RelicModel>().GroupBy(relic => relic.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    private static readonly Lazy<Dictionary<string, PotionModel>> CanonicalPotions = new(() =>
        ModelDb.All.OfType<PotionModel>().GroupBy(potion => potion.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    private static readonly Lazy<Dictionary<string, PowerModel>> CanonicalPowers = new(() =>
        ModelDb.All.OfType<PowerModel>().GroupBy(power => power.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    public static FlashRules Compile(CombatPredictionSimulator simulator, Player player,
        CombatPredictionSimulator? baselineSimulator = null,
        CombatPredictionSimulator? contentSimulator = null,
        CombatPredictionSimulator? lostHpSimulator = null)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        // A turn-setup search enumerates one post-setup root per native choice. Card rules must cover
        // every starting card (a choice can exhaust/discard/move one out of the first root), while the
        // root-derived values below stay tied to the search root that the Flash state starts from.
        SimulatedCombatState contentCombat = contentSimulator is null
            ? combat
            : (SimulatedCombatState)contentSimulator.State.CombatState;
        SimulatedCombatState lostHpCombat = lostHpSimulator is null
            ? combat
            : (SimulatedCombatState)lostHpSimulator.State.CombatState;
        // _rootPowerAmounts is captured on the request root and copied into every fork, so a fixed
        // prefix must not move the baseline used by AdjustMonsterMoveDamage. The caller passes the
        // request-root simulator when the search starts after a prefix; null means "this is the root".
        SimulatedCombatState? baselineCombat = baselineSimulator is null
            ? null
            : (SimulatedCombatState)baselineSimulator.State.CombatState;
        ushort lampId = ushort.MaxValue;
        foreach (RelicModel relic in contentCombat.RelicsOf(player))
        {
            if (FlashRegistry.TryRelic(relic.Id.Entry, out ushort relicId)
                && FlashRegistry.Relic(relicId).IsUnsettlingLamp)
            {
                lampId = relicId;
                break;
            }
        }
        FlashRulesBuilder builder = new()
        {
            BaseMaxEnergy = combat.GetBaseMaxEnergy(player),
            BaseOrbSlotCount = player.Character.BaseOrbSlotCount,
            // _rootPowerAmounts[(player.Creature, typeof(SwordSagePower))] in MaterializeRoot.
            RootSwordSageAmount = combat.GetAmount<MegaCrit.Sts2.Core.Models.Powers.SwordSagePower>(
                player.Creature),
            UnsettlingLampRelicId = lampId,
        };
        SimPlayerCombatState playerState = (contentSimulator ?? simulator).State.GetPlayerCombatState(player);
        bool hasIdentityContent = builder.RootSwordSageAmount != 0
            || player.Character is MegaCrit.Sts2.Core.Models.Characters.Regent
                or MegaCrit.Sts2.Core.Models.Characters.Necrobinder
                or MegaCrit.Sts2.Core.Models.Characters.Silent;
        // Enchantments only enter combat from the out-of-combat deck; the root piles are the complete
        // source of the two turn-phase enchantment hooks, so a root without them skips the dispatch.
        bool hasShuffleOrderEnchantment = false, hasBeforeFlushEnchantment = false;
        foreach (SimCardPile pile in new[] { playerState.Hand, playerState.DrawPile, playerState.DiscardPile,
                     playerState.ExhaustPile, playerState.PlayPile })
        {
            foreach (PredictedCard card in pile.Cards)
            {
                AddCard(builder, card.Preview, player);
                FlashCardDef def = FlashRegistry.Card(CardId(card.Preview));
                hasIdentityContent |= def.UsesIdentityLedger || def.TracksFetchPlay
                    || def.MayGenerateIdentityContent || def.IsIdentityContent;
                if (card.Preview.Enchantment is { } enchantment
                    && FlashRegistry.TryEnchantment(enchantment.Id.Entry, out ushort enchantmentId))
                {
                    FlashHookMaskExt hooks = FlashRegistry.Enchantment(enchantmentId).HooksExt;
                    hasShuffleOrderEnchantment |= (hooks & FlashHookMaskExt.ModifyShuffleOrder) != 0;
                    hasBeforeFlushEnchantment |= (hooks & FlashHookMaskExt.BeforeFlush) != 0;
                }
            }
        }
        builder.HasShuffleOrderEnchantment = hasShuffleOrderEnchantment;
        builder.HasBeforeFlushEnchantment = hasBeforeFlushEnchantment;
        foreach (PowerModel power in contentCombat.EffectivePowers())
        {
            if (FlashRegistry.TryPower(power.Id.Entry, out ushort powerId))
            {
                hasIdentityContent |= FlashRegistry.Power(powerId).IsIdentityContent;
            }
        }
        bool hasPaelsLegion = false;
        bool furCoatSpawnHpOne = false;
        foreach (RelicModel relic in contentCombat.RelicsOf(player))
        {
            if (FlashRegistry.TryRelic(relic.Id.Entry, out ushort relicId))
                hasIdentityContent |= FlashRegistry.Relic(relicId).MayGenerateIdentityContent
                    || FlashRegistry.Relic(relicId).IsIdentityContent;
            if (relic.IsMelted)
                continue;
            if (FlashRegistry.TryRelic(relic.Id.Entry, out ushort relicRuleId)
                && FlashRegistry.Relic(relicRuleId).IsPaelsLegion)
            {
                hasPaelsLegion = true;
            }
            // MonsterSpawnSupport.ApplyCreatureAddedRelics: FurCoat marks the current map coordinate
            // outside combat; the marked-coordinate check is root data, so freeze its result.
            if (relic is FurCoat furCoat
                && contentCombat.CurrentMapCoord is { } mapCoord
                && furCoat.GetMarkedCoords()?.Contains(mapCoord) == true)
            {
                furCoatSpawnHpOne = true;
            }
        }
        builder.HasIdentityContent = hasIdentityContent;
        builder.HasPaelsLegion = hasPaelsLegion;
        builder.FurCoatSpawnHpOne = furCoatSpawnHpOne;
        builder.HasTheftContent = HasTheftContent(contentCombat);
        foreach (string nativeId in PowerIdsWithVars())
            AddPowerVars(builder, nativeId);
        for (ushort id = 0; id < FlashRegistry.PotionCount; id++)
            AddPotionStats(builder, id);
        for (ushort id = 0; id < FlashRegistry.RelicCount; id++)
            AddRelicVars(builder, id);
        IReadOnlyList<Creature> known = combat.KnownEnemies;
        if (known.Count > FlashLimits.Enemies)
            throw new FlashImportException(FlashRejection.Capacity, $"敌人数 {known.Count}");
        // AdvancePlayerTurn drops the prediction cache; HasLostHpThisTurn then falls back to the
        // immutable root history. Freeze the root answer per owner for extra-turn restoration.
        ushort rootLostHpMask = 0;
        if (lostHpCombat.HasLostHpThisTurn(player.Creature))
            rootLostHpMask |= 1 << FlashLimits.PlayerOwner;
        if (lostHpCombat.GetOsty(player) is { } osty && lostHpCombat.HasLostHpThisTurn(osty))
            rootLostHpMask |= 1 << FlashLimits.OstyOwner;
        for (int index = 0; index < known.Count && index < FlashLimits.Enemies; index++)
        {
            if (lostHpCombat.HasLostHpThisTurn(known[index]))
                rootLostHpMask |= (ushort)(1 << FlashKernel.EnemyOwner(index));
        }
        builder.RootLostHpMask = rootLostHpMask;
        List<ushort> spawnClosure = SpawnClosure(known);
        builder.HasInfiniteHpDisplayMonster = HasInfiniteHpDisplayMonster(known, spawnClosure);
        AddGeneratedCardClosure(builder, contentSimulator ?? simulator, contentCombat, combat,
            player, spawnClosure);
        builder.RootEnemyCount = known.Count;
        builder.HasEncounter = combat.Encounter != null;
        builder.EncounterSlots = combat.EncounterSlotsForFlash.ToArray();
        if (known.Count + spawnClosure.Count > FlashLimits.MonsterRules)
            throw new FlashImportException(FlashRejection.Capacity,
                $"怪物规则 {known.Count}+{spawnClosure.Count}");
        // ApplyCreatureAddedRelics' Murderous modifier gives every newly created enemy Strength 3.
        // PhilosophersStone and FurCoat are rejected unconditionally by their missing relic
        // definitions in FlashStateImporter.ImportRelics.
        if (spawnClosure.Count > 0 && combat.Modifiers.Any(static modifier => modifier is Murderous))
            throw new FlashImportException(FlashRejection.UnsupportedClosure, "Murderous 修饰的召唤入场力量未移植");
        // Monster rules are compiled from a private fork: reading monster-private integers through
        // GetMonsterInt caches them, and admission must not mutate the caller's simulator.
        CombatPredictionSimulator rootFork = simulator.Fork();
        SimulatedCombatState rootCombat = (SimulatedCombatState)rootFork.State.CombatState;
        for (int index = 0; index < known.Count; index++)
            builder.SetMonster(index, CompileMonster(rootCombat, rootCombat.KnownEnemies[index], baselineCombat));
        foreach (ushort monsterId in spawnClosure)
            CompileSpawnedMonster(builder, simulator, monsterId);
        return builder.Build();
    }

    /// <summary>
    /// Native <c>HpDisplay.InfiniteWithoutNumbers</c> is set only by the Waterfall Giant's steam
    /// eruption rewrite; a root with no such monster (known or spawnable) skips Hellraiser's scan.
    /// </summary>
    private static bool HasInfiniteHpDisplayMonster(IReadOnlyList<Creature> known, IReadOnlyList<ushort> spawnClosure)
    {
        foreach (Creature creature in known)
        {
            if (creature.Monster is { } monster
                && FlashRegistry.TryMonster(monster.Id.Entry, out ushort id)
                && FlashRegistry.Monster(id).HasInfiniteHpDisplay)
            {
                return true;
            }
        }
        foreach (ushort monsterId in spawnClosure)
        {
            if (FlashRegistry.Monster(monsterId).HasInfiniteHpDisplay)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The two thief encounters and any root that already carries a theft power. Only those roots pay
    /// for the outstanding-stolen-resource counters and the evaluation's theft scans.
    /// </summary>
    private static bool HasTheftContent(SimulatedCombatState combat)
    {
        if (TheftEncounterStrategy.IsApplicable(combat.Encounter?.Id.Entry, combat.KnownEnemies))
            return true;
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (power is ThieveryPower or HeistPower or SwipePower or SurprisePower)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Native ids reachable from the root enemies' <see cref="FlashMonsterDef.SpawnedMonsterIds"/>,
    /// transitively, in first-seen order.
    /// </summary>
    private static List<ushort> SpawnClosure(IReadOnlyList<Creature> known)
    {
        List<ushort> closure = [];
        HashSet<ushort> seen = [];
        Queue<string> pending = new();
        void Declare(FlashMonsterDef def)
        {
            foreach (string nativeId in def.SpawnedMonsterIds)
                pending.Enqueue(nativeId);
        }
        foreach (Creature creature in known)
        {
            string nativeId = creature.Monster?.Id.Entry
                ?? throw new FlashImportException(FlashRejection.UnsupportedMonster, "敌人没有怪物模型");
            if (!FlashRegistry.TryMonster(nativeId, out ushort id))
                throw new FlashImportException(FlashRejection.UnsupportedMonster, nativeId);
            Declare(FlashRegistry.Monster(id));
        }
        while (pending.Count > 0)
        {
            string nativeId = pending.Dequeue();
            if (!FlashRegistry.TryMonster(nativeId, out ushort id))
                throw new FlashImportException(FlashRejection.UnsupportedClosure,
                    $"生成怪物 {nativeId} 没有 Flash 定义");
            if (!seen.Add(id))
                continue;
            closure.Add(id);
            Declare(FlashRegistry.Monster(id));
        }
        return closure;
    }

    /// <summary>
    /// Compiles one spawnable type on its own private fork through the existing engine's creation
    /// path (<c>MonsterSpawnSupport.Create</c> + <c>AddPredictedMonster</c> +
    /// <c>PreparePredictedMonster</c>), so the state machine, static values and initial private
    /// integers come from a fresh instance rather than from a same-typed root enemy.
    /// </summary>
    private static void CompileSpawnedMonster(FlashRulesBuilder builder,
        CombatPredictionSimulator simulator, ushort monsterId)
    {
        FlashMonsterDef def = FlashRegistry.Monster(monsterId);
        string nativeId = def.NativeId;
        MonsterModel canonical = CanonicalMonsters.Value.TryGetValue(nativeId, out MonsterModel? monster)
            ? monster
            : throw new FlashImportException(FlashRejection.UnsupportedClosure, $"原版没有怪物 {nativeId}");
        CombatPredictionSimulator fork = simulator.Fork();
        SimulatedCombatState combat = (SimulatedCombatState)fork.State.CombatState;
        string? slot = def.SpawnSlotName ?? combat.NextFreeSlot();
        if (string.IsNullOrEmpty(slot))
            slot = null;
        // Same creation path as MonsterSpawnSupport.Create, with the definition's spawn configuration:
        // Wriggler's StartStunned selects the SPAWNED_MOVE initial node, and its INIT_MOVE conditional
        // reads the slot name, so a spawn rule entry must not be compiled from an unmatched slot.
        MonsterModel mutable = canonical.ToMutable();
        if (def.SpawnedStunned && mutable is Wriggler wriggler)
            wriggler.StartStunned = true;
        if (def.SpawnStockAmount >= 0 && mutable is Axebot axebot)
            axebot.StockAmount = def.SpawnStockAmount;
        Creature created = combat.CreatePredictedMonster(fork, mutable, CombatSide.Enemy, slot);
        combat.AddPredictedMonster(created);
        combat.PreparePredictedMonster(fork, created);
        builder.AddSpawnableMonster(CompileMonster(combat, created));
    }

    private static readonly Lazy<Dictionary<string, MonsterModel>> CanonicalMonsters = new(() =>
        ModelDb.All.OfType<MonsterModel>().GroupBy(monster => monster.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    public static ushort CardId(CardModel card)
        => FlashRegistry.TryCard(card.Id.Entry, out ushort id)
            ? id
            : throw new FlashImportException(FlashRejection.UnsupportedCard, card.Id.Entry);

    private static void AddCard(FlashRulesBuilder builder, CardModel card, Player player)
    {
        AddStatsWithUpgrade(builder, CardId(card), card.CurrentUpgradeLevel, card.CanonicalInstance, player);
    }

    /// <summary>
    /// Adds the stats of <paramref name="upgrade"/> and, for an upgradable card, of the next level, so
    /// in-combat upgrades (<c>FlashKernel.UpgradeCard</c>) find their target level. An upgraded card's
    /// base level is added too: MagiKnight's Dampen resets every upgraded card to level 0
    /// (<c>SimulatedCombatState.ApplyDampen</c> / <c>FlashKernel.DowngradeCard</c>) and the downgraded
    /// card can be read and played before the last caster dies and restores it.
    /// </summary>
    private static void AddStatsWithUpgrade(FlashRulesBuilder builder, ushort id, int upgrade, CardModel canonical, Player player)
    {
        builder.HasDecimalCardVars |= FlashRegistry.Card(id).UsesDecimalDamageVar;
        if (upgrade > 0 && !builder.HasCard(id, 0))
            builder.SetCard(id, 0, CanonicalStats(canonical, 0, player));
        if (!builder.HasCard(id, upgrade))
            builder.SetCard(id, upgrade, CanonicalStats(canonical, upgrade, player));
        if (upgrade < canonical.MaxUpgradeLevel && !builder.HasCard(id, upgrade + 1))
            builder.SetCard(id, upgrade + 1, CanonicalStats(canonical, upgrade + 1, player));
    }

    /// <summary>Values of the canonical card upgraded <paramref name="upgrade"/> times.</summary>
    internal static FlashCardStats CanonicalStats(CardModel canonical, int upgrade, Player player)
    {
        CardModel copy = PredictionUtils.CreateCard(canonical, player);
        for (int level = 0; level < upgrade; level++)
            PredictionUtils.UpgradeCard(copy);
        if (copy.CurrentUpgradeLevel != upgrade)
            throw new FlashImportException(FlashRejection.UnsupportedCard, $"{canonical.Id.Entry} 无法升级到 {upgrade}");
        FlashCardDef def = FlashRegistry.Card(CardId(canonical));
        decimal[] vars = new decimal[def.VarNames.Count];
        for (int index = 0; index < vars.Length; index++)
        {
            string name = def.VarNames[index];
            vars[index] = copy.DynamicVars.TryGetValue(name, out var variable)
                ? variable.BaseValue
                : throw new FlashImportException(FlashRejection.UnsupportedCard, $"{canonical.Id.Entry} 没有动态变量 {name}");
        }
        return new FlashCardStats
        {
            Type = (FlashCardType)(byte)copy.Type,
            Target = (FlashTargetType)(byte)copy.TargetType,
            Rarity = (FlashCardRarity)(byte)copy.Rarity,
            Pool = copy.Pool switch
            {
                ColorlessCardPool => FlashTransformPool.Colorless,
                StatusCardPool => FlashTransformPool.Status,
                CurseCardPool => FlashTransformPool.Curse,
                _ => FlashTransformPool.Character,
            },
            EnergyCost = copy.EnergyCost._base,
            CostsX = copy.EnergyCost.CostsX,
            StarCost = copy.BaseStarCost,
            StarCostsX = copy.HasStarCostX,
            Keywords = FlashStateImporter.MapKeywords(copy.Keywords),
            MaxUpgradeLevel = copy.MaxUpgradeLevel,
            Tags = MapTags(copy.Tags),
            IsSoul = copy.Id.Entry == "SOUL",
            IsColorless = copy.VisualCardPool.IsColorless,
            Vars = vars,
        };
    }

    /// <summary>
    /// Adds canonical stats for every card that supported content declares it can generate, following
    /// the declarations transitively. Undefined generated cards reject the root (UnsupportedClosure).
    /// </summary>
    private static void AddGeneratedCardClosure(FlashRulesBuilder builder,
        CombatPredictionSimulator contentSimulator, SimulatedCombatState contentCombat,
        SimulatedCombatState rootCombat, Player player, IReadOnlyList<ushort> spawnClosure)
    {
        Queue<(string CardId, int Upgrade)> pendingCards = new();
        Queue<string> pendingPools = new();
        Queue<string> pendingPowers = new();
        HashSet<(string, int)> seenCards = [];
        HashSet<string> seenPools = new(StringComparer.Ordinal);
        HashSet<string> seenPowerDefs = new(StringComparer.Ordinal);
        HashSet<ushort> seenPotionDefs = [];
        ushort[]? inCombatPotionPool = null;
        ushort[]? outOfCombatPotionPool = null;
        bool needsPotionPool = false;
        bool needsOutOfCombatPotionPool = false;
        void Declare(FlashContentDef def)
        {
            needsPotionPool |= def.GeneratesRandomPotion;
            needsOutOfCombatPotionPool |= def is FlashPotionDef { GeneratesOutOfCombatPotion: true };
            if (def is FlashPotionDef && !seenPotionDefs.Add(def.Id))
                return;
            foreach ((string cardId, int upgrade) in def.GeneratedCards)
            {
                if (seenCards.Add((cardId, upgrade)))
                    pendingCards.Enqueue((cardId, upgrade));
            }
            foreach (string pool in def.GeneratedCardPools)
            {
                if (seenPools.Add(pool))
                    pendingPools.Enqueue(pool);
            }
            foreach (string power in def.AppliedPowers)
                pendingPowers.Enqueue(power);
        }
        // The closure starts from the content root's reachable content, as admission's reachability
        // rule requires: a pool only has to be representable for a root that can actually use it.
        // A turn-setup search passes the pre-setup request root here so cards a setup choice moves out
        // of the first post-setup root still have rules in every sibling root.
        SimPlayerCombatState playerState = contentSimulator.State.GetPlayerCombatState(player);
        foreach (SimCardPile pile in new[] { playerState.Hand, playerState.DrawPile, playerState.DiscardPile,
                     playerState.ExhaustPile, playerState.PlayPile })
        {
            foreach (PredictedCard card in pile.Cards)
            {
                if (FlashRegistry.TryCard(card.Preview.Id.Entry, out ushort id))
                    Declare(FlashRegistry.Card(id));
            }
        }
        foreach (PowerModel power in contentCombat.EffectivePowers())
        {
            if (FlashRegistry.TryPower(power.Id.Entry, out ushort id))
                Declare(FlashRegistry.Power(id));
        }
        foreach (RelicModel relic in contentCombat.RelicsOf(player))
        {
            if (FlashRegistry.TryRelic(relic.Id.Entry, out ushort id))
                Declare(FlashRegistry.Relic(id));
        }
        for (int slot = 0; slot < contentCombat.PotionSlotCountForFlash(player); slot++)
        {
            if (contentCombat.GetPotionAtSlot(player, slot) is { } potion
                && FlashRegistry.TryPotion(potion.Id.Entry, out ushort id))
                Declare(FlashRegistry.Potion(id));
        }
        // Root potions identify "this slot still holds the potion the search started with": that is a
        // search-root property (a fixed prefix may already have drunk it), so it reads the real root.
        for (int slot = 0; slot < rootCombat.PotionSlotCountForFlash(player); slot++)
        {
            if (rootCombat.GetPotionAtSlot(player, slot) is { } rootPotion
                && FlashRegistry.TryPotion(rootPotion.Id.Entry, out ushort rootPotionId))
            {
                builder.SetRootPotion(slot, rootPotionId);
            }
        }
        foreach (Creature enemy in contentCombat.KnownEnemies)
        {
            if (enemy.Monster is { } monster && FlashRegistry.TryMonster(monster.Id.Entry, out ushort id))
                Declare(FlashRegistry.Monster(id));
        }
        foreach (ushort monsterId in spawnClosure)
            Declare(FlashRegistry.Monster(monsterId));
        while (true)
        {
          while (pendingCards.Count > 0 || pendingPools.Count > 0 || pendingPowers.Count > 0)
          {
            while (pendingPowers.Count > 0)
            {
                string powerId = pendingPowers.Dequeue();
                if (!seenPowerDefs.Add(powerId))
                    continue;
                if (!FlashRegistry.TryPower(powerId, out ushort powerInternalId))
                    throw new FlashImportException(FlashRejection.UnsupportedClosure,
                        $"生成 Power {powerId} 没有 Flash 定义");
                Declare(FlashRegistry.Power(powerInternalId));
            }
            while (pendingPools.Count > 0)
            {
                string poolKey = pendingPools.Dequeue();
                ushort[] pool = FreezeCardPool(poolKey, player);
                builder.SetCardPool(poolKey, pool);
                foreach (ushort id in pool)
                {
                    Declare(FlashRegistry.Card(id));
                    CardModel poolCanonical = CanonicalCards.Value.TryGetValue(FlashRegistry.Card(id).NativeId,
                        out CardModel? canonicalCard)
                        ? canonicalCard
                        : throw new FlashImportException(FlashRejection.UnsupportedClosure,
                            $"原版没有卡牌 {FlashRegistry.Card(id).NativeId}");
                    AddStatsWithUpgrade(builder, id, 0, poolCanonical, player);
                }
            }
            if (pendingCards.Count == 0)
                break;
            (string cardId, int upgrade) = pendingCards.Dequeue();
            if (!FlashRegistry.TryCard(cardId, out ushort cardInternalId))
                throw new FlashImportException(FlashRejection.UnsupportedClosure, $"生成卡 {cardId} 没有 Flash 定义");
            CardModel canonical = CanonicalCards.Value.TryGetValue(cardId, out CardModel? card)
                ? card
                : throw new FlashImportException(FlashRejection.UnsupportedClosure, $"原版没有卡牌 {cardId}");
            AddStatsWithUpgrade(builder, cardInternalId, upgrade, canonical, player);
            Declare(FlashRegistry.Card(cardInternalId));
          }
          // A random-potion candidate can itself generate cards or potions (Alchemize class
          // potions), so declaring the frozen pool must re-run the closure before it is complete.
          if (needsPotionPool && inCombatPotionPool == null)
          {
              inCombatPotionPool = FreezePotionPool(player, inCombat: true);
              builder.SetPotionPool(inCombatPotionPool);
              foreach (ushort id in inCombatPotionPool)
                  Declare(FlashRegistry.Potion(id));
              continue;
          }
          if (needsOutOfCombatPotionPool && outOfCombatPotionPool == null)
          {
              outOfCombatPotionPool = FreezePotionPool(player, inCombat: false);
              builder.SetOutOfCombatPotionPool(outOfCombatPotionPool);
              foreach (ushort id in outOfCombatPotionPool)
                  Declare(FlashRegistry.Potion(id));
              continue;
          }
          break;
        }
    }

    /// <summary>
    /// The random-potion option pool: the character and shared potion pools' unlocked options in
    /// that order. <paramref name="inCombat"/> selects <c>CreateRandomPotionInCombat</c>'s
    /// <c>CanBeGeneratedInCombat</c> filter; the out-of-combat pool (EntropicBrew) has no filter.
    /// </summary>
    private static ushort[] FreezePotionPool(Player player, bool inCombat)
    {
        List<PotionModel> options = PotionFactory.GetPotionOptions(player)
            .Where(potion => !inCombat || potion.CanBeGeneratedInCombat)
            .ToList();
        ushort[] ids = new ushort[options.Count];
        for (int index = 0; index < options.Count; index++)
        {
            PotionModel potion = options[index];
            if (!FlashRegistry.TryPotion(potion.Id.Entry, out ushort id))
                throw new FlashImportException(FlashRejection.UnsupportedClosure,
                    $"随机药水 {potion.Id.Entry} 没有 Flash 定义");
            ids[index] = id;
        }
        return ids;
    }

    /// <summary>
    /// Freezes one generation pool with the same query order the existing engine uses at the call site
    /// (<c>Player.GetUnlockedCards</c> + <c>FilterForCombatAndPlayerCount</c>, then the caller predicate).
    /// </summary>
    private static ushort[] FreezeCardPool(string poolKey, Player player)
    {
        CardMultiplayerConstraint constraint = player.RunState.CardMultiplayerConstraint;
        IEnumerable<CardModel> cards = poolKey switch
        {
            FlashCardPools.CharacterAll => player.GetUnlockedCharacterCards(constraint),
            FlashCardPools.CharacterAttacks => player.GetUnlockedCharacterCards(constraint)
                .Where(static card => card.Type == CardType.Attack),
            FlashCardPools.CharacterZeroCost => player.GetUnlockedCharacterCards(constraint)
                .Where(static card => card.EnergyCost is { Canonical: 0, CostsX: false }),
            FlashCardPools.ColorlessAll => player.GetUnlockedColorlessCards(constraint),
            FlashCardPools.ColorlessExceptJackOfAllTrades => player.GetUnlockedColorlessCards(constraint)
                .Where(static card => card is not JackOfAllTrades),
            FlashCardPools.OtherCharacterAttacks => OtherCharacterAttacks(player, constraint),
            FlashCardPools.CharacterPowers => player.GetUnlockedCharacterCards(constraint)
                .Where(static card => card.Type == CardType.Power),
            FlashCardPools.CharacterEthereal => player.GetUnlockedCharacterCards(constraint)
                .Where(static card => card.Keywords.Contains(CardKeyword.Ethereal)),
            FlashCardPools.StatusAll => ModelDb.CardPool<StatusCardPool>()
                .GetUnlockedCards(player.UnlockState, constraint),
            FlashCardPools.CurseAll => ModelDb.CardPool<CurseCardPool>()
                .GetUnlockedCards(player.UnlockState, constraint),
            _ => throw new FlashImportException(FlashRejection.UnsupportedClosure, $"未知卡池 {poolKey}"),
        };
        List<CardModel> candidates = cards.FilterForCombatAndPlayerCount(constraint).ToList();
        ushort[] ids = new ushort[candidates.Count];
        for (int index = 0; index < candidates.Count; index++)
        {
            CardModel candidate = candidates[index];
            if (!FlashRegistry.TryCard(candidate.Id.Entry, out ushort id))
                throw new FlashImportException(FlashRejection.UnsupportedClosure,
                    $"生成卡 {candidate.Id.Entry} 没有 Flash 定义（卡池 {poolKey}）");
            ids[index] = id;
        }
        return ids;
    }

    /// <summary><c>SplashOnPlay</c>'s pool choice: every unlocked character pool except the player's own when there is more than one.</summary>
    private static IEnumerable<CardModel> OtherCharacterAttacks(Player player, CardMultiplayerConstraint constraint)
    {
        List<CardPoolModel> pools = player.UnlockState.CharacterCardPools.ToList();
        if (pools.Count > 1)
            pools.Remove(player.Character.CardPool);
        return pools
            .SelectMany(pool => player.GetUnlockedCards(pool, constraint))
            .Where(static card => card.Type == CardType.Attack);
    }

    private static readonly Lazy<Dictionary<string, CardModel>> CanonicalCards = new(() =>
        ModelDb.All.OfType<CardModel>().GroupBy(card => card.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    private static FlashCardTag MapTags(IEnumerable<MegaCrit.Sts2.Core.Entities.Cards.CardTag> tags)
    {
        FlashCardTag result = FlashCardTag.None;
        foreach (var tag in tags)
        {
            result |= tag switch
            {
                MegaCrit.Sts2.Core.Entities.Cards.CardTag.Strike => FlashCardTag.Strike,
                MegaCrit.Sts2.Core.Entities.Cards.CardTag.Defend => FlashCardTag.Defend,
                MegaCrit.Sts2.Core.Entities.Cards.CardTag.Minion => FlashCardTag.Minion,
                MegaCrit.Sts2.Core.Entities.Cards.CardTag.OstyAttack => FlashCardTag.OstyAttack,
                MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shiv => FlashCardTag.Shiv,
                _ => throw new FlashImportException(FlashRejection.UnsupportedCard, $"卡牌标签 {tag}"),
            };
        }
        return result;
    }

    private static IEnumerable<string> PowerIdsWithVars()
    {
        for (ushort id = 0; id < FlashRegistry.PowerCount; id++)
        {
            FlashPowerDef def = FlashRegistry.Power(id);
            if (def.VarNames.Count > 0)
                yield return def.NativeId;
        }
    }

    /// <summary>The named dynamic variables of the canonical relic.</summary>
    private static void AddRelicVars(FlashRulesBuilder builder, ushort id)
    {
        FlashRelicDef def = FlashRegistry.Relic(id);
        if (def.VarNames.Count == 0)
            return;
        RelicModel canonical = CanonicalRelics.Value.TryGetValue(def.NativeId, out RelicModel? relic)
            ? relic
            : throw new InvalidOperationException($"原版没有遗物 {def.NativeId}。");
        decimal[] vars = new decimal[def.VarNames.Count];
        for (int index = 0; index < vars.Length; index++)
        {
            vars[index] = canonical.DynamicVars.TryGetValue(def.VarNames[index], out var variable)
                ? variable.BaseValue
                : throw new InvalidOperationException($"遗物 {def.NativeId} 没有动态变量 {def.VarNames[index]}。");
        }
        builder.SetRelicVars(id, vars);
    }

    /// <summary>Target type and named variables (with their value props) of the canonical potion.</summary>
    private static void AddPotionStats(FlashRulesBuilder builder, ushort id)
    {
        FlashPotionDef def = FlashRegistry.Potion(id);
        PotionModel canonical = CanonicalPotions.Value.TryGetValue(def.NativeId, out PotionModel? potion)
            ? potion
            : throw new InvalidOperationException($"原版没有药水 {def.NativeId}。");
        decimal[] vars = new decimal[def.VarNames.Count];
        FlashValueProps[] props = new FlashValueProps[vars.Length];
        for (int index = 0; index < vars.Length; index++)
        {
            var variable = canonical.DynamicVars.TryGetValue(def.VarNames[index], out var found)
                ? found
                : throw new InvalidOperationException($"药水 {def.NativeId} 没有动态变量 {def.VarNames[index]}。");
            vars[index] = variable.BaseValue;
            props[index] = variable switch
            {
                DamageVar damage => (FlashValueProps)(byte)damage.Props,
                BlockVar block => (FlashValueProps)(byte)block.Props,
                _ => FlashValueProps.None,
            };
        }
        builder.SetPotion(id, new FlashPotionStats
        {
            Target = (FlashTargetType)(byte)canonical.TargetType,
            Rarity = (FlashPotionRarity)(byte)canonical.Rarity,
            StrategicCost = PotionUsePolicy.StrategicHpCost(canonical),
            Vars = vars,
            VarProps = props,
        });
    }

    private static void AddPowerVars(FlashRulesBuilder builder, string nativeId)
    {
        FlashRegistry.TryPower(nativeId, out ushort id);
        FlashPowerDef def = FlashRegistry.Power(id);
        PowerModel canonical = CanonicalPowers.Value.TryGetValue(nativeId, out PowerModel? power)
            ? power
            : throw new InvalidOperationException($"原版没有 Power {nativeId}。");
        decimal[] vars = new decimal[def.VarNames.Count];
        for (int index = 0; index < vars.Length; index++)
            vars[index] = canonical.DynamicVars[def.VarNames[index]].BaseValue;
        builder.SetPowerVars(id, vars);
    }

    private static FlashMonsterRules CompileMonster(SimulatedCombatState combat, Creature creature,
        SimulatedCombatState? baselineCombat = null)
    {
        MonsterModel monster = creature.Monster
            ?? throw new FlashImportException(FlashRejection.UnsupportedMonster, "敌人没有怪物模型");
        if (!FlashRegistry.TryMonster(monster.Id.Entry, out ushort monsterId))
            throw new FlashImportException(FlashRejection.UnsupportedMonster, monster.Id.Entry);
        FlashMonsterDef def = FlashRegistry.Monster(monsterId);
        BranchMonsterAiState ai = combat.GetMonsterAiStateForFlash(creature);
        MonsterMoveStateMachine machine = ai.Machine;
        List<MonsterState> states = machine.States.Values.ToList();
        // A native initial branch may live outside States (PhantasmalGardener's INIT_MOVE); it is
        // compiled after the registered states so their indices stay those of States.
        if (machine._initialState is { } unregistered && !machine.States.ContainsKey(unregistered.Id))
            states.Add(unregistered);
        List<string> ids = states.Select(state => state.Id).ToList();
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        for (int position = 0; position < ids.Count; position++)
            index[ids[position]] = position;
        int nodeCount = ids.Count + 1;
        if (def.IllusionRevive)
            nodeCount++;
        if (nodeCount > 64)
            throw new FlashImportException(FlashRejection.Capacity, $"{monster.Id.Entry} 状态数 {ids.Count}");

        FlashMonsterNode[] nodes = new FlashMonsterNode[nodeCount];
        for (int position = 0; position < ids.Count; position++)
        {
            MonsterState state = states[position];
            nodes[position] = state switch
            {
                MoveState move => CompileMove(move, ai.Static, index, monster),
                RandomBranchState random => new FlashMonsterNode
                {
                    NativeId = random.Id,
                    Kind = FlashMonsterNodeKind.RandomBranch,
                    Options = random.States.Select(option => new FlashRandomBranchOption(
                        index.TryGetValue(option.stateId, out int target)
                            ? target
                            : throw new FlashImportException(FlashRejection.UnsupportedMonster, $"{monster.Id.Entry}.{option.stateId}"),
                        ai.Static.RandomBaseWeights.TryGetValue((random.Id, option.stateId), out float weight)
                            ? weight
                            : throw new FlashImportException(FlashRejection.UnsupportedMonster, $"{monster.Id.Entry} 随机权重 {random.Id}.{option.stateId}"),
                        (FlashMoveRepeatType)(byte)option.repeatType,
                        option.maxTimes,
                        option.cooldown)).ToArray(),
                },
                ConditionalBranchState conditional => new FlashMonsterNode
                {
                    NativeId = conditional.Id,
                    Kind = FlashMonsterNodeKind.ConditionalBranch,
                    ConditionalTarget = ai.Static.ConditionalSelections.TryGetValue(conditional.Id, out string? selected)
                        ? index[selected]
                        : -1,
                },
                _ => throw new FlashImportException(FlashRejection.UnsupportedMonster, $"{monster.Id.Entry} 状态 {state.GetType().Name}"),
            };
        }

        int initial = ai.Machine._initialState is { } initialState
            ? index[initialState.Id]
            : throw new FlashImportException(FlashRejection.UnsupportedMonster, $"{monster.Id.Entry} 没有初始状态");
        // Synthetic STUNNED node, appended after the registered states and the unregistered initial
        // branch so every native node keeps its index. Its follow-up is the dynamic
        // FlashEnemy.ForcedMove (SimulatedCombatState.MonsterAi.cs:76-86).
        int stunned = ids.Count;
        nodes[stunned] = new FlashMonsterNode
        {
            NativeId = FlashKernel.StunnedNodeId,
            Kind = FlashMonsterNodeKind.Move,
            MustPerformOnceBeforeTransitioning = true,
        };
        int revive = -1;
        if (def.IllusionRevive)
        {
            // SimulatedCombatState.BeginIllusionRevive builds this MoveState with
            // MustPerformOnceBeforeTransitioning and a dynamic FollowUpStateId
            // (SimulatedCombatState.DeathLifecycle.cs:100-119).
            revive = stunned + 1;
            nodes[revive] = new FlashMonsterNode
            {
                NativeId = FlashKernel.ReviveNodeId,
                Kind = FlashMonsterNodeKind.Move,
                MustPerformOnceBeforeTransitioning = true,
            };
        }
        // SimulatedCombatState.RegisterPendingInitialMonsterAi picks the first registered MoveState
        // as the placeholder while a fresh monster waits for its initial roll.
        int pendingInitial = -1;
        for (int position = 0; position < ids.Count; position++)
        {
            if (states[position] is MoveState)
            {
                pendingInitial = position;
                break;
            }
        }
        if (pendingInitial < 0)
            throw new FlashImportException(FlashRejection.UnsupportedMonster, $"{monster.Id.Entry} 没有可用行动状态");
        int[] staticInts = new int[def.StaticIntNames.Count];
        for (int position = 0; position < staticInts.Length; position++)
        {
            string name = def.StaticIntNames[position];
            staticInts[position] = ai.Static.StaticIntValues.TryGetValue(name, out int value)
                ? value
                : throw new FlashImportException(FlashRejection.UnsupportedMonster, $"{monster.Id.Entry} 没有静态值 {name}");
        }
        int[] rootPowerAmounts = new int[def.RootPowerAmountNames.Count];
        if (def.RootPowerAmountNames.Count > 0)
        {
            // No baseline means this simulator is itself the request root (differential probe).
            SimulatedCombatState baselineSource = baselineCombat ?? combat;
            Creature? baselineCreature = baselineCombat is null
                ? creature
                : baselineCombat.GetCreature(creature.CombatId);
            for (int position = 0; position < rootPowerAmounts.Length; position++)
            {
                string nativePower = def.RootPowerAmountNames[position];
                rootPowerAmounts[position] = 0;
                if (baselineCreature == null)
                    continue;
                foreach (PowerModel power in baselineSource.EffectivePowers())
                {
                    if (ReferenceEquals(power.Owner, baselineCreature) && power.Id.Entry == nativePower)
                    {
                        rootPowerAmounts[position] = power.Amount;
                        break;
                    }
                }
            }
        }
        int[] initialAiRegisters = new int[def.AiRegisterNames.Count];
        for (int position = 0; position < initialAiRegisters.Length; position++)
            initialAiRegisters[position] = combat.GetMonsterIntForFlash(creature, def.AiRegisterNames[position]);
        return new FlashMonsterRules
        {
            MonsterId = monsterId,
            Nodes = nodes,
            InitialNode = initial,
            PendingInitialMoveNode = pendingInitial,
            InitialMoveLogged = ai.Machine._initialState is MoveState,
            StunnedNode = stunned,
            ReviveNode = revive,
            InitialHpMin = monster.MinInitialHp,
            InitialHpMax = monster.MaxInitialHp,
            StaticInts = staticInts,
            RootPowerAmounts = rootPowerAmounts,
            InitialAiRegisters = initialAiRegisters,
        };
    }

    private static FlashMonsterNode CompileMove(MoveState move, BranchMonsterStaticSnapshot statics,
        Dictionary<string, int> index, MonsterModel monster)
    {
        string? followUp = move.FollowUpState?.Id ?? move.FollowUpStateId;
        IReadOnlyList<BranchMonsterAttack> attacks = statics.AttacksByMove.TryGetValue(move.Id, out var captured) ? captured : [];
        return new FlashMonsterNode
        {
            NativeId = move.Id,
            Kind = FlashMonsterNodeKind.Move,
            FollowUp = followUp != null && index.TryGetValue(followUp, out int target) ? target : -1,
            MustPerformOnceBeforeTransitioning = move.MustPerformOnceBeforeTransitioning,
            Attacks = attacks.Select(attack => new FlashMonsterAttack(attack.BaseDamage, attack.Repeats)).ToArray(),
        };
    }
}
