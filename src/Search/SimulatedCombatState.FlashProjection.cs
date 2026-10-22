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
        // WaterfallGiant's dynamic move values live in the branch dictionaries
        // (_pressureGunBonus / _steamEruptionDamage), with zero as the constructor's value.
        if (creature.Monster?.GetType().Name == "WaterfallGiant"
            && name is "PressureGunBonus" or "SteamEruptionDamage")
        {
            if (_monsterIntStates?.TryGetValue((creature, name), out int captured) == true)
                return captured;
            return name == "PressureGunBonus"
                ? _pressureGunBonus?.GetValueOrDefault(creature) ?? 0
                : _steamEruptionDamage?.GetValueOrDefault(creature) ?? 0;
        }
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
    /// Waterfall Giant's forced Steam Eruption phase as 0/1/2
    /// (<c>FlashSteamPhase.None/AboutToBlow/Explode</c>). The branch dictionary is keyed by the known
    /// Waterfall Giant creature; the importer writes the value into <c>FlashEnemy.SteamPhase</c>.
    /// </summary>
    internal int SteamEruptionPhaseForFlash(Creature creature)
        => _steamEruptionPhases?.TryGetValue(creature, out SteamEruptionPhase phase) == true
            ? (int)phase + 1
            : 0;

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
        // Nightmare selections are projected into FlashBattle.DetachedCards. Entries whose power has
        // been removed are inert (only positive EffectivePowers instances read their selection).
        if (_nightmareSelections != null)
        {
            foreach (NightmarePower power in _nightmareSelections.Keys)
            {
                if (power.Amount > 0 && !EffectivePowers().Contains(power))
                    return nameof(_nightmareSelections);
            }
        }
        // _orbitEnergyRemainders/_paleBlueDotActivated are projected into the power rows; entries of a
        // zero-amount power are inert for the same reason (readers visit positive EffectivePowers).
        // Monologue's per-card dictionary must be empty at a stable boundary.
        foreach (PowerModel power in EffectivePowers())
        {
            if (power is MonologuePower { Amount: > 0 } monologue && MonologuePendingCountForFlash(monologue) != 0)
                return "monologue";
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
        // _pressureGunBonus/_steamEruptionDamage/_steamEruptionPhases are projected onto the
        // WaterfallGiant AI registers and FlashEnemy.SteamPhase; every key must be a known
        // WaterfallGiant or the entry has no defined projection.
        if (_pressureGunBonus is { Count: > 0 } pressureBonus)
        {
            foreach (Creature creature in pressureBonus.Keys)
            {
                if (!KnownEnemies.Contains(creature) || creature.Monster?.GetType().Name != "WaterfallGiant")
                    return nameof(_pressureGunBonus);
            }
        }
        if (_steamEruptionDamage is { Count: > 0 } eruptionDamage)
        {
            foreach (Creature creature in eruptionDamage.Keys)
            {
                if (!KnownEnemies.Contains(creature) || creature.Monster?.GetType().Name != "WaterfallGiant")
                    return nameof(_steamEruptionDamage);
            }
        }
        if (_steamEruptionPhases is { Count: > 0 } eruptionPhases)
        {
            foreach (Creature creature in eruptionPhases.Keys)
            {
                if (!KnownEnemies.Contains(creature) || creature.Monster?.GetType().Name != "WaterfallGiant")
                    return nameof(_steamEruptionPhases);
            }
        }
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
        // _escapedCreatures is a run-facing list (the encounter's post-combat gold proportion);
        // CreatureEscaped also removes the creature from _knownEnemies and the roster, which are the
        // only prediction state. A non-empty list therefore has no unprojected prediction value.
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
        // _battlewornDummyTimedOut is projected as FlashOutcome.EventDefeat by the importer: the
        // existing engine sets it immediately before returning SearchBoundaryReason.EventDefeat, so a
        // state carrying the flag is exactly that boundary. The importer rejects a flag without the
        // EventDefeat boundary explicitly (a protocol error): see FlashStateImporter.Import.
        return null;
    }

    /// <summary>Instanced power types Flash represents as one row per instance and projects.</summary>
    private static bool IsProjectedInstancedPower(PowerModel power)
        => power is AutomationPower or PanachePower or RollingBoulderPower or TheBombPower
            or GigantificationPower or SandpitPower or MonologuePower or OrbitPower or NightmarePower
            or ThieveryPower or HeistPower or SwipePower or ToricToughnessPower;

    /// <summary>The applied SwordSage replay bonus of one blade, as the normalization table records it.</summary>
    internal int? SwordSageAppliedForFlash(PredictedCard card)
        => _swordSageReplayBonuses != null && _swordSageReplayBonuses.TryGetValue(card, out int applied)
            ? applied
            : null;

    /// <summary>Membership in <c>_fetchCardsPlayedThisTurn</c> (root history + this turn's plays).</summary>
    internal bool FetchPlayedThisTurnForFlash(PredictedCard card)
        => GetFetchCardsPlayedThisTurn().Contains(card.Original);

    /// <summary><c>_orbitEnergyRemainders[power]</c>; false when the instance has no entry.</summary>
    internal bool TryGetOrbitEnergyRemainder(OrbitPower power, out int remainder)
    {
        remainder = 0;
        return _orbitEnergyRemainders != null && _orbitEnergyRemainders.TryGetValue(power, out remainder);
    }

    /// <summary>The selected-card clone of an active NightmarePower (<c>_nightmareSelections</c>).</summary>
    internal PredictedCard? NightmareSelectionForFlash(NightmarePower power)
        => _nightmareSelections?.GetValueOrDefault(power);

    /// <summary>
    /// The number of cards recorded in a MonologuePower's internal per-card dictionary. A stable
    /// boundary must have none (BeforeCardPlayed adds, AfterCardPlayed removes).
    /// </summary>
    internal int MonologuePendingCountForFlash(MonologuePower power)
    {
        object? data = PowerInternalDataField.GetValue(power);
        if (data == null)
            return 0;
        System.Reflection.FieldInfo field = data.GetType().GetField(
            "amountsForPlayedCards",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException(data.GetType().FullName, "amountsForPlayedCards");
        return ((System.Collections.ICollection)field.GetValue(data)!).Count;
    }
}
