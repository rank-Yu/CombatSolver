using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

internal sealed partial class CombatBeamSolver
{
    private SearchBoundaryReason AdvanceRoundPlayerStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        SimPlayerCombatState playerState,
        SimCreatureState simulatedPlayer,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        ref int shufflesCrossed,
        TurnStartChoiceCursor roundChoices,
        bool takingExtraTurn,
        RoundReplayCheckpointCapture? capture = null)
        => SearchRoundExecution.AdvanceRoundPlayerStart(
            RoundContext, simulator, simulatedCombat, playerState, simulatedPlayer,
            roundIndex, processedEnemyDeaths, ref shufflesCrossed, roundChoices,
            takingExtraTurn, capture);

    internal int VerifyRoundReplayCheckpointForTesting(
        bool learnFromProbe = false, bool handDrawShuffle = false)
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        SearchNode parent = new(null, 0, rootSnapshot.PotionUseCount,
            rootSnapshot.PotionStrategicCost, rootSnapshot.Turn, SearchRouteTraits.None,
            0, rootSnapshot.Score, rootSnapshot.StateKey, rootSnapshot.HasRisk,
            rootSnapshot.BoundaryReason, false, null, rootSnapshot,
            CombatProgressState.Capture(rootSnapshot));
        ContinuationStamp parentBefore = CaptureDiagnosticContinuation(rootSnapshot);
        using RoundReplayCheckpointCapture capture = new(parent);
        SimulationSnapshot? probe = null;
        try
        {
            PlanAction endTurn = new(PlanActionKind.EndTurn, parent.Turn);
            if (learnFromProbe || handDrawShuffle)
            {
                using RoundReplayCheckpointCapture discovery = new(parent);
                SimulationSnapshot first = ReplayAction(parent, endTurn, roundCheckpointCapture: discovery);
                try
                {
                    bool reachedExpectedPoint = handDrawShuffle
                        ? discovery.ReachedHandDrawShuffle && !discovery.ReachedStablePrefix
                        : discovery.ReachedStablePrefix;
                    if (discovery.HasCheckpoint || !reachedExpectedPoint
                        || first.BoundaryReason != SearchBoundaryReason.PendingChoice)
                        throw new InvalidOperationException("Adaptive prefix fixture did not learn from the expected uncached choice.");
                    discovery.ObservePendingChoice(ReplayCore,
                        ((SimulatedCombatState)first.Simulator.State.CombatState).PendingTurnStartChoice?.SourceId);
                }
                finally { first.ReleaseSimulator(); }
            }
            if (handDrawShuffle)
            {
                var withoutSource = (CombatPredictionSimulator)rootSnapshot.Simulator.Fork();
                var withoutSourceCombat = (SimulatedCombatState)withoutSource.State.CombatState;
                withoutSourceCombat.SetAmount<StratagemPower>(_player.Creature, 0);
                using RoundReplayCheckpointCapture inactive = new(parent);
                inactive.CaptureBeforeHandDraw(ReplayCore, withoutSource, withoutSourceCombat,
                    new TurnStartChoiceCursor(null), new ForkableSet<uint>(), 0,
                    takingExtraTurn: false, sideTurnStartTriggeredEarly: false,
                    drawCount: 5, willShuffle: true);
                if (inactive.HasCheckpoint)
                    throw new InvalidOperationException("Inactive shuffle-choice source displaced the post-draw prefix.");
            }
            probe = ReplayAction(parent, endTurn, roundCheckpointCapture: capture);
            using RoundReplayCheckpoint checkpoint = capture.Take()
                ?? throw new InvalidOperationException("Round prefix fixture did not capture a checkpoint.");
            if (checkpoint.HandDrawCount.HasValue != handDrawShuffle)
                throw new InvalidOperationException("Round prefix fixture captured the wrong continuation point.");
            SimulatedCombatState combat = (SimulatedCombatState)probe.Simulator.State.CombatState;
            var request = combat.PendingTurnStartChoice
                ?? throw new InvalidOperationException("Round prefix fixture did not reach a turn-start choice.");
            CardChoiceSpec spec = TurnStartChoiceSupport.BuildPendingSpec(
                (CombatPredictionSimulator)probe.Simulator, combat, _player);
            var choices = CardChoiceSupport.BuildChoices(spec, displayNames,
                _profile.MaxPileChoiceBranchesPerAction, _profile.MaxHandChoiceBranchesPerAction);
            int checkedBranches = 0;
            // Revisit the first sibling after all other writes to expose shared mutable state.
            foreach (var choice in choices.Concat(choices.Take(1)))
            {
                PlanAction action = endTurn with { TurnStartChoices = [choice with
                {
                    SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing,
                }] };
                SimulationSnapshot full = ReplayAction(parent, action);
                SimulationSnapshot? resumed = null;
                try
                {
                    _parallelActionReplayForkGate = new object();
                    _roundReplayCheckpoint = checkpoint;
                    resumed = ReplayAction(parent, action);
                    AssertIncrementalEquivalent(action, [action], resumed, full);
                    if (resumed.ShufflesCrossed != full.ShufflesCrossed
                        || resumed.Simulator.History.Entries.Count != full.Simulator.History.Entries.Count)
                        throw new InvalidOperationException("Round prefix history/shuffle accounting differs.");
                    checkedBranches++;
                }
                finally
                {
                    _parallelActionReplayForkGate = null;
                    _roundReplayCheckpoint = null;
                    resumed?.ReleaseSimulator();
                    full.ReleaseSimulator();
                }
            }
            if (checkedBranches < 3 || _run.RoundReplayPrefixReuses != checkedBranches
                || CaptureDiagnosticContinuation(rootSnapshot) != parentBefore)
                throw new InvalidOperationException("Round prefix sibling isolation/reuse was not proved.");
            return checkedBranches;
        }
        finally
        {
            probe?.ReleaseSimulator();
            rootSnapshot.ReleaseSimulator();
        }
    }

}
