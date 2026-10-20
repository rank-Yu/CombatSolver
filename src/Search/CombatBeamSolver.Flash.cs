using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

/// <summary>A Flash action's choices do not line up with the choices the existing engine asks for.</summary>
internal sealed class FlashChoiceMismatchException(string message) : InvalidOperationException(message);

internal sealed partial class CombatBeamSolver
{
    /// <summary>
    /// Replays <paramref name="actions"/> through the existing engine from the root or from
    /// <paramref name="parent"/>. The Flash bridge uses this as the authoritative step for
    /// differential checks and route materialization.
    /// </summary>
    internal SimulationSnapshot ReplayForFlash(IReadOnlyList<PlanAction> actions, SimulationSnapshot? parent)
        => Replay(actions, parent, parent?.Turn ?? 0, countTransition: false, allowExecutionCapture: false);

    /// <summary>
    /// Replays this solver's fixed prefix from <paramref name="rootSnapshot"/> through the existing
    /// search's own fixed-prefix path and returns the state at its end. Returns null when the existing
    /// search rejects the prefix. The replay takes ownership of <paramref name="rootSnapshot"/>; callers
    /// that still need the request root keep a second root replay alive (see FlashRouteMaterializer).
    /// </summary>
    internal SimulationSnapshot? ReplayFixedPrefixForFlash(SimulationSnapshot rootSnapshot)
    {
        SearchNode seed = OpeningProbe.CreateOpeningSearchSeed(rootSnapshot);
        return ApplyFixedPrefix(seed)?.Snapshot;
    }

