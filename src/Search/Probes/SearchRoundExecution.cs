using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed class SearchRoundContext(
    Player player,
    int startTurnNumber,
    SearchPerformanceMetrics performance,
    bool recordExecutionFrames,
    Action<object?, CombatPredictionSimulator, SimulatedCombatState, TurnStartChoiceCursor, ISet<uint>, int, bool, bool, int, bool>? captureBeforeHandDraw,
    Action<object?, CombatPredictionSimulator, SimulatedCombatState, TurnStartChoiceCursor, ISet<uint>, int, bool, bool>? capture)
{
    public Player Player { get; } = player;
    public int StartTurnNumber { get; } = startTurnNumber;
    public SearchPerformanceMetrics Performance { get; } = performance;
    public bool RecordExecutionFrames { get; } = recordExecutionFrames;
    public Action<object?, CombatPredictionSimulator, SimulatedCombatState, TurnStartChoiceCursor, ISet<uint>, int, bool, bool, int, bool>? CaptureBeforeHandDraw { get; } = captureBeforeHandDraw;
    public Action<object?, CombatPredictionSimulator, SimulatedCombatState, TurnStartChoiceCursor, ISet<uint>, int, bool, bool>? Capture { get; } = capture;
}

internal static class SearchRoundExecution
{
    internal static SearchBoundaryReason AdvanceRound(
        SearchRoundContext context,
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        ref int shufflesCrossed,
        IReadOnlyList<PlanCardChoice>? turnStartChoices,
        object? roundCheckpointCapture = null)
    {
        if (!simulator.IsInProgress)
            return SearchBoundaryReason.None;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(context.Player);
        IReadOnlyList<PlanCardChoice>? roundChoicePlans = turnStartChoices?
            .Where(choice => choice.Effect != PlanChoiceEffect.ApplyKnowledgeCurse)
            .ToArray();
        TurnStartChoiceCursor roundChoices = new(roundChoicePlans);
        simulatedCombat.BeginActionChoices(roundChoices);
        simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnEnd);
        try
        {
        int roundHistoryEntryStart = simulator.History.Entries.Count;
        int etherealExhaustCount = simulatedCombat.CountEtherealCardsInHand(simulator, context.Player);
        {
            using SearchMeasurementScope _ = context.Performance.Measure(SearchMetricPhase.RoundPlayerEnd);
            bool playerTurnEndCompleted;
            using (context.Performance.Measure(SearchMetricPhase.RoundEndSimulation))
                playerTurnEndCompleted = PlayerTurnEndLifecycle.RunPhaseOne(
                    simulator,
                    simulatedCombat,
                    context.Player,
                    [context.Player.Creature]);
            if (!playerTurnEndCompleted)
                return SearchBoundaryReason.PendingChoice;
            simulatedCombat.CommitHistoryCourseTurn(context.Player);
            simulatedCombat.NormalizeAeonglassWithers(simulator);
            simulatedCombat.NormalizeCardAfflictions(simulator);
            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    simulatedCombat,
                    simulatedCombat.KnownEnemies,
                    processedEnemyDeaths))
            {
                return SearchBoundaryReason.PendingChoice;
            }
            // Finish the already-started phase-one compensation above, but do not
            // flush the hand or enter phase two after either native phase-one check ended combat.
            if (!simulator.IsInProgress)
                return SearchBoundaryReason.None;
            using (context.Performance.Measure(SearchMetricPhase.RoundFlush))
                CorePowerSupport.FlushPlayerHandAtTurnEnd(simulator, simulatedCombat, context.Player);
            int turnEndShuffleEvents = simulator.ShuffleEventCount;
            bool playerTurnEndCompletedPhaseTwo;
            using (context.Performance.Measure(SearchMetricPhase.RoundPlayerEndPowers))
            {
                playerTurnEndCompletedPhaseTwo = PlayerTurnEndLifecycle.RunPhaseTwo(
                    simulator,
                    simulatedCombat,
                    [context.Player.Creature],
                    etherealExhaustCount);
            }
            shufflesCrossed += simulator.ShuffleEventCount - turnEndShuffleEvents;
            if (!playerTurnEndCompletedPhaseTwo)
                return SearchBoundaryReason.PendingChoice;
            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    simulatedCombat,
                    simulatedCombat.KnownEnemies,
                    processedEnemyDeaths))
            {
                return SearchBoundaryReason.PendingChoice;
            }
        }

        bool takingExtraTurn = Engine.InCombat.Mirrors.HookMirrors.ShouldTakeExtraTurn(simulator, simulatedCombat, context.Player);
        bool hasActiveEmotionChip = simulatedCombat.RelicsOf(context.Player).Any(relic => relic is MegaCrit.Sts2.Core.Models.Relics.EmotionChip && !relic.IsMelted);
        SimCreatureState simulatedPlayer = simulator.State.GetCreature(context.Player.Creature);
        if (!takingExtraTurn)
        {
            simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.EnemyTurn);
            using SearchMeasurementScope _ = context.Performance.Measure(SearchMetricPhase.RoundEnemyTurn);
            Creature[] actingEnemies = simulatedCombat.Enemies.ToArray();
            {
                using SearchMeasurementScope enemyStart = context.Performance.Measure(SearchMetricPhase.RoundEnemyStart);
                simulatedCombat.CurrentSide = CombatSide.Enemy;
                foreach (Creature enemy in simulatedCombat.Enemies)
                    simulatedCombat.BeginSideTurn(enemy);
                simulatedCombat.SnapshotPowerAmountsAtTurnStart(simulatedCombat.Enemies);
                // 怪物方开始回合时，上一怪物回合留下的格挡先清除。
                if (!CombatSolver.Engine.InCombat.Mirrors.HookMirrors.BeforeSideTurnStart(
                        simulator, simulatedCombat.CurrentSide, simulatedCombat.Enemies))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                foreach (Creature enemy in simulatedCombat.Enemies)
                {
                    SimCreatureState simulatedEnemy = simulator.State.GetCreature(enemy);
                    if (simulatedEnemy.Block > 0)
                    {
                        if (simulatedCombat.ShouldClearBlock(enemy, out AbstractModel? preventer))
                            simulatedEnemy.DamageBlock(simulatedEnemy.Block, ValueProp.Move);
                        else
                            PersistentRelicSupport.TriggerAfterPreventingBlockClear(simulator, preventer, enemy);
                    }
                    if (!CorePowerSupport.TriggerAfterBlockCleared(
                            simulator,
                            simulatedCombat,
                            enemy))
                    {
                        return SearchBoundaryReason.PendingChoice;
                    }
                }
                bool decrementEnemyPlating = simulatedCombat.RoundNumber > 1;
                if (!simulatedCombat.TriggerSideTurnStart(
                        simulator,
                        CombatSide.Enemy,
                        simulatedCombat.Enemies,
                        decrementEnemyPlating))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                int enemyPoisonHistoryStart = simulator.History.Entries.Count;
                if (!CorePowerSupport.TriggerPoison(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.Enemies.ToArray()))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                TriggeredPowerSupport.CompensateHistorySince(
                    simulator,
                    simulatedCombat,
                    enemyPoisonHistoryStart);
                if (simulatedCombat.HasPendingChoice)
                    return SearchBoundaryReason.PendingChoice;
                if (!CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.KnownEnemies,
                        processedEnemyDeaths))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
            }
            // Vanilla checks after the entire enemy-side start, not between listeners.
            if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(context.Player)))
                return SearchBoundaryReason.None;
            Dictionary<Creature, MoveState> performedMoves;
            {
                using SearchMeasurementScope enemyMoves = context.Performance.Measure(SearchMetricPhase.RoundEnemyMoves);
                performedMoves = new Dictionary<Creature, MoveState>(actingEnemies.Length);
                foreach (Creature actingEnemy in actingEnemies)
                {
                    if (!simulatedCombat.CanPerformMonsterMove(simulator, actingEnemy))
                        continue;
                    ForecastMove move = simulatedCombat.CurrentMonsterMove(actingEnemy);
                    if (simulatedCombat.ConsumeStunNextMove(actingEnemy))
                    {
                        performedMoves[actingEnemy] = move.Move;
                        if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(context.Player)))
                            return SearchBoundaryReason.None;
                        continue;
                    }
                    if (simulatedCombat.TryConsumeForcedMonsterMove(actingEnemy, out string forcedMove, out int forcedDamage))
                    {
                        performedMoves[actingEnemy] = move.Move;
                        if (forcedMove == SimulatedCombatState.ExplodeMoveId)
                        {
                            MonsterMoveSemantics.DamagePlayer(
                                simulator,
                                simulatedCombat,
                                move.Owner,
                                context.Player.Creature,
                                forcedDamage);
                            if (simulatedCombat.HasPendingChoice)
                                return SearchBoundaryReason.PendingChoice;
                            using (simulator.PushDamageSource(
                                CombatDamageSource.For(
                                    CombatDamageSourceKind.MonsterMove,
                                    move.Owner.Monster?.Id.Entry)))
                            {
                                simulator.Kill(move.Owner, force: true);
                            }
                            if (simulatedCombat.HasPendingChoice)
                                return SearchBoundaryReason.PendingChoice;
                            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                                    simulator,
                                    simulatedCombat,
                                    simulatedCombat.KnownEnemies,
                                    processedEnemyDeaths))
                            {
                                return SearchBoundaryReason.PendingChoice;
                            }
                        }
                        if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(context.Player)))
                            return SearchBoundaryReason.None;
                        continue;
                    }
                    bool playerDied = MonsterMoveSemantics.ApplyForecastMove(
                            simulator,
                            simulatedCombat,
                            move,
                            context.Player.Creature,
                            processedEnemyDeaths,
                            turnStartChoices);
                    performedMoves[actingEnemy] = move.Move;
                    if (move.Owner.CombatId is uint revivedCombatId
                        && simulator.State.GetCreature(move.Owner).IsAlive)
                    {
                        processedEnemyDeaths.Remove(revivedCombatId);
                    }
                    if (simulatedCombat.HasPendingChoice)
                        return SearchBoundaryReason.PendingChoice;
                    // ApplyForecastMove has completed its attack finally and command tails.
                    if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(context.Player))
                        || playerDied)
                        return SearchBoundaryReason.None;
                }
            }

            using (context.Performance.Measure(SearchMetricPhase.RoundEnemyEndPowers))
            {
                if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.Enemies.ToArray()))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                if (simulatedCombat.BattlewornDummyTimedOut)
                    return SearchBoundaryReason.EventDefeat;
                if (!CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.KnownEnemies,
                        processedEnemyDeaths))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                int playerPoisonHistoryStart = simulator.History.Entries.Count;
                if (!CorePowerSupport.TriggerPoison(
                        simulator,
                        simulatedCombat,
                        [context.Player.Creature]))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                TriggeredPowerSupport.CompensateHistorySince(
                    simulator,
                    simulatedCombat,
                    playerPoisonHistoryStart);
                if (simulatedCombat.HasPendingChoice)
                    return SearchBoundaryReason.PendingChoice;
                simulatedCombat.ClearNoDraw(context.Player.Creature);
                simulatedCombat.RecordRelicRoundDamage(simulator, context.Player, roundHistoryEntryStart);
            }
            if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(context.Player)))
                return SearchBoundaryReason.None;
            simulatedCombat.PrepareMonsterMovesForNextRound(simulator, performedMoves);
        }
        else
        {
            // An extra turn advances the player's turn number too, so damage from the
            // just-finished turn becomes Emotion Chip's "previous turn" window.
            if (hasActiveEmotionChip)
                simulatedCombat.RecordRelicRoundDamage(simulator, context.Player, roundHistoryEntryStart);
            if (!Engine.InCombat.Mirrors.HookMirrors.AfterTakingExtraTurn(simulator, simulatedCombat, context.Player))
                return SearchBoundaryReason.PendingChoice;
        }

        return AdvanceRoundPlayerStart(context, simulator, simulatedCombat, playerState, simulatedPlayer,
            roundIndex, processedEnemyDeaths, ref shufflesCrossed, roundChoices, takingExtraTurn,
            turnStartChoices is not { Count: > 0 } ? roundCheckpointCapture : null);
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    internal static SearchBoundaryReason AdvanceRoundPlayerStart(
        SearchRoundContext context,
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        SimPlayerCombatState playerState,
        SimCreatureState simulatedPlayer,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        ref int shufflesCrossed,
        TurnStartChoiceCursor roundChoices,
        bool takingExtraTurn,
        object? capture = null)
    {
        simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
        using SearchMeasurementScope _ = context.Performance.Measure(SearchMetricPhase.RoundPlayerStart);
        simulatedCombat.CurrentSide = CombatSide.Player;
        if (!takingExtraTurn)
            simulatedCombat.RoundNumber++;
        simulatedCombat.AdvancePlayerTurn(context.Player);
        simulatedCombat.BeginSideTurn(context.Player.Creature);
        simulatedCombat.SnapshotPowerAmountsAtTurnStart([context.Player.Creature]);

        if (!CombatSolver.Engine.InCombat.Mirrors.HookMirrors.BeforeSideTurnStart(
                simulator, simulatedCombat.CurrentSide, [context.Player.Creature]))
        {
            return SearchBoundaryReason.PendingChoice;
        }

        if (simulatedPlayer.Block > 0)
        {
            if (simulatedCombat.ShouldClearBlock(context.Player.Creature, out AbstractModel? preventer))
                simulatedPlayer.DamageBlock(simulatedPlayer.Block, ValueProp.Move);
            else
                PersistentRelicSupport.TriggerAfterPreventingBlockClear(
                    simulator,
                    preventer,
                    context.Player.Creature);
        }
        if (!CorePowerSupport.TriggerAfterBlockCleared(
                simulator,
                simulatedCombat,
                context.Player.Creature))
        {
            return SearchBoundaryReason.PendingChoice;
        }

        if (PersistentRelicSupport.ShouldPlayerResetEnergy(simulatedCombat, context.Player))
            playerState.LoseEnergy(playerState.Energy);
        playerState.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(simulatedCombat, context.Player)
            + simulatedCombat.ConsumeEnergyNextTurn(context.Player));
        if (simulatedCombat.HasPendingChoice
            || !PersistentPowerSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, context.Player))
        {
            return SearchBoundaryReason.PendingChoice;
        }
        TurnStartRelicSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, context.Player);
        if (simulatedCombat.HasPendingChoice)
            return SearchBoundaryReason.PendingChoice;
        TurnStartRelicSupport.TriggerAfterEnergyResetLate(simulator, simulatedCombat, context.Player);
        if (simulatedCombat.HasPendingChoice)
            return SearchBoundaryReason.PendingChoice;
        var progress = new PlayerStartProgress(context.Player, context.StartTurnNumber + roundIndex + 1,
            rootSetup: false, takingExtraTurn, processedEnemyDeaths, shufflesCrossed, simulator.ShuffleEventCount);
        SearchBoundaryReason result = ContinuePlayerStart(context, simulator, simulatedCombat, progress,
            PlayerStartStage.BeforeHand, capture);
        shufflesCrossed = progress.ShufflesCrossed;
        return result;
    }

    internal static SearchBoundaryReason ContinuePlayerStart(
        SearchRoundContext context,
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PlayerStartProgress progress,
        PlayerStartStage stage,
        object? capture = null)
    {
        simulator.AcknowledgeExecutionDispatch();
        Player player = progress.Player;
        var playerState = simulator.State.GetPlayerCombatState(player);
        TurnStartChoiceCursor choices = combat.ActiveExecutionChoices;
        if (stage == PlayerStartStage.BeforeHand && simulator.IsCapturingExecutionContinuation)
        {
            using (combat.SuspendExecutionChoicesForStablePrefix()) simulator.TrySealStableExecutionPrefix();
        }
        bool StartSide()
        {
            progress.SideStarted = true;
            // Side-start sources with additional work must qualify their own continuation.
            using var dispatch = simulator.BeginExecutionDispatch();
            return combat.TriggerSideTurnStart(simulator, CombatSide.Player, [player.Creature],
                decrementPlating: combat.GetPlayerTurnNumber(player) != 1, progress.TakingExtraTurn);
        }
        bool Suspended(PlayerStartStage next)
        {
            if (!combat.HasPendingChoice) return false;
            if (context.RecordExecutionFrames)
                simulator.AppendExecutionContinuation(new PlayerStartFrame(context, progress, next));
            return true;
        }
        using var beforeChoice = !progress.SideStarted && stage <= PlayerStartStage.AfterPlayer
            ? choices.BeforeNextTake(StartSide) : null;
        if (stage <= PlayerStartStage.BeforeHand)
        {
            combat.PrepareBeforeHandDraw(simulator, player, choices);
            if (Suspended(PlayerStartStage.PrepareDraw)) return SearchBoundaryReason.PendingChoice;
        }
        if (stage <= PlayerStartStage.PrepareDraw)
        {
            // BeforeHandDraw may generate cards. Their listeners (for example Arsenal)
            // can change Power amounts, which must resolve before the pre-draw checkpoint.
            using (simulator.BeginExecutionDispatch())
                PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
            if (Suspended(PlayerStartStage.PrepareDraw)) return SearchBoundaryReason.PendingChoice;
            if (!progress.RootSetup)
                progress.ShufflesCrossed += simulator.ShuffleEventCount - progress.BeforeHandShuffles;
            progress.DrawCount = PersistentPowerSupport.ConsumeModifiedHandDraw(combat, player, CombatManager.baseHandDrawCount);
            if (progress.RootSetup && progress.TurnNumber == 1)
            {
                SimCardPile drawPile = playerState.DrawPile;
                PredictedCard[] bottom = drawPile.Cards.Where(card => card.Preview.Enchantment?.ShouldStartAtBottomOfDrawPile ?? false).ToArray();
                foreach (PredictedCard card in bottom) { drawPile.Remove(card); drawPile.Add(card); }
                PredictedCard[] innate = drawPile.Cards.Where(card => card.Preview.Keywords.Contains(CardKeyword.Innate)).Except(bottom).ToArray();
                foreach (PredictedCard card in innate) { drawPile.Remove(card); drawPile.Insert(0, card); }
                progress.DrawCount = Math.Min(Math.Max(progress.DrawCount, innate.Length), combat.GetMaxHandSize(player));
            }
            int effectiveDraw = Math.Min(progress.DrawCount, combat.GetMaxHandSize(player) - playerState.Hand.Cards.Count);
            progress.WillShuffle = effectiveDraw > playerState.DrawPile.Cards.Count && !playerState.DiscardPile.IsEmpty;
            context.CaptureBeforeHandDraw?.Invoke(capture, simulator, combat, choices, progress.Deaths,
                progress.ShufflesCrossed, progress.TakingExtraTurn, progress.SideStarted, progress.DrawCount, progress.WillShuffle);
        }
        if (stage <= PlayerStartStage.Draw)
        {
            progress.DrawHistoryStart = simulator.History.Entries.Count;
            using (context.Performance.Measure(SearchMetricPhase.RoundDraw))
                simulator.Draw(player, progress.DrawCount, fromHandDraw: true);
            if (!progress.RootSetup && progress.WillShuffle) progress.ShufflesCrossed++;
            if (Suspended(PlayerStartStage.CompensateDraw)) return SearchBoundaryReason.PendingChoice;
        }
        if (stage <= PlayerStartStage.CompensateDraw)
        {
            using (simulator.BeginExecutionDispatch())
                TriggeredPowerSupport.CompensateHistorySince(simulator, combat, progress.DrawHistoryStart);
            if (Suspended(PlayerStartStage.AfterPlayer)) return SearchBoundaryReason.PendingChoice;
            context.Capture?.Invoke(capture, simulator, combat, choices, progress.Deaths,
                progress.ShufflesCrossed, progress.TakingExtraTurn, progress.SideStarted);
        }
        if (stage <= PlayerStartStage.AfterPlayer)
        {
            combat.TriggerAfterPlayerTurnStart(simulator, player.Creature, choices);
            if (Suspended(PlayerStartStage.Side)) return SearchBoundaryReason.PendingChoice;
        }
        if (stage <= PlayerStartStage.Side && !progress.SideStarted)
        {
            StartSide();
            if (Suspended(PlayerStartStage.Deaths)) return SearchBoundaryReason.PendingChoice;
        }
        beforeChoice?.Dispose();
        if (stage <= PlayerStartStage.Deaths)
        {
            using (simulator.BeginExecutionDispatch())
                CorePowerSupport.ApplyEnemyDeathPowers(simulator, combat, combat.KnownEnemies, progress.Deaths);
            if (Suspended(PlayerStartStage.Orbs)) return SearchBoundaryReason.PendingChoice;
        }
        if (stage <= PlayerStartStage.Orbs)
        {
            using (simulator.BeginExecutionDispatch())
                EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(simulator, player);
            if (Suspended(PlayerStartStage.AutoPlay)) return SearchBoundaryReason.PendingChoice;
        }
        if (stage <= PlayerStartStage.AutoPlay)
        {
            using (simulator.BeginExecutionDispatch())
                combat.TriggerAutoPrePlayEarly(simulator, player, progress.TurnNumber, choices, progress.Deaths);
            if (Suspended(PlayerStartStage.Finish)) return SearchBoundaryReason.PendingChoice;
        }
        choices.AssertConsumed();
        combat.NormalizeAeonglassWithers(simulator);
        combat.NormalizeCardAfflictions(simulator);
        simulator.CheckWinCondition(combat.GetPlayerTurnNumber(player));
        return SearchBoundaryReason.None;
    }
}

