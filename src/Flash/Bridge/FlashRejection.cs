using CombatSolver.Flash.Kernel;
using CombatSolver.Flash.Search;
using CombatSolver.Flash.Search.Scoring;

namespace CombatSolver.Flash.Bridge;

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

    /// <summary>Mock/test content (MockCardPool and MOCK_* powers); never part of a vanilla run.</summary>
    TestContent,

    /// <summary>Deprecated content (DeprecatedCardPool); kept by the game only for old saves.</summary>
    DeprecatedContent,

    /// <summary>
    /// A development-only ranking strategy is active. Off mode still supports it; On mode reports an
    /// explicit <c>development-strategy</c> failure because Flash has no equivalent ordering hooks.
    /// </summary>
    DevelopmentStrategy,
}

/// <summary>
/// Admission cannot express the existing engine's root in Flash. In On mode this is an explicit
/// <see cref="FlashSearchFailedException"/>; Off and Shadow keep their existing behavior.
/// </summary>
internal sealed class FlashImportException(FlashRejection reason, string detail)
    : Exception($"Flash 准入拒绝 {reason}：{detail}")
{
    public FlashRejection Reason { get; } = reason;

    public string Detail { get; } = detail;
}
