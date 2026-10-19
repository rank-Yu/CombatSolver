using CombatSolver.Engine.InCombat.Flash;

namespace CombatSolver.FlashBeam;

/// <summary>
/// The potion-specific bridge checks an audited Flash request needs: the request's forced directives
/// resolved to Flash potion ids for the final filter, and the post-materialization checks that the
/// route the existing solver packaged still satisfies the request's exact-use bounds, earliest-turn
/// gate, policy override and forced potion identities.
/// </summary>
internal static class FlashPotionBridge
{
    /// <summary>
    /// The request's forced directives in the Flash kernel's identity form. A fixed prefix's forced uses
    /// are folded into the mask because they are not Flash nodes; their strategic cost is looked up from
    /// the same <see cref="PotionUsePolicy.StrategicHpCost(string, bool)"/> table the existing search uses.
    /// </summary>
    public static FlashForcedPotionDirectives CaptureForcedDirectives(
        PotionStrategySnapshot strategy,
        IReadOnlyList<PlanAction> prefix,
        bool renewablePotionShapedRock)
    {
        if (!strategy.HasForcedDirectives)
            return FlashForcedPotionDirectives.None;
        PotionSlotDirective[] forced = strategy.Directives
            .Where(directive => directive.Directive == SolverPotionDirective.Force)
            .ToArray();
        int[] slots = new int[forced.Length];
        ushort[] potionIds = new ushort[forced.Length];
        int alreadyUsedMask = 0;
        int alreadyUsedStrategicHpCost = 0;
        for (int index = 0; index < forced.Length; index++)
        {
            PotionSlotDirective directive = forced[index];
            if (!FlashRegistry.TryPotion(directive.PotionId, out ushort potionId))
            {
                throw new FlashImportException(FlashRejection.UnsupportedPotion,
                    $"强制用药指令的药水 {directive.PotionId} 没有 Flash 定义");
            }
            slots[index] = directive.Slot;
            potionIds[index] = potionId;
            foreach (PlanAction action in prefix)
            {
                if (action.Kind != PlanActionKind.UsePotion
                    || action.PotionSlot != directive.Slot
                    || !string.Equals(action.PotionId, directive.PotionId, StringComparison.Ordinal))
                {
                    continue;
                }
                alreadyUsedMask |= 1 << index;
                alreadyUsedStrategicHpCost += PotionUsePolicy.StrategicHpCost(
                    directive.PotionId, renewablePotionShapedRock);
                break;
            }
        }
        return new FlashForcedPotionDirectives(slots, potionIds, alreadyUsedMask,
            alreadyUsedStrategicHpCost);
    }

    /// <summary>
    /// Verifies the potion constraints on the materialized route. Returns a stable failure description, or
    /// null when the route is acceptable. A failure falls back to the existing solver; the Flash search's
    /// "no route" is never presented as the existing search's <c>PotionPolicyUnsatisfiedException</c>.
    /// </summary>
    public static string? VerifyRoute(
        SolverResult result,
        SearchPolicySnapshot policy,
        bool enforcePotionDirectives,
        bool renewablePotionShapedRock,
        int? minimumPotionUses,
        int? maximumPotionUses,
        SolverPotionPolicy? potionPolicyOverride,
        int? earliestPotionTurn)
    {
        int explicitUses = 0;
        foreach (PredictedPotionUse use in result.PotionUses)
        {
            if (!use.Automatic)
                explicitUses++;
        }
        int actionUses = 0;
        foreach (PlanAction action in result.BestNode.Actions)
        {
            if (action.Kind != PlanActionKind.UsePotion)
                continue;
            actionUses++;
            if (earliestPotionTurn is { } earliest && action.Turn < earliest)
                return $"route-earliest-potion turn={action.Turn} earliest={earliest}";
        }
        if (actionUses != explicitUses)
        {
            return $"route-potion-count action_uses={actionUses} explicit_uses={explicitUses}";
        }
        if (minimumPotionUses is { } minimum && explicitUses < minimum)
            return $"route-minimum-potions explicit_uses={explicitUses} minimum={minimum}";
        if (maximumPotionUses is { } maximum && explicitUses > maximum)
            return $"route-maximum-potions explicit_uses={explicitUses} maximum={maximum}";
        SolverPotionPolicy effectivePolicy = potionPolicyOverride ?? policy.PotionPolicy;
        if (effectivePolicy == SolverPotionPolicy.RequireAtLeastOne && explicitUses == 0)
            return "route-required-potion missing";
        if (enforcePotionDirectives && policy.PotionStrategy.HasForcedDirectives)
        {
            ForcedPotionUseEvaluation forced = policy.PotionStrategy.EvaluateForcedUses(
                result.BestNode.Actions, renewablePotionShapedRock);
            if (!forced.AllForcedUsesSatisfied)
            {
                return $"route-forced-potions matched={forced.ForcedUseCount} "
                    + $"required={policy.PotionStrategy.ForcedDirectiveCount} "
                    + $"directives={policy.PotionStrategy.DescribeForcedUses()}";
            }
        }
        return null;
    }

    /// <summary>The canonical ambergris potion id, used by the audited final-ordering restriction.</summary>
    public static ushort AmbergrisPotionId()
        => FlashScoreBridge.PotionId<MegaCrit.Sts2.Core.Models.Potions.Ambergris>();

    /// <summary>The canonical ambergris potion string id; the prefix replay matches the action's id.</summary>
    public static string AmbergrisPotionNativeId()
        => MegaCrit.Sts2.Core.Models.ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.Ambergris>().Id.Entry;
}
