using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// Replay work counters shared by the existing search and the opening probes. Session-level
/// metrics (expanded nodes, retention counters, …) stay with the search run; everything a replay
/// performs is recorded here so both callers observe the same accounting.
/// </summary>
internal sealed class SearchReplayAccounting(
    bool measurePhasePerformance,
    SearchFramePressureSignal? framePressureSignal)
{
    public readonly SearchPerformanceMetrics Performance = new(measurePhasePerformance);
    public readonly SearchWorkPacer? WorkPacer = framePressureSignal is null
        ? null : new SearchWorkPacer(framePressureSignal);
    public int ReplayCount;
    public int ForkCount;
    public int TransitionCount;
    public int ChoiceReplayAttempts;
    public int ChoiceBranchesEvaluated;
    public int ChoiceReplayBudgetExhaustions;
    public int ChoiceBranchesDroppedByBudget;
    public int RoundReplayPrefixCaptures;
    public int RoundReplayPrefixReuses;
    public int ExecutionChoiceCaptures;
    public int ExecutionChoiceReuses;
    public int CardChoicePrefixAttempts;
    public int CardChoicePrefixCaptures;
    public int CardChoicePrefixReuses;
    public int CardChoicePrefixFallbacks;
    public int PotionChoicePrefixForks;
    public int PotionChoicePrefixCaptures;
    public int PotionChoicePrefixReuses;
    public int PotionChoicePrefixFallbacks;
    // Lane-local scheduling hints; never combat state or candidate policy.
    public bool HasObservedPostDrawRoundChoice;
    public HashSet<string>? ObservedHandDrawShuffleChoiceSources;
    public int InitialPersistentBuffValue;
    public int InitialEnemyStrengthSuppression;
    public int InitialEnemyWeakTurns;
    public int InitialRetainedAttackValue;
}

internal sealed class SearchReplayTestingState
{
    public bool DisableExecutionChoiceContinuations;
    public bool VerifyChoiceContinuationSteps;
}

/// <summary>The probe/engine replay configuration. Every member is an explicit injected fact.</summary>
internal sealed class SearchReplayOptions(
    CombatRootSnapshot root,
    SolverDisplayNames displayNames,
    SearchPolicySnapshot policy,
    Player player,
    int startTurnNumber,
    IntentForecast forecast,
    SearchEvaluator evaluator,
    SearchRoundContext roundContext,
    SearchReplayAccounting accounting,
    CancellationToken cancellationToken,
    bool includeTurnSetup,
    SearchDiagnosticsSink diagnostics,
    bool detailedDiagnostics)
{
    public CombatRootSnapshot Root { get; } = root;
    public SolverDisplayNames DisplayNames { get; } = displayNames;
    public SearchPolicySnapshot Policy { get; } = policy;
    public Player Player { get; } = player;
    public int StartTurnNumber { get; } = startTurnNumber;
    public IntentForecast Forecast { get; } = forecast;
    public SearchEvaluator Evaluator { get; } = evaluator;
    public SearchRoundContext RoundContext { get; } = roundContext;
    public SearchReplayAccounting Accounting { get; } = accounting;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    public bool IncludeTurnSetup { get; } = includeTurnSetup;
    public SearchDiagnosticsSink Diagnostics { get; } = diagnostics;
    public bool DetailedDiagnostics { get; } = detailedDiagnostics;
    public bool VerifyIncrementalSearch { get; init; }
    public bool DisableCardChoiceContinuations { get; init; }
    public bool HasChoiceBudget => true;
}

internal sealed record TurnSetupChoiceLayer(
    TurnStartChoiceRequest Request,
    CardChoiceSpec Spec,
    IReadOnlyList<PlanCardChoice> Branches);

internal sealed record PendingChoiceBudgetSeed(
    CardChoiceSpec? Spec,
    int SemanticBranchCount,
    TurnSetupChoiceLayer? TurnSetupLayer);

