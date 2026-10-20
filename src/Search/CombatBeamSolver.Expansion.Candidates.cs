using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;


internal sealed partial class CombatBeamSolver
{
    private ActionCandidate BuildCandidate(
        SimulationSnapshot before,
        SimulationSnapshot after,
        SearchNode node,
        CardType cardType,
        uint? targetCombatId)
        => SearchProbeHelpers.BuildCandidate(_player, before, after, node, cardType, targetCombatId);
private List<ActionCandidate> SelectActionCandidates(
        SearchNode parent,
        List<ActionCandidate> candidates)
        => SearchProbeHelpers.SelectActionCandidates(parent, candidates, CandidateSelection);



    private bool Dominates(ActionCandidate left, ActionCandidate right)
    {
        bool leftHasCycleEvidence = left.Node.CycleProbeLease != null || left.Node.Cycle != null;
        bool rightHasCycleEvidence = right.Node.CycleProbeLease != null || right.Node.Cycle != null;
        bool leftHasCycleExitProbe = left.Node.CycleExitProbe != null
            || HasValidPendingCycleExitObservation(left.Node);
        bool rightHasCycleExitProbe = right.Node.CycleExitProbe != null
            || HasValidPendingCycleExitObservation(right.Node);
        if (ReferenceEquals(left.Node, right.Node)
            || !left.IsPure
            || !right.IsPure
            || leftHasCycleEvidence != rightHasCycleEvidence
            || leftHasCycleEvidence
                && BuildCycleProbeFamilyKey(left.Node) != BuildCycleProbeFamilyKey(right.Node)
            || leftHasCycleExitProbe != rightHasCycleExitProbe
            || leftHasCycleExitProbe
                && BuildCycleExitAdmissionFamilyKey(left.Node)
                    != BuildCycleExitAdmissionFamilyKey(right.Node)
            || (leftHasCycleEvidence || leftHasCycleExitProbe)
                && CycleStartupHealthRiskBucket(left.Node)
                    != CycleStartupHealthRiskBucket(right.Node)
            || left.CardType != right.CardType
            || left.TargetCombatId != right.TargetCombatId
            || left.OptionFamilies != right.OptionFamilies
            || left.EnergySpent != right.EnergySpent
            || left.StarsSpent != right.StarsSpent)
        {
            return false;
        }

        bool noWorse = left.Damage >= right.Damage
            && left.Block >= right.Block
            && left.Hp >= right.Hp
            && left.MaxHp >= right.MaxHp
            && left.CumulativeHpLost <= right.CumulativeHpLost
            && left.LongTermResourceValue >= right.LongTermResourceValue
            && left.Node.Snapshot.StrategicHpCredit >= right.Node.Snapshot.StrategicHpCredit
            && (left.Node.Snapshot.RelicCounters.SatisfiedMask & right.Node.Snapshot.RelicCounters.SatisfiedMask)
                == right.Node.Snapshot.RelicCounters.SatisfiedMask
            && left.Node.Snapshot.StrategyGoalCount >= right.Node.Snapshot.StrategyGoalCount
            && left.AngerCopiesGenerated <= right.AngerCopiesGenerated;
        bool strictlyBetter = left.Damage > right.Damage
            || left.Block > right.Block
            || left.Hp > right.Hp
            || left.MaxHp > right.MaxHp
            || left.CumulativeHpLost < right.CumulativeHpLost
            || left.LongTermResourceValue > right.LongTermResourceValue
            || left.AngerCopiesGenerated < right.AngerCopiesGenerated;
        return noWorse && strictlyBetter;
    }

    private void AddNonDominatedCandidate(
        List<ActionCandidate> candidates,
        ActionCandidate candidate,
        ExpansionBatch? batch = null)
    {
        for (int index = candidates.Count - 1; index >= 0; index--)
        {
            ActionCandidate current = candidates[index];
            if (Dominates(current, candidate))
            {
                _run.DominatedActionsPruned++;
                ReleasePlannedCandidate(candidate.Node, batch);
                return;
            }
            if (!Dominates(candidate, current))
                continue;
            candidates.RemoveAt(index);
            _run.DominatedActionsPruned++;
            ReleasePlannedCandidate(current.Node, batch);
        }
        candidates.Add(candidate);
    }

    private static bool HasValidCycleProbeLease(SearchNode candidate)
    {
        if (candidate.CycleProbeLease is not { } lease
            || lease.Tracker == null
            || lease.Tracker.PeriodActions <= 0
            || lease.NextActionIndex < 0
            || lease.NextActionIndex >= lease.Tracker.PeriodActions
            || lease.CompletedRepetitions < 0)
        {
            return false;
        }
        return true;
    }

