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
    internal int GetMonsterIntForFlash(Creature creature, string name) => GetMonsterInt(creature, name);

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
        if (_dampenOriginalUpgrades is { Count: > 0 } || _dampenCasters is { Count: > 0 })
            return "dampen";
        if (_swordSageReplayBonuses is { Count: > 0 })
            return nameof(_swordSageReplayBonuses);
        if (_unsettlingLampTriggeringCards is { Count: > 0 } || _unsettlingLampInternalPowerTypes is { Count: > 0 })
            return "unsettling lamp";
        if (_nemesisShouldApplyIntangible is { Count: > 0 })
            return nameof(_nemesisShouldApplyIntangible);
        if (_aeonglassAdditionalStrength is { Count: > 0 } || _aeonglassWitherUpgradeCount is { Count: > 0 })
            return "aeonglass";
        if (_knowledgeDemonCurseCounters is { Count: > 0 })
            return nameof(_knowledgeDemonCurseCounters);
        if (_pressureGunBonus is { Count: > 0 })
            return nameof(_pressureGunBonus);
        if (_simulatedOsties is { Count: > 0 } || _simulatedOstyMaxHp is { Count: > 0 })
            return "osty";
        if (_steamEruptionDamage is { Count: > 0 } || _steamEruptionPhases is { Count: > 0 })
            return "steam eruption";
        if (_stolenDexterity is { Count: > 0 } || _stolenStrength is { Count: > 0 })
            return "possess";
        if (_drawNextTurn != null && _drawNextTurn.Values.Any(value => value != 0))
            return nameof(_drawNextTurn);
        if (_freeEntropicPotions is { Count: > 0 })
            return nameof(_freeEntropicPotions);
        // Stateful relic states are claimed by the Flash bridge's relic projections
        // (StatefulRelicKeysForFlash); root capture may also record inert states for relics outside
        // IsStatefulRelic, which no reader consumes.
        if (_escapedCreatures.Count > 0)
            return nameof(_escapedCreatures);
        if (_returnToHandNextTurn is { Count: > 0 })
            return nameof(_returnToHandNextTurn);
        if (_addedPowerInstances is { Count: > 0 } || _rootMultiInstancePowerClones is { Count: > 0 })
            return "instanced powers";
        if (_tenderCardsPlayed != null && _tenderCardsPlayed.Values.Any(value => value != 0))
            return nameof(_tenderCardsPlayed);
        if (_outstandingStolenGold is > 0 || _outstandingStolenCards is > 0)
            return "theft";
        if (_battlewornDummyTimedOut)
            return nameof(_battlewornDummyTimedOut);
        return null;
    }
}
