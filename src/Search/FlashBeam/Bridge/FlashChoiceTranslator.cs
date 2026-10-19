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
        Action<int, TurnStartChoiceRequest, CardChoiceSpec>? inspect = null,
        Action<int, KnowledgeDemonChoiceRequest>? inspectKnowledge = null)
    {
        planned = FlashActionTranslator.ToPlanAction(parent, player, action);
        ulong[] selections = new ulong[action.ChoiceCount];
        for (int index = 0; index < selections.Length; index++)
            selections[index] = action.Choices[index];
        return replay.ReplayWithChoicesForFlash(ref planned, parent, selections, inspect, inspectKnowledge);
    }

    /// <summary>
    /// Differences between a Flash fixed-option request and the existing engine's knowledge choice.
    /// The old request carries option card ids; the Flash request carries their values in
    /// <see cref="FlashChoiceRequest.GeneratedOptions"/>.
    /// </summary>
    public static List<string> CompareKnowledgeRequest(in FlashBattle partial, in FlashChoiceRequest flash,
        KnowledgeDemonChoiceRequest request)
    {
        List<string> differences = [];
        if (flash.Kind != FlashChoiceKind.SelectOption)
            differences.Add($"种类 old=SelectOption flash={flash.Kind}");
        if (flash.Effect.ToString() != nameof(PlanChoiceEffect.ApplyKnowledgeCurse))
            differences.Add($"效果 old=ApplyKnowledgeCurse flash={flash.Effect}");
        if (flash.Timing.ToString() != nameof(PlanChoiceTiming.EnemyTurn))
            differences.Add($"时机 old=EnemyTurn flash={flash.Timing}");
        if (flash.MinCount != 1 || flash.MaxCount != 1)
            differences.Add($"数量 old=1..1 flash={flash.MinCount}..{flash.MaxCount}");
        if (!flash.OptionsAreGenerated || !flash.SourceIsNone)
            differences.Add("选项来源 old=None/Generated flash=不是");
        if (flash.OptionCount != request.OptionIds.Count)
            differences.Add($"选项数 old={request.OptionIds.Count} flash={flash.OptionCount}");
        int shared = Math.Min(flash.OptionCount, request.OptionIds.Count);
        for (int index = 0; index < shared; index++)
        {
            FlashCard card = flash.GeneratedOptions[index];
            string flashId = FlashRegistry.Card(card.Id).NativeId;
            if (flashId != request.OptionIds[index] || card.Upgrade != 0)
                differences.Add($"选项[{index}] old={request.OptionIds[index]}+0 flash={flashId}+{card.Upgrade}");
        }
        return differences;
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
        string flashSource = flash.SourceIsNone ? "None" : flash.Source.ToString();
        if (flashSource != spec.SourcePile.ToString())
            differences.Add($"来源牌堆 old={spec.SourcePile} flash={flashSource}");
        if (flash.Timing.ToString() != request.Timing.ToString())
            differences.Add($"时机 old={request.Timing} flash={flash.Timing}");
        if (flash.MinCount != minTake || flash.MaxCount != maxTake)
            differences.Add($"数量 old={minTake}..{maxTake} flash={flash.MinCount}..{flash.MaxCount}");
        if (flash.OptionCount != spec.Options.Count)
            differences.Add($"选项数 old={spec.Options.Count} flash={flash.OptionCount}");
        int shared = Math.Min(flash.OptionCount, spec.Options.Count);
        for (int index = 0; index < shared; index++)
        {
            FlashCard card = flash.OptionsAreGenerated
                ? flash.GeneratedOptions[index]
                : partial.Cards[flash.Options[index]];
            string flashId = FlashRegistry.Card(card.Id).NativeId;
            var option = spec.Options[index].Preview;
            if (flashId != option.Id.Entry || card.Upgrade != option.CurrentUpgradeLevel)
                differences.Add($"选项[{index}] old={option.Id.Entry}+{option.CurrentUpgradeLevel} flash={flashId}+{card.Upgrade}");
        }
        return differences;
    }
}
