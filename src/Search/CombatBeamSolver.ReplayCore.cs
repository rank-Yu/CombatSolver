namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private SearchReplayCore? _replayCore;

    private SearchReplayCore ReplayCore => _replayCore ??= CreateReplayCore();

    internal SearchReplayCore ReplayCoreForTesting => ReplayCore;

    private SearchReplayCore CreateReplayCore()
    {
        SearchReplayCore core = new(new SearchReplayOptions(
            root,
            displayNames,
            policy,
            _player,
            _startTurnNumber,
            _forecast,
            Evaluator,
            RoundContext,
            _run.Replay,
            ReplayCancellationToken,
            _includeTurnSetup,
            policy.Diagnostics,
            _detailedDiagnostics))
        {
            Profile = _profile,
        };
        core.Testing.DisableExecutionChoiceContinuations =
            _disableExecutionChoiceContinuationsBacking;
        core.Testing.VerifyChoiceContinuationSteps =
            _verifyChoiceContinuationStepsBacking;
        core.VerifyRejectedChoiceContinuationStep =
            (parent, action, error) => VerifyRejectedChoiceContinuationStepForTesting(parent, action, error);
        core.VerifyChoiceContinuationStep =
            (parent, action, incremental) => VerifyChoiceContinuationStepForTesting(parent, action, incremental);
        return core;
    }

    private ExecutionChoiceReplayCheckpoint? TakeExecutionChoiceCheckpoint(
        SearchNode? parent, PlanAction? action, SimulationSnapshot snapshot,
        IReadOnlyList<PlanCardChoice>? setupChoices = null)
        => ReplayCore.TakeExecutionChoiceCheckpointForTesting(parent, action, snapshot, setupChoices);

    private SimulationSnapshot ResumeExecutionChoice(ExecutionChoiceReplayCheckpoint checkpoint,
        SearchNode? parent, PlanAction? action, IReadOnlyList<PlanCardChoice>? setupChoices = null)
        => ReplayCore.ResumeExecutionChoiceForCaller(checkpoint, parent, action, setupChoices);

    private ReplayForkSeed PrepareReplayForkSeed(
        SimulationSnapshot parentSnapshot,
        object? forkGate = null)
        => ReplayCore.PrepareReplayForkSeedForCaller(parentSnapshot, forkGate);

    private object? _parallelActionReplayForkGate
    {
        get => ReplayCore.ParallelActionReplayForkGate;
        set => ReplayCore.ParallelActionReplayForkGate = value;
    }

    private ExecutionChoiceReplayCheckpoint? _executionChoiceReplayCheckpoint
    {
        get => ReplayCore.ExecutionChoiceCheckpoint;
        set => ReplayCore.ExecutionChoiceCheckpoint = value;
    }

    private PotionChoiceReplayCheckpoint? _potionChoiceReplayCheckpoint
    {
        get => ReplayCore.PotionChoiceCheckpoint;
        set => ReplayCore.PotionChoiceCheckpoint = value;
    }

    private CardChoiceReplayCheckpoint? _cardChoiceReplayCheckpoint
    {
        get => ReplayCore.CardChoiceCheckpoint;
        set => ReplayCore.CardChoiceCheckpoint = value;
    }

    private RoundReplayCheckpoint? _roundReplayCheckpoint
    {
        get => ReplayCore.RoundCheckpoint;
        set => ReplayCore.RoundCheckpoint = value;
    }

    private bool _disableExecutionChoiceContinuationsBacking;
    private bool _verifyChoiceContinuationStepsBacking;

    private bool _disableExecutionChoiceContinuationsForTesting
    {
        get => _replayCore?.Testing.DisableExecutionChoiceContinuations
            ?? _disableExecutionChoiceContinuationsBacking;
        set
        {
            _disableExecutionChoiceContinuationsBacking = value;
            if (_replayCore != null)
                _replayCore.Testing.DisableExecutionChoiceContinuations = value;
        }
    }

    private bool _verifyChoiceContinuationStepsForTesting
    {
        get => _replayCore?.Testing.VerifyChoiceContinuationSteps
            ?? _verifyChoiceContinuationStepsBacking;
        set
        {
            _verifyChoiceContinuationStepsBacking = value;
            if (_replayCore != null)
                _replayCore.Testing.VerifyChoiceContinuationSteps = value;
        }
    }
}
