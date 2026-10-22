using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Flash.Bridge;
using CombatSolver.Flash.Kernel;
using CombatSolver.Flash.Search;
using MegaCrit.Sts2.Core.Entities.Players;

namespace OfflineSearchHarness;

/// <summary>
/// K1 action-set extension: at one existing-engine state, compare the existing solver's planned
/// action set with Flash's legal action set under the same canonical key
/// (kind, card/potion, target, potion slot, and whether a potion asks a choice). Card choices are
/// attached during expansion and are compared by K1's request list instead. The existing card
/// enumerator deduplicates byte-equivalent playable cards; the canonical set applies the same dedup
/// to Flash so duplicate hand positions cancel out. Potions are compared without the potion policy
/// gate on either side: policy is applied after the legal action set, and
/// <c>FlashKernel.LegalActions</c> already skips non-searchable potions exactly like
/// <c>PotionOnUseSupport.CanSearch</c>.
/// </summary>
internal sealed class FlashActionSetCheck(
    CombatRootSnapshot root,
    SolverDisplayNames names,
    BattleDamageSnapshot damage,
    SearchPolicySnapshot policy,
    Player player,
    FlashRules rules,
    List<string> report)
{
    private readonly OpeningProbe _probe = new(root, names, damage, policy, CancellationToken.None,
        searchProfile: policy.Profile);
    private readonly HashSet<StateFingerprint> _checked = [];

    public int Checked { get; private set; }

    public int Failed { get; private set; }

    public string? FirstFailure { get; private set; }

    /// <summary>
    /// Duplicate-branch check: the first hand position kept for each playable-card state key must be
    /// the same on both sides. This is the observable output of the existing enumerator's
    /// <c>DuplicateCardBranchesPruned</c>; the plain action set below collapses equal canonical keys
    /// and therefore cannot see it.
    /// </summary>
    public int DuplicatePlayableChecked { get; private set; }

    public int DuplicatePlayableFailed { get; private set; }

    public string? FirstDuplicatePlayableFailure { get; private set; }

    public void Check(SimulationSnapshot snapshot, in FlashBattle battle, string label, bool isRoot)
    {
        if (!snapshot.HasSimulator || snapshot.BoundaryReason != SearchBoundaryReason.None
            || !_checked.Add(snapshot.StateKey))
        {
            return;
        }
        Checked++;
        CheckDuplicatePlayable(snapshot, battle, label);
        HybridSet oldSet = [];
        HybridSet flashSet = [];
        CollectExisting(snapshot, isRoot ? 0 : 1, oldSet);
        CollectFlash(snapshot, battle, flashSet);
        List<string> oldOnly = [.. oldSet.Where(key => !flashSet.Contains(key))];
        List<string> flashOnly = [.. flashSet.Where(key => !oldSet.Contains(key))];
        if (oldOnly.Count == 0 && flashOnly.Count == 0)
            return;
        Failed++;
        if (FirstFailure == null)
            FirstFailure = $"{label} existing-only=[{string.Join(",", oldOnly)}] flash-only=[{string.Join(",", flashOnly)}]";
        if (report.Count < 200)
        {
            report.Add($"ACTION-SET MISMATCH {label}");
            if (oldOnly.Count > 0)
                report.Add("  existing-only: " + string.Join(", ", oldOnly));
            if (flashOnly.Count > 0)
                report.Add("  flash-only: " + string.Join(", ", flashOnly));
        }
    }

    private void CheckDuplicatePlayable(
        SimulationSnapshot snapshot, in FlashBattle battle, string label)
    {
        if (!snapshot.HasSimulator)
            return;
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        IReadOnlyList<PredictedCard> hand =
            simulator.State.GetPlayerCombatState(player).Hand.Cards;
        if (hand.Count != battle.HandCount)
            return;
        DuplicatePlayableChecked++;
        List<int> oldKept = [];
        List<StateFingerprint> oldKeys = [];
        HashSet<StateFingerprint> oldSeen = [];
        for (int handIndex = 0; handIndex < hand.Count; handIndex++)
        {
            PredictedCard card = hand[handIndex];
            if (!combat.CanPlayCard(simulator, card))
                continue;
            StateFingerprint key = _probe.EvaluatorForBridge.BuildPlayableCardKey(card);
            if (!oldSeen.Add(key))
                continue;
            oldKept.Add(handIndex);
            oldKeys.Add(key);
        }

        List<int> flashKept = [];
        List<ulong> flashKeys = [];
        HashSet<ulong> flashSeen = [];
        for (int handIndex = 0; handIndex < battle.HandCount; handIndex++)
        {
            if (!FlashKernel.CanPlayCard(battle, rules, handIndex))
                continue;
            StateFingerprint fingerprint = FlashCardKey.Playable(
                battle, battle.Cards[handIndex], rules);
            ulong hash = Hash(fingerprint);
            if (!flashSeen.Add(hash))
                continue;
            flashKept.Add(handIndex);
            flashKeys.Add(hash);
        }

        bool same = oldKept.Count == flashKept.Count;
        for (int index = 0; same && index < oldKept.Count; index++)
            same = oldKept[index] == flashKept[index];
        // Equal-key groups must be the same on both sides even when the kept positions match by
        // accident; compare the pairwise partition of the kept cards.
        for (int left = 0; same && left < oldKeys.Count; left++)
        {
            for (int right = left + 1; same && right < oldKeys.Count; right++)
            {
                bool oldEqual = oldKeys[left] == oldKeys[right];
                bool flashEqual = flashKeys[left] == flashKeys[right];
                same = oldEqual == flashEqual;
            }
        }
        if (same)
            return;
        DuplicatePlayableFailed++;
        FirstDuplicatePlayableFailure ??=
            $"{label} existing-kept=[{string.Join(",", oldKept)}] flash-kept=[{string.Join(",", flashKept)}]";
        if (report.Count < 200)
        {
            report.Add($"DUPLICATE-PLAYABLE MISMATCH {label}");
            report.Add($"  existing-kept: [{string.Join(",", oldKept)}]");
            report.Add($"  flash-kept: [{string.Join(",", flashKept)}]");
        }
    }

    private static ulong Hash(in StateFingerprint fingerprint)
        => (fingerprint.First * 0x9E3779B97F4A7C15UL) ^ fingerprint.Second;

    private void CollectExisting(SimulationSnapshot snapshot, int actionCount, HybridSet output)
    {
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SearchNode node = new(null, actionCount, snapshot.PotionUseCount, snapshot.PotionStrategicCost,
            snapshot.Turn, SearchRouteTraits.None, 0, snapshot.Score, snapshot.StateKey,
            snapshot.HasRisk, snapshot.BoundaryReason, false, null, snapshot,
            CombatProgressState.Capture(snapshot));
        foreach (PreparedCardAction prepared in SearchExpansionShared.EnumeratePlannedCardActions(
                     _probe.ExpansionContextForBridge, new ExpansionPlan(node, CardNameFirst: false)))
        {
            // Card choices are attached during expansion, not at legal-action enumeration. K1's
            // CompareRequest compares the resolved request list; the action-set key therefore uses
            // only the stable card/target identity. Potion choice-required bits are compared below
            // because a potion with an empty option list still asks a choice.
            output.Add(CardKey(prepared.Action, needsChoice: false));
        }
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead)
            return;
        int potionSlots = combat.PotionSlotCountForFlash(player);
        for (int slot = 0; slot < potionSlots; slot++)
        {
            var potion = combat.GetPotionAtSlot(player, slot);
            if (potion == null || !combat.IsPotionAvailable(player, slot)
                || !PotionOnUseSupport.CanSearch(potion))
                continue;
            bool needsChoice = PotionChoiceSupport.RequiresChoice(potion);
            foreach ((int targetIndex, var target) in
                     SearchProbeHelpers.TargetsForPotion(player, potion, simulator))
            {
                output.Add(PotionKey(potion.Id.Entry, slot, targetIndex, target?.CombatId, needsChoice));
            }
        }
        if (!snapshot.PlayerDead && !snapshot.AllEnemiesDead
            && snapshot.BoundaryReason == SearchBoundaryReason.None)
        {
            output.Add("E");
        }
    }

    private void CollectFlash(SimulationSnapshot snapshot, in FlashBattle battle, HybridSet output)
    {
        Span<FlashAction> actions = stackalloc FlashAction[FlashKernel.MaxLegalActions];
        int count = FlashKernel.LegalActions(battle, rules, actions);
        for (int index = 0; index < count; index++)
        {
            FlashAction action = actions[index];
            PlanAction planned = FlashActionTranslator.ToPlanAction(snapshot, player, action);
            bool needsChoice;
            if (action.Kind == FlashActionKind.EndTurn)
            {
                output.Add("E");
                continue;
            }
            if (action.Kind == FlashActionKind.PlayCard)
            {
                output.Add(CardKey(planned, needsChoice: false));
                continue;
            }
            var oldPotion = ((SimulatedCombatState)snapshot.Simulator.State.CombatState)
                .GetPotionAtSlot(player, action.PotionSlot);
            bool oldRequiresChoice = oldPotion != null && PotionChoiceSupport.RequiresChoice(oldPotion);
            FlashBattle probe = battle;
            needsChoice = oldRequiresChoice
                && FlashKernel.Step(ref probe, action, rules, out _) == FlashStepResult.NeedsChoice;
            output.Add(PotionKey(planned.PotionId, action.PotionSlot, planned.TargetIndex,
                planned.TargetCombatId, needsChoice));
        }
    }

    private static string CardKey(PlanAction action, bool needsChoice)
        => $"C|{action.CardId}|{action.CardUpgradeLevel}|{action.TargetCombatId?.ToString() ?? "-"}" +
            $"|{(needsChoice ? 1 : 0)}";

    private static string PotionKey(string potionId, int slot, int targetIndex, uint? targetCombatId,
        bool needsChoice)
        => $"P|{slot}|{potionId}|{targetCombatId?.ToString() ?? targetIndex.ToString()}" +
            $"|{(needsChoice ? 1 : 0)}";

    private sealed class HybridSet : HashSet<string>;
}
