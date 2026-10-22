using System.Diagnostics;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Flash.Bridge;
using CombatSolver.Flash.Search;
using MegaCrit.Sts2.Core.Combat;

namespace OfflineSearchHarness;

/// <summary>
/// Offline route trace (<c>OFFLINE_HARNESS_FLASH_TRACE_ROUTE=&lt;route.json&gt;</c>). Replays the
/// target existing-engine route on the Flash root, runs the same request with the Flash trace
/// enabled, then runs the same request through the existing solver with
/// <see cref="SearchPathObserver"/> watching the target state keys. Neither run changes selection.
/// </summary>
internal static class FlashRouteTraceProbe
{
    private static readonly JsonSerializerOptions Json = new(UnattendedTestFiles.JsonOptions)
    {
        WriteIndented = false,
    };

    public static Dictionary<string, object?> Run(CombatState combat, HarnessOptions options,
        string routePath, MainLoopContext loop)
    {
        List<PlanAction> route = JsonSerializer.Deserialize<List<PlanAction>>(
            File.ReadAllText(routePath), Json)
            ?? throw new InvalidDataException($"路线为空：{routePath}");
        int prefixLength = ReadInt("OFFLINE_HARNESS_FLASH_TRACE_PREFIX", 0);
        if (prefixLength < 0 || prefixLength > route.Count)
            throw new InvalidOperationException($"追踪前缀长度 {prefixLength} 超出路线动作数 {route.Count}。");
        List<PlanAction> prefix = [.. route.Take(prefixLength)];
        string purposeText = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FLASH_TRACE_PURPOSE") ?? "";
        bool continuation = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FLASH_TRACE_CONTINUATION") == "1";
        DirectSearchPurpose? directPurpose = continuation ? null : ParseDirect(purposeText);
        ContinuationPurpose? continuationPurpose = continuation ? ParseContinuation(purposeText) : null;
        int? maximumPotionUses = ReadNullableInt("OFFLINE_HARNESS_FLASH_TRACE_MAX_POTIONS");
        int? minimumPotionUses = ReadNullableInt("OFFLINE_HARNESS_FLASH_TRACE_MIN_POTIONS");
        PrimarySearchIncumbent? incumbent = ReadNullableInt("OFFLINE_HARNESS_FLASH_TRACE_INCUMBENT_DEFICIT") is { } deficit
            ? new PrimarySearchIncumbent(deficit,
                ReadNullableInt("OFFLINE_HARNESS_FLASH_TRACE_INCUMBENT_TURN")
                    ?? throw new InvalidOperationException("追踪 incumbent 缺少结束回合。"))
            : null;
        SolverPotionPolicy? potionPolicyOverride = Environment.GetEnvironmentVariable(
            "OFFLINE_HARNESS_FLASH_TRACE_POTION_POLICY") is { Length: > 0 } potionPolicyText
                ? Enum.Parse<SolverPotionPolicy>(potionPolicyText, ignoreCase: true)
                : null;
        bool resetBaseline = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FLASH_TRACE_RESET_BASELINE") == "1";
        bool runFlash = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FLASH_TRACE_SIDE") != "existing";
        bool runExisting = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FLASH_TRACE_SIDE") != "flash";

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings, combat, includeTurnSetup: false, theftPolicy: SolverController.ResolveTheftPolicy(combat));
        SolverSearchProfile profile = policy.Profile with
        {
            SoftTimeBudgetMilliseconds = options.BudgetMilliseconds,
            BeamWidth = ReadNullableInt("OFFLINE_HARNESS_FLASH_TRACE_BEAM") ?? policy.Profile.BeamWidth,
            MaxExpandedNodes = ReadNullableInt("OFFLINE_HARNESS_FLASH_TRACE_NODES")
                ?? policy.Profile.MaxExpandedNodes,
        };
        string flags = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FLASH_TRACE_FLAGS") ?? "";
        profile = profile with
        {
            SecondRankBand = profile.SecondRankBand || flags.Contains("band", StringComparison.Ordinal),
            BaseScoreOnly = profile.BaseScoreOnly || flags.Contains("base", StringComparison.Ordinal),
            AggressivePowerCommitment = profile.AggressivePowerCommitment
                || flags.Contains("power", StringComparison.Ordinal),
        };
        policy = policy with { Profile = profile };

