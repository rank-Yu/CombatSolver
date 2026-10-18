using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Flash;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.FlashBeam;

/// <summary>
/// Projects the existing engine's per-model state that lives outside the model amounts — entries of
/// <see cref="PredictionStateStore"/> and private model data — into Flash slots
/// (<see cref="FlashPower.Aux0"/>..<see cref="FlashPower.Aux2"/>, <see cref="FlashRelic.Counter"/>).
/// Every store entry must be claimed by a projection or be provably inert; any other entry rejects the
/// state, so an unmodelled value can never be dropped silently.
/// </summary>
/// <remarks>
/// A projection reads the store entry when it exists and otherwise the model data the entry's
/// constructor would read, so a lazily created entry and its absence project the same value. The
/// matching Flash definition maintains the slot; the differential probe compares the projection after
/// every step, which catches a projection that reads the wrong value.
/// </remarks>
internal sealed class FlashStateProjections(PredictionStateStore store, SimulatedCombatState combat)
{
    private readonly HashSet<object> _claimed = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<RelicModel> _claimedRelicStates = new(ReferenceEqualityComparer.Instance);

    /// <summary>Fills the private slots of one imported power.</summary>
    public void ProjectPower(PowerModel power, ref FlashPower target)
    {
        switch (power)
        {
            // Supported power states are added here together with their Flash definitions.
            default:
                break;
        }
    }

    /// <summary>Fills <see cref="FlashRelic.Counter"/> and <see cref="FlashRelic.Aux"/> of one imported relic.</summary>
    public void ProjectRelic(RelicModel relic, ref FlashRelic target)
    {
        switch (relic)
        {
            // Supported relic states are added here together with their Flash definitions.
            default:
                break;
        }
    }

    /// <summary>
    /// Claims the relic's <c>SimulatedCombatState</c> stateful state (<c>StatefulRelicState</c>) and returns it
    /// as the existing engine reads it.
    /// </summary>
    private (int Current, int Previous) ClaimStatefulRelic(RelicModel relic)
    {
        _claimedRelicStates.Add(relic);
        return combat.StatefulRelicStateForFlash(relic);
    }

    /// <summary>Claims a store entry of <typeparamref name="TState"/> for <paramref name="model"/> if one exists.</summary>
    private TState? Claim<TState>(AbstractModel model)
        where TState : class, IPredictionStateForkable
    {
        if (!store.TryGetReadOnly(model, out TState? state))
            return null;
        _claimed.Add(state!);
        return state;
    }

    /// <summary>
    /// Rejects the state unless every store entry was claimed or is inert. A
    /// <see cref="PowerAmountPredictionState"/> whose amount equals its power's amount is inert:
    /// <c>SynchronizePowerAmountPredictionStates</c> would only remove it.
    /// </summary>
    public void RequireAllClaimed()
    {
        foreach ((AbstractModel model, object state) in store.EntriesForFlash())
        {
            if (_claimed.Contains(state))
                continue;
            if (state is PowerAmountPredictionState amount && model is PowerModel power && amount.Amount == power.Amount)
                continue;
            throw new FlashImportException(FlashRejection.UnrepresentableState, $"StateStore: {state.GetType().Name}@{model.Id.Entry}");
        }
        foreach (RelicModel relic in combat.StatefulRelicKeysForFlash())
        {
            if (!_claimedRelicStates.Contains(relic))
                throw new FlashImportException(FlashRejection.UnrepresentableState, $"StatefulRelicState: {relic.Id.Entry}");
        }
    }
}
