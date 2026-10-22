using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// Cross-engine state identity: the 128-bit hash of the canonical <c>old:</c>/<c>flash:</c> text
/// projection (the same field dump the differential probe compares) after importing the existing
/// snapshot. Only used by the offline retention diff probe; production diagnostics leave this null.
/// </summary>
internal readonly record struct SearchRetentionStateKey(ulong First, ulong Second)
{
    public override string ToString() => $"{First:x16}{Second:x16}";
}

/// <summary>The two retention boundaries of the existing search loop.</summary>
internal enum SearchRetentionBoundaryKind
{
    /// <summary>One action layer inside a player turn (the existing <c>Prune</c> after expansion).</summary>
    ActionLayer,

    /// <summary>The turn-start frontier after end-turn annotation (the existing turn-boundary <c>Prune</c>).</summary>
    TurnBoundary,
}

/// <summary>Boundary metadata used to align the existing solver and the Flash layer loop.</summary>
internal readonly record struct SearchRetentionBoundary(
    int BoundaryId,
    SearchRetentionBoundaryKind Kind,
    int TurnLayer,
    int PlayDepth,
    int InputCount,
    int Width)
{
    public string Key => $"{Kind}:{TurnLayer}:{PlayDepth}";
}

/// <summary>One retained-pool entry at a boundary. <see cref="Rank"/> is -1 when the entry was added
/// after the ranked selection (resource/cycle/ordered portfolios).</summary>
internal readonly record struct SearchRetentionPoolState(
    int BoundaryId,
    StateFingerprint LocalKey,
    SearchRetentionStateKey? Key,
    double Score,
    int Rank,
    bool Required,
    string? RequiredLabel,
    bool Selected,
    string LastAction,
    int Turn,
    int ActionCount);

/// <summary>One member of the final ordered retained list of a boundary.</summary>
internal readonly record struct SearchRetentionSelected(
    int BoundaryId,
    int Order,
    StateFingerprint LocalKey,
    SearchRetentionStateKey? Key,
    double Score,
    string LastAction,
    int Turn,
    int ActionCount);

/// <summary>A generated/admitted/rejected state. <see cref="Status"/> is the last observed admission
/// status (for example <c>generated</c>, <c>accepted_new_state</c>, <c>rejected_dominated</c>).</summary>
internal readonly record struct SearchRetentionGeneration(
    StateFingerprint LocalKey,
    StateFingerprint? ParentKey,
    SearchRetentionStateKey? Key,
    double Score,
    int Turn,
    int ActionCount,
    string LastAction,
    string Status);

/// <summary>
/// Opt-in retention-boundary observer. The callbacks are synchronous and never outlive the search;
/// <see cref="SimulationSnapshot"/> arguments are borrowed only for the duration of the call.
/// </summary>
internal sealed class SearchRetentionObserver(
    Func<StateFingerprint, SimulationSnapshot, SearchRetentionStateKey?> project,
    Action<SearchRetentionBoundary> boundary,
    Action<SearchRetentionPoolState> poolState,
    Action<SearchRetentionSelected> selected,
    Action<SearchRetentionGeneration> generation)
{
    public SearchRetentionStateKey? Project(StateFingerprint key, SimulationSnapshot snapshot)
        => project(key, snapshot);

    public void Boundary(in SearchRetentionBoundary value) => boundary(value);

    public void PoolState(in SearchRetentionPoolState value) => poolState(value);

    public void Selected(in SearchRetentionSelected value) => selected(value);

    public void Generation(in SearchRetentionGeneration value) => generation(value);
}
