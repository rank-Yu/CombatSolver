using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;


internal sealed partial class CombatBeamSolver
{
    private readonly record struct StandPatEvaluation(
        bool AllEnemiesDead,
        int DelayedDamage,
        int ProjectedPlayerHp,
        int ResourceValue);

    internal readonly record struct CanonicalCycleFamilyKey(
        int Turn,
        int PrimitivePeriodActions,
        StateFingerprint SequenceKey);

    // A region deliberately excludes the exact action sequence. Exact sequence identity is
    // still used by CycleFamilyLedger for delayed-benefit leases, while this coarser key caps
    // permutations that revisit the same semantic recurrence in one turn.
    internal readonly record struct CycleRegionKey(
        int Turn,
        StateFingerprint ShapeKey);

    private sealed class CycleFamilyLedgerEntry
    {
        public HashSet<int> AdmittedActionCounts { get; } = [];
        // Family keys intentionally omit mutable exact state, so the first sibling admitted at
        // one depth is not necessarily the sibling which proves progress. Track that evidence
        // independently while still bounding it by the admitted action-depth set.
        public HashSet<int>? ImprovementEvidenceActionCounts;
        public int ProbeStarts;
        public int ProbeExpandedNodes;
        public byte EarnedImprovementEpoch;
        public byte ActiveImprovementEpoch;
        public byte RequestedImprovementEpoch;
    }

    private sealed class CycleRegionLedgerEntry
    {
        public int AdmittedNodes;
        public int ProbeAdmittedNodes;
        public int ProgressAdmittedNodes;
        // Identity only: an inactive region must not keep an obsolete route's parent chain
        // alive. Treat each handle as immutable; provisional ledgers may share it, but an
        // updated witness always receives a new handle rather than calling SetTarget.
        public WeakReference<SearchNode>? ProgressContinuationNode;
        public int ProgressActionsRemaining;
        public int DroppedNodes;
        public int ProgressEpochs;
        public bool HasObservation;
        public EnemyDurabilityVector EnemyDurabilityFloor;
        public long BestHealthRisk;
        public int BestProjectedPlayerHp;
        public int BestUsefulBlock;
        public long BestSetupValue;
    }