/// <summary>
/// A hierarchical lease over one exclusively owned whole-action budget. Child leases cap how
/// much an earlier stable branch may consume while reserving one unit for every later sibling;
/// actual consumption is also charged to every ancestor. The single implementation is shared by
/// the Beam action expansion and the turn-setup root enumeration.
/// </summary>
internal sealed class ChoiceSearchBudget
{
    private readonly ChoiceSearchBudget? _parent;
    private int _semanticFinalQuota;
    private int _materializedOccurrenceFinalQuota;
    private int _replayAttemptQuota;

    public ChoiceSearchBudget(
        int semanticFinalQuota,
        int materializedOccurrenceFinalQuota,
        int replayAttemptQuota)
        : this(
            parent: null,
            semanticFinalQuota,
            materializedOccurrenceFinalQuota,
            replayAttemptQuota)
    {
    }

    private ChoiceSearchBudget(
        ChoiceSearchBudget? parent,
        int semanticFinalQuota,
        int materializedOccurrenceFinalQuota,
        int replayAttemptQuota)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(semanticFinalQuota);
        ArgumentOutOfRangeException.ThrowIfNegative(materializedOccurrenceFinalQuota);
        ArgumentOutOfRangeException.ThrowIfNegative(replayAttemptQuota);
        _parent = parent;
        _semanticFinalQuota = semanticFinalQuota;
        _materializedOccurrenceFinalQuota = materializedOccurrenceFinalQuota;
        _replayAttemptQuota = replayAttemptQuota;
    }

    public int SemanticFinalQuota => Math.Min(
        _semanticFinalQuota,
        _parent?.SemanticFinalQuota ?? int.MaxValue);

    public int MaterializedOccurrenceFinalQuota => Math.Min(
        _materializedOccurrenceFinalQuota,
        _parent?.MaterializedOccurrenceFinalQuota ?? int.MaxValue);

    public int ReplayAttemptQuota => Math.Min(
        _replayAttemptQuota,
        _parent?.ReplayAttemptQuota ?? int.MaxValue);

    public int ActiveFinalQuota => SaturatingQuotaSum(
        SemanticFinalQuota,
        MaterializedOccurrenceFinalQuota);

    public bool HasWork => ActiveFinalQuota > 0 && ReplayAttemptQuota > 0;

    public ChoiceSearchBudget CreateChildLease(
        int activeFinalQuota,
        int replayAttemptQuota)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeFinalQuota);
        ArgumentOutOfRangeException.ThrowIfNegative(replayAttemptQuota);
        int semanticFinalQuota = Math.Min(SemanticFinalQuota, activeFinalQuota);
        int occurrenceFinalQuota = Math.Min(
            MaterializedOccurrenceFinalQuota,
            activeFinalQuota - semanticFinalQuota);
        return new ChoiceSearchBudget(
            this,
            semanticFinalQuota,
            occurrenceFinalQuota,
            Math.Min(ReplayAttemptQuota, replayAttemptQuota));
    }

    public bool TrySpendReplayAttempt()
    {
        if (_replayAttemptQuota <= 0)
            return false;
        if (_parent != null && !_parent.TrySpendReplayAttempt())
            return false;
        _replayAttemptQuota--;
        return true;
    }

    public bool TryConsumeFinal()
    {
        if (SemanticFinalQuota > 0 && TryConsumeSemanticFinal())
            return true;
        return MaterializedOccurrenceFinalQuota > 0
            && TryConsumeOccurrenceFinal();
    }

    private bool TryConsumeSemanticFinal()
    {
        if (_semanticFinalQuota <= 0)
            return false;
        if (_parent != null && !_parent.TryConsumeSemanticFinal())
            return false;
        _semanticFinalQuota--;
        return true;
    }

    private bool TryConsumeOccurrenceFinal()
    {
        if (_materializedOccurrenceFinalQuota <= 0)
            return false;
        if (_parent != null && !_parent.TryConsumeOccurrenceFinal())
            return false;
        _materializedOccurrenceFinalQuota--;
        return true;
    }

    private static int SaturatingQuotaSum(int left, int right)
        => left > int.MaxValue - right ? int.MaxValue : left + right;
}

