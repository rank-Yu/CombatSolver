using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

/// <summary>
/// Read-only classification entry points for the Flash healing-bound port. The existing engine stays the
/// semantic authority: the Flash bridge freezes the per-content answers of these same predicates at the
/// root, and the Flash evaluation only reads the frozen table.
/// </summary>
internal static partial class StrategicHpRecoveryBound
{
    internal static bool IsComponentHealingCard(CardModel card) => ComponentRemainingCard(card);

    internal static bool IsComponentHealingPower(PowerModel power) => ComponentPower(power.GetType());

    internal static bool IsComponentHealingRelic(RelicModel relic) => ComponentRelics.Contains(relic.GetType());

    internal static bool IsComponentHealingMonster(MonsterModel monster) => ComponentEnemies.Contains(monster.GetType());

    internal static bool IsComponentHealingPotion(PotionModel potion) => ComponentPotion(potion.GetType());

    internal static bool IsComponentHealingEnchantment(EnchantmentModel? enchantment)
        => enchantment is Slither or Inky
            or global::MegaCrit.Sts2.Core.Models.Enchantments.Instinct;

    internal static bool IsComponentHealingAffliction(AfflictionModel? affliction)
        => affliction is Tainted;

    internal static bool IsKnownNativeHealingRelic(RelicModel relic)
        => relic is DemonTongue or BookOfFiveRings or BookRepairKnife or LizardTail
            or DragonFruit or DarkstonePeriapt or ChosenCheese;

    internal static bool IsKnownNativeHealingCard(CardModel card) => card is NotYet or Feed;

    internal static bool IsRegenPower(PowerModel power) => power is RegenPower;

    internal static bool IsReattachPower(PowerModel power) => power is ReattachPower;

    internal static bool IsSwipePower(PowerModel power) => power is SwipePower;

    internal static bool IsRegenPotion(PotionModel potion) => potion is RegenPotion;

    internal static bool IsDirectHealPotion(PotionModel potion) => potion is BloodPotion or Ambergris;

    internal static bool IsFruitJuicePotion(PotionModel potion) => potion is FruitJuice;

    internal static bool IsFairyPotion(PotionModel potion) => potion is FairyInABottle;

    internal static bool IsNativeHealingModel(AbstractModel model)
        => model.GetType().Assembly == typeof(CardModel).Assembly;

    internal static decimal DirectHealPotionPercent(PotionModel potion)
        => potion.DynamicVars["HealPercent"].BaseValue;

    internal static decimal RegenPotionAmount(PotionModel potion)
        => potion.DynamicVars["RegenPower"].IntValue;

    internal static bool IsReattachSafeOwner(MonsterModel? monster)
        => monster is DecimillipedeSegmentFront or DecimillipedeSegmentMiddle or DecimillipedeSegmentBack;
}
