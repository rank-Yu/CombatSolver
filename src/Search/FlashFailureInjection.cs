namespace CombatSolver;

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
/// Test-only, single-shot fault injection for <see cref="BeamSolveDispatch"/>. Production never arms
/// it; the offline failure contract consumes it through <see cref="Take"/>, which also clears the
/// arm so a failed assertion cannot leak into the next request. The injection is armed inside the
/// same try block as a real Flash solve, so it exercises the production catch and mapping paths
/// instead of bypassing them.
/// </summary>
internal static class FlashFailureInjectionForTesting
{
    private static int _next;

    internal static void Arm(FlashFailureInjection injection)
        => Interlocked.Exchange(ref _next, (int)injection);

    /// <summary>Reads and clears the armed injection.</summary>
    internal static FlashFailureInjection Take()
        => (FlashFailureInjection)Interlocked.Exchange(ref _next, (int)FlashFailureInjection.None);

    internal static void Reset() => Interlocked.Exchange(ref _next, (int)FlashFailureInjection.None);
}
