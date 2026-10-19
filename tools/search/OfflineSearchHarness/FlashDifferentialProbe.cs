using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Creatures;
using CombatSolver.Engine.InCombat.Simulation;
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
/// K4: on every state whose projection matched, the <see cref="FlashScorer"/> evaluation must equal
/// the existing snapshot's (<see cref="FlashScoreFeatures.Diff"/>).
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
            flashRoot = FlashStateImporter.Import(rootSnapshot, player, rules, rootSnapshot.PotionUseCount);
        }
        catch (FlashImportException ex)
        {
            summary["admitted"] = false;
            summary["rejection"] = ex.Reason.ToString();
            summary["detail"] = ex.Detail;
            report.Add($"REJECTED {ex.Reason}: {ex.Detail}");
            report.Add(MissingContent(rootSnapshot.Simulator, player));
            File.WriteAllLines(Path.Combine(outputDirectory, "flash-diff.txt"), report);
            return summary;
        }
        summary["admitted"] = true;
        report.Add("ROOT");
        report.AddRange(FlashBattleDump.Lines(flashRoot).Select(line => "  " + line));
        FlashScorer? scorer = null;
        try
        {
            scorer = FlashScoreBridge.Create(root, policy, policy.Profile, rootSnapshot, rootSnapshot,
                player, rules, flashRoot);
        }
        catch (FlashImportException ex)
        {
            report.Add($"SCORE REJECTED {ex.Reason}: {ex.Detail}");
        }
        FlashBoundFeatures? bound = null;
        if (scorer != null)
            bound = FlashBoundBridge.Create(root, policy, rootSnapshot, player, rules, flashRoot,
                solver.CapturePruningRequestForFlash());
        ScoreCheck scores = new(scorer, bound, solver, report);
        scores.Check(rootSnapshot, flashRoot, 0, "ROOT");

        FlashAction[] actions = new FlashAction[FlashKernel.MaxLegalActions];
        int oneStepChecked = 0, oneStepFailed = 0, choiceActions = 0, choiceCapped = 0, choiceDeadEnds = 0, unsupported = 0;
        int count = FlashKernel.LegalActions(flashRoot, rules, actions);
        for (int index = 0; index < count; index++)
        {
            List<Completion> completions = [];
            try
            {
                if (Complete(rules, flashRoot, actions[index], [], completions))
                    choiceCapped++;
            }
            catch (Exception ex) when (ex is FlashUnsupportedException or FlashCapacityException)
            {
                // A Flash boundary, not a divergence: production falls back to the existing solver.
                if (unsupported++ == 0)
                    report.Add($"UNSUPPORTED K1 {actions[index]}: {ex.Message}");
                continue;
            }
            foreach (Completion completion in completions)
            {
                if (completion.DeadEnd)
                {
                    choiceDeadEnds++;
                    continue;
                }
                if (completion.Requests.Count > 0)
                    choiceActions++;
                Outcome outcome = Compare(solver, player, rules, rootSnapshot, rootSnapshot.PotionUseCount, completion);
                oneStepChecked++;
                if (outcome.Diff.Count > 0)
                {
                    oneStepFailed++;
                    report.Add($"K1 MISMATCH {completion.Action} choices={Selections(completion.Action)}");
                    report.AddRange(outcome.Diff.Select(line => "  " + line));
                }
                else
                {
                    scores.Check(outcome.OldChild!, outcome.FlashChild, 1, $"K1 {completion.Action} choices={Selections(completion.Action)}");
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
                Completion completion;
                try
                {
                    completion = CompleteRandomly(rules, battle, action, random);
                }
                catch (Exception ex) when (ex is FlashUnsupportedException or FlashCapacityException)
                {
                    if (unsupported++ == 0)
                        report.Add($"UNSUPPORTED K2 seed={seed} step={step}: {ex.Message}");
                    break;
                }
                path.Add($"{completion.Action}{(completion.Requests.Count > 0 ? " " + Selections(completion.Action) : "")}");
                if (completion.DeadEnd)
                {
                    choiceDeadEnds++;
                    break;
                }
                if (completion.Requests.Count > 0)
                    choiceActions++;
                Outcome outcome = Compare(solver, player, rules, snapshot, rootSnapshot.PotionUseCount, completion);
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
                scores.Check(outcome.OldChild!, outcome.FlashChild, 1, $"K2 seed={seed} step={step} path={string.Join(" > ", path)}");
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
        summary["scoreChecked"] = scores.Checked;
        summary["scoreFailed"] = scores.Failed;
        summary["scoreSkippedBoundary"] = scores.SkippedBoundary;
        summary["scoreFailedFields"] = scores.FailedFields;
        summary["bound"] = scores.BoundStatus;
        summary["boundChecked"] = scores.BoundChecked;
        summary["boundFailed"] = scores.BoundFailed;
        summary["unsupported"] = unsupported;
        summary["boundFailedFields"] = scores.BoundFailedFields;
        report.Insert(0, $"SUMMARY oneStep={oneStepChecked} failed={oneStepFailed} walkTransitions={walkTransitions} "
            + $"walkFailed={walkFailed} terminals={terminals} choiceActions={choiceActions} choiceCapped={choiceCapped} "
            + $"choiceDeadEnds={choiceDeadEnds} endings={string.Join(",", endings.Select(item => $"{item.Key}:{item.Value}"))} "
            + $"score={(scorer == null ? "rejected" : "checked")} scoreChecked={scores.Checked} scoreFailed={scores.Failed} "
            + $"scoreSkippedBoundary={scores.SkippedBoundary} "
            + (scores.BoundStatus == "checked"
                ? $"bound=checked boundChecked={scores.BoundChecked} boundFailed={scores.BoundFailed}"
                : $"bound=disabled({scores.BoundStatus})") + $" unsupported={unsupported}");
        if (scores.FailedFields.Count > 0)
            report.Insert(1, "K4 FIELDS " + string.Join(",", scores.FailedFields
                .OrderByDescending(item => item.Value).Select(item => $"{item.Key}:{item.Value}")));
        if (scores.BoundFailedFields.Count > 0)
            report.Insert(1, "K5 FIELDS " + string.Join(",", scores.BoundFailedFields
                .OrderByDescending(item => item.Value).Select(item => $"{item.Key}:{item.Value}")));
        File.WriteAllLines(Path.Combine(outputDirectory, "flash-diff.txt"), report);
        return summary;
    }

    private sealed record Outcome(SimulationSnapshot? OldChild, FlashBattle FlashChild, List<string> Diff);

    /// <summary>
    /// K4 and K5: the Flash evaluation and the incumbent lower-bound inputs of every projected state must
    /// equal the existing snapshot's. States the existing engine stopped at a search boundary are skipped.
    /// </summary>
    /// <summary>
    /// Every card, relic, potion and known monster of the root that has no Flash definition, so a survey
    /// sees more than the first rejection.
    /// </summary>
    private static string MissingContent(CombatPredictionSimulator simulator, Player player)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        SortedSet<string> cards = new(StringComparer.Ordinal), relics = new(StringComparer.Ordinal),
            potions = new(StringComparer.Ordinal), monsters = new(StringComparer.Ordinal);
        foreach (SimCardPile pile in new[] { state.Hand, state.DrawPile, state.DiscardPile, state.ExhaustPile, state.PlayPile })
        {
            foreach (PredictedCard card in pile.Cards)
            {
                if (!FlashRegistry.TryCard(card.Preview.Id.Entry, out _))
                    cards.Add(card.Preview.Id.Entry);
            }
        }
        foreach (RelicModel relic in combat.RelicsOf(player))
        {
            if (!FlashRegistry.TryRelic(relic.Id.Entry, out _))
                relics.Add(relic.Id.Entry);
        }
        for (int slot = 0; slot < combat.PotionSlotCountForFlash(player); slot++)
        {
            if (combat.GetPotionAtSlot(player, slot) is { } potion && !FlashRegistry.TryPotion(potion.Id.Entry, out _))
                potions.Add(potion.Id.Entry);
        }
        foreach (Creature enemy in combat.KnownEnemies)
        {
            if (enemy.Monster is { } monster && !FlashRegistry.TryMonster(monster.Id.Entry, out _))
                monsters.Add(monster.Id.Entry);
        }
        return $"MISSING cards=[{string.Join(",", cards)}] relics=[{string.Join(",", relics)}] "
            + $"potions=[{string.Join(",", potions)}] monsters=[{string.Join(",", monsters)}]";
    }

    private sealed class ScoreCheck(
        FlashScorer? scorer, FlashBoundFeatures? bound, CombatBeamSolver solver, List<string> report)
    {
        private const int ReportedMismatches = 20;

        public int Checked { get; private set; }

        public int Failed { get; private set; }

        /// <summary>States the existing engine stopped at a search boundary; their snapshot skips the threat projection.</summary>
        public int SkippedBoundary { get; private set; }

        public Dictionary<string, int> FailedFields { get; } = new(StringComparer.Ordinal);

        public int BoundChecked { get; private set; }

        public int BoundFailed { get; private set; }

        public Dictionary<string, int> BoundFailedFields { get; } = new(StringComparer.Ordinal);

        public string BoundStatus => bound == null ? "score_rejected" : bound.Enabled ? "checked" : bound.DisabledReason;

        public void Check(SimulationSnapshot old, in FlashBattle flash, int actionCount, string label)
        {
            if (old.BoundaryReason != SearchBoundaryReason.None)
            {
                SkippedBoundary++;
                return;
            }
            FlashScoreFeatures? flashFeatures = scorer?.Features(flash, actionCount);
            CheckScore(old, flash, flashFeatures, label);
            CheckBound(old, flash, flashFeatures, label);
        }

        private void CheckScore(SimulationSnapshot old, in FlashBattle flash,
            FlashScoreFeatures? flashFeatures, string label)
        {
            if (flashFeatures is not { } features)
                return;
            Checked++;
            int oldMinimumOutstanding = 0;
            if (old.HasSimulator)
            {
                CombatPredictionSimulator oldSimulator = (CombatPredictionSimulator)old.Simulator;
                oldMinimumOutstanding =
                    ((SimulatedCombatState)oldSimulator.State.CombatState)
                    .MinimumOutstandingStolenResource(oldSimulator);
            }
            List<string> diff = FlashScoreFeatures.Diff(
                FlashScoreProjection.FromSnapshot(old, oldMinimumOutstanding), features);
            if (diff.Count == 0)
                return;
            Failed++;
            foreach (string line in diff)
                FailedFields[Field(line)] = FailedFields.GetValueOrDefault(Field(line)) + 1;
            if (Failed <= ReportedMismatches)
            {
                report.Add($"K4 MISMATCH {label}");
                report.AddRange(diff.Select(line => "  " + line));
            }
        }

        private void CheckBound(SimulationSnapshot old, in FlashBattle flash,
            FlashScoreFeatures? flashFeatures, string label)
        {
            if (bound is not { Enabled: true } || flashFeatures is not { } features)
                return;
            BoundChecked++;
            FlashBoundValues oldValues = new(
                HasRisk: old.HasRisk,
                FutureHealPotential: old.FutureHealPotential,
                RemainingHealingPotential: solver.PrimaryIncumbentRemainingHealingPotential(old) ?? int.MaxValue,
                FutureHealUsed: solver.PrimaryIncumbentFutureHealPotential(old),
                CumulativePlayerHpLost: old.CumulativePlayerHpLost,
                RecoveredPlayerHp: old.RecoveredPlayerHp,
                PlayerHp: old.PlayerHp,
                PlayerMaxHp: old.PlayerMaxHp,
                DeathSaveHpRestored: old.DeathSaveHpRestored,
                ExplicitPotionStrategicCost: old.ExplicitPotionStrategicCost,
                StrategicHpLowerBound: solver.PrimaryIncumbentHpLowerBound(old),
                SmartPotionRemainingHealingPotential: solver.PrimarySmartPotionRemainingHealingPotential(old),
                SmartPotionHpLowerBound: solver.PrimarySmartPotionHpLowerBound(old));
            FlashBoundValues flashValues = bound.Evaluate(flash, features.ExplicitPotionStrategicCost,
                includeSmartPotionLowerBound: true);
            List<string> diff = FlashBoundFeatures.Diff(oldValues, flashValues);
            if (diff.Count == 0)
                return;
            BoundFailed++;
            foreach (string line in diff)
                BoundFailedFields[Field(line)] = BoundFailedFields.GetValueOrDefault(Field(line)) + 1;
            if (BoundFailed <= ReportedMismatches)
            {
                report.Add($"K5 MISMATCH {label}");
                report.AddRange(diff.Select(line => "  " + line));
            }
        }

        private static string Field(string line) => line[..line.IndexOf(' ')];
    }

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
        SimulationSnapshot parent, int skippedPotionUses, Completion completion)
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
                },
                (index, knowledge) =>
                {
                    (FlashChoiceRequest flash, FlashBattle partial) = completion.Requests[index];
                    requestDiff.AddRange(FlashChoiceTranslator.CompareKnowledgeRequest(partial, flash, knowledge)
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
            projected = FlashStateImporter.Import(oldChild, player, rules, skippedPotionUses);
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