        List<string> output = [];
        output.Add($"route={routePath} actions={route.Count} prefix={prefixLength}" +
            $" purpose={purposeText} continuation={continuation}" +
            $" beam={profile.BeamWidth} nodes={profile.MaxExpandedNodes}");
        Dictionary<string, object?> summary = new()
        {
            ["route"] = routePath,
            ["actions"] = route.Count,
            ["prefix"] = prefixLength,
            ["purpose"] = purposeText,
            ["continuation"] = continuation,
            ["beam"] = profile.BeamWidth,
            ["nodes"] = profile.MaxExpandedNodes,
        };

        if (runFlash)
        {
            string? traceError = null;
            FlashRouteTrace? trace = null;
            Stopwatch watch = Stopwatch.StartNew();
            Task<FlashSolveOutcome> task = Task.Run(() => FlashRouteMaterializer.Solve(
                root, names, damage, policy, profile, CancellationToken.None,
                potionPolicyOverride: potionPolicyOverride,
                purpose: directPurpose,
                primaryIncumbent: incumbent,
                fixedPrefixActions: prefix,
                maximumPotionUses: maximumPotionUses,
                minimumPotionUses: minimumPotionUses,
                continuationPurpose: continuationPurpose,
                resetFixedPrefixSchedulingBaseline: continuation ? resetBaseline : null,
                traceFactory: context =>
                {
                    trace = FlashRouteTrace.TryCreate(context.Probe, context.Player,
                        context.SearchRoot, context.Root, context.Rules, route, prefixLength,
                        out traceError);
                    return trace;
                }));
            loop.RunUntilCompleted(task, TimeSpan.FromSeconds(660), "FlashRouteMaterializer.Solve(trace)");
            FlashSolveOutcome outcome = task.GetAwaiter().GetResult();
            output.Add($"FLASH status rejection={outcome.Rejection} detail={outcome.Detail}" +
                $" ms={watch.Elapsed.TotalMilliseconds:0} mapping={traceError ?? "ok"}");
            if (trace != null)
                output.AddRange(trace.Lines);
            summary["flash"] = new
            {
                rejection = outcome.Rejection.ToString(),
                detail = outcome.Detail,
                mapping = traceError,
                wallMs = watch.Elapsed.TotalMilliseconds,
                expanded = outcome.Search?.ExpandedNodes,
                transitions = outcome.Search?.Transitions,
                won = outcome.Result?.Snapshot is { } snapshot && snapshot.AllEnemiesDead && !snapshot.PlayerDead,
                hpLost = outcome.Result?.ProjectedBattleHpLost,
                endTurn = outcome.Result?.CombatEndedTurn,
                plan = outcome.Plan.Count,
                trace = trace?.Lines,
            };
        }

        if (runExisting)
        {
            List<StateFingerprint> keys = TargetStateKeys(root, names, damage, policy, profile, route,
                prefixLength, maximumPotionUses, minimumPotionUses, out string keyError);
            Dictionary<StateFingerprint, int> stepOfKey = new();
            for (int index = 0; index < keys.Count; index++)
                stepOfKey.TryAdd(keys[index], prefixLength + index + 1);
            output.Add($"EXISTING targetStates={keys.Count} keys={keyError}");
            HashSet<StateFingerprint> watched = [.. keys];
            List<object> retention = [];
            SearchPathObserver observer = new(
                state => watched.Contains(state),
                observation =>
                {
                    if (observation.Stage != SearchPathObservationStage.GlobalRetention
                        || !watched.Contains(observation.StateKey))
                        return;
                    SearchPathRetentionDetails retentionDetails = observation.Retention
                        ?? throw new InvalidOperationException("GlobalRetention 缺少 retention 详情。");
                    retention.Add(new
                    {
                        step = stepOfKey.TryGetValue(observation.StateKey, out int tracedStep)
                            ? tracedStep : 0,
                        state = observation.StateKey,
                        observation.Turn,
                        observation.ActionCount,
                        rawRank = retentionDetails.RawRank,
                        requiredIndex = retentionDetails.RequiredIndex,
                        requiredLabel = retentionDetails.RequiredLabel,
                        selectedIndex = retentionDetails.SelectedIndex,
                        limit = retentionDetails.Limit,
                        effectiveLimit = retentionDetails.EffectiveLimit,
                        requiredCount = retentionDetails.RequiredCount,
                        selectedCount = retentionDetails.SelectedCount,
                    });
                },
                state => watched.Contains(state));
            SearchPolicySnapshot observed = policy with
            {
                Diagnostics = new SearchDiagnosticsSink(_ => { }, _ => { }, observer,
                    retentionLabels: true),
            };
            Stopwatch watch = Stopwatch.StartNew();
            Task<SolverResult> task = Task.Run(() => new CombatBeamSolver(
                root, names, damage, observed, searchProfile: profile,
                potionPolicyOverride: potionPolicyOverride,
                primaryIncumbent: incumbent,
                maximumPotionUses: maximumPotionUses,
                fixedPrefixActions: prefix,
                resetFixedPrefixSchedulingBaseline: continuation && resetBaseline,
                minimumPotionUses: minimumPotionUses).Solve());
            loop.RunUntilCompleted(task, TimeSpan.FromSeconds(660), "CombatBeamSolver.Solve(trace)");
            SolverResult result = task.GetAwaiter().GetResult();
            output.Add($"EXISTING result won={result.Snapshot.AllEnemiesDead && !result.Snapshot.PlayerDead}" +
                $" hpLost={result.ProjectedBattleHpLost} endTurn={result.CombatEndedTurn}" +
                $" actions={result.BestNode?.ActionCount ?? 0} ms={watch.Elapsed.TotalMilliseconds:0}");
            foreach (object record in retention)
                output.Add($"EXISTING-RETAIN {JsonSerializer.Serialize(record, Json)}");
            summary["existing"] = new
            {
                targetStates = keys.Count,
                keyError,
                wallMs = watch.Elapsed.TotalMilliseconds,
                hpLost = result.ProjectedBattleHpLost,
                won = result.Snapshot.AllEnemiesDead && !result.Snapshot.PlayerDead,
                endTurn = result.CombatEndedTurn,
                retention,
            };
        }