internal static class SearchChoicePlanning
{
    /// <summary>
    /// Inspects a pending boundary without advancing or mutating its simulator. Root-level
    /// choice coordinators use the actual first-layer semantic width instead of silently falling
    /// back to the configured profile width. Setup can also reuse the materialized turn-start
    /// layer so the potentially wide choice list is not built twice.
    /// </summary>
    internal static PendingChoiceBudgetSeed? BuildCurrentPendingChoiceBudgetSeed(
        SolverSearchProfile profile,
        SolverDisplayNames displayNames,
        Player player,
        SimulationSnapshot snapshot)
    {
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
            return null;

        SimulatedCombatState combat =
            (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (combat.PendingKnowledgeDemonChoice is { } knowledgeRequest)
        {
            IReadOnlyList<PlanCardChoice> knowledgeBranches =
                KnowledgeDemonChoiceSupport.BuildChoices(knowledgeRequest, displayNames);
            return new PendingChoiceBudgetSeed(
                Spec: null,
                SemanticBranchCount: knowledgeBranches.Count,
                TurnSetupLayer: null);
        }

        if (combat.PendingTurnStartChoice is not { } request)
            return null;

        CombatPredictionSimulator simulator =
            (CombatPredictionSimulator)snapshot.Simulator;
        CardChoiceSpec spec =
            TurnStartChoiceSupport.BuildPendingSpec(simulator, combat, player);
        IReadOnlyList<PlanCardChoice> branches = CardChoiceSupport.BuildChoices(
            spec,
            displayNames,
            profile.MaxPileChoiceBranchesPerAction,
            profile.MaxHandChoiceBranchesPerAction);
        int semanticBranchCount =
            CardChoiceSupport.IsIdentityChangingPersistentChoiceEffect(spec.Effect)
                ? CardChoiceSupport.CountSemanticChoices(branches)
                : branches.Count;
        return new PendingChoiceBudgetSeed(
            spec,
            semanticBranchCount,
            new TurnSetupChoiceLayer(request, spec, branches));
    }
}

internal readonly record struct WholeActionChoiceBudget(
    ChoiceSearchBudget SemanticSearchBudget,
    int OccurrenceFinalReserve,
    int OccurrenceReplayAttemptQuota);

/// <summary>
/// Collects physical-identity supplements across the whole action. Non-identity layers share
/// this coordinator instead of pre-assigning its two slots to outer branches: a later stable
/// semantic branch can therefore still expose the first actual identity-sensitive frontier.
/// Supplements are replayed only after the semantic pass, so they can never replace a distinct
/// semantic decision. The collector is action-local and has one ordered consumer; workers never race
/// on a shared counter.
/// </summary>
internal sealed class ChoiceOccurrenceCollector<TCandidate>(
    int finalReserve,
    int replayAttemptQuota)
{
    private readonly List<TCandidate> _branches = [];
    private bool _sealed;

    public int FinalReserve { get; } = finalReserve is >= 0 and
        <= CardChoiceSupport.MaximumIdentityOccurrenceReservedBranches
            ? finalReserve
            : throw new ArgumentOutOfRangeException(nameof(finalReserve));

    public int ReplayAttemptQuota { get; } = replayAttemptQuota >= 0
        ? replayAttemptQuota
        : throw new ArgumentOutOfRangeException(nameof(replayAttemptQuota));

    public int RemainingFinalReserve => _sealed
        ? 0
        : Math.Max(0, FinalReserve - _branches.Count);

    public IReadOnlyList<TCandidate> Seal()
    {
        _sealed = true;
        return _branches;
    }

    public void Collect(TCandidate candidate)
    {
        if (_sealed
            || RemainingFinalReserve <= 0
            || ReplayAttemptQuota <= _branches.Count)
        {
            return;
        }
        _branches.Add(candidate);
    }
}

/// <summary>The single choice-budget arithmetic used by expansion and turn-setup roots.</summary>
internal static class SearchChoiceBudgetPolicy
{
    internal static int ResolveConfiguredWholeActionChoiceBranchLimit(
        SolverSearchProfile profile,
        CardChoiceSpec? primaryChoiceSpec)
        => primaryChoiceSpec?.SourcePile == PileType.Hand
            ? profile.MaxHandChoiceBranchesPerAction
            : primaryChoiceSpec == null
                ? Math.Max(
                    profile.MaxPileChoiceBranchesPerAction,
                    profile.MaxHandChoiceBranchesPerAction)
                : profile.MaxPileChoiceBranchesPerAction;

