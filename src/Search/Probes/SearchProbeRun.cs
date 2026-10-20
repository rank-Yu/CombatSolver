using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// Scratch and metric storage for one probe lifetime. It deliberately has no Beam frontier,
/// transposition table or retention state: probes are read-only over the frozen root.
/// </summary>
internal sealed class SearchProbeRun
{
    public readonly SearchPerformanceMetrics Performance = new(enabled: false);
    public readonly SnapshotListBuffer<PredictedCard> SnapshotLiveCards = new();
    public readonly Dictionary<(StateFingerprint State, int RoundIndex), ThreatProjection>
        ThreatProjectionCache = [];
    public readonly Dictionary<PredictionRiskSignature, CoverageSummary> CoverageCache = [];
    public int ReplayCount;
    public int ForkCount;
    public int TransitionCount;
    public int Expanded;
}
