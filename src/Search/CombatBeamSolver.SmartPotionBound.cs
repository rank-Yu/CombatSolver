namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

internal sealed partial class CombatBeamSolver
{
    // An exact optional-potion layer can affect the Smart selection only if it
    // meets the existing HP-saving threshold after a complete potion-free victory.
    // Component certification excludes new potion/relic acquisition and HP-growth
    // sources; branch-unknown sources retain the full counterfactual search.
    private readonly int? _smartPotionEligibilityHpCeiling =
        ComputeSmartPotionEligibilityHpCeiling(
            root,
            policy,
            directSearchPurpose,
            attributionPurpose,
            potionFreePolicyBaseline,
            minimumPotionUses,
            maximumPotionUses);

    /// <summary>
    /// The exact optional-potion layer's HP ceiling, shared by the existing solver and the Flash
    /// bridge so both prune with the same rule. Returns null when the layer cannot affect the Smart
    /// selection or when the root is outside the existing certification (no pruning).
    /// </summary>
    internal static int? ComputeSmartPotionEligibilityHpCeiling(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        DirectSearchPurpose? directSearchPurpose,
        ContinuationPurpose? attributionPurpose,
        PotionFreePolicyBaseline? potionFreePolicyBaseline,
        int? minimumPotionUses,
        int? maximumPotionUses)
        => SmartPotionBoundPolicy.ComputeSmartPotionEligibilityHpCeiling(
            root, policy, directSearchPurpose, attributionPurpose,
            potionFreePolicyBaseline, minimumPotionUses, maximumPotionUses);

    internal static bool CanUseComponentSmartPotionEligibility(CombatRootSnapshot root, SearchPolicySnapshot policy)
        => SmartPotionBoundPolicy.CanUseComponentSmartPotionEligibility(root, policy);

    private int _smartPotionEligibilityBranchesPruned;
    internal bool ComponentSmartBoundEnabledForTesting => _smartPotionEligibilityHpCeiling is not null;
    internal int ComponentSmartBoundPrunedForTesting => _smartPotionEligibilityBranchesPruned;

    internal static int? SmartPotionEligibilityHpCeiling(
        PotionFreePolicyBaseline? baseline, int exactPotionUses,
        int minimumPotionStrategicCost, int replacementHpCredit, BossHpRelief bossHpRelief)
        => SmartPotionBoundPolicy.SmartPotionEligibilityHpCeiling(
            baseline, exactPotionUses, minimumPotionStrategicCost, replacementHpCredit, bossHpRelief);

    private List<SearchNode> ApplySmartPotionEligibilityBound(List<SearchNode> retained)
    {
        if (_smartPotionEligibilityHpCeiling is not { } ceiling)
            return retained;
        List<SearchNode>? bounded = null;
        for (int index = 0; index < retained.Count; index++)
        {
            SearchNode node = retained[index];
            int healing = RemainingHealingPotential(node.Snapshot);
            // Unknown branch sources retain the original exhaustive audit.
            if (root.UsesComponentHealingCertificate && healing == int.MaxValue)
            {
                bounded?.Add(node);
                continue;
            }
            if (StrategicHpLowerBound(node.Snapshot, _strategicBossHpRelief, healing) > ceiling)
            {
                if (bounded is null)
                {
                    bounded = new List<SearchNode>(retained.Count);
                    if (index > 0)
                        bounded.AddRange(retained.GetRange(0, index));
                }
                _smartPotionEligibilityBranchesPruned++;
                continue;
            }
            bounded?.Add(node);
        }
        return bounded ?? retained;
    }

    private void EmitSmartPotionEligibilityBoundDiagnostics()
    {
        if (_smartPotionEligibilityHpCeiling is { } ceiling)
            policy.Diagnostics.Info($"[CombatSolver/Test] SMART_POTION_ELIGIBILITY_BOUND "
                + $"ceiling={ceiling} pruned={_smartPotionEligibilityBranchesPruned}");
    }
}
