using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

/// <summary>
/// A parent simulator cannot be forked concurrently: prediction history seals its mutable
/// tail and several COW containers publish a shared bit during Fork. Lanes serialize seed
/// creation through the parent's gate; each worker then consumes only its private fork.
/// </summary>
internal sealed class ReplayForkSeed(
    CombatPredictionSimulator simulator,
    ForkableSet<uint> processedEnemyDeaths) : IDisposable
{
    private CombatPredictionSimulator? _simulator = simulator;
    private ForkableSet<uint>? _processedEnemyDeaths = processedEnemyDeaths;

    public (CombatPredictionSimulator Simulator, ForkableSet<uint> ProcessedEnemyDeaths) Take()
    {
        CombatPredictionSimulator ownedSimulator = Interlocked.Exchange(ref _simulator, null)
            ?? throw new InvalidOperationException("并行动作 Fork seed 已被消费或释放。");
        ForkableSet<uint> ownedDeaths = Interlocked.Exchange(ref _processedEnemyDeaths, null)
            ?? throw new InvalidOperationException("并行动作死亡集合 seed 已被消费或释放。");
        return (ownedSimulator, ownedDeaths);
    }

    public void Dispose()
    {
        // Simulators do not own native resources. Clearing both roots is the explicit release
        // boundary for a seed that failed before dispatch or was canceled before consumption.
        Interlocked.Exchange(ref _simulator, null);
        Interlocked.Exchange(ref _processedEnemyDeaths, null);
    }
}

// This final frame carries only branch-owned replay accounting. Search performs the
// action boundary after all engine frames have returned, as in ordinary replay.
internal sealed record ExecutionReplayTailFrame(ForkableSet<uint> Deaths, int Turn,
    int ActionCount, int ShufflesCrossed, int ShuffleEventsBefore, int ConsumedChoices,
    PlayerStartProgress? PlayerStart, bool RootSetup = false) : ICombatPredictionExecutionFrame
{
    public int ConsumedChoices { get; set; } = ConsumedChoices;
    public void PrepareFork(PredictionForkContext context)
    {
        SimulatedCombatState.ForkExecutionDeaths(Deaths, context);
        PlayerStart?.Fork(context);
    }
    public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
        => this with { Deaths = context.RequireRemap(Deaths),
            PlayerStart = PlayerStart is null ? null : context.RequireRemap(PlayerStart) };
    public bool Resume(CombatPredictionSimulator simulator) => true;
}

internal sealed class ExecutionChoiceReplayCheckpoint(SearchNode? parent, PlanAction? action,
    IReadOnlyList<PlanCardChoice> prefix, CombatPredictionSimulator simulator,
    PredictionExecutionContinuation continuation, ExecutionReplayTailFrame tail) : IDisposable
{
    private readonly object _gate = new();
    private CombatPredictionSimulator? _simulator = simulator;
    private SearchNode? _parent = parent;
    private PlanAction? _action = action;
    private IReadOnlyList<PlanCardChoice>? _prefix = prefix;
    private PredictionExecutionContinuation? _continuation = continuation;
    private readonly bool _rootSetup = tail.RootSetup;
    private readonly bool _roundStart = tail.PlayerStart != null;
    public bool RootSetup => _rootSetup;
    public int PrefixCount { get; } = prefix.Count;
    public bool Matches(SearchNode candidate, PlanAction resolved)
    {
        if (!ReferenceEquals(_parent, candidate) || _action is null || _rootSetup) return false;
        if (resolved with { Choice = _action.Choice, NestedChoices = _action.NestedChoices,
            NestedChoicesBeforePrimary = _action.NestedChoicesBeforePrimary, TurnStartChoices = _action.TurnStartChoices } != _action)
            return false;
        IReadOnlyList<PlanCardChoice> choices = _roundStart
            ? resolved.TurnStartChoices ?? [] : ActionChoicesForReplay(resolved) ?? [];
        return MatchesPrefix(choices);
    }
    public bool MatchesPrefix(IReadOnlyList<PlanCardChoice> choices)
        => _prefix != null && choices.Count == PrefixCount + 1 && choices.Take(PrefixCount).SequenceEqual(_prefix);
    public IReadOnlyList<PlanCardChoice> RemainingChoices(PlanAction resolved)
        => (_roundStart ? resolved.TurnStartChoices ?? [] : ActionChoicesForReplay(resolved) ?? [])
            .Skip(PrefixCount).ToArray();
    public (CombatPredictionSimulator Simulator, PredictionExecutionContinuation Plan, ExecutionReplayTailFrame Tail)
        Fork(SearchReplayCore owner)
    {
        lock (_gate)
        {
            owner.CancellationToken.ThrowIfCancellationRequested();
            owner.Accounting.ExecutionChoiceReuses++;
            if (_rootSetup) owner.Accounting.ReplayCount++;
            else { owner.Accounting.ForkCount++; owner.Accounting.TransitionCount++; }
            using var measure = owner.Accounting.Performance.Measure(SearchMetricPhase.Fork);
            using PredictionForkContext context = new();
            var child = (_simulator ?? throw new ObjectDisposedException(nameof(ExecutionChoiceReplayCheckpoint)))
                .ForkExecutionContinuation(_continuation!, context, out var copied);
            return (child, copied, copied.Steps.Select(step => step.Frame).OfType<ExecutionReplayTailFrame>().Single());
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _simulator = null; _parent = null; _action = null; _prefix = null; _continuation = null;
        }
    }
}

