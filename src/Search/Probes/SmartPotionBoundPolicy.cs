namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

/// <summary>
/// The exact optional-potion layer's HP ceiling and the strict healing/relic certification
/// shared by the existing solver, the coordinator and the Flash materializer.
/// </summary>
internal static class SmartPotionBoundPolicy
{
    internal static bool CanUseStrictHpRelicBound(CombatRootSnapshot root, SearchPolicySnapshot policy)
        => CanUseStrictHpRelicBound(root.CanCertifyRemainingHealing || root.UsesKnownNativeHealingPolicy,
            policy.EffectiveHasGrowthTargets, policy.RelicTargets);

    internal static bool CanUseStrictHpRelicBound(bool hasRemainingHealingBound,
        bool hasGrowthTargets, IReadOnlyList<RelicCounterTarget> targets)
        => hasRemainingHealingBound && !hasGrowthTargets && targets.Count > 0
            && targets.All(target => target.HpAllowance == 0);

    internal static bool CanUseComponentSmartPotionEligibility(CombatRootSnapshot root, SearchPolicySnapshot policy)
        => root.UsesComponentHealingCertificate
            && root.SearchablePotions.Count > 0 && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && !policy.EffectiveHasGrowthTargets
            && (policy.RelicTargets.Count == 0 || CanUseStrictHpRelicBound(root, policy))
            && policy.TheftPolicy != SolverTheftPolicy.PreserveResources
            && (root.InitialRemainingHealingUpperBound == 0
                || StrategicHpRecoveryBound.ComponentHealingUpperBound(root.ForkSimulator(),
                    root.PlayerIdentity, postCombatHeal: 0,
                    potionStrategy: policy.PotionStrategy, effectivePotionPolicy: policy.PotionPolicy) == 0);

    internal static int? ComputeSmartPotionEligibilityHpCeiling(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        DirectSearchPurpose? directSearchPurpose,
        ContinuationPurpose? attributionPurpose,
        PotionFreePolicyBaseline? potionFreePolicyBaseline,
        int? minimumPotionUses,
        int? maximumPotionUses)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.Smart
            || policy.PotionStrategy.HasForcedDirectives
            || policy.TheftPolicy == SolverTheftPolicy.PreserveResources
            || root.SearchablePotions.Count == 0
            || minimumPotionUses is not > 0
            || maximumPotionUses != minimumPotionUses)
        {
            return null;
        }
        bool componentEligible = CanUseComponentSmartPotionEligibility(root, policy);
        if (directSearchPurpose != DirectSearchPurpose.SmartPotionGradient
            && !(componentEligible
                && attributionPurpose == ContinuationPurpose.SmartOpeningPotionPosterior))
        {
            return null;
        }
        bool eligible = componentEligible
            || (root.PlayerIdentity.Character.GetType() == typeof(MegaCrit.Sts2.Core.Models.Characters.Defect)
                && root.Enemies.Count > 0
                && root.Enemies.All(enemy => enemy.Monster?.GetType() == typeof(MegaCrit.Sts2.Core.Models.Monsters.InfestedPrism))
                && CanUseStrictHpRelicBound(root.CanCertifyRemainingHealing
                    && root.InitialRemainingHealingUpperBound == 0,
                    policy.EffectiveHasGrowthTargets, policy.RelicTargets)
                && root.SearchablePotions.All(potion => potion.PotionId
                    == MegaCrit.Sts2.Core.Models.ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.FoulPotion>().Id.Entry));
        if (!eligible)
            return null;
        return SmartPotionEligibilityHpCeiling(potionFreePolicyBaseline,
            minimumPotionUses.Value, root.MinimumSearchablePotionStrategicCost!.Value,
            root.PotionRewardOutlook.ReplacementHpCredit,
            ActEndingBossPolicy.ResolveStrategicHpRelief(root.BossHpRelief,
                policy.ActTransitionBossHpStrategy, policy.FinalBossHpStrategy));
    }

    internal static int? SmartPotionEligibilityHpCeiling(
        PotionFreePolicyBaseline? baseline, int exactPotionUses,
        int minimumPotionStrategicCost, int replacementHpCredit, BossHpRelief bossHpRelief)
    {
        if (baseline is not { Won: true, DeathSaveUseCount: 0 } audited
            || exactPotionUses <= 0 || minimumPotionStrategicCost < 0)
            return null;
        int minimumCost = checked(exactPotionUses * minimumPotionStrategicCost);
        int requiredHpSaved = PotionUsePolicy.SmartRequiredHpSaved(
            PotionUsePolicy.ApplyReplacementCredit(minimumCost, exactPotionUses, replacementHpCredit),
            bossHpRelief);
        return audited.HpDeficit - requiredHpSaved;
    }
}