    private static bool HasValidCycleExitProbe(
        SearchNode candidate,
        bool requireIssuedTicket)
    {
        if (candidate.CycleExitProbe is not { } probe
            || probe.OriginTracker == null
            || probe.OriginGeneration <= 0
            || probe.OriginPeriodActions <= 0
            || probe.OriginPeriodActions != probe.OriginTracker.PeriodActions
            || probe.OriginShapeKey != probe.OriginTracker.ShapeKey
            || probe.OriginSequenceKey != probe.OriginTracker.SequenceKey
            || probe.OriginPhaseIndex < 0
            || probe.OriginPhaseIndex >= probe.OriginPeriodActions
            || probe.RemainingActions <= 0
            || probe.RemainingActions > MaximumCycleExitProbeActions
            || probe.RemainingEpochActions <= 0
            || probe.RemainingEpochActions > probe.RemainingActions
            || probe.RemainingTurnTransitions < 0
            || probe.RemainingTurnTransitions > MaximumCycleExitProbeTurnTransitions)
        {
            return false;
        }
        if (probe.LeaseIssued)
        {
            // Issued siblings own independent bounded continuations. Another sibling may
            // settle the shared tracker generation without revoking this embedded ticket.
            return true;
        }
        return !requireIssuedTicket
            && probe.OriginTracker.HasPendingExitProbe(
                probe.OriginPhaseIndex,
                probe.ExitActionKey,
                probe.OriginGeneration);
    }

    private static bool HasCycleAdmissionTranspositionLease(SearchNode candidate)
        => HasValidCycleProbeLease(candidate)
            || HasValidCycleExitProbe(candidate, requireIssuedTicket: false)
            || HasValidPendingCycleExitObservation(candidate);

    private static bool HasCycleExpansionTranspositionLease(SearchNode candidate)
        => HasValidCycleProbeLease(candidate)
            || HasValidCycleExitProbe(candidate, requireIssuedTicket: true);

    private static CycleExitProbeFamilyKey BuildCycleExitAdmissionFamilyKey(
        SearchNode candidate)
    {
        if (candidate.CycleExitProbe != null)
            return BuildCycleExitProbeFamilyKey(candidate);
        PendingCycleExitObservation pending = candidate.PendingCycleExitObservation
            ?? throw new InvalidOperationException("循环出口 admission 候选缺少临时证据。");
        return new CycleExitProbeFamilyKey(
            pending.OriginTracker.ShapeKey,
            pending.OriginTracker.SequenceKey,
            pending.OriginTracker.PeriodActions,
            pending.OriginPhaseIndex,
            pending.OriginTracker,
            OriginGeneration: 0,
            ExitActionKey: pending.ExitActionKey);
    }

    private static bool ShouldDeferCycleTranspositionUntilActionAdmission(
        SearchNode candidate)
        => !HasCycleAdmissionTranspositionLease(candidate)
            && RequiresBoundedCyclePlanning(candidate);

    private static bool TryIssueSingleDeferredCycleProbeLease(
        IReadOnlyList<ActionCandidate> admitted,
        IReadOnlyList<ActionCandidate> deferred,
        ActionCandidate preferred)
    {
        foreach (ActionCandidate candidate in admitted)
        {
            if (HasValidCycleProbeLease(candidate.Node))
                return false;
        }
        bool containsPreferred = false;
        foreach (ActionCandidate candidate in deferred)
        {
            containsPreferred |= ReferenceEquals(candidate.Node, preferred.Node);
            if (HasValidCycleProbeLease(candidate.Node))
                return false;
        }
        if (!containsPreferred
            || preferred.Node.CycleProbeLease != null
            || preferred.Node.CycleExitProbe != null
            || !RequiresBoundedCyclePlanning(preferred.Node))
        {
            return false;
        }
        StartCycleProbeLease(preferred.Node);
        return HasValidCycleProbeLease(preferred.Node);
    }