    private sealed class SearchRunContext(
        bool measurePhasePerformance,
        SearchFramePressureSignal framePressureSignal)
    {
        public NoveltySearchRun? Novelty;
        public Guid PathDiagnosticsSolverId;
        public int PathDiagnosticsBoundaryId;
        public long RoutingChoiceSummaryBuilds;
        public long RoutingChoiceSummaryHits;
        public long RoutingChoiceSummaryBypasses;
        public long PowerValuationCandidates;
        public long PowerFrontierEvaluations;
        public int PowerCommitmentsCreated;
        public int PowerCommitmentsAdmitted;
        public int PowerCommitmentsExpired;
        public int PowerCommitmentsRealized;
        public int PowerCommitmentSeatsPeak;
        public readonly SearchReplayAccounting Replay = new(measurePhasePerformance, framePressureSignal);
        public SearchPerformanceMetrics Performance => Replay.Performance;
        public SearchWorkPacer WorkPacer => Replay.WorkPacer
            ?? throw new InvalidOperationException("Search run has no frame pressure signal.");
        public readonly OwnedExpansionBatch<SimulationSnapshot, RawCardCandidate, SearchNode>.Pool
            ExpansionBatchPool = new(static snapshot => snapshot.ReleaseSimulator());
        public readonly SnapshotListBuffer<PredictedCard> SnapshotLiveCards = new();
        public ParallelExpansionExecutor? ActiveParallelExpansion;
        public Dictionary<StateFingerprint, TranspositionFrontier> Transpositions = [];
        public Dictionary<StateFingerprint, TranspositionFrontier> ExpandedTranspositions = [];
        /// <summary>诊断计数：因为合并条目上限而没有写入的新转置状态数。</summary>
        public int TranspositionLimitBypasses;
        public readonly TranspositionCapDiagnostics TranspositionDiagnostics = new();
        public Dictionary<StateFingerprint, StandPatEvaluation> StandPatCache = [];
        // Only the coordinator owns a prune checkpoint; probe lanes never receive it.
        public Action<long>? EnsurePruneMemory;
        public Action<string>? CheckpointPruneMetadata;
        public long StandPatProbeAllocatedHighWater;
        public long StandPatBatchAllocatedBytes;

        public readonly PotionStrategicCostLookup PotionStrategicCosts = new();
        public Dictionary<(StateFingerprint State, int RoundIndex), ThreatProjection> ThreatProjectionCache = [];
        public Dictionary<PredictionRiskSignature, CoverageSummary> CoverageCache = [];
        // This ledger is search semantics rather than a rebuildable cache. In particular, a
        // memory-pressure checkpoint must not let an already sampled cycle family buy fresh
        // work merely because the transposition tables were rebuilt.
        public readonly Dictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry>
            CycleFamilyLedger = [];
        public readonly HashSet<CycleExitProbeTicketKey> ExpandedCycleProbeTickets = [];
        public readonly Dictionary<CycleRegionKey, CycleRegionLedgerEntry>
            CycleRegionLedger = [];
        // Region run budgets are partitioned by combat turn. A combinatorial cycle in one
        // turn cannot spend the admission reserve needed to inspect a later turn, while all
        // recurrent shapes within the same turn still share one deterministic hard cap.
        public readonly Dictionary<int, int> CycleRegionAdmissionsByTurn = [];
        public readonly Dictionary<int, int> CycleRegionProbeAdmissionsByTurn = [];
        public readonly Dictionary<int, int> CycleRegionMaxProgressEpochsByTurn = [];
        // Cycle-family work is budgeted independently for each combat turn. A large same-turn
        // recurrence cannot consume the reserve required to inspect a later turn, while the
        // dictionaries remain bounded by the number of turns that actually earned work.
        public readonly Dictionary<int, int> CycleFamilyDepthsConsumedByTurn = [];
        public readonly Dictionary<int, int> CycleProbeExpandedNodesConsumedByTurn = [];
        // Scheduling semantics, not rebuildable caches. Every derived lane is charged to the
        // collision root's hard budget; the narrower ledgers only order roots, original lanes,
        // and current derived lanes fairly after checkpoint rebuilds.
        public readonly Dictionary<StateFingerprint, int>
            OrderedMutationAdmissionsByRootLease = [];
        public readonly Dictionary<StateFingerprint, int>
            OrderedMutationAdmissionsByInitialLease = [];
        public readonly Dictionary<StateFingerprint, int> OrderedMutationAdmissionsByLease = [];
        public readonly Dictionary<OrderedMutationHandoffSourceLedgerKey, int>
            OrderedMutationHandoffAdmissionsByInitialAndSource = [];
        public readonly Dictionary<SearchNode, OrderedMutationHandoffSourceLedgerKey>
            PendingOrderedMutationHandoffSourceByNode = new(
                ReferenceEqualityComparer.Instance);
        // Naturally ranked inherited leases still pay the ordered admission ledger. If that
        // payment cannot commit, only the scheduling lease expires; the ordinary route remains
        // eligible for the normal cycle-region coordinator.
        public readonly HashSet<SearchNode> PendingOrderedMutationOrdinaryFallbackNodes = new(
            ReferenceEqualityComparer.Instance);
        public int CycleRegionPortfolioNodesConsumed;
        public int OrderedMutationPortfolioNodesConsumed;
        public int OrderedMutationLeaseExpiredBudget;
        public int OrderedMutationOrdinaryFallbacks;
        public int OrderedMutationColdAtomicCommitted;
        public int OrderedMutationColdAtomicRejected;
        public int Expanded;
        public int DominatedActionsPruned;
        public int TopQueueActionsDropped;
        public int ActionAdmissionRepresentativesProtected;
        public int DuplicateCardBranchesPruned;
        public int ShuffleBranchesPruned;
        public int SoldHpBranchesPruned;
        public int HpInvestmentBranchesProtected;
        public int ChoiceReplayAttempts { get => Replay.ChoiceReplayAttempts; set => Replay.ChoiceReplayAttempts = value; }
        public int ChoiceBranchesEvaluated { get => Replay.ChoiceBranchesEvaluated; set => Replay.ChoiceBranchesEvaluated = value; }
        public int ChoiceReplayBudgetExhaustions { get => Replay.ChoiceReplayBudgetExhaustions; set => Replay.ChoiceReplayBudgetExhaustions = value; }
        public int ChoiceBranchesDroppedByBudget { get => Replay.ChoiceBranchesDroppedByBudget; set => Replay.ChoiceBranchesDroppedByBudget = value; }
        public int ReplayCount { get => Replay.ReplayCount; set => Replay.ReplayCount = value; }
        public int ForkCount { get => Replay.ForkCount; set => Replay.ForkCount = value; }
        public int RoundReplayPrefixCaptures { get => Replay.RoundReplayPrefixCaptures; set => Replay.RoundReplayPrefixCaptures = value; }
        public int RoundReplayPrefixReuses { get => Replay.RoundReplayPrefixReuses; set => Replay.RoundReplayPrefixReuses = value; }
        public int ExecutionChoiceCaptures { get => Replay.ExecutionChoiceCaptures; set => Replay.ExecutionChoiceCaptures = value; }
        public int ExecutionChoiceReuses { get => Replay.ExecutionChoiceReuses; set => Replay.ExecutionChoiceReuses = value; }
        public int CardChoicePrefixAttempts { get => Replay.CardChoicePrefixAttempts; set => Replay.CardChoicePrefixAttempts = value; }
        public int CardChoicePrefixCaptures { get => Replay.CardChoicePrefixCaptures; set => Replay.CardChoicePrefixCaptures = value; }
        public int CardChoicePrefixReuses { get => Replay.CardChoicePrefixReuses; set => Replay.CardChoicePrefixReuses = value; }
        public int CardChoicePrefixFallbacks { get => Replay.CardChoicePrefixFallbacks; set => Replay.CardChoicePrefixFallbacks = value; }
        public int PotionChoicePrefixForks { get => Replay.PotionChoicePrefixForks; set => Replay.PotionChoicePrefixForks = value; }
        public int PotionChoicePrefixCaptures { get => Replay.PotionChoicePrefixCaptures; set => Replay.PotionChoicePrefixCaptures = value; }
        public int PotionChoicePrefixReuses { get => Replay.PotionChoicePrefixReuses; set => Replay.PotionChoicePrefixReuses = value; }
        public int PotionChoicePrefixFallbacks { get => Replay.PotionChoicePrefixFallbacks; set => Replay.PotionChoicePrefixFallbacks = value; }
        public bool HasObservedPostDrawRoundChoice { get => Replay.HasObservedPostDrawRoundChoice; set => Replay.HasObservedPostDrawRoundChoice = value; }
        public HashSet<string>? ObservedHandDrawShuffleChoiceSources { get => Replay.ObservedHandDrawShuffleChoiceSources; set => Replay.ObservedHandDrawShuffleChoiceSources = value; }
        public int TransitionCount { get => Replay.TransitionCount; set => Replay.TransitionCount = value; }
        public int ReusedNodeSnapshots;
        public int TranspositionBranchesPruned;
        public int RepeatableNoProgressBranchesPruned;
        public int CycleShapesDetected;
        public int CycleProbeContinuationsExpanded;
        public int CycleCandidatesProtected;
        public readonly HashSet<CycleRegionKey> CycleReplayRegions = [];
        public int CycleReplayAttempts;
        public int CycleReplayActions;
        public int CycleReplayVictories;
        public int CycleReplayContinuations;
        public int CycleContinuationsStopped;
        public int CycleStoppedUnproductive;
        public int CycleStoppedRepetitionBudget;
        public int CycleStoppedFamilyBudget;
        public int CycleStoppedExitBudget;
        public int TurnLayerBudgetStops;
        public int TurnLayerTimeBudgetStops;
        public int TurnLayerNodeBudgetStops;
        public int CycleRegionsDetected;
        public int CycleRegionCandidatesConsidered;
        public int CycleRegionCandidatesAdmitted;
        public int CycleRegionCandidatesDropped;
        public int CycleRegionProgressEpochs;
        public int CycleRegionProbeCandidatesAdmitted;
        public int CycleRegionProgressCandidatesAdmitted;
        public int CycleRegionMaxActionFamilies;
        public int CrossTurnCandidatesProtected;
        public int CrossTurnContinuationsStopped;
        public int PrimaryIncumbentBranchesPruned;
        public int PrimaryIncumbentCertifiedHealingBoundBranchesPruned;
        public int PrimaryIncumbentUpdates;
        public int StandPatProbes;
        public long OffThreadAllocatedBytes;
        public int ParallelExpansionWaves;
        public int ParallelExpansionWorkItems;
        public int MaxParallelExpansionConcurrency;
        public int ParallelActionReplayWaves;
        public int ParallelActionReplayWorkItems;
        public int MaxParallelActionReplayConcurrency;
        public int DeferredRoundChoiceActions;
        public int DeferredRoundChoiceLayerWidthTotal;
        public int MaxDeferredRoundChoiceLayerWidth;
        public int DeferredRoundChoiceFiniteQuotaFallbacks;
        public int DeferredRoundChoiceFinitePrimaryLayers;
        public int DeferredRoundChoiceFinitePendingFallbacks;
        // Choice preparation/replay/continuation jobs on the same fixed expansion lanes.
        public int ParallelRoundChoiceReplayWaves = 0;
        public int ParallelRoundChoiceReplayWorkItems = 0;
        public int MaxParallelRoundChoiceReplayConcurrency = 0;
        public int NodeLimitSnapshotsReleased;
        public int InitialPersistentBuffValue { get => Replay.InitialPersistentBuffValue; set => Replay.InitialPersistentBuffValue = value; }
        public int InitialEnemyStrengthSuppression { get => Replay.InitialEnemyStrengthSuppression; set => Replay.InitialEnemyStrengthSuppression = value; }
        public int InitialEnemyWeakTurns { get => Replay.InitialEnemyWeakTurns; set => Replay.InitialEnemyWeakTurns = value; }
        public int InitialRetainedAttackValue { get => Replay.InitialRetainedAttackValue; set => Replay.InitialRetainedAttackValue = value; }

        public SearchMaterializationMetrics CaptureMaterializationMetrics()
            => new(Performance)
            {
                WorkPacer = WorkPacer,
                Expanded = Expanded,
                TransitionCount = TransitionCount,
                ReplayCount = ReplayCount,
                ForkCount = ForkCount,
                ActionAdmissionRepresentativesProtected = ActionAdmissionRepresentativesProtected,
                CardChoicePrefixAttempts = CardChoicePrefixAttempts,
                CardChoicePrefixCaptures = CardChoicePrefixCaptures,
                CardChoicePrefixFallbacks = CardChoicePrefixFallbacks,
                CardChoicePrefixReuses = CardChoicePrefixReuses,
                ChoiceBranchesDroppedByBudget = ChoiceBranchesDroppedByBudget,
                ChoiceBranchesEvaluated = ChoiceBranchesEvaluated,
                ChoiceReplayAttempts = ChoiceReplayAttempts,
                ChoiceReplayBudgetExhaustions = ChoiceReplayBudgetExhaustions,
                CrossTurnCandidatesProtected = CrossTurnCandidatesProtected,
                CrossTurnContinuationsStopped = CrossTurnContinuationsStopped,
                CycleCandidatesProtected = CycleCandidatesProtected,
                CycleContinuationsStopped = CycleContinuationsStopped,
                CycleProbeContinuationsExpanded = CycleProbeContinuationsExpanded,
                CycleRegionCandidatesAdmitted = CycleRegionCandidatesAdmitted,
                CycleRegionCandidatesConsidered = CycleRegionCandidatesConsidered,
                CycleRegionCandidatesDropped = CycleRegionCandidatesDropped,
                CycleRegionMaxActionFamilies = CycleRegionMaxActionFamilies,
                CycleRegionProbeCandidatesAdmitted = CycleRegionProbeCandidatesAdmitted,
                CycleRegionProgressCandidatesAdmitted = CycleRegionProgressCandidatesAdmitted,
                CycleRegionProgressEpochs = CycleRegionProgressEpochs,
                CycleRegionsDetected = CycleRegionsDetected,
                CycleReplayActions = CycleReplayActions,
                CycleReplayAttempts = CycleReplayAttempts,
                CycleReplayContinuations = CycleReplayContinuations,
                CycleReplayVictories = CycleReplayVictories,
                CycleShapesDetected = CycleShapesDetected,
                CycleStoppedExitBudget = CycleStoppedExitBudget,
                CycleStoppedFamilyBudget = CycleStoppedFamilyBudget,
                CycleStoppedRepetitionBudget = CycleStoppedRepetitionBudget,
                CycleStoppedUnproductive = CycleStoppedUnproductive,
                DeferredRoundChoiceActions = DeferredRoundChoiceActions,
                DeferredRoundChoiceFinitePendingFallbacks = DeferredRoundChoiceFinitePendingFallbacks,
                DeferredRoundChoiceFinitePrimaryLayers = DeferredRoundChoiceFinitePrimaryLayers,
                DeferredRoundChoiceFiniteQuotaFallbacks = DeferredRoundChoiceFiniteQuotaFallbacks,
                DeferredRoundChoiceLayerWidthTotal = DeferredRoundChoiceLayerWidthTotal,
                DominatedActionsPruned = DominatedActionsPruned,
                DuplicateCardBranchesPruned = DuplicateCardBranchesPruned,
                ExecutionChoiceCaptures = ExecutionChoiceCaptures,
                ExecutionChoiceReuses = ExecutionChoiceReuses,
                HpInvestmentBranchesProtected = HpInvestmentBranchesProtected,
                MaxDeferredRoundChoiceLayerWidth = MaxDeferredRoundChoiceLayerWidth,
                MaxParallelActionReplayConcurrency = MaxParallelActionReplayConcurrency,
                MaxParallelExpansionConcurrency = MaxParallelExpansionConcurrency,
                MaxParallelRoundChoiceReplayConcurrency = MaxParallelRoundChoiceReplayConcurrency,
                NodeLimitSnapshotsReleased = NodeLimitSnapshotsReleased,
                OrderedMutationColdAtomicCommitted = OrderedMutationColdAtomicCommitted,
                OrderedMutationColdAtomicRejected = OrderedMutationColdAtomicRejected,
                OrderedMutationLeaseExpiredBudget = OrderedMutationLeaseExpiredBudget,
                OrderedMutationOrdinaryFallbacks = OrderedMutationOrdinaryFallbacks,
                OrderedMutationPortfolioNodesConsumed = OrderedMutationPortfolioNodesConsumed,
                ParallelActionReplayWaves = ParallelActionReplayWaves,
                ParallelActionReplayWorkItems = ParallelActionReplayWorkItems,
                ParallelExpansionWaves = ParallelExpansionWaves,
                ParallelExpansionWorkItems = ParallelExpansionWorkItems,
                ParallelRoundChoiceReplayWaves = ParallelRoundChoiceReplayWaves,
                ParallelRoundChoiceReplayWorkItems = ParallelRoundChoiceReplayWorkItems,
                PotionChoicePrefixCaptures = PotionChoicePrefixCaptures,
                PotionChoicePrefixFallbacks = PotionChoicePrefixFallbacks,
                PotionChoicePrefixForks = PotionChoicePrefixForks,
                PotionChoicePrefixReuses = PotionChoicePrefixReuses,
                PrimaryIncumbentBranchesPruned = PrimaryIncumbentBranchesPruned,
                PrimaryIncumbentCertifiedHealingBoundBranchesPruned = PrimaryIncumbentCertifiedHealingBoundBranchesPruned,
                PrimaryIncumbentUpdates = PrimaryIncumbentUpdates,
                RepeatableNoProgressBranchesPruned = RepeatableNoProgressBranchesPruned,
                ReusedNodeSnapshots = ReusedNodeSnapshots,
                RoundReplayPrefixCaptures = RoundReplayPrefixCaptures,
                RoundReplayPrefixReuses = RoundReplayPrefixReuses,
                ShuffleBranchesPruned = ShuffleBranchesPruned,
                SoldHpBranchesPruned = SoldHpBranchesPruned,
                StandPatProbes = StandPatProbes,
                TopQueueActionsDropped = TopQueueActionsDropped,
                TranspositionBranchesPruned = TranspositionBranchesPruned,
                TranspositionLimitBypasses = TranspositionLimitBypasses,
                TurnLayerBudgetStops = TurnLayerBudgetStops,
                TurnLayerNodeBudgetStops = TurnLayerNodeBudgetStops,
                TurnLayerTimeBudgetStops = TurnLayerTimeBudgetStops,
                TranspositionCount = Transpositions.Count,
                ExpandedTranspositionCount = ExpandedTranspositions.Count,
                StandPatCacheCount = StandPatCache.Count,
                ThreatProjectionCacheCount = ThreatProjectionCache.Count,
                CoverageCacheCount = CoverageCache.Count,
                MaxObservedGcPause = WorkPacer.MaxObservedGcPause,
                WorkerYieldCount = WorkPacer.YieldCount,
                FrameRecoveryWaitCount = WorkPacer.FrameRecoveryWaitCount,
                FrameRecoveryWaitDuration = WorkPacer.FrameRecoveryWaitDuration,
                OffThreadAllocatedBytes = OffThreadAllocatedBytes,
                NoveltySearch = Novelty?.Describe(),
            };

        public void ResetRebuildableCaches(IReadOnlyList<SearchNode> frontier)
        {
            ExpansionBatchPool.Clear();
            SnapshotLiveCards.Clear();
            Transpositions = [];
            foreach (SearchNode node in frontier)
            {
                Transpositions[node.StateKey] = new TranspositionFrontier(
                    new TranspositionLabel(
                        node.PotionCount,
                        node.PotionStrategicCost,
                        node.FutureSoldHp,
                        node.Snapshot.CumulativePlayerHpLost,
                        node.ActionCount,
                        node.Score));
            }
            ExpandedTranspositions = [];
            StandPatCache = [];
            ThreatProjectionCache.Clear();
            CoverageCache.Clear();
        }

        public void ResetReclaimableCaches()
        {
            ExpansionBatchPool.Clear();
            SnapshotLiveCards.Clear();
            // A running prune may have skipped cached representatives when preparing its
            // pending probes. Keep these scalar answers through its drained checkpoints.
            if (EnsurePruneMemory == null)
                StandPatCache = [];
            ThreatProjectionCache.Clear();
            CoverageCache.Clear();
        }
    }

