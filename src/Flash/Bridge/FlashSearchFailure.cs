using CombatSolver.Flash.Search;
using CombatSolver.Flash.Search.Scoring;
using CombatSolver.Flash.Bridge;
using CombatSolver.Flash.Kernel;

namespace CombatSolver.Flash.Bridge;

/// <summary>
/// The stable categories of a Flash solve that refuses to continue in On mode. A no-route result is
/// not one of these: it is reported with the existing search's own no-route shape
/// (<see cref="PotionPolicyUnsatisfiedException"/> or a normal outcome) so callers keep their policy.
/// </summary>
internal enum FlashSearchFailureReason
{
    /// <summary>The existing engine replayed a step and disagreed with the Flash projection.</summary>
    VerifyMismatch,

    /// <summary>The captured root is not the ordinary player/Play root or the modeled player/Start root.</summary>
    RootPhase,

    /// <summary>A development-only ranking strategy is active; On mode has no equivalent ordering.</summary>
    DevelopmentStrategy,

    /// <summary>The kernel reached a semantic it does not model (<c>FlashUnsupportedException</c>).</summary>
    KernelUnsupported,

    /// <summary>A root import or search state exceeded a kernel capacity (<c>FlashCapacityException</c>).</summary>
    KernelCapacity,

    /// <summary>
    /// Flash selected a route that the existing engine's final ordering or potion-route verification
    /// rejects under the request's potion policy. This is a two-sided judgement mismatch, not a
    /// no-route result: the no-route category is only <c>flash-potion-policy-no-route</c>.
    /// </summary>
    PotionPolicyMismatch,

    /// <summary>Admission rejected multiplayer-only content.</summary>
    AdmissionMultiPlayer,

    /// <summary>Admission rejected Mock/test content.</summary>
    AdmissionTestContent,

    /// <summary>Admission rejected deprecated content.</summary>
    AdmissionDeprecatedContent,

    /// <summary>Admission rejected a card, power, relic, potion, orb, monster, enchantment or affliction.</summary>
    AdmissionUnsupportedContent,

    /// <summary>Admission rejected a request shape Flash does not implement.</summary>
    AdmissionUnsupportedRequest,

    /// <summary>Admission rejected an estimated root capacity.</summary>
    AdmissionCapacity,

    /// <summary>Admission could not project part of the existing engine's root state.</summary>
    AdmissionUnrepresentableState,

    /// <summary>The root carries a pending choice or execution continuation.</summary>
    AdmissionPendingChoice,

    /// <summary>A remaining admission rejection without a more specific category.</summary>
    AdmissionOther,
}

internal static class FlashSearchFailureReasons
{
    /// <summary>The stable token written to <c>FLASH_SEARCH_FAILED reason=</c> and read by triage.</summary>
    public static string Token(this FlashSearchFailureReason reason)
        => reason switch
        {
            FlashSearchFailureReason.VerifyMismatch => "verify-mismatch",
            FlashSearchFailureReason.RootPhase => "root-phase",
            FlashSearchFailureReason.DevelopmentStrategy => "development-strategy",
            FlashSearchFailureReason.KernelUnsupported => "kernel-unsupported",
            FlashSearchFailureReason.KernelCapacity => "kernel-capacity",
            FlashSearchFailureReason.PotionPolicyMismatch => "potion-policy-mismatch",
            FlashSearchFailureReason.AdmissionMultiPlayer => "admission-multiplayer",
            FlashSearchFailureReason.AdmissionTestContent => "admission-test-content",
            FlashSearchFailureReason.AdmissionDeprecatedContent => "admission-deprecated-content",
            FlashSearchFailureReason.AdmissionUnsupportedContent => "admission-unsupported-content",
            FlashSearchFailureReason.AdmissionUnsupportedRequest => "admission-unsupported-request",
            FlashSearchFailureReason.AdmissionCapacity => "admission-capacity",
            FlashSearchFailureReason.AdmissionUnrepresentableState => "admission-unrepresentable-state",
            FlashSearchFailureReason.AdmissionPendingChoice => "admission-pending-choice",
            FlashSearchFailureReason.AdmissionOther => "admission-other",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        };

