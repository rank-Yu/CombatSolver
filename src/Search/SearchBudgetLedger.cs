using System.Diagnostics;

namespace CombatSolver;

internal readonly record struct SearchBudgetWindow(int RemainingMilliseconds, long RemainingNodes)
{
    internal bool CanStart(int minimumMilliseconds)
        => RemainingMilliseconds > minimumMilliseconds && RemainingNodes > 0;

    internal SolverSearchProfile Limit(
        SolverSearchProfile profile,
        int maximumNodes,
        int maximumMilliseconds,
        int reserveMilliseconds)
        => profile with
        {
            MaxExpandedNodes = (int)Math.Min(maximumNodes, RemainingNodes),
            SoftTimeBudgetMilliseconds = Math.Min(
                maximumMilliseconds, RemainingMilliseconds - reserveMilliseconds),
        };
}

internal sealed class SearchBudgetLedger
{
    private readonly Stopwatch _requestClock;
    private readonly int _requestLimitMilliseconds;

    internal SearchBudgetLedger(Stopwatch requestClock, SearchPolicySnapshot policy)
    {
        _requestClock = requestClock;
        _requestLimitMilliseconds = policy.BudgetOverrideMilliseconds
            ?? policy.Profile.SoftTimeBudgetMilliseconds;
    }

    internal SearchRequestWorkTotals WorkTotals { get; } = new();

    internal int RemainingRequestMilliseconds
        => _requestLimitMilliseconds - (int)_requestClock.ElapsedMilliseconds;

    internal long RemainingRequestMillisecondsLong
        => _requestLimitMilliseconds - _requestClock.ElapsedMilliseconds;

    internal long RemainingNodes(SolverSearchProfile profile)
        // Budget unit: expanded nodes. A contribution defaults to its physical ExpandedNodes
        // (see SearchRequestWorkTotals.BudgetExpandedNodes) unless it reports another unit.
        => profile.MaxExpandedNodes - WorkTotals.Snapshot().BudgetExpandedNodes;

    internal SearchBudgetWindow RequestWindow(SolverSearchProfile profile)
    {
        int remainingMilliseconds = RemainingRequestMilliseconds;
        long remainingNodes = RemainingNodes(profile);
        return new(remainingMilliseconds, remainingNodes);
    }

    internal SearchBudgetWindow PassWindow(SolverSearchProfile profile, Stopwatch clock)
    {
        int remainingMilliseconds = RemainingPassMillisecondsForSlice(profile, clock);
        long remainingNodes = RemainingNodes(profile);
        return new(remainingMilliseconds, remainingNodes);
    }

    internal SearchBudgetWindow ProfileWindow(SolverSearchProfile profile)
        => new(profile.SoftTimeBudgetMilliseconds, RemainingNodes(profile));

    internal long RemainingMilliseconds(SolverSearchProfile profile, Stopwatch clock)
        => profile.SoftTimeBudgetMilliseconds - clock.ElapsedMilliseconds;

    internal int RemainingPassMillisecondsForSlice(SolverSearchProfile profile, Stopwatch clock)
        => profile.SoftTimeBudgetMilliseconds - (int)clock.ElapsedMilliseconds;
}
