using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Flash;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver.FlashBeam;

/// <summary>
/// Builds the <see cref="FlashScorer"/> for one request: compiles the per-content evaluation values
/// from the existing engine's models at the root, and freezes the request settings the evaluation
/// reads. Settings the scorer does not model are rejected with <see cref="FlashRejection.UnsupportedRequest"/>.
/// </summary>
internal static class FlashScoreBridge
{
    public static FlashScorer Create(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        SimulationSnapshot rootSnapshot,
        SimulationSnapshot searchRootSnapshot,
        Player player,
        FlashRules rules,
        in FlashBattle flashRoot)
    {
        RejectUnsupportedSettings(root, policy, profile);

        FlashCardScore?[][] cards = CompileCards(player);
        FlashPowerScore[] powers = CompilePowers();
        int[] potionCosts = CompilePotionCosts(player, rootSnapshot);
        ValidateAdmittedCards(flashRoot, cards);
        ValidateMonsterSpecials(root);

        ushort[] rootPotions = new ushort[FlashLimits.PotionSlots];
        for (int slot = 0; slot < FlashLimits.PotionSlots && slot < flashRoot.PotionSlotCount; slot++)
            rootPotions[slot] = flashRoot.Potions[slot];
        FlashPotionUsageForBridge potionUsage =
            CombatBeamSolver.CapturePotionUsageForFlash(searchRootSnapshot);

        FlashScoreRules score = new()
        {
            Rules = rules,
            Cards = cards,
            Powers = powers,
            PotionStrategicCost = potionCosts,
            InitialPlayerMaxHp = root.InitialPlayerMaxHp,
            IsActEndingBoss = root.IsActEndingBoss,
            BaseScoreOnly = profile.BaseScoreOnly,
            BeamWeightPerturbation = profile.BeamWeightPerturbation,
            IgnoreLongTermRewards = policy.IgnoreLongTermRewards,
            TheftPolicy = policy.TheftPolicy,
            InitialEnemyCount = root.Enemies.Count,
            InitialPersistentBuffValue = rootSnapshot.PersistentBuffValue,
            InitialEnemyStrengthSuppression = rootSnapshot.EnemyStrengthSuppression,
            InitialEnemyWeakTurns = rootSnapshot.EnemyWeakTurns,
            InitialRetainedAttackValue = rootSnapshot.RetainedAttackValue,
            RootPotions = rootPotions,
            RootPotionSlotCount = flashRoot.PotionSlotCount,
            InitialPotionUseCount = potionUsage.PotionUseCount,
            InitialPotionStrategicCost = potionUsage.PotionStrategicCost,
            InitialExplicitPotionStrategicCost = potionUsage.ExplicitPotionStrategicCost,
            StrengthPowerId = PowerId<StrengthPower>(),
            WeakPowerId = PowerId<WeakPower>(),
            VulnerablePowerId = PowerId<VulnerablePower>(),
            PoisonPowerId = PowerId<PoisonPower>(),
            DemisePowerId = PowerId<DemisePower>(),
            DoomPowerId = PowerId<DoomPower>(),
            SummonNextTurnPowerId = PowerId<SummonNextTurnPower>(),
            DieForYouPowerId = PowerId<DieForYouPower>(),
            SleightOfFleshPowerId = PowerId<SleightOfFleshPower>(),
            RetainHandPowerId = PowerId<RetainHandPower>(),
            EnergyNextTurnPowerId = PowerId<EnergyNextTurnPower>(),
            DrawCardsNextTurnPowerId = PowerId<DrawCardsNextTurnPower>(),
            StarNextTurnPowerId = PowerId<StarNextTurnPower>(),
            FreeAttackPowerId = PowerId<FreeAttackPower>(),
            FreeSkillPowerId = PowerId<FreeSkillPower>(),
            FreePowerPowerId = PowerId<FreePowerPower>(),
            IllusionPowerId = PowerId<IllusionPower>(),
            ReattachPowerId = PowerId<ReattachPower>(),
            AdaptablePowerId = PowerId<AdaptablePower>(),
            BodySlamCardId = CardId<BodySlam>(),
            BufferPowerId = PowerId<MegaCrit.Sts2.Core.Models.Powers.BufferPower>(),
            NoDrawPowerId = PowerId<NoDrawPower>(),
            TheGambitPowerId = PowerId<TheGambitPower>(),
            FairyInABottlePotionId = PotionId<FairyInABottle>(),
            AmbergrisPotionId = PotionId<Ambergris>(),
            LizardTailRelicId = RelicId<LizardTail>(),
        };
        return new FlashScorer(score);
    }

