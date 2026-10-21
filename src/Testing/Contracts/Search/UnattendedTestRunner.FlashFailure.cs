using System.IO.Compression;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using CombatSolver.Flash.Bridge;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// FLASH-SEARCH-FAILED-CONTRACT: run an On-mode search with a development strategy (supported by
    /// Off, explicitly refused by On), then assert the production failure path: the search task faults
    /// with <see cref="FlashSearchFailedException"/>, no result/deployment survives, the issue ledger
    /// and exported bundle carry <c>FLASH_SEARCH_FAILED reason=development-strategy</c>, and the
    /// player-facing message is localized. The contract itself passes; a separate request without the
    /// contract is expected to fail with <c>failureCategory=development-strategy</c>.
    /// </summary>
    private async Task AssertFlashSearchFailedContractAsync(CombatState combat, Player player)
    {
        SetStage("flash_search_failed_contract");
        if (SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null).FlashMode
            != FlashSearchMode.On)
        {
            throw new InvalidOperationException("Flash failure contract requires On mode.");
        }
        if (CurrentDevelopmentStrategy == null)
            throw new InvalidOperationException("Flash failure contract requires a development strategy.");
        long deployedBefore = SolverController.LastDeployedActionStartedAtMillisecondsForTesting;
        var host = NGame.Instance ?? throw new InvalidOperationException("Flash failure contract has no host.");
        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        long deadline = System.Environment.TickCount64 + 30_000;
        while (SolverController.IsSearching)
        {
            if (System.Environment.TickCount64 >= deadline)
                throw new TimeoutException("Flash failure contract search exceeded 30 seconds.");
            await NextFrameAsync();
        }

        FlashSearchFailedException failure =
            SolverController.LastSearchFailureForTesting as FlashSearchFailedException
            ?? throw new InvalidOperationException(
                "Flash failure contract expected FlashSearchFailedException, got "
                + SolverController.LastSearchFailureForTesting?.GetType().Name);
        if (failure.Reason != FlashSearchFailureReason.DevelopmentStrategy
            || failure.ReasonToken != "development-strategy")
        {
            throw new InvalidOperationException(
                $"Flash failure category {failure.Reason}/{failure.ReasonToken}, expected development-strategy.");
        }
        if (!failure.EventLine.StartsWith(
                "FLASH_SEARCH_FAILED reason=development-strategy detail=", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected Flash failure event: {failure.EventLine}");
        }
        if (SolverController.LastCompletedResultForTesting != null)
            throw new InvalidOperationException("A failed Flash search left a completed result behind.");
        if (SolverController.IsDeploying
            || SolverController.LastDeployedActionStartedAtMillisecondsForTesting != deployedBefore)
        {
            throw new InvalidOperationException("A failed Flash search started deployment.");
        }
        if (SolverController.FullAutoEnabled)
            throw new InvalidOperationException("A failed Flash search left full auto enabled.");

        CombatBugReportClassificationSnapshot classification =
            SolverController.CaptureBugReportClassificationForExport();
        if (!classification.Issues.Any(issue =>
                issue.Kind == CombatBugReportIssueKind.FlashSearchFailed
                && issue.Detail?.Contains("reason=development-strategy", StringComparison.Ordinal) == true))
        {
            throw new InvalidOperationException("Issue ledger is missing the Flash failure category.");
        }
        if (!classification.Issues.Any(issue => issue.Kind == CombatBugReportIssueKind.SearchFailure))
            throw new InvalidOperationException("Issue ledger is missing the generic search failure entry.");

        string directory = ProjectSettings.GlobalizePath("user://combat-solver-test-bug-reports");
        string archivePath = await CombatBugReportExporter.ExportCurrentAsync(
            directory, "Flash 失败合同，不上传");
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        AssertBugReportArchive(archive, "current", _request.ExpectedBugReportControlMode);
        using Stream reportStream = archive.GetEntry("report.json")!.Open();
        using JsonDocument report = JsonDocument.Parse(reportStream);
        string reportText = report.RootElement.GetRawText();
        if (!reportText.Contains("FLASH_SEARCH_FAILED reason=development-strategy", StringComparison.Ordinal)
            || !reportText.Contains("\"FlashSearchFailed\"", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Exported bug report is missing the Flash failure classification.");
        }
        string playerMessage = SolverController.FormatSearchFailureForTesting(
            failure, parallelSearchWasEnabled: false);
        if (!playerMessage.Contains("development-strategy", StringComparison.Ordinal)
            || !(playerMessage.Contains("Flash 快速搜索失败", StringComparison.Ordinal)
                || playerMessage.Contains("Flash fast search failed", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Player-facing Flash failure message is missing the category.");
        }
        bool logHasEvent = false;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("diagnostics/logs/", StringComparison.Ordinal)
                || !entry.FullName.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                continue;
            }
            using Stream stream = entry.Open();
            using StreamReader reader = new(stream);
            if ((await reader.ReadToEndAsync()).Contains(
                    "FLASH_SEARCH_FAILED reason=development-strategy", StringComparison.Ordinal))
            {
                logHasEvent = true;
                break;
            }
        }
        if (!logHasEvent)
            throw new InvalidDataException("Exported diagnostic logs are missing FLASH_SEARCH_FAILED.");

        _completedChecks.Add(
            "FlashSearchFailed:reason=development-strategy:StoppedDeployment:IssueBundleVerified");
    }
}
