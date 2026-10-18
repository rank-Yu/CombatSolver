using CombatSolver.Engine.InCombat.Flash;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver.FlashBeam;

/// <summary>
/// Builds <see cref="FlashBoundFeatures"/> for one request. Every predicate is asked of the existing
/// engine's canonical model at the root; per-state evaluation reads only <see cref="FlashBattle"/> and the
/// frozen table. A root whose bound cannot be given exactly is disabled here, never approximated.
/// </summary>
internal static class FlashBoundBridge
{
    public static FlashBoundFeatures Create(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SimulationSnapshot rootSnapshot,
        Player player,
        FlashRules rules,
        in FlashBattle flashRoot,
        FlashPruningRequest pruning)
    {
        string? disabled = DisabledReason(root, policy, rootSnapshot, flashRoot);
        if (disabled != null)
            return Disabled(disabled);

        Dictionary<string, CardModel> canonicalCards = [];
        foreach (CardModel card in ModelDb.All.OfType<CardModel>())
            canonicalCards.TryAdd(card.Id.Entry, card);
        Dictionary<string, PowerModel> canonicalPowers = [];
        foreach (PowerModel power in ModelDb.All.OfType<PowerModel>())
            canonicalPowers.TryAdd(power.Id.Entry, power);
        Dictionary<string, RelicModel> canonicalRelics = [];
        foreach (RelicModel relic in ModelDb.All.OfType<RelicModel>())
            canonicalRelics.TryAdd(relic.Id.Entry, relic);
        Dictionary<string, MonsterModel> canonicalMonsters = [];
        foreach (MonsterModel monster in ModelDb.All.OfType<MonsterModel>())
            canonicalMonsters.TryAdd(monster.Id.Entry, monster);
        Dictionary<string, PotionModel> canonicalPotions = [];
        foreach (PotionModel potion in ModelDb.All.OfType<PotionModel>())
            canonicalPotions.TryAdd(potion.Id.Entry, potion);

        bool[] componentUnsafeCard = new bool[FlashRegistry.CardCount];
        bool[] growthCard = new bool[FlashRegistry.CardCount];
        bool[] knownBlockingCard = new bool[FlashRegistry.CardCount];
        var generatedCards = new List<(ushort Id, int Upgrade)>[FlashRegistry.CardCount];
        for (ushort id = 0; id < FlashRegistry.CardCount; id++)
        {
            FlashCardDef def = FlashRegistry.Card(id);
            if (!canonicalCards.TryGetValue(def.NativeId, out CardModel? card))
                return Disabled($"unknown_card({def.NativeId})");
            componentUnsafeCard[id] = !StrategicHpRecoveryBound.IsComponentHealingCard(card);
            growthCard[id] = GrowthValues.HasTarget(card);
            knownBlockingCard[id] = StrategicHpRecoveryBound.IsKnownNativeHealingCard(card);
            var generated = new List<(ushort, int)>();
            foreach ((string cardId, int upgrade) in def.GeneratedCards)
            {
                if (FlashRegistry.TryCard(cardId, out ushort generatedId))
                    generated.Add((generatedId, upgrade));
            }
            generatedCards[id] = generated;
        }
        if (GrowthReachable(flashRoot, growthCard, generatedCards))
            return Disabled("feed_growth");

        bool[] componentUnsafePower = new bool[FlashRegistry.PowerCount];
        bool[] regenPower = new bool[FlashRegistry.PowerCount];
        bool[] reattachPower = new bool[FlashRegistry.PowerCount];
        bool[] swipePower = new bool[FlashRegistry.PowerCount];
        int regenPowerId = -1, reattachPowerId = -1;
        for (ushort id = 0; id < FlashRegistry.PowerCount; id++)
        {
            FlashPowerDef def = FlashRegistry.Power(id);
            if (!canonicalPowers.TryGetValue(def.NativeId, out PowerModel? power))
                return Disabled($"unknown_power({def.NativeId})");
            componentUnsafePower[id] = !StrategicHpRecoveryBound.IsComponentHealingPower(power);
            regenPower[id] = StrategicHpRecoveryBound.IsRegenPower(power);
            reattachPower[id] = StrategicHpRecoveryBound.IsReattachPower(power);
            swipePower[id] = StrategicHpRecoveryBound.IsSwipePower(power);
            if (regenPower[id])
                regenPowerId = id;
            if (reattachPower[id])
                reattachPowerId = id;
        }

        bool[] componentUnsafeRelic = new bool[FlashRegistry.RelicCount];
        bool[] knownBlockingRelic = new bool[FlashRegistry.RelicCount];
        for (ushort id = 0; id < FlashRegistry.RelicCount; id++)
        {
            FlashRelicDef def = FlashRegistry.Relic(id);
            if (!canonicalRelics.TryGetValue(def.NativeId, out RelicModel? relic))
                return Disabled($"unknown_relic({def.NativeId})");
            componentUnsafeRelic[id] = !StrategicHpRecoveryBound.IsComponentHealingRelic(relic);
            knownBlockingRelic[id] = StrategicHpRecoveryBound.IsKnownNativeHealingRelic(relic);
        }

        bool[] componentUnsafeMonster = new bool[FlashRegistry.MonsterCount];
        bool[] reattachSafeMonster = new bool[FlashRegistry.MonsterCount];
        for (ushort id = 0; id < FlashRegistry.MonsterCount; id++)
        {
            FlashMonsterDef def = FlashRegistry.Monster(id);
            if (!canonicalMonsters.TryGetValue(def.NativeId, out MonsterModel? monster))
                return Disabled($"unknown_monster({def.NativeId})");
            componentUnsafeMonster[id] = !StrategicHpRecoveryBound.IsComponentHealingMonster(monster);
            reattachSafeMonster[id] = StrategicHpRecoveryBound.IsReattachSafeOwner(monster);
        }

        bool[] componentUnsafePotion = new bool[FlashRegistry.PotionCount];
        bool[] regenPotion = new bool[FlashRegistry.PotionCount];
        bool[] directHealPotion = new bool[FlashRegistry.PotionCount];
        bool[] fruitJuicePotion = new bool[FlashRegistry.PotionCount];
        bool[] fairyPotion = new bool[FlashRegistry.PotionCount];
        bool[] nativePotion = new bool[FlashRegistry.PotionCount];
        int[] regenPotionAmount = new int[FlashRegistry.PotionCount];
        decimal[] directHealPotionPercent = new decimal[FlashRegistry.PotionCount];
        for (ushort id = 0; id < FlashRegistry.PotionCount; id++)
        {
            FlashPotionDef def = FlashRegistry.Potion(id);
            if (!canonicalPotions.TryGetValue(def.NativeId, out PotionModel? potion))
                return Disabled($"unknown_potion({def.NativeId})");
            componentUnsafePotion[id] = !StrategicHpRecoveryBound.IsComponentHealingPotion(potion);
            regenPotion[id] = StrategicHpRecoveryBound.IsRegenPotion(potion);
            directHealPotion[id] = StrategicHpRecoveryBound.IsDirectHealPotion(potion);
            fruitJuicePotion[id] = StrategicHpRecoveryBound.IsFruitJuicePotion(potion);
            fairyPotion[id] = StrategicHpRecoveryBound.IsFairyPotion(potion);
            nativePotion[id] = StrategicHpRecoveryBound.IsNativeHealingModel(potion);
            regenPotionAmount[id] = regenPotion[id]
                ? checked((int)StrategicHpRecoveryBound.RegenPotionAmount(potion))
                : 0;
            directHealPotionPercent[id] = directHealPotion[id]
                ? StrategicHpRecoveryBound.DirectHealPotionPercent(potion)
                : 0m;
        }

        bool[] safeEnchantment = new bool[256];
        foreach (EnchantmentModel enchantment in ModelDb.All.OfType<EnchantmentModel>())
        {
            if (FlashRegistry.TryEnchantment(enchantment.Id.Entry, out ushort id))
                safeEnchantment[id] = StrategicHpRecoveryBound.IsComponentHealingEnchantment(enchantment);
        }
        bool[] safeAffliction = new bool[256];
        foreach (AfflictionModel affliction in ModelDb.All.OfType<AfflictionModel>())
        {
            if (FlashRegistry.TryAffliction(affliction.Id.Entry, out ushort id))
                safeAffliction[id] = StrategicHpRecoveryBound.IsComponentHealingAffliction(affliction);
        }

        // The component certificate's RegenPotion allowance depends on the potion strategy, which the
        // Flash bound does not model. A root carrying one is disabled rather than guessed.
        if (root.UsesComponentHealingCertificate)
        {
            for (int slot = 0; slot < flashRoot.PotionSlotCount; slot++)
            {
                ushort potion = flashRoot.Potions[slot];
                if (potion != 0 && regenPotion[potion - 1])
                    return Disabled("regen_potion_policy");
            }
        }
        // KnownNativeHealingPotential ignores a SwipePower's stolen card unless the card is NotYet/Feed;
        // the Flash state does not carry that card, so any SwipePower disables the bound.
        for (int index = 0; index < flashRoot.PowerCount; index++)
        {
            if (swipePower[flashRoot.Powers[index].Id])
                return Disabled("swipe_power");
        }

        ushort[] rootPotions = new ushort[FlashLimits.PotionSlots];
        for (int slot = 0; slot < FlashLimits.PotionSlots && slot < flashRoot.PotionSlotCount; slot++)
            rootPotions[slot] = flashRoot.Potions[slot];

        FlashHealingCertificate certificate = root.UsesComponentHealingCertificate
            ? FlashHealingCertificate.Component
            : root.UsesKnownNativeHealingPolicy
                ? FlashHealingCertificate.KnownNative
                : FlashHealingCertificate.None;
        int postCombatHeal = checked(
            root.PostCombatRelicHeal.UnconditionalHeal + root.PostCombatRelicHeal.WoundedHeal);
        return new FlashBoundFeatures(new FlashBoundFeatures.Config
        {
            Enabled = true,
            DisabledReason = "",
            FutureHealPotential = root.HasOnlyPostCombatHealing
                ? Math.Max(0, root.PostCombatRelicHeal.UnconditionalHeal)
                : int.MaxValue,
            Certificate = certificate,
            PostCombatHeal = postCombatHeal,
            BossHpRelief = pruning.StrategicBossHpRelief,
            RequireCertifiedHealingProof = root.UsesComponentHealingCertificate,
            PruneEqualHp = policy.RelicTargets.Count == 0,
            ForceAllPotionsDisabled = pruning.ForceAllPotionsDisabled,
            MaximumPotionUses = pruning.MaximumPotionUses,
            ComponentUnsafeCard = componentUnsafeCard,
            ComponentUnsafePower = componentUnsafePower,
            ComponentUnsafeRelic = componentUnsafeRelic,
            ComponentUnsafeMonster = componentUnsafeMonster,
            ComponentUnsafePotion = componentUnsafePotion,
            SafeEnchantment = safeEnchantment,
            SafeAffliction = safeAffliction,
            KnownBlockingCard = knownBlockingCard,
            KnownBlockingRelic = knownBlockingRelic,
            RegenPower = regenPower,
            ReattachPower = reattachPower,
            SwipePower = swipePower,
            RegenPotion = regenPotion,
            DirectHealPotion = directHealPotion,
            FruitJuicePotion = fruitJuicePotion,
            FairyPotion = fairyPotion,
            NativePotion = nativePotion,
            RegenPotionAmount = regenPotionAmount,
            DirectHealPotionPercent = directHealPotionPercent,
            ReattachSafeMonster = reattachSafeMonster,
            RegenPowerId = regenPowerId,
            ReattachPowerId = reattachPowerId,
            RootPotions = rootPotions,
            RootPotionSlotCount = flashRoot.PotionSlotCount,
        });
    }