    internal static WholeActionChoiceBudget CreateWholeActionChoiceBudget(
        SolverSearchProfile profile,
        CardChoiceSpec? primaryChoiceSpec,
        int minimumSemanticFinalQuota = 1,
        PendingChoiceBudgetSeed? currentPendingChoice = null)
    {
        int configuredFinalQuota = ResolveConfiguredWholeActionChoiceBranchLimit(
            profile, primaryChoiceSpec);
        if (currentPendingChoice != null)
        {
            configuredFinalQuota = Math.Max(
                configuredFinalQuota,
                ResolveConfiguredWholeActionChoiceBranchLimit(
                    profile, currentPendingChoice.Spec));
        }
        return ResolveSeededWholeActionChoiceBudgetCore(
            configuredFinalQuota,
            minimumSemanticFinalQuota,
            currentPendingChoice?.SemanticBranchCount ?? 0);
    }

    internal static WholeActionChoiceBudget ResolveSeededWholeActionChoiceBudgetCore(
        int configuredFinalQuota,
        int primarySemanticBranchCount,
        int pendingSemanticBranchCount)
    {
        if (configuredFinalQuota < 1)
            throw new ArgumentOutOfRangeException(nameof(configuredFinalQuota));
        if (primarySemanticBranchCount < 0)
            throw new ArgumentOutOfRangeException(nameof(primarySemanticBranchCount));
        if (pendingSemanticBranchCount < 0)
            throw new ArgumentOutOfRangeException(nameof(pendingSemanticBranchCount));
        return ResolveWholeActionChoiceBudgetCore(Math.Max(
            configuredFinalQuota,
            Math.Max(primarySemanticBranchCount, pendingSemanticBranchCount)));
    }

    internal static WholeActionChoiceBudget ResolveWholeActionChoiceBudgetCore(
        int semanticFinalQuota)
    {
        if (semanticFinalQuota < 1)
            throw new ArgumentOutOfRangeException(nameof(semanticFinalQuota));
        int maximumFinalQuota = semanticFinalQuota
            > int.MaxValue - CardChoiceSupport.MaximumIdentityOccurrenceReservedBranches
                ? int.MaxValue
                : semanticFinalQuota
                    + CardChoiceSupport.MaximumIdentityOccurrenceReservedBranches;
        int totalReplayAttemptQuota = ResolveFiniteChoiceReplayAttemptLimit(maximumFinalQuota);

        // Semantic decisions always receive the capped attempt budget first. Only genuine cap
        // headroom is reserved for the later physical-occurrence pass, so a 600-way semantic
        // layer under the 512 hard cap keeps 512 different decisions and zero supplements.
        int minimumSemanticAttempts = Math.Min(
            semanticFinalQuota,
            totalReplayAttemptQuota);
        int occurrenceReplayAttemptQuota = Math.Min(
            CardChoiceSupport.MaximumIdentityOccurrenceReservedBranches * 4,
            totalReplayAttemptQuota - minimumSemanticAttempts);
        int occurrenceFinalReserve = Math.Min(
            CardChoiceSupport.MaximumIdentityOccurrenceReservedBranches,
            occurrenceReplayAttemptQuota);
        return new WholeActionChoiceBudget(
            new ChoiceSearchBudget(
                semanticFinalQuota,
                materializedOccurrenceFinalQuota: 0,
                totalReplayAttemptQuota - occurrenceReplayAttemptQuota),
            occurrenceFinalReserve,
            occurrenceReplayAttemptQuota);
    }

