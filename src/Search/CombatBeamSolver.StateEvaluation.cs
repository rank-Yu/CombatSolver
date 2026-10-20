using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

internal sealed partial class CombatBeamSolver
{
    // The scoring and fingerprint implementation is shared with the opening probes. This file only
    // keeps the solver-facing adapter and the testing entry points that already existed.
    private SimulationSnapshot Snapshot(
        CombatPredictionSimulator simulator,
        int turn,
        int actionCount,
        int shufflesCrossed,
        SearchBoundaryReason boundary,
        IReadOnlySet<uint> processedEnemyDeaths)
        => Evaluator.Snapshot(
            simulator, turn, actionCount, shufflesCrossed, boundary, processedEnemyDeaths);

    private StateFingerprint BuildPlayableCardKey(PredictedCard card)
        => Evaluator.BuildPlayableCardKey(card);

    internal int ProjectDiagnosticHits(SimulationSnapshot snapshot, Creature attacker, params int[] hits)
        => Evaluator.ProjectDiagnosticHits(snapshot, attacker, hits);

    internal (int Hp, int DeathSaveUseCount, int DeathSaveHpRestored) ProjectDiagnosticEnemyTurn(
        SimulationSnapshot snapshot)
        => Evaluator.ProjectDiagnosticEnemyTurn(snapshot);

    internal (int Hp, int DeathSaveUseCount, int DeathSaveHpRestored) ProjectDiagnosticThreat(
        SimulationSnapshot snapshot, Creature attacker, params int[] hits)
        => Evaluator.ProjectDiagnosticThreat(snapshot, attacker, hits);

    internal static int CaptureVoidFormOpportunityValueForTesting(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        SimPlayerCombatState playerState,
        Creature owner)
        => SearchEvaluator.CaptureVoidFormOpportunityValueForTesting(
            simulator, combat, playerState, owner);

    internal static void StableShuffleProjection(List<PredictedCard> cards, MegaCrit.Sts2.Core.Random.Rng rng)
        => SearchEvaluator.StableShuffleProjection(cards, rng);

    internal static StateFingerprint CaptureRngStateFingerprintForTesting(MegaCrit.Sts2.Core.Random.Rng rng)
        => SearchEvaluator.CaptureRngStateFingerprintForTesting(rng);

    internal static StateFingerprint CaptureCardStateFingerprintForTesting(PredictedCard card)
        => SearchEvaluator.CaptureCardStateFingerprintForTesting(card);
    internal static bool SettleReplayActionBoundary(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
        => SearchProbeHelpers.SettleReplayActionBoundary(simulator, combat);
}
