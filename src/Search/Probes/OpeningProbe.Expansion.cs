using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

internal sealed partial class OpeningProbe
{
    private IEnumerable<SearchNode> Expand(SearchNode node)
        => ExpandProbe(node);

    private IEnumerable<SearchNode> ExpandProbe(SearchNode node)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SimulationSnapshot snapshot = node.Snapshot;
        if (node.IsTerminal
            || snapshot.PlayerDead
            || snapshot.AllEnemiesDead
            || snapshot.BoundaryReason != SearchBoundaryReason.None)
        {
            yield break;
        }
        _run.Expanded++;
        List<ActionCandidate> cardCandidates = [];
        foreach (PreparedCardAction prepared in EnumeratePlannedCardActions(new ExpansionPlan(node, CardNameFirst: false)))
        {
            foreach ((PlanAction resolvedAction, SimulationSnapshot resolvedSnapshot) in
                     ResolveCardBranches(node, prepared))
            {
                SearchNode child = CreatePlannedCardChild(node, resolvedAction, resolvedSnapshot);
                ActionCandidate candidate = SearchProbeHelpers.BuildCandidate(
                    _player, node.Snapshot, child.Snapshot, child, prepared.CardType, prepared.TargetCombatId);
                cardCandidates.Add(candidate);
            }
        }
        foreach (ActionCandidate candidate in SearchProbeHelpers.SelectActionCandidates(node, cardCandidates, CandidateSelection))
            yield return candidate.Node;

        foreach (PreparedPotionAction prepared in EnumeratePlannedPotionActions(new ExpansionPlan(node, CardNameFirst: false)))
        {
            foreach ((PlanAction resolvedAction, SimulationSnapshot resolvedSnapshot) in
                     ResolvePotionBranches(node, prepared))
            {
                yield return CreatePlannedPotionChild(node, resolvedAction, resolvedSnapshot);
            }
        }

