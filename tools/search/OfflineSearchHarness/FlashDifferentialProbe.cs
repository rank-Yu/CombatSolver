using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Creatures;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver;
using CombatSolver.Flash.Kernel;
using CombatSolver.Flash.Search;
using CombatSolver.Flash.Search.Scoring;
using CombatSolver.Flash.Bridge;
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

    public static Dictionary<string, object?> Run(CombatState combat, HarnessOptions options, string outputDirectory,
        IReadOnlyList<string>? prefixActions = null, CapturedTurnSetupRoot? turnSetupRoot = null,
        IReadOnlyList<PlanAction>? prefixPlanOverride = null)
    {
        int walks = ReadInt("OFFLINE_HARNESS_FLASH_DIFF_WALKS", 16);
        int steps = ReadInt("OFFLINE_HARNESS_FLASH_DIFF_STEPS", 60);
        List<string> report = [];
        Dictionary<string, object?> summary = new() { ["walks"] = walks, ["steps"] = steps };

        CombatRootSnapshot root = turnSetupRoot?.Root ?? CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = turnSetupRoot?.Names ?? SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = turnSetupRoot?.Damage ?? BattleDamageTracker.Observe(combat);
        SolverSettingsSnapshot settings = turnSetupRoot?.Settings ?? SolverSettings.Capture();
        SearchPolicySnapshot policy = turnSetupRoot?.Policy ?? SolverController.CaptureSearchPolicy(
            settings, combat, includeTurnSetup: false, theftPolicy: SolverController.ResolveTheftPolicy(combat));
        policy = policy with
        {
            Profile = policy.Profile with
            {
                BaseScoreOnly = options.Ordering == "base",
                ContextualRanking = options.RankingModelPath == null ? null
                    : ContextualRankingModel.Parse(File.ReadAllText(options.RankingModelPath)),
                ContinuousThreatRanking = options.ContinuousThreatRanking,
                BeamWeightPerturbation = options.BeamWeightPerturbation,
            },
        };
        List<PlanAction> prefixPlan = prefixPlanOverride is { Count: > 0 }
            ? [.. prefixPlanOverride]
            : [];
        if (prefixPlan.Count > 0)
        {
            prefixActions = prefixPlan.Select(static action =>
                $"{action.Kind}:{action.CardId}:turn{action.Turn}").ToArray();
        }
        else if (prefixActions is { Count: > 0 })
        {
            int prefixTurn = 1;
            foreach (string action in prefixActions)
            {
                if (action == "EndTurn")
                {
                    prefixPlan.Add(new PlanAction(PlanActionKind.EndTurn, Turn: prefixTurn));
                    prefixTurn++;
                    continue;
                }
                // PlayCard:<cardId>:<occurrence>[:<targetCombatId>] on the current prefix turn.
                string[] parts = action.Split(':');
                if (parts.Length is < 3 or > 4 || parts[0] != "PlayCard")
                    throw new InvalidOperationException($"FlashPrefixActions 不支持 {action}。");
                uint? target = parts.Length == 4 ? uint.Parse(parts[3]) : null;
                prefixPlan.Add(new PlanAction(PlanActionKind.PlayCard, Turn: prefixTurn, CardId: parts[1],
                    CardOccurrence: int.Parse(parts[2]), TargetCombatId: target));
            }
        }
        if (policy.IncludeTurnSetup && prefixPlan.Count > 0)
        {
            summary["admitted"] = false;
            summary["rejection"] = FlashRejection.UnsupportedRequest.ToString();
            summary["detail"] = "回合准备根暂不支持固定前缀差分。";
            report.Add("REJECTED UnsupportedRequest: 回合准备根暂不支持固定前缀差分。");
            File.WriteAllLines(Path.Combine(outputDirectory, "flash-diff.txt"), report);
            return summary;
        }
        CombatBeamSolver prefixSolver = new(root, names, damage, policy, searchProfile: policy.Profile,
            fixedPrefixActions: prefixPlan);
        CombatBeamSolver solver = prefixPlan.Count == 0
            ? prefixSolver
            : new CombatBeamSolver(root, names, damage, policy, searchProfile: policy.Profile);
        Player player = root.PlayerIdentity;
        // The request root stays the baseline for values the existing engine freezes on it
        // (SimulatedCombatState._rootPowerAmounts), exactly as FlashRouteMaterializer compiles rules.
        SimulationSnapshot requestRootSnapshot;
        List<TurnSetupRootCandidate> searchRoots = [];
        if (policy.IncludeTurnSetup)
        {
            requestRootSnapshot = solver.CaptureRootSnapshotForFlash();
            IReadOnlyList<TurnSetupRootCandidate> candidates = solver.CaptureTurnSetupRootsForFlash();
            searchRoots.AddRange(candidates);
            solver.SetFlashRankBaselineForTurnSetup();
        }
        else
        {
            requestRootSnapshot = solver.ReplayForFlash([], null);
            SimulationSnapshot searchRootSnapshot = requestRootSnapshot;
            if (prefixPlan.Count > 0)
            {
                SimulationSnapshot prefixRoot = prefixSolver.ReplayForFlash([], null);
                searchRootSnapshot = prefixSolver.ReplayFixedPrefixForFlash(prefixRoot)
                    ?? throw new InvalidOperationException("FlashPrefixActions 无法在旧引擎回放。");
                summary["prefixActions"] = prefixActions;
            }
            searchRoots.Add(new TurnSetupRootCandidate([], searchRootSnapshot));
            solver.SetFlashRankBaseline(searchRootSnapshot);
        }
        if (searchRoots.Count == 0)
        {
            summary["admitted"] = false;
            summary["rejection"] = FlashRejection.RootPhase.ToString();
            summary["detail"] = "回合准备阶段没有生成可搜索根。";
            report.Add("REJECTED RootPhase: 回合准备阶段没有生成可搜索根。");
            File.WriteAllLines(Path.Combine(outputDirectory, "flash-diff.txt"), report);
            return summary;
        }
        summary["turnSetupRoots"] = searchRoots.Count;

        FlashRules rules;
        FlashBattle[] flashRoots = new FlashBattle[searchRoots.Count];
        try
        {
            rules = policy.IncludeTurnSetup
                ? FlashRulesCompiler.Compile(searchRoots[0].Snapshot.Simulator, player,
                    requestRootSnapshot.Simulator,
                    contentSimulator: requestRootSnapshot.Simulator,
                    lostHpSimulator: requestRootSnapshot.Simulator)
                : FlashRulesCompiler.Compile(searchRoots[0].Snapshot.Simulator, player,
                    requestRootSnapshot.Simulator);
            for (int index = 0; index < searchRoots.Count; index++)
            {
                flashRoots[index] = FlashStateImporter.Import(searchRoots[index].Snapshot, player, rules,
                    searchRoots[index].Snapshot.PotionUseCount);
            }
        }
        catch (FlashImportException ex)
        {
            summary["admitted"] = false;
            summary["rejection"] = ex.Reason.ToString();
            summary["detail"] = ex.Detail;
            report.Add($"REJECTED {ex.Reason}: {ex.Detail}");
            report.Add(MissingContent(requestRootSnapshot.Simulator, player));
            File.WriteAllLines(Path.Combine(outputDirectory, "flash-diff.txt"), report);
            return summary;
        }
        summary["admitted"] = true;
        report.Add(policy.IncludeTurnSetup
            ? $"ROOT turn-setup choices={searchRoots.Count}"
            : "ROOT");
        report.AddRange(FlashBattleDump.Lines(flashRoots[0]).Select(line => "  " + line));
        FlashScorer? scorer = null;
        try
        {
            scorer = FlashScoreBridge.Create(root, policy, policy.Profile, requestRootSnapshot,
                searchRoots[0].Snapshot, player, rules, flashRoots[0], rootSetup: policy.IncludeTurnSetup);
        }
        catch (FlashImportException ex)
        {
            report.Add($"SCORE REJECTED {ex.Reason}: {ex.Detail}");
        }
        FlashBoundFeatures? bound = null;
        if (scorer != null)
            bound = FlashBoundBridge.Create(root, policy, requestRootSnapshot, player, rules, flashRoots[0],
                solver.CapturePruningRequestForFlash());
        bool checkRank = policy.Profile.ContinuousThreatRanking || policy.Profile.ContextualRanking != null;
        ScoreCheck scores = new(scorer, bound, solver, report, checkRank);
        TraitCheck traits = new(player, rules, report);
        FlashActionSetCheck actionSet = new(root, names, damage, policy, player, rules, report);

        FlashAction[] actions = new FlashAction[FlashKernel.MaxLegalActions];
        int oneStepChecked = 0, oneStepFailed = 0, choiceActions = 0, choiceCapped = 0, choiceDeadEnds = 0, unsupported = 0;
        int walkTransitions = 0, walkFailed = 0, terminals = 0;
        Dictionary<string, int> endings = new(StringComparer.Ordinal);
        for (int rootIndex = 0; rootIndex < searchRoots.Count; rootIndex++)
        {
            SimulationSnapshot rootSnapshot = searchRoots[rootIndex].Snapshot;
            FlashBattle flashRoot = flashRoots[rootIndex];
            string rootLabel = policy.IncludeTurnSetup
                ? $"root[{rootIndex}]{DescribeChoices(rootIndex, searchRoots)}"
                : "root";
            scores.Check(rootSnapshot, flashRoot, prefixPlan.Count, rootLabel);
            actionSet.Check(rootSnapshot, flashRoot, $"{rootLabel} root", isRoot: true);
            if (policy.IncludeTurnSetup)
                report.Add($"ROOT[{rootIndex}] choices={DescribeChoiceVector(searchRoots[rootIndex].Choices)}");

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
                        report.Add($"UNSUPPORTED K1 {rootLabel} {actions[index]}: {ex.Message}");
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
                    Outcome outcome = Compare(solver, player, rules, rootSnapshot,
                        rootSnapshot.PotionUseCount, completion);
                    oneStepChecked++;
                    if (outcome.Diff.Count > 0)
                    {
                        oneStepFailed++;
                        report.Add($"K1 MISMATCH {rootLabel} {completion.Action} choices={Selections(completion.Action)}");
                        report.AddRange(outcome.Diff.Select(line => "  " + line));
                    }
                    else
                    {
                        actionSet.Check(outcome.OldChild!, outcome.FlashChild,
                            $"{rootLabel} K1 {completion.Action}", isRoot: false);
                        traits.Check(rootSnapshot, outcome.OldChild!, flashRoot, outcome.FlashChild,
                            completion.Action, $"K1 {rootLabel} {completion.Action}", completion.HistoryRisk);
                        scores.Check(outcome.OldChild!, outcome.FlashChild, 1,
                            $"K1 {rootLabel} {completion.Action} choices={Selections(completion.Action)}");
                    }
                }
            }

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
                    actionSet.Check(snapshot, battle, $"{rootLabel} K2 seed={seed} step={step}",
                        isRoot: false);
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
                            report.Add($"UNSUPPORTED K2 {rootLabel} seed={seed} step={step}: {ex.Message}");
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
                    Outcome outcome = Compare(solver, player, rules, snapshot,
                        rootSnapshot.PotionUseCount, completion);
                    walkTransitions++;
                    if (outcome.Diff.Count > 0)
                    {
                        walkFailed++;
                        report.Add($"K2 MISMATCH {rootLabel} seed={seed} step={step} path={string.Join(" > ", path)}");
                        report.AddRange(outcome.Diff.Select(line => "  " + line));
                        if (walkFailed <= 3)
                        {
                            report.Add("  parent (flash):");
                            report.AddRange(FlashBattleDump.Lines(battle).Select(line => "    " + line));
                        }
                        break;
                    }
                    traits.Check(snapshot, outcome.OldChild!, battle, outcome.FlashChild,
                        completion.Action, $"K2 {rootLabel} seed={seed} step={step}", completion.HistoryRisk);
                    scores.Check(outcome.OldChild!, outcome.FlashChild, 1,
                        $"K2 {rootLabel} seed={seed} step={step} path={string.Join(" > ", path)}");
                    snapshot = outcome.OldChild!;
                    battle = outcome.FlashChild;
                }
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
        summary["actionSetChecked"] = actionSet.Checked;
        summary["actionSetFailed"] = actionSet.Failed;
        summary["actionSetFirst"] = actionSet.FirstFailure;
        summary["duplicatePlayableChecked"] = actionSet.DuplicatePlayableChecked;
        summary["duplicatePlayableFailed"] = actionSet.DuplicatePlayableFailed;
        summary["duplicatePlayableFirst"] = actionSet.FirstDuplicatePlayableFailure;
        summary["boundFailedFields"] = scores.BoundFailedFields;
        summary["rankChecked"] = scores.RankChecked;
        summary["noveltyFailed"] = scores.NoveltyFailed;
        summary["rankFailed"] = scores.RankFailed;
        summary["traitChecked"] = traits.Checked;
        summary["traitFailed"] = traits.Failed;
        summary["pocketwatchChecked"] = traits.PocketwatchChecked;
        summary["pocketwatchFailed"] = traits.PocketwatchFailed;
        summary["declinedChecked"] = traits.DeclinedChecked;
        summary["declinedFailed"] = traits.DeclinedFailed;
        report.Insert(0, $"SUMMARY oneStep={oneStepChecked} failed={oneStepFailed} walkTransitions={walkTransitions} "
            + $"walkFailed={walkFailed} terminals={terminals} choiceActions={choiceActions} choiceCapped={choiceCapped} "
            + $"choiceDeadEnds={choiceDeadEnds} endings={string.Join(",", endings.Select(item => $"{item.Key}:{item.Value}"))} "
            + $"score={(scorer == null ? "rejected" : "checked")} scoreChecked={scores.Checked} scoreFailed={scores.Failed} "
            + $"scoreSkippedBoundary={scores.SkippedBoundary} "
            + (scores.BoundStatus == "checked"
                ? $"bound=checked boundChecked={scores.BoundChecked} boundFailed={scores.BoundFailed}"
                : $"bound=disabled({scores.BoundStatus})")
            + (scores.RankChecked == 0 ? "" : $" rankChecked={scores.RankChecked} rankFailed={scores.RankFailed}")
            + (scores.NoveltyFailed == 0 ? "" : $" noveltyFailed={scores.NoveltyFailed}")
            + $" traitChecked={traits.Checked} traitFailed={traits.Failed}"
            + $" pocketwatchChecked={traits.PocketwatchChecked} pocketwatchFailed={traits.PocketwatchFailed}"
            + $" declinedChecked={traits.DeclinedChecked} declinedFailed={traits.DeclinedFailed}"
            + $" pureChecked={traits.PureChecked} pureRefMismatch={traits.PureReferenceMismatch}"
            + (policy.IncludeTurnSetup ? $" turnSetupRoots={searchRoots.Count}" : "")
            + $" actionSetChecked={actionSet.Checked} actionSetFailed={actionSet.Failed}"
            + (actionSet.FirstFailure is { } firstActionSet ? $" actionSetFirst={firstActionSet}" : "")
            + $" duplicatePlayableChecked={actionSet.DuplicatePlayableChecked}"
            + $" duplicatePlayableFailed={actionSet.DuplicatePlayableFailed}"
            + (actionSet.FirstDuplicatePlayableFailure is { } firstDuplicate
                ? $" duplicatePlayableFirst={firstDuplicate}" : "")
            + $" unsupported={unsupported}");
        if (scores.FailedFields.Count > 0)
            report.Insert(1, "K4 FIELDS " + string.Join(",", scores.FailedFields
                .OrderByDescending(item => item.Value).Select(item => $"{item.Key}:{item.Value}")));
        if (scores.BoundFailedFields.Count > 0)
            report.Insert(1, "K5 FIELDS " + string.Join(",", scores.BoundFailedFields
                .OrderByDescending(item => item.Value).Select(item => $"{item.Key}:{item.Value}")));
        File.WriteAllLines(Path.Combine(outputDirectory, "flash-diff.txt"), report);
        return summary;
    }

    private static string DescribeChoiceVector(IReadOnlyList<PlanCardChoice> choices)
    {
        if (choices.Count == 0)
            return "-";
        return string.Join(";", choices.Select(choice =>
            $"{choice.SourceId}/{choice.Effect}/{choice.SourcePile}/"
            + string.Join(",", choice.Cards.Select(card => $"{card.CardId}+{card.UpgradeLevel}"))));
    }

    private static string DescribeChoices(int rootIndex, IReadOnlyList<TurnSetupRootCandidate> roots)
        => roots[rootIndex].Choices.Count == 0 ? "" : $" {DescribeChoiceVector(roots[rootIndex].Choices)}";

    /// <summary>
    /// K4 route-trait extension: the cumulative <see cref="SearchRouteTraits"/> delta of one transition
    /// must equal the existing engine's <c>Classify*Traits</c> for the same before/after snapshots. The
    /// The probe feeds the reference history-derived <c>pure</c> to both sides, so the classifier's
    /// feature terms are compared exactly. Production approximates the input from the card type (a
    /// non-attack card play is treated as not pure); that approximation is the residual not covered by
    /// this check.
    /// </summary>
    private sealed class TraitCheck(Player player, FlashRules rules, List<string> report)
    {
        public int Checked { get; private set; }

        public int Failed { get; private set; }

        public int PocketwatchChecked { get; private set; }

        public int PocketwatchFailed { get; private set; }

        public int DeclinedChecked { get; private set; }

        public int DeclinedFailed { get; private set; }

        public int PureChecked { get; private set; }

        public int PureReferenceMismatch { get; private set; }

        public void Check(SimulationSnapshot before, SimulationSnapshot after, in FlashBattle beforeFlash,
            in FlashBattle afterFlash, FlashAction action, string label, bool historyRisk = false)
        {
            if (after.BoundaryReason is not (SearchBoundaryReason.None or SearchBoundaryReason.EventDefeat)
                || !before.HasSimulator)
            {
                return;
            }
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)after.Simulator;
            bool pure = true;
            foreach (CombatPredictionHistoryEntry entry in
                     simulator.History.EntriesFrom(before.HistoryEntryCount))
            {
                if (SearchProbeHelpers.IsPureHistoryEntry(entry))
                    continue;
                pure = false;
                break;
            }
            SimulatedCombatState beforeCombat = (SimulatedCombatState)
                ((CombatPredictionSimulator)before.Simulator).State.CombatState;
            bool declinedExtraTurn = beforeCombat.RelicsOf(player)
                .OfType<MegaCrit.Sts2.Core.Models.Relics.PaelsEye>()
                .Any(relic => !relic.IsMelted && beforeCombat.IsPaelsEyeUnused(relic));
            FlashScoreFeatures beforeFeatures = FlashScoreProjection.FromSnapshot(before);
            FlashScoreFeatures afterFeatures = FlashScoreProjection.FromSnapshot(after);
            bool flashPure = FlashBeamSolver.IsPureTransitionForTesting(
                beforeFlash, afterFlash, action, rules, historyRisk);
            SearchRouteTraits reference = action.Kind switch
            {
                // The history-derived `pure` includes state-dependent MethodNotMirrored risk that the
                // Flash kernel only observes where its content annotates it; the classifier check uses
                // the production pure input for both sides, and the raw difference is reported as a
                // diagnostic (pureRefMismatch) instead of a field mismatch.
                FlashActionKind.PlayCard => SearchProbeHelpers.ClassifyCardTraits(
                    SearchRouteTraits.None, before, after, flashPure, declinedExtraTurn),
                FlashActionKind.UsePotion => SearchProbeHelpers.ClassifyPotionTraits(
                    SearchRouteTraits.None, before, after),
                _ => SearchProbeHelpers.ClassifyRoundTransitionTraits(
                    SearchRouteTraits.None, before, after),
            };
            if (action.Kind == FlashActionKind.PlayCard)
            {
                PureChecked++;
                if (flashPure != pure)
                {
                    PureReferenceMismatch++;
                    if (PureReferenceMismatch <= 5)
                    {
                        string card = "-";
                        if (before.HasSimulator)
                        {
                            SimPlayerCombatState handState =
                                ((CombatPredictionSimulator)before.Simulator).State
                                    .GetPlayerCombatState(player);
                            if (action.HandIndex < handState.Hand.Cards.Count)
                                card = handState.Hand.Cards[action.HandIndex].Preview.Id.Entry;
                        }
                        report.Add($"K4 PURE-REF DIFF {label} action={action} card={card} "
                            + $"reference={pure} flash={flashPure}");
                        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FLASH_DEBUG") == "1")
                        {
                            List<string> nonPure = [];
                            foreach (CombatPredictionHistoryEntry entry in
                                     simulator.History.EntriesFrom(before.HistoryEntryCount))
                            {
                                if (SearchProbeHelpers.IsPureHistoryEntry(entry))
                                    continue;
                                string detail = entry.GetType().Name;
                                if (entry is CombatPredictionRiskEntry risk)
                                {
                                    detail += ":" + risk.Reason;
                                    if (risk.Trace != null)
                                        detail += "#" + risk.Trace.Invocation.Method?.Name
                                            + "@" + string.Join("/", risk.Trace.Ancestors()
                                            .Select(frame => frame.Source.GetType().Name).Take(4));
                                }
                                nonPure.Add(detail);
                            }
                            report.Add("  NONPURE " + string.Join(",", nonPure));
                            List<string> flashTags = [];
                            if (action.HandIndex < beforeFlash.HandCount)
                            {
                                FlashCard playedCard = beforeFlash.Cards[action.HandIndex];
                                if (rules.Card(playedCard.Id, playedCard.Upgrade).PlaysRecordRisk)
                                    flashTags.Add("card-risk");
                            }
                            if (action.ChoiceCount > 0) flashTags.Add("choice");
                            if (afterFlash.Counter(FlashCounter.CardsDrawn)
                                != beforeFlash.Counter(FlashCounter.CardsDrawn)) flashTags.Add("drawn");
                            if (afterFlash.Counter(FlashCounter.CardsGeneratedThisCombat)
                                != beforeFlash.Counter(FlashCounter.CardsGeneratedThisCombat))
                                flashTags.Add("generated-counter");
                            if (afterFlash.CardCount > beforeFlash.CardCount) flashTags.Add("card-count");
                            if (afterFlash.Counter(FlashCounter.OrbChannels)
                                != beforeFlash.Counter(FlashCounter.OrbChannels)) flashTags.Add("orb");
                            if (afterFlash.DrawCount < beforeFlash.DrawCount) flashTags.Add("draw-count");
                            for (int i = 0; i < afterFlash.PotionSlotCount; i++)
                            {
                                if (afterFlash.Potions[i] != 0
                                    && afterFlash.Potions[i] != beforeFlash.Potions[i])
                                    flashTags.Add("potion");
                            }
                            report.Add("  FLASH TAGS " + string.Join(",", flashTags));
                        }
                    }
                }
            }
            SearchRouteTraits flash = FlashBeamSolver.ClassifyRouteTraitsForTesting(
                beforeFeatures, afterFeatures, action, declinedExtraTurn, !flashPure);
            Checked++;
            if (reference != flash)
            {
                Failed++;
                if (Failed <= 5)
                {
                    report.Add($"K4 TRAIT MISMATCH {label} action={action}");
                    report.Add($"  reference={reference} flash={flash} pure={pure} declined={declinedExtraTurn}");
                }
            }
            // The route-trait inputs that are not part of the score snapshot: the K4 projection reads
            // the existing snapshot's pocketwatch fields and the PaelsEye state, so the Flash route
            // helper must produce the same values on the same state.
            PocketwatchChecked++;
            FlashBeamSolver.PocketwatchState(afterFlash, rules,
                out int flashThreshold, out int flashPlayed);
            if (flashThreshold != after.PocketwatchCardThreshold
                || flashPlayed != after.PocketwatchCardsPlayedThisTurn)
            {
                PocketwatchFailed++;
                if (PocketwatchFailed <= 5)
                {
                    report.Add($"K4 POCKETWATCH MISMATCH {label} action={action}");
                    report.Add($"  old={after.PocketwatchCardThreshold}/{after.PocketwatchCardsPlayedThisTurn} "
                        + $"flash={flashThreshold}/{flashPlayed}");
                }
            }
            DeclinedChecked++;
            bool oldDeclined = beforeCombat.RelicsOf(player)
                .OfType<MegaCrit.Sts2.Core.Models.Relics.PaelsEye>()
                .Any(relic => !relic.IsMelted && beforeCombat.IsPaelsEyeUnused(relic));
            bool flashDeclined = FlashBeamSolver.DeclinedExtraTurn(beforeFlash);
            if (oldDeclined != flashDeclined)
            {
                DeclinedFailed++;
                if (DeclinedFailed <= 5)
                {
                    report.Add($"K4 DECLINED MISMATCH {label} action={action}");
                    report.Add($"  old={oldDeclined} flash={flashDeclined}");
                }
            }
        }
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
        FlashScorer? scorer, FlashBoundFeatures? bound, CombatBeamSolver solver, List<string> report,
        bool checkRank)
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

        /// <summary>Projected states whose novelty fact multiset differed.</summary>
        public int NoveltyFailed { get; private set; }

        /// <summary>Intermediate Beam rank comparisons (only when the profile carries a rank modifier).</summary>
        public int RankChecked { get; private set; }

        public int RankFailed { get; private set; }

        public string BoundStatus => bound == null ? "score_rejected" : bound.Enabled ? "checked" : bound.DisabledReason;

        public void Check(SimulationSnapshot old, in FlashBattle flash, int actionCount, string label)
        {
            // EventDefeat is a compared terminal state: the existing evaluation still produces its
            // threat projection as (current HP, no death saves), and the Flash scorer mirrors that.
            // Other boundaries keep the pre-existing skip (their snapshots also skip projections).
            if (old.BoundaryReason is not (SearchBoundaryReason.None or SearchBoundaryReason.EventDefeat))
            {
                SkippedBoundary++;
                return;
            }
            FlashScoreFeatures? flashFeatures = scorer?.Features(flash, actionCount);
            CheckScore(old, flash, flashFeatures, label);
            CheckBound(old, flash, flashFeatures, label);
            if (checkRank && flashFeatures is { } features)
                CheckRank(old, flash, features, actionCount, label);
            if (flashFeatures is { } noveltyFeatures)
                CheckNoveltyFacts(old, flash, noveltyFeatures, label);
        }

        /// <summary>
        /// K4 novelty facts: the fact multiset read by the novelty search must be the same on both
        /// sides, including the pile card identities, top draw cards and effective powers.
        /// </summary>
        private void CheckNoveltyFacts(SimulationSnapshot old, in FlashBattle flash,
            in FlashScoreFeatures features, string label)
        {
            List<BfwsFact> oldFacts = solver.CaptureNoveltyFactsForFlash(old);
            List<BfwsFact> flashFacts = [];
            FlashNoveltyFacts.Capture(flash, features, flashFacts);
            List<string> diff = FlashNoveltyFacts.Diff(oldFacts, flashFacts);
            if (diff.Count == 0)
                return;
            NoveltyFailed++;
            Failed++;
            FailedFields["novelty-fact"] = FailedFields.GetValueOrDefault("novelty-fact") + 1;
            if (NoveltyFailed <= ReportedMismatches)
            {
                report.Add($"K4 NOVELTY MISMATCH {label}");
                report.AddRange(diff.Select(line => "  " + line));
            }
        }

        private void CheckRank(SimulationSnapshot old, in FlashBattle flash,
            in FlashScoreFeatures features, int actionCount, string label)
        {
            RankChecked++;
            bool terminal = !FlashKernel.IsInProgress(flash);
            for (int actionKind = 0; actionKind < 2; actionKind++)
            {
                bool isEndTurn = actionKind == 0;
                double flashRank = scorer!.BeamRankScore(flash, features, terminal, isEndTurn);
                double oldRank = solver.BeamRankForFlash(
                    old, actionCount, old.PotionUseCount, terminal, isEndTurn);
                if (BitConverter.DoubleToInt64Bits(flashRank)
                    == BitConverter.DoubleToInt64Bits(oldRank))
                {
                    continue;
                }
                RankFailed++;
                if (RankFailed <= ReportedMismatches)
                {
                    report.Add($"K4 RANK MISMATCH {label} endTurn={isEndTurn}");
                    report.Add($"  rank old={oldRank:R} flash={flashRank:R} "
                        + $"delta={flashRank - oldRank:R} "
                        + $"adj old={solver.ContextualAdjustmentForFlash(old, terminal):R} "
                        + $"flash={scorer.ContextualAdjustmentForTesting(flash, features, terminal):R}");
                }
                return;
            }
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
        FlashBattle Child, bool DeadEnd, bool HistoryRisk);

    /// <summary>Every choice completion of <paramref name="action"/>; returns true when the cap truncated it.</summary>
    private static bool Complete(FlashRules rules, in FlashBattle battle, FlashAction action,
        List<(FlashChoiceRequest Request, FlashBattle Partial)> requests, List<Completion> output)
    {
        FlashBattle child = battle;
        if (FlashKernel.Step(ref child, action, rules, out FlashChoiceRequest request, out bool historyRisk)
            == FlashStepResult.Completed)
        {
            output.Add(new Completion(action, [.. requests], child, false, historyRisk));
            return false;
        }
        Span<ulong> selections = stackalloc ulong[OneStepChoiceCap];
        int count = FlashChoiceEnumerator.Enumerate(request, selections);
        if (count == 0)
        {
            output.Add(new Completion(action, [.. requests], default, true, false));
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
            if (FlashKernel.Step(ref child, action, rules, out FlashChoiceRequest request, out bool historyRisk)
                == FlashStepResult.Completed)
                return new Completion(action, requests, child, false, historyRisk);
            int count = FlashChoiceEnumerator.Enumerate(request, selections);
            if (count == 0)
                return new Completion(action, requests, default, true, false);
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
            oldChild = FlashChoiceTranslator.Replay(solver.ReplayCoreForTesting, parent, player, completion.Action, out _,
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
