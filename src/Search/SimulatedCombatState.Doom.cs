using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    public void DoomKill(CombatPredictionSimulator simulator, IReadOnlyList<Creature> creatures)
    {
        if (creatures.Count == 0)
            return;
        // Native DoomPower.DoomKill calls CreatureCmd.Kill for every creature and only then runs
        // Hook.AfterDiedToDoom. CreatureCmd.Kill completes that creature's AfterDeath hook and power
        // removal before returning, so AfterDiedToDoom reads every batch member after its own death
        // cleanup (notably ReattachPower.ShouldOwnerDeathTriggerFatal sees the whole batch dead).
        // Enemy death effects are deferred in this branch, so run the same deferred pass after each
        // kill; it is idempotent for the caller's later boundary pass.
        HashSet<uint>? processedDeaths = null;
        foreach (Creature creature in creatures)
        {
            if (!simulator.Kill(creature))
                return;
            processedDeaths ??= [];
            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator, this, KnownEnemies, processedDeaths))
            {
                return;
            }
        }
        Dictionary<Player, int> fatalCounts = [];
        foreach (Player player in Players)
        {
            int count = creatures.Count(creature => creature != player.Creature
                && ShouldOwnerDeathTriggerFatal(simulator, creature));
            fatalCounts[player] = count;
        }
        foreach ((Player player, int count) in fatalCounts)
        {
            if (count <= 0)
                continue;
            foreach (BookRepairKnife relic in RelicsOf(player)
                         .OfType<BookRepairKnife>()
                         .Where(relic => !relic.IsMelted))
            {
                simulator.Heal(player.Creature, relic.DynamicVars.Heal.BaseValue * count);
                if (simulator.HasPendingChoice)
                    return;
            }
        }
    }
}
