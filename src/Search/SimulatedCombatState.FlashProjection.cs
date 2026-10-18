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

    internal bool IsPrimaryEnemyForFlash(Creature creature)
        => ((CombatSolver.Engine.Common.ICombatPredictionCreatureSemantics)this).IsPrimaryEnemy(creature);

    /// <summary>
    /// Names the first branch state Flash does not model, or null. Counters that every card play
    /// updates are not listed: only the content that reads them makes them matter, and such content
    /// has no Flash definition.
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
        if (_monsterIntStates is { Count: > 0 })
            return nameof(_monsterIntStates);
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
        // Root capture may record inert states for relics outside IsStatefulRelic; only states that
        // some reader consumes matter, and those relics have no Flash definition yet.
        if (_statefulRelicStates != null)
        {
            foreach (RelicModel relic in _statefulRelicStates.Keys)
            {
                if (IsStatefulRelic(relic))
                    return $"{nameof(_statefulRelicStates)}:{relic.Id.Entry}";
            }
        }
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
        if (_deathPhases != null && _deathPhases.Values.Any(phase => phase == PredictedDeathPhase.Reviving))
            return "reviving enemy";
        return null;
    }
}