    internal static ChoiceSearchBudget? CreateChoiceBranchBudgetCore(
        ChoiceSearchBudget parentBudget,
        int laterSiblingCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(laterSiblingCount);
        int effectiveFinalQuota = Math.Min(
            parentBudget.ActiveFinalQuota,
            parentBudget.ReplayAttemptQuota);
        if (effectiveFinalQuota <= 0)
            return null;

        // Divide the live remainder fairly among this branch and its later stable siblings. This
        // reproduces the old 4/3/3 breadth when every branch uses its share, but a shallow or
        // invalid branch leaves its unspent quota in the parent; the next branch then receives a
        // larger share of the new remainder instead of losing that work permanently.
        int remainingSiblingCount = checked(laterSiblingCount + 1);
        int finalQuota = DivideRoundUp(effectiveFinalQuota, remainingSiblingCount);
        int attemptQuota = DivideRoundUp(
            parentBudget.ReplayAttemptQuota,
            remainingSiblingCount);
        return parentBudget.CreateChildLease(
            finalQuota,
            attemptQuota);
    }

    internal static int DivideRoundUp(int value, int divisor)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (divisor < 1)
            throw new ArgumentOutOfRangeException(nameof(divisor));
        return value == 0 ? 0 : 1 + (value - 1) / divisor;
    }

    internal static int ResolveFiniteChoiceReplayAttemptLimit(int finiteFinalBranches)
    {
        // Four replay attempts per retained leaf covers ordinary primary + nested chains while
        // keeping a hard ceiling for recursive generators. Custom profiles remain bounded too.
        return (int)Math.Min(512L, Math.Max(1L, finiteFinalBranches * 4L));
    }
}

