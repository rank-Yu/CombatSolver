using System.Text;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Flash.Kernel;
using CombatSolver.Flash.Search;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.Flash.Bridge;

/// <summary>
/// Offline route trace (see <see cref="FlashBeamTrace"/>). Before the search starts, the target
/// existing-engine <see cref="PlanAction"/> route is replayed once against the Flash root to map every
/// step to the exact Flash action (choices included) and to the parent route node. The search then
/// reports that node's generation, dedup, rank/lane retention, bound pruning and end-turn compaction.
/// This class is constructed only by the offline harness; production never sets
/// <see cref="FlashBeamOptions.Trace"/>.
/// </summary>
internal sealed class FlashRouteTrace : FlashBeamTrace
{
    private const int MaxChoiceBranches = 32;

    private readonly FlashAction[] _actions;
    private readonly int[] _parentNodes;
    private readonly int[] _nodes;
    private readonly bool[] _created;
    private readonly bool[] _finished;
    private readonly bool[] _inserted;
    private readonly bool[] _mismatchLogged;
    private bool _rootLogged;
    private readonly PlanAction[] _plan;
    private readonly List<string> _lines = [];
    private readonly int _firstTurn;
    private int _next;

    private FlashRouteTrace(FlashAction[] actions, PlanAction[] plan, int firstTurn)
    {
        _actions = actions;
        _plan = plan;
        _firstTurn = firstTurn;
        int count = actions.Length;
        _parentNodes = new int[count];
        _nodes = new int[count];
        _created = new bool[count];
        _finished = new bool[count];
        _inserted = new bool[count];
        _mismatchLogged = new bool[count];
        Array.Fill(_nodes, -1);
        for (int index = 0; index < count; index++)
            _parentNodes[index] = index == 0 ? 0 : -1;
    }

    internal IReadOnlyList<string> Lines => _lines;

    /// <summary>
    /// Maps <paramref name="targetPlan"/> to Flash actions. <paramref name="prefixLength"/> actions
    /// belong to the request's fixed prefix and are not searched; the trace starts after them.
    /// Returns null with a reason when the route cannot be replayed on the Flash root.
    /// </summary>
    internal static FlashRouteTrace? TryCreate(
        OpeningProbe probe,
        Player player,
        SimulationSnapshot start,
        FlashBattle startBattle,
        FlashRules rules,
        IReadOnlyList<PlanAction> targetPlan,
        int prefixLength,
        out string error)
    {
        int searchStart = Math.Clamp(prefixLength, 0, targetPlan.Count);
        List<FlashAction> actions = new(targetPlan.Count - searchStart);
        List<PlanAction> plan = new(targetPlan.Count - searchStart);
        FlashBattle battle = startBattle;
        SimulationSnapshot snapshot = start;
        bool ownsSnapshot = false;
        try
        {
            for (int step = searchStart; step < targetPlan.Count; step++)
            {
                PlanAction target = targetPlan[step];
                if (!TryFindAction(probe, player, snapshot, battle, rules, target,
                        out FlashAction action, out FlashBattle child, out SimulationSnapshot? childSnapshot))
                {
                    error = $"第 {step} 步 {Describe(target)} 无法在 Flash 根上映射为动作。";
                    FlashRouteTrace partial = new([.. actions], [.. plan], startBattle.PlayerTurn);
                    partial._lines.Add($"MAPPING-STOP step={step} reason={error}");
                    return partial;
                }
                actions.Add(action);
                plan.Add(target);
                battle = child;
                if (ownsSnapshot)
                    snapshot.ReleaseSimulator();
                snapshot = childSnapshot!;
                ownsSnapshot = true;
            }
            error = "";
            return new FlashRouteTrace([.. actions], [.. plan], startBattle.PlayerTurn);
        }
        finally
        {
            if (ownsSnapshot)
                snapshot.ReleaseSimulator();
        }
    }

    internal override int Match(int parentNode, in FlashAction action)
    {
        if (_next >= _actions.Length)
            return 0;
        if (_next == 0 && parentNode == 0 && !_rootLogged)
        {
            _rootLogged = true;
            _lines.Add("ROOT-TARGET " + _actions[0]);
        }
        int expectedParent = _next == 0 ? 0 : _nodes[_next - 1];
        if (expectedParent < 0 || parentNode != expectedParent)
            return 0;
        if (!SameAction(action, _actions[_next]))
        {
            if (!_mismatchLogged[_next])
            {
                _mismatchLogged[_next] = true;
                _lines.Add($"STEP {_next + 1} root-action mismatch: search={action} " +
                    $"target={_actions[_next]}");
            }
            return 0;
        }
        _next++;
        return _next;
    }

    internal override bool IsWatched(int node)
    {
        for (int index = 0; index < _nodes.Length; index++)
        {
            if (_nodes[index] == node)
                return true;
        }
        return false;
    }

