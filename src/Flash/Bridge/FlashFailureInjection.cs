using CombatSolver.Flash.Kernel;
using CombatSolver.Flash.Search;
using CombatSolver.Flash.Search.Scoring;

namespace CombatSolver.Flash.Bridge;

/// <summary>Test-only fault selectors for the On-mode dispatch contract.</summary>
internal enum FlashFailureInjection
{
    None = 0,

    /// <summary>Raise a verification mismatch after admission but before the kernel runs.</summary>
    VerificationMismatch,

    /// <summary>Raise the kernel's unsupported-semantic exception.</summary>
    KernelUnsupported,

    /// <summary>Raise a root-level kernel capacity exception.</summary>
    KernelCapacity,

    /// <summary>Raise an admission rejection with the multiplayer-only category.</summary>
    AdmissionMultiPlayer,

    /// <summary>Return a post-materialization route-* potion-policy mismatch.</summary>
    PotionPolicyMismatch,
}

/// <summary>
/// Test-only, single-shot fault injection for the search dispatch point. Production never arms
/// it; the offline failure contract consumes it through <see cref="Take"/>, which also clears the
/// arm so a failed assertion cannot leak into the next request. The injection is armed inside the
/// same try block as a real Flash solve, so it exercises the production catch and mapping paths
/// instead of bypassing them.
/// </summary>
internal static class FlashFailureInjectionForTesting
{
    private static int _next;
    private static int _purpose = -1;

    internal static void Arm(FlashFailureInjection injection, DirectSearchPurpose? purpose = null)
    {
        Volatile.Write(ref _purpose, purpose is { } value ? (int)value : -1);
        Interlocked.Exchange(ref _next, (int)injection);
    }

    /// <summary>Reads and clears the armed injection when its optional purpose matches.</summary>
    internal static FlashFailureInjection Take(DirectSearchPurpose? purpose = null)
    {
        int expected = Volatile.Read(ref _purpose);
        if (expected >= 0 && (purpose is not { } actual || expected != (int)actual))
            return FlashFailureInjection.None;
        Volatile.Write(ref _purpose, -1);
        return (FlashFailureInjection)Interlocked.Exchange(ref _next, (int)FlashFailureInjection.None);
    }

    internal static void Reset()
    {
        Volatile.Write(ref _purpose, -1);
        Interlocked.Exchange(ref _next, (int)FlashFailureInjection.None);
    }
}
