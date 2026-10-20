using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

internal sealed class SearchMaterializationMetrics
{
    public SearchMaterializationMetrics(SearchPerformanceMetrics performance)
        => Performance = performance;
    public SearchPerformanceMetrics Performance { get; }
    public SearchWorkPacer? WorkPacer { get; init; }
    public int Expanded;
    public int TransitionCount;
    public int ReplayCount;
    public int ForkCount;
    public int ActionAdmissionRepresentativesProtected;
    public int CardChoicePrefixAttempts;
    public int CardChoicePrefixCaptures;
    public int CardChoicePrefixFallbacks;
    public int CardChoicePrefixReuses;
    public int ChoiceBranchesDroppedByBudget;
    public int ChoiceBranchesEvaluated;
    public int ChoiceReplayAttempts;
    public int ChoiceReplayBudgetExhaustions;
    public int CrossTurnCandidatesProtected;
    public int CrossTurnContinuationsStopped;
    public int CycleCandidatesProtected;
    public int CycleContinuationsStopped;
    public int CycleProbeContinuationsExpanded;
    public int CycleRegionCandidatesAdmitted;
    public int CycleRegionCandidatesConsidered;
    public int CycleRegionCandidatesDropped;
    public int CycleRegionMaxActionFamilies;
    public int CycleRegionProbeCandidatesAdmitted;
    public int CycleRegionProgressCandidatesAdmitted;
    public int CycleRegionProgressEpochs;
    public int CycleRegionsDetected;
    public int CycleReplayActions;
    public int CycleReplayAttempts;
    public int CycleReplayContinuations;
    public int CycleReplayVictories;
    public int CycleShapesDetected;
    public int CycleStoppedExitBudget;
    public int CycleStoppedFamilyBudget;
    public int CycleStoppedRepetitionBudget;
    public int CycleStoppedUnproductive;
    public int DeferredRoundChoiceActions;
    public int DeferredRoundChoiceFinitePendingFallbacks;
    public int DeferredRoundChoiceFinitePrimaryLayers;
    public int DeferredRoundChoiceFiniteQuotaFallbacks;
    public int DeferredRoundChoiceLayerWidthTotal;
    public int DominatedActionsPruned;
    public int DuplicateCardBranchesPruned;
    public int ExecutionChoiceCaptures;
    public int ExecutionChoiceReuses;
    public int HpInvestmentBranchesProtected;
    public int MaxDeferredRoundChoiceLayerWidth;
    public int MaxParallelActionReplayConcurrency;
    public int MaxParallelExpansionConcurrency;
    public int MaxParallelRoundChoiceReplayConcurrency;
    public int NodeLimitSnapshotsReleased;
    public int OrderedMutationColdAtomicCommitted;
    public int OrderedMutationColdAtomicRejected;
    public int OrderedMutationLeaseExpiredBudget;
    public int OrderedMutationOrdinaryFallbacks;
    public int OrderedMutationPortfolioNodesConsumed;
    public int ParallelActionReplayWaves;
    public int ParallelActionReplayWorkItems;
    public int ParallelExpansionWaves;
    public int ParallelExpansionWorkItems;
    public int ParallelRoundChoiceReplayWaves;
    public int ParallelRoundChoiceReplayWorkItems;
    public int PotionChoicePrefixCaptures;
    public int PotionChoicePrefixFallbacks;
    public int PotionChoicePrefixForks;
    public int PotionChoicePrefixReuses;
    public int PrimaryIncumbentBranchesPruned;
    public int PrimaryIncumbentCertifiedHealingBoundBranchesPruned;
    public int PrimaryIncumbentUpdates;
    public int RepeatableNoProgressBranchesPruned;
    public int ReusedNodeSnapshots;
    public int RoundReplayPrefixCaptures;
    public int RoundReplayPrefixReuses;
    public int ShuffleBranchesPruned;
    public int SoldHpBranchesPruned;
    public int StandPatProbes;
    public int TopQueueActionsDropped;
    public int TranspositionBranchesPruned;
    public int TranspositionLimitBypasses;
    public int TurnLayerBudgetStops;
    public int TurnLayerNodeBudgetStops;
    public int TurnLayerTimeBudgetStops;
    public int TranspositionCount;
    public int ExpandedTranspositionCount;
    public int StandPatCacheCount;
    public int ThreatProjectionCacheCount;
    public int CoverageCacheCount;
    public TimeSpan MaxObservedGcPause;
    public int WorkerYieldCount;
    public int FrameRecoveryWaitCount;
    public TimeSpan FrameRecoveryWaitDuration;
    public long OffThreadAllocatedBytes;
    public NoveltySearchTelemetry? NoveltySearch;

    internal void RefreshReplayCounters(SearchReplayAccounting accounting)
    {
        ReplayCount = accounting.ReplayCount;
        ForkCount = accounting.ForkCount;
        TransitionCount = accounting.TransitionCount;
        ChoiceReplayAttempts = accounting.ChoiceReplayAttempts;
        ChoiceBranchesEvaluated = accounting.ChoiceBranchesEvaluated;
        ChoiceReplayBudgetExhaustions = accounting.ChoiceReplayBudgetExhaustions;
        ChoiceBranchesDroppedByBudget = accounting.ChoiceBranchesDroppedByBudget;
        RoundReplayPrefixCaptures = accounting.RoundReplayPrefixCaptures;
        RoundReplayPrefixReuses = accounting.RoundReplayPrefixReuses;
        ExecutionChoiceCaptures = accounting.ExecutionChoiceCaptures;
        ExecutionChoiceReuses = accounting.ExecutionChoiceReuses;
        CardChoicePrefixAttempts = accounting.CardChoicePrefixAttempts;
        CardChoicePrefixCaptures = accounting.CardChoicePrefixCaptures;
        CardChoicePrefixReuses = accounting.CardChoicePrefixReuses;
        CardChoicePrefixFallbacks = accounting.CardChoicePrefixFallbacks;
        PotionChoicePrefixForks = accounting.PotionChoicePrefixForks;
        PotionChoicePrefixCaptures = accounting.PotionChoicePrefixCaptures;
        PotionChoicePrefixReuses = accounting.PotionChoicePrefixReuses;
        PotionChoicePrefixFallbacks = accounting.PotionChoicePrefixFallbacks;
    }
}