internal sealed class CardChoiceReplayCapture(SearchNode parent, PlanAction action) : IDisposable
{
    private CardChoiceReplayCheckpoint? _checkpoint;
    public void Receive(SearchReplayCore owner, CombatPredictionSimulator simulator, ForkableSet<uint> deaths)
    {
        if (_checkpoint != null) throw new InvalidOperationException("Card choice capture received twice.");
        CardChoiceContinuation? continuation = CardChoiceContinuation.Take(simulator, deaths);
        if (continuation is null) return;
        try { _checkpoint = new CardChoiceReplayCheckpoint(parent, action, continuation); }
        catch { continuation.Dispose(); throw; }
        owner.Accounting.CardChoicePrefixCaptures++;
    }
    public CardChoiceReplayCheckpoint? Take()
    {
        CardChoiceReplayCheckpoint? result = _checkpoint;
        _checkpoint = null;
        return result;
    }
    public void Dispose() { _checkpoint?.Dispose(); _checkpoint = null; }
}

// A checkpoint is scoped to exactly one parent/action. The original primary-choice frontier
// owns it across all producers and its single ordered consumer; serial traversal owns it locally.
internal sealed class CardChoiceReplayCheckpoint(SearchNode parent, PlanAction action,
    CardChoiceContinuation continuation) : IDisposable
{
    private SearchNode? _parent = parent;
    private PlanAction? _action = action;
    private CardChoiceContinuation? _continuation = continuation;
    public bool Matches(SearchNode candidate, PlanAction resolved)
        => ReferenceEquals(_parent, candidate) && _action != null && resolved.Choice != null
            && resolved == (_action with { Choice = resolved.Choice });

    public ReplayForkSeed Fork(SearchReplayCore owner, out ManualCardChoiceFrame frame)
    {
        owner.Accounting.ForkCount++;
        owner.Accounting.CardChoicePrefixReuses++;
        using var measure = owner.Accounting.Performance.Measure(SearchMetricPhase.Fork);
        var child = (_continuation ?? throw new ObjectDisposedException(nameof(CardChoiceReplayCheckpoint)))
            .Fork(owner.CancellationToken);
        frame = child.Frame;
        return new ReplayForkSeed(child.Simulator, child.Deaths);
    }
    public void Dispose()
    {
        _continuation?.Dispose();
        _continuation = null; _parent = null; _action = null;
    }
}

internal sealed class PotionChoiceReplayCheckpoint(SearchNode parent, PlanAction action,
    PotionChoiceContinuation continuation) : IDisposable
{
    private SearchNode? _parent = parent;
    private PlanAction? _action = action;
    private PotionChoiceContinuation? _continuation = continuation;

    // The original no-selection probe is also resumed, so its existing after-use hooks
    // still run before generated options are read by the unchanged choice builder.
    public bool Matches(SearchNode candidate, PlanAction resolved)
        => ReferenceEquals(_parent, candidate) && _action != null
            && resolved == (_action with { Choice = resolved.Choice });

    public ReplayForkSeed Fork(SearchReplayCore owner, out PotionChoiceFrame frame)
    {
        owner.Accounting.ForkCount++; owner.Accounting.PotionChoicePrefixReuses++;
        using var measure = owner.Accounting.Performance.Measure(SearchMetricPhase.Fork);
        var child = (_continuation ?? throw new ObjectDisposedException(nameof(PotionChoiceReplayCheckpoint)))
            .Fork(owner.CancellationToken);
        frame = child.Frame;
        return new ReplayForkSeed(child.Simulator, child.Deaths);
    }

    public void Dispose()
    {
        _continuation?.Dispose(); _continuation = null; _parent = null; _action = null;
    }
}