/// <summary>Shared root facts the Flash bridge consumes from the existing engine's snapshot.</summary>
internal static class FlashBridgeFacts
{
    internal static FlashPotionUsageForBridge CapturePotionUsage(SimulationSnapshot snapshot)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        int automatic = 0;
        foreach (PredictedPotionUse use in combat.PotionUses)
        {
            if (use.Automatic)
                automatic = checked(automatic + 1);
        }
        return new FlashPotionUsageForBridge(
            snapshot.PotionUseCount,
            checked(snapshot.PotionUseCount - automatic),
            snapshot.PotionStrategicCost,
            snapshot.ExplicitPotionStrategicCost);
    }

    /// <summary>
    /// The existing search's potion constraints at <paramref name="snapshot"/>, as plain values:
    /// per slot whether an explicit use is offered and its strategic HP cost, the earliest search
    /// turn (<c>node.Turn</c> units) and the maximum number of explicit uses.
    /// </summary>
    internal static (bool[] Allowed, int[] Costs, int? EarliestTurn, int? MaximumUses)
        CapturePotionPolicy(
            CombatRootSnapshot root,
            Player player,
            PotionStrategySnapshot strategy,
            SolverPotionPolicy effectivePolicy,
            bool forceAllPotionsDisabled,
            SimulationSnapshot snapshot,
            int? earliestPotionTurn,
            int? maximumPotionUses)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        bool[] allowed = new bool[root.PotionSlotCount];
        int[] costs = new int[root.PotionSlotCount];
        for (int slot = 0; slot < allowed.Length; slot++)
        {
            PotionModel? potion = combat.GetPotionAtSlot(player, slot);
            if (potion is null)
                continue;
            allowed[slot] = combat.IsPotionAvailable(player, slot)
                && PotionOnUseSupport.CanSearch(potion)
                && SearchProbeHelpers.AllowsPotionUse(
                    strategy,
                    effectivePolicy,
                    forceAllPotionsDisabled,
                    slot,
                    potion.Id.Entry);
            bool renewable = potion is PotionShapedRock
                && combat.RelicsOf(player).OfType<PetrifiedToad>().Any(static relic => !relic.IsMelted);
            costs[slot] = combat.IsFreeEntropicPotionAtSlot(player, slot) ? 0 : PotionUsePolicy.StrategicHpCost(potion, renewable);
        }
        return (allowed, costs, earliestPotionTurn, maximumPotionUses);
    }

    internal static FlashPruningRequest CapturePruningRequest(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverPotionPolicy? potionPolicyOverride,
        int? maximumPotionUses,
        int? minimumPotionUses,
        BattleDamageSnapshot battleDamage)
    {
        SolverPotionPolicy effective = potionPolicyOverride
            ?? (policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                ? SolverPotionPolicy.Smart
                : policy.PotionPolicy == SolverPotionPolicy.RequireAtLeastOne
                    && battleDamage.PotionsUsedSoFar > 0
                        ? SolverPotionPolicy.Smart
                        : policy.PotionPolicy);
        return new FlashPruningRequest(
            effective,
            potionPolicyOverride == SolverPotionPolicy.Disabled,
            maximumPotionUses,
            minimumPotionUses ?? 0,
            ActEndingBossPolicy.ResolveStrategicHpRelief(
                root.BossHpRelief,
                policy.ActTransitionBossHpStrategy,
                policy.FinalBossHpStrategy));
    }

    /// <summary>
    /// Structural equality of two turn-setup choice vectors. <see cref="PlanCardChoice"/> holds a card
    /// list, so record equality would compare list references.
    /// </summary>
    internal static bool SameTurnSetupChoices(
        IReadOnlyList<PlanCardChoice> left,
        IReadOnlyList<PlanCardChoice> right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            PlanCardChoice a = left[index];
            PlanCardChoice b = right[index];
            if (a.Effect != b.Effect
                || a.SourcePile != b.SourcePile
                || a.Timing != b.Timing
                || !string.Equals(a.SourceId, b.SourceId, StringComparison.Ordinal)
                || !string.Equals(a.ContextId, b.ContextId, StringComparison.Ordinal)
                || a.Cards.Count != b.Cards.Count)
            {
                return false;
            }
            for (int card = 0; card < a.Cards.Count; card++)
            {
                PlanCardToken x = a.Cards[card];
                PlanCardToken y = b.Cards[card];
                if (!string.Equals(x.CardId, y.CardId, StringComparison.Ordinal)
                    || x.UpgradeLevel != y.UpgradeLevel
                    || !string.Equals(x.StateKey, y.StateKey, StringComparison.Ordinal)
                    || x.SourceOccurrence != y.SourceOccurrence
                    || x.OptionOccurrence != y.OptionOccurrence)
                {
                    return false;
                }
            }
        }
        return true;
    }
}

/// <summary>The existing primary member's incumbent policy, frozen for the Flash bound.</summary>
internal readonly record struct FlashPruningRequest(
    SolverPotionPolicy EffectivePotionPolicy,
    bool ForceAllPotionsDisabled,
    int? MaximumPotionUses,
    int MinimumPotionUses,
    BossHpRelief StrategicBossHpRelief);

/// <summary>The cumulative potion counters of an existing-engine snapshot, frozen for a Flash search root.</summary>
internal readonly record struct FlashPotionUsageForBridge(
    int PotionUseCount,
    int ExplicitPotionUseCount,
    int PotionStrategicCost,
    int ExplicitPotionStrategicCost);