internal sealed class SearchRouteMaterializer(
    CombatRootSnapshot root,
    SolverDisplayNames displayNames,
    BattleDamageSnapshot battleDamage,
    SearchPolicySnapshot policy,
    SearchReplayCore replay,
    SearchExpansionContext expansionContext,
    FinalPlanOrdering finalOrdering,
    SearchMaterializationMetrics metrics,
    bool forceAllPotionsDisabled,
    SolverTheftPolicy? theftPolicy,
    bool isActEndingBoss,
    BossHpRelief bossHpRelief,
    Stopwatch stopwatch,
    long allocatedBytesAtStart,
    int gen0AtStart,
    int gen1AtStart,
    int gen2AtStart,
    TimeSpan gcPauseAtStart)
{
    private CombatRootSnapshot Root => root;
    private SolverDisplayNames DisplayNames => displayNames;
    private BattleDamageSnapshot BattleDamage => battleDamage;
    private SearchPolicySnapshot Policy => policy;
    private SearchReplayCore Replay => replay;
    private SearchExpansionContext ExpansionContext => expansionContext;
    private FinalPlanOrdering FinalOrdering => finalOrdering;
    private SearchMaterializationMetrics Metrics => metrics;
    private bool ForceAllPotionsDisabled => forceAllPotionsDisabled;
    private SolverSearchProfile Profile => Policy.Profile;
    private const string BlockPotionId = "BLOCK_POTION";
    private SolverTheftPolicy? TheftPolicy => theftPolicy;
    private bool IsActEndingBoss => isActEndingBoss;
    private BossHpRelief BossHpRelief => bossHpRelief;
    private Stopwatch Stopwatch => stopwatch;
    private long AllocatedBytesAtStart => allocatedBytesAtStart;
    private int Gen0AtStart => gen0AtStart;
    private int Gen1AtStart => gen1AtStart;
    private int Gen2AtStart => gen2AtStart;
    private TimeSpan GcPauseAtStart => gcPauseAtStart;
    private sealed record AfterimageFrontloading(SearchNode Node, RouteAnnotations Annotations, int HpSaved);
    private sealed record BlockPotionInsertion(SearchNode Node, RouteAnnotations Annotations, int HpSaved);

internal static RouteAnnotations BuildRouteAnnotations(
    SearchNode best,
    CombatRootSnapshot Root,
    SolverDisplayNames DisplayNames,
    ActionRelicTriggerRecorder? killRecorder = null,
    IReadOnlyList<Creature>? annotationEnemies = null)
{
    List<SearchNode> path = [];
    for (SearchNode? node = best; node?.Parent != null; node = node.Parent)
        path.Add(node);
    path.Reverse();

    Dictionary<int, int> losses = [];
    Dictionary<int, int> enemyHpLosses = [];
    Dictionary<int, int> sold = [];
    Dictionary<int, int> maxBlock = [];
    Dictionary<int, int> actualBlock = [];
    Dictionary<int, int> recoveries = [];
    Dictionary<int, int> energy = [];
    Dictionary<int, int> potionCounts = [];
    Dictionary<int, int> potionCosts = [];
    Dictionary<int, IReadOnlyList<string>> kills = [];
    ulong aliveMask = Root.InitialAliveEnemyMask;
    int? combatEndedTurn = null;
    int? deathTurn = null;

    foreach (SearchNode node in path)
    {
        SearchNode parent = node.Parent!;
        PlanAction action = node.Action
            ?? throw new InvalidOperationException("路线标注节点缺少动作。");
        int potionCount = node.PotionCount - parent.PotionCount;
        int potionCost = node.PotionStrategicCost - parent.PotionStrategicCost;
        if (potionCount > 0)
            potionCounts[action.Turn] = potionCounts.GetValueOrDefault(action.Turn) + potionCount;
        if (potionCost > 0)
            potionCosts[action.Turn] = potionCosts.GetValueOrDefault(action.Turn) + potionCost;
        ulong newlyKilledMask = aliveMask & ~node.Snapshot.AliveEnemyMask;
        if (action.IsExecutable && newlyKilledMask != 0)
        {
            List<string> newlyKilled = [];
            for (int enemyIndex = 0; enemyIndex < Root.Enemies.Count; enemyIndex++)
            {
                if ((newlyKilledMask & (1UL << enemyIndex)) != 0)
                    newlyKilled.Add(DisplayNames.Creature(Root.Enemies[enemyIndex]));
            }
            kills[node.ActionCount - 1] = newlyKilled;
        }
        aliveMask = node.Snapshot.AliveEnemyMask;

        TurnOutcome? turnOutcome = node.Outcome;
        // Boundary fallbacks can retain the original node rather than its ranked annotation clone.
        if (turnOutcome == null && (node.IsTerminal || node.Turn > parent.Turn))
            turnOutcome = CreateUncomparedTurnOutcome(node);
        if (turnOutcome is { } outcome)
        {
            losses[outcome.Turn] = outcome.HpLost;
            recoveries[outcome.Turn] = outcome.HpRecovered;
            enemyHpLosses[outcome.Turn] = outcome.EnemyHpLost;
            actualBlock[outcome.Turn] = outcome.ActualBlock;
            maxBlock[outcome.Turn] = outcome.MaxBlock;
            sold[outcome.Turn] = outcome.SoldHp;
            energy[outcome.Turn] = outcome.EnergyLeft;
        }
        if (combatEndedTurn == null
            && SolverInterimResultOrdering.IsCompleteVictory(
                node.ActionCount,
                node.Snapshot.AllEnemiesDead,
                node.Snapshot.PlayerDead,
                node.Snapshot.ProjectedPlayerHp)
            && node.Snapshot.BoundaryReason != SearchBoundaryReason.UnsupportedEffect)
        {
            combatEndedTurn = node.Snapshot.CombatEndedTurn;
        }
        if (deathTurn == null && node.Snapshot.PlayerDead)
            deathTurn = node.Snapshot.DeathTurn;
    }
    if (killRecorder != null)
    {
        ArgumentNullException.ThrowIfNull(annotationEnemies);
        Dictionary<int, IReadOnlyList<string>> attributedKills = [];
        foreach (SearchNode node in path)
        {
            int actionIndex = node.ActionCount - 1;
            IReadOnlyList<RecordedKill> recorded = killRecorder.KillsForAction(actionIndex);
            if (recorded.Count > 0)
            {
                attributedKills[actionIndex] = recorded
                    .Select(kill =>
                    {
                        Creature enemy = annotationEnemies.First(candidate => candidate.CombatId == kill.CombatId);
                        string targetName = DisplayNames.Creature(enemy, annotationEnemies);
                        if (string.IsNullOrEmpty(targetName))
                            targetName = kill.TargetId;
                        return $"{targetName}（{DisplayNames.DamageSource(kill.Source)}）";
                    })
                    .ToArray();
            }
            else if (kills.TryGetValue(actionIndex, out IReadOnlyList<string>? fallback))
            {
                attributedKills[actionIndex] = fallback
                    .Select(name => $"{name}（{DisplayNames.DamageSource(CombatDamageSource.Unknown)}）")
                    .ToArray();
            }
        }
        kills = attributedKills;
    }

    return new RouteAnnotations(
        losses,
        recoveries,
        enemyHpLosses,
        sold,
        maxBlock,
        actualBlock,
        energy,
        potionCounts,
        potionCosts,
        kills,
        combatEndedTurn,
        deathTurn);
}

internal static TurnOutcome CreateUncomparedTurnOutcome(SearchNode node)
{
    SearchNode parent = node.Parent
        ?? throw new InvalidOperationException("回合结果节点没有父节点。");
    SearchNode turnStart = FindTurnStart(parent);
    bool endedByTurn = node.Action!.Kind == PlanActionKind.EndTurn || node.Turn > parent.Turn;
    int block = endedByTurn ? parent.Snapshot.PlayerBlock : node.Snapshot.PlayerBlock;
    int energy = endedByTurn ? parent.Snapshot.Energy : node.Snapshot.Energy;
    int soldThisTurn = node.FutureSoldHp - parent.FutureSoldHp;
    if (soldThisTurn < 0)
        throw new InvalidOperationException("路线回合累计卖血不能减少。");
    return CreateTurnOutcome(node, turnStart,
        Math.Max(0, node.Snapshot.CumulativePlayerHpLost - turnStart.Snapshot.CumulativePlayerHpLost),
        soldThisTurn, block, block, energy);
}

private static TurnOutcome CreateTurnOutcome(
    SearchNode node,
    SearchNode turnStart,
    int hpLost,
    int soldThisTurn,
    int maxBlock,
    int actualBlock,
    int energyLeft)
    => new(
        node.Action!.Turn,
        hpLost,
        Math.Max(0, node.Snapshot.RecoveredPlayerHp - turnStart.Snapshot.RecoveredPlayerHp),
        node.CumulativeEnemyHpLost - turnStart.CumulativeEnemyHpLost,
        soldThisTurn,
        maxBlock,
        actualBlock,
        energyLeft);

internal static SearchNode FindTurnStart(SearchNode node)
{
    SearchNode current = node;
    while (current.Parent is { } parent && parent.Turn == current.Turn)
        current = parent;
    return current;
}

internal static ContinuationCapture PrepareContinuationCapture(SearchNode best)
{
    List<(int ActionIndex, int Turn)> boundaries = [];
    HashSet<int> laterActionTurns = [];
    for (SearchNode? node = best; node?.Parent != null; node = node.Parent)
    {
        PlanAction action = node.Action
            ?? throw new InvalidOperationException("续用路径节点缺少动作。");
        if ((action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn)
            && !node.Snapshot.PlayerDead
            && !node.Snapshot.AllEnemiesDead
            && node.Snapshot.BoundaryReason == SearchBoundaryReason.None
            && laterActionTurns.Contains(node.Turn))
        {
            boundaries.Add((node.ActionCount - 1, node.Turn));
        }
        laterActionTurns.Add(action.Turn);
    }
    boundaries.Reverse();
    return new ContinuationCapture(boundaries);
}

private AfterimageFrontloading? TryFrontloadAfterimages(
    SearchNode original,
    RouteAnnotations originalAnnotations,
    SolverResultScope resultScope)
{
    if (resultScope != SolverResultScope.SearchCompletion
        || !SolverInterimResultOrdering.IsCompleteVictory(
            original.ActionCount,
            original.Snapshot.AllEnemiesDead,
            original.Snapshot.PlayerDead,
            original.Snapshot.ProjectedPlayerHp))
    {
        return null;
    }

    PlanAction[] actions = original.Actions.ToArray();
    if (!HasDelayedAfterimage(actions))
        return null;

    SearchNode? candidate = ReplayAdjustedRoute(
        actions,
        original.GetTurnSetupChoices(),
        original.GetTurnSetupPlayState(),
        originalAnnotations,
        frontloadAfterimages: true);
    if (candidate == null)
        return null;

    bool samePolicy = candidate.PotionCount == original.PotionCount
        && candidate.PotionStrategicCost == original.PotionStrategicCost
        && candidate.Snapshot.ProjectedDeathSaveUseCount
            <= original.Snapshot.ProjectedDeathSaveUseCount;
    bool lessDamage = candidate.Snapshot.CumulativePlayerHpLost
        < original.Snapshot.CumulativePlayerHpLost;
    bool completeVictory = SolverInterimResultOrdering.IsCompleteVictory(
        candidate.ActionCount,
        candidate.Snapshot.AllEnemiesDead,
        candidate.Snapshot.PlayerDead,
        candidate.Snapshot.ProjectedPlayerHp);
    if (!samePolicy || !lessDamage || !completeVictory)
    {
        candidate.Snapshot.ReleaseSimulator();
        return null;
    }

    FinalPlanSelection comparison;
    try
    {
        comparison = FinalOrdering.Select(
            [(original, original.Snapshot), (candidate, candidate.Snapshot)],
            Root.InitialPlayerHp,
            emitDiagnostics: false);
    }
    catch (PotionPolicyUnsatisfiedException)
    {
        candidate.Snapshot.ReleaseSimulator();
        return null;
    }
    if (!ReferenceEquals(comparison.Candidate.Node, candidate))
    {
        candidate.Snapshot.ReleaseSimulator();
        return null;
    }

    int hpSaved = original.Snapshot.CumulativePlayerHpLost
        - candidate.Snapshot.CumulativePlayerHpLost;
    Policy.Diagnostics.Info(
        $"[CombatSolver/Test] AFTERIMAGE_ROUTE_FRONTLOADED " +
        $"hp_saved={hpSaved} expanded_nodes_added=0");
    return new AfterimageFrontloading(
        candidate,
        BuildRouteAnnotations(candidate, Root, DisplayNames),
        hpSaved);
}

private static bool IsAfterimageAction(PlanAction action)
    => action.Kind == PlanActionKind.PlayCard
        && string.Equals(action.CardId, "AFTERIMAGE", StringComparison.Ordinal);

private static bool HasDelayedAfterimage(IReadOnlyList<PlanAction> actions)
{
    int turn = int.MinValue;
    bool priorAction = false;
    foreach (PlanAction action in actions)
    {
        if (action.Turn != turn)
        {
            turn = action.Turn;
            priorAction = false;
        }
        if (IsAfterimageAction(action) && priorAction)
            return true;
        priorAction |= !IsAfterimageAction(action);
    }
    return false;
}

private bool FrontloadAvailableAfterimages(
    SimulationSnapshot turnStart,
    PlanAction[] actions,
    int start)
{
    int end = start;
    while (end < actions.Length && actions[end].Turn == turnStart.Turn)
        end++;

    PlanAction[] afterimages = actions[start..end]
        .Where(IsAfterimageAction)
        .ToArray();
    if (afterimages.Length == 0
        || actions.AsSpan(start, afterimages.Length).ToArray().All(IsAfterimageAction))
    {
        return false;
    }

    CombatPredictionSimulator simulator = turnStart.Simulator;
    SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
    SimPlayerCombatState player = simulator.State.GetPlayerCombatState(Replay.Player);
    List<PredictedCard> available = [.. player.Hand.Cards];
    int energyNeeded = 0;
    int starsNeeded = 0;
    foreach (PlanAction action in afterimages)
    {
        PredictedCard? card = FindCardForReplay(available, action);
        if (card == null || !combat.CanPlayCard(simulator, card))
            return false;
        energyNeeded += Math.Max(0, card.GetEnergyCostWithModifiers(simulator, player));
        starsNeeded += Math.Max(0, card.GetStarCostWithModifiers(simulator, player));
        available.Remove(card);
    }
    if (energyNeeded > player.Energy || starsNeeded > player.Stars)
        return false;

    PlanAction[] remainder = actions[start..end]
        .Where(action => !IsAfterimageAction(action))
        .ToArray();
    afterimages.CopyTo(actions, start);
    remainder.CopyTo(actions, start + afterimages.Length);
    return true;
}

private BlockPotionInsertion? TryInsertBlockPotion(
    SearchNode original,
    RouteAnnotations originalAnnotations,
    SolverResultScope resultScope)
{
    if (resultScope != SolverResultScope.SearchCompletion
        || !ForceAllPotionsDisabled
        || Policy.PotionPolicy != SolverPotionPolicy.Smart
        || Policy.PotionStrategy.HasForcedDirectives
        || !SolverInterimResultOrdering.IsCompleteVictory(
            original.ActionCount,
            original.Snapshot.AllEnemiesDead,
            original.Snapshot.PlayerDead,
            original.Snapshot.ProjectedPlayerHp)
        || original.Actions.Any(action => action.Kind == PlanActionKind.UsePotion))
    {
        return null;
    }

    SearchablePotionSlotSnapshot? blockPotion = Root.SearchablePotions
        .Where(potion => string.Equals(potion.PotionId, BlockPotionId, StringComparison.Ordinal)
            && Policy.PotionStrategy.AllowsExplicitUse(
                potion.Slot,
                potion.PotionId,
                SolverPotionPolicy.Smart,
                forceAllDisabled: false))
        .OrderBy(potion => potion.Slot)
        .Cast<SearchablePotionSlotSnapshot?>()
        .FirstOrDefault();
    if (blockPotion is not { } selectedPotion)
        return null;

    (int Turn, int HpLost)? target = originalAnnotations.HpLostByTurn
        .Where(item => item.Value >= SolverWeights.PotionMinimumHpSaved)
        .OrderBy(item => item.Key)
        .Select(item => ((int Turn, int HpLost)?)(item.Key, item.Value))
        .FirstOrDefault();
    if (target is not { } targetTurn)
        return null;

    PlanAction[] originalActions = original.Actions.ToArray();
    int insertionIndex = Array.FindIndex(originalActions, action =>
        action.Turn == targetTurn.Turn
        && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn));
    if (insertionIndex < 0)
        return null;

    PlanAction potionAction = new(
        PlanActionKind.UsePotion,
        targetTurn.Turn,
        TargetIndex: -1,
        TargetCombatId: null,
        TargetName: string.Empty,
        PotionSlot: selectedPotion.Slot,
        PotionId: selectedPotion.PotionId,
        PotionTitle: DisplayNames.Potion(selectedPotion.PotionId));
    PlanAction[] insertedActions = new PlanAction[originalActions.Length + 1];
    Array.Copy(originalActions, 0, insertedActions, 0, insertionIndex);
    insertedActions[insertionIndex] = potionAction;
    Array.Copy(
        originalActions,
        insertionIndex,
        insertedActions,
        insertionIndex + 1,
        originalActions.Length - insertionIndex);

    SearchNode? inserted = ReplayAdjustedRoute(
        insertedActions,
        original.GetTurnSetupChoices(),
        original.GetTurnSetupPlayState(),
        originalAnnotations);
    if (inserted == null)
        return null;
    RouteAnnotations insertedAnnotations = BuildRouteAnnotations(inserted, Root, DisplayNames);
    int hpSaved = original.Snapshot.CumulativePlayerHpLost
        - inserted.Snapshot.CumulativePlayerHpLost;
    bool accepted = SolverInterimResultOrdering.IsCompleteVictory(
            inserted.ActionCount,
            inserted.Snapshot.AllEnemiesDead,
            inserted.Snapshot.PlayerDead,
            inserted.Snapshot.ProjectedPlayerHp)
        && inserted.Snapshot.BoundaryReason != SearchBoundaryReason.UnsupportedEffect
        && inserted.PotionCount == original.PotionCount + 1
        && inserted.Snapshot.ProjectedDeathSaveUseCount
            <= original.Snapshot.ProjectedDeathSaveUseCount
        && hpSaved >= SolverWeights.PotionMinimumHpSaved;
    if (!accepted)
    {
        inserted.Snapshot.ReleaseSimulator();
        return null;
    }

    Policy.Diagnostics.Info(
        $"[CombatSolver/Test] BLOCK_POTION_ROUTE_INSERTED " +
        $"turn={targetTurn.Turn} slot={selectedPotion.Slot} " +
        $"turn_hp_lost={targetTurn.HpLost} hp_saved={hpSaved} " +
        $"expanded_nodes_added=0");
    return new BlockPotionInsertion(
        inserted,
        insertedAnnotations,
        hpSaved);
}

