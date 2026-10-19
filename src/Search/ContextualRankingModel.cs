using System.Text.Json;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// Optional, immutable correction to intermediate Beam ranking. This is an estimate,
/// never an admissible bound; final policy and exact dominance do not consume it.
/// Loading/validation belongs to the caller before Solve, not expansion workers.
/// </summary>
internal sealed class ContextualRankingModel
{
    internal const int FeatureCount = 28;
    internal static IReadOnlyList<string> FeatureNames { get; } = Array.AsReadOnly(new[]
    {
        "energy", "hp_pressure", "incoming_fraction", "enemy_hp", "hand", "free_plays",
        "reachable_hand", "persistent", "setup", "retained_attack", "replay", "future_resource",
        "delayed_damage", "clutter_fraction", "strength_suppression", "weak_turns", "vulnerable_turns",
        "deck_size", "turn", "energy_x_pressure", "energy_x_incoming", "hand_x_energy",
        "persistent_x_pressure", "setup_x_pressure", "future_x_pressure",
        "delayed_x_enemy_hp", "reachable_x_incoming", "free_x_incoming",
    });

    internal sealed record Document(int SchemaVersion, string SolverAssemblyId, string GameAssemblyId,
        string[] FeatureNames, double[] Weights, double[] Minimum, double[] Maximum,
        double MaximumAdjustmentHp, string ModelId);

    /// <summary>
    /// The scalar node context the model reads. <see cref="SearchNode"/> and the Flash scorer both
    /// map their own state into this value, so both compute the same 28 features and adjustment.
    /// </summary>
    internal readonly record struct Input(
        int Turn,
        bool Terminal,
        int ProjectedPlayerHp,
        int Energy,
        int PlayerHp,
        int PlayerMaxHp,
        int EnemyHp,
        int HandCount,
        int ZeroCostPlayableCount,
        int ReachableHandValue,
        int PersistentBuffValue,
        int LatentSetupValue,
        int RetainedAttackValue,
        int ReplayPotentialValue,
        int FutureResourceValue,
        int DelayedDamageValue,
        int LiveDeckClutter,
        int LiveDeckSize,
        int EnemyStrengthSuppression,
        int EnemyWeakTurns,
        int EnemyVulnerableTurns)
    {
        internal static Input From(SearchNode node)
        {
            SimulationSnapshot snapshot = node.Snapshot;
            return new Input(
                node.Turn,
                node.IsTerminal,
                snapshot.ProjectedPlayerHp,
                snapshot.Energy,
                snapshot.PlayerHp,
                snapshot.PlayerMaxHp,
                snapshot.EnemyHp,
                snapshot.HandCount,
                snapshot.ZeroCostPlayableCount,
                snapshot.ReachableHandValue,
                snapshot.PersistentBuffValue,
                snapshot.LatentSetupValue,
                snapshot.RetainedAttackValue,
                snapshot.ReplayPotentialValue,
                snapshot.FutureResourceValue,
                snapshot.DelayedDamageValue,
                snapshot.LiveDeckClutter,
                snapshot.LiveDeckSize,
                snapshot.EnemyStrengthSuppression,
                snapshot.EnemyWeakTurns,
                snapshot.EnemyVulnerableTurns);
        }
    }

    private readonly Document _document;
    public string ModelId => _document.ModelId;

    private ContextualRankingModel(Document document) => _document = document;