// A checkpoint belongs to one admitted parent's EndTurn frontier. It is released only
// after all replay producers and the ordered continuation have drained.
internal sealed class RoundReplayCheckpoint(
    SearchNode parent,
    CombatPredictionSimulator simulator,
    ForkableSet<uint> deaths,
    int shufflesCrossed,
    bool takingExtraTurn,
    bool sideTurnStartTriggeredEarly,
    int? handDrawCount = null) : IDisposable
{
    private CombatPredictionSimulator? _simulator = simulator;
    private ForkableSet<uint>? _deaths = deaths;
    public int ShufflesCrossed { get; } = shufflesCrossed;
    public int? HandDrawCount { get; } = handDrawCount;
    public bool TakingExtraTurn { get; } = takingExtraTurn;
    public bool SideTurnStartTriggeredEarly { get; } = sideTurnStartTriggeredEarly;
    public bool Matches(SearchNode candidate, PlanAction action)
        => ReferenceEquals(parent, candidate)
            && action.Kind == PlanActionKind.EndTurn
            && action.Turn == parent.Turn
            && action.TurnStartChoices is { Count: > 0 } choices
            && choices.All(choice => choice.Timing == PlanChoiceTiming.PlayerTurnStart
                && choice.Effect != PlanChoiceEffect.ApplyKnowledgeCurse);

    public ReplayForkSeed Fork(SearchReplayCore owner, object gate, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.Accounting.ForkCount++;
            owner.Accounting.RoundReplayPrefixReuses++;
            using var measure = owner.Accounting.Performance.Measure(SearchMetricPhase.Fork);
            return new ReplayForkSeed(
                (_simulator ?? throw new InvalidOperationException("Round checkpoint was released.")).Fork(),
                (_deaths ?? throw new InvalidOperationException("Round checkpoint was released.")).Fork());
        }
    }

    public void Dispose()
    {
        _simulator = null;
        _deaths = null;
    }
}

