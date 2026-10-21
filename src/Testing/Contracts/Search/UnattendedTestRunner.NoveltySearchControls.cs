using System.Diagnostics;
using CombatSolver.Flash.Search;
using CombatSolver.Flash.Search.Scoring;
using CombatSolver.Flash.Bridge;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task CheckNoveltyControlsAsync(CombatState combat, Player player,
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot captured)
    {
        var enemies = combat.Enemies.ToArray();
        var actual = enemies.Select(enemy => CaptureActual(combat, player, enemy)).ToArray();
        var shadow = CaptureKnownRouteRootStates(root, player, enemies);
        var profile = captured.Profile with { MaxExpandedNodes = 4000, SoftTimeBudgetMilliseconds = 30000 };
        var common = captured with { Profile = profile, BudgetOverrideMilliseconds = 30000,
            FixedBudget = true, UseNoveltyPortfolio = true, StopAtAcceptableBattleHpLoss = false,
            MaxDegreeOfParallelism = 2 };
        foreach (bool route in new[] { false, true })
        {
            SearchInteractionState interaction = new();
            bool requested = false;
            SolverRouteAdoptionSeed? seed = null;
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
            SolverResult adopted = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage,
                common with { Interaction = interaction }, deadline.Token, progress =>
                {
                    if (requested) return;
                    if (route && progress.RouteAdoptionSeed is { } candidate)
                    {
                        seed = candidate;
                        requested = interaction.RequestAdoptRoute(candidate);
                    }
                    else if (!route && progress.CurrentTurnPreview != null)
                        requested = interaction.RequestApplyCurrentTurn();
                }), deadline.Token);
            if (!requested || adopted.BestNode.ActionCount == 0
                || route && (seed == null || !adopted.BestNode.Actions.SequenceEqual(seed.Actions))
                || !route && adopted.ResultScope != SolverResultScope.CurrentTurnAdoption)
                throw new InvalidOperationException("Novelty search failed to honor the displayed-route takeover.");
            _writer.CaptureSolverResult(adopted);
            AssertRoot();
            _completedChecks.Add(route ? "Novelty:AdoptDisplayedRoute:ExactActions" : "Novelty:ApplyCurrentTurn");
        }
        await CheckFlashRuntimeControlsAsync(root, names, damage, captured);
        using CancellationTokenSource cancelled = new(TimeSpan.FromSeconds(30));
        SearchRequestWorkTotals work = new();
        bool cancellationRequested = false;
        try
        {
            await Task.Run(() => new CombatBeamSolver(root, names, damage,
                common with { NoveltySearch = new(), RequestWorkTotals = work }, cancelled.Token,
                progress =>
                {
                    if (progress.ExpandedNodes > 0)
                    {
                        cancellationRequested = true;
                        cancelled.Cancel();
                    }
                }, profile, potionPolicyOverride: SolverPotionPolicy.Disabled).Solve(), cancelled.Token);
            throw new InvalidOperationException("Novelty cancellation did not propagate.");
        }
        catch (OperationCanceledException) when (cancellationRequested && cancelled.IsCancellationRequested)
        {
            SearchRequestWorkSnapshot cancelledWork = work.Snapshot();
            if (cancelledWork.RecordedSolverCount != 1 || cancelledWork.ExpandedNodes <= 0
                || cancelledWork.TransitionCount <= 0)
                throw new InvalidOperationException("Cancelled novelty work was not accounted exactly once.");
            AssertRoot();
            _completedChecks.Add("Novelty:Cancel:WorkCounted:LiveAndShadowRootUnchanged");
        }

        void AssertRoot()
        {
            var after = CaptureKnownRouteRootStates(root, player, enemies);
            for (int i = 0; i < enemies.Length; i++)
            {
                AssertSnapshotEqual(actual[i], CaptureActual(combat, player, enemies[i]), "NoveltyControls", "live_root");
                AssertSnapshotEqual(shadow[i], after[i], "NoveltyControls", "shadow_root");
            }
        }
    }

    /// <summary>
    /// Direct Flash runtime-interaction contract (On mode only): progress/preview, route adoption,
    /// apply-current-turn, cancellation latency and the normalized work ledger. The novelty control
    /// above already exercises the coordinator's takeover plumbing; this covers the Flash search's own
    /// checkpoints without a portfolio or novelty scheduler in between.
    /// </summary>
    private async Task CheckFlashRuntimeControlsAsync(CombatRootSnapshot root,
        SolverDisplayNames names, BattleDamageSnapshot damage, SearchPolicySnapshot captured)
    {
        if (captured.FlashMode != FlashSearchMode.On)
        {
            _completedChecks.Add("Flash:DirectRuntimeControls:SkippedOff");
            return;
        }
        SolverSearchProfile profile = captured.Profile with
        {
            BeamWidth = Math.Min(captured.Profile.BeamWidth, 60),
            MaxExpandedNodes = 4_000,
            SoftTimeBudgetMilliseconds = 30_000,
        };
        SearchPolicySnapshot basePolicy = captured with
        {
            Profile = profile,
            BudgetOverrideMilliseconds = 30_000,
            FixedBudget = true,
            FlashMode = FlashSearchMode.On,
            MaxDegreeOfParallelism = 2,
        };

        // 1. Progress + displayed-route adoption from the Flash search's own checkpoint.
        SearchInteractionState routeInteraction = new();
        SearchRequestWorkTotals routeWork = new();
        bool requestedRoute = false;
        bool previewSeen = false;
        bool seedSeen = false;
        int progressEvents = 0;
        SolverRouteAdoptionSeed? requestedSeed = null;
        using (CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30)))
        {
            FlashSolveOutcome outcome = await Task.Run(() => FlashRouteMaterializer.Solve(
                root, names, damage,
                basePolicy with { Interaction = routeInteraction, RequestWorkTotals = routeWork },
                profile, deadline.Token,
                progressCallback: progress =>
                {
                    progressEvents++;
                    if (progress.SpeculativeRoutePreview != null)
                        previewSeen = true;
                    if (!requestedRoute && progress.RouteAdoptionSeed is { } seed)
                    {
                        requestedSeed = seed;
                        seedSeen = true;
                        requestedRoute = routeInteraction.RequestAdoptRoute(seed);
                    }
                }), deadline.Token);
            if (outcome.Result is not { } adopted
                || adopted.ResultScope != SolverResultScope.RouteAdoption
                || !requestedRoute || requestedSeed == null
                || !adopted.BestNode.Actions.SequenceEqual(requestedSeed.Actions))
            {
                throw new InvalidOperationException(
                    "Flash 显示路线接管没有返回请求的候选：" +
                    $"events={progressEvents} preview={previewSeen} seed={seedSeen} " +
                    $"requested={requestedRoute} scope={outcome.Result?.ResultScope} " +
                    $"detail={outcome.Detail}");
            }
            if (!previewSeen || progressEvents < 2)
                throw new InvalidOperationException(
                    $"Flash 搜索期间没有发布路线预览/进度：events={progressEvents} preview={previewSeen}。");
            SearchRequestWorkSnapshot work = routeWork.Snapshot();
            long expectedBudget = (work.ExpandedNodes + 3) / 4;
            if (work.RecordedSolverCount != 1 || work.ExpandedNodes <= 0
                || work.TransitionCount <= 0 || work.BudgetExpandedNodes != expectedBudget)
            {
                throw new InvalidOperationException(
                    "Flash 路线接管的工作量没有按一次搜索记录并归一化：" +
                    $"records={work.RecordedSolverCount} expanded={work.ExpandedNodes} " +
                    $"transitions={work.TransitionCount} budget={work.BudgetExpandedNodes} " +
                    $"expected_budget={expectedBudget}。");
            }
        }
        _completedChecks.Add("Flash:ProgressPreview:AdoptDisplayedRoute:WorkNormalized");

        // 2. Apply-current-turn from a running Flash search.
        SearchInteractionState turnInteraction = new();
        bool requestedTurn = false;
        using (CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30)))
        {
            FlashSolveOutcome outcome = await Task.Run(() => FlashRouteMaterializer.Solve(
                root, names, damage,
                basePolicy with { Interaction = turnInteraction },
                profile, deadline.Token,
                progressCallback: progress =>
                {
                    if (!requestedTurn && progress.CurrentTurnPreview != null)
                        requestedTurn = turnInteraction.RequestApplyCurrentTurn();
                }), deadline.Token);
            if (outcome.Result is not { } adopted
                || adopted.ResultScope != SolverResultScope.CurrentTurnAdoption
                || !requestedTurn
                || adopted.BestNode.ActionCount == 0)
            {
                throw new InvalidOperationException(
                    "Flash 搜索期间的当前回合接管没有返回 CurrentTurnAdoption：" +
                    $"requested={requestedTurn} scope={outcome.Result?.ResultScope} " +
                    $"detail={outcome.Detail}");
            }
        }
        _completedChecks.Add("Flash:ApplyCurrentTurn:CurrentTurnAdoption");

        // 3. A complete Flash solve with no takeover: the shared pacer must yield its work slice at
        // least once. This also gives the ordinary work numbers the normalized-budget check reads.
        using (CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30)))
        {
            FlashSolveOutcome outcome = await Task.Run(() => FlashRouteMaterializer.Solve(
                root, names, damage, basePolicy, profile, deadline.Token), deadline.Token);
            if (outcome.Result == null || outcome.Search is not { } complete
                || complete.ExpandedNodes <= 0 || complete.WorkerYieldCount <= 0)
            {
                throw new InvalidOperationException(
                    "Flash 普通搜索没有按工作切片让出：" +
                    $"expanded={outcome.Search?.ExpandedNodes} yields={outcome.Search?.WorkerYieldCount} " +
                    $"detail={outcome.Detail}");
            }
            _completedChecks.Add(
                $"Flash:WorkSliceYield:Expanded={complete.ExpandedNodes}:" +
                $"Yields={complete.WorkerYieldCount}");
        }

        // 4. Frame pressure: a main-thread signal raising the pressure epoch must make the shared
        // pacer wait for a frame before the loop continues. The background observer stands in for
        // the game's frame callback; the search runs long enough to cross many pacer checks.
        SearchFramePressureSignal pressure = new();
        pressure.ResetPressure();
        using (CancellationTokenSource frameStop = new())
        {
            Task frames = Task.Run(() =>
            {
                while (!frameStop.IsCancellationRequested)
                {
                    pressure.ObserveFrame(50d, searchActive: true);
                    Thread.Sleep(1);
                }
            }, frameStop.Token);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
            FlashSolveOutcome frameOutcome;
            try
            {
                frameOutcome = await Task.Run(() => FlashRouteMaterializer.Solve(
                    root, names, damage,
                    basePolicy with { FramePressureSignal = pressure },
                    profile, deadline.Token), deadline.Token);
            }
            finally
            {
                frameStop.Cancel();
                try { await frames; } catch (OperationCanceledException) { }
            }
            if (frameOutcome.Result == null || frameOutcome.Search is not { } frameSearch
                || frameSearch.WorkerYieldCount == 0 && frameSearch.FrameRecoveryWaitCount == 0)
            {
                throw new InvalidOperationException(
                    "Flash 搜索没有响应帧压力让出：" +
                    $"yields={frameOutcome.Search?.WorkerYieldCount} " +
                    $"frame_waits={frameOutcome.Search?.FrameRecoveryWaitCount} " +
                    $"detail={frameOutcome.Detail}");
            }
            _completedChecks.Add(
                $"Flash:FramePressure:Yields={frameSearch.WorkerYieldCount}:" +
                $"Waits={frameSearch.FrameRecoveryWaitCount}:" +
                $"WaitMs={frameSearch.FrameRecoveryWaitDuration.TotalMilliseconds:F1}");
        }

        // 5. Cancellation latency and work accounting: cancel after real work, then measure the
        // return. The search checks cancellation inside every action loop.
        SearchRequestWorkTotals cancelWork = new();
        using CancellationTokenSource cancellation = new();
        long cancelTimestamp = 0;
        long cancelStarted = 0;
        bool cancelRequested = false;
        try
        {
            await Task.Run(() => FlashRouteMaterializer.Solve(
                root, names, damage,
                basePolicy with { RequestWorkTotals = cancelWork },
                profile, cancellation.Token,
                progressCallback: progress =>
                {
                    if (cancelRequested || progress.ExpandedNodes < 200)
                        return;
                    cancelRequested = true;
                    cancelStarted = Stopwatch.GetTimestamp();
                    cancellation.Cancel();
                }), cancellation.Token);
            throw new InvalidOperationException("Flash 搜索取消没有向外传播。");
        }
        catch (OperationCanceledException) when (cancelRequested && cancellation.IsCancellationRequested)
        {
            cancelTimestamp = Stopwatch.GetTimestamp();
        }
        double cancelLatencyMilliseconds = Stopwatch.GetElapsedTime(cancelStarted, cancelTimestamp)
            .TotalMilliseconds;
        SearchRequestWorkSnapshot canceled = cancelWork.Snapshot();
        if (cancelStarted == 0 || cancelLatencyMilliseconds > 1_000
            || canceled.RecordedSolverCount != 1 || canceled.ExpandedNodes <= 0
            || canceled.TransitionCount <= 0)
        {
            throw new InvalidOperationException(
                "Flash 取消响应或工作量记账不正确：" +
                $"latency_ms={cancelLatencyMilliseconds:F1} records={canceled.RecordedSolverCount} " +
                $"expanded={canceled.ExpandedNodes} transitions={canceled.TransitionCount}。");
        }
        _completedChecks.Add(
            $"Flash:CancelLatencyMs={cancelLatencyMilliseconds:F1}:WorkCounted");
    }
}
