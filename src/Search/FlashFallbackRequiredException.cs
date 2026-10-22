using CombatSolver.Flash.Bridge;

namespace CombatSolver;

/// <summary>
/// One coordinator search cannot be represented by the Flash backend because of admission content or
/// an unrepresentable state. The coordinator must discard this whole attempt and re-run it with the
/// regular policy; this exception is never a stable user-facing failure by itself. It lives in
/// <c>src/Search</c> so the coordinator does not reference the Flash module directly.
/// </summary>
internal sealed class FlashFallbackRequiredException(
    FlashSearchFailureReason reason, string detail, string purpose)
    : Exception($"Flash fallback required: {reason.Token()} detail={detail} purpose={purpose}")
{
    /// <summary>The stable fallback record shared with Runtime and the issue bundle.</summary>
    public FlashFallbackNotice Notice { get; } = new(reason.Token(), detail, purpose);

    internal FlashSearchFailureReason Reason { get; } = reason;

    internal string Detail { get; } = detail;
}

internal static class FlashFallbackFailure
{
    /// <summary>
    /// The exact closed-mode profile saved before Flash budgets were installed. A missing value is a
    /// capture defect: falling back to the Flash profile or scaling it would change the search.
    /// </summary>
    public static SolverSearchProfile RequireRegularProfile(SearchPolicySnapshot policy)
        => policy.RegularProfile
           ?? throw new InvalidOperationException(
               "开启模式 Flash 准入改用常规搜索，但策略快照缺少换档前的 RegularProfile；" +
               "这是分发或设置捕获缺陷，不能按 Flash 档位或比例缩放兜底。");

    /// <summary>
    /// A fallback arrived after this coordinator search already published a route, route seed or
    /// preview. Re-running would mix two attempts, so keep the original explicit failure contract.
    /// </summary>
    public static void ThrowPublished(
        FlashFallbackRequiredException fallback,
        SearchPolicySnapshot policy)
    {
        FlashSearchFailedException failure = new(fallback.Reason, fallback.Detail);
        policy.Diagnostics.Info(failure.DiagnosticLine);
        throw failure;
    }
}
