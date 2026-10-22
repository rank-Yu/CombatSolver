namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    /// <summary>
    /// The single production entry for one coordinator search. In On mode it catches only
    /// <see cref="FlashFallbackRequiredException"/> from the first Flash attempt: if nothing was
    /// published, it writes exactly one <c>FLASH_FALLBACK</c> and starts a brand-new search with
    /// <see cref="SearchPolicySnapshot.RegularProfile"/> and <see cref="FlashSearchMode.Off"/>;
    /// a published fallback is converted by <c>SolveOnce</c> into the stable failure instead.
    /// Off mode is the plain pass-through.
    /// </summary>
    public static SolverResult Solve(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback)
    {
        if (policy.FlashMode != FlashSearchMode.On)
            return SolveOnce(root, displayNames, battleDamage, policy, cancellationToken, progressCallback);

        try
        {
            return SolveOnce(root, displayNames, battleDamage, policy, cancellationToken, progressCallback);
        }
        catch (FlashFallbackRequiredException fallback)
        {
            // Admission depends only on root content, so this normally happens before the first Flash
            // member publishes anything. SolveOnce rejects a published fallback with the stable failure.
            SolverSearchProfile regularProfile = FlashFallbackFailure.RequireRegularProfile(policy);
            policy.Diagnostics.FlashFallback(fallback.Notice);
            SearchPolicySnapshot regularPolicy = policy with
            {
                FlashMode = FlashSearchMode.Off,
                Profile = regularProfile,
            };
            return SolveOnce(
                root, displayNames, battleDamage, regularPolicy, cancellationToken, progressCallback);
        }
    }
}
