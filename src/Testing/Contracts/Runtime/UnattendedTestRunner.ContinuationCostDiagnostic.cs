using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// The continuation stamp must expose ordered local cost layers, not only the final
    /// displayed cost. Deployment matches cards by the full cost key, so two cards that
    /// both display 0 but carry [-2,-1] versus [-3,-1] must produce a cost-state
    /// difference before the whole-card text is truncated.
    /// </summary>
    private async Task AssertContinuationCostStateDiagnosticAsync(CombatState combat, Player player)
    {
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player,
            new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        CardModel card = player.PlayerCombatState!.Hand.Cards.Single(candidate =>
            candidate.Id.Entry == "STRIKE_IRONCLAD");
        card.EnergyCost.SetThisCombat(0);
        card.EnergyCost.AddUntilPlayed(-1);
        string first = ContinuationStamp.CaptureLive(combat).StateText;
        card.EnergyCost.AfterCardPlayedCleanup();
        card.EnergyCost.AddUntilPlayed(-2);
        string second = ContinuationStamp.CaptureLive(combat).StateText;
        IReadOnlyList<string> differences = new ContinuationStamp(first).DescribeDifferences(
            new ContinuationStamp(second));
        string firstDifference = differences.FirstOrDefault() ?? "none";
        if (!firstDifference.Contains("field=H[0].cost-state", StringComparison.Ordinal)
            || !firstDifference.Contains("-1", StringComparison.Ordinal)
            || !firstDifference.Contains("-2", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Continuation cost-state diagnostic did not expose the ordered layers: {firstDifference}");
        }
        Entry.Logger.Info(
            $"[CombatSolver/Test] CONTINUATION_COST_STATE_DIAGNOSTIC " +
            $"first={firstDifference}");
        _completedChecks.Add($"ContinuationCostStateDiagnostic:OrderedLayers:First={firstDifference}");
    }
}
