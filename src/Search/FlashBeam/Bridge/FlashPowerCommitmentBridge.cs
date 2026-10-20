using CombatSolver.Engine.InCombat.Flash;

namespace CombatSolver.FlashBeam;

/// <summary>
/// Compiles the existing per-card power-commitment descriptors into the FlashBeam route-metadata
/// tables. The existing solver gates the lane on the root holding at least one registered power card
/// (<c>the existing search's _hasRegisteredPowerCards</c>); the same gate is frozen here.
/// </summary>
internal static class FlashPowerCommitmentBridge
{
    /// <summary>The commitment's played-card set is an exact two-word mask, matching the bridge catalog.</summary>
    internal const int MaximumRegisteredCards = 128;

    public static FlashPowerCommitmentCatalog? Compile(CombatRootSnapshot root)
    {
        if (!root.PlayerCardIds.Any(PowerCardValuationModels.Registry.ContainsCardId))
            return null;
        FlashPowerCommitmentRule[] rules = new FlashPowerCommitmentRule[FlashRegistry.CardCount];
        int index = 0;
        for (ushort cardId = 0; cardId < rules.Length; cardId++)
        {
            if (!PowerCardValuationModels.Registry.TryGetCommitmentDescriptor(
                    FlashRegistry.Card(cardId).NativeId, out PowerCommitmentDescriptor descriptor))
            {
                continue;
            }
            if (index >= MaximumRegisteredCards)
            {
                throw new FlashImportException(FlashRejection.UnsupportedRequest,
                    $"能力承诺登记卡数超过 {MaximumRegisteredCards}：{FlashRegistry.Card(cardId).NativeId}");
            }
            rules[cardId] = new FlashPowerCommitmentRule(
                descriptor.Family, descriptor.Priority, descriptor.Admission, index++);
        }
        return index == 0 ? null : new FlashPowerCommitmentCatalog(rules, root.TotalFloor);
    }
}