    private void CommitDeferredCycleCandidates(
        List<ActionCandidate> nonDominated,
        IReadOnlyList<ActionCandidate>? deferred,
        ExpansionBatch? batch)
    {
        if (deferred == null || deferred.Count == 0)
            return;
        int bestMaxHp = deferred[0].Node.Snapshot.PlayerMaxHp;
        foreach (ActionCandidate candidate in nonDominated)
            bestMaxHp = Math.Max(bestMaxHp, candidate.Node.Snapshot.PlayerMaxHp);
        foreach (ActionCandidate candidate in deferred)
            bestMaxHp = Math.Max(bestMaxHp, candidate.Node.Snapshot.PlayerMaxHp);

        ActionCandidate? leaseCandidate = nonDominated.Any(candidate =>
                HasValidCycleProbeLease(candidate.Node))
            ? null
            : SelectPreferredCycleAdmissionCandidate(
                deferred.Where(candidate => candidate.Node.CycleProbeLease == null
                    && candidate.Node.CycleExitProbe == null
                    && RequiresBoundedCyclePlanning(candidate.Node)),
                bestMaxHp);
        if (leaseCandidate is { } preferred)
        {
            if (!TryIssueSingleDeferredCycleProbeLease(
                    nonDominated,
                    deferred,
                    preferred))
            {
                throw new InvalidOperationException(
                    "动作 admission 选中的循环候选未取得有效探测租约。");
            }
            _run.CycleCandidatesProtected++;
        }

        // Only the single preferred recurrence owns a lease before the global table. Every
        // sibling first proves it is independently non-dominated in the exact-state frontier.
        ActionCandidate? protectedCandidate = null;
        foreach (ActionCandidate candidate in deferred)
        {
            if (!TryAcceptTransposition(candidate.Node))
            {
                if (batch == null)
                    candidate.Node.Snapshot.ReleaseSimulator();
                else
                    batch.Release(candidate.Node.Snapshot);
                continue;
            }
            if (leaseCandidate is { } leased
                && ReferenceEquals(candidate.Node, leased.Node))
            {
                protectedCandidate = candidate;
                continue;
            }
            AddNonDominatedCandidate(nonDominated, candidate, batch);
        }
        // This is the one explicit cycle lane. It neither removes ordinary candidates nor
        // participates in their pairwise dominance pruning; final action admission decides
        // whether it also wins a normal slot and otherwise appends the issued lease once.
        if (protectedCandidate is { } protectedCycle)
            nonDominated.Add(protectedCycle);
    }