        foreach ((PlanAction endAction, SimulationSnapshot endSnapshot) in BuildEndTurnBranches(node, []))
        {
            bool combatEnded = endSnapshot.PlayerDead || endSnapshot.AllEnemiesDead;
            bool endTerminal = combatEnded || endSnapshot.BoundaryReason != SearchBoundaryReason.None;
            yield return new SearchNode(
                endAction,
                node.ActionCount + 1,
                endSnapshot.PotionUseCount,
                endSnapshot.PotionStrategicCost,
                endSnapshot.Turn,
                ClassifyRoundTransitionTraits(node.Traits, node.Snapshot, endSnapshot),
                node.FutureSoldHp,
                ApplySoldHpPenalty(endSnapshot.Score, node.FutureSoldHp),
                endSnapshot.StateKey,
                endSnapshot.HasRisk,
                endSnapshot.BoundaryReason,
                endTerminal,
                node,
                endSnapshot,
                node.CombatProgress.Advance(endSnapshot))
            {
                CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, endSnapshot),
            };
        }
    }

    private SearchNode CreatePlannedCardChild(
        SearchNode node,
        PlanAction finalAction,
        SimulationSnapshot finalSnapshot)
        => SearchExpansionShared.CreatePlannedCardChild(
            ExpansionContext, node, finalAction, finalSnapshot);



    private SearchNode CreatePlannedPotionChild(
        SearchNode node,
        PlanAction finalAction,
        SimulationSnapshot finalSnapshot)
        => SearchExpansionShared.CreatePlannedPotionChild(
            ExpansionContext, node, finalAction, finalSnapshot);



    private IEnumerable<PreparedCardAction> EnumeratePlannedCardActions(ExpansionPlan plan)
        => SearchExpansionShared.EnumeratePlannedCardActions(ExpansionContext, plan);



    private IEnumerable<PreparedPotionAction> EnumeratePlannedPotionActions(ExpansionPlan plan)
        => SearchExpansionShared.EnumeratePlannedPotionActions(ExpansionContext, plan);



    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolveCardBranches(
        SearchNode node,
        PreparedCardAction prepared)
    {
        PlanAction action = prepared.Action;
        SimulationSnapshot probe = ReplayAction(node, action);
        CardChoiceSpec? choiceSpec = BuildPrimaryCardChoiceSpec(_player, probe);
        if (choiceSpec == null && prepared.RequiresUnsupportedExistingChoice)
        {
            probe.ReleaseSimulator();
            yield break;
        }
        CardChoiceSpec? primaryChoiceSpec = choiceSpec
            ?? BuildRequiredEmptyChoiceSpec(prepared.RequiredEmptyChoice);
        foreach ((PlanAction resolved, SimulationSnapshot resolvedSnapshot) in
                 ResolvePendingBranches(node, action, probe, primaryChoiceSpec))
        {
            yield return (resolved, resolvedSnapshot);
        }
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolvePotionBranches(
        SearchNode node,
        PreparedPotionAction prepared)
    {
        PlanAction action = prepared.Action;
        if (!PotionChoiceSupport.RequiresChoice(prepared.Potion))
        {
            yield return (action, ReplayAction(node, action));
            yield break;
        }

        SimulationSnapshot? probe = null;
        CardChoiceSpec spec;
        if (PotionChoiceSupport.GeneratesCardChoice(prepared.Potion))
        {
            probe = ReplayAction(node, action);
            spec = PotionChoiceSupport.GetSpec(
                (CombatPredictionSimulator)probe.Simulator,
                prepared.Potion);
        }
        else
        {
            spec = PotionChoiceSupport.GetSpec(
                (CombatPredictionSimulator)node.Snapshot.Simulator,
                prepared.Potion);
        }
        IReadOnlyList<PlanCardChoice> choices = CardChoiceSupport.BuildChoices(
                spec,
                displayNames,
                _profile.MaxPileChoiceBranchesPerAction,
                _profile.MaxHandChoiceBranchesPerAction)
            .Select(choice => choice with { SourceId = prepared.Potion.Id.Entry })
            .ToArray();
        probe?.ReleaseSimulator();
        if (choices.Count == 0)
            throw new InvalidOperationException($"药水 {prepared.Potion.Id.Entry} 的选择没有生成任何分支。");
        foreach (PlanCardChoice choice in choices)
        {
            PlanAction resolved = action with { Choice = choice };
            SimulationSnapshot snapshot = ReplayAction(node, resolved);
            foreach ((PlanAction nested, SimulationSnapshot nestedSnapshot) in
                     ResolvePendingBranches(node, resolved, snapshot, primaryChoiceSpec: null, depth: 1))
            {
                yield return (nested, nestedSnapshot);
            }
        }
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolvePendingBranches(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot snapshot,
        CardChoiceSpec? primaryChoiceSpec,
        int depth = 0)
    {
        if (depth > 64)
        {
            snapshot.ReleaseSimulator();
            throw new InvalidOperationException("探针选择递归超过上限。");
        }
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
        {
            yield return (action, snapshot);
            yield break;
        }
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (primaryChoiceSpec != null)
        {
            IReadOnlyList<PlanCardChoice> choices = CardChoiceSupport.BuildChoices(
                primaryChoiceSpec,
                displayNames,
                _profile.MaxPileChoiceBranchesPerAction,
                _profile.MaxHandChoiceBranchesPerAction);
            if (choices.Count == 0)
                choices = [null!];
            snapshot.ReleaseSimulator();
            foreach (PlanCardChoice? choice in choices)
            {
                PlanAction resolved = action with { Choice = choice };
                SimulationSnapshot child = ReplayAction(node, resolved);
                foreach ((PlanAction nested, SimulationSnapshot nestedSnapshot) in
                         ResolvePendingBranches(node, resolved, child, primaryChoiceSpec: null, depth + 1))
                {
                    yield return (nested, nestedSnapshot);
                }
            }
            yield break;
        }
        if (combat.PendingKnowledgeDemonChoice is { } knowledgeRequest)
        {
            IReadOnlyList<PlanCardChoice> branches = KnowledgeDemonChoiceSupport.BuildChoices(
                knowledgeRequest,
                displayNames);
            snapshot.ReleaseSimulator();
            foreach (PlanCardChoice branch in branches)
            {
                List<PlanCardChoice> next = new(action.TurnStartChoices ?? []);
                next.Add(branch);
                PlanAction resolved = action with { TurnStartChoices = next };
                SimulationSnapshot child = ReplayAction(node, resolved);
                foreach ((PlanAction nested, SimulationSnapshot nestedSnapshot) in
                         ResolvePendingBranches(node, resolved, child, primaryChoiceSpec: null, depth + 1))
                {
                    yield return (nested, nestedSnapshot);
                }
            }
            yield break;
        }
        if (combat.PendingTurnStartChoice is { } request)
        {
            CardChoiceSpec spec = TurnStartChoiceSupport.BuildPendingSpec(
                (CombatPredictionSimulator)snapshot.Simulator,
                combat,
                _player);
            IReadOnlyList<PlanCardChoice> branches = CardChoiceSupport.BuildChoices(
                spec,
                displayNames,
                _profile.MaxPileChoiceBranchesPerAction,
                _profile.MaxHandChoiceBranchesPerAction);
            if (branches.Count == 0)
                branches = [null!];
            bool turnResolution = action.Kind == PlanActionKind.EndTurn
                || snapshot.Turn > node.Turn
                || request.Timing is PlanChoiceTiming.PlayerTurnEnd
                    or PlanChoiceTiming.EnemyTurn or PlanChoiceTiming.PlayerTurnStart;
            snapshot.ReleaseSimulator();
            foreach (PlanCardChoice? branch in branches)
            {
                PlanAction resolved = AppendPendingChoice(action, request, branch!, turnResolution, null);
                SimulationSnapshot child = ReplayAction(node, resolved);
                foreach ((PlanAction nested, SimulationSnapshot nestedSnapshot) in
                         ResolvePendingBranches(node, resolved, child, primaryChoiceSpec: null, depth + 1))
                {
                    yield return (nested, nestedSnapshot);
                }
            }
            yield break;
        }
        snapshot.ReleaseSimulator();
        throw new InvalidOperationException(
            $"动作 {PolicyActionToken(action)} 产生了未登记的分支选择。");
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> BuildEndTurnBranches(
        SearchNode node,
        IReadOnlyList<PlanCardChoice> choices)
    {
        PlanAction action = new(
            PlanActionKind.EndTurn,
            node.Turn,
            TurnStartChoices: choices.Count == 0 ? null : choices);
        SimulationSnapshot snapshot = ReplayAction(node, action);
        foreach ((PlanAction resolvedAction, SimulationSnapshot resolvedSnapshot) in
                 ResolvePendingBranches(node, action, snapshot, primaryChoiceSpec: null))
        {
            yield return (resolvedAction, resolvedSnapshot);
        }
    }

}
