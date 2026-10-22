using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Flash.Bridge;
using CombatSolver.Flash.Kernel;
using CombatSolver.Flash.Search;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace OfflineSearchHarness;

/// <summary>
/// K6 retention-set diff (<c>OFFLINE_HARNESS_RETENTION_DIFF=1</c>). Runs the existing
/// <see cref="CombatBeamSolver"/> and <see cref="FlashRouteMaterializer"/> on the same request with the
/// same purpose, width, profile flags, node/time budget, incumbent and potion policy, captures the
/// ordered retained set at every boundary, aligns the boundaries, and reports the first divergence.
/// The tool never changes selection; production leaves both observers null.
/// </summary>
internal static class RetentionDiffProbe
{
    private const int DefaultTop = 8;

    private static readonly JsonSerializerOptions Json = new(UnattendedTestFiles.JsonOptions)
    {
        WriteIndented = false,
    };

    public static Dictionary<string, object?> Run(CombatState combat, HarnessOptions options, MainLoopContext loop)
    {
        int top = ReadInt("OFFLINE_HARNESS_RETENTION_TOP", DefaultTop, minimum: 1);
        string label = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_RETENTION_LABEL")
            ?? options.Label;
        SolverPotionPolicy? potionPolicyOverride = ReadPotionPolicy();
        DirectSearchPurpose purpose = ReadPurpose();

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings, combat, includeTurnSetup: false, theftPolicy: SolverController.ResolveTheftPolicy(combat));
        SolverSearchProfile profile = MemberProfile(policy.Profile, options);
        policy = policy with { Profile = profile, MaxDegreeOfParallelism = 1 };

        string outputPath = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_RETENTION_OUT")
            ?? Path.Combine(options.OutputDirectory, "retention-diff.json");
        Dictionary<string, object?> summary = new()
        {
            ["label"] = label,
            ["purpose"] = purpose.ToString(),
            ["potionPolicyOverride"] = potionPolicyOverride?.ToString(),
            ["member"] = MemberIdentity(profile),
            ["profile"] = new
            {
                profile.BeamWidth,
                profile.MaxExpandedNodes,
                profile.SoftTimeBudgetMilliseconds,
                profile.SecondRankBand,
                profile.BaseScoreOnly,
                profile.AggressivePowerCommitment,
                profile.MaxCardBranchesPerNode,
                profile.MaxPileChoiceBranchesPerAction,
                profile.MaxHandChoiceBranchesPerAction,
                BeamWeightPerturbation = profile.BeamWeightPerturbation?.ToString(),
            },
            ["top"] = top,
        };

        SideCapture oldSide = new("old");
        SideCapture flashSide = new("flash");
        SolverResult? oldResult = null;
        FlashSolveOutcome? flashOutcome = null;
        string? error = null;
        int unprojectable = 0;

        FlashRules rules;
        Player player = root.PlayerIdentity;
        SimulationSnapshot requestRoot;
        OpeningProbe projectionProbe = new(root, names, damage, policy, CancellationToken.None,
            searchProfile: profile, potionPolicyOverride: potionPolicyOverride);
        try
        {
            requestRoot = projectionProbe.ReplayForFlash([], null);
            rules = FlashRulesCompiler.Compile(requestRoot.Simulator, player, requestRoot.Simulator);
        }
        catch (Exception ex) when (ex is FlashImportException or InvalidOperationException)
        {
            summary["status"] = "error";
            summary["error"] = $"根无法投影：{ex.Message}";
            Write(outputPath, summary);
            return summary;
        }

        int skippedPotionUses = requestRoot.PotionUseCount;
        Dictionary<string, int> handCounts = new(StringComparer.Ordinal);
        SimPlayerCombatState rootHand = ((CombatPredictionSimulator)requestRoot.Simulator)
            .State.GetPlayerCombatState(player);
        foreach (PredictedCard card in rootHand.Hand.Cards)
            handCounts[card.Preview.Id.Entry] = handCounts.GetValueOrDefault(card.Preview.Id.Entry) + 1;
        summary["rootHandCounts"] = handCounts;
        SearchRetentionObserver retention = new(
            (key, snapshot) => ProjectOld(oldSide, key, snapshot, player, rules, skippedPotionUses),
            value => oldSide.AddBoundary(value),
            value => oldSide.AddPoolState(value),
            value => oldSide.AddSelected(value),
            value => oldSide.AddGeneration(value));
        SearchPolicySnapshot observedPolicy = policy with
        {
            Diagnostics = new SearchDiagnosticsSink(static _ => { }, static _ => { }, retentionLabels: true,
                retention: retention),
        };

