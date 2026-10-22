using System.Text.Json;
using CombatSolver;
using CombatSolver.Flash.Bridge;

namespace OfflineSearchHarness;

/// <summary>
/// Fallback contract for the coordinator entry. The fixture root carries content Flash has no
/// definition for (SOULBOUND_POWER) and expands well past the regular beam width, so re-running with
/// the Flash budget instead of the saved regular profile would be visible. Off mode is the baseline;
/// On mode must discard the whole Flash attempt, log exactly one <c>FLASH_FALLBACK</c>, and rerun with
/// the regular profile. A regular solve forced onto the Flash profile must produce different work.
/// A direct On request with an unregistered purpose must still fail without a fallback.
/// </summary>
internal static class FlashFallbackChecks
{
    public static void Run(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot basePolicy,
        string outputDirectory)
    {
        List<string> offLines = [];
        List<string> onLines = [];
        List<string> sensitivityLines = [];
        List<FlashFallbackNotice> offNotices = [];
        List<FlashFallbackNotice> onNotices = [];
        List<FlashFallbackNotice> sensitivityNotices = [];

        SolverSearchProfile regularProfile = FlashFallbackFailure.RequireRegularProfile(basePolicy);
        SearchPolicySnapshot offPolicy = Policy(basePolicy with
        {
            FlashMode = FlashSearchMode.Off,
            Profile = regularProfile,
        }, offLines, offNotices);
        SearchPolicySnapshot onPolicy = Policy(basePolicy, onLines, onNotices);
        // Same regular mode and budget, but the Flash profile. This is the sensitivity control that
        // proves the fixture can distinguish the two profile sources.
        SearchPolicySnapshot sensitivityPolicy = Policy(basePolicy with
        {
            FlashMode = FlashSearchMode.Off,
            Profile = basePolicy.Profile,
        }, sensitivityLines, sensitivityNotices);

        SolverResult off = CombatSearchCoordinator.Solve(
            root, names, damage, offPolicy, CancellationToken.None, null);
        SolverResult on = CombatSearchCoordinator.Solve(
            root, names, damage, onPolicy, CancellationToken.None, null);
        SolverResult sensitivity = CombatSearchCoordinator.Solve(
            root, names, damage, sensitivityPolicy, CancellationToken.None, null);

        string[] offRoute = DescribeRoute(off);
        string[] onRoute = DescribeRoute(on);
        string offQuality = Quality(root, offPolicy, off);
        string onQuality = Quality(root, onPolicy, on);
        string[] fallbackLines = onLines
            .Where(line => line.Contains("FLASH_FALLBACK", StringComparison.Ordinal))
            .ToArray();
        bool fallbackFailed = onLines.Any(line =>
            line.Contains("FLASH_SEARCH_FAILED", StringComparison.Ordinal));
        bool sensitivityDiffers = off.ExpandedNodes != sensitivity.ExpandedNodes
                                  || off.TransitionCount != sensitivity.TransitionCount;

        Check(offRoute.SequenceEqual(onRoute, StringComparer.Ordinal),
            "off/on fallback routes differ");
        Check(offQuality == onQuality, "off/on fallback quality differs");
        Check(off.ExpandedNodes == on.ExpandedNodes && off.TransitionCount == on.TransitionCount,
            "off/on fallback work differs");
        Check(off.FlashExpandedNodes == null && on.FlashExpandedNodes == null,
            "fallback result must not carry Flash work metrics");
        Check(fallbackLines.Length == 1,
            $"On fallback logged {fallbackLines.Length} FLASH_FALLBACK lines, expected 1");
        Check(fallbackLines[0].Contains(
                "reason=admission-unsupported-content detail=SOULBOUND_POWER purpose=PrimaryBeam",
                StringComparison.Ordinal),
            "On fallback line has an unexpected shape");
        Check(!fallbackFailed, "On fallback logged FLASH_SEARCH_FAILED");
        Check(offNotices.Count == 0 && sensitivityNotices.Count == 0,
            "Off mode published a Flash fallback notice");
        Check(onNotices.Count == 1
              && onNotices[0].Reason == "admission-unsupported-content"
              && onNotices[0].Detail == "SOULBOUND_POWER",
            "On fallback did not publish the typed diagnostics notice");
        Check(sensitivityDiffers,
            "Flash-profile regular solve did not change work; fixture cannot separate the budgets");

        List<string> rejectionLines = [];
        List<FlashFallbackNotice> rejectionNotices = [];
        SearchPolicySnapshot rejectionPolicy = Policy(basePolicy, rejectionLines, rejectionNotices);
        string rejectionToken = "";
        try
        {
            BeamSolveDispatch.Solve(new BeamSolveRequest(
                root, names, damage, rejectionPolicy, CancellationToken.None, null,
                rejectionPolicy.Profile, null, null, (DirectSearchPurpose)(-1)));
            throw new InvalidOperationException("unregistered direct purpose did not fail in On mode");
        }
        catch (FlashSearchFailedException failure)
        {
            rejectionToken = failure.ReasonToken;
        }
        Check(rejectionToken == "admission-unsupported-request",
            $"unregistered direct purpose failed with {rejectionToken}");
        Check(rejectionLines.Any(line =>
                line.Contains("FLASH_SEARCH_FAILED reason=admission-unsupported-request",
                    StringComparison.Ordinal)),
            "non-admission rejection did not log FLASH_SEARCH_FAILED");
        Check(!rejectionLines.Any(line =>
                line.Contains("FLASH_FALLBACK", StringComparison.Ordinal)),
            "non-admission rejection logged FLASH_FALLBACK");
        Check(rejectionNotices.Count == 0,
            "non-admission rejection published a Flash fallback notice");

        // A fallback after anything was already published must keep the failure contract instead of
        // rerunning. Use a pre-existing route adoption seed to exercise the guard without needing a
        // second fallback-capable root.
        SearchInteractionState publishedInteraction = new();
        Check(publishedInteraction.RequestAdoptRoute(new SolverRouteAdoptionSeed(
                1, [], () => throw new InvalidOperationException(
                    "pre-published adoption seed must not be materialized"))),
            "could not set up the published-result guard");
        List<string> publishedLines = [];
        List<FlashFallbackNotice> publishedNotices = [];
        SearchPolicySnapshot publishedPolicy = Policy(
            basePolicy with { Interaction = publishedInteraction }, publishedLines, publishedNotices);
        string publishedToken = "";
        try
        {
            CombatSearchCoordinator.Solve(
                root, names, damage, publishedPolicy, CancellationToken.None, null);
            throw new InvalidOperationException("published fallback unexpectedly reran");
        }
        catch (FlashSearchFailedException failure)
        {
            publishedToken = failure.ReasonToken;
        }
        Check(publishedToken == "admission-unsupported-content",
            $"published fallback failed with {publishedToken}");
        Check(publishedLines.Any(line =>
                line.Contains("FLASH_SEARCH_FAILED reason=admission-unsupported-content",
                    StringComparison.Ordinal)),
            "published fallback did not log FLASH_SEARCH_FAILED");
        Check(!publishedLines.Any(line =>
                line.Contains("FLASH_FALLBACK", StringComparison.Ordinal)),
            "published fallback logged FLASH_FALLBACK and reran");
        Check(publishedNotices.Count == 0,
            "published fallback published a Flash fallback notice");

        var payload = new
        {
            status = "Passed",
            scenario = "FLASH-FALLBACK-SOULBOUND-POWER-NONTRIVIAL",
            regularProfile = new
            {
                regularProfile.BeamWidth,
                regularProfile.MaxExpandedNodes,
                regularProfile.SoftTimeBudgetMilliseconds,
            },
            flashProfile = new
            {
                basePolicy.Profile.BeamWidth,
                basePolicy.Profile.MaxExpandedNodes,
                basePolicy.Profile.SoftTimeBudgetMilliseconds,
            },
            routeSame = true,
            qualitySame = true,
            workSame = true,
            offWork = new { off.ExpandedNodes, off.TransitionCount },
            onWork = new { on.ExpandedNodes, on.TransitionCount },
            sensitivityWork = new { sensitivity.ExpandedNodes, sensitivity.TransitionCount },
            sensitivityDiffers,
            publishedFallbackBlocked = true,
            publishedFailureToken = publishedToken,
            fallbackLines,
            fallbackNoticeCount = onNotices.Count,
            fallbackNotices = onNotices.Select(notice => new
            {
                notice.Reason,
                notice.Detail,
                notice.Purpose,
            }).ToArray(),
            rejectionToken,
            rejectionLine = rejectionLines.FirstOrDefault(line =>
                line.Contains("FLASH_SEARCH_FAILED", StringComparison.Ordinal)),
        };
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Combine(outputDirectory, "flash-fallback-checks.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            "FLASH_FALLBACK_CHECKS status=Passed route=Same quality=Same work=Same "
            + $"fallback_lines={fallbackLines.Length} "
            + $"off_work={off.ExpandedNodes}/{off.TransitionCount} "
            + $"flash_profile_work={sensitivity.ExpandedNodes}/{sensitivity.TransitionCount} "
            + $"sensitivity={sensitivityDiffers} rejection={rejectionToken}");
    }

    private static SearchPolicySnapshot Policy(
        SearchPolicySnapshot policy,
        List<string> lines,
        List<FlashFallbackNotice> notices)
        => policy with
        {
            // Keep the check lines in the JSON evidence only. The normal On search after this probe
            // writes the production FLASH_FALLBACK line to the host log, so the log scan cannot mix in
            // the deliberate non-admission FLASH_SEARCH_FAILED from this contract.
            Diagnostics = new SearchDiagnosticsSink(
                lines.Add, _ => { }, flashFallback: notice => notices.Add(notice)),
        };

    private static string[] DescribeRoute(SolverResult result)
        => result.BestNode.Actions
            .Select(action => $"{action.Turn}:{action.Kind}:{action.CardId ?? action.PotionId ?? "-"}"
                + $":target={action.TargetCombatId?.ToString() ?? "-"}:key={action.CardStateKey}")
            .ToArray();

    private static string Quality(
        CombatRootSnapshot root, SearchPolicySnapshot policy, SolverResult result)
        => JsonSerializer.Serialize(
            CombatSearchCoordinator.CapturePortfolioQuality(root, policy, result),
            UnattendedTestFiles.JsonOptions);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