private SearchNode? ReplayAdjustedRoute(
    IReadOnlyList<PlanAction> actions,
    IReadOnlyList<PlanCardChoice> turnSetupChoices,
    ContinuationStamp? turnSetupPlayState,
    RouteAnnotations originalAnnotations,
    bool frontloadAfterimages = false)
{
    SimulationSnapshot rootSnapshot = Replay.IncludeTurnSetup
        ? Replay.ReplayTurnSetup(turnSetupChoices)
        : Replay.Replay([]);
    SearchNode current = new(
        null,
        0,
        rootSnapshot.PotionUseCount,
        rootSnapshot.PotionStrategicCost,
        Replay.StartTurnNumber,
        SearchRouteTraits.None,
        0,
        rootSnapshot.Score,
        rootSnapshot.StateKey,
        rootSnapshot.HasRisk,
        rootSnapshot.BoundaryReason,
        rootSnapshot.PlayerDead
            || rootSnapshot.AllEnemiesDead
            || rootSnapshot.BoundaryReason != SearchBoundaryReason.None,
        null,
        rootSnapshot,
        CombatProgressState.Capture(rootSnapshot),
        TurnSetupChoices: turnSetupChoices,
        TurnSetupPlayState: turnSetupPlayState);

    PlanAction[] replayActions = actions.ToArray();
    bool reordered = false;
    try
    {
        for (int index = 0; index < replayActions.Length; index++)
        {
            // An inserted action may win early or alter later draws/costs. In that case
            // the original action list is no longer a route through this combat state.
            if (current.IsTerminal)
            {
                current.Snapshot.ReleaseSimulator();
                return null;
            }
            if (frontloadAfterimages
                && (index == 0 || replayActions[index - 1].Turn != current.Turn))
            {
                reordered |= FrontloadAvailableAfterimages(
                    current.Snapshot,
                    replayActions,
                    index);
            }
            PlanAction action = replayActions[index];
            if (action.Turn != current.Turn)
            {
                if (frontloadAfterimages)
                {
                    current.Snapshot.ReleaseSimulator();
                    return null;
                }
                throw new InvalidOperationException(
                    $"调整路线的动作回合不连续：action_turn={action.Turn} " +
                    $"state_turn={current.Turn}。");
            }

            if (action.Kind is (PlanActionKind.PlayCard or PlanActionKind.UsePotion)
                && !CanApplyFixedPrefixAction(ExpansionContext, current, action))
            {
                current.Snapshot.ReleaseSimulator();
                return null;
            }

            SearchNode parent = current;
            SimulationSnapshot snapshot = Replay.ReplayAction(parent, action);
            bool terminal = snapshot.PlayerDead
                || snapshot.AllEnemiesDead
                || snapshot.BoundaryReason != SearchBoundaryReason.None;
            bool turnBoundary = terminal || snapshot.Turn > parent.Turn;
            SearchRouteTraits traits = action.Kind == PlanActionKind.UsePotion
                ? ClassifyPotionTraits(parent.Traits, parent.Snapshot, snapshot)
                : turnBoundary
                    ? ClassifyRoundTransitionTraits(parent.Traits, parent.Snapshot, snapshot)
                    : parent.Traits;
            int futureSold = parent.FutureSoldHp;
            TurnOutcome? outcome = null;
            if (turnBoundary)
            {
                SearchNode turnStart = FindTurnStart(parent);
                int hpLost = Math.Max(
                    0,
                    snapshot.CumulativePlayerHpLost
                        - turnStart.Snapshot.CumulativePlayerHpLost);
                int soldThisTurn = Math.Min(
                    hpLost,
                    originalAnnotations.SoldHpByTurn.GetValueOrDefault(action.Turn));
                futureSold += soldThisTurn;
                bool endedByTurn = action.Kind == PlanActionKind.EndTurn
                    || snapshot.Turn > parent.Turn;
                int actualBlock = endedByTurn
                    ? parent.Snapshot.PlayerBlock
                    : snapshot.PlayerBlock;
                int energyLeft = endedByTurn
                    ? parent.Snapshot.Energy
                    : snapshot.Energy;
                outcome = new TurnOutcome(
                    action.Turn,
                    hpLost,
                    Math.Max(
                        0,
                        snapshot.RecoveredPlayerHp
                            - turnStart.Snapshot.RecoveredPlayerHp),
                    checked(AccumulateEnemyHpLost(parent, snapshot)
                        - turnStart.CumulativeEnemyHpLost),
                    soldThisTurn,
                    actualBlock,
                    actualBlock,
                    energyLeft);
            }

            current = new SearchNode(
                action,
                parent.ActionCount + 1,
                snapshot.PotionUseCount,
                snapshot.PotionStrategicCost,
                snapshot.Turn,
                traits,
                futureSold,
                ApplySoldHpPenalty(snapshot.Score, futureSold),
                snapshot.StateKey,
                snapshot.HasRisk,
                snapshot.BoundaryReason,
                terminal,
                parent,
                snapshot,
                turnBoundary
                    ? parent.CombatProgress.Advance(snapshot)
                    : parent.CombatProgress,
                Outcome: outcome)
            {
                CumulativeEnemyHpLost = AccumulateEnemyHpLost(parent, snapshot),
            };
            parent.Snapshot.ReleaseSimulator();
        }
        if (frontloadAfterimages && !reordered)
        {
            current.Snapshot.ReleaseSimulator();
            return null;
        }
        return current;
    }
    catch (InvalidPlannedChoiceBranchException) when (frontloadAfterimages)
    {
        current.Snapshot.ReleaseSimulator();
        return null;
    }
    catch
    {
        current.Snapshot.ReleaseSimulator();
        throw;
    }
}