    internal static void VerifyCycleTranspositionLeasePolicyForTesting()
    {
        SimulationSnapshot snapshot = (SimulationSnapshot)System.Runtime.CompilerServices
            .RuntimeHelpers.GetUninitializedObject(typeof(SimulationSnapshot));
        StateFingerprint shapeKey = new(1, 2);
        StateFingerprint sequenceKey = new(3, 4);
        StateFingerprint actionKey = new(5, 6);
        CycleSearchState coarseCycle = new(
            shapeKey,
            sequenceKey,
            PeriodActions: 1,
            Repetitions: 1,
            LastDelta: default,
            HasConsistentDelta: false);
        SearchNode candidate = new(
            Action: null,
            ActionCount: 2,
            PotionCount: 0,
            PotionStrategicCost: 0,
            Turn: 1,
            Traits: SearchRouteTraits.None,
            FutureSoldHp: 0,
            Score: 9,
            StateKey: new StateFingerprint(7, 8),
            HasPredictionRisk: false,
            BoundaryReason: SearchBoundaryReason.None,
            IsTerminal: false,
            Parent: null,
            Snapshot: null!,
            CombatProgress: null!,
            Cycle: coarseCycle);
        TranspositionLabel dominating = new(0, 0, 0, 0, 1, 10);
        TranspositionLabel dominated = new(0, 0, 0, 0, 2, 9);

        if (!ShouldDeferCycleTranspositionUntilActionAdmission(candidate)
            || HasCycleAdmissionTranspositionLease(candidate)
            || new TranspositionFrontier(dominating).TryAccept(dominated))
        {
            throw new InvalidOperationException(
                "没有租约的循环元数据未重新受到转置支配约束。");
        }

        SearchNode testRoot = new(
            Action: null,
            ActionCount: 0,
            PotionCount: 0,
            PotionStrategicCost: 0,
            Turn: 1,
            Traits: SearchRouteTraits.None,
            FutureSoldHp: 0,
            Score: 0,
            StateKey: default,
            HasPredictionRisk: false,
            BoundaryReason: SearchBoundaryReason.None,
            IsTerminal: false,
            Parent: null,
            Snapshot: snapshot,
            CombatProgress: null!);
        SearchNode firstRecurrence = new(
            Action: new PlanAction(PlanActionKind.PlayCard, 1),
            ActionCount: 1,
            PotionCount: 0,
            PotionStrategicCost: 0,
            Turn: 1,
            Traits: SearchRouteTraits.None,
            FutureSoldHp: 0,
            Score: 0,
            StateKey: new StateFingerprint(9, 10),
            HasPredictionRisk: false,
            BoundaryReason: SearchBoundaryReason.None,
            IsTerminal: false,
            Parent: testRoot,
            Snapshot: snapshot,
            CombatProgress: null!,
            Cycle: coarseCycle);
        SearchNode secondRecurrence = firstRecurrence with
        {
            StateKey = new StateFingerprint(11, 12),
        };
        ActionCandidate firstAction = new(
            Node: firstRecurrence,
            CardType: CardType.Attack,
            TargetCombatId: null,
            EnergySpent: 0,
            StarsSpent: 0,
            Damage: 0,
            Block: 0,
            Hp: 0,
            MaxHp: 0,
            CumulativeHpLost: 0,
            LongTermResourceValue: 0,
            AngerCopiesGenerated: 0,
            OptionFamilies: ActionOptionFamily.ResourceAndCycle,
            IsPure: true,
            NormalizedValue: 0);
        ActionCandidate secondAction = firstAction with { Node = secondRecurrence };
        ActionCandidate[] multipleRecurrences = [firstAction, secondAction];
        List<ActionCandidate> ordinarySelected = [secondAction];
        if (!TryIssueSingleDeferredCycleProbeLease(
                Array.Empty<ActionCandidate>(),
                multipleRecurrences,
                firstAction)
            || TryIssueSingleDeferredCycleProbeLease(
                Array.Empty<ActionCandidate>(),
                multipleRecurrences,
                secondAction)
            || !AdmitExistingCycleProbeLease(
                multipleRecurrences,
                ordinarySelected,
                bestMaxHp: 0)
            || ordinarySelected.Count != 2
            || !ReferenceEquals(ordinarySelected[1].Node, firstRecurrence)
            || !HasValidCycleProbeLease(firstRecurrence)
            || HasValidCycleProbeLease(secondRecurrence)
            || !HasCycleAdmissionTranspositionLease(firstRecurrence)
            || HasCycleAdmissionTranspositionLease(secondRecurrence)
            || multipleRecurrences.Count(item => HasValidCycleProbeLease(item.Node)) != 1)
        {
            throw new InvalidOperationException(
                "同一父节点的循环 admission 没有保持并复用唯一探测租约。");
        }

        SearchNode deferredAfterInheritedLease = secondRecurrence with
        {
            StateKey = new StateFingerprint(13, 14),
        };
        ActionCandidate deferredAfterInheritedAction = secondAction with
        {
            Node = deferredAfterInheritedLease,
        };
        if (TryIssueSingleDeferredCycleProbeLease(
                [firstAction],
                [deferredAfterInheritedAction],
                deferredAfterInheritedAction)
            || !HasValidCycleProbeLease(firstRecurrence)
            || HasValidCycleProbeLease(deferredAfterInheritedLease))
        {
            throw new InvalidOperationException(
                "父节点已有继承循环租约时仍给 deferred recurrence 签发了第二租约。");
        }

        CycleProbeTracker tracker = new(
            shapeKey,
            sequenceKey,
            [actionKey],
            default);
        candidate.CycleProbeLease = new CycleProbeLease(
            tracker,
            NextActionIndex: 0,
            CompletedRepetitions: 0,
            ImprovedSinceWrap: false,
            LastCompletedRepetitionImproved: false,
            ObservedExitQualityEpoch: 0);
        if (!HasCycleAdmissionTranspositionLease(candidate)
            || !HasCycleExpansionTranspositionLease(candidate))
        {
            throw new InvalidOperationException("有效循环探测租约没有绕过转置约束。");
        }

        candidate.CycleProbeLease = null;
        if (HasCycleAdmissionTranspositionLease(candidate)
            || new TranspositionFrontier(dominating).TryAccept(dominated))
        {
            throw new InvalidOperationException("被剥离的循环探测租约仍然绕过转置约束。");
        }

        long generation = tracker.ObserveExit(0, actionKey, default, out _);
        candidate.CycleExitProbe = new CycleExitProbeState(
            OriginTracker: tracker,
            OriginNode: candidate,
            OriginPhaseIndex: 0,
            OriginShapeKey: shapeKey,
            OriginSequenceKey: sequenceKey,
            OriginPeriodActions: 1,
            ExitActionKey: actionKey,
            OriginGeneration: generation,
            RemainingActions: MaximumCycleExitProbeActions,
            RemainingEpochActions: BaseCycleExitProbeActions,
            RemainingTurnTransitions: MaximumCycleExitProbeTurnTransitions);
        if (!HasCycleAdmissionTranspositionLease(candidate)
            || HasCycleExpansionTranspositionLease(candidate))
        {
            throw new InvalidOperationException(
                "待签发的循环出口票据没有被限制在 admission 阶段。");
        }
        if (!tracker.TryMarkExitProbeIssued(0, actionKey, generation))
            throw new InvalidOperationException("循环出口测试票据无法签发。");
        candidate.CycleExitProbe = candidate.CycleExitProbe with { LeaseIssued = true };
        if (!HasCycleExpansionTranspositionLease(candidate))
            throw new InvalidOperationException("已签发的循环出口票据无法继续推进。");
    }

