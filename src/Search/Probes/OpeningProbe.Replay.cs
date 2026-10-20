using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

internal sealed partial class OpeningProbe
{
    private IReadOnlyList<TurnSetupRootCandidate> BuildTurnSetupRoots()
    {
        List<TurnSetupRootCandidate> roots = [];
        SimulationSnapshot initial = ReplayTurnSetup([]);
        bool transferred = false;
        try
        {
            ResolveTurnSetupChoices([], initial, roots, depth: 0);
            transferred = true;
        }
        finally
        {
            if (!transferred)
                initial.ReleaseSimulator();
        }
        return roots;
    }

    private void ResolveTurnSetupChoices(IReadOnlyList<PlanCardChoice> choices,
        SimulationSnapshot snapshot,
        List<TurnSetupRootCandidate> roots,
        int depth)
        => ResolveTurnSetupChoicesProbe(choices, snapshot, roots, depth);

    private void ResolveTurnSetupChoicesProbe(
        IReadOnlyList<PlanCardChoice> choices,
        SimulationSnapshot snapshot,
        List<TurnSetupRootCandidate> roots,
        int depth)
    {
        if (depth > 32)
            throw new InvalidOperationException("回合准备选择递归超过上限。");
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
        {
            roots.Add(new TurnSetupRootCandidate(choices, snapshot));
            return;
        }

        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (combat.PendingTurnStartChoice is not { } request)
            throw new InvalidOperationException("回合准备阶段产生了未登记的选择类型。");
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
            throw new InvalidOperationException("回合准备选择没有生成任何分支。");
        snapshot.ReleaseSimulator();
        foreach (PlanCardChoice branch in branches)
        {
            PlanCardChoice resolved = branch with
            {
                SourceId = request.SourceId,
                ContextId = request.ContextId,
                Timing = request.Timing,
            };
            List<PlanCardChoice> next = new(choices.Count + 1);
            next.AddRange(choices);
            next.Add(resolved);
            SimulationSnapshot child = ReplayTurnSetup(next);
            ResolveTurnSetupChoices(next, child, roots, depth + 1);
        }
    }

    private SimulationSnapshot ReplayTurnSetup(IReadOnlyList<PlanCardChoice> choices)
        => ReplayTurnSetupProbe(choices);

