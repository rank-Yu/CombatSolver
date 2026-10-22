using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Flash.Bridge;
using CombatSolver.Flash.Kernel;
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

    public void Check(SimulationSnapshot snapshot, in FlashBattle battle, string label, bool isRoot)
    {
        if (!snapshot.HasSimulator || snapshot.BoundaryReason != SearchBoundaryReason.None
            || !_checked.Add(snapshot.StateKey))
        {
            return;
        }
        Checked++;
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
