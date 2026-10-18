using MegaCrit.Sts2.Core.Localization.DynamicVars;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Flash;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
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

    public static FlashRules Compile(CombatPredictionSimulator simulator, Player player)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        FlashRulesBuilder builder = new()
        {
            BaseMaxEnergy = combat.GetBaseMaxEnergy(player),
        };
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        foreach (SimCardPile pile in new[] { playerState.Hand, playerState.DrawPile, playerState.DiscardPile,
                     playerState.ExhaustPile, playerState.PlayPile })
        {
            foreach (PredictedCard card in pile.Cards)
                AddCard(builder, card.Preview, player);
        }
        foreach (string nativeId in PowerIdsWithVars())
            AddPowerVars(builder, nativeId);
        for (ushort id = 0; id < FlashRegistry.PotionCount; id++)
            AddPotionStats(builder, id);
        for (ushort id = 0; id < FlashRegistry.RelicCount; id++)
            AddRelicVars(builder, id);
        AddGeneratedCardClosure(builder, player);
        IReadOnlyList<Creature> known = combat.KnownEnemies;
        if (known.Count > FlashLimits.Enemies)
            throw new FlashImportException(FlashRejection.Capacity, $"敌人数 {known.Count}");
        for (int index = 0; index < known.Count; index++)
            builder.SetMonster(index, CompileMonster(combat, known[index]));
        return builder.Build();
    }

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
    /// in-combat upgrades (<c>FlashKernel.UpgradeCard</c>) find their target level.
    /// </summary>
    private static void AddStatsWithUpgrade(FlashRulesBuilder builder, ushort id, int upgrade, CardModel canonical, Player player)
    {
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
            EnergyCost = copy.EnergyCost._base,
            CostsX = copy.EnergyCost.CostsX,
            StarCost = copy.BaseStarCost,
            StarCostsX = copy.HasStarCostX,
            Keywords = FlashStateImporter.MapKeywords(copy.Keywords),
            MaxUpgradeLevel = copy.MaxUpgradeLevel,
            Tags = MapTags(copy.Tags),
            Vars = vars,
        };
    }

    /// <summary>
    /// Adds canonical stats for every card that supported content declares it can generate, following
    /// the declarations transitively. Undefined generated cards reject the root (UnsupportedClosure).
    /// </summary>
    private static void AddGeneratedCardClosure(FlashRulesBuilder builder, Player player)
    {
        Queue<(string CardId, int Upgrade)> pending = new();
        HashSet<(string, int)> seen = [];
        void Declare(FlashContentDef def)
        {
            foreach ((string cardId, int upgrade) in def.GeneratedCards)
            {
                if (seen.Add((cardId, upgrade)))
                    pending.Enqueue((cardId, upgrade));
            }
        }
        for (ushort id = 0; id < FlashRegistry.CardCount; id++)
            Declare(FlashRegistry.Card(id));
        for (ushort id = 0; id < FlashRegistry.PowerCount; id++)
            Declare(FlashRegistry.Power(id));
        for (ushort id = 0; id < FlashRegistry.MonsterCount; id++)
            Declare(FlashRegistry.Monster(id));
        for (ushort id = 0; id < FlashRegistry.RelicCount; id++)
            Declare(FlashRegistry.Relic(id));
        while (pending.Count > 0)
        {
            (string cardId, int upgrade) = pending.Dequeue();
            if (!FlashRegistry.TryCard(cardId, out ushort id))
                throw new FlashImportException(FlashRejection.UnsupportedClosure, $"生成卡 {cardId} 没有 Flash 定义");
            CardModel canonical = CanonicalCards.Value.TryGetValue(cardId, out CardModel? card)
                ? card
                : throw new FlashImportException(FlashRejection.UnsupportedClosure, $"原版没有卡牌 {cardId}");
            AddStatsWithUpgrade(builder, id, upgrade, canonical, player);
            Declare(FlashRegistry.Card(id));
        }
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

    private static FlashMonsterRules CompileMonster(SimulatedCombatState combat, Creature creature)
    {
        MonsterModel monster = creature.Monster
            ?? throw new FlashImportException(FlashRejection.UnsupportedMonster, "敌人没有怪物模型");
        if (!FlashRegistry.TryMonster(monster.Id.Entry, out ushort monsterId))
            throw new FlashImportException(FlashRejection.UnsupportedMonster, monster.Id.Entry);
        FlashMonsterDef def = FlashRegistry.Monster(monsterId);
        BranchMonsterAiState ai = combat.GetMonsterAiStateForFlash(creature);
        MonsterMoveStateMachine machine = ai.Machine;
        List<string> ids = machine.States.Keys.ToList();
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        for (int position = 0; position < ids.Count; position++)
            index[ids[position]] = position;
        if (ids.Count > 64)
            throw new FlashImportException(FlashRejection.Capacity, $"{monster.Id.Entry} 状态数 {ids.Count}");

        FlashMonsterNode[] nodes = new FlashMonsterNode[ids.Count];
        for (int position = 0; position < ids.Count; position++)
        {
            MonsterState state = machine.States[ids[position]];
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
        int[] staticInts = new int[def.StaticIntNames.Count];
        for (int position = 0; position < staticInts.Length; position++)
        {
            string name = def.StaticIntNames[position];
            staticInts[position] = ai.Static.StaticIntValues.TryGetValue(name, out int value)
                ? value
                : throw new FlashImportException(FlashRejection.UnsupportedMonster, $"{monster.Id.Entry} 没有静态值 {name}");
        }
        return new FlashMonsterRules { Nodes = nodes, InitialNode = initial, StaticInts = staticInts };
    }

    private static FlashMonsterNode CompileMove(MoveState move, BranchMonsterStaticSnapshot statics,
        Dictionary<string, int> index, MonsterModel monster)
    {
        if (move.MustPerformOnceBeforeTransitioning)
            throw new FlashImportException(FlashRejection.UnsupportedMonster, $"{monster.Id.Entry}.{move.Id} 必须执行一次");
        string? followUp = move.FollowUpState?.Id ?? move.FollowUpStateId;
        IReadOnlyList<BranchMonsterAttack> attacks = statics.AttacksByMove.TryGetValue(move.Id, out var captured) ? captured : [];
        return new FlashMonsterNode
        {
            NativeId = move.Id,
            Kind = FlashMonsterNodeKind.Move,
            FollowUp = followUp != null && index.TryGetValue(followUp, out int target) ? target : -1,
            Attacks = attacks.Select(attack => new FlashMonsterAttack(attack.BaseDamage, attack.Repeats)).ToArray(),
        };
    }
}