    private static string? DisabledReason(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SimulationSnapshot rootSnapshot,
        in FlashBattle flashRoot)
    {
        if (policy.EffectiveHasGrowthTargets)
            return "growth_targets";
        if (policy.RelicTargets.Count != 0)
            return "relic_targets";
        if (policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            return "theft_policy";
        if (root.InitialGrowthRewards.Total != 0)
            return "growth_rewards";
        if (rootSnapshot.HasRisk)
            return "root_risk";
        if (((SimulatedCombatState)rootSnapshot.Simulator.State.CombatState).HasPendingReturningCards)
            return "pending_returning_cards";
        if (root.CanCertifyRemainingHealing && !root.UsesComponentHealingCertificate)
            return "remaining_certificate";
        return null;
    }

    private static bool GrowthReachable(
        in FlashBattle flashRoot,
        bool[] growthCard,
        List<(ushort Id, int Upgrade)>[] generatedCards)
    {
        int cardCount = flashRoot.CardCount;
        for (int index = 0; index < cardCount; index++)
        {
            if (growthCard[flashRoot.Cards[index].Id])
                return true;
        }
        // A generated growth card makes every root's bucket grow after the kill. Only supported cards can
        // be generated, so the registered card defs are the complete generation closure.
        for (int id = 0; id < generatedCards.Length; id++)
        {
            if (!growthCard[id])
                continue;
            foreach (List<(ushort Id, int Upgrade)> generated in generatedCards)
            {
                foreach ((ushort generatedId, _) in generated)
                {
                    if (generatedId == id)
                        return true;
                }
            }
        }
        return false;
    }

    private static FlashBoundFeatures Disabled(string reason) => new(new FlashBoundFeatures.Config
    {
        Enabled = false,
        DisabledReason = reason,
        FutureHealPotential = int.MaxValue,
        Certificate = FlashHealingCertificate.None,
        PostCombatHeal = 0,
        BossHpRelief = BossHpRelief.None,
        RequireCertifiedHealingProof = false,
        PruneEqualHp = false,
        ForceAllPotionsDisabled = false,
        MaximumPotionUses = null,
        ComponentUnsafeCard = [],
        ComponentUnsafePower = [],
        ComponentUnsafeRelic = [],
        ComponentUnsafeMonster = [],
        ComponentUnsafePotion = [],
        SafeEnchantment = [],
        SafeAffliction = [],
        KnownBlockingCard = [],
        KnownBlockingRelic = [],
        RegenPower = [],
        ReattachPower = [],
        SwipePower = [],
        RegenPotion = [],
        DirectHealPotion = [],
        FruitJuicePotion = [],
        FairyPotion = [],
        NativePotion = [],
        RegenPotionAmount = [],
        DirectHealPotionPercent = [],
        ReattachSafeMonster = [],
        RegenPowerId = -1,
        ReattachPowerId = -1,
        RootPotions = [],
        RootPotionSlotCount = 0,
    });
}
