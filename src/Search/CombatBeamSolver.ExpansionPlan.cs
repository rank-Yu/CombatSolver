using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

internal sealed partial class CombatBeamSolver
{
    private bool TryAdmitExpansionParent(SearchNode node, SimulationSnapshot snapshot, bool parallel)
    {
        if (node.IsTerminal
            || snapshot.PlayerDead
            || snapshot.AllEnemiesDead
            || snapshot.BoundaryReason != SearchBoundaryReason.None)
        {
            throw new InvalidOperationException(parallel
                ? "终结搜索节点不应进入并行展开阶段。"
                : "终结搜索节点不应进入展开阶段。");
        }
        _run.ReusedNodeSnapshots++;
        if (!TryMarkExpandedState(node))
            return false;
        if (!TryConsumeCycleExitProbeExpansionBudget(node))
        {
            _run.CycleContinuationsStopped++;
            _run.CycleStoppedExitBudget++;
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionBlocked, "cycle_exit_budget");
            return false;
        }
        _run.Expanded++;
        ObserveSearchPath(node, SearchPathObservationStage.Expanded,
            parallel ? "parallel_parent" : "serial_parent");
        return true;
    }

    private IEnumerable<PreparedCardAction> EnumeratePlannedCardActions(ExpansionPlan plan)
        => SearchExpansionShared.EnumeratePlannedCardActions(ExpansionContext, plan);



    private IEnumerable<PreparedPotionAction> EnumeratePlannedPotionActions(ExpansionPlan plan)
        => SearchExpansionShared.EnumeratePlannedPotionActions(ExpansionContext, plan);



    private SearchNode CreatePlannedCardChild(
        SearchNode node,
        PlanAction finalAction,
        SimulationSnapshot finalSnapshot)
        => SearchExpansionShared.CreatePlannedCardChild(
            ExpansionContext, node, finalAction, finalSnapshot);



    private bool TryResolvePlannedCardChoices(
        SearchNode node,
        PreparedCardAction action,
        SimulationSnapshot probeSnapshot,
        out IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> resolvedBranches)
    {
        CardChoiceSpec? choiceSpec = BuildPrimaryCardChoiceSpec(_player, probeSnapshot);
        if (choiceSpec == null && action.RequiresUnsupportedExistingChoice)
        {
            probeSnapshot.ReleaseSimulator();
            resolvedBranches = [];
            return false;
        }
        PlanCardChoice? requiredEmptyChoice = action.RequiredEmptyChoice;
        CardChoiceSpec? primaryChoiceSpec = choiceSpec
            ?? BuildRequiredEmptyChoiceSpec(requiredEmptyChoice);
        resolvedBranches = HasChoiceBeforePrimary(probeSnapshot, primaryChoiceSpec)
            ? ResolveRoundChoiceBranches(
                node,
                action.Action,
                probeSnapshot,
                BuildPrimaryChoiceMatch(primaryChoiceSpec),
                budgetPrimaryChoiceSpec: primaryChoiceSpec)
            : ResolvePrimaryCardChoiceBranches(
                node,
                action.Action,
                probeSnapshot,
                choiceSpec,
                requiredEmptyChoice);
        return true;
    }

    private SearchNode CreatePlannedPotionChild(
        SearchNode node,
        PlanAction finalAction,
        SimulationSnapshot finalSnapshot)
        => SearchExpansionShared.CreatePlannedPotionChild(
            ExpansionContext, node, finalAction, finalSnapshot);



    private bool TryAdmitPlannedPotionChild(SearchNode child, out bool rejectedByCycle)
    {
        EnsureBoundedCycleProbeLease(child);
        rejectedByCycle = ShouldRejectCycleCandidate(child);
        return !rejectedByCycle && TryAcceptTransposition(child);
    }

    private IEnumerable<SearchNode> AdmitPlannedEndTurnChildren(ExpansionBatch batch)
    {
        foreach (SearchNode child in batch.EndTurns)
        {
            if (!TryAcceptTransposition(child))
            {
                batch.Release(child.Snapshot);
                continue;
            }
            batch.Transfer(child.Snapshot);
            yield return child;
        }
    }

    private void ProcessExpandedCardCandidate(
        SearchNode parent,
        RawCardCandidate raw,
        List<ActionCandidate> nonDominated,
        ref List<ActionCandidate>? deferredCycleCandidates,
        ExpansionBatch? batch)
    {
        SearchNode child = raw.Node;
        PromoteOrderedMutationProgressTail(child);
        CommitCycleExitObservation(child);
        if (ShouldPruneCrossTurnNoProgress(child))
        {
            _run.RepeatableNoProgressBranchesPruned++;
            ReleasePlannedCandidate(child, batch);
            return;
        }
        bool retainedMutation = CanRetainOrderedMutationLease(_run, child);
        bool deferredCycle = !retainedMutation
            && ShouldDeferCycleTranspositionUntilActionAdmission(child);
        // Tactical classification scans history and clones the node, but neither result
        // participates in exact-state admission. Reject ordinary duplicates before paying
        // that cost. Mutation leases and deferred cycle admission keep their original paths.
        if (!retainedMutation && !deferredCycle && !TryAcceptTransposition(child))
        {
            ReleasePlannedCandidate(child, batch);
            return;
        }
        ActionCandidate candidate = BuildCandidate(
            parent.Snapshot, child.Snapshot, child, raw.CardType, raw.TargetCombatId);
        if (retainedMutation)
        {
            nonDominated.Add(candidate);
            return;
        }
        if (deferredCycle)
        {
            deferredCycleCandidates ??= [];
            deferredCycleCandidates.Add(candidate);
            return;
        }
        AddNonDominatedCandidate(nonDominated, candidate, batch);
    }

    private static void ReleasePlannedCandidate(SearchNode child, ExpansionBatch? batch)
    {
        if (batch == null)
            child.Snapshot.ReleaseSimulator();
        else
            batch.Release(child.Snapshot);
    }
}
