using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Flash;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
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
internal sealed class FlashStateProjections(PredictionStateStore store, CombatPredictionState combatState,
    SimulatedCombatState combat)
{
    private readonly HashSet<object> _claimed = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<RelicModel> _claimedRelicStates = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(Creature Creature, string Name)> _claimedMonsterInts = [];

    /// <summary>
    /// Claims the monster-private integers a definition reads and writes them into
    /// <see cref="FlashEnemy.Ai"/>; inert names are claimed without a register.
    /// </summary>
    public void ProjectMonster(Creature creature, FlashMonsterDef def, ref FlashEnemy target)
    {
        if (def.AiRegisterNames.Count > FlashLimits.EnemyAiRegisters)
            throw new FlashImportException(FlashRejection.Capacity,
                $"{creature.Monster?.Id.Entry} AI 寄存器 {def.AiRegisterNames.Count}");
        for (int register = 0; register < def.AiRegisterNames.Count; register++)
        {
            string name = def.AiRegisterNames[register];
            _claimedMonsterInts.Add((creature, name));
            target.Ai[register] = combat.GetMonsterIntForFlash(creature, name);
        }
        foreach (string name in def.InertMonsterIntNames)
            _claimedMonsterInts.Add((creature, name));
    }

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
            // SkittishPower.Aux0 = HasGainedBlockThisTurn (AfterAttackMirrors.HandleSkittishPower,
            // captured by SkittishPredictionState; without an entry the constructor reads the live
            // internal-data flag).
            case SkittishPower skittish:
                target.Aux0 = (Claim<SkittishPredictionState>(power)?.HasGainedBlockThisTurn
                    ?? skittish.HasGainedBlockThisTurn) ? 1 : 0;
                break;
            // HardenedShellPower.Aux0 = damageReceivedThisTurn
            // (AfterDamageReceivedMirrors.HandleHardenedShellPower and
            // ModifyHpLostMirrors.HandleHardenedShellPower use HardenedShellPredictionState; without
            // an entry the constructor reads the model's internal data).
            case HardenedShellPower hardened:
                target.Aux0 = (int)(Claim<HardenedShellPredictionState>(power)?.DamageReceivedThisTurn
                    ?? hardened.GetInternalData<HardenedShellPower.Data>().damageReceivedThisTurn);
                break;
            // VigorPower.Aux0 is the amount captured by the Flash definition for the current attack and is
            // zero at every action boundary. The existing engine keeps the same pair (AttackCommand,
            // amountWhenAttackStarted) in VigorPowerMirrors' private State; an idle state has no
            // unmodelled value, and a capture in progress is not an action boundary.
            case VigorPower vigor:
                foreach ((AbstractModel model, object state) in store.EntriesForFlash())
                {
                    if (!ReferenceEquals(model, vigor) || !VigorPowerMirrors.OwnsStateForFlash(state))
                        continue;
                    if (!VigorPowerMirrors.IsIdleStateForFlash(state))
                        throw new FlashImportException(FlashRejection.UnrepresentableState, "活力攻击结算进行中");
                    _claimed.Add(state);
                }
                break;
            // RupturePower.Aux0/Aux1 are the pending card play's Strength and its marker; both are zero
            // at every non-terminal action boundary. The damage card source is read directly from
            // FlashDamageResult (no staged Aux2). The existing engine keeps the same pending
            // information in RupturePredictionState.StrengthByCard, a map keyed by CardModel that only
            // holds the in-flight card play. A stable boundary therefore has an empty map: claim it,
            // and reject a state captured in the middle of a play.
            case RupturePower:
            {
                // A stale entry can outlive a model remap under the old instance; every entry of this
                // power is empty at a stable boundary and is inert once empty.
                foreach ((AbstractModel model, object state) in store.EntriesForFlash())
                {
                    if (state is not RupturePredictionState pending || model is not RupturePower)
                        continue;
                    if (pending.StrengthByCard.Count != 0)
                        throw new FlashImportException(FlashRejection.UnrepresentableState, "撕裂结算进行中");
                    _claimed.Add(state);
                }
                break;
            }
            // HellraiserPower keeps infiniteAutoPlaysThisTurn in HellraiserPredictionState. Every
            // admitted root has finite-HP enemies (WaterfallGiant's infinite display HP is not
            // admitted), so the native finite branch resets the counter on every draw and the only
            // representable stable value is zero.
            case HellraiserPower:
            {
                foreach ((AbstractModel model, object state) in store.EntriesForFlash())
                {
                    if (!ReferenceEquals(model, power) || state is not HellraiserPredictionState hellraiser)
                        continue;
                    if (hellraiser.InfiniteAutoPlaysThisTurn != 0)
                        throw new FlashImportException(FlashRejection.UnrepresentableState,
                            "Hellraiser 无限生命自动打出计数非零");
                    _claimed.Add(state);
                }
                break;
            }
            // CalamityPower pairs an Attack play with the power amount in CardPlayPairPredictionState.
            // The pair is removed by AfterCardPlayed, so a stable boundary has an empty map.
            case CalamityPower:
            {
                foreach ((AbstractModel model, object state) in store.EntriesForFlash())
                {
                    if (!ReferenceEquals(model, power) || state is not CardPlayPairPredictionState pair)
                        continue;
                    if (pair.Amounts.Count != 0)
                        throw new FlashImportException(FlashRejection.UnrepresentableState, "灾厄结算进行中");
                    _claimed.Add(state);
                }
                break;
            }
            // SurroundedPower.Aux0 = Facing (BeforeCardPlayedMirrors/DeathPowerSupport update
            // SurroundedPredictionState; without an entry its constructor reads the live field).
            case SurroundedPower surrounded:
                target.Aux0 = (int)(Claim<SurroundedPredictionState>(power)?.Facing ?? surrounded.Facing);
                break;
            // CurlUpPower.Aux0 = Consumed, .Aux1 = a card is recorded. Both live only in
            // CurlUpPredictionState (its constructor reads nothing from the model); a fresh entry is
            // false/false, and a live power is claimed here.
            case CurlUpPower:
            {
                CurlUpPredictionState? curl = Claim<CurlUpPredictionState>(power);
                target.Aux0 = curl?.Consumed == true ? 1 : 0;
                target.Aux1 = curl?.PlayedCard != null ? 1 : 0;
                break;
            }
            // TenderPower.Aux0 = cards played this turn. The existing engine reads it through
            // GetTenderCardsPlayed (_tenderCardsPlayed when captured, otherwise the native model field).
            case TenderPower tender:
                target.Aux0 = combat.TenderCardsPlayedForFlash(tender.Owner);
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
            // RuinedHelmet.UsedThisCombat = the existing engine's StatefulRelicState.Current
            // (ModifyPowerAmountForRuinedHelmet sets it to 1 after doubling; the live field is not
            // written by the simulator). The Flash definition consumes the flag directly.
            case RuinedHelmet:
                if (ClaimStatefulRelic(relic).Current != 0)
                    target.Flags |= FlashRelicFlags.UsedThisCombat;
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
            // FakeHappyFlower.Counter = TurnsSeen (StatefulRelicState Current, CaptureLiveState).
            case FakeHappyFlower:
                target.Counter = ClaimStatefulRelic(relic).Current;
                break;
            // FakeVenerableTeaSet.Counter = GainEnergyInNextCombat (StatefulRelicState Current).
            case FakeVenerableTeaSet:
                target.Counter = ClaimStatefulRelic(relic).Current;
                break;
            // BoneTea.Counter = CombatsLeft (StatefulRelicState Current).
            case BoneTea:
                target.Counter = ClaimStatefulRelic(relic).Current;
                break;
            // PollinousCore.Counter = TurnsSeen (StatefulRelicState Current).
            case PollinousCore:
                target.Counter = ClaimStatefulRelic(relic).Current;
                break;
            // PaelsTears.Counter = HadLeftoverEnergy (StatefulRelicState Current).
            case PaelsTears:
                target.Counter = ClaimStatefulRelic(relic).Current;
                break;
            // IronClub.Counter = cards played toward the 4-card cycle
            // (AfterCardPlayedMirrors.HandleIronClub uses CounterPredictionState; the live field is
            // the fallback its constructor reads).
            case IronClub club:
                target.Counter = Claim<CounterPredictionState>(relic)?.Value ?? club.CardsPlayed;
                break;
            // VelvetChoker.Counter = cards played this turn
            // (AfterCardPlayedMirrors.HandleVelvetChoker uses CounterPredictionState; the live field is
            // the fallback its constructor reads).
            case VelvetChoker choker:
                target.Counter = Claim<CounterPredictionState>(relic)?.Value ?? choker._cardsPlayedThisTurn;
                break;
            // FakeOrichalcum.Counter = the latched ShouldTrigger value (OrichalcumMirrors' private state
            // entry, shared by Orichalcum and FakeOrichalcum).
            case FakeOrichalcum:
            {
                target.Counter = OrichalcumMirrors.ShouldTriggerForFlash(store, relic, out object? entry) ? 1 : 0;
                if (entry != null)
                    _claimed.Add(entry);
                break;
            }
            // PumpkinCandle.Counter = KindleCount. The existing engine has no state-store entry for it;
            // the model field is the data its constructor reads and it does not change during combat.
            case PumpkinCandle candle:
                target.Counter = candle.KindleCount;
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
    /// Rejects the state unless every store entry was claimed or is inert. Inert entries:
    /// <list type="bullet">
    /// <item>a <see cref="PowerAmountPredictionState"/> whose amount equals its power's amount
    /// (<c>SynchronizePowerAmountPredictionStates</c> would only remove it);</item>
    /// <item>any entry of a power whose enemy owner has left the combat (died and was removed, or
    /// escaped). The existing engine reads power state only through powers attached to creatures in
    /// the combat, and an enemy that left never returns: native summons always create a new creature
    /// (<c>CreatureCmd.Add</c> callers; only the player's pet is re-added), so the entry can no longer
    /// be read.</item>
    /// <item>the idle private <c>VigorPowerMirrors.State</c> of a removed (zero-amount) VigorPower: the
    /// engine reads the state only while that power is attached to a creature (the Flash
    /// <c>VigorPower</c> definition mirrors it through Aux0).</item>
    /// </list>
    /// </summary>
    public void RequireAllClaimed()
    {
        foreach ((AbstractModel model, object state) in store.EntriesForFlash())
        {
            if (_claimed.Contains(state))
                continue;
            if (state is PowerAmountPredictionState amount && model is PowerModel power && amount.Amount == power.Amount)
                continue;
            // VigorPower removes itself when the attack it modified completes, but the store keeps
            // VigorPowerMirrors' private State keyed by the retired model (Amount == 0). An idle state is
            // inert; a live Vigor is claimed by the ProjectPower case above.
            if (model is VigorPower { Amount: 0 } && VigorPowerMirrors.IsIdleStateForFlash(state))
                continue;
            if (model is PowerModel { Owner: { Side: CombatSide.Enemy } owner } && !combat.ContainsCreature(owner))
                continue;
            // Retired power prediction states whose only readers are the power's own hooks. The power
            // amount reached zero and ImportPowers skipped it, so no EffectivePowers entry can reach
            // these readers again (a re-acquired power is a fresh native instance).
            //
            // SurroundedPower is removed only when the player dies (HandlePlayerDeath clears the
            // player's powers); its Facing is read only by BeforeCardPlayed/ModifyDamage on the living
            // player.
            if (model is SurroundedPower { Amount: 0 } && state is SurroundedPredictionState)
            {
                continue;
            }
            // CurlUpPower consumes itself in AfterCardPlayed; the store entry keeps Consumed and the
            // cleared card. AfterDamageReceived/AfterCardPlayed only visit a positive effective power.
            if (model is CurlUpPower { Amount: 0 } && state is CurlUpPredictionState)
            {
                continue;
            }
            // Retired power states left behind by HandlePlayerDeath -> RemovePowersAfterDeath: the
            // power amount reaches zero but the store entry keeps its accumulated value. Every
            // reader is a hook of that power and only visits positive EffectivePowers entries; the
            // global CardPlayPair cleanup only removes in-flight CardPlay keys. The player is dead,
            // so no further card play, draw or turn phase can reach them.
            if (model is PowerModel { Amount: 0 } retired
                && retired.Owner.Player is not null
                && combatState.GetCreature(retired.Owner).IsDead
                && state is DarkEmbracePredictionState or JugglingPredictionState
                    or HellraiserPredictionState or RupturePredictionState or CardPlayPairPredictionState)
            {
                continue;
            }
            throw new FlashImportException(FlashRejection.UnrepresentableState, $"StateStore: {state.GetType().Name}@{model.Id.Entry}");
        }
        foreach (RelicModel relic in combat.StatefulRelicKeysForFlash())
        {
            if (!_claimedRelicStates.Contains(relic))
                throw new FlashImportException(FlashRejection.UnrepresentableState, $"StatefulRelicState: {relic.Id.Entry}");
        }
    }

    /// <summary>
    /// Rejects any captured monster-private integer that no monster definition claimed, either as an
    /// <see cref="FlashMonsterDef.AiRegisterNames"/> register or as an
    /// <see cref="FlashMonsterDef.InertMonsterIntNames"/> entry.
    /// </summary>
    public void RequireAllMonsterIntsClaimed()
    {
        foreach ((Creature creature, string name, int _) in combat.MonsterIntEntriesForFlash())
        {
            if (!_claimedMonsterInts.Contains((creature, name)))
                throw new FlashImportException(FlashRejection.UnrepresentableState,
                    $"MonsterInt: {creature.Monster?.Id.Entry}.{name}");
        }
    }
}
