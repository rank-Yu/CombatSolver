using CombatSolver.Engine.Common;

namespace CombatSolver;

internal static partial class CardChoiceSupport
{
    /// <summary>
    /// The plan choice selecting <paramref name="optionIndexes"/> of <paramref name="spec"/>, with
    /// tokens in the given order and built as <see cref="BuildChoices"/> builds them. The Flash bridge
    /// uses it to answer the existing engine with a selection made by the Flash search.
    /// </summary>
    internal static PlanCardChoice BuildSelectedChoiceForFlash(
        CardChoiceSpec spec,
        IReadOnlyList<int> optionIndexes,
        SolverDisplayNames displayNames)
    {
        int minTake = Math.Min(spec.MinCount, spec.Options.Count);
        int maxTake = Math.Min(spec.MaxCount, spec.Options.Count);
        if (spec.MaxCount < spec.MinCount || optionIndexes.Count < minTake || optionIndexes.Count > maxTake)
        {
            throw new InvalidPlannedChoiceBranchException(
                $"Flash 选择 {optionIndexes.Count} 张，旧引擎要求 {minTake}..{maxTake} 张（{spec.Effect}/{spec.SourcePile}）。");
        }
        List<PredictedCard> selected = new(optionIndexes.Count);
        foreach (int index in optionIndexes)
            selected.Add(spec.Options[index]);
        return new PlanCardChoice(
            spec.Effect,
            spec.SourcePile,
            ToTokens(selected, spec.Options, spec.SourceCards, displayNames.Card),
            ContextId: spec.ContextId);
    }
}