internal sealed class RoundReplayCheckpointCapture(SearchNode parent) : IDisposable
{
    private RoundReplayCheckpoint? _checkpoint;
    public bool ReachedStablePrefix { get; private set; }
    public bool ReachedHandDrawShuffle { get; private set; }
    public bool HasCheckpoint => _checkpoint is not null;
    public void ObservePendingChoice(SearchReplayCore owner, string? sourceId = null)
    {
        if (ReachedStablePrefix)
            owner.Accounting.HasObservedPostDrawRoundChoice = true;
        else if (ReachedHandDrawShuffle && sourceId != null)
            (owner.Accounting.ObservedHandDrawShuffleChoiceSources ??= new(StringComparer.Ordinal)).Add(sourceId);
    }
    public void CaptureBeforeHandDraw(SearchReplayCore owner, CombatPredictionSimulator simulator,
        SimulatedCombatState combat, TurnStartChoiceCursor cursor,
        ISet<uint> deaths, int shufflesCrossed, bool takingExtraTurn,
        bool sideTurnStartTriggeredEarly, int drawCount, bool willShuffle)
    {
        if (!willShuffle || !simulator.IsInProgress || combat.PlayerTurnEndRequested
            || combat.HasPendingChoice)
            return;
        ReachedHandDrawShuffle = true;
        if (owner.Accounting.ObservedHandDrawShuffleChoiceSources is not { Count: > 0 } sources)
            return;
        // Hints contain source identifiers only. Keep ordinary branches on the later
        // post-draw prefix when their observed choice-producing power is absent.
        IReadOnlyList<PowerModel> powers = combat.EffectivePowers();
        for (int index = 0; index < powers.Count; index++)
        {
            PowerModel power = powers[index];
            if (power.Amount > 0 && ReferenceEquals(power.Owner, owner.Player.Creature)
                && sources.Contains(power.Id.Entry))
            {
                CaptureCore(owner, simulator, combat, cursor, deaths, shufflesCrossed,
                    takingExtraTurn, sideTurnStartTriggeredEarly, drawCount);
                return;
            }
        }
    }
    public void Capture(SearchReplayCore owner, CombatPredictionSimulator simulator,
        SimulatedCombatState combat, TurnStartChoiceCursor cursor,
        ISet<uint> deaths, int shufflesCrossed, bool takingExtraTurn, bool sideTurnStartTriggeredEarly)
    {
        if (!simulator.IsInProgress || combat.PlayerTurnEndRequested)
            return;
        ReachedStablePrefix = true;
        // A pre-draw prefix already covers this parent's earlier choice point.
        // Other duplicate captures still fail in CaptureCore.
        if (_checkpoint is { HandDrawCount: not null })
            return;
        // Keep the existing immediate reservation, and learn other sources from a
        // completed probe. Outside the existing reservation, a lane pays for no
        // copies until it has observed a post-draw choice.
        // Every copy still belongs to this parent; the hint never shares state.
        if (!owner.Accounting.HasObservedPostDrawRoundChoice
            && combat.GetAmount<ToolsOfTheTradePower>(owner.Player.Creature) <= 0)
            return;
        CaptureCore(owner, simulator, combat, cursor, deaths, shufflesCrossed,
            takingExtraTurn, sideTurnStartTriggeredEarly, handDrawCount: null);
    }
    private void CaptureCore(SearchReplayCore owner, CombatPredictionSimulator simulator,
        SimulatedCombatState combat, TurnStartChoiceCursor cursor,
        ISet<uint> deaths, int shufflesCrossed, bool takingExtraTurn,
        bool sideTurnStartTriggeredEarly, int? handDrawCount)
    {
        if (_checkpoint != null)
            throw new InvalidOperationException("Round prefix captured twice.");
        PlanChoiceTiming timing = combat.ActiveActionChoiceTiming;
        // This initial probe has no planned choices. All prefix commands have returned;
        // the original Fork assertions still check every engine and model transaction.
        combat.EndActionChoices();
        try
        {
            owner.Accounting.ForkCount++;
            owner.Accounting.RoundReplayPrefixCaptures++;
            using var measure = owner.Accounting.Performance.Measure(SearchMetricPhase.Fork);
            var fork = simulator.ForkStableExecutionPrefix();
            _checkpoint = new(parent, fork, ((ForkableSet<uint>)deaths).Fork(),
                shufflesCrossed, takingExtraTurn, sideTurnStartTriggeredEarly, handDrawCount);
        }
        finally
        {
            combat.BeginActionChoices(cursor);
            combat.SetActionChoiceTiming(timing);
        }
    }
    public RoundReplayCheckpoint? Take()
    {
        var result = _checkpoint;
        _checkpoint = null;
        return result;
    }
    public void Dispose()
    {
        _checkpoint?.Dispose();
        _checkpoint = null;
    }
}

internal sealed class ContinuationCapture(IReadOnlyList<(int ActionIndex, int Turn)> boundaries)
{
    private readonly List<CachedContinuation> _continuations = new(boundaries.Count);

    public void Observe(SearchReplayCore solver, CombatPredictionSimulator simulator,
        int actionIndex, int turn, SearchBoundaryReason boundary)
    {
        if (_continuations.Count == boundaries.Count)
            return;
        var expected = boundaries[_continuations.Count];
        if (actionIndex < expected.ActionIndex)
            return;
        if (actionIndex != expected.ActionIndex || turn != expected.Turn
            || boundary != SearchBoundaryReason.None || simulator.HasPendingChoice
            || simulator.TerminalStamp != null)
        {
            throw new InvalidOperationException(
                $"续用回放未抵达预期稳定边界：action={actionIndex}/{expected.ActionIndex} " +
                $"turn={turn}/{expected.Turn} boundary={boundary}。");
        }
        ((SimulatedCombatState)simulator.State.CombatState).AssertForkable();
        ContinuationStamp stamp = ContinuationStamp.CapturePredicted(
            solver.Player, simulator, turn, solver.Forecast, solver.StartTurnNumber);
        _continuations.Add(new CachedContinuation(stamp, turn, turn - solver.StartTurnNumber));
    }

    public IReadOnlyList<CachedContinuation> Complete()
    {
        if (_continuations.Count != boundaries.Count)
            throw new InvalidOperationException(
                $"续用回放未采集全部边界：{_continuations.Count}/{boundaries.Count}。");
        return _continuations;
    }
}