    private static void RejectUnsupportedSettings(
        CombatRootSnapshot root, SearchPolicySnapshot policy, SolverSearchProfile profile)
    {
        // A growth card in the deck alone does not move the snapshot: GrowthHpCredit is the configured
        // budget multiplied by the realized rewards, so it stays zero while every budget is zero. Only
        // non-zero (effective) budgets need the unported credit path.
        if (policy.EffectiveGrowthBudgets != default)
            throw Reject("GrowthBudgets", "成长额度（GrowthHpCredit）未移植");
        if (policy.RelicTargets.Count != 0)
            throw Reject("RelicTargets", $"遗物计数目标 {policy.RelicTargets.Count} 项未移植");
        if (policy.Act3BossStrategy)
            throw Reject("Act3BossStrategy", "Act 3 Boss 交互估值未移植");
        if (policy.DevelopmentStrategy != null)
            throw Reject("DevelopmentStrategy", "开发策略的排序调整未移植");
        if (!StrategicEffectMirrors.IsEmpty)
            throw Reject("StrategicEffectMirrors", "第三方战略效果登记表非空");
        if (!CardRemovalValueMirrors.IsEmpty)
            throw Reject("CardRemovalValueMirrors", "卡牌移除估值登记表非空，牌库杂质判定未移植");
        if (profile.ContinuousThreatRanking)
            throw Reject("Profile.ContinuousThreatRanking", "连续致命意图定价未移植");
        if (profile.ContextualRanking != null)
            throw Reject("Profile.ContextualRanking", "上下文排序模型未移植");
        if (TheftApplicable(root))
            throw Reject("TheftEncounter", "偷窃战未移植（OutstandingStolenResource 计数）");
    }

    private static bool TheftApplicable(CombatRootSnapshot root)
    {
        for (int index = 0; index < root.Enemies.Count; index++)
        {
            string? name = root.Enemies[index].Monster?.GetType().Name;
            if (name is "GremlinMerc" or "ThievingHopper" or "FatGremlin")
                return true;
        }
        return false;
    }

    private static void ValidateMonsterSpecials(CombatRootSnapshot root)
    {
        for (int index = 0; index < root.Enemies.Count; index++)
        {
            MonsterModel? monster = root.Enemies[index].Monster;
            if (monster is WaterfallGiant)
                throw new FlashImportException(FlashRejection.UnsupportedMonster,
                    $"{monster.Id.Entry} 的 AdjustMonsterMoveDamage 特例未移植");
        }
    }

    private static void ValidateAdmittedCards(in FlashBattle flashRoot, FlashCardScore?[][] cards)
    {
        for (int position = 0; position < flashRoot.CardCount; position++)
        {
            ref readonly FlashCard card = ref flashRoot.Cards[position];
            FlashCardScore score = Score(cards, card.Id, card.Upgrade, FlashRegistry.Card(card.Id).NativeId);
            if (score.Calculated == FlashCalculatedKind.Unsupported)
            {
                throw new FlashImportException(FlashRejection.UnsupportedCard,
                    $"{FlashRegistry.Card(card.Id).NativeId} 的计算伤害乘数未移植");
            }
        }
    }

    private static FlashCardScore?[][] CompileCards(Player player)
    {
        Dictionary<string, CardModel> canonical = [];
        foreach (CardModel card in ModelDb.All.OfType<CardModel>())
            canonical.TryAdd(card.Id.Entry, card);
        FlashCardScore?[][] result = new FlashCardScore?[FlashRegistry.CardCount][];
        for (ushort id = 0; id < FlashRegistry.CardCount; id++)
        {
            FlashCardDef def = FlashRegistry.Card(id);
            if (!canonical.TryGetValue(def.NativeId, out CardModel? model))
                continue;
            int maxUpgrade = model.MaxUpgradeLevel;
            FlashCardScore?[] levels = new FlashCardScore?[maxUpgrade + 1];
            for (int upgrade = 0; upgrade <= maxUpgrade; upgrade++)
                levels[upgrade] = CompileCard(def, model, upgrade, player);
            result[id] = levels;
        }
        return result;
    }