    /// <summary>
    /// The existing <c>PlanCommitment.CountRealizedPayoffs</c> over the solver's fixed prefix, for the
    /// Flash plan-payoff initial count. The prefix nodes carry actions and snapshots but no power
    /// commitments, exactly as in the existing search's fixed-prefix path.
    /// </summary>
    internal int ReplayFixedPrefixPlanPayoffForFlash(PlanCommitment plan)
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        SearchNode seed = OpeningProbe.CreateOpeningSearchSeed(rootSnapshot);
        SearchNode? node = ApplyFixedPrefix(seed);
        return node == null ? 0 : plan.CountRealizedPayoffs(node);
    }

    /// <summary>
    /// The existing search's potion constraints at <paramref name="root"/>, as plain values for the Flash
    /// search: per slot whether an explicit use is offered (<see cref="PotionOnUseSupport.CanSearch"/> and
    /// <see cref="AllowsPotionUse"/>) and its strategic HP cost, the earliest search turn
    /// (<c>node.Turn</c> units) and the maximum number of explicit uses.
    /// </summary>
    internal (bool[] Allowed, int[] Costs, int? EarliestTurn, int? MaximumUses) CapturePotionPolicyForFlash(SimulationSnapshot snapshot)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        bool[] allowed = new bool[root.PotionSlotCount];
        int[] costs = new int[root.PotionSlotCount];
        for (int slot = 0; slot < allowed.Length; slot++)
        {
            MegaCrit.Sts2.Core.Models.PotionModel? potion = combat.GetPotionAtSlot(_player, slot);
            if (potion == null)
                continue;
            allowed[slot] = combat.IsPotionAvailable(_player, slot)
                && PotionOnUseSupport.CanSearch(potion)
                && AllowsPotionUse(slot, potion.Id.Entry);
            bool renewable = potion is MegaCrit.Sts2.Core.Models.Potions.PotionShapedRock
                && combat.RelicsOf(_player).OfType<MegaCrit.Sts2.Core.Models.Relics.PetrifiedToad>().Any(static relic => !relic.IsMelted);
            costs[slot] = combat.IsFreeEntropicPotionAtSlot(_player, slot) ? 0 : PotionUsePolicy.StrategicHpCost(potion, renewable);
        }
        return (allowed, costs, _earliestPotionTurn, _maximumPotionUses);
    }

    /// <summary>
    /// Replays one action from <paramref name="parent"/>, answering each card choice the existing
    /// engine asks for with the next of <paramref name="selections"/> (bit sets over the request's
    /// spec options, in option order). Each answer is placed on the action exactly as the existing
    /// search places a resolved branch (<see cref="AppendPendingChoice"/>), with the first
    /// source-less request of a card play as its primary choice. <paramref name="inspect"/> sees each
    /// request before it is answered. Returns the snapshot after the last answer.
    /// </summary>
    internal SimulationSnapshot ReplayWithChoicesForFlash(
        ref PlanAction action,
        SimulationSnapshot parent,
        IReadOnlyList<ulong> selections,
        Action<int, TurnStartChoiceRequest, CardChoiceSpec>? inspect = null,
        Action<int, KnowledgeDemonChoiceRequest>? inspectKnowledge = null)
    {
        PrimaryChoiceMatch? primary = null;
        int taken = 0;
        while (true)
        {
            // Potion card-generation choices are not pending turn-start choices: the existing engine
            // stores the generated options in History during Prepare and resolves the action's
            // PlanAction.Choice in PotionExecutionSupport.Complete (PotionChoiceSupport.GetSpec/Apply).
            // Probe once to read the same options, then attach the selection as the action's choice.
            if (action.Kind == PlanActionKind.UsePotion && action.Choice == null && taken < selections.Count
                && TryBuildPotionChoice(action, parent, selections[taken], out CardChoiceSpec potionSpec,
                    out PlanAction potionAction))
            {
                inspect?.Invoke(taken, new TurnStartChoiceRequest(action.PotionId ?? potionSpec.Effect.ToString(),
                        potionSpec.Effect, potionSpec.SourcePile,
                        Math.Min(potionSpec.MaxCount, potionSpec.Options.Count), potionSpec,
                        Timing: PlanChoiceTiming.Action), potionSpec);
                action = potionAction;
                taken++;
                continue;
            }
            SimulationSnapshot child = ReplayForFlash([action], parent);
            if (child.BoundaryReason != SearchBoundaryReason.PendingChoice)
            {
                if (taken != selections.Count)
                    throw new FlashChoiceMismatchException($"Flash 做了 {selections.Count} 次选择，旧引擎只请求了 {taken} 次。");
                return child;
            }
            SimulatedCombatState combat = (SimulatedCombatState)child.Simulator.State.CombatState;
            // Knowledge Demon's curse choice is a fixed two-card option list, not a CardChoiceSpec:
            // KnowledgeDemonChoiceSupport.Resolve consumes a PlanCardChoice with SourcePile None and
            // one card token, and the existing search appends it to TurnStartChoices.
            if (combat.PendingKnowledgeDemonChoice is { } knowledge)
            {
                IReadOnlyList<PlanCardChoice> knowledgeBranches =
                    KnowledgeDemonChoiceSupport.BuildChoices(knowledge, displayNames);
                if (taken >= selections.Count)
                {
                    throw new FlashChoiceMismatchException(
                        $"旧引擎请求第 {taken + 1} 次选择（知识恶魔 {knowledge.SourceId}），Flash 没有做。");
                }
                inspectKnowledge?.Invoke(taken, knowledge);
                ulong knowledgeBits = selections[taken++];
                if (knowledgeBits == 0 || (knowledgeBits & (knowledgeBits - 1)) != 0
                    || knowledgeBits >> knowledgeBranches.Count != 0)
                {
                    throw new FlashChoiceMismatchException(
                        $"知识恶魔选择 {knowledgeBits:X} 不是单个合法选项（{knowledgeBranches.Count} 项）。");
                }
                List<PlanCardChoice> nextKnowledge = new((action.TurnStartChoices ?? []).Count + 1);
                nextKnowledge.AddRange(action.TurnStartChoices ?? []);
                nextKnowledge.Add(knowledgeBranches[System.Numerics.BitOperations.TrailingZeroCount(knowledgeBits)]);
                action = action with { TurnStartChoices = nextKnowledge };
                child.ReleaseSimulator();
                continue;
            }
            TurnStartChoiceRequest request = combat.PendingTurnStartChoice
                ?? throw new FlashChoiceMismatchException("旧引擎的待定选择不是卡牌选择。");
            CardChoiceSpec spec = TurnStartChoiceSupport.BuildPendingSpec(
                (CombatPredictionSimulator)child.Simulator, combat, _player);
            if (taken == 0)
            {
                primary = action.Kind == PlanActionKind.PlayCard && string.IsNullOrEmpty(request.SourceId)
                    ? BuildPrimaryChoiceMatch(spec)
                    : null;
            }
            if (taken >= selections.Count)
            {
                throw new FlashChoiceMismatchException(
                    $"旧引擎请求第 {taken + 1} 次选择（{request.SourceId}/{request.Effect}/{request.SourcePile}），Flash 没有做。");
            }
            inspect?.Invoke(taken, request, spec);
            ulong bits = selections[taken++];
            if (spec.Options.Count < 64 && bits >> spec.Options.Count != 0)
                throw new FlashChoiceMismatchException($"选择 {bits:X} 超出旧引擎的 {spec.Options.Count} 个选项。");
            List<int> indexes = [];
            for (int index = 0; index < spec.Options.Count; index++)
            {
                if ((bits & (1UL << index)) != 0)
                    indexes.Add(index);
            }
            PlanCardChoice branch = CardChoiceSupport.BuildSelectedChoiceForFlash(spec, indexes, displayNames);
            bool turnResolution = action.Kind == PlanActionKind.EndTurn
                || child.Turn > parent.Turn
                || request.Timing is PlanChoiceTiming.PlayerTurnEnd
                    or PlanChoiceTiming.EnemyTurn or PlanChoiceTiming.PlayerTurnStart;
            action = AppendPendingChoice(action, request, branch, turnResolution, primary);
            child.ReleaseSimulator();
        }
    }

    /// <summary>
    /// Builds the <see cref="PlanAction.Choice"/> of a potion whose existing-engine effect asks a card
    /// selection (generation potions, DropletOfPrecognition, GamblersBrew, …) from a probe replay.
    /// <c>PotionExecutionSupport.Prepare</c> records generation options in
    /// <c>History.CardGenerationOptions</c> for the card-generation potions;
    /// <see cref="PotionChoiceSupport.GetSpec"/> then builds the same spec the existing search branches
    /// on (GenerateToHand for generation potions, MoveToHand/Draw for Droplet, …), and
    /// <see cref="PotionChoiceSupport.Apply"/> resolves it during the real replay. Returns false only
    /// when the potion does not ask for a choice, leaving <paramref name="action"/> untouched.
    /// </summary>
    private bool TryBuildPotionChoice(PlanAction action, SimulationSnapshot parent, ulong bits,
        out CardChoiceSpec spec, out PlanAction resolved)
    {
        spec = null!;
        resolved = action;
        int slot = action.PotionSlot;
        if (slot < 0)
            return false;
        SimulatedCombatState combat = (SimulatedCombatState)parent.Simulator.State.CombatState;
        MegaCrit.Sts2.Core.Models.PotionModel? potion = combat.GetPotionAtSlot(_player, slot);
        if (potion == null || !PotionChoiceSupport.RequiresChoice(potion))
            return false;
        SimulationSnapshot probe = ReplayForFlash([action], parent);
        try
        {
            if (probe.BoundaryReason != SearchBoundaryReason.None)
                throw new FlashChoiceMismatchException(
                    $"药水 {action.PotionId} 的选择探测停在了 {probe.BoundaryReason}。");
            spec = PotionChoiceSupport.GetSpec((CombatPredictionSimulator)probe.Simulator, potion);
        }
        finally
        {
            probe.ReleaseSimulator();
        }
        if (spec.Options.Count < 64 && bits >> spec.Options.Count != 0)
            throw new FlashChoiceMismatchException($"选择 {bits:X} 超出旧引擎的 {spec.Options.Count} 个选项。");
        List<int> indexes = [];
        for (int index = 0; index < spec.Options.Count; index++)
        {
            if ((bits & (1UL << index)) != 0)
                indexes.Add(index);
        }
        resolved = action with
        {
            Choice = CardChoiceSupport.BuildSelectedChoiceForFlash(spec, indexes, displayNames),
        };
        return true;
    }

    /// <summary>
    /// The existing potion counters at a Flash search root. A fixed prefix may already have spent potions;
    /// the Flash scorer and bound add these frozen totals so their per-state counters equal the existing
    /// snapshot's cumulative values instead of restarting at the prefix end.
    /// </summary>
    internal static FlashPotionUsageForBridge CapturePotionUsageForFlash(SimulationSnapshot snapshot)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        int automatic = 0;
        foreach (PredictedPotionUse use in combat.PotionUses)
        {
            if (use.Automatic)
                automatic = checked(automatic + 1);
        }
        return new FlashPotionUsageForBridge(
            snapshot.PotionUseCount,
            checked(snapshot.PotionUseCount - automatic),
            snapshot.PotionStrategicCost,
            snapshot.ExplicitPotionStrategicCost);
    }

    /// <summary>
    /// The existing primary member's incumbent settings, frozen for the Flash bound. The Flash bridge uses the
    /// same values so its lower bound and potion-cost gate are evaluated with the existing search's semantics.
    /// </summary>
    internal FlashPruningRequest CapturePruningRequestForFlash()
        => new(_potionPolicy, _forceAllPotionsDisabled, _maximumPotionUses, _minimumPotionUses,
            _strategicBossHpRelief);

    /// <summary>
    /// <c>RemainingHealingPotential</c> as the primary member applies it, or null when the existing solver
    /// passes no remaining-healing function. K5 compares the Flash port against this.
    /// </summary>
    internal int? PrimaryIncumbentRemainingHealingPotential(SimulationSnapshot snapshot)
        => root.CanCertifyRemainingHealing || root.UsesKnownNativeHealingPolicy
            ? RemainingHealingPotential(snapshot)
            : null;

    /// <summary>The exact <c>futureHealPotential</c> argument <c>ApplyPrimaryIncumbentBoundCore</c> uses.</summary>
    internal int PrimaryIncumbentFutureHealPotential(SimulationSnapshot snapshot)
    {
        int baseline = PrimaryIncumbentRemainingHealingPotential(snapshot) ?? int.MaxValue;
        return root.UsesComponentHealingCertificate && policy.RelicTargets.Count == 0
            ? baseline
            : Math.Min(baseline, snapshot.FutureHealPotential);
    }

    internal int PrimaryIncumbentHpLowerBound(SimulationSnapshot snapshot)
        => StrategicHpLowerBound(snapshot, _strategicBossHpRelief,
            PrimaryIncumbentFutureHealPotential(snapshot));

    /// <summary>
    /// The healing input <c>ApplySmartPotionEligibilityBound</c> reads. It differs from the incumbent
    /// bound when relic targets disable <c>PruneEqualHp</c>: the ceiling always uses
    /// <c>RemainingHealingPotential</c>, not its minimum with <c>FutureHealPotential</c>.
    /// </summary>
    internal int PrimarySmartPotionRemainingHealingPotential(SimulationSnapshot snapshot)
        => RemainingHealingPotential(snapshot);

    internal int PrimarySmartPotionHpLowerBound(SimulationSnapshot snapshot)
        => StrategicHpLowerBound(snapshot, _strategicBossHpRelief,
            RemainingHealingPotential(snapshot));

    /// <summary>
    /// The intermediate Beam rank of one detached snapshot for the Flash differential probe. Read-only:
    /// the node only carries values the solver already has, and no runtime state moves.
    /// </summary>
    internal double BeamRankForFlash(SimulationSnapshot snapshot, int actionCount, int potionCount,
        bool terminal, bool isEndTurn)
    {
        PlanAction action = new(
            isEndTurn ? PlanActionKind.EndTurn : PlanActionKind.PlayCard,
            snapshot.Turn,
            EndsPlayerTurn: isEndTurn);
        SearchNode node = new(action, actionCount, potionCount, 0, snapshot.Turn, default, 0,
            snapshot.Score, default, false, SearchBoundaryReason.None, terminal, null, snapshot, default!);
        return Retention.BeamRankScore(node);
    }

    /// <summary>
    /// Sets the four root-relative Beam rank baselines the solver's Solve phase normally freezes.
    /// The differential probe compares ranks before running a solve; this only copies root values.
    /// </summary>
    internal void SetFlashRankBaseline(SimulationSnapshot rootSnapshot)
    {
        _run.InitialPersistentBuffValue = rootSnapshot.PersistentBuffValue;
        _run.InitialEnemyStrengthSuppression = rootSnapshot.EnemyStrengthSuppression;
        _run.InitialEnemyWeakTurns = rootSnapshot.EnemyWeakTurns;
        _run.InitialRetainedAttackValue = rootSnapshot.RetainedAttackValue;
    }

    /// <summary>
    /// The turn-setup solve freezes these four baselines at zero (the pre-setup Start snapshot's
    /// values are about to be replaced by the setup page). The differential probe uses the same.
    /// </summary>
    internal void SetFlashRankBaselineForTurnSetup()
    {
        _run.InitialPersistentBuffValue = 0;
        _run.InitialEnemyStrengthSuppression = 0;
        _run.InitialEnemyWeakTurns = 0;
        _run.InitialRetainedAttackValue = 0;
    }

    internal double ContextualAdjustmentForFlash(SimulationSnapshot snapshot, bool terminal)
    {
        PlanAction action = new(PlanActionKind.EndTurn, snapshot.Turn, EndsPlayerTurn: true);
        SearchNode node = new(action, 0, 0, 0, snapshot.Turn, default, 0,
            snapshot.Score, default, false, SearchBoundaryReason.None, terminal, null, snapshot, default!);
        return _profile.ContextualRanking?.Adjustment(node) ?? 0;
    }
}

/// <summary>The cumulative potion counters of an existing-engine snapshot, frozen for a Flash search root.</summary>
internal readonly record struct FlashPotionUsageForBridge(
    int PotionUseCount,
    int ExplicitPotionUseCount,
    int PotionStrategicCost,
    int ExplicitPotionStrategicCost);

/// <summary>The existing primary member's incumbent policy, frozen for the Flash bound.</summary>
internal readonly record struct FlashPruningRequest(
    SolverPotionPolicy EffectivePotionPolicy,
    bool ForceAllPotionsDisabled,
    int? MaximumPotionUses,
    int MinimumPotionUses,
    BossHpRelief StrategicBossHpRelief);