    /// <summary>两张转置表共用条目预算；已有状态的支配标签继续更新。</summary>
    private bool AtTranspositionEntryLimit()
        => policy.TranspositionEntryLimit > 0
            && _run.Transpositions.Count + _run.ExpandedTranspositions.Count
                >= policy.TranspositionEntryLimit;

    private void ObserveTranspositionEntries()
        => _run.TranspositionDiagnostics.ObserveEntries(
            _run.Transpositions.Count + _run.ExpandedTranspositions.Count,
            policy.TranspositionEntryLimit, _run.Expanded);

    private bool TryAcceptTransposition(SearchNode candidate)
    {
        // Scheduling obligations are deliberately bounded elsewhere. A normal route at the
        // same simulator state cannot inherit their exact pattern/envelope history, so it must
        // not erase the probe before the obligation reaches the frontier.
        if (HasCycleAdmissionTranspositionLease(candidate)
            || CanRetainOrderedMutationLease(_run, candidate))
        {
            ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "bypass_cycle_or_ordered_lease");
            return true;
        }
        if ((policy.TranspositionPruningDisabledMask & 1) != 0)
        {
            ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "disabled_for_measurement");
            return true;
        }
        TranspositionLabel next = new(
            candidate.PotionCount,
            candidate.PotionStrategicCost,
            candidate.FutureSoldHp,
            candidate.Snapshot.CumulativePlayerHpLost,
            candidate.ActionCount,
            candidate.Score);
        if (!_run.Transpositions.TryGetValue(candidate.StateKey, out TranspositionFrontier? frontier))
        {
            if (AtTranspositionEntryLimit())
            {
                _run.TranspositionLimitBypasses++;
                ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "entry_limit");
                return true;
            }
            _run.Transpositions.Add(candidate.StateKey, new TranspositionFrontier(next));
            ObserveTranspositionEntries();
            ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "accepted_new_state");
            return true;
        }
        if (frontier.TryAccept(next))
        {
            ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "accepted_label");
            return true;
        }
        _run.TranspositionBranchesPruned++;
        ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "rejected_dominated");
        if (_detailedDiagnostics && candidate.ActionCount <= 2)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Debug] TRANSPOSITION_REJECT route=" +
                $"{string.Join('>', candidate.Actions.Select(PolicyActionToken))} " +
                $"score={candidate.Score:F0} hp={candidate.Snapshot.ProjectedPlayerHp} " +
                $"enemy={candidate.Snapshot.EnemyHp} hand=" +
                $"{candidate.Snapshot.HandCount}/{candidate.Snapshot.ReachableHandValue}/" +
                $"{candidate.Snapshot.ZeroCostPlayableCount}");
        }
        return false;
    }

    private bool TryMarkExpandedState(SearchNode node)
    {
        if (node.PendingCycleExitObservation != null)
        {
            SearchReplayEvidence.PublishCandidateFailure(policy.Diagnostics, node, "expansion_admission_frontier");
            throw new InvalidOperationException(
                "临时循环出口 observation 越过了 action admission frontier。");
        }
        if (HasCycleExpansionTranspositionLease(node)
            || CanRetainOrderedMutationLease(_run, node))
        {
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "bypass_cycle_or_ordered_lease");
            return true;
        }
        if ((policy.TranspositionPruningDisabledMask & 2) != 0)
        {
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "disabled_for_measurement");
            return true;
        }
        TranspositionLabel next = new(
            node.PotionCount,
            node.PotionStrategicCost,
            node.FutureSoldHp,
            node.Snapshot.CumulativePlayerHpLost,
            node.ActionCount,
            node.Score);
        if (!_run.ExpandedTranspositions.TryGetValue(node.StateKey, out TranspositionFrontier? frontier))
        {
            if (AtTranspositionEntryLimit())
            {
                _run.TranspositionLimitBypasses++;
                ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "entry_limit");
                return true;
            }
            _run.ExpandedTranspositions.Add(node.StateKey, new TranspositionFrontier(next));
            ObserveTranspositionEntries();
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "accepted_new_state");
            return true;
        }
        if (frontier.TryAccept(next))
        {
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "accepted_label");
            return true;
        }
        _run.TranspositionBranchesPruned++;
        ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "rejected_dominated");
        return false;
    }

}
