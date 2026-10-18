using CombatSolver;
using CombatSolver.Engine.InCombat.Flash;
using CombatSolver.FlashBeam;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace OfflineSearchHarness;

/// <summary>
/// Flash differential probe (OFFLINE_HARNESS_FLASH_DIFF=1): from the harness root, compares every
/// legal one-step action (K1, each choice completion up to <see cref="OneStepChoiceCap"/>) and seeded
/// multi-turn random walks (K2, random legal selections) between the existing engine and the Flash
/// kernel. Equality is byte equality of the projected <see cref="FlashBattle"/>; every choice the
/// existing engine asks for must also match the Flash request (options, order, bounds, effect, timing).
/// Writes <c>flash-diff.txt</c> and returns a summary for <c>harness-result.json</c>.
/// </summary>
internal static class FlashDifferentialProbe
{
    private const int OneStepChoiceCap = 64;

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
        int oneStepChecked = 0, oneStepFailed = 0, choiceActions = 0, choiceCapped = 0, choiceDeadEnds = 0;
        int count = FlashKernel.LegalActions(flashRoot, rules, actions);
        for (int index = 0; index < count; index++)
        {
            List<Completion> completions = [];
            if (Complete(rules, flashRoot, actions[index], [], completions))
                choiceCapped++;
            foreach (Completion completion in completions)
            {
                if (completion.DeadEnd)
                {
                    choiceDeadEnds++;
                    continue;
                }
                if (completion.Requests.Count > 0)
                    choiceActions++;
                Outcome outcome = Compare(solver, player, rules, rootSnapshot, completion);
                oneStepChecked++;
                if (outcome.Diff.Count > 0)
                {
                    oneStepFailed++;
                    report.Add($"K1 MISMATCH {completion.Action} choices={Selections(completion.Action)}");
                    report.AddRange(outcome.Diff.Select(line => "  " + line));
                }
            }
        }