    private static FlashCardScore CompileCard(
        FlashCardDef def, CardModel canonical, int upgrade, Player player)
    {
        CardModel card = PredictionUtils.CreateCard(canonical, player);
        for (int level = 0; level < upgrade; level++)
            PredictionUtils.UpgradeCard(card);
        if (card.CurrentUpgradeLevel != upgrade)
        {
            throw new FlashImportException(FlashRejection.UnsupportedCard,
                $"{canonical.Id.Entry} 无法升级到 {upgrade}");
        }

        bool hasBlockVar = false, hasDebuffVar = false;
        foreach (KeyValuePair<string, MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar> entry
            in new DynamicVarSetAccess.EntryEnumerable(card.DynamicVars))
        {
            hasBlockVar |= IsBlockDynamicVar(entry.Key);
            hasDebuffVar |= IsDebuffDynamicVar(entry.Key);
        }

        IReadOnlyList<string> names = def.VarNames;
        return new FlashCardScore
        {
            BaseDamage = DynamicVarBaseValue(card.DynamicVars, "Damage"),
            BaseBlock = DynamicVarBaseValue(card.DynamicVars, "Block"),
            BaseCards = DynamicVarBaseValue(card.DynamicVars, "Cards"),
            IsPower = card.Type == CardType.Power,
            DamageVar = VarIndex(names, "Damage"),
            BlockVar = VarIndex(names, "Block"),
            CardsVar = VarIndex(names, "Cards"),
            RepeatVar = VarIndex(names, "Repeat"),
            ShivCardsVar = VarIndex(names, "Cards"),
            ShivShivsVar = VarIndex(names, "Shivs"),
            CalculationBaseVar = VarIndex(names, "CalculationBase"),
            CalculationExtraVar = names.Contains("CalculationExtra")
                ? VarIndex(names, "CalculationExtra")
                : VarIndex(names, "ExtraDamage"),
            DoomThresholdVar = VarIndex(names, "DoomThreshold"),
            AttackHits = card switch
            {
                TwinStrike or RipAndTear => FlashAttackHitKind.Two,
                SwordBoomerang or SovereignBlade => FlashAttackHitKind.RepeatVar,
                _ => FlashAttackHitKind.One,
            },
            ShivSupply = card switch
            {
                BladeDance or CloakAndDagger => FlashShivSupplyKind.CardsVar,
                FanOfKnives => FlashShivSupplyKind.ShivsVar,
                _ => FlashShivSupplyKind.None,
            },
            HasBlockDynamicVar = hasBlockVar,
            HasDebuffDynamicVar = hasDebuffVar,
            HasTurnEndInHandEffect = card.HasTurnEndInHandEffect,
            HasOstyAttackTag = card.Tags.Contains(CardTag.OstyAttack),
            HasShivTag = card.Tags.Contains(CardTag.Shiv),
            LatentValue = LatentValue(card),
            LatentTrait = LatentTrait(card),
            Calculated = CalculatedKind(card),
        };
    }

    private static sbyte VarIndex(IReadOnlyList<string> names, string name)
    {
        for (int index = 0; index < names.Count; index++)
        {
            if (names[index] == name)
                return checked((sbyte)index);
        }
        return -1;
    }

    private static double DynamicVarBaseValue(
        MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet vars, string key)
        => vars.TryGetValue(key, out MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar? variable)
            ? (double)variable.BaseValue
            : 0d;