private SearchNode RefreshReleasedFallback(SearchNode fallback)
{
    if (fallback.Snapshot.HasSimulator)
        return fallback;
    SimulationSnapshot? turnSetupRoot = Replay.IncludeTurnSetup
        ? Replay.ReplayTurnSetup(fallback.GetTurnSetupChoices())
        : null;
    SimulationSnapshot snapshot;
    try
    {
        snapshot = Replay.Replay(
            fallback.Actions,
            turnSetupRoot,
            Replay.StartTurnNumber,
            priorActionCount: 0);
    }
    finally
    {
        turnSetupRoot?.ReleaseSimulator();
    }
    return fallback with
    {
        Score = snapshot.Score,
        StateKey = snapshot.StateKey,
        HasPredictionRisk = snapshot.HasRisk,
        BoundaryReason = snapshot.BoundaryReason,
        IsTerminal = snapshot.PlayerDead
            || snapshot.AllEnemiesDead
            || snapshot.BoundaryReason != SearchBoundaryReason.None,
        Snapshot = snapshot,
    };
}

    private PlanAction WithDisplayNames(PlanAction action)
    => action with
    {
        Choice = action.Choice == null ? null : WithDisplayNames(action.Choice),
        NestedChoices = action.NestedChoices?.Select(WithDisplayNames).ToArray(),
        TurnStartChoices = action.TurnStartChoices?.Select(WithDisplayNames).ToArray(),
    };

