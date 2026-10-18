using CombatSolver.Engine.InCombat.Flash;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.FlashBeam;

/// <summary>
/// Replays a Flash action, choices included, through the existing engine. Each Flash selection is
/// a bit set over the request's options; the existing engine's pending request supplies the same
/// options in the same order, and <see cref="CombatBeamSolver.ReplayWithChoicesForFlash"/> turns the
/// selection into plan tokens and places it as the existing search would.
/// </summary>
internal static class FlashChoiceTranslator
{
    public static SimulationSnapshot Replay(CombatBeamSolver replay, SimulationSnapshot parent, Player player,
        in FlashAction action, out PlanAction planned,
        Action<int, TurnStartChoiceRequest, CardChoiceSpec>? inspect = null)
    {
        planned = FlashActionTranslator.ToPlanAction(parent, player, action);
        ulong[] selections = new ulong[action.ChoiceCount];
        for (int index = 0; index < selections.Length; index++)
            selections[index] = action.Choices[index];
        return replay.ReplayWithChoicesForFlash(ref planned, parent, selections, inspect);
    }

    /// <summary>
    /// Differences between a Flash request (options are positions in <paramref name="partial"/>, the
    /// battle at the time of the request) and the existing engine's pending request and spec.
    /// </summary>
    public static List<string> CompareRequest(in FlashBattle partial, in FlashChoiceRequest flash,
        TurnStartChoiceRequest request, CardChoiceSpec spec)
    {
        List<string> differences = [];
        int minTake = Math.Min(spec.MinCount, spec.Options.Count);
        int maxTake = Math.Min(spec.MaxCount, spec.Options.Count);
        if (flash.Effect.ToString() != spec.Effect.ToString())
            differences.Add($"效果 old={spec.Effect} flash={flash.Effect}");
        if (flash.Source.ToString() != spec.SourcePile.ToString())
            differences.Add($"来源牌堆 old={spec.SourcePile} flash={flash.Source}");
        if (flash.Timing.ToString() != request.Timing.ToString())
            differences.Add($"时机 old={request.Timing} flash={flash.Timing}");
        if (flash.MinCount != minTake || flash.MaxCount != maxTake)
            differences.Add($"数量 old={minTake}..{maxTake} flash={flash.MinCount}..{flash.MaxCount}");
        if (flash.OptionCount != spec.Options.Count)
            differences.Add($"选项数 old={spec.Options.Count} flash={flash.OptionCount}");
        int shared = Math.Min(flash.OptionCount, spec.Options.Count);
        for (int index = 0; index < shared; index++)
        {
            FlashCard card = partial.Cards[flash.Options[index]];
            string flashId = FlashRegistry.Card(card.Id).NativeId;
            var option = spec.Options[index].Preview;
            if (flashId != option.Id.Entry || card.Upgrade != option.CurrentUpgradeLevel)
                differences.Add($"选项[{index}] old={option.Id.Entry}+{option.CurrentUpgradeLevel} flash={flashId}+{card.Upgrade}");
        }
        return differences;
    }
}