    internal override void NodeCreated(int node, int step)
    {
        int index = step - 1;
        if ((uint)index >= (uint)_nodes.Length || _created[index])
            return;
        _nodes[index] = node;
        _created[index] = true;
        PlanAction plan = _plan[index];
        _lines.Add($"STEP {step} gen turn={plan.Turn} kind={plan.Kind} card={plan.CardId}" +
            $"#{plan.CardOccurrence} target={plan.TargetCombatId?.ToString() ?? "-"}" +
            $" node={node} parent={(index == 0 ? 0 : _nodes[index - 1])}");
        if (index + 1 < _parentNodes.Length)
            _parentNodes[index + 1] = node;
    }

    internal override void NodeInserted(int node, bool added, int existingNode, bool incomingWon,
        double rank, double existingRank, double score, double existingScore,
        int existingPotionCount, int existingTurn, int existingDepth, int existingEnemyHp,
        int existingProjectedHp)
    {
        int step = StepOf(node);
        if (step == 0)
        {
            // An untracked state replaced a tracked representative's seat; the route may already
            // have children, so only report the seat change (the first missing step is the cut).
            int replaced = StepOf(existingNode);
            if (replaced > 0 && !incomingWon)
                _lines.Add($"STEP {replaced} seat replaced by untracked node={node}");
            return;
        }
        bool firstInsert = !_inserted[step - 1];
        if (firstInsert)
            _inserted[step - 1] = true;
        if (added)
        {
            _lines.Add($"STEP {step} pool insert unique node={node}");
            return;
        }
        if (incomingWon)
        {
            int replaced = StepOf(existingNode);
            _lines.Add($"STEP {step} pool dedup replaced node={existingNode} with node={node}" +
                $" rank={rank:0.###} oldRank={existingRank:0.###}" +
                $" score={score:0.###} oldScore={existingScore:0.###}" +
                (replaced > 0 ? $" replacedStep={replaced}" : ""));
            return;
        }
        if (firstInsert)
        {
            Drop(step, $"dedup-first-admission representative={existingNode} rank={rank:0.###}" +
                $" score={score:0.###} repScore={existingScore:0.###}" +
                $" repRank={existingRank:0.###} repPotions={existingPotionCount}" +
                $" repTurn={existingTurn} repDepth={existingDepth} repEnemyHp={existingEnemyHp}" +
                $" repProjectedHp={existingProjectedHp}");
            return;
        }
        _lines.Add($"STEP {step} seat kept representative={existingNode} rank={rank:0.###}" +
            $" repRank={existingRank:0.###}");
    }

    internal override void NodeRetained(int node, string phase, bool selected, bool required,
        int rankPosition, int count, int width, string lanes)
    {
        int step = StepOf(node);
        if (step == 0)
            return;
        _lines.Add($"STEP {step} {phase} selected={selected} required={required}" +
            $" rankPos={rankPosition} pool={count} width={width}" +
            (selected ? "" : $" lanes=[{lanes}]"));
    }

    internal override void StandPatProbe(int node, bool selected, bool available, bool allEnemiesDead,
        int delayedDamage, int projectedHp, int resourceValue, int laneValue, double score, bool chosen)
    {
        int step = StepOf(node);
        if (step == 0)
            return;
        _lines.Add($"STEP {step} standpat probe selected={selected} available={available}" +
            $" dead={allEnemiesDead} delayed={delayedDamage} hp={projectedHp}" +
            $" resource={resourceValue} lane={laneValue} score={score:0.###} chosen={chosen}");
    }

    internal override void NodePruned(int node, string phase, string reason)
    {
        int step = StepOf(node);
        if (step == 0)
            return;
        Drop(step, $"{phase}:{reason}");
    }

    internal override void Finish(string reason, int turns, int expanded)
    {
        _lines.Add($"FINISH reason={reason} reached={_next}/{_actions.Length} turns={turns} expanded={expanded}");
        if (_next < _actions.Length)
        {
            PlanAction missing = _plan[_next];
            _lines.Add($"FIRST-MISSING step={_next + 1} turn={missing.Turn} kind={missing.Kind}" +
                $" card={missing.CardId}#{missing.CardOccurrence}");
        }
    }

    private void Drop(int step, string reason)
    {
        if (step <= 0 || step > _finished.Length || _finished[step - 1])
            return;
        _finished[step - 1] = true;
        _lines.Add($"STEP {step} cut {reason}");
    }

    private int StepOf(int node)
    {
        if (node < 0)
            return 0;
        for (int index = 0; index < _nodes.Length; index++)
        {
            if (_nodes[index] == node)
                return index + 1;
        }
        return 0;
    }

