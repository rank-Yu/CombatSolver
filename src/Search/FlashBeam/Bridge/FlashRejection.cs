namespace CombatSolver.FlashBeam;

/// <summary>Stable admission rejection codes; see docs/flash-search/admission-and-verification.md.</summary>
internal enum FlashRejection
{
    None = 0,
    MultiPlayer,
    RootPhase,
    PendingChoice,
    ThirdParty,
    UnsupportedCard,
    UnsupportedPower,
    UnsupportedRelic,
    UnsupportedPotion,
    UnsupportedOrb,
    UnsupportedMonster,
    UnsupportedEnchantment,
    UnsupportedAffliction,
    UnsupportedClosure,
    UnrepresentableState,
    Capacity,
    UnsupportedRequest,
}

/// <summary>The existing engine's state cannot be projected into Flash; the request falls back.</summary>
internal sealed class FlashImportException(FlashRejection reason, string detail)
    : Exception($"Flash 准入拒绝 {reason}：{detail}")
{
    public FlashRejection Reason { get; } = reason;

    public string Detail { get; } = detail;
}
