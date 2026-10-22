using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

/// <summary>
/// Repeatable contract for the removed <c>FlashSearchMode.Shadow</c> value (run with
/// <c>--check-flash-mode-compat</c> or <c>flash_regression.py compat</c>):
/// <list type="bullet">
/// <item>settings files and checkpoint/issue-bundle policy JSON that recorded <c>"Shadow"</c> or any
/// numeric enum form load as <see cref="FlashSearchMode.Off"/> without a deserialization error;</item>
/// <item>an enabled mode still serializes as the string <c>"On"</c>, and round-trips as On.</item>
/// </list>
/// The checks run before the game bootstrap and touch no file, so they are safe to repeat.
/// </summary>
internal static class FlashModeCompatChecks
{
    public static int Run()
    {
        int assertions = 0;
        void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
            assertions++;
        }

        // The settings file reader/writer.
        foreach ((string label, string json) in new[]
        {
            ("shadow-string", "{\"flashSearchMode\":\"Shadow\"}"),
            ("shadow-lowercase", "{\"flashSearchMode\":\"shadow\"}"),
            ("numeric-shadow", "{\"flashSearchMode\":1}"),
            ("numeric-off", "{\"flashSearchMode\":0}"),
            ("numeric-on", "{\"flashSearchMode\":2}"),
            ("numeric-unknown", "{\"flashSearchMode\":7}"),
            ("unknown-string", "{\"flashSearchMode\":\"FutureMode\"}"),
            ("missing", "{}"),
        })
        {
            SolverSettingsData data = SolverSettings.DeserializeForTesting(json);
            Require(data.FlashSearchMode == FlashSearchMode.Off,
                $"settings {label}: read {data.FlashSearchMode}, expected Off");
        }

        SolverSettingsData on = new() { FlashSearchMode = FlashSearchMode.On };
        string onJson = SolverSettings.SerializeForTesting(on);
        Require(onJson.Contains("\"flashSearchMode\": \"On\"", StringComparison.Ordinal),
            $"settings On serialized as {onJson}");
        Require(SolverSettings.DeserializeForTesting(onJson).FlashSearchMode == FlashSearchMode.On,
            "settings On did not round-trip");

        SolverSettingsData off = new() { FlashSearchMode = FlashSearchMode.Off };
        Require(SolverSettings.SerializeForTesting(off).Contains(
                "\"flashSearchMode\": \"Off\"", StringComparison.Ordinal),
            "settings Off serialized in an unexpected form");

        // Checkpoints and issue bundles persist policy with the unattended-test serializer, which has its
        // own options; the same legacy values must read as Off there.
        foreach ((string label, string json) in new[]
        {
            ("checkpoint-shadow", "{\"flashSearchMode\":\"Shadow\"}"),
            ("checkpoint-numeric-shadow", "{\"flashSearchMode\":1}"),
            ("checkpoint-numeric-unknown", "{\"flashSearchMode\":99}"),
        })
        {
            SolverSettingsData data = JsonSerializer.Deserialize<SolverSettingsData>(
                json, UnattendedTestFiles.JsonOptions)!;
            Require(data.FlashSearchMode == FlashSearchMode.Off,
                $"checkpoint {label}: read {data.FlashSearchMode}, expected Off");
        }
        string checkpointOn = JsonSerializer.Serialize(on, UnattendedTestFiles.JsonOptions);
        Require(checkpointOn.Contains("\"flashSearchMode\": \"On\"", StringComparison.Ordinal),
            $"checkpoint On serialized as {checkpointOn}");
        Require(JsonSerializer.Deserialize<SolverSettingsData>(checkpointOn,
                UnattendedTestFiles.JsonOptions)!.FlashSearchMode == FlashSearchMode.On,
            "checkpoint On did not round-trip");

        // Recorded test requests carry a nullable mode. Legacy values must read as Off, and On must
        // round-trip, through the same unattended-test serializer used by checkpoints.
        foreach ((string label, string json) in new[]
        {
            ("request-shadow", "{\"flashSearchModeForTest\":\"Shadow\"}"),
            ("request-numeric-shadow", "{\"flashSearchModeForTest\":1}"),
            ("request-numeric-unknown", "{\"flashSearchModeForTest\":5}"),
        })
        {
            UnattendedTestRequest request = JsonSerializer.Deserialize<UnattendedTestRequest>(
                json, UnattendedTestFiles.JsonOptions)!;
            Require(request.FlashSearchModeForTest == FlashSearchMode.Off,
                $"request {label}: read {request.FlashSearchModeForTest}, expected Off");
        }
        UnattendedTestRequest onRequest = JsonSerializer.Deserialize<UnattendedTestRequest>(
            "{\"flashSearchModeForTest\":\"On\"}", UnattendedTestFiles.JsonOptions)!;
        Require(onRequest.FlashSearchModeForTest == FlashSearchMode.On,
            "request On did not read as On");
        Require(JsonSerializer.Serialize(onRequest, UnattendedTestFiles.JsonOptions)
                .Contains("\"flashSearchModeForTest\": \"On\"", StringComparison.Ordinal),
            "request On did not serialize as the string On");

        // The removed value must not be accepted as a live mode by the dispatch enum.
        Require(!Enum.IsDefined((FlashSearchMode)1),
            "numeric value 1 is still a defined FlashSearchMode");
        Require((int)FlashSearchMode.On == 2,
            $"On changed its persisted numeric value to {(int)FlashSearchMode.On}");

        Console.WriteLine($"FLASH_MODE_COMPAT status=Passed assertions={assertions}");
        return 0;
    }
}