    private static bool TryFindAction(
        OpeningProbe probe,
        Player player,
        SimulationSnapshot parent,
        FlashBattle battle,
        FlashRules rules,
        PlanAction target,
        out FlashAction matched,
        out FlashBattle child,
        out SimulationSnapshot? childSnapshot)
    {
        Span<FlashAction> actions = stackalloc FlashAction[64];
        int count = FlashKernel.LegalActions(battle, rules, actions);
        for (int index = 0; index < count; index++)
        {
            FlashAction candidate = actions[index];
            PlanAction translated = FlashActionTranslator.ToPlanAction(parent, player, candidate);
            if (!SameNonChoice(translated, target))
                continue;
            if (TryResolve(probe, player, parent, battle, rules, candidate, target, depth: 0,
                    out FlashAction resolved, out child, out childSnapshot))
            {
                matched = resolved;
                return true;
            }
        }
        matched = default;
        child = default;
        childSnapshot = null;
        return false;
    }

    private static bool TryResolve(
        OpeningProbe probe,
        Player player,
        SimulationSnapshot parent,
        FlashBattle battle,
        FlashRules rules,
        FlashAction action,
        PlanAction target,
        int depth,
        out FlashAction resolved,
        out FlashBattle child,
        out SimulationSnapshot? childSnapshot)
    {
        resolved = action;
        child = battle;
        childSnapshot = null;
        if (depth > FlashLimits.MaxChoices)
            return false;
        FlashStepResult result = FlashKernel.Step(ref child, action, rules, out FlashChoiceRequest request);
        if (result == FlashStepResult.NeedsChoice)
        {
            Span<ulong> selections = stackalloc ulong[MaxChoiceBranches];
            int count = FlashChoiceEnumerator.Enumerate(request, selections);
            for (int index = 0; index < count; index++)
            {
                if (TryResolve(probe, player, parent, battle, rules, action.WithChoice(selections[index]),
                        target, depth + 1, out resolved, out child, out childSnapshot))
                {
                    return true;
                }
            }
            return false;
        }
        SimulationSnapshot planned = FlashChoiceTranslator.Replay(
            probe.ReplayCoreForBridge, parent, player, action, out PlanAction translated);
        if (!SameAction(translated, target))
        {
            planned.ReleaseSimulator();
            return false;
        }
        childSnapshot = planned;
        return true;
    }

    private static bool SameAction(in FlashAction left, in FlashAction right)
    {
        if (left.Kind != right.Kind || left.HandIndex != right.HandIndex
            || left.Target != right.Target || left.PotionSlot != right.PotionSlot
            || left.ChoiceCount != right.ChoiceCount)
            return false;
        for (int index = 0; index < left.ChoiceCount; index++)
        {
            if (left.Choices[index] != right.Choices[index])
                return false;
        }
        return true;
    }

    private static bool SameNonChoice(PlanAction left, PlanAction right)
        => left.Kind == right.Kind && left.Turn == right.Turn
            && string.Equals(left.CardId, right.CardId, StringComparison.Ordinal)
            && left.CardOccurrence == right.CardOccurrence
            && left.TargetIndex == right.TargetIndex
            && left.TargetCombatId == right.TargetCombatId
            && left.PotionSlot == right.PotionSlot
            && string.Equals(left.PotionId, right.PotionId, StringComparison.Ordinal);

    private static bool SameAction(PlanAction left, PlanAction right)
    {
        if (!SameNonChoice(left, right)
            || !string.Equals(left.CardStateKey, right.CardStateKey, StringComparison.Ordinal)
            || left.CardStateOccurrence != right.CardStateOccurrence
            || left.ReplayCount != right.ReplayCount
            || left.EndsPlayerTurn != right.EndsPlayerTurn)
            return false;
        return SameChoice(left.Choice, right.Choice)
            && SameChoices(left.NestedChoices, right.NestedChoices);
    }

    private static bool SameChoices(IReadOnlyList<PlanCardChoice>? left, IReadOnlyList<PlanCardChoice>? right)
    {
        if (left == null || right == null)
            return left == null && right == null;
        if (left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (!SameChoice(left[index], right[index]))
                return false;
        }
        return true;
    }

    private static bool SameChoice(PlanCardChoice? left, PlanCardChoice? right)
    {
        if (left == null || right == null)
            return left == null && right == null;
        if (left.Effect != right.Effect || left.SourcePile != right.SourcePile
            || !string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal)
            || left.Timing != right.Timing || left.Cards.Count != right.Cards.Count)
            return false;
        for (int index = 0; index < left.Cards.Count; index++)
        {
            PlanCardToken a = left.Cards[index];
            PlanCardToken b = right.Cards[index];
            if (!string.Equals(a.CardId, b.CardId, StringComparison.Ordinal)
                || a.UpgradeLevel != b.UpgradeLevel
                || !string.Equals(a.StateKey, b.StateKey, StringComparison.Ordinal)
                || a.SourceOccurrence != b.SourceOccurrence
                || a.OptionOccurrence != b.OptionOccurrence)
                return false;
        }
        return true;
    }

    private static string Describe(PlanAction action)
        => $"{action.Kind}:{action.CardId}#{action.CardOccurrence}" +
            (action.PotionId.Length == 0 ? "" : $" potion={action.PotionId}@{action.PotionSlot}") +
            $" target={action.TargetCombatId?.ToString() ?? "-"}";
}