        Stopwatch oldWatch = Stopwatch.StartNew();
        try
        {
            Task<SolverResult> task = Task.Run(() => new CombatBeamSolver(
                root, names, damage, observedPolicy, searchProfile: profile,
                potionPolicyOverride: potionPolicyOverride,
                directSearchPurpose: purpose).Solve());
            loop.RunUntilCompleted(task, TimeSpan.FromSeconds(660), "RetentionDiff existing");
            oldResult = task.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = $"existing: {ex.GetType().Name}: {ex.Message}";
        }
        oldWatch.Stop();

        Stopwatch flashWatch = Stopwatch.StartNew();
        if (error == null)
        {
            FlashCapture capture = new(flashSide);
            try
            {
                Task<FlashSolveOutcome> task = Task.Run(() => FlashRouteMaterializer.Solve(
                    root, names, damage, policy, profile, CancellationToken.None,
                    potionPolicyOverride: potionPolicyOverride,
                    purpose: purpose,
                    retentionObserver: capture));
                loop.RunUntilCompleted(task, TimeSpan.FromSeconds(660), "RetentionDiff flash");
                flashOutcome = task.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                error = $"flash: {ex.GetType().Name}: {ex.Message}";
            }
        }
        flashWatch.Stop();
        requestRoot.ReleaseSimulator();

        summary["old"] = SideSummary(oldSide, oldResult, oldWatch.Elapsed.TotalMilliseconds);
        summary["flash"] = FlashSummary(flashSide, flashOutcome, flashWatch.Elapsed.TotalMilliseconds);
        summary["unprojectable"] = oldSide.Unprojectable + flashSide.Unprojectable;
        unprojectable = oldSide.Unprojectable + flashSide.Unprojectable;

