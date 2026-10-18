using CombatSolver;
using CombatSolver.Engine.InCombat.Flash;
using CombatSolver.FlashBeam;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace OfflineSearchHarness;

/// <summary>
/// Flash differential probe (OFFLINE_HARNESS_FLASH_DIFF=1): from the harness root, compares every
/// legal one-step action (K1) and seeded multi-turn random walks (K2) between the existing engine
/// and the Flash kernel. Equality is byte equality of the projected <see cref="FlashBattle"/>.
/// Writes <c>flash-diff.txt</c> and returns a summary for <c>harness-result.json</c>.
/// </summary>
internal static class FlashDifferentialProbe
{
    public static Dictionary<string, object?> Run(CombatState combat, string outputDirectory)
    {
        int walks = ReadInt("OFFLINE_HARNESS_FLASH_DIFF_WALKS", 16);
        int steps = ReadInt("OFFLINE_HARNESS_FLASH_DIFF_STEPS", 60);
        List<string> report = [];
        Dictionary<string, object?> summary = new() { ["walks"] = walks, ["steps"] = steps };

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings, combat, includeTurnSetup: false, theftPolicy: SolverController.ResolveTheftPolicy(combat));
        CombatBeamSolver solver = new(root, names, damage, policy, searchProfile: policy.Profile);
        Player player = root.PlayerIdentity;
        SimulationSnapshot rootSnapshot = solver.ReplayForFlash([], null);

        FlashRules rules;
        FlashBattle flashRoot;
        try
        {
            rules = FlashRulesCompiler.Compile(rootSnapshot.Simulator, player);
            flashRoot = FlashStateImporter.Import(rootSnapshot, player, rules);
        }
        catch (FlashImportException ex)
        {
            summary["admitted"] = false;
            summary["rejection"] = ex.Reason.ToString();
            summary["detail"] = ex.Detail;
            report.Add($"REJECTED {ex.Reason}: {ex.Detail}");
            File.WriteAllLines(Path.Combine(outputDirectory, "flash-diff.txt"), report);
            return summary;
        }
        summary["admitted"] = true;
        report.Add("ROOT");
        report.AddRange(FlashBattleDump.Lines(flashRoot).Select(line => "  " + line));

        FlashAction[] actions = new FlashAction[FlashKernel.MaxLegalActions];
        int oneStepChecked = 0, oneStepFailed = 0, choiceSkipped = 0;
        int count = FlashKernel.LegalActions(flashRoot, rules, actions);
        for (int index = 0; index < count; index++)
        {
            Outcome outcome = Compare(solver, player, rules, rootSnapshot, flashRoot, actions[index]);
            if (outcome.Skipped)
            {
                choiceSkipped++;
                continue;
            }
            oneStepChecked++;
            if (outcome.Diff.Count > 0)
            {
                oneStepFailed++;
                report.Add($"K1 MISMATCH {actions[index]}");
                report.AddRange(outcome.Diff.Select(line => "  " + line));
            }
        }

        int walkTransitions = 0, walkFailed = 0, terminals = 0;
        for (int seed = 1; seed <= walks; seed++)
        {
            Random random = new(seed);
            SimulationSnapshot snapshot = rootSnapshot;
            FlashBattle battle = flashRoot;
            List<string> path = [];
            for (int step = 0; step < steps; step++)
            {
                if (!FlashKernel.IsInProgress(battle) || snapshot.BoundaryReason != SearchBoundaryReason.None)
                {
                    terminals++;
                    break;
                }
                count = FlashKernel.LegalActions(battle, rules, actions);
                // Favour card plays so walks reach deeper hands, deaths and shuffles.
                FlashAction action = count > 1 && random.Next(4) != 0 ? actions[random.Next(count - 1)] : actions[random.Next(count)];
                path.Add(action.ToString());
                Outcome outcome = Compare(solver, player, rules, snapshot, battle, action);
                if (outcome.Skipped)
                {
                    choiceSkipped++;
                    break;
                }
                walkTransitions++;
                if (outcome.Diff.Count > 0)
                {
                    walkFailed++;
                    report.Add($"K2 MISMATCH seed={seed} step={step} path={string.Join(" > ", path)}");
                    report.AddRange(outcome.Diff.Select(line => "  " + line));
                    break;
                }
                snapshot = outcome.OldChild!;
                battle = outcome.FlashChild;
            }
        }

        summary["oneStepChecked"] = oneStepChecked;
        summary["oneStepFailed"] = oneStepFailed;
        summary["walkTransitions"] = walkTransitions;
        summary["walkFailed"] = walkFailed;
        summary["walkTerminals"] = terminals;
        summary["choiceSkipped"] = choiceSkipped;
        report.Insert(0, $"SUMMARY oneStep={oneStepChecked} failed={oneStepFailed} walkTransitions={walkTransitions} "
            + $"walkFailed={walkFailed} terminals={terminals} choiceSkipped={choiceSkipped}");
        File.WriteAllLines(Path.Combine(outputDirectory, "flash-diff.txt"), report);
        return summary;
    }

    private sealed record Outcome(bool Skipped, SimulationSnapshot? OldChild, FlashBattle FlashChild, List<string> Diff);

    private static Outcome Compare(CombatBeamSolver solver, Player player, FlashRules rules,
        SimulationSnapshot parent, in FlashBattle battle, in FlashAction action)
    {
        FlashBattle child = battle;
        if (FlashKernel.Step(ref child, action, rules, out _) == FlashStepResult.NeedsChoice)
            return new Outcome(true, null, default, []);
        PlanAction plan = FlashActionTranslator.ToPlanAction(parent, player, action);
        SimulationSnapshot oldChild = solver.ReplayForFlash([plan], parent);
        FlashBattle projected;
        try
        {
            projected = FlashStateImporter.Import(oldChild, player, rules);
        }
        catch (FlashImportException ex)
        {
            return new Outcome(false, oldChild, child, [$"旧引擎结果无法投影：{ex.Reason} {ex.Detail}"]);
        }
        return new Outcome(false, oldChild, child, FlashBattleDump.Diff(projected, child, "old", "flash"));
    }

    private static int ReadInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;
}