    /// <summary>
    /// Maps an admission rejection to the failure category reported to Runtime and the issue bundle.
    /// The reason keeps the admission category; the detail keeps the concrete content id.
    /// </summary>
    public static FlashSearchFailureReason FromRejection(FlashRejection rejection)
        => rejection switch
        {
            FlashRejection.MultiPlayer => FlashSearchFailureReason.AdmissionMultiPlayer,
            FlashRejection.TestContent => FlashSearchFailureReason.AdmissionTestContent,
            FlashRejection.DeprecatedContent => FlashSearchFailureReason.AdmissionDeprecatedContent,
            FlashRejection.UnsupportedCard
                or FlashRejection.UnsupportedPower
                or FlashRejection.UnsupportedRelic
                or FlashRejection.UnsupportedPotion
                or FlashRejection.UnsupportedOrb
                or FlashRejection.UnsupportedMonster
                or FlashRejection.UnsupportedEnchantment
                or FlashRejection.UnsupportedAffliction
                or FlashRejection.UnsupportedClosure => FlashSearchFailureReason.AdmissionUnsupportedContent,
            FlashRejection.UnsupportedRequest => FlashSearchFailureReason.AdmissionUnsupportedRequest,
            FlashRejection.Capacity => FlashSearchFailureReason.AdmissionCapacity,
            FlashRejection.UnrepresentableState => FlashSearchFailureReason.AdmissionUnrepresentableState,
            FlashRejection.PendingChoice => FlashSearchFailureReason.AdmissionPendingChoice,
            FlashRejection.RootPhase => FlashSearchFailureReason.RootPhase,
            FlashRejection.DevelopmentStrategy => FlashSearchFailureReason.DevelopmentStrategy,
            _ => FlashSearchFailureReason.AdmissionOther,
        };
}

/// <summary>
/// The complete content of one <c>FLASH_VERIFY_MISMATCH</c>. The stable event line and
/// <see cref="FlashSearchFailedException.Detail"/> stay bounded for logs and issue bundles; the
/// offline host writes this record into its result JSON so the full route and both states survive.
/// </summary>
internal sealed record FlashVerificationFailure(
    int Step,
    string Action,
    string Plan,
    IReadOnlyList<string> Differences,
    IReadOnlyList<string> StateBefore,
    IReadOnlyList<string> OldAfter,
    IReadOnlyList<string> FlashAfter,
    string? DirectPurpose,
    string? ContinuationPurpose,
    IReadOnlyList<string> Prefix);

/// <summary>
/// The stable, search-owned failure of an On-mode Flash solve. Search reports it through
/// <see cref="SearchDiagnosticsSink"/> and the faulted search task; Runtime keeps its existing
/// failed-search path (stop this turn's deployment, clear the session, record the issue bundle entry
/// and show the localized message). Unsupported content and unrepresentable state are the two
/// admission categories that instead switch the request to the regular solver before this failure
/// is constructed; every other category never falls back.
/// </summary>
internal sealed class FlashSearchFailedException : InvalidOperationException
{
    private const int MaximumDetailLength = 320;

    public FlashSearchFailedException(FlashSearchFailureReason reason, string detail,
        FlashVerificationFailure? verification = null)
        : base(BuildEventLine(reason, detail))
    {
        Reason = reason;
        ReasonToken = reason.Token();
        Detail = NormalizeDetail(detail);
        EventLine = BuildEventLine(reason, detail);
        Verification = verification;
    }

    public FlashSearchFailureReason Reason { get; }

    /// <summary>The stable <c>reason=</c> token, e.g. <c>verify-mismatch</c>.</summary>
    public string ReasonToken { get; }

    /// <summary>Full mismatch content for offline evidence; null for every other failure category.</summary>
    public FlashVerificationFailure? Verification { get; }

    /// <summary>The first difference or admission detail, normalized to one bounded line.</summary>
    public string Detail { get; }

    /// <summary><c>FLASH_SEARCH_FAILED reason=&lt;类别&gt; detail=&lt;首个差异/拒绝原因&gt;</c>.</summary>
    public string EventLine { get; }

    /// <summary>
    /// Runtime already logs the event and records the issue bundle entry. Search writes this line via
    /// its diagnostics sink exactly once so offline hosts and the combat log can count it by grep.
    /// </summary>
    public string DiagnosticLine => $"[CombatSolver/Flash] {EventLine}";

    private static string BuildEventLine(FlashSearchFailureReason reason, string detail)
        => $"FLASH_SEARCH_FAILED reason={reason.Token()} detail={NormalizeDetail(detail)}";

    private static string NormalizeDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "-";
        string normalized = string.Join(' ', detail.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaximumDetailLength
            ? normalized
            : normalized[..MaximumDetailLength] + "…";
    }
}
