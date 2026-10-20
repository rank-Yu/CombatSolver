namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static OpeningProbe NewOpeningProbe(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken = default,
        Action<SolverProgress>? progressCallback = null,
        SolverSearchProfile? searchProfile = null,
        SolverPotionPolicy? potionPolicyOverride = null,
        PotionFreePolicyBaseline? potionFreePolicyBaseline = null,
        int? maximumPotionUses = null,
        IReadOnlyList<PlanAction>? fixedPrefixActions = null,
        bool resetFixedPrefixSchedulingBaseline = false,
        int? minimumPotionUses = null,
        PrimarySearchIncumbent? primaryIncumbent = null,
        int? earliestPotionTurn = null,
        int earlyTurnScoutDepth = 0,
        Action<int, IReadOnlyList<EarlyTurnFrontierCandidate>>? earlyTurnScoutObserver = null,
        PlanCommitment? planCommitment = null,
        ContinuationPurpose? attributionPurpose = null,
        DirectSearchPurpose? directSearchPurpose = null,
        IReadOnlyList<PlanCardChoice>? rootTurnSetupChoices = null)
        => new(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            searchProfile,
            potionPolicyOverride,
            maximumPotionUses,
            earliestPotionTurn,
            resetFixedPrefixSchedulingBaseline);

}