    internal static ContextualRankingModel Parse(string json)
    {
        Document document = JsonSerializer.Deserialize<Document>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty contextual ranking model.");
        if (document.SchemaVersion != 1 || document.FeatureNames == null
            || !document.FeatureNames.SequenceEqual(FeatureNames)
            || document.Weights?.Length != FeatureCount || document.Minimum?.Length != FeatureCount
            || document.Maximum?.Length != FeatureCount || string.IsNullOrWhiteSpace(document.ModelId)
            || !double.IsFinite(document.MaximumAdjustmentHp) || document.MaximumAdjustmentHp is <= 0 or > 8)
            throw new InvalidDataException("Invalid contextual ranking model schema.");
        if (document.SolverAssemblyId != typeof(ContextualRankingModel).Assembly.ManifestModule.ModuleVersionId.ToString()
            || document.GameAssemblyId != typeof(CardModel).Assembly.ManifestModule.ModuleVersionId.ToString())
            throw new InvalidDataException("Contextual ranking model assembly mismatch.");
        for (int i = 0; i < FeatureCount; i++)
            if (!double.IsFinite(document.Weights[i]) || Math.Abs(document.Weights[i]) > 100
                || !double.IsFinite(document.Minimum[i]) || !double.IsFinite(document.Maximum[i])
                || document.Minimum[i] > document.Maximum[i])
                throw new InvalidDataException("Invalid contextual ranking coefficient or range.");
        return new ContextualRankingModel(document);
    }

    internal double Adjustment(SearchNode node) => Adjustment(Input.From(node));

    internal double Adjustment(in Input input)
    {
        if (input.Terminal || input.ProjectedPlayerHp <= 0)
            return 0;
        Span<double> features = stackalloc double[FeatureCount];
        Capture(input, features);
        double value = 0;
        for (int i = 0; i < features.Length; i++)
        {
            double feature = features[i];
            if (feature < _document.Minimum[i] || feature > _document.Maximum[i])
                return 0; // Unsupported context keeps the original heuristic.
            value += feature * _document.Weights[i];
        }
        return Math.Clamp(value, -_document.MaximumAdjustmentHp, _document.MaximumAdjustmentHp) * SolverWeights.Hp;
    }

    internal static void Capture(SearchNode node, Span<double> values) => Capture(Input.From(node), values);

    internal static void Capture(in Input input, Span<double> values)
    {
        if (values.Length != FeatureCount)
            throw new ArgumentException("Wrong contextual ranking feature buffer size.", nameof(values));
        static double Cap(double value, double scale) => Math.Clamp(value / scale, 0, 4);
        double energy = Cap(input.Energy, 6);
        double pressure = 1 - Math.Clamp((double)input.PlayerHp / Math.Max(1, input.PlayerMaxHp), 0, 1);
        double incoming = Math.Clamp(
            (double)(input.PlayerHp - input.ProjectedPlayerHp) / Math.Max(1, input.PlayerHp), 0, 1);
        double enemyHp = Cap(input.EnemyHp, 200);
        double hand = Cap(input.HandCount, 10);
        double free = Cap(input.ZeroCostPlayableCount, 10);
        double reachable = Cap(input.ReachableHandValue, 50);
        double persistent = Cap(input.PersistentBuffValue, 128);
        double setup = Cap(input.LatentSetupValue, 64);
        double future = Cap(input.FutureResourceValue, 64);
        double delayed = Cap(input.DelayedDamageValue, 100);
        values[0] = energy;
        values[1] = pressure;
        values[2] = incoming;
        values[3] = enemyHp;
        values[4] = hand;
        values[5] = free;
        values[6] = reachable;
        values[7] = persistent;
        values[8] = setup;
        values[9] = Cap(input.RetainedAttackValue, 128);
        values[10] = Cap(input.ReplayPotentialValue, 64);
        values[11] = future;
        values[12] = delayed;
        values[13] = Math.Clamp((double)input.LiveDeckClutter / Math.Max(1, input.LiveDeckSize), 0, 1);
        values[14] = Cap(input.EnemyStrengthSuppression, 16);
        values[15] = Cap(input.EnemyWeakTurns, 16);
        values[16] = Cap(input.EnemyVulnerableTurns, 16);
        values[17] = Cap(input.LiveDeckSize, 30);
        values[18] = Cap(input.Turn, 10);
        values[19] = energy * pressure;
        values[20] = energy * incoming;
        values[21] = hand * energy;
        values[22] = persistent * pressure;
        values[23] = setup * pressure;
        values[24] = future * pressure;
        values[25] = delayed * enemyHp;
        values[26] = reachable * incoming;
        values[27] = free * incoming;
    }
}