internal enum PlayerStartStage { BeforeHand, PrepareDraw, Draw, CompensateDraw, AfterPlayer, Side, Deaths, Orbs, AutoPlay, Finish }

// One branch owns the progress object. All frames and the replay accounting refer to
// the same forked copy; an early side-start callback is never stored in a frame.
internal sealed class PlayerStartProgress(Player player, int turnNumber, bool rootSetup,
    bool takingExtraTurn, ISet<uint> deaths, int shufflesCrossed, int beforeHandShuffles)
{
    public Player Player { get; } = player;
    public int TurnNumber { get; } = turnNumber;
    public bool RootSetup { get; } = rootSetup;
    public bool TakingExtraTurn { get; } = takingExtraTurn;
    public ISet<uint> Deaths { get; private set; } = deaths;
    public int ShufflesCrossed = shufflesCrossed;
    public int BeforeHandShuffles = beforeHandShuffles;
    public bool SideStarted;
    public int DrawCount;
    public bool WillShuffle;
    public int DrawHistoryStart;

    public PlayerStartProgress Fork(PredictionForkContext context)
    {
        if (context.TryRemap(this, out PlayerStartProgress? found)) return found!;
        var result = (PlayerStartProgress)MemberwiseClone();
        result.Deaths = SimulatedCombatState.ForkExecutionDeaths(Deaths, context);
        context.Register(this, result);
        return result;
    }
}

internal sealed record PlayerStartFrame(SearchRoundContext Context, PlayerStartProgress Progress, PlayerStartStage Stage)
    : ICombatPredictionExecutionFrame
{
    public void PrepareFork(PredictionForkContext context) => Progress.Fork(context);
    public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
        => this with { Progress = context.RequireRemap(Progress) };
    public bool Resume(CombatPredictionSimulator simulator)
        => SearchRoundExecution.ContinuePlayerStart(Context, simulator, (SimulatedCombatState)simulator.State.CombatState,
            Progress, Stage) == SearchBoundaryReason.None;
}
