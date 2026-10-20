using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// Scratch and metric storage for one probe lifetime. It deliberately has no Beam frontier,
/// transposition table or retention state: probes are read-only over the frozen root.
/// </summary>
internal sealed class SearchProbeRun
{
    public readonly SearchReplayAccounting Replay = new(measurePhasePerformance: false, framePressureSignal: null);
    public SearchPerformanceMetrics Performance => Replay.Performance;
    public readonly SnapshotListBuffer<PredictedCard> SnapshotLiveCards = new();
    public readonly Dictionary<(StateFingerprint State, int RoundIndex), ThreatProjection>
        ThreatProjectionCache = [];
    public readonly Dictionary<PredictionRiskSignature, CoverageSummary> CoverageCache = [];
    public int ReplayCount { get => Replay.ReplayCount; set => Replay.ReplayCount = value; }
    public int ForkCount { get => Replay.ForkCount; set => Replay.ForkCount = value; }
    public int TransitionCount { get => Replay.TransitionCount; set => Replay.TransitionCount = value; }
    public int Expanded;
}
