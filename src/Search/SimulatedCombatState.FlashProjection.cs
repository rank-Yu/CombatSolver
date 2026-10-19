using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// Read-only views of branch state for the Flash bridge (<c>src/Search/FlashBeam/Bridge</c>).
/// Nothing here mutates the simulation.
/// </summary>
internal sealed partial class SimulatedCombatState
{
    internal BranchMonsterAiState GetMonsterAiStateForFlash(Creature creature) => GetMonsterAiState(creature);

    internal bool WillSkipNextMoveForFlash(Creature creature) => _skipNextMove?.Contains(creature) == true;

    internal bool HasMonsterDurationSkipForFlash(Creature creature, Type powerType)
        => _skipNextDurationTick?.Contains((creature, powerType)) == true;

    internal PredictedDeathPhase DeathPhaseForFlash(Creature creature)
        => _deathPhases?.GetValueOrDefault(creature) ?? PredictedDeathPhase.None;

    internal uint NextCreatureIdForFlash => _nextCreatureId;

    internal bool PlayerTurnEndRequestedForFlash => _playerTurnEndRequested;

    internal int PotionSlotCountForFlash(Player player) => PotionSlotCount(player);

    internal int CardsExhaustedThisTurnForFlash(Creature actor) => GetCardsExhaustedThisTurn(actor);

    /// <summary>Relics whose stateful state some reader consumes (<c>IsStatefulRelic</c>) and that hold a state.</summary>
    internal IEnumerable<RelicModel> StatefulRelicKeysForFlash()
        => _statefulRelicStates == null ? [] : _statefulRelicStates.Keys.Where(IsStatefulRelic).ToArray();

    /// <summary>The stateful relic state as the existing engine reads it (captured lazily from the root relic).</summary>
    internal (int Current, int Previous) StatefulRelicStateForFlash(RelicModel relic)
    {
        StatefulRelicState state = GetStatefulRelicState(relic);
        return (state.Current, state.Previous);
    }

    internal bool IsPrimaryEnemyForFlash(Creature creature)
        => ((CombatSolver.Engine.Common.ICombatPredictionCreatureSemantics)this).IsPrimaryEnemy(creature);

    /// <summary>Native encounter slot names (<c>_encounterSlots</c>), for the Flash rules table.</summary>
    internal IReadOnlyList<string> EncounterSlotsForFlash => _encounterSlots;

    /// <summary>
    /// The TenderPower card count through <see cref="GetTenderCardsPlayed"/>: the captured
    /// <c>_tenderCardsPlayed</c> entry when present, otherwise the native model field the getter would
    /// read. Only called for an owner that still holds a positive TenderPower; a retired entry without
    /// the power is inert (no reader consults it without the power).
    /// </summary>
    internal int TenderCardsPlayedForFlash(Creature owner) => GetTenderCardsPlayed(owner);

    /// <summary>
    /// The branch-local monster-private integers as the existing engine currently holds them. The
    /// bridge claims every entry through a monster definition's <c>AiRegisterNames</c> or
    /// <c>InertMonsterIntNames</c>, or rejects the root.
    /// </summary>
    internal IEnumerable<(Creature Creature, string Name, int Value)> MonsterIntEntriesForFlash()
    {
        if (_monsterIntStates == null)
            return [];
        return _monsterIntStates
            .Select(item => (item.Key.Creature, item.Key.Name, item.Value))
            .ToArray();
    }