        string path = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_FLASH_TRACE_OUT")
            ?? Path.Combine(options.OutputDirectory, "flash-trace.txt");
        File.WriteAllLines(path, output);
        summary["tracePath"] = path;
        return summary;
    }

    /// <summary>
    /// Replays the target route from the request root (prefix included) through the existing engine
    /// and returns each searched action's state key, so the path observer can watch the same states.
    /// </summary>
    private static List<StateFingerprint> TargetStateKeys(
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, SolverSearchProfile profile, IReadOnlyList<PlanAction> route,
        int prefixLength, int? maximumPotionUses, int? minimumPotionUses, out string error)
    {
        List<StateFingerprint> keys = [];
        OpeningProbe probe = new(root, names, damage, policy, CancellationToken.None,
            searchProfile: profile, maximumPotionUses: maximumPotionUses);
        SimulationSnapshot? snapshot = null;
        try
        {
            SimulationSnapshot rootSnapshot = probe.ReplayForFlash([], null);
            if (prefixLength > 0)
            {
                snapshot = probe.ReplayFixedPrefixForFlash(rootSnapshot, [.. route.Take(prefixLength)]);
                if (snapshot == null)
                {
                    error = "固定前缀无法在旧引擎回放";
                    return keys;
                }
            }
            else
            {
                snapshot = rootSnapshot;
            }
            for (int step = prefixLength; step < route.Count; step++)
            {
                SimulationSnapshot child = probe.ReplayForFlash([route[step]], snapshot);
                if (step > prefixLength)
                    snapshot.ReleaseSimulator();
                snapshot = child;
                keys.Add(child.StateKey);
            }
            error = "ok";
            return keys;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidPlannedChoiceBranchException)
        {
            error = ex.Message;
            return keys;
        }
        finally
        {
            snapshot?.ReleaseSimulator();
        }
    }

    private static DirectSearchPurpose? ParseDirect(string value)
    {
        if (value.Length == 0)
            return DirectSearchPurpose.PrimaryBeam;
        return Enum.TryParse(value, ignoreCase: true, out DirectSearchPurpose parsed)
            ? parsed
            : throw new ArgumentException($"未知 DirectSearchPurpose：{value}");
    }

    private static ContinuationPurpose ParseContinuation(string value)
        => value.Length == 0
            ? throw new ArgumentException("续接追踪缺少 OFFLINE_HARNESS_FLASH_TRACE_PURPOSE。")
            : Enum.TryParse(value, ignoreCase: true, out ContinuationPurpose parsed)
                ? parsed
                : throw new ArgumentException($"未知 ContinuationPurpose：{value}");

    private static int ReadInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    private static int? ReadNullableInt(string name)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : null;
}
