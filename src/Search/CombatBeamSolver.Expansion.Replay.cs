using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

/// <summary>
/// Beam-facing adapters over the single <see cref="SearchReplayCore"/>. These methods only
/// forward; the replay semantics live in <c>Probes/SearchReplayCore.cs</c>.
/// </summary>
internal sealed partial class CombatBeamSolver
{
    private IReadOnlyList<(IReadOnlyList<PlanCardChoice> Choices, SimulationSnapshot Snapshot)>
        BuildTurnSetupRoots()
        => ReplayCore.CaptureTurnSetupRoots()
            .Select(candidate => (candidate.Choices, candidate.Snapshot))
            .ToArray();

    private void ResolveTurnSetupChoices(IReadOnlyList<PlanCardChoice> choices,
        SimulationSnapshot snapshot,
        List<(IReadOnlyList<PlanCardChoice>, SimulationSnapshot)> roots,
        ChoiceSearchBudget searchBudget,
        ChoiceOccurrenceCollector<IReadOnlyList<PlanCardChoice>> occurrenceCollector,
        TurnSetupChoiceLayer? preparedLayer = null)
        => ReplayCore.ResolveTurnSetupChoicesForCaller(
            choices, snapshot, roots, searchBudget, occurrenceCollector, preparedLayer);

    private SimulationSnapshot ReplayTurnSetup(IReadOnlyList<PlanCardChoice> choices,
        bool allowExecutionCapture = true)
        => ReplayCore.ReplayTurnSetup(choices, allowExecutionCapture);

    private SimulationSnapshot Replay(IReadOnlyList<PlanAction> actions,
        SimulationSnapshot? parentSnapshot = null,
        int startingTurn = 0,
        int priorActionCount = 0,
        ActionRelicTriggerRecorder? triggerRecorder = null,
        ReplayForkSeed? replayForkSeed = null,
        SearchReplayEvidence? replayEvidence = null,
        RoundReplayCheckpoint? roundCheckpoint = null,
        RoundReplayCheckpointCapture? roundCheckpointCapture = null,
        CardChoiceReplayCapture? cardChoiceCapture = null,
        ManualCardChoiceFrame? cardChoiceFrame = null,
        PotionChoiceFrame? potionChoiceFrame = null,
        bool countTransition = true,
        bool allowExecutionCapture = true,
        ContinuationCapture? continuationCapture = null)
        => ReplayCore.Replay(actions, parentSnapshot, startingTurn, priorActionCount,
            triggerRecorder, replayForkSeed, replayEvidence, roundCheckpoint,
            roundCheckpointCapture, cardChoiceCapture, cardChoiceFrame, potionChoiceFrame,
            countTransition, allowExecutionCapture, continuationCapture);

    private SimulationSnapshot ReplayAction(
        SearchNode parent,
        PlanAction action,
        ReplayForkSeed? replayForkSeed = null,
        RoundReplayCheckpointCapture? roundCheckpointCapture = null,
        CardChoiceReplayCapture? cardChoiceCapture = null)
        => ReplayCore.ReplayAction(
            parent, action, replayForkSeed, roundCheckpointCapture, cardChoiceCapture);

    private SimulationSnapshot? ReplayPlannedChoiceBranch(
        SearchNode parent,
        PlanAction action,
        ReplayForkSeed? replayForkSeed = null)
    {
        try
        {
            return ReplayAction(parent, action, replayForkSeed);
        }
        catch (InvalidPlannedChoiceBranchException ex)
        {
            if (_detailedDiagnostics)
            {
                policy.Diagnostics.Debug(
                    $"[CombatSolver/Test] CHOICE_REPLAY_PRUNED action={PolicyActionToken(action)} " +
                    $"reason={ex.Message}");
            }
            return null;
        }
    }

    private void AssertIncrementalEquivalent(
        PlanAction action,
        IReadOnlyList<PlanAction> fullActions,
        SimulationSnapshot incremental,
        SimulationSnapshot replayed)
        => ReplayCore.AssertIncrementalEquivalentForCaller(action, fullActions, incremental, replayed);

    private SearchBoundaryReason AdvanceRound(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        ref int shufflesCrossed,
        IReadOnlyList<PlanCardChoice>? turnStartChoices,
        RoundReplayCheckpointCapture? roundCheckpointCapture = null)
        => SearchRoundExecution.AdvanceRound(
            RoundContext, simulator, simulatedCombat, roundIndex, processedEnemyDeaths,
            ref shufflesCrossed, turnStartChoices, roundCheckpointCapture);
}