        int walkTransitions = 0, walkFailed = 0, terminals = 0;
        Dictionary<string, int> endings = new(StringComparer.Ordinal);
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
                    endings[$"{battle.Outcome}/{snapshot.BoundaryReason}"] = endings.GetValueOrDefault($"{battle.Outcome}/{snapshot.BoundaryReason}") + 1;
                    break;
                }
                count = FlashKernel.LegalActions(battle, rules, actions);
                // Favour card plays so walks reach deeper hands, deaths and shuffles.
                FlashAction action = count > 1 && random.Next(4) != 0 ? actions[random.Next(count - 1)] : actions[random.Next(count)];
                Completion completion = CompleteRandomly(rules, battle, action, random);
                path.Add($"{completion.Action}{(completion.Requests.Count > 0 ? " " + Selections(completion.Action) : "")}");
                if (completion.DeadEnd)
                {
                    choiceDeadEnds++;
                    break;
                }
                if (completion.Requests.Count > 0)
                    choiceActions++;
                Outcome outcome = Compare(solver, player, rules, snapshot, completion);
                walkTransitions++;
                if (outcome.Diff.Count > 0)
                {
                    walkFailed++;
                    report.Add($"K2 MISMATCH seed={seed} step={step} path={string.Join(" > ", path)}");
                    report.AddRange(outcome.Diff.Select(line => "  " + line));
                    if (walkFailed <= 3)
                    {
                        report.Add("  parent (flash):");
                        report.AddRange(FlashBattleDump.Lines(battle).Select(line => "    " + line));
                    }
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
        summary["choiceActions"] = choiceActions;
        summary["choiceCapped"] = choiceCapped;
        summary["choiceDeadEnds"] = choiceDeadEnds;
        summary["walkEndings"] = endings;
        report.Insert(0, $"SUMMARY oneStep={oneStepChecked} failed={oneStepFailed} walkTransitions={walkTransitions} "
            + $"walkFailed={walkFailed} terminals={terminals} choiceActions={choiceActions} choiceCapped={choiceCapped} "
            + $"choiceDeadEnds={choiceDeadEnds} endings={string.Join(",", endings.Select(item => $"{item.Key}:{item.Value}"))}");
        File.WriteAllLines(Path.Combine(outputDirectory, "flash-diff.txt"), report);
        return summary;
    }

    private sealed record Outcome(SimulationSnapshot? OldChild, FlashBattle FlashChild, List<string> Diff);

    /// <summary>A fully specified action, the Flash requests it answered (with the partial battle at each request) and its result.</summary>
    private sealed record Completion(FlashAction Action, List<(FlashChoiceRequest Request, FlashBattle Partial)> Requests,
        FlashBattle Child, bool DeadEnd);

    /// <summary>Every choice completion of <paramref name="action"/>; returns true when the cap truncated it.</summary>
    private static bool Complete(FlashRules rules, in FlashBattle battle, FlashAction action,
        List<(FlashChoiceRequest Request, FlashBattle Partial)> requests, List<Completion> output)
    {
        FlashBattle child = battle;
        if (FlashKernel.Step(ref child, action, rules, out FlashChoiceRequest request) == FlashStepResult.Completed)
        {
            output.Add(new Completion(action, [.. requests], child, false));
            return false;
        }
        Span<ulong> selections = stackalloc ulong[OneStepChoiceCap];
        int count = FlashChoiceEnumerator.Enumerate(request, selections);
        if (count == 0)
        {
            output.Add(new Completion(action, [.. requests], default, true));
            return false;
        }
        bool capped = FlashChoiceEnumerator.Count(request, OneStepChoiceCap + 1) > count;
        requests.Add((request, child));
        for (int index = 0; index < count && output.Count < OneStepChoiceCap; index++)
            capped |= Complete(rules, battle, action.WithChoice(selections[index]), requests, output);
        requests.RemoveAt(requests.Count - 1);
        return capped || output.Count >= OneStepChoiceCap;
    }

    private static Completion CompleteRandomly(FlashRules rules, in FlashBattle battle, FlashAction action, Random random)
    {
        List<(FlashChoiceRequest Request, FlashBattle Partial)> requests = [];
        Span<ulong> selections = stackalloc ulong[OneStepChoiceCap];
        while (true)
        {
            FlashBattle child = battle;
            if (FlashKernel.Step(ref child, action, rules, out FlashChoiceRequest request) == FlashStepResult.Completed)
                return new Completion(action, requests, child, false);
            int count = FlashChoiceEnumerator.Enumerate(request, selections);
            if (count == 0)
                return new Completion(action, requests, default, true);
            requests.Add((request, child));
            action = action.WithChoice(selections[random.Next(count)]);
        }
    }

    private static string Selections(in FlashAction action)
    {
        List<string> parts = [];
        for (int index = 0; index < action.ChoiceCount; index++)
            parts.Add($"{action.Choices[index]:X}");
        return "[" + string.Join(",", parts) + "]";
    }

    private static Outcome Compare(CombatBeamSolver solver, Player player, FlashRules rules,
        SimulationSnapshot parent, Completion completion)
    {
        List<string> requestDiff = [];
        SimulationSnapshot oldChild;
        try
        {
            oldChild = FlashChoiceTranslator.Replay(solver, parent, player, completion.Action, out _,
                (index, request, spec) =>
                {
                    (FlashChoiceRequest flash, FlashBattle partial) = completion.Requests[index];
                    requestDiff.AddRange(FlashChoiceTranslator.CompareRequest(partial, flash, request, spec)
                        .Select(line => $"选择[{index}] {line}"));
                });
        }
        catch (Exception ex) when (ex is FlashChoiceMismatchException or InvalidPlannedChoiceBranchException)
        {
            return new Outcome(null, completion.Child, [.. requestDiff, $"选择对不上：{ex.Message}"]);
        }
        if (requestDiff.Count > 0)
            return new Outcome(oldChild, completion.Child, requestDiff);
        FlashBattle projected;
        try
        {
            projected = FlashStateImporter.Import(oldChild, player, rules);
        }
        catch (FlashImportException ex)
        {
            return new Outcome(oldChild, completion.Child, [$"旧引擎结果无法投影：{ex.Reason} {ex.Detail}"]);
        }
        return new Outcome(oldChild, completion.Child, FlashBattleDump.Diff(projected, completion.Child, "old", "flash"));
    }

    private static int ReadInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;
}