    /// <summary>
    /// Reads a monster-private integer through the existing <see cref="GetMonsterInt"/> path: a
    /// captured entry when present, otherwise the value read from the model (and cached on this
    /// private fork). The bridge only calls this on its own import fork.
    /// </summary>
    internal int GetMonsterIntForFlash(Creature creature, string name)
    {
        // Aeonglass._additionalStrength/_witherUpgradeCount are branch dictionaries with the model
        // field as their fallback (ReadAeonglassCounter); a root captured after the boss acted has no
        // model-field write, so the dictionary is the value the existing engine reads.
        if (creature.Monster?.GetType().Name == "Fabricator" && name == "fabricator_last_spawn")
            return GetFabricatorLastSpawn(creature);
        // KnowledgeDemon._curseOfKnowledgeCounter is not one of the captured root monster ints; the
        // existing engine's BranchMonsterAi/KnowledgeDemonChoiceSupport read it from the model field
        // when no branch entry exists. Project that same value (the entry wins when present).
        if (creature.Monster?.GetType().Name == "KnowledgeDemon" && name == "_curseOfKnowledgeCounter")
        {
            if (_monsterIntStates?.TryGetValue((creature, name), out int captured) == true)
                return captured;
            if (_knowledgeDemonCurseCounters?.TryGetValue(creature, out int predicted) == true)
                return predicted;
            return MonsterValueReader.ReadInt(creature.Monster, name);
        }
        if (creature.Monster?.GetType().Name == "Aeonglass"
            && name is "AdditionalStrength" or "WitherUpgradeCount")
        {
            ForkableDictionary<Creature, int>? values = name == "AdditionalStrength"
                ? _aeonglassAdditionalStrength
                : _aeonglassWitherUpgradeCount;
            if (values != null && values.TryGetValue(creature, out int value))
                return value;
            return MonsterValueReader.ReadInt(creature.Monster, name);
        }
        return GetMonsterInt(creature, name);
    }

    /// <summary>
    /// The possession ledgers (<c>_stolenStrength</c>/<c>_stolenDexterity</c>) as (possessor, target,
    /// isStrength, stolen) entries. A missing entry is the constructor's zero.
    /// </summary>
    internal IEnumerable<(Creature Possessor, Creature Target, bool Strength, int Value)> PossessedEntriesForFlash()
    {
        if (_stolenStrength != null)
        {
            foreach (((Creature possessor, Creature target), int value) in _stolenStrength)
                yield return (possessor, target, true, value);
        }
        if (_stolenDexterity != null)
        {
            foreach (((Creature possessor, Creature target), int value) in _stolenDexterity)
                yield return (possessor, target, false, value);
        }
    }

    /// <summary>The stolen amount of one possessor against the player, or zero when no entry exists.</summary>
    internal int PossessedAmountForFlash(Creature possessor, bool strength)
    {
        ForkableDictionary<(Creature Possessor, Creature Target), int>? ledger =
            strength ? _stolenStrength : _stolenDexterity;
        return ledger != null && ledger.TryGetValue((possessor, Players[0].Creature), out int value) ? value : 0;
    }

    /// <summary>The (target, caster) pairs of <c>_dampenCasters</c>, as the existing engine reads them.</summary>
    internal IEnumerable<(Creature Target, Creature Caster)> DampenCastersForFlash()
        => _dampenCasters == null ? [] : _dampenCasters.Select(entry => (entry.Target, entry.Caster)).ToArray();

    /// <summary>
    /// The card/level pairs of <c>_dampenOriginalUpgrades</c>. The importer writes the level into
    /// <see cref="FlashCard.DampenOriginalUpgrade"/> and rejects a card it cannot find in the state.
    /// </summary>
    internal IEnumerable<(PredictedCard Card, int Level)> DampenOriginalUpgradesForFlash()
        => _dampenOriginalUpgrades == null
            ? []
            : _dampenOriginalUpgrades.Select(entry => (entry.Key, entry.Value)).ToArray();

    /// <summary>The Aeonglass branch counters, in the order the existing engine reads them.</summary>
    internal IEnumerable<(Creature Creature, string Name, int Value)> AeonglassCounterEntriesForFlash()
    {
        if (_aeonglassAdditionalStrength != null)
        {
            foreach ((Creature creature, int value) in _aeonglassAdditionalStrength)
                yield return (creature, "AdditionalStrength", value);
        }
        if (_aeonglassWitherUpgradeCount != null)
        {
            foreach ((Creature creature, int value) in _aeonglassWitherUpgradeCount)
                yield return (creature, "WitherUpgradeCount", value);
        }
    }