    private SimulationSnapshot ReplayTurnSetupProbe(IReadOnlyList<PlanCardChoice> choices)
    {
        _run.ReplayCount++;
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        ForkableSet<uint> processedEnemyDeaths = [];
        foreach (Creature enemy in root.Enemies)
        {
            if (enemy.CombatId is uint combatId && simulator.State.GetCreature(enemy).IsDead)
                processedEnemyDeaths.Add(combatId);
        }

        TurnStartChoiceCursor cursor = new(choices);
        simulatedCombat.BeginActionChoices(cursor);
        simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
        SearchBoundaryReason boundary;
        try
        {
            using var executionDispatch = simulator.BeginExecutionDispatch();
            boundary = PreparePlayerPlayPhase(
                RoundContext, simulator,
                simulatedCombat,
                cursor,
                processedEnemyDeaths);
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
        if (boundary == SearchBoundaryReason.None
            && !SettleReplayActionBoundary(simulator, simulatedCombat))
        {
            boundary = SearchBoundaryReason.PendingChoice;
        }
        return Snapshot(
            simulator,
            _startTurnNumber,
            actionCount: 0,
            shufflesCrossed: simulator.ShuffleEventCount,
            boundary,
            processedEnemyDeaths);
    }

    private SimulationSnapshot Replay(IReadOnlyList<PlanAction> actions,
        SimulationSnapshot? parentSnapshot = null,
        int startingTurn = 0,
        int priorActionCount = 0,
        bool countTransition = true)
        => ReplayProbe(actions, parentSnapshot, startingTurn, priorActionCount, countTransition);

    private SimulationSnapshot ReplayProbe(
        IReadOnlyList<PlanAction> actions,
        SimulationSnapshot? parentSnapshot = null,
        int startingTurn = 0,
        int priorActionCount = 0,
        bool countTransition = true)
    {
        CombatPredictionSimulator simulator;
        SimulatedCombatState simulatedCombat;
        int turn;
        int shufflesCrossed;
        SearchBoundaryReason boundary = SearchBoundaryReason.None;
        ForkableSet<uint> processedEnemyDeaths;
        if (parentSnapshot is null)
        {
            _run.ReplayCount++;
            simulator = root.ForkSimulator();
            simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
            turn = _startTurnNumber;
            shufflesCrossed = 0;
            processedEnemyDeaths = [];
            foreach (Creature enemy in root.Enemies)
            {
                if (enemy.CombatId is uint combatId && simulator.State.GetCreature(enemy).IsDead)
                    processedEnemyDeaths.Add(combatId);
            }
        }
        else
        {
            if (parentSnapshot.BoundaryReason != SearchBoundaryReason.None)
                throw new InvalidOperationException("不能从已抵达搜索边界的模拟状态继续分叉。");
            if (countTransition) _run.TransitionCount += actions.Count;
            _run.ForkCount++;
            simulator = ((CombatPredictionSimulator)parentSnapshot.Simulator).Fork();
            processedEnemyDeaths =
                ((ForkableSet<uint>)parentSnapshot.ProcessedEnemyDeaths).Fork();
            simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
            turn = startingTurn;
            shufflesCrossed = parentSnapshot.ShufflesCrossed;
        }

        int actionShuffleEventsBefore = simulator.ShuffleEventCount;
        try
        {
            for (int actionOffset = 0; actionOffset < actions.Count; actionOffset++)
            {
                if (!simulator.IsInProgress)
                    throw new InvalidOperationException("回放包含已锁定战斗终局之后的动作。");
                PlanAction action = actions[actionOffset];
                cancellationToken.ThrowIfCancellationRequested();
                if (action.Kind == PlanActionKind.EndTurn)
                {
                    using var executionDispatch = simulator.BeginExecutionDispatch();
                    boundary = AdvanceRound(
                        simulator,
                        simulatedCombat,
                        turn - _startTurnNumber,
                        processedEnemyDeaths,
                        ref shufflesCrossed,
                        action.TurnStartChoices);
                    _ = simulatedCombat.ConsumePlayerTurnEndRequest();
                    if (boundary == SearchBoundaryReason.None
                        && !SettleReplayActionBoundary(simulator, simulatedCombat))
                    {
                        boundary = SearchBoundaryReason.PendingChoice;
                    }
                    turn = simulatedCombat.GetPlayerTurnNumber(_player);
                    continue;
                }

                if (action.Kind == PlanActionKind.UsePotion)
                {
                    PotionModel potion = simulatedCombat.GetPotionAtSlot(_player, action.PotionSlot)
                        ?? throw new InvalidOperationException($"回放时药水槽位 {action.PotionSlot} 为空。");
                    if (!string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"回放时药水槽位 {action.PotionSlot} 为 {potion.Id.Entry}，预期 {action.PotionId}。");
                    }
                    if (!simulatedCombat.IsPotionAvailable(_player, action.PotionSlot))
                        throw new InvalidOperationException($"回放时药水 {action.PotionId} 已被消耗。");
                    Creature? potionTarget = simulatedCombat.GetCreature(action.TargetCombatId);
                    int potionShuffleEvents = simulator.ShuffleEventCount;
                    int potionHistoryEntryStart = simulator.History.Entries.Count;
                    simulatedCombat.BeginActionChoices(action.NestedChoices);
                    try
                    {
                        if (!PotionExecutionSupport.Prepare(
                                simulator, simulatedCombat, potion, action.PotionSlot, potionTarget))
                        {
                            boundary = SearchBoundaryReason.PendingChoice;
                            break;
                        }
                        if (!PotionExecutionSupport.Complete(simulator, simulatedCombat, potion, potionTarget,
                                action.Choice, potionHistoryEntryStart, processedEnemyDeaths))
                        {
                            boundary = SearchBoundaryReason.PendingChoice;
                            break;
                        }
                        if (!SettleReplayActionBoundary(simulator, simulatedCombat))
                        {
                            boundary = SearchBoundaryReason.PendingChoice;
                            break;
                        }
                    }
                    finally
                    {
                        simulatedCombat.EndActionChoices();
                    }
                    if (simulator.ShuffleEventCount != potionShuffleEvents)
                        shufflesCrossed++;
                    simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player));
                    boundary = ResolveRequestedPlayerTurnEnd(
                        RoundContext, simulator, simulatedCombat, action, processedEnemyDeaths, ref turn, ref shufflesCrossed);
                    continue;
                }

                SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
                PredictedCard? card = FindCardForReplay(playerState.Hand.Cards, action);
                if (card is null)
                {
                    string hand = string.Join(',', playerState.Hand.Cards.Select(candidate =>
                        $"{candidate.Preview.Id.Entry}#{candidate.Preview.CurrentUpgradeLevel}:{CardChoiceSupport.ChoiceCardKey(candidate)}"));
                    throw new InvalidOperationException(
                        $"回放时找不到手牌 {action.CardId}#{action.CardOccurrence}：turn={turn} " +
                        $"action_index={priorActionCount + actionOffset} " +
                        $"state_occurrence={action.CardStateOccurrence} state_key={action.CardStateKey} hand={hand}。");
                }
                Creature? target = simulatedCombat.GetCreature(action.TargetCombatId);
                if (!simulatedCombat.CanPlayCard(simulator, card))
                {
                    int energyCost = card.GetEnergyCostWithModifiers(simulator, playerState);
                    int starCost = card.GetStarCostWithModifiers(simulator, playerState);
                    string hand = string.Join(',', playerState.Hand.Cards.Select(candidate =>
                        $"{candidate.Preview.Id.Entry}#{candidate.Preview.CurrentUpgradeLevel}"));
                    throw new InvalidOperationException(
                        $"回放时 {action.CardId} 已不可打出：turn={turn} " +
                        $"action_index={priorActionCount + actionOffset} occurrence={action.CardOccurrence} " +
                        $"energy={playerState.Energy} cost={energyCost} stars={playerState.Stars} star_cost={starCost} " +
                        $"hand={hand}。");
                }
                int shuffleEvents = simulator.ShuffleEventCount;
                simulatedCombat.BeginActionChoices(ActionChoicesForReplay(action));
                using IDisposable cardExecutionScope =
                    simulatedCombat.BeginCardExecutionScope(processedEnemyDeaths);
                bool cardPlayCompleted;
                try
                {
                    cardPlayCompleted = simulator.ManualPlay(card, target, out _);
                }
                finally
                {
                    simulatedCombat.EndActionChoices();
                }
                if (!cardPlayCompleted)
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                    break;
                }
                bool deathsCompleted;
                using (simulator.BeginExecutionDispatch())
                    deathsCompleted = CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator, simulatedCombat, simulatedCombat.KnownEnemies, processedEnemyDeaths);
                if (!deathsCompleted)
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                    break;
                }
                if (!SettleReplayActionBoundary(simulator, simulatedCombat))
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                    break;
                }
                if (simulator.ShuffleEventCount != shuffleEvents)
                    shufflesCrossed++;
                simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player));
                boundary = ResolveRequestedPlayerTurnEnd(
                    RoundContext, simulator, simulatedCombat, action, processedEnemyDeaths, ref turn, ref shufflesCrossed);
            }
        }
        catch
        {
            simulatedCombat.EndActionChoices();
            throw;
        }

        return Snapshot(
            simulator,
            turn,
            priorActionCount + actions.Count,
            shufflesCrossed,
            boundary,
            processedEnemyDeaths);
    }

    private SimulationSnapshot ReplayAction(SearchNode parent, PlanAction action)
        => SearchTransitionGuard.Execute(action, parent.StateKey, parent.ActionCount, () =>
            Replay([action], parent.Snapshot, parent.Turn, parent.ActionCount));

    private SearchBoundaryReason AdvanceRound(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        ref int shufflesCrossed,
        IReadOnlyList<PlanCardChoice>? turnStartChoices)
        => SearchRoundExecution.AdvanceRound(
            RoundContext, simulator, simulatedCombat, roundIndex, processedEnemyDeaths,
            ref shufflesCrossed, turnStartChoices);

    private SearchBoundaryReason AdvanceRoundPlayerStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        SimPlayerCombatState playerState,
        SimCreatureState simulatedPlayer,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        ref int shufflesCrossed,
        TurnStartChoiceCursor roundChoices,
        bool takingExtraTurn)
        => SearchRoundExecution.AdvanceRoundPlayerStart(
            RoundContext, simulator, simulatedCombat, playerState, simulatedPlayer,
            roundIndex, processedEnemyDeaths, ref shufflesCrossed, roundChoices,
            takingExtraTurn, null);

    private SimulationSnapshot Snapshot(
        CombatPredictionSimulator simulator,
        int turn,
        int actionCount,
        int shufflesCrossed,
        SearchBoundaryReason boundary,
        IReadOnlySet<uint> processedEnemyDeaths)
        => Evaluator.Snapshot(
            simulator, turn, actionCount, shufflesCrossed, boundary, processedEnemyDeaths);
}
