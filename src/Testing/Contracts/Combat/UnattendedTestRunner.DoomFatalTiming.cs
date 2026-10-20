using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private const string DoomFatalTimingScenarioId = "DOOM-FATAL-TIMING-V0111";

    /// <summary>
    /// Native <c>DoomPower.DoomKill</c> kills every doomed creature, then dispatches
    /// <c>AfterDiedToDoom</c>; <c>BookRepairKnife</c> counts fatal creatures there from their
    /// post-death power table. This contract runs the native kill and the branch kill from the same
    /// root and compares the complete state. The fixture keeps a non-Doom survivor so the combat is
    /// still live when the heal is observable.
    /// </summary>
    private async Task<int> RunDoomFatalTimingDifferentialAsync(CombatState combat, Player player)
    {
        if (combat.Players.Count != 1 || player.Osty != null)
            throw new InvalidOperationException("Doom 致命计数夹具要求无奥斯提的单人战斗。");
        if (combat.Enemies.Count(enemy => enemy.Monster is DecimillipedeSegment) != 3)
            throw new InvalidOperationException("Doom 致命计数夹具要求三个千足虫节段。");
        if (!combat.Enemies.Any(enemy => enemy.IsAlive && enemy.Monster is not DecimillipedeSegment))
            throw new InvalidOperationException("Doom 致命计数夹具要求至少一个不会被 Doom 击杀的存活敌人。");

        Creature[] segments = combat.Enemies
            .Where(enemy => enemy.Monster is DecimillipedeSegment)
            .ToArray();
        foreach (Creature segment in segments)
        {
            if (segment.GetPower<ReattachPower>()?.Amount != 25)
                throw new InvalidOperationException("Doom 致命计数夹具缺少原版 25 层重新接合能力。");
        }

        BookRepairKnife knife = player.Relics.OfType<BookRepairKnife>().SingleOrDefault()
            ?? throw new InvalidOperationException("Doom 致命计数夹具要求恰好一个 BookRepairKnife。");
        if (knife.DynamicVars.Heal.BaseValue != 3)
            throw new InvalidOperationException("BookRepairKnife 的治疗量不是原版 3。");

        await CreatureCmd.SetMaxHp(player.Creature, 1000);
        await CreatureCmd.SetCurrentHp(player.Creature, 500);
        foreach (Creature segment in segments)
        {
            await CreatureCmd.SetCurrentHp(segment, 20);
            int doom = segment.GetPower<DoomPower>()?.Amount ?? 0;
            if (doom < 20)
            {
                await PowerCmd.Apply(
                    new BlockingPlayerChoiceContext(),
                    ModelDb.Power<DoomPower>().ToMutable(),
                    segment,
                    20 - doom,
                    player.Creature,
                    null);
            }
        }
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        const int baseHp = 500;
        int healPerFatal = (int)knife.DynamicVars.Heal.BaseValue;
        int expectedHeal = healPerFatal * segments.Length;
        foreach (Creature segment in segments)
        {
            if (segment.CurrentHp != 20 || segment.GetPower<DoomPower>()?.Amount < 20)
                throw new InvalidOperationException("Doom 致命计数夹具没有把节段置于同一次 Doom 击杀阈值。");
        }

        SimulatedCombatState simulatedCombat;
        CombatPredictionSimulator simulator;
        using (BranchMonsterStaticSnapshot.AllowUnreachableConditionalsForTesting())
        {
            simulatedCombat = new SimulatedCombatState(combat);
            simulator = new CombatPredictionSimulator(simulatedCombat);
        }

        // Run the branch first: it snapshots the same root, so the later native kill cannot mutate
        // the branch's power rows or death phases.
        simulatedCombat.DoomKill(simulator, segments);
        MoveStateSnapshot predicted = CaptureSimulated(simulator, simulatedCombat, player, segments[0]);

        await DoomPower.DoomKill(segments);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        MoveStateSnapshot actual = CaptureActual(combat, player, segments[0]);

        Entry.Logger.Info(
            $"[CombatSolver/Unattended] DOOM_FATAL_TIMING run_id={_request.RunId} " +
            $"fatal={segments.Length} heal={expectedHeal} " +
            $"predicted_hp={predicted.PlayerHp} actual_hp={actual.PlayerHp} " +
            $"predicted_enemy_powers=[{string.Join(",", predicted.EnemyPowers.Select(pair => $"{pair.Key}={pair.Value}"))}] " +
            $"actual_enemy_powers=[{string.Join(",", actual.EnemyPowers.Select(pair => $"{pair.Key}={pair.Value}"))}]");
        if (actual.PlayerHp != baseHp + expectedHeal)
            throw new InvalidOperationException(
                $"原版 Doom 击杀后玩家生命为 {actual.PlayerHp}，预期 {baseHp}+{expectedHeal}。");
        if (predicted.PlayerHp != actual.PlayerHp)
            throw new InvalidOperationException(
                $"Doom 致命计数的治疗时点不一致：predicted_hp={predicted.PlayerHp} actual_hp={actual.PlayerHp} "
                + $"predicted_enemy_powers=[{string.Join(",", predicted.EnemyPowers.Select(pair => $"{pair.Key}={pair.Value}"))}] "
                + $"actual_enemy_powers=[{string.Join(",", actual.EnemyPowers.Select(pair => $"{pair.Key}={pair.Value}"))}]。");
        foreach (Creature segment in segments)
        {
            if (segment.GetPower<DoomPower>() != null)
                throw new InvalidOperationException("原版 Doom 击杀后仍保留 DoomPower。");
            if (segment.GetPower<ReattachPower>() == null)
                throw new InvalidOperationException("原版 Doom 击杀后重新接合能力不应被移除。");
        }
        AssertSnapshotEqual(predicted, actual, "DoomPower", "DoomKillBookRepairKnife");
        _completedChecks.Add(
            $"DoomFatalTiming:Segments={segments.Length}:Heal={expectedHeal}:PostDeathPowers");
        return player.PlayerCombatState?.TurnNumber ?? 1;
    }
}