        if (error != null)
        {
            summary["status"] = "error";
            summary["error"] = error;
            summary["comparison"] = new { status = "error", error };
        }
        else
        {
            ComparisonResult comparison = Compare(oldSide, flashSide, oldResult, flashOutcome, profile, top);
            summary["status"] = comparison.Status;
            summary["comparison"] = comparison.View;
        }
        Write(outputPath, summary);
        return summary;
    }

    private readonly record struct ComparisonResult(string Status, object View);

    private static ComparisonResult Compare(
        SideCapture oldSide, SideCapture flashSide, SolverResult? oldResult,
        FlashSolveOutcome? flashOutcome, SolverSearchProfile profile, int top)
    {
        BoundaryView[] ordered = OrderedBoundaries(oldSide, flashSide);
        List<object> trace = [];
        object? first = null;
        int orderOnly = 0;
        for (int index = 0; index < ordered.Length; index++)
        {
            BoundaryView view = ordered[index];
            string key = view.Key;
            int oldCount = view.Old?.SelectedKeys.Count ?? 0;
            int flashCount = view.Flash?.SelectedKeys.Count ?? 0;
            bool sameSet = view.Old != null && view.Flash != null
                && SameMultiset(view.Old.SelectedKeys, view.Flash.SelectedKeys);
            bool sameOrder = sameSet && view.Old!.SelectedKeys.SequenceEqual(view.Flash!.SelectedKeys);
            Dictionary<string, object?> traceItem = new()
            {
                ["index"] = index,
                ["key"] = key,
                ["kind"] = view.Kind.ToString(),
                ["turn"] = view.TurnLayer,
                ["depth"] = view.PlayDepth,
                ["oldInput"] = view.Old?.Meta.InputCount,
                ["flashInput"] = view.Flash?.Meta.InputCount,
                ["oldSelected"] = oldCount,
                ["flashSelected"] = flashCount,
                ["sameSet"] = sameSet,
                ["sameOrder"] = sameOrder,
            };
            if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_RETENTION_ORDER") == "1")
            {
                traceItem["oldOrder"] = view.Old?.Selected
                    .Select(key => view.Old.SelectedActionByKey.GetValueOrDefault(key, "")).ToArray();
                traceItem["flashOrder"] = view.Flash?.Selected
                    .Select(key => view.Flash.SelectedActionByKey.GetValueOrDefault(key, "")).ToArray();
                traceItem["oldRequired"] = view.Old?.Pool
                    .Where(entry => entry.Required)
                    .Select(entry => $"{entry.Key}:{entry.Label ?? "-"}:{entry.LastAction}:{entry.Score:R}").ToArray();
                traceItem["flashRequired"] = view.Flash?.Pool
                    .Where(entry => entry.Required)
                    .Select(entry => $"{entry.Key}:{entry.Label ?? "-"}:{entry.LastAction}:{entry.Score:R}").ToArray();
            }
            trace.Add(traceItem);
            if (!sameSet && first == null)
            {
                first = DescribeFirstDivergence(index, view, oldSide, flashSide, oldResult,
                    flashOutcome, profile, top);
            }
            else if (sameSet && !sameOrder)
            {
                orderOnly++;
            }
        }

        string[] oldRoute = OldRoute(oldResult);
        string[] flashRoute = FlashRoute(flashOutcome);
        bool routeIdentical = oldRoute.SequenceEqual(flashRoute, StringComparer.Ordinal);
        if (first == null && !routeIdentical && ordered.All(item =>
                item.Old != null && item.Flash != null
                && SameMultiset(item.Old.SelectedKeys, item.Flash.SelectedKeys)))
        {
            first = new
            {
                category = "C",
                reason = "retained-sets-identical-final-route-differs",
                index = ordered.Length,
                kind = "Final",
                oldRoute,
                flashRoute,
            };
        }

        string status = first == null && routeIdentical ? "identical" : "divergent";
        object body = new
        {
            status,
            identical = first == null && routeIdentical,
            boundaryCount = ordered.Length,
            oldBoundaries = oldSide.Boundaries.Count,
            flashBoundaries = flashSide.Boundaries.Count,
            oldCommitted = oldSide.Boundaries.Sum(item => item.SelectedKeys.Count),
            flashCommitted = flashSide.Boundaries.Sum(item => item.SelectedKeys.Count),
            orderOnlyBoundaries = orderOnly,
            routeIdentical,
            oldRoute,
            flashRoute,
            firstDivergence = first,
            boundaryTrace = trace,
            errors = new
            {
                old = oldSide.ProjectionError,
                flash = flashSide.ProjectionError,
            },
        };
        return new ComparisonResult(status, body);
    }

    private static object DescribeFirstDivergence(
        int index, BoundaryView view, SideCapture oldSide, SideCapture flashSide,
        SolverResult? oldResult, FlashSolveOutcome? flashOutcome,
        SolverSearchProfile profile, int top)
    {
        List<SearchRetentionStateKey> oldOnly = view.Old == null
            ? []
            : Difference(view.Old.SelectedKeys, view.Flash?.SelectedKeys ?? []);
        List<SearchRetentionStateKey> flashOnly = view.Flash == null
            ? []
            : Difference(view.Flash.SelectedKeys, view.Old?.SelectedKeys ?? []);
        Dictionary<string, int> categories = new(StringComparer.Ordinal);
        foreach (SearchRetentionStateKey key in oldOnly)
        {
            (string category, _) = ClassifyState(key, oldSide, flashSide, view, fromOld: true);
            categories[category] = categories.GetValueOrDefault(category) + 1;
        }
        foreach (SearchRetentionStateKey key in flashOnly)
        {
            (string category, _) = ClassifyState(key, oldSide, flashSide, view, fromOld: false);
            categories[category] = categories.GetValueOrDefault(category) + 1;
        }
        List<object> oldDetails = [];
        foreach (SearchRetentionStateKey key in LimitByRank(oldOnly, view.Old, top))
            oldDetails.Add(DescribeState(key, oldSide, flashSide, view, fromOld: true));
        List<object> flashDetails = [];
        foreach (SearchRetentionStateKey key in LimitByRank(flashOnly, view.Flash, top))
            flashDetails.Add(DescribeState(key, oldSide, flashSide, view, fromOld: false));
        string primary = PrimaryCategory(categories);
        if (view.Old == null || view.Flash == null)
        {
            bool budget = BudgetSuspected(oldResult, flashOutcome, profile, oldSide, flashSide);
            primary = budget ? "F" : "G";
            categories[primary] = categories.GetValueOrDefault(primary) + 1;
        }

        return new
        {
            index,
            category = primary,
            categories,
            key = view.Key,
            kind = view.Kind.ToString(),
            turn = view.TurnLayer,
            depth = view.PlayDepth,
            oldInput = view.Old?.Meta.InputCount,
            flashInput = view.Flash?.Meta.InputCount,
            oldSelected = view.Old?.SelectedKeys.Count,
            flashSelected = view.Flash?.SelectedKeys.Count,
            oldOnlyCount = oldOnly.Count,
            flashOnlyCount = flashOnly.Count,
            oldOnly = oldDetails,
            flashOnly = flashDetails,
            missingBoundary = view.Old == null ? "old" : view.Flash == null ? "flash" : null,
            budgetSuspected = BudgetSuspected(oldResult, flashOutcome, profile, oldSide, flashSide),
        };
    }

    private static object DescribeState(
        SearchRetentionStateKey key, SideCapture oldSide, SideCapture flashSide,
        BoundaryView view, bool fromOld)
    {
        BoundaryRecord? missing = fromOld ? view.Flash : view.Old;
        SideCapture missingSide = fromOld ? flashSide : oldSide;
        SideCapture presentSide = fromOld ? oldSide : flashSide;
        BoundaryRecord? present = fromOld ? view.Old : view.Flash;
        PoolEntry? presentEntry = present?.Best(key);
        missingSide.Generated.TryGetValue(key, out GenerationInfo? missingGeneration);
        PoolEntry[] missingPool = missing?.PoolFor(key) ?? [];
        PoolEntry[] presentPool = present?.PoolFor(key) ?? [];

        SearchRetentionStateKey? otherKey = key;
        (string category, string reason) = ClassifyState(
            key, oldSide, flashSide, view, fromOld, presentEntry, missingGeneration, missingPool);

        PoolEntry? otherBest = missingPool.Length == 0 ? null
            : missingPool.OrderByDescending(item => item.Selected)
                .ThenBy(item => item.Rank < 0 ? int.MaxValue : item.Rank).First();
        return new
        {
            key = key.ToString(),
            category,
            reason,
            lastAction = presentEntry?.LastAction ?? present?.SelectedActionByKey.GetValueOrDefault(key, ""),
            presentRank = presentEntry?.Rank,
            presentScore = presentEntry?.Score,
            presentRequired = presentEntry?.Required,
            presentLabel = presentEntry?.Label,
            otherRank = otherBest?.Rank,
            otherScore = otherBest?.Score,
            otherRequired = otherBest?.Required,
            otherLabel = otherBest?.Label,
            otherStatus = missingGeneration?.LastStatus,
            presentSide = new
            {
                generated = presentSide.Generated.TryGetValue(key, out GenerationInfo? presentGeneration)
                    ? Status(presentGeneration)
                    : null,
                selectedOrder = present?.SelectedOrder(key),
                pool = presentPool.Take(4).Select(PoolEntryView).ToArray(),
            },
            otherSide = new
            {
                generated = missingGeneration == null ? null : Status(missingGeneration),
                selectedOrder = missing?.SelectedOrder(key),
                pool = missingPool.Take(4).Select(PoolEntryView).ToArray(),
            },
            otherKey,
        };
    }

    private static (string Category, string Reason) ClassifyState(
        SearchRetentionStateKey key, SideCapture oldSide, SideCapture flashSide,
        BoundaryView view, bool fromOld)
        => ClassifyState(key, oldSide, flashSide, view, fromOld, null, null, null);

    private static (string Category, string Reason) ClassifyState(
        SearchRetentionStateKey key, SideCapture oldSide, SideCapture flashSide,
        BoundaryView view, bool fromOld, PoolEntry? presentEntry, GenerationInfo? missingGeneration,
        PoolEntry[]? missingPool)
    {
        BoundaryRecord? missing = fromOld ? view.Flash : view.Old;
        SideCapture missingSide = fromOld ? flashSide : oldSide;
        presentEntry ??= (fromOld ? view.Old : view.Flash)?.Best(key);
        missingGeneration ??= missingSide.Generated.GetValueOrDefault(key);
        missingPool ??= missing?.PoolFor(key) ?? [];
        if (missingGeneration == null)
            return ("A", $"not-generated-on-{(fromOld ? "flash" : "old")}");
        if (missingGeneration.Rejected && !missingGeneration.Admitted)
            return ("B", $"rejected-on-{(fromOld ? "flash" : "old")}:{missingGeneration.LastStatus}");
        if (missingPool.Length == 0)
        {
            return ("C", $"generated-but-not-in-{(fromOld ? "flash" : "old")}-pool:"
                + $"{missingGeneration.LastStatus}");
        }
        PoolEntry other = missingPool.OrderBy(item => item.Rank < 0 ? int.MaxValue : item.Rank).First();
        double score = presentEntry?.Score ?? double.NaN;
        bool scoreDiffers = !double.IsNaN(score)
            && BitConverter.DoubleToInt64Bits(score) != BitConverter.DoubleToInt64Bits(other.Score);
        bool requiredDiffers = presentEntry is { } mine
            && (mine.Required != other.Required
                || !string.Equals(mine.Label ?? "", other.Label ?? "", StringComparison.Ordinal));
        if (scoreDiffers)
            return ("D", "score-differs");
        if (requiredDiffers || other.Required)
            return ("E", "required-representative-differs");
        return ("C", "same-score-rank-or-tie-order");
    }

    private static object PoolEntryView(PoolEntry entry) => new
    {
        score = entry.Score,
        rank = entry.Rank,
        required = entry.Required,
        label = entry.Label,
        selected = entry.Selected,
        lastAction = entry.LastAction,
        turn = entry.Turn,
        depth = entry.Depth,
    };

    private static object? Status(GenerationInfo info) => info == null ? null : new
    {
        admitted = info.Admitted,
        rejected = info.Rejected,
        status = info.LastStatus,
        score = info.Score,
        lastAction = info.LastAction,
        updates = info.Updates,
    };

    private static string PrimaryCategory(Dictionary<string, int> categories)
    {
        foreach (string category in new[] { "A", "B", "F", "D", "E", "C" })
        {
            if (categories.GetValueOrDefault(category) > 0)
                return category;
        }
        return "C";
    }

    private static bool BudgetSuspected(
        SolverResult? oldResult, FlashSolveOutcome? flashOutcome, SolverSearchProfile profile,
        SideCapture oldSide, SideCapture flashSide)
    {
        if (oldResult != null && oldResult.BoundaryReason == SearchBoundaryReason.NodeLimit)
            return true;
        if (flashOutcome?.Search is { } search && search.ExpandedNodes >= profile.MaxExpandedNodes)
            return true;
        if (oldResult != null && oldResult.ExpandedNodes >= profile.MaxExpandedNodes)
            return true;
        if (flashSide.Compactions > 0 || flashSide.CompactedStates > 0)
            return true;
        return oldSide.Boundaries.Count != flashSide.Boundaries.Count
            && (oldSide.Boundaries.Count == 0 || flashSide.Boundaries.Count == 0);
    }

    private static List<SearchRetentionStateKey> Difference(
        IReadOnlyList<SearchRetentionStateKey> left, IReadOnlyList<SearchRetentionStateKey> right)
    {
        Dictionary<SearchRetentionStateKey, int> counts = new();
        foreach (SearchRetentionStateKey key in right)
            counts[key] = counts.GetValueOrDefault(key) + 1;
        List<SearchRetentionStateKey> result = [];
        foreach (SearchRetentionStateKey key in left)
        {
            if (counts.TryGetValue(key, out int count) && count > 0)
            {
                counts[key] = count - 1;
                continue;
            }
            result.Add(key);
        }
        return result;
    }

    private static List<SearchRetentionStateKey> LimitByRank(
        List<SearchRetentionStateKey> keys, BoundaryRecord? boundary, int top)
        => keys
            .OrderBy(key => boundary?.Best(key)?.Rank is { } rank && rank >= 0 ? rank : int.MaxValue)
            .Take(top)
            .ToList();

    private static bool SameMultiset(
        IReadOnlyList<SearchRetentionStateKey> left, IReadOnlyList<SearchRetentionStateKey> right)
    {
        if (left.Count != right.Count)
            return false;
        Dictionary<SearchRetentionStateKey, int> counts = new();
        foreach (SearchRetentionStateKey key in left)
            counts[key] = counts.GetValueOrDefault(key) + 1;
        foreach (SearchRetentionStateKey key in right)
        {
            if (!counts.TryGetValue(key, out int count) || count == 0)
                return false;
            counts[key] = count - 1;
        }
        return true;
    }

    private static BoundaryView[] OrderedBoundaries(SideCapture oldSide, SideCapture flashSide)
    {
        Dictionary<string, (BoundaryRecord? Old, BoundaryRecord? Flash)> map = new(StringComparer.Ordinal);
        foreach (BoundaryRecord record in oldSide.Boundaries)
        {
            string key = record.Key;
            map[key] = (record, map.TryGetValue(key, out var existing) ? existing.Flash : null);
        }
        foreach (BoundaryRecord record in flashSide.Boundaries)
        {
            string key = record.Key;
            map[key] = (map.TryGetValue(key, out var existing) ? existing.Old : null, record);
        }
        return map
            .Select(pair => new BoundaryView(
                pair.Value.Old?.Meta.Kind ?? pair.Value.Flash!.Meta.Kind,
                pair.Value.Old?.Meta.TurnLayer ?? pair.Value.Flash!.Meta.TurnLayer,
                pair.Value.Old?.Meta.PlayDepth ?? pair.Value.Flash!.Meta.PlayDepth,
                pair.Key, pair.Value.Old, pair.Value.Flash))
            .OrderBy(view => view.TurnLayer)
            .ThenBy(view => view.Kind == SearchRetentionBoundaryKind.TurnBoundary ? int.MaxValue : view.PlayDepth)
            .ThenBy(view => view.Kind)
            .ToArray();
    }

    private static string[] OldRoute(SolverResult? result)
        => result?.BestNode?.Actions.Select(SearchProbeHelpers.PolicyActionToken).ToArray() ?? [];

    private static string[] FlashRoute(FlashSolveOutcome? outcome)
        => outcome?.Plan.Count > 0
            ? outcome.Plan.Select(SearchProbeHelpers.PolicyActionToken).ToArray()
            : [];

    private static SearchRetentionStateKey? ProjectOld(
        SideCapture side, StateFingerprint key, SimulationSnapshot snapshot, Player player,
        FlashRules rules, int skippedPotionUses)
    {
        lock (side.Gate)
        {
            if (side.Cache.TryGetValue(key, out SearchRetentionStateKey? cached))
                return cached;
            try
            {
                FlashBattle battle = FlashStateImporter.Import(snapshot, player, rules, skippedPotionUses);
                SearchRetentionStateKey projected = HashLines(FlashBattleDump.Lines(battle));
                side.Cache[key] = projected;
                return projected;
            }
            catch (Exception ex) when (ex is FlashImportException or FlashCapacityException
                or InvalidOperationException)
            {
                side.Unprojectable++;
                side.ProjectionError ??= $"{ex.GetType().Name}: {ex.Message}";
                side.Cache[key] = null;
                return null;
            }
        }
    }

    private static SearchRetentionStateKey HashLines(IReadOnlyList<string> lines)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("flashbattle-dump-v1\n"u8);
        foreach (string line in lines)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(line));
            hash.AppendData("\n"u8);
        }
        byte[] digest = hash.GetHashAndReset();
        return new SearchRetentionStateKey(
            BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(8)));
    }

    private static bool IsExpected(Exception ex)
        => ex is FlashVerificationException or FlashImportException or FlashUnsupportedException
            or FlashCapacityException or InvalidOperationException or OperationCanceledException;

    private static Dictionary<string, object?> SideSummary(
        SideCapture side, SolverResult? result, double wallMs) => new()
    {
        ["boundaries"] = side.Boundaries.Count,
        ["committed"] = side.Boundaries.Sum(item => item.SelectedKeys.Count),
        ["poolStates"] = side.Boundaries.Sum(item => item.Pool.Count),
        ["generated"] = side.Generated.Count,
        ["unprojectable"] = side.Unprojectable,
        ["compactions"] = side.Compactions,
        ["compactedStates"] = side.CompactedStates,
        ["expanded"] = result?.ExpandedNodes,
        ["transitions"] = result?.TransitionCount,
        ["duplicateCardBranchesPruned"] = result?.DuplicateCardBranchesPruned,
        ["dominatedActionsPruned"] = result?.DominatedActionsPruned,
        ["boundaryReason"] = result?.BoundaryReason.ToString(),
        ["wallMs"] = wallMs,
    };

    private static Dictionary<string, object?> FlashSummary(
        SideCapture side, FlashSolveOutcome? outcome, double wallMs) => new()
    {
        ["boundaries"] = side.Boundaries.Count,
        ["committed"] = side.Boundaries.Sum(item => item.SelectedKeys.Count),
        ["poolStates"] = side.Boundaries.Sum(item => item.Pool.Count),
        ["generated"] = side.Generated.Count,
        ["unprojectable"] = side.Unprojectable,
        ["compactions"] = side.Compactions,
        ["compactedStates"] = side.CompactedStates,
        ["expanded"] = outcome?.Search?.ExpandedNodes,
        ["transitions"] = outcome?.Search?.Transitions,
        ["duplicates"] = outcome?.Search?.Duplicates,
        ["duplicateCardBranchesPruned"] = outcome?.Search?.DuplicateCardBranchesPruned,
        ["turns"] = outcome?.Search?.TurnsSearched,
        ["capacityDropped"] = outcome?.Search?.CapacityDroppedChildren,
        ["rejection"] = outcome?.Rejection.ToString(),
        ["detail"] = outcome?.Detail,
        ["wallMs"] = wallMs,
    };

    private static SolverSearchProfile MemberProfile(SolverSearchProfile profile, HarnessOptions options)
    {
        string flags = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_RETENTION_FLAGS") ?? "";
        string beamWeight = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_RETENTION_BEAM_WEIGHT") ?? "";
        int? nodeBudget = int.TryParse(
            Environment.GetEnvironmentVariable("OFFLINE_HARNESS_RETENTION_NODE_BUDGET"), out int budget)
                ? budget : null;
        BeamWeightPerturbation? perturbation = profile.BeamWeightPerturbation;
        if (beamWeight.Length > 0)
        {
            string[] parts = beamWeight.Split(':', 2);
            if (parts.Length != 2
                || !Enum.TryParse(parts[0], ignoreCase: true, out BeamWeightTerm term)
                || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double scale))
            {
                throw new ArgumentException($"OFFLINE_HARNESS_RETENTION_BEAM_WEIGHT 无法解析：{beamWeight}");
            }
            perturbation = new BeamWeightPerturbation(term, scale);
        }
        return profile with
        {
            BeamWidth = options.Beam ?? profile.BeamWidth,
            MaxExpandedNodes = nodeBudget ?? options.Nodes ?? profile.MaxExpandedNodes,
            SoftTimeBudgetMilliseconds = options.BudgetMilliseconds,
            SecondRankBand = profile.SecondRankBand
                || flags.Contains("band", StringComparison.OrdinalIgnoreCase),
            BaseScoreOnly = profile.BaseScoreOnly
                || flags.Contains("base", StringComparison.OrdinalIgnoreCase),
            AggressivePowerCommitment = profile.AggressivePowerCommitment
                || flags.Contains("power", StringComparison.OrdinalIgnoreCase),
            BeamWeightPerturbation = perturbation,
        };
    }

    private static Dictionary<string, object?> MemberIdentity(SolverSearchProfile profile) => new()
    {
        ["beam"] = profile.BeamWidth,
        ["nodes"] = profile.MaxExpandedNodes,
        ["band"] = profile.SecondRankBand,
        ["base"] = profile.BaseScoreOnly,
        ["power"] = profile.AggressivePowerCommitment,
        ["beamWeight"] = profile.BeamWeightPerturbation?.ToString(),
    };

    private static DirectSearchPurpose ReadPurpose()
        => Enum.TryParse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_RETENTION_PURPOSE"),
            ignoreCase: true, out DirectSearchPurpose purpose)
                ? purpose
                : DirectSearchPurpose.PrimaryBeam;

    private static SolverPotionPolicy? ReadPotionPolicy()
        => Enum.TryParse(Environment.GetEnvironmentVariable("OFFLINE_HARNESS_RETENTION_POTION_POLICY"),
            ignoreCase: true, out SolverPotionPolicy policy)
                ? policy
                : null;

    private static int ReadInt(string name, int fallback, int minimum)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value >= minimum
            ? value : fallback;

    private static void Write(string path, Dictionary<string, object?> summary)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(summary, Json));
    }

    private sealed class SideCapture(string name)
    {
        public readonly object Gate = new();
        public readonly string Name = name;
        public readonly List<BoundaryRecord> Boundaries = [];
        public readonly Dictionary<int, BoundaryRecord> ById = [];
        public readonly Dictionary<SearchRetentionStateKey, GenerationInfo> Generated = [];
        public readonly Dictionary<StateFingerprint, SearchRetentionStateKey?> Cache = [];
        public int Unprojectable;
        public int Compactions;
        public int CompactedStates;
        public string? ProjectionError;

        public void AddBoundary(in SearchRetentionBoundary meta)
        {
            lock (Gate)
            {
                if (ById.ContainsKey(meta.BoundaryId))
                    return;
                BoundaryRecord record = new(meta);
                Boundaries.Add(record);
                ById.Add(meta.BoundaryId, record);
            }
        }

        public void AddPoolState(in SearchRetentionPoolState value)
        {
            lock (Gate)
            {
                if (!ById.TryGetValue(value.BoundaryId, out BoundaryRecord? record))
                    return;
                if (value.Key is not { } key)
                    return;
                PoolEntry entry = new(key, value.Score, value.Rank, value.Required,
                    value.RequiredLabel, value.Selected, value.LocalKey, value.LastAction,
                    value.Turn, value.ActionCount);
                record.Pool.Add(entry);
                if (!record.ByKey.TryGetValue(key, out List<PoolEntry>? list))
                {
                    list = [];
                    record.ByKey.Add(key, list);
                }
                list.Add(entry);
            }
        }

        public void AddSelected(in SearchRetentionSelected value)
        {
            lock (Gate)
            {
                if (!ById.TryGetValue(value.BoundaryId, out BoundaryRecord? record))
                    return;
                if (value.Key is not { } key)
                    return;
                record.Selected.Add(key);
                record.SelectedOrderByKey[key] = value.Order;
                record.SelectedScoreByKey[key] = value.Score;
                record.SelectedActionByKey[key] = value.LastAction;
            }
        }

        public void AddGeneration(in SearchRetentionGeneration value)
        {
            lock (Gate)
            {
                if (value.Key is not { } key)
                    return;
                if (!Generated.TryGetValue(key, out GenerationInfo? info))
                {
                    info = new GenerationInfo(key);
                    Generated.Add(key, info);
                }
                info.Updates++;
                info.LastStatus = value.Status;
                info.Score = value.Score;
                info.LastAction = value.LastAction;
                if (value.Status.StartsWith("accepted", StringComparison.Ordinal)
                    || value.Status is "bypass_cycle_or_ordered_lease"
                        or "disabled_for_measurement" or "entry_limit" or "added"
                        or "duplicate-merged" or "duplicate-dominating-label")
                {
                    info.Admitted = true;
                }
                if (value.Status == "rejected_dominated")
                    info.Rejected = true;
            }
        }
    }

    private sealed class BoundaryRecord(in SearchRetentionBoundary meta)
    {
        public readonly SearchRetentionBoundary Meta = meta;
        public readonly List<PoolEntry> Pool = [];
        public readonly Dictionary<SearchRetentionStateKey, List<PoolEntry>> ByKey = [];
        public readonly List<SearchRetentionStateKey> Selected = [];
        public readonly Dictionary<SearchRetentionStateKey, int> SelectedOrderByKey = [];
        public readonly Dictionary<SearchRetentionStateKey, double> SelectedScoreByKey = [];
        public readonly Dictionary<SearchRetentionStateKey, string> SelectedActionByKey = [];

        public string Key => Meta.Key;

        public IReadOnlyList<SearchRetentionStateKey> SelectedKeys => Selected;

        public PoolEntry[] PoolFor(SearchRetentionStateKey key)
            => ByKey.TryGetValue(key, out List<PoolEntry>? list) ? [.. list] : [];

        public PoolEntry? Best(SearchRetentionStateKey key)
        {
            if (ByKey.TryGetValue(key, out List<PoolEntry>? list) && list.Count > 0)
            {
                return list
                    .OrderByDescending(entry => entry.Selected)
                    .ThenBy(entry => entry.Rank < 0 ? int.MaxValue : entry.Rank)
                    .ThenByDescending(entry => entry.Score)
                    .First();
            }
            if (SelectedScoreByKey.TryGetValue(key, out double score))
            {
                return new PoolEntry(key, score, -1, false, null, true, default,
                    SelectedActionByKey.GetValueOrDefault(key, ""), 0, 0);
            }
            return null;
        }

        public int? SelectedOrder(SearchRetentionStateKey key)
            => SelectedOrderByKey.TryGetValue(key, out int order) ? order : null;
    }

    private readonly record struct PoolEntry(
        SearchRetentionStateKey Key, double Score, int Rank, bool Required, string? Label,
        bool Selected, StateFingerprint LocalKey, string LastAction, int Turn, int Depth);

    private sealed class GenerationInfo(SearchRetentionStateKey key)
    {
        public readonly SearchRetentionStateKey Key = key;
        public bool Admitted;
        public bool Rejected;
        public string LastStatus = "";
        public double Score;
        public string LastAction = "";
        public int Updates;
    }

    private sealed class FlashCapture(SideCapture side) : FlashRetentionObserver
    {
        private readonly Dictionary<int, SearchRetentionStateKey> _nodeKeys = [];

        public override SearchRetentionStateKey Project(in FlashBattle battle)
            => HashLines(FlashBattleDump.Lines(battle));

        public override void Boundary(in FlashRetentionBoundary boundary)
            => side.AddBoundary(new SearchRetentionBoundary(
                boundary.BoundaryId, boundary.Kind, boundary.TurnLayer, boundary.PlayDepth,
                boundary.InputCount, boundary.Width));

        public override void Attempt(int node, int existingNode, in FlashBattle state, string reason)
        {
            SearchRetentionStateKey key = Project(state);
            _nodeKeys[node] = key;
            side.AddGeneration(new SearchRetentionGeneration(
                default, null, key, 0, 0, 0, "", reason));
        }

        public override void PoolState(
            int boundaryId, int node, double score, int rankPosition, bool required, bool selected,
            string lane, int turn, int depth, string lastAction)
        {
            if (!_nodeKeys.TryGetValue(node, out SearchRetentionStateKey key))
            {
                side.Unprojectable++;
                return;
            }
            side.AddPoolState(new SearchRetentionPoolState(
                boundaryId, default, key, score, rankPosition, required, lane, selected,
                lastAction, turn, depth));
            if (selected)
            {
                side.AddSelected(new SearchRetentionSelected(
                    boundaryId, rankPosition, default, key, score, lastAction, turn, depth));
            }
        }

        public override void Pruned(int boundaryId, int node, string reason)
        {
            if (!side.ById.TryGetValue(boundaryId, out BoundaryRecord? record))
                return;
            if (_nodeKeys.TryGetValue(node, out SearchRetentionStateKey key))
                record.Selected.Remove(key);
        }

        public override void Compacted(int before, int after)
        {
            side.Compactions++;
            side.CompactedStates += before - after;
        }
    }

    private sealed class BoundaryView(
        SearchRetentionBoundaryKind kind, int turnLayer, int playDepth, string key,
        BoundaryRecord? oldRecord, BoundaryRecord? flashRecord)
    {
        public readonly SearchRetentionBoundaryKind Kind = kind;
        public readonly int TurnLayer = turnLayer;
        public readonly int PlayDepth = playDepth;
        public readonly string Key = key;
        public readonly BoundaryRecord? Old = oldRecord;
        public readonly BoundaryRecord? Flash = flashRecord;
    }
}
