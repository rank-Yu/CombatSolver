using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;

internal static class OrichalcumMirrors
{
    public static void BeforeSideTurnEndVeryEarly(RelicModel relic, BeforeSideTurnEndMirrorContext context)
    {
        if (context.Participants.Contains(relic.Owner.Creature) &&
            context.State.GetCreature(relic.Owner.Creature).Block <= 0)
        {
            GetState(relic, context).ShouldTrigger = true;
        }
    }

    public static void BeforeSideTurnEnd(RelicModel relic, BeforeSideTurnEndMirrorContext context)
    {
        var state = GetState(relic, context);
        if (state.ShouldTrigger)
        {
            state.ShouldTrigger = false;
            context.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
        }
    }

    /// <summary>
    /// The latched trigger as the Flash bridge projects it: the stored entry when present (returned for claiming),
    /// otherwise false, which is what a newly created entry holds.
    /// </summary>
    internal static bool ShouldTriggerForFlash(PredictionStateStore store, RelicModel relic, out object? entry)
    {
        if (store.TryGetReadOnly(relic, out State? state))
        {
            entry = state;
            return state!.ShouldTrigger;
        }
        entry = null;
        return false;
    }

    private static State GetState(RelicModel relic, BeforeSideTurnEndMirrorContext context)
    {
        return context.StateStore.Get<State>(relic);
    }

    private sealed class State : IPredictionStateForkable
    {
        public bool ShouldTrigger { get; set; }

        public object Fork(PredictionForkContext context) => MemberwiseClone();
    }
}
