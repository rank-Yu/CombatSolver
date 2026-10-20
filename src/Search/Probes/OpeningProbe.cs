using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

/// <summary>
/// Read-only search probes over a frozen combat root. The coordinator uses this type to propose
/// candidate opening and prefix continuations; it owns replay and shallow expansion but no Beam
/// frontier, retention policy or final ordering. The existing solver calls the same helpers where
/// it still needs a probe (for example fixed-prefix replay for Flash).
/// </summary>
internal sealed partial class OpeningProbe(
    CombatRootSnapshot root,
    SolverDisplayNames displayNames,
    BattleDamageSnapshot battleDamage,
    SearchPolicySnapshot policy,
    CancellationToken cancellationToken = default,
    SolverSearchProfile? searchProfile = null,
    SolverPotionPolicy? potionPolicyOverride = null,
    int? maximumPotionUses = null,
    int? earliestPotionTurn = null,
    bool resetFixedPrefixSchedulingBaseline = false)
{
    private Func<IReadOnlyList<PlanAction>, SimulationSnapshot?, int, int, SimulationSnapshot>? _fixedPrefixReplay;
    private Func<IReadOnlyList<PlanAction>, SimulationSnapshot?, int, int, SimulationSnapshot> FixedPrefixReplay
        => _fixedPrefixReplay ??= (actions, parent, turn, count) => Replay(actions, parent, turn, count);

    private readonly SolverSearchProfile _profile = searchProfile ?? SolverSearchProfile.Default;
    private readonly SearchProbeRun _run = new();
    private readonly bool _includeTurnSetup = policy.IncludeTurnSetup;
    private readonly Player _player = root.PlayerIdentity;
    private readonly IntentForecast _forecast = root.Forecast;
    private readonly int _startTurnNumber = root.StartTurnNumber;
    private readonly CombatHistoryDependencies _historyDependencies = root.HistoryDependencies;
    private readonly GrowthValues _growthBudgets = policy.EffectiveGrowthBudgets;
    private readonly IReadOnlyList<RelicCounterTarget> _relicTargets = policy.RelicTargets;
    private readonly bool _ignoreLongTermRewards = policy.IgnoreLongTermRewards;
    private readonly BossHpRelief _strategicBossHpRelief = ActEndingBossPolicy.ResolveStrategicHpRelief(
        root.BossHpRelief,
        policy.ActTransitionBossHpStrategy,
        policy.FinalBossHpStrategy);
    private readonly SolverTheftPolicy? _theftPolicy = policy.TheftPolicy;
    private readonly DevelopmentSearchStrategy? _developmentStrategy = policy.DevelopmentStrategy;
    private readonly int? _maximumPotionUses = maximumPotionUses;
    private readonly int? _earliestPotionTurn = earliestPotionTurn;
    private readonly bool _resetFixedPrefixSchedulingBaseline = resetFixedPrefixSchedulingBaseline;
    private readonly PotionStrategySnapshot _potionStrategy = policy.PotionStrategy;
    private readonly bool _forceAllPotionsDisabled = potionPolicyOverride == SolverPotionPolicy.Disabled;
    private readonly SolverPotionPolicy _potionPolicy = potionPolicyOverride
        ?? (policy.TheftPolicy == SolverTheftPolicy.PreserveResources
            ? SolverPotionPolicy.Smart
            : policy.PotionPolicy == SolverPotionPolicy.RequireAtLeastOne
                && battleDamage.PotionsUsedSoFar > 0
                    ? SolverPotionPolicy.Smart
                    : policy.PotionPolicy);
    private SearchEvaluator? _evaluator;
    private SearchRoundContext? _roundContext;
    private SearchExpansionContext? _expansionContext;
    private CandidateSelectionContext? _candidateSelection;
    private CandidateSelectionContext CandidateSelection => _candidateSelection ??= new(
        _profile,
        _theftPolicy,
        _developmentStrategy,
        onProtected: null,
        detailedDiagnostics: false,
        log: null,
        isRoutingChoice: null);
    private SearchExpansionContext ExpansionContext => _expansionContext ??= new SearchExpansionContext(
        _player,
        displayNames,
        Evaluator,
        root.PotionSlotCount,
        _maximumPotionUses,
        _earliestPotionTurn,
        _potionStrategy,
        _potionPolicy,
        _forceAllPotionsDisabled,
        onDuplicateCard: null,
        decorateNode: null);
    private SearchRoundContext RoundContext => _roundContext ??= new SearchRoundContext(
        _player,
        _startTurnNumber,
        _run.Performance,
        recordExecutionFrames: true,
        captureBeforeHandDraw: null,
        capture: null);

    private SearchEvaluator Evaluator => _evaluator ??= new SearchEvaluator(
        root,
        _player,
        policy,
        _run.Performance,
        _run.SnapshotLiveCards,
        _run.ThreatProjectionCache,
        _run.CoverageCache,
        _startTurnNumber,
        _historyDependencies,
        _growthBudgets,
        _relicTargets,
        _ignoreLongTermRewards,
        _strategicBossHpRelief,
        _theftPolicy);

    internal SearchReplayCore ReplayCoreForBridge => ReplayCore;
    internal SearchReplayAccounting AccountingForBridge => _run.Replay;
    internal SearchExpansionContext ExpansionContextForBridge => ExpansionContext;
    internal SearchEvaluator EvaluatorForBridge => Evaluator;
    internal SolverSearchProfile ProfileForBridge => _profile;
    internal SolverPotionPolicy PotionPolicyForBridge => _potionPolicy;
    internal bool ForceAllPotionsDisabledForBridge => _forceAllPotionsDisabled;
    internal PotionStrategySnapshot PotionStrategyForBridge => _potionStrategy;
    internal SolverTheftPolicy? TheftPolicyForBridge => _theftPolicy;
    internal int? MaximumPotionUsesForBridge => _maximumPotionUses;
    internal int? EarliestPotionTurnForBridge => _earliestPotionTurn;
    internal int MinimumPotionUsesForBridge => 0;
    internal BossHpRelief StrategicBossHpReliefForBridge =>
        ActEndingBossPolicy.ResolveStrategicHpRelief(
            root.BossHpRelief,
            policy.ActTransitionBossHpStrategy,
            policy.FinalBossHpStrategy);

    internal static SearchNode CreateOpeningSearchSeed(SimulationSnapshot rootSnapshot,
        IReadOnlyList<PlanCardChoice>? turnSetupChoices = null,
        ContinuationStamp? turnSetupPlayState = null)
        => new(
            null,
            0,
            rootSnapshot.PotionUseCount,
            rootSnapshot.PotionStrategicCost,
            rootSnapshot.Turn,
            SearchRouteTraits.None,
            0,
            rootSnapshot.Score,
            rootSnapshot.StateKey,
            rootSnapshot.HasRisk,
            rootSnapshot.BoundaryReason,
            false,
            null,
            rootSnapshot,
            CombatProgressState.Capture(rootSnapshot),
            TurnSetupChoices: turnSetupChoices,
            TurnSetupPlayState: turnSetupPlayState);

    private SearchNode CreateOpeningFollowUpSeed(
        IReadOnlyList<PlanAction> prefix,
        SearchRouteTraits traits = SearchRouteTraits.Scaling)
        => TryCreateOpeningFollowUpSeed(prefix, traits)
            ?? throw new InvalidOperationException("Opening follow-up prefix is no longer applicable.");

    private SearchNode? TryCreateOpeningFollowUpSeed(
        IReadOnlyList<PlanAction> prefix,
        SearchRouteTraits traits = SearchRouteTraits.Scaling)
    {
        SimulationSnapshot snapshot = Replay([]);
        SearchNode seed = new(
            null,
            0,
            snapshot.PotionUseCount,
            snapshot.PotionStrategicCost,
            snapshot.Turn,
            traits,
            0,
            snapshot.Score,
            snapshot.StateKey,
            snapshot.HasRisk,
            snapshot.BoundaryReason,
            false,
            null,
            snapshot,
            CombatProgressState.Capture(snapshot));
        // Build the actual parent chain, so replay verification and descendant actions
        // include the resource/potion/setup cards that produced this state.
        return SearchProbeHelpers.ApplyFixedPrefix(
            seed, prefix, ExpansionContext,
            FixedPrefixReplay,
            _resetFixedPrefixSchedulingBaseline);
    }

    internal bool CanContinueAtPrefix(IReadOnlyList<PlanAction> prefix)
    {
        SearchNode? applied = TryCreateOpeningFollowUpSeed(prefix, SearchRouteTraits.None);
        if (applied == null)
            return false;
        try
        {
            return applied.ActionCount == prefix.Count
                && !applied.Snapshot.PlayerDead && !applied.Snapshot.AllEnemiesDead
                && !applied.Snapshot.HasRisk
                && applied.Snapshot.BoundaryReason == SearchBoundaryReason.None;
        }
        finally
        {
            applied.Snapshot.ReleaseSimulator();
        }
    }

    internal bool CanReplayOpeningPrefix(IReadOnlyList<PlanAction> prefix)
    {
        if (prefix.Any(action => action.EndsPlayerTurn))
            return false;
        IReadOnlyList<SimulationSnapshot> roots = _includeTurnSetup
            ? BuildTurnSetupRoots().Select(candidate => candidate.Snapshot).ToArray()
            : [Replay([])];
        bool applicable = false;
        foreach (SimulationSnapshot snapshot in roots)
        {
            SearchNode? applied;
            try
            {
                applied = SearchProbeHelpers.ApplyFixedPrefix(
                    CreateOpeningSearchSeed(snapshot), prefix, ExpansionContext,
                    FixedPrefixReplay,
                    _resetFixedPrefixSchedulingBaseline);
            }
            catch (InvalidPlannedChoiceBranchException)
            {
                continue;
            }
            if (applied == null)
                continue;
            applicable = true;
            applied.Snapshot.ReleaseSimulator();
        }
        return applicable;
    }


    internal static bool HasPlayableFetchedPower(SearchNode node, Player player)
    {
        if (node.Action?.Choice is not { Effect: PlanChoiceEffect.MoveToHand } choice)
            return false;
        var simulator = (CombatPredictionSimulator)node.Snapshot.Simulator;
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        return simulator.State.GetPlayerCombatState(player).Hand.Cards.Any(card =>
            card.Preview.Type == CardType.Power && combat.CanPlayCard(simulator, card)
            && choice.Cards.Any(token => CardChoiceSupport.MatchesToken(card, token)));
    }
}
