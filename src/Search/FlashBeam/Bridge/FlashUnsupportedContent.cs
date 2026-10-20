using CombatSolver.Engine.InCombat.Flash;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver.FlashBeam;

/// <summary>
/// Explicit categories for the content the single-player vanilla scope deliberately does not mirror.
/// Flash implements every single-player vanilla card, power, relic, potion, monster and encounter it
/// can simulate; the remainder is rejected with a stable category instead of the generic
/// <see cref="FlashRejection.UnsupportedCard"/>/<see cref="FlashRejection.UnsupportedPower"/>
/// "undefined" reason. The inventory and the reasons are documented in
/// docs/flash-search/admission-and-verification.md.
/// </summary>
internal static class FlashUnsupportedContent
{
    public static FlashImportException Card(CardModel card)
    {
        string id = card.Id.Entry;
        if (card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
            return new FlashImportException(FlashRejection.MultiPlayer, $"{id} 是多人专用内容，单人原版流程不会进入牌组。");
        if (card.Pool is MockCardPool)
            return new FlashImportException(FlashRejection.TestContent, $"{id} 是测试/Mock 内容（MockCardPool），不属于可玩内容。");
        if (card.Pool is DeprecatedCardPool)
            return new FlashImportException(FlashRejection.DeprecatedContent, $"{id} 是已废弃内容（DeprecatedCardPool）。");
        return new FlashImportException(FlashRejection.UnsupportedCard, id);
    }

    public static FlashImportException Power(PowerModel power)
    {
        string id = power.Id.Entry;
        if (Array.IndexOf(FlashUnsupportedNativeIds.MultiplayerOnlyPowers, id) >= 0)
            return new FlashImportException(FlashRejection.MultiPlayer, $"{id} 是多人专用 Power，单人原版流程不会出现。");
        if (id.StartsWith(FlashUnsupportedNativeIds.MockPrefix, StringComparison.Ordinal))
            return new FlashImportException(FlashRejection.TestContent, $"{id} 是测试/Mock Power，不属于可玩内容。");
        return new FlashImportException(FlashRejection.UnsupportedPower, id);
    }
}
