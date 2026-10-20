using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

internal sealed partial class CombatBeamSolver
{
    private bool _disableCardChoiceContinuationsForTesting;
    internal bool DisableCardChoiceContinuationsForTesting
    { init => _disableCardChoiceContinuationsForTesting = value; }

    internal int VerifyCardChoiceContinuationForTesting()
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        SearchNode parent = new(null, 0, rootSnapshot.PotionUseCount,
            rootSnapshot.PotionStrategicCost, rootSnapshot.Turn, SearchRouteTraits.None,
            0, rootSnapshot.Score, rootSnapshot.StateKey, rootSnapshot.HasRisk,
            rootSnapshot.BoundaryReason, false, null, rootSnapshot,
            CombatProgressState.Capture(rootSnapshot));
        ContinuationStamp before = CaptureDiagnosticContinuation(rootSnapshot);
        int checkedBranches = 0;
        try
        {
            foreach (PreparedCardAction prepared in PrepareCardActions(parent)
                .Where(c => CombatPredictionSimulator.SupportsManualCardChoiceContinuation(c.Action.CardId)))
            {
                PlanAction action = prepared.Action;
                using CardChoiceReplayCapture capture = PrepareCardChoiceCapture(parent, action)!;
                SimulationSnapshot probe = ReplayAction(parent, action, cardChoiceCapture: capture);
                using CardChoiceReplayCheckpoint checkpoint = capture.Take()
                    ?? throw new InvalidOperationException("Search discovery did not capture card continuation.");
                PlanCardChoice[] choices;
                try
                {
                    CardChoiceSpec spec = BuildPrimaryCardChoiceSpec(_player, probe)!;
                    choices = CardChoiceSupport.BuildChoices(spec, displayNames, 32, 32).ToArray();
                }
                finally { probe.ReleaseSimulator(); }
                foreach (PlanCardChoice choice in choices.Concat(choices.Take(1)))
                {
                    PlanAction resolved = action with { Choice = choice };
                    SimulationSnapshot full = ReplayAction(parent, resolved);
                    SimulationSnapshot? resumed = null;
                    int transitions = _run.TransitionCount;
                    try
                    {
                        _parallelActionReplayForkGate = new object();
                        _cardChoiceReplayCheckpoint = checkpoint;
                        resumed = ReplayAction(parent, resolved);
                        AssertIncrementalEquivalent(resolved, [resolved], resumed, full);
                        if (resumed.ShufflesCrossed != full.ShufflesCrossed
                            || resumed.Simulator.History.Entries.Count != full.Simulator.History.Entries.Count
                            || _run.TransitionCount != transitions + 1)
                            throw new InvalidOperationException("Card continuation history/shuffle/budget differs.");
                        checkedBranches++;
                    }
                    finally
                    {
                        _parallelActionReplayForkGate = null;
                        _cardChoiceReplayCheckpoint = null;
                        resumed?.ReleaseSimulator(); full.ReleaseSimulator();
                    }
                }
            }
            if (checkedBranches < 6 || _run.CardChoicePrefixReuses != checkedBranches
                || _run.CardChoicePrefixFallbacks <= 0 || CaptureDiagnosticContinuation(rootSnapshot) != before)
                throw new InvalidOperationException("Search continuation siblings/fallback/isolation not exercised.");
            return checkedBranches;
        }
        finally { rootSnapshot.ReleaseSimulator(); }
    }

    private CardChoiceReplayCapture? PrepareCardChoiceCapture(SearchNode parent, PlanAction action)
    {
        if (_disableCardChoiceContinuationsForTesting || action.Kind != PlanActionKind.PlayCard
            || !CombatPredictionSimulator.SupportsManualCardChoiceContinuation(action.CardId)
            || action.ReplayCount != 0 || action.Choice != null
            || action.NestedChoices is { Count: > 0 } || action.TurnStartChoices is { Count: > 0 })
            return null;
        _run.CardChoicePrefixAttempts++;
        return new CardChoiceReplayCapture(parent, action);
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> WithCardChoiceCheckpoint(
        CardChoiceReplayCheckpoint? checkpoint,
        IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> branches)
    {
        if (checkpoint is null) return branches;
        return Enumerate(this, checkpoint, branches);
        static IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> Enumerate(
            CombatBeamSolver owner, CardChoiceReplayCheckpoint checkpoint,
            IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> branches)
        {
            using (checkpoint)
            {
                CardChoiceReplayCheckpoint? previous = owner._cardChoiceReplayCheckpoint;
                owner._cardChoiceReplayCheckpoint = checkpoint;
                try
                {
                    foreach (var branch in branches) yield return branch;
                }
                finally { owner._cardChoiceReplayCheckpoint = previous; }
            }
        }
    }
}
