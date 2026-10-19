using System.Diagnostics;
using System.Globalization;

namespace CombatSolver;

/// <summary>
/// The permanent observability line for a legacy <see cref="CombatBeamSolver"/> search that runs while
/// Flash is On. Off and Shadow are comparison modes and never write it.
/// <para>
/// The stable prefix is <c>[CombatSolver/Flash] FLASH_LEGACY purpose=&lt;用途&gt; reason=&lt;原因&gt;</c>;
/// the trailing <c>ms=</c> is the measured legacy search interval. Reason codes come from the FI
/// inventory (docs/flash-search/search.md) and must stay stable enough to count by grep.
/// </para>
/// </summary>
internal static class FlashLegacyLog
{
    internal static void Write(SearchDiagnosticsSink diagnostics, string purpose, string reason,
        TimeSpan elapsed)
        => diagnostics.Info(string.Create(CultureInfo.InvariantCulture,
            $"[CombatSolver/Flash] FLASH_LEGACY purpose={purpose} reason={reason} ms={elapsed.TotalMilliseconds:0}"));

    /// <summary>Runs a legacy-only direct search and logs it when the request is in On mode.</summary>
    internal static SolverResult Run(SearchPolicySnapshot policy, string purpose, string reason,
        Func<SolverResult> solve)
    {
        if (policy.FlashMode != FlashSearchMode.On)
            return solve();
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            return solve();
        }
        finally
        {
            Write(policy.Diagnostics, purpose, reason, watch.Elapsed);
        }
    }
}
