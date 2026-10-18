using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Flash;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

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
            // JugglingPower.Aux0 = attacks played this turn (BeforeCardPlayedMirrors.HandleJugglingPower,
            // captured by JugglingPredictionState; without an entry the constructor reads the internal data).
            case JugglingPower juggling:
                target.Aux0 = Claim<JugglingPredictionState>(power)?.AttacksPlayedThisTurn
                    ?? juggling.GetInternalData<JugglingPower.Data>().attacksPlayedThisTurn;
                break;
            // DarkEmbracePower.Aux0 = Ethereal exhausts deferred to the player side turn end
            // (AfterCardExhaustedMirrors.HandleDarkEmbracePower, captured by DarkEmbracePredictionState).
            case DarkEmbracePower darkEmbrace:
                target.Aux0 = Claim<DarkEmbracePredictionState>(power)?.EtherealCount
                    ?? darkEmbrace.GetInternalData<DarkEmbracePower.Data>().etherealCount;
                break;
            // CrimsonMantlePower.Aux0 / InfernoPower.Aux0 = the private SelfDamage dynamic variable
            // (SimulatedCombatState.IncrementCrimsonMantle and CardOnPlaySupport's Inferno case).
            case CrimsonMantlePower crimsonMantle:
                target.Aux0 = crimsonMantle.DynamicVars["SelfDamage"].IntValue;
                break;
            case InfernoPower inferno:
                target.Aux0 = inferno.DynamicVars["SelfDamage"].IntValue;
                break;
            // SlowPower.Aux0 = the private SlowAmount counter (AfterCardPlayedMirrors.HandleSlowPower
            // and ModifyDamageMirrors.HandleSlowPower use a CounterPredictionState; without an entry
            // the constructor reads DynamicVars["SlowAmount"].BaseValue).
            case SlowPower slow:
                target.Aux0 = Claim<CounterPredictionState>(power)?.Value
                    ?? slow.DynamicVars["SlowAmount"].IntValue;
                break;
            // RagePower has no private state; its stale PowerAmountPredictionState after the turn-end
            // removal is inert (amount equals the removed power's amount) and stays unclaimed.
            default:
                break;
        }
    }

    /// <summary>Fills <see cref="FlashRelic.Counter"/> and <see cref="FlashRelic.Aux"/> of one imported relic.</summary>
    public void ProjectRelic(RelicModel relic, ref FlashRelic target)
    {
        switch (relic)
        {
            // ArtOfWar.Counter = attacks played this turn, .Aux = attacks played last turn
            // (StatefulRelicState Current/Previous; RecordRelicCardPlayed and CompleteRelicsAfterSideTurnEnd).
            case ArtOfWar:
                (target.Counter, target.Aux) = ClaimStatefulRelic(relic);
                break;
            // BurningSticks.Counter = was used this combat (BurningSticksPredictionState.WasUsedThisCombat).
            case BurningSticks sticks:
                target.Counter = (Claim<BurningSticksPredictionState>(relic)?.WasUsedThisCombat
                    ?? sticks.WasUsedThisCombat) ? 1 : 0;
                break;
            // CentennialPuzzle.Counter = was used this combat (CentennialPuzzlePredictionState.UsedThisCombat).
            case CentennialPuzzle puzzle:
                target.Counter = (Claim<CentennialPuzzlePredictionState>(relic)?.UsedThisCombat
                    ?? puzzle.UsedThisCombat) ? 1 : 0;
                break;
            // DemonTongue.Counter = triggered this turn (DemonTonguePredictionState.TriggeredThisTurn).
            case DemonTongue tongue:
                target.Counter = (Claim<DemonTonguePredictionState>(relic)?.TriggeredThisTurn
                    ?? tongue._triggeredThisTurn) ? 1 : 0;
                break;
            // HappyFlower.Counter = turns seen in the cycle (StatefulRelicState Current = TurnsSeen).
            case HappyFlower:
                target.Counter = ClaimStatefulRelic(relic).Current;
                break;
            // JossPaper.Counter = exhausts toward the threshold, .Aux = deferred Ethereal exhausts
            // (JossPaperPredictionState.CardsExhausted/EtherealCount).
            case JossPaper paper:
            {
                JossPaperPredictionState? state = Claim<JossPaperPredictionState>(relic);
                target.Counter = state?.CardsExhausted ?? paper.CardsExhausted;
                target.Aux = state?.EtherealCount ?? paper._etherealCount;
                break;
            }
            // Kunai/Kusarigama/LetterOpener/Nunchaku/OrnamentalFan/Shuriken/TuningFork use a
            // CounterPredictionState; the fallback is the live field its constructor would read.
            case Kunai kunai:
                target.Counter = Claim<CounterPredictionState>(relic)?.Value ?? kunai._attacksPlayedThisTurn;
                break;
            case Kusarigama kusarigama:
                target.Counter = Claim<CounterPredictionState>(relic)?.Value ?? kusarigama._attacksPlayedThisTurn;
                break;
            case LetterOpener opener:
                target.Counter = Claim<CounterPredictionState>(relic)?.Value ?? opener._skillsPlayedThisTurn;
                break;
            case Nunchaku nunchaku:
                target.Counter = Claim<CounterPredictionState>(relic)?.Value ?? nunchaku.AttacksPlayed;
                break;
            case OrnamentalFan fan:
                target.Counter = Claim<CounterPredictionState>(relic)?.Value ?? fan._attacksPlayedThisTurn;
                break;
            case Shuriken shuriken:
                target.Counter = Claim<CounterPredictionState>(relic)?.Value ?? shuriken._attacksPlayedThisTurn;
                break;
            case TuningFork fork:
                target.Counter = Claim<CounterPredictionState>(relic)?.Value ?? fork.SkillsPlayed;
                break;
            // Orichalcum.Counter = the latched ShouldTrigger value (OrichalcumMirrors' private state entry).
            case Orichalcum:
            {
                target.Counter = OrichalcumMirrors.ShouldTriggerForFlash(store, relic, out object? entry) ? 1 : 0;
                if (entry != null)
                    _claimed.Add(entry);
                break;
            }
            // PenNib.Counter = attacks played modulo 10 (PenNibPredictionState.AttacksPlayed); the
            // doubled-card latch Aux stays 0 at a stable boundary.
            case PenNib penNib:
                target.Counter = Claim<PenNibPredictionState>(relic)?.AttacksPlayed ?? penNib.AttacksPlayed;
                break;
            // Pendulum.Counter = turns seen in the cycle (StatefulRelicState Current = TurnsSeen).
            case Pendulum:
                target.Counter = ClaimStatefulRelic(relic).Current;
                break;
            // Permafrost.Counter = was activated this combat (FlagPredictionState(_activatedThisCombat)).
            case Permafrost permafrost:
                target.Counter = (Claim<FlagPredictionState>(relic)?.Value
                    ?? permafrost._activatedThisCombat) ? 1 : 0;
                break;
            // Pocketwatch.Counter = cards played this turn, .Aux = cards played last turn
            // (StatefulRelicState Current/Previous).
            case Pocketwatch:
                (target.Counter, target.Aux) = ClaimStatefulRelic(relic);
                break;
            // RainbowRing.Counter = activations this turn, .Aux = Powers played this turn
            // (RainbowRingPredictionState.ActivationCountThisTurn/PowersPlayedThisTurn; the attack and
            // Skill counts are the kernel's per-turn counters).
            case RainbowRing ring:
            {
                // The engine also keeps a derived StatefulRelicState latch (Current = activation
                // count clamped to 1 in RecordRelicCardPlayed); claim it as well.
                (int statefulCurrent, _) = ClaimStatefulRelic(relic);
                RainbowRingPredictionState? state = Claim<RainbowRingPredictionState>(relic);
                target.Counter = state?.ActivationCountThisTurn ?? statefulCurrent;
                target.Aux = state?.PowersPlayedThisTurn ?? ring._powersPlayedThisTurn;
                break;
            }
            // VenerableTeaSet.Counter = pending pre-combat tea (StatefulRelicState
            // Current = GainEnergyInNextCombat).
            case VenerableTeaSet:
                target.Counter = ClaimStatefulRelic(relic).Current;
                break;
            // BeatingRemnant.Counter = damage received this turn
            // (AfterDamageReceivedMirrors.HandleBeatingRemnant keeps it in
            // BeatingRemnantPredictionState; without an entry the constructor reads the live field).
            case BeatingRemnant remnant:
                target.Counter = (int)(Claim<BeatingRemnantPredictionState>(relic)?.DamageReceivedThisTurn
                    ?? remnant._damageReceivedThisTurn);
                break;
            // LizardTail.Counter = WasUsed (DeathPreventerMirrors.LizardTailMirrors uses
            // LizardTailPredictionState.WasUsed; without an entry the live property is the source).
            case LizardTail tail:
                target.Counter = (Claim<LizardTailPredictionState>(relic)?.WasUsed ?? tail.WasUsed) ? 1 : 0;
                break;
            // RedSkull.Counter = the engine's StrengthApplied flag held in StatefulRelicState
            // (AfterCurrentHpChangedMirrors.HandleRedSkull through GetRedSkullStrengthApplied).
            case RedSkull:
                target.Counter = ClaimStatefulRelic(relic).Current;
                break;
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