    /// <summary>
    /// Names the first branch state Flash does not model, or null. The per-turn counters Flash tracks
    /// are projected by the importer; the remaining counters (stars gained, non-hand draws, powered
    /// attack hits, Doom appliers, Fetch plays, enemy per-turn counts) are not listed: only content that
    /// reads them makes them matter, and such content has no Flash definition. Monster-private integers
    /// are claimed by the monster definitions (<see cref="MonsterIntEntriesForFlash"/>).
    /// </summary>
    internal string? DescribeFlashUnrepresentableState()
    {
        if (HasPendingChoice || _activeActionChoices != null)
            return "pending choice";
        if (_cardExecutionScopeDepth != 0)
            return "card execution scope";
        if (_pendingPowerAmountChanges is { Count: > 0 })
            return nameof(_pendingPowerAmountChanges);
        if (_powerCardSources is { Count: > 0 })
            return nameof(_powerCardSources);
        if (_nightmareSelections is { Count: > 0 })
            return nameof(_nightmareSelections);
        if (_orbitEnergyRemainders is { Count: > 0 })
            return nameof(_orbitEnergyRemainders);
        if (_paleBlueDotActivated is { Count: > 0 })
            return nameof(_paleBlueDotActivated);
        if (_swordSageReplayBonuses is { Count: > 0 })
        {
            // NormalizeSwordSageReplays records one entry per Sovereign Blade even when no Sword Sage
            // Power exists (applied bonus 0). With no SwordSagePower anywhere the target bonus is
            // always 0, so every entry is at its target and no reader can observe the table; the
            // per-blade bonus is already part of the imported BaseReplayCount. A positive power (the
            // card is not implemented) or a non-zero applied bonus is still rejected.
            bool inert = true;
            foreach (int applied in _swordSageReplayBonuses.Values)
            {
                if (applied != 0)
                {
                    inert = false;
                    break;
                }
            }
            foreach (Player player in Players)
            {
                if (GetAmount<SwordSagePower>(player.Creature) != 0)
                {
                    inert = false;
                    break;
                }
            }
            if (!inert)
                return nameof(_swordSageReplayBonuses);
        }
        if (_unsettlingLampTriggeringCards is { Count: > 0 } || _unsettlingLampInternalPowerTypes is { Count: > 0 })
            return "unsettling lamp";
        // Nemesis toggles are projected into the power's Aux0; every entry must belong to a known
        // enemy that still holds the power.
        if (_nemesisShouldApplyIntangible != null)
        {
            foreach ((Creature creature, bool _) in _nemesisShouldApplyIntangible)
            {
                // The toggle is keyed by the creature, so a re-acquired NemesisPower reads the same
                // entry; only an owner outside the known enemies cannot be projected. A zero-amount
                // power simply has no reader until it is re-applied.
                if (!KnownEnemies.Contains(creature))
                    return nameof(_nemesisShouldApplyIntangible);
            }
        }
        // Aeonglass keeps its two private counters in branch dictionaries; ProjectMonster claims them
        // through the monster definition's AI registers. An entry for anything but a known Aeonglass
        // cannot be projected.
        foreach ((Creature creature, string _, int _) in AeonglassCounterEntriesForFlash())
        {
            if (creature.Monster?.GetType().Name != "Aeonglass" || !KnownEnemies.Contains(creature))
                return "aeonglass";
        }
        // The simulated pet ledger is projected as the Osty creature. A key that is not a combat player,
        // or an entry that no longer matches GetOsty (stale after a fork), cannot be projected.
        if (_simulatedOsties != null)
        {
            foreach ((Player key, Creature value) in _simulatedOsties)
            {
                if (!Players.Contains(key) || !ReferenceEquals(GetOsty(key), value))
                    return nameof(_simulatedOsties);
            }
        }
        if (_simulatedOstyMaxHp != null)
        {
            foreach ((Creature osty, int value) in _simulatedOstyMaxHp)
            {
                if (Players.Count == 0 || !ReferenceEquals(GetOsty(Players[0]), osty)
                    || _predictionState == null || value != _predictionState.GetCreature(osty).MaxHp)
                    return nameof(_simulatedOstyMaxHp);
            }
        }
        // _knowledgeDemonCurseCounters is projected into the owning KnowledgeDemon's AI register
        // (FlashMonsterDef.AiRegisterNames), with the model field as the fallback value. Every key must
        // be a known KnowledgeDemon; an entry on any other creature has no defined projection.
        if (_knowledgeDemonCurseCounters is { Count: > 0 } curseCounters)
        {
            foreach (Creature creature in curseCounters.Keys)
            {
                if (!KnownEnemies.Contains(creature)
                    || creature.Monster?.GetType().Name != "KnowledgeDemon")
                {
                    return nameof(_knowledgeDemonCurseCounters);
                }
            }
        }
        if (_pressureGunBonus is { Count: > 0 })
            return nameof(_pressureGunBonus);
        if (_steamEruptionDamage is { Count: > 0 } || _steamEruptionPhases is { Count: > 0 })
            return "steam eruption";
        // Possession ledgers are projected into the matching power's Aux0 (one player target).
        foreach ((Creature possessor, Creature target, bool strength, int _) in PossessedEntriesForFlash())
        {
            if (!ReferenceEquals(target, Players[0].Creature) || !KnownEnemies.Contains(possessor))
                return "possess";
            int amount = strength
                ? GetAmount<PossessStrengthPower>(possessor)
                : GetAmount<PossessSpeedPower>(possessor);
            if (amount <= 0)
                return "possess";
        }
        if (_drawNextTurn != null && _drawNextTurn.Values.Any(value => value != 0))
            return nameof(_drawNextTurn);
        // _freeEntropicPotions is projected onto FlashCounter.EntropicFreePotionMask by
        // FlashStateImporter: true entries mark slots that hold a generated free potion and are cleared
        // when the slot is consumed; false entries are retired flags that no reader inspects.
        // Stateful relic states are claimed by the Flash bridge's relic projections
        // (StatefulRelicKeysForFlash); root capture may also record inert states for relics outside
        // IsStatefulRelic, which no reader consumes.
        if (_escapedCreatures.Count > 0)
            return nameof(_escapedCreatures);
        // _returnToHandNextTurn (Bolas/ThrummingHatchet) is projected onto FlashCard.ReturnToHandNextTurn
        // by FlashStateImporter; the set holds the same cards as their piles.
        // Instanced powers are represented as one FlashPower row per native instance: EffectivePowers
        // yields each instance (including _rootMultiInstancePowerClones), and FlashStateImporter imports
        // them in listener order. FlashStateProjections reads each instance's private state. Only the
        // instanced power types with a Flash definition and projection are admissible.
        if (_addedPowerInstances is { Count: > 0 } instances
            && instances.Any(static power => !IsProjectedInstancedPower(power)))
        {
            return "instanced powers";
        }
        if (_rootMultiInstancePowerClones is { Count: > 0 } clones
            && clones.Values.Any(static power => !IsProjectedInstancedPower(power)))
        {
            return "instanced powers";
        }
        // _tenderCardsPlayed is projected into TenderPower.Aux0 by the bridge
        // (FlashStateProjections.ProjectPower). An entry can outlive the power when the owner dies and
        // its powers are cleared, but no reader calls GetTenderCardsPlayed without a positive
        // TenderPower, so a retired entry is inert.
        if (_outstandingStolenGold is > 0 || _outstandingStolenCards is > 0)
            return "theft";
        if (_battlewornDummyTimedOut)
            return nameof(_battlewornDummyTimedOut);
        return null;
    }

    /// <summary>Instanced power types Flash represents as one row per instance and projects.</summary>
    private static bool IsProjectedInstancedPower(PowerModel power)
        => power is AutomationPower or PanachePower or RollingBoulderPower or TheBombPower
            or GigantificationPower or SandpitPower;
}