private PlanCardChoice WithDisplayNames(PlanCardChoice choice)
    => choice with
    {
        Cards = choice.Cards
            .Select(card => card with
            {
                Title = DisplayNames.Card(card.CardId, card.UpgradeLevel),
            })
            .ToArray(),
    };

internal SolverResult MaterializeSelectedRoute(
        FinalPlanSelection ordering,
        bool onlyDeathRoutesFound,
        SolverResultScope resultScope,
        int candidateSearchedTurnLayers,
        bool candidateTimeBudgetReached,
        bool candidateMemoryNoProgress = false,
        IReadOnlyList<PlanAction>? routeAdoptionActions = null,
        Action? validateLedger = null)
    {
        SearchMeasurement finalMeasurement = Metrics.Performance.Begin();
        FinalPlanCandidate publishedCandidate = ordering.Candidate;
        SearchNode materializedNode = publishedCandidate.Node.Snapshot.HasSimulator
            ? publishedCandidate.Node
            : RefreshReleasedFallback(publishedCandidate.Node);
        RouteAnnotations materializedAnnotations = BuildRouteAnnotations(materializedNode, Root, DisplayNames);
        AfterimageFrontloading? afterimageFrontloading = TryFrontloadAfterimages(
            materializedNode,
            materializedAnnotations,
            resultScope);
        if (afterimageFrontloading != null)
        {
            materializedNode.Snapshot.ReleaseSimulator();
            materializedNode = afterimageFrontloading.Node;
            materializedAnnotations = afterimageFrontloading.Annotations;
        }
        BlockPotionInsertion? blockPotionInsertion = TryInsertBlockPotion(
            materializedNode,
            materializedAnnotations,
            resultScope);
        if (blockPotionInsertion != null)
        {
            materializedNode.Snapshot.ReleaseSimulator();
            materializedNode = blockPotionInsertion.Node;
            materializedAnnotations = blockPotionInsertion.Annotations;
        }
        FinalPlanCandidate selectedCandidate = publishedCandidate with
        {
            Node = materializedNode,
            Snapshot = materializedNode.Snapshot,
            Features = SearchFeatures.Capture(materializedNode),
            FutureSold = materializedNode.FutureSoldHp,
            BattleSold = BattleDamage.SoldHpCommitted + materializedNode.FutureSoldHp,
            PotionCount = materializedNode.PotionCount,
            Score = blockPotionInsertion == null && afterimageFrontloading == null
                ? publishedCandidate.Score
                : materializedNode.Score,
        };
        int potionBranchesRejected = ordering.PotionBranchesRejected;
        int potionHpSaved = blockPotionInsertion?.HpSaved ?? ordering.PotionHpSaved;
        int potionHpRequired = blockPotionInsertion == null
            ? ordering.PotionHpRequired
            : SolverWeights.PotionMinimumHpSaved;
        int annotatedFutureSold = materializedAnnotations.SoldHpByTurn.Values.Sum();
        if (annotatedFutureSold != selectedCandidate.FutureSold)
        {
            throw new InvalidOperationException(
                $"卖血路径状态不一致：节点累计 {selectedCandidate.FutureSold}，逐回合累计 {annotatedFutureSold}。");
        }
        SearchNode best = selectedCandidate.Node with { Score = selectedCandidate.Score };

        SimulationSnapshot finalSnapshot = selectedCandidate.Snapshot;
        RouteAnnotations annotations = materializedAnnotations;
        ContinuationCapture continuationCapture = PrepareContinuationCapture(best);
        int searchedTurns = Math.Max(1, best.Actions
            .Select(action => action.Turn)
            .DefaultIfEmpty(Replay.StartTurnNumber)
            .Max() - Replay.StartTurnNumber + 1);
        SearchBoundaryReason boundary = finalSnapshot.BoundaryReason;
        if (resultScope != SolverResultScope.RouteAdoption)
        {
            // 内存截断是本轮搜索真正停下的原因，优先于收尾候选自带的边界；组合器
            // 只凭这个归类判断成员是否可比，不能因为最后一个候选恰好是终局/异常边界
            // 就把截断成员重新放回整条选优。
            if (candidateMemoryNoProgress)
                boundary = SearchBoundaryReason.MemoryNoProgress;
            else if (boundary == SearchBoundaryReason.None && candidateTimeBudgetReached)
                boundary = SearchBoundaryReason.TimeLimit;
            else if (boundary == SearchBoundaryReason.None && Metrics.Expanded >= Profile.MaxExpandedNodes)
                boundary = SearchBoundaryReason.NodeLimit;
            else if (boundary == SearchBoundaryReason.None
                     && Policy.VerifyIncrementalSearch
                     && candidateSearchedTurnLayers >= SolverWeights.IncrementalVerificationMaxTurns)
                boundary = SearchBoundaryReason.TurnLimit;
        }
        int futureHpLost = finalSnapshot.CumulativePlayerHpLost;
        int futureUnavoidableHpLost = annotations.HpLostByTurn.Sum(item =>
            Math.Max(0, item.Value - annotations.SoldHpByTurn.GetValueOrDefault(item.Key)));
        int battleUnavoidableHpLost = Math.Max(0, BattleDamage.HpLostSoFar - BattleDamage.SoldHpCommitted)
            + futureUnavoidableHpLost;
        ActionRelicTriggerRecorder relicTriggerRecorder = new();
        SearchReplayEvidence replayEvidence = new(best);
        SimulationSnapshot? annotationRoot = Replay.IncludeTurnSetup
            ? Replay.ReplayTurnSetup(best.GetTurnSetupChoices())
            : null;
        SimulationSnapshot annotationReplay;
        bool replayFailed = true;
        try
        {
            annotationReplay = Replay.Replay(best.Actions, annotationRoot, Replay.StartTurnNumber,
                priorActionCount: 0, triggerRecorder: relicTriggerRecorder, replayEvidence: replayEvidence,
                continuationCapture: continuationCapture);
            replayFailed = false;
        }
        finally
        {
            if (replayFailed)
                replayEvidence.Publish(Policy.Diagnostics,
                    Replay.CancellationToken.IsCancellationRequested ? "replay_cancelled" : "replay_failed", relicTriggerRecorder);
            annotationRoot?.ReleaseSimulator();
        }
        if (annotationReplay.StateKey != finalSnapshot.StateKey
            || annotationReplay.PlayerHp != finalSnapshot.PlayerHp
            || annotationReplay.EnemyHp != finalSnapshot.EnemyHp
            || annotationReplay.BoundaryReason != finalSnapshot.BoundaryReason)
        {
            ContinuationStamp expectedStamp = ContinuationStamp.CapturePredicted(
                Replay.Player,
                (CombatPredictionSimulator)finalSnapshot.Simulator,
                finalSnapshot.Turn,
                Replay.Forecast,
                Replay.StartTurnNumber);
            ContinuationStamp replayStamp = ContinuationStamp.CapturePredicted(
                Replay.Player,
                (CombatPredictionSimulator)annotationReplay.Simulator,
                annotationReplay.Turn,
                Replay.Forecast,
                Replay.StartTurnNumber);
            string difference = expectedStamp.DescribeFirstDifference(replayStamp);
            replayEvidence.Publish(Policy.Diagnostics, "final_state_mismatch", relicTriggerRecorder,
                expectedStamp.StateText, replayStamp.StateText);
            annotationReplay.ReleaseSimulator();
            throw new InvalidOperationException(
                $"最终路线的遗物标注回放与选中状态不一致：{difference}；" +
                $"hp={finalSnapshot.PlayerHp}/{annotationReplay.PlayerHp} " +
                $"enemy_hp={finalSnapshot.EnemyHp}/{annotationReplay.EnemyHp} " +
                $"boundary={finalSnapshot.BoundaryReason}/{annotationReplay.BoundaryReason}。");
        }
        RouteAnnotations replayAnnotations = BuildRouteAnnotations(best, Root, DisplayNames, relicTriggerRecorder,
            ((SimulatedCombatState)annotationReplay.Simulator.State.CombatState).KnownEnemies);
        replayEvidence.Publish(Policy.Diagnostics, "selected_route", relicTriggerRecorder);
        annotations = annotations with { KillsAfterAction = replayAnnotations.KillsAfterAction };
        string[] plannedPotionIds = ((SimulatedCombatState)((CombatPredictionSimulator)annotationReplay.Simulator)
            .State.CombatState).PotionUses.Select(use => use.PotionId).ToArray();
        if (plannedPotionIds.Length != selectedCandidate.PotionCount)
        {
            annotationReplay.ReleaseSimulator();
            throw new InvalidOperationException("路线用药数量与回放药水身份不一致。");
        }
        annotationReplay.ReleaseSimulator();
        IReadOnlyList<CachedContinuation> continuations = continuationCapture.Complete();
        IReadOnlyList<PlanAction> annotatedActions = resultScope == SolverResultScope.RouteAdoption
            && routeAdoptionActions != null
                ? routeAdoptionActions
                : best.Actions
                    .Select((action, actionIndex) => WithDisplayNames(action) with
                    {
                        RelicEffects = relicTriggerRecorder.ForAction(actionIndex)
                            .Select(trigger => new PlanRelicEffect(
                                trigger.RelicId,
                                DisplayNames.Relic(trigger.RelicId),
                                trigger.Summary))
                            .ToArray(),
                    })
                    .ToArray();
        Metrics.RefreshReplayCounters(Replay.Accounting);
        Metrics.Performance.End(SearchMetricPhase.FinalSelection, finalMeasurement);
        Metrics.WorkPacer?.ObserveGcPause();
        Stopwatch.Stop();
        long workerAllocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - allocatedBytesAtStart
            + Metrics.OffThreadAllocatedBytes;
        int gen0Collections = GC.CollectionCount(0) - gen0AtStart;
        int gen1Collections = GC.CollectionCount(1) - gen1AtStart;
        int gen2Collections = GC.CollectionCount(2) - gen2AtStart;
        TimeSpan gcPauseDuration = GC.GetTotalPauseDuration() - gcPauseAtStart;
        // 必须在返回前把节点链和模拟器图压平成运行时真正需要的数据。
        // Coordinator 会在深化期间保留短搜结果；这里若返回 SearchNode/SimulationSnapshot，
        // 短搜的全部父链和每步模拟器都会成为长寿命 GC 根。
        SelectedSearchPlan selectedPlan = new(
            annotatedActions,
            best.ActionCount,
            best.Score);
        SolverSnapshot selectedSnapshot = new(
            finalSnapshot.HasRisk,
            finalSnapshot.PlayerDead,
            finalSnapshot.AllEnemiesDead,
            finalSnapshot.PlayerHp,
            finalSnapshot.PlayerMaxHp,
            finalSnapshot.CumulativePlayerHpLost,
            finalSnapshot.RecoveredPlayerHp,
            finalSnapshot.DeathSaveRelicHpRestored,
            finalSnapshot.LongTermResourceValue,
            finalSnapshot.AngerCopiesGenerated,
            finalSnapshot.ProjectedPlayerHp,
            finalSnapshot.PlayerBlock,
            finalSnapshot.EnemyHp,
            finalSnapshot.AliveEnemyCount,
            finalSnapshot.Energy,
            finalSnapshot.Stars,
            finalSnapshot.HandCount,
            finalSnapshot.OutstandingStolenResource,
            finalSnapshot.Turn,
            finalSnapshot.ShufflesCrossed,
            finalSnapshot.BoundaryReason,
            finalSnapshot.PredictionGaps.ToArray())
        {
            DeathSavePotionHpRestored = finalSnapshot.DeathSavePotionHpRestored,
            DeathSaveUseCount = finalSnapshot.DeathSaveUseCount,
            ProjectedDeathSaveUseCount = finalSnapshot.ProjectedDeathSaveUseCount,
            GrowthHpCredit = finalSnapshot.GrowthHpCredit,
            RelicCounters = finalSnapshot.RelicCounters,
            GrowthRewards = finalSnapshot.GrowthRewards,
            UnrecoveredGold = finalSnapshot.UnrecoveredGold,
            UnrecoveredCards = finalSnapshot.UnrecoveredCards,
        };
        SolverResult result = new()
        {
            ResultScope = resultScope,
            DeterministicBlockPotionInserted = blockPotionInsertion != null,
            TotalSearchElapsed = Stopwatch.Elapsed,
            TotalWorkerAllocatedBytes = workerAllocatedBytes,
            TotalGen0Collections = gen0Collections,
            TotalGen1Collections = gen1Collections,
            TotalGen2Collections = gen2Collections,
            TotalGcPauseDuration = gcPauseDuration,
            ForkMetric = Metrics.Performance.Snapshot(SearchMetricPhase.Fork),
            ActionMetric = Metrics.Performance.Snapshot(SearchMetricPhase.Action),
            ExecutionChoiceResumeMetric = Metrics.Performance.Snapshot(SearchMetricPhase.ExecutionChoiceResume),
            CardExecutionMetric = Metrics.Performance.Snapshot(SearchMetricPhase.CardExecution),
            CardPostProcessingMetric = Metrics.Performance.Snapshot(SearchMetricPhase.CardPostProcessing),
            PotionExecutionMetric = Metrics.Performance.Snapshot(SearchMetricPhase.PotionExecution),
            RoundAdvanceMetric = Metrics.Performance.Snapshot(SearchMetricPhase.RoundAdvance),
            RoundPlayerEndMetric = Metrics.Performance.Snapshot(SearchMetricPhase.RoundPlayerEnd),
            RoundEndSimulationMetric = Metrics.Performance.Snapshot(SearchMetricPhase.RoundEndSimulation),
            RoundFlushMetric = Metrics.Performance.Snapshot(SearchMetricPhase.RoundFlush),
            RoundPlayerEndPowersMetric = Metrics.Performance.Snapshot(SearchMetricPhase.RoundPlayerEndPowers),
            RoundEnemyTurnMetric = Metrics.Performance.Snapshot(SearchMetricPhase.RoundEnemyTurn),
            RoundEnemyStartMetric = Metrics.Performance.Snapshot(SearchMetricPhase.RoundEnemyStart),
            RoundEnemyMovesMetric = Metrics.Performance.Snapshot(SearchMetricPhase.RoundEnemyMoves),
            RoundEnemyEndPowersMetric = Metrics.Performance.Snapshot(SearchMetricPhase.RoundEnemyEndPowers),
            RoundPlayerStartMetric = Metrics.Performance.Snapshot(SearchMetricPhase.RoundPlayerStart),
            RoundDrawMetric = Metrics.Performance.Snapshot(SearchMetricPhase.RoundDraw),
            SnapshotMetric = Metrics.Performance.Snapshot(SearchMetricPhase.Snapshot),
            ThreatProjectionMetric = Metrics.Performance.Snapshot(SearchMetricPhase.ThreatProjection),
            FingerprintMetric = Metrics.Performance.Snapshot(SearchMetricPhase.Fingerprint),
            ProjectedShuffleMetric = Metrics.Performance.Snapshot(SearchMetricPhase.ProjectedShuffle),
            PileFingerprintMetric = Metrics.Performance.Snapshot(SearchMetricPhase.PileFingerprint),
            PileFingerprintMissMetric = Metrics.Performance.Snapshot(SearchMetricPhase.PileFingerprintMiss),
            CardFingerprintMissMetric = Metrics.Performance.Snapshot(SearchMetricPhase.CardFingerprintMiss),
            CombatFingerprintMetric = Metrics.Performance.Snapshot(SearchMetricPhase.CombatFingerprint),
            PruneMetric = Metrics.Performance.Snapshot(SearchMetricPhase.Prune),
            FinalSelectionMetric = Metrics.Performance.Snapshot(SearchMetricPhase.FinalSelection),
            StartTurnNumber = Replay.StartTurnNumber,
            TranspositionCount = Metrics.TranspositionCount,
            ExpandedTranspositionCount = Metrics.ExpandedTranspositionCount,
            TranspositionLimitBypasses = Metrics.TranspositionLimitBypasses,
            StandPatCacheCount = Metrics.StandPatCacheCount,
            ThreatProjectionCacheCount = Metrics.ThreatProjectionCacheCount,
            CoverageCacheCount = Metrics.CoverageCacheCount,
            TurnSetupChoices = best.GetTurnSetupChoices().Select(WithDisplayNames).ToArray(),
            TurnSetupPlayState = best.GetTurnSetupPlayState(),
            BestNode = selectedPlan,
            PotionUses = ((SimulatedCombatState)finalSnapshot.Simulator.State.CombatState)
                .PotionUses.ToArray(),
            Snapshot = selectedSnapshot,
            Forecast = Replay.Forecast,
            ExpandedNodes = Metrics.Expanded,
            TotalExpandedNodes = Metrics.Expanded,
            NoveltySearch = Metrics.NoveltySearch,
            DominatedActionsPruned = Metrics.DominatedActionsPruned,
            TopQueueActionsDropped = Metrics.TopQueueActionsDropped,
            ActionAdmissionRepresentativesProtected = Metrics.ActionAdmissionRepresentativesProtected,
            DuplicateCardBranchesPruned = Metrics.DuplicateCardBranchesPruned,
            ChoiceBranchesEvaluated = Metrics.ChoiceBranchesEvaluated,
            TotalChoiceBranchesEvaluated = Metrics.ChoiceBranchesEvaluated,
            ChoiceReplayAttempts = Metrics.ChoiceReplayAttempts,
            ChoiceReplayBudgetExhaustions = Metrics.ChoiceReplayBudgetExhaustions,
            ChoiceBranchesDroppedByBudget = Metrics.ChoiceBranchesDroppedByBudget,
            ShuffleBranchesPruned = Metrics.ShuffleBranchesPruned,
            SoldHpBranchesPruned = Metrics.SoldHpBranchesPruned,
            HpInvestmentBranchesProtected = Metrics.HpInvestmentBranchesProtected,
            ReplayCount = Metrics.ReplayCount,
            ForkCount = Metrics.ForkCount,
            RoundReplayPrefixCaptures = Metrics.RoundReplayPrefixCaptures,
            RoundReplayPrefixReuses = Metrics.RoundReplayPrefixReuses,
            ExecutionChoiceCaptures = Metrics.ExecutionChoiceCaptures,
            ExecutionChoiceReuses = Metrics.ExecutionChoiceReuses,
            CardChoicePrefixAttempts = Metrics.CardChoicePrefixAttempts,
            CardChoicePrefixCaptures = Metrics.CardChoicePrefixCaptures,
            CardChoicePrefixReuses = Metrics.CardChoicePrefixReuses,
            CardChoicePrefixFallbacks = Metrics.CardChoicePrefixFallbacks,
            PotionChoicePrefixForks = Metrics.PotionChoicePrefixForks,
            PotionChoicePrefixCaptures = Metrics.PotionChoicePrefixCaptures,
            PotionChoicePrefixReuses = Metrics.PotionChoicePrefixReuses,
            PotionChoicePrefixFallbacks = Metrics.PotionChoicePrefixFallbacks,
            TransitionCount = Metrics.TransitionCount,
            TotalTransitionCount = Metrics.TransitionCount,
            ReusedNodeSnapshots = Metrics.ReusedNodeSnapshots,
            TranspositionBranchesPruned = Metrics.TranspositionBranchesPruned,
            RepeatableNoProgressBranchesPruned = Metrics.RepeatableNoProgressBranchesPruned,
            CycleShapesDetected = Metrics.CycleShapesDetected,
            CycleProbeContinuationsExpanded = Metrics.CycleProbeContinuationsExpanded,
            CycleCandidatesProtected = Metrics.CycleCandidatesProtected,
            CycleContinuationsStopped = Metrics.CycleContinuationsStopped,
            CycleReplayAttempts = Metrics.CycleReplayAttempts,
            CycleReplayActions = Metrics.CycleReplayActions,
            TotalCycleReplayActions = Metrics.CycleReplayActions,
            CycleReplayVictories = Metrics.CycleReplayVictories,
            CycleReplayContinuations = Metrics.CycleReplayContinuations,
            CycleStoppedUnproductive = Metrics.CycleStoppedUnproductive,
            CycleStoppedRepetitionBudget = Metrics.CycleStoppedRepetitionBudget,
            CycleStoppedFamilyBudget = Metrics.CycleStoppedFamilyBudget,
            CycleStoppedExitBudget = Metrics.CycleStoppedExitBudget,
            TurnLayerBudgetStops = Metrics.TurnLayerBudgetStops,
            TurnLayerTimeBudgetStops = Metrics.TurnLayerTimeBudgetStops,
            TurnLayerNodeBudgetStops = Metrics.TurnLayerNodeBudgetStops,

            CycleRegionsDetected = Metrics.CycleRegionsDetected,
            CycleRegionCandidatesConsidered = Metrics.CycleRegionCandidatesConsidered,
            CycleRegionCandidatesAdmitted = Metrics.CycleRegionCandidatesAdmitted,
            CycleRegionCandidatesDropped = Metrics.CycleRegionCandidatesDropped,
            CycleRegionProgressEpochs = Metrics.CycleRegionProgressEpochs,
            CycleRegionProbeCandidatesAdmitted =
                Metrics.CycleRegionProbeCandidatesAdmitted,
            CycleRegionProgressCandidatesAdmitted =
                Metrics.CycleRegionProgressCandidatesAdmitted,
            CycleRegionMaxActionFamilies = Metrics.CycleRegionMaxActionFamilies,
            OrderedMutationCandidatesAdmitted =
                Metrics.OrderedMutationPortfolioNodesConsumed,
            OrderedMutationLeaseExpiredBudget =
                Metrics.OrderedMutationLeaseExpiredBudget,
            OrderedMutationOrdinaryFallbacks =
                Metrics.OrderedMutationOrdinaryFallbacks,
            OrderedMutationColdAtomicCommitted =
                Metrics.OrderedMutationColdAtomicCommitted,
            OrderedMutationColdAtomicRejected =
                Metrics.OrderedMutationColdAtomicRejected,
            CrossTurnCandidatesProtected = Metrics.CrossTurnCandidatesProtected,
            CrossTurnContinuationsStopped = Metrics.CrossTurnContinuationsStopped,
            PrimaryIncumbentBranchesPruned = Metrics.PrimaryIncumbentBranchesPruned,
            PrimaryIncumbentCertifiedHealingBoundBranchesPruned =
                Metrics.PrimaryIncumbentCertifiedHealingBoundBranchesPruned,
            PrimaryIncumbentUpdates = Metrics.PrimaryIncumbentUpdates,
            StandPatProbes = Metrics.StandPatProbes,
            ParallelExpansionWaves = Metrics.ParallelExpansionWaves,
            ParallelExpansionWorkItems = Metrics.ParallelExpansionWorkItems,
            MaxParallelExpansionConcurrency = Metrics.MaxParallelExpansionConcurrency,
            ParallelActionReplayWaves = Metrics.ParallelActionReplayWaves,
            ParallelActionReplayWorkItems = Metrics.ParallelActionReplayWorkItems,
            MaxParallelActionReplayConcurrency = Metrics.MaxParallelActionReplayConcurrency,
            DeferredRoundChoiceActions = Metrics.DeferredRoundChoiceActions,
            DeferredRoundChoiceLayerWidthTotal = Metrics.DeferredRoundChoiceLayerWidthTotal,
            MaxDeferredRoundChoiceLayerWidth = Metrics.MaxDeferredRoundChoiceLayerWidth,
            DeferredRoundChoiceFiniteQuotaFallbacks =
                Metrics.DeferredRoundChoiceFiniteQuotaFallbacks,
            DeferredRoundChoiceFinitePrimaryLayers =
                Metrics.DeferredRoundChoiceFinitePrimaryLayers,
            DeferredRoundChoiceFinitePendingFallbacks =
                Metrics.DeferredRoundChoiceFinitePendingFallbacks,
            ParallelRoundChoiceReplayWaves = Metrics.ParallelRoundChoiceReplayWaves,
            ParallelRoundChoiceReplayWorkItems = Metrics.ParallelRoundChoiceReplayWorkItems,
            MaxParallelRoundChoiceReplayConcurrency =
                Metrics.MaxParallelRoundChoiceReplayConcurrency,
            NodeLimitSnapshotsReleased = Metrics.NodeLimitSnapshotsReleased,
            TransitionCacheHits = 0,
            WorkerAllocatedBytes = workerAllocatedBytes,
            Gen0Collections = gen0Collections,
            Gen1Collections = gen1Collections,
            Gen2Collections = gen2Collections,
            GcPauseDuration = gcPauseDuration,
            MaxObservedGcPause = Metrics.MaxObservedGcPause,
            WorkerYieldCount = Metrics.WorkerYieldCount,
            FrameRecoveryWaitCount = Metrics.FrameRecoveryWaitCount,
            FrameRecoveryWaitDuration = Metrics.FrameRecoveryWaitDuration,
            SearchedTurns = searchedTurns,
            BoundaryReason = boundary,
            UnavoidableHpLost = battleUnavoidableHpLost,
            SoldHp = selectedCandidate.BattleSold,
            FutureSoldHp = selectedCandidate.FutureSold,
            BattleHpLostSoFar = BattleDamage.HpLostSoFar,
            BattleHpRecoveredOrGainedSoFar = BattleDamage.HpRecoveredOrGainedSoFar,
            ProjectedBattleHpLost = BattleDamage.HpLostSoFar + futureHpLost,
            BattlePotionsUsedSoFar = BattleDamage.PotionsUsedSoFar,
            BattlePotionIdsUsedSoFar = BattleDamage.PotionIdsUsedSoFar,
            PlannedPotionIds = plannedPotionIds,
            PotionRewardOutlook = Root.PotionRewardOutlook,
            PotionCount = selectedCandidate.PotionCount,
            ExplicitPotionCount = annotatedActions.Count(action =>
                action.Kind == PlanActionKind.UsePotion),
            PotionHpSaved = potionHpSaved,
            PotionHpRequired = potionHpRequired,
            PotionBranchesRejected = potionBranchesRejected,
            TheftPolicy = TheftPolicy,
            OutstandingStolenResource = finalSnapshot.OutstandingStolenResource,
            SoldHpByTurn = annotations.SoldHpByTurn,
            HpLostByTurn = annotations.HpLostByTurn,
            HpRecoveredByTurn = annotations.HpRecoveredByTurn,
            PostCombatRelicHeal = SolverInterimResultOrdering.IsCompleteVictory(
                best.ActionCount,
                finalSnapshot.AllEnemiesDead,
                finalSnapshot.PlayerDead,
                finalSnapshot.ProjectedPlayerHp)
                ? Root.PostCombatRelicHeal.HealFor(
                    finalSnapshot.PlayerHp,
                    finalSnapshot.PlayerMaxHp)
                : 0,
            EnemyHpLostByTurn = annotations.EnemyHpLostByTurn,
            MaxBlockByTurn = annotations.MaxBlockByTurn,
            ActualBlockByTurn = annotations.ActualBlockByTurn,
            EnergyLeftByTurn = annotations.EnergyLeftByTurn,
            PotionCountByTurn = annotations.PotionCountByTurn,
            PotionStrategicCostByTurn = annotations.PotionStrategicCostByTurn,
            KillsAfterAction = annotations.KillsAfterAction,
            CombatEndedTurn = annotations.CombatEndedTurn,
            DeathTurn = annotations.DeathTurn,
            OnlyDeathRoutesFound = onlyDeathRoutesFound,
            IsActEndingBoss = IsActEndingBoss,
            BossHpRelief = BossHpRelief,
            Elapsed = Stopwatch.Elapsed,
            Continuations = resultScope == SolverResultScope.CurrentTurnAdoption ? [] : continuations,
        };
        finalSnapshot.ReleaseSimulator();
        result.AssertCompleteTurnOutcomes();
        return result;
    }

}
