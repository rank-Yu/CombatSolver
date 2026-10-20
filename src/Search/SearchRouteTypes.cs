using MegaCrit.Sts2.Core.Entities.Cards;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record RouteAnnotations(
    IReadOnlyDictionary<int, int> HpLostByTurn,
    IReadOnlyDictionary<int, int> HpRecoveredByTurn,
    IReadOnlyDictionary<int, int> EnemyHpLostByTurn,
    IReadOnlyDictionary<int, int> SoldHpByTurn,
    IReadOnlyDictionary<int, int> MaxBlockByTurn,
    IReadOnlyDictionary<int, int> ActualBlockByTurn,
    IReadOnlyDictionary<int, int> EnergyLeftByTurn,
    IReadOnlyDictionary<int, int> PotionCountByTurn,
    IReadOnlyDictionary<int, int> PotionStrategicCostByTurn,
    IReadOnlyDictionary<int, IReadOnlyList<string>> KillsAfterAction,
    int? CombatEndedTurn,
    int? DeathTurn);

internal readonly record struct SearchFeatures(
    bool AllEnemiesDead,
    int ProjectedPlayerHp,
    int PlayerHp,
    int PlayerMaxHp,
    int CumulativePlayerHpLost,
    int RecoveredPlayerHp,
    int DeathSaveHpRestored,
    int LongTermResourceValue,
    int AngerCopiesGenerated,
    int PlayerBlock,
    int AliveEnemyCount,
    int EnemyHp,
    int RawEnemyHp,
    int MaxCurrentEnemyHp,
    int OutstandingStolenResource,
    int PersistentBuffValue,
    int LatentSetupValue,
    int FutureResourceValue,
    int RetainedAttackValue,
    int DelayedDamageValue,
    int ReactiveDamageValue,
    int SandpitRemaining,
    int LiveDeckClutter,
    int Energy,
    int Stars,
    int HandCount,
    int RevivingEnemyCount,
    int FocusTargetPressure,
    int FocusTargetRemainingHp,
    int FocusTargetCurrentThreat,
    int PotionCount,
    int FutureSoldHp,
    int ActionCount,
    double Score,
    SearchRouteTraits Traits,
    SearchBoundaryReason BoundaryReason)
{
    public static SearchFeatures Capture(SearchNode node)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        return new SearchFeatures(
            snapshot.AllEnemiesDead,
            snapshot.ProjectedPlayerHp,
            snapshot.PlayerHp,
            snapshot.PlayerMaxHp,
            snapshot.CumulativePlayerHpLost,
            snapshot.RecoveredPlayerHp,
            snapshot.DeathSaveHpRestored,
            snapshot.LongTermResourceValue,
            snapshot.AngerCopiesGenerated,
            snapshot.PlayerBlock,
            snapshot.AliveEnemyCount,
            snapshot.EnemyHp,
            snapshot.RawEnemyHp,
            snapshot.MaxCurrentEnemyHp,
            snapshot.OutstandingStolenResource,
            snapshot.PersistentBuffValue,
            snapshot.LatentSetupValue,
            snapshot.FutureResourceValue,
            snapshot.RetainedAttackValue,
            snapshot.DelayedDamageValue,
            snapshot.ReactiveDamageValue,
            snapshot.SandpitRemaining,
            snapshot.LiveDeckClutter,
            snapshot.Energy,
            snapshot.Stars,
            snapshot.HandCount,
            snapshot.RevivingEnemyCount,
            snapshot.FocusTargetPressure,
            snapshot.FocusTargetRemainingHp,
            snapshot.FocusTargetCurrentThreat,
            node.PotionCount,
            node.FutureSoldHp,
            node.ActionCount,
            node.Score,
            node.Traits,
            snapshot.BoundaryReason);
    }
}

internal sealed record FinalPlanCandidate(
    SearchNode Node,
    SimulationSnapshot Snapshot,
    SearchFeatures Features,
    int FutureSold,
    int BattleSold,
    int PotionCount,
    double Score);

internal sealed record FinalPlanSelection(
    FinalPlanCandidate Candidate,
    int PotionBranchesRejected,
    int PotionHpSaved,
    int PotionHpRequired);

