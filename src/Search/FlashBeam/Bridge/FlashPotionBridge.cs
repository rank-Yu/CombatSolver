namespace CombatSolver.FlashBeam;

/// <summary>
/// The potion-specific bridge checks an audited Flash request needs after materialization: the route the
/// existing solver packaged must still satisfy the request's exact-use bounds and policy override.
/// </summary>
internal static class FlashPotionBridge
{
    /// <summary>
    /// Verifies the potion constraints on the materialized route. Returns a stable failure description, or
    /// null when the route is acceptable. A failure falls back to the existing solver; the Flash search's
    /// "no route" is never presented as the existing search's <c>PotionPolicyUnsatisfiedException</c>.
    /// </summary>
    public static string? VerifyRoute(
        SolverResult result,
        int? minimumPotionUses,
        int? maximumPotionUses,
        SolverPotionPolicy? potionPolicyOverride)
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
            if (action.Kind == PlanActionKind.UsePotion)
                actionUses++;
        }
        if (actionUses != explicitUses)
        {
            return $"route-potion-count action_uses={actionUses} explicit_uses={explicitUses}";
        }
        if (minimumPotionUses is { } minimum && explicitUses < minimum)
            return $"route-minimum-potions explicit_uses={explicitUses} minimum={minimum}";
        if (maximumPotionUses is { } maximum && explicitUses > maximum)
            return $"route-maximum-potions explicit_uses={explicitUses} maximum={maximum}";
        if (potionPolicyOverride == SolverPotionPolicy.RequireAtLeastOne && explicitUses == 0)
            return "route-required-potion missing";
        return null;
    }

    /// <summary>The canonical ambergris potion id, used by the audited final-ordering restriction.</summary>
    public static ushort AmbergrisPotionId()
        => FlashScoreBridge.PotionId<MegaCrit.Sts2.Core.Models.Potions.Ambergris>();

    /// <summary>The canonical ambergris potion string id; the prefix replay matches the action's id.</summary>
    public static string AmbergrisPotionNativeId()
        => MegaCrit.Sts2.Core.Models.ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.Ambergris>().Id.Entry;
}