    [InlineArray(10)]
    private struct HandFingerprintBuffer
    {
        private StateFingerprint _element0;
    }

    private sealed record PendingTurnOutcome(
        SearchNode Node,
        SearchNode TurnStart,
        int Turn,
        int HpLost,
        int ActualBlock,
        int EnergyLeft,
        ulong PotionSlotsUsed,
        bool IsComparable);

}

[Flags]
internal enum ActionOptionFamily
{
    None = 0,
    ImmediateDefense = 1 << 0,
    ImmediateOffense = 1 << 1,
    ResourceAndCycle = 1 << 2,
    PersistentSetup = 1 << 3,
    Control = 1 << 4,
    TargetRemoval = 1 << 5,
    HpInvestment = 1 << 6,
}

internal readonly record struct ActionCandidate(
    SearchNode Node,
    CardType CardType,
    uint? TargetCombatId,
    int EnergySpent,
    int StarsSpent,
    int Damage,
    int Block,
    int Hp,
    int MaxHp,
    int CumulativeHpLost,
    int LongTermResourceValue,
    int AngerCopiesGenerated,
    ActionOptionFamily OptionFamilies,
    bool IsPure,
    double NormalizedValue);

internal readonly record struct ThreatFocus(
    uint? CombatId,
    int Pressure,
    int RemainingHp,
    int CurrentThreat,
    int TotalThreat,
    int IncomingHitCount);

internal sealed record CoverageSummary(
    IReadOnlyList<PredictionGap> Gaps,
    bool HasUncompensatedRisk)
{
    public static CoverageSummary None { get; } = new([], false);
}

internal readonly record struct ThreatProjection(
    int Hp,
    int DeathSaveHpRestored,
    int DeathSaveUseCount,
    int BlockConsumed = 0);