    private static bool IsDebuffDynamicVar(string key)
        => key.Contains("Weak", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Vulnerable", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Poison", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Doom", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Debuff", StringComparison.OrdinalIgnoreCase)
            || key.Contains("StrengthLoss", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockDynamicVar(string key)
        => key.Contains("Block", StringComparison.OrdinalIgnoreCase);

    private static FlashCalculatedKind CalculatedKind(CardModel card)
    {
        if (!card.DynamicVars.ContainsKey("CalculatedDamage")
            || !CalculatedVarSpecRegistry.SupportedTypes.Contains(card.GetType()))
        {
            return FlashCalculatedKind.None;
        }
        return card switch
        {
            BodySlam or DemonicShield => FlashCalculatedKind.PlayerBlock,
            PerfectedStrike => FlashCalculatedKind.StrikeCardCount,
            AshenStrike => FlashCalculatedKind.ExhaustPileCount,
            ExpectAFight => FlashCalculatedKind.PlayerStrength,
            Bully => FlashCalculatedKind.TargetVulnerable,
            MindBlast => FlashCalculatedKind.DrawPileCount,
            Rend => FlashCalculatedKind.TargetDebuffPowerCount,
            GoldAxe => FlashCalculatedKind.FinishedCardPlays,
            MementoMori => FlashCalculatedKind.CardsDiscardedThisTurn,
            PreciseCut => FlashCalculatedKind.NegativeHandCount,
            Murder => FlashCalculatedKind.CardsDrawn,
            CrescentSpear => FlashCalculatedKind.StarCardCount,
            Unleash => FlashCalculatedKind.OstyCurrentHp,
            Rattle => FlashCalculatedKind.OstyAttacksPlusOne,
            Squeeze => FlashCalculatedKind.OstyAttackCardsMinusSelf,
            Protector => FlashCalculatedKind.OstyMaxHp,
            SoulStorm => FlashCalculatedKind.ExhaustSoulCount,
            PullFromBelow => FlashCalculatedKind.EtherealPlays,
            NoEscape => FlashCalculatedKind.DoomOverThreshold,
            TimesUp => FlashCalculatedKind.TargetDoom,
            DeathMarch => FlashCalculatedKind.NonHandDraws,
            _ => FlashCalculatedKind.Unsupported,
        };
    }

    private static int LatentValue(CardModel card) => card switch
    {
        EchoForm => 12,
        MegaCrit.Sts2.Core.Models.Cards.Buffer => 8,
        Defragment or Hotfix => 6,
        LightningRod => 4,
        Thunder => 3,
        DemonForm or CreativeAi => 5,
        MasterPlanner => 4,
        _ when card.Type == CardType.Power => 2,
        _ => 0,
    };

    private static FlashSetupTraits LatentTrait(CardModel card) => card switch
    {
        EchoForm => FlashSetupTraits.EchoForm,
        MegaCrit.Sts2.Core.Models.Cards.Buffer => FlashSetupTraits.Buffer,
        Defragment or Hotfix => FlashSetupTraits.Focus,
        LightningRod => FlashSetupTraits.OrbEngine,
        Thunder => FlashSetupTraits.Thunder,
        DemonForm or CreativeAi => FlashSetupTraits.RecurringScaling,
        _ => FlashSetupTraits.None,
    };

    private static FlashPowerScore[] CompilePowers()
    {
        Dictionary<string, PowerModel> canonical = [];
        foreach (PowerModel power in ModelDb.All.OfType<PowerModel>())
            canonical.TryAdd(power.Id.Entry, power);
        FlashPowerScore[] result = new FlashPowerScore[FlashRegistry.PowerCount];
        for (ushort id = 0; id < FlashRegistry.PowerCount; id++)
        {
            FlashPowerDef def = FlashRegistry.Power(id);
            if (!canonical.TryGetValue(def.NativeId, out PowerModel? model))
                throw new InvalidOperationException($"原版没有 Power {def.NativeId}。");
            result[id] = new FlashPowerScore
            {
                Kind = StrategicKind(model),
                Requirements = (FlashStrategicRequirements)(int)StrategicEffectModel.Requirements(model, false),
                Trait = PersistentPowerTrait(model),
                IsTemporary = model is ITemporaryPower,
                Type = (FlashPowerType)(byte)model.Type,
            };
        }
        return result;
    }

    private static FlashStrategicKind StrategicKind(PowerModel power) => power switch
    {
        AfterimagePower => FlashStrategicKind.Afterimage,
        MegaCrit.Sts2.Core.Models.Powers.BufferPower => FlashStrategicKind.Buffer,
        FeelNoPainPower => FlashStrategicKind.FeelNoPain,
        DarkEmbracePower => FlashStrategicKind.DarkEmbrace,
        ThornsPower => FlashStrategicKind.Thorns,
        EchoFormPower => FlashStrategicKind.EchoForm,
        EnvenomPower => FlashStrategicKind.Envenom,
        AccuracyPower => FlashStrategicKind.Accuracy,
        SleightOfFleshPower => FlashStrategicKind.SleightOfFlesh,
        StrengthPower => FlashStrategicKind.Strength,
        LethalityPower => FlashStrategicKind.Lethality,
        ReaperFormPower => FlashStrategicKind.ReaperForm,
        DexterityPower => FlashStrategicKind.Dexterity,
        DemonFormPower => FlashStrategicKind.DemonForm,
        CuriousPower => FlashStrategicKind.Curious,
        CorruptionPower => FlashStrategicKind.Corruption,
        CreativeAiPower => FlashStrategicKind.CreativeAi,
        IterationPower => FlashStrategicKind.Iteration,
        MasterPlannerPower => FlashStrategicKind.MasterPlanner,
        FocusPower => FlashStrategicKind.Focus,
        FurnacePower => FlashStrategicKind.Furnace,
        ThunderPower => FlashStrategicKind.Thunder,
        LightningRodPower => FlashStrategicKind.LightningRod,
        AutomationPower => FlashStrategicKind.RecurringEnergy,
        _ => FlashStrategicKind.Scaling,
    };

    private static FlashSetupTraits PersistentPowerTrait(PowerModel power) => power switch
    {
        CuriousPower => FlashSetupTraits.Curious,
        EchoFormPower => FlashSetupTraits.EchoForm,
        MegaCrit.Sts2.Core.Models.Powers.BufferPower => FlashSetupTraits.Buffer,
        FocusPower => FlashSetupTraits.Focus,
        ThunderPower => FlashSetupTraits.Thunder,
        LightningRodPower => FlashSetupTraits.OrbEngine,
        DemonFormPower or CreativeAiPower => FlashSetupTraits.RecurringScaling,
        _ => FlashSetupTraits.None,
    };

    /// <summary>
    /// <c>PotionUsePolicy.StrategicHpCost</c> per canonical potion. Potion Shaped Rock is renewable when
    /// Petrified Toad is held, exactly as <c>SimulatedCombatState.ConsumePotionAtSlot</c> decides.
    /// </summary>
    private static int[] CompilePotionCosts(Player player, SimulationSnapshot rootSnapshot)
    {
        Dictionary<string, PotionModel> canonical = [];
        foreach (PotionModel potion in ModelDb.All.OfType<PotionModel>())
            canonical.TryAdd(potion.Id.Entry, potion);
        SimulatedCombatState combat = (SimulatedCombatState)rootSnapshot.Simulator.State.CombatState;
        bool renewablePotionShapedRock = combat.RelicsOf(player)
            .OfType<PetrifiedToad>().Any(relic => !relic.IsMelted);
        int[] costs = new int[FlashRegistry.PotionCount];
        for (ushort id = 0; id < FlashRegistry.PotionCount; id++)
        {
            string nativeId = FlashRegistry.Potion(id).NativeId;
            if (!canonical.TryGetValue(nativeId, out PotionModel? potion))
                throw new InvalidOperationException($"原版没有药水 {nativeId}。");
            costs[id] = PotionUsePolicy.StrategicHpCost(potion, renewablePotionShapedRock);
        }
        return costs;
    }

    private static FlashCardScore Score(
        FlashCardScore?[][] cards, ushort id, int upgrade, string nativeId)
        => id < cards.Length
            && cards[id] is { } levels
            && (uint)upgrade < (uint)levels.Length
            && levels[upgrade] is { } score
                ? score
                : throw new FlashImportException(FlashRejection.UnsupportedCard,
                    $"评分表缺少卡牌 {nativeId}+{upgrade}");

    /// <summary>Flash id of a native power, resolved through the canonical model's own id entry.</summary>
    private static ushort PowerId<TPower>() where TPower : PowerModel
        => FlashRegistry.TryPower(ModelDb.Power<TPower>().Id.Entry, out ushort id) ? id : ushort.MaxValue;

    private static ushort CardId<TCard>() where TCard : CardModel
        => FlashRegistry.TryCard(ModelDb.Card<TCard>().Id.Entry, out ushort id) ? id : ushort.MaxValue;

    internal static ushort PotionId<TPotion>() where TPotion : PotionModel
        => FlashRegistry.TryPotion(ModelDb.Potion<TPotion>().Id.Entry, out ushort id) ? id : ushort.MaxValue;

    private static ushort RelicId<TRelic>() where TRelic : RelicModel
        => FlashRegistry.TryRelic(ModelDb.Relic<TRelic>().Id.Entry, out ushort id) ? id : ushort.MaxValue;

    private static FlashImportException Reject(string setting, string detail)
        => new(FlashRejection.UnsupportedRequest, $"{setting}：{detail}");
}
