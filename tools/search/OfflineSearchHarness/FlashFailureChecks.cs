using System.Collections.ObjectModel;
using CombatSolver;
using CombatSolver.FlashBeam;
using MegaCrit.Sts2.Core.Combat;

namespace OfflineSearchHarness;

/// <summary>
/// Offline contract for the On-mode no-fallback dispatch (OFFLINE_HARNESS_FLASH_FAILURE_CHECKS=1).
/// Each explicit failure is injected inside the same try block as a real Flash solve, so the production
/// catch/mapping path is exercised; the test-only arm is single-shot and cleared in finally. The same
/// root is also solved in Off mode to show the comparison mode is unaffected.
/// </summary>
internal static class FlashFailureChecks
{
    private sealed class NoopStrategy : IDevelopmentSearchStrategy;

    internal static int Run(CombatState combat, CapturedTurnSetupRoot? turnSetup)
    {
        int assertions = 0;
        void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
            assertions++;
        }

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot basePolicy = SolverController.CaptureSearchPolicy(
            settings, combat, includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat));
        SolverSearchProfile profile = basePolicy.Profile with
        {
            BeamWidth = 8,
            MaxExpandedNodes = 8_000,
            SoftTimeBudgetMilliseconds = 2_000,
        };

        BeamSolveRequest Request(SearchPolicySnapshot policy,
            int? minimumPotionUses = null, int? maximumPotionUses = null,
            SolverPotionPolicy? potionPolicyOverride = null,
            DirectSearchPurpose purpose = DirectSearchPurpose.PrimaryBeam)
            => new(root, names, damage, policy, CancellationToken.None,
                ProgressCallback: null, Profile: profile, PotionPolicyOverride: potionPolicyOverride,
                PrimaryIncumbent: null, Purpose: purpose)
            {
                MinimumPotionUses = minimumPotionUses,
                MaximumPotionUses = maximumPotionUses,
            };

        List<string> Capture(out SearchPolicySnapshot policy)
        {
            List<string> lines = [];
            policy = basePolicy with
            {
                FlashMode = FlashSearchMode.On,
                Diagnostics = new SearchDiagnosticsSink(
                    message => { lock (lines) lines.Add(message); }, static _ => { }),
            };
            return lines;
        }

        void ExpectFailure(string label, FlashFailureInjection injection,
            FlashSearchFailureReason expected, string expectedToken, string detailContains)
        {
            List<string> lines = Capture(out SearchPolicySnapshot policy);
            bool threw = false;
            FlashFailureInjectionForTesting.Arm(injection);
            try
            {
                BeamSolveDispatch.Solve(Request(policy));
            }
            catch (FlashSearchFailedException failure)
            {
                threw = true;
                Require(failure.Reason == expected,
                    $"{label}: reason={failure.Reason}, expected={expected}");
                Require(failure.ReasonToken == expectedToken,
                    $"{label}: token={failure.ReasonToken}, expected={expectedToken}");
                Require(failure.Detail.Contains(detailContains, StringComparison.Ordinal),
                    $"{label}: detail={failure.Detail}");
                Require(failure.EventLine.StartsWith(
                        $"FLASH_SEARCH_FAILED reason={expectedToken} ", StringComparison.Ordinal),
                    $"{label}: event={failure.EventLine}");
            }
            finally
            {
                FlashFailureInjectionForTesting.Reset();
            }
            Require(threw, $"{label}: On mode did not throw FlashSearchFailedException");
            Require(lines.Count(line => line.Contains("FLASH_SEARCH_FAILED", StringComparison.Ordinal)) == 1,
                $"{label}: expected exactly one FLASH_SEARCH_FAILED line, got {lines.Count}");
            Require(lines.Count(line => line.Contains($"reason={expectedToken}", StringComparison.Ordinal)) == 1,
                $"{label}: missing reason={expectedToken} in diagnostics");
            Require(!lines.Any(line => line.Contains("FLASH_LEGACY", StringComparison.Ordinal)),
                $"{label}: On mode wrote FLASH_LEGACY");
            Require(!lines.Any(line => line.Contains("FLASH_NO_ROUTE", StringComparison.Ordinal)),
                $"{label}: explicit failure wrote FLASH_NO_ROUTE");
        }

        ExpectFailure("verify-mismatch", FlashFailureInjection.VerificationMismatch,
            FlashSearchFailureReason.VerifyMismatch, "verify-mismatch", "FLASH_VERIFY_MISMATCH");
        ExpectFailure("kernel-unsupported", FlashFailureInjection.KernelUnsupported,
            FlashSearchFailureReason.KernelUnsupported, "kernel-unsupported", "测试注入");
        ExpectFailure("kernel-capacity", FlashFailureInjection.KernelCapacity,
            FlashSearchFailureReason.KernelCapacity, "kernel-capacity", "Cards");
        ExpectFailure("admission-multiplayer", FlashFailureInjection.AdmissionMultiPlayer,
            FlashSearchFailureReason.AdmissionMultiPlayer, "admission-multiplayer", "MultiPlayer");
        ExpectFailure("potion-policy-mismatch", FlashFailureInjection.PotionPolicyMismatch,
            FlashSearchFailureReason.PotionPolicyMismatch, "potion-policy-mismatch",
            "route-minimum-potions");

        // Every admission code keeps a stable category token, including the explicit content classes.
        foreach (FlashRejection rejection in Enum.GetValues<FlashRejection>())
        {
            if (rejection == FlashRejection.None)
                continue;
            FlashSearchFailureReason reason = FlashSearchFailureReasons.FromRejection(rejection);
            Require(!string.IsNullOrWhiteSpace(reason.Token()),
                $"admission mapping {rejection}: empty token");
        }
        Require(FlashSearchFailureReasons.FromRejection(FlashRejection.MultiPlayer)
            == FlashSearchFailureReason.AdmissionMultiPlayer, "multiplayer mapping");
        Require(FlashSearchFailureReasons.FromRejection(FlashRejection.TestContent)
            == FlashSearchFailureReason.AdmissionTestContent, "mock mapping");
        Require(FlashSearchFailureReasons.FromRejection(FlashRejection.DeprecatedContent)
            == FlashSearchFailureReason.AdmissionDeprecatedContent, "deprecated mapping");
        Require(FlashSearchFailureReasons.FromRejection(FlashRejection.UnsupportedRelic)
            == FlashSearchFailureReason.AdmissionUnsupportedContent, "undefined content mapping");

        {
            List<string> lines = Capture(out SearchPolicySnapshot policy);
            DevelopmentSearchStrategy strategy = new(
                new NoopStrategy(), new ReadOnlyDictionary<string, double>(
                    new Dictionary<string, double>(StringComparer.Ordinal)));
            bool threw = false;
            try
            {
                BeamSolveDispatch.Solve(Request(policy with { DevelopmentStrategy = strategy }));
            }
            catch (FlashSearchFailedException failure)
            {
                threw = true;
                Require(failure.Reason == FlashSearchFailureReason.DevelopmentStrategy,
                    $"development-strategy: reason={failure.Reason}");
                Require(failure.ReasonToken == "development-strategy",
                    $"development-strategy: token={failure.ReasonToken}");
            }
            Require(threw, "development-strategy: On mode did not fail");
            Require(lines.Count(line => line.Contains("FLASH_SEARCH_FAILED", StringComparison.Ordinal)) == 1,
                "development-strategy: expected one FLASH_SEARCH_FAILED");
        }

        {
            Require(turnSetup != null,
                "root-phase: run with flashTurnSetupRootForTest=true to capture the Player/Start root");
            List<string> lines = [];
            SearchPolicySnapshot setupPolicy = turnSetup!.Policy with
            {
                IncludeTurnSetup = false,
                FlashMode = FlashSearchMode.On,
                Diagnostics = new SearchDiagnosticsSink(
                    message => { lock (lines) lines.Add(message); }, static _ => { }),
            };
            bool threw = false;
            try
            {
                BeamSolveDispatch.Solve(new BeamSolveRequest(
                    turnSetup.Root, turnSetup.Names, turnSetup.Damage, setupPolicy,
                    CancellationToken.None, ProgressCallback: null, Profile: profile,
                    PotionPolicyOverride: null, PrimaryIncumbent: null,
                    Purpose: DirectSearchPurpose.PrimaryBeam));
            }
            catch (FlashSearchFailedException failure)
            {
                threw = true;
                Require(failure.Reason == FlashSearchFailureReason.RootPhase,
                    $"root-phase: reason={failure.Reason}");
                Require(failure.Detail.Contains("root-phase:Player:Start", StringComparison.Ordinal),
                    $"root-phase: detail={failure.Detail}");
            }
            Require(threw, "root-phase: On mode did not fail");
            Require(lines.Count(line => line.Contains("FLASH_SEARCH_FAILED", StringComparison.Ordinal)) == 1,
                "root-phase: expected one FLASH_SEARCH_FAILED");
        }

        {
            Require(root.SearchablePotionCount == 0,
                "potion-no-route: the fixture root must not carry searchable potions");
            List<string> lines = Capture(out SearchPolicySnapshot policy);
            bool rejected = false;
            try
            {
                BeamSolveDispatch.Solve(Request(policy,
                    minimumPotionUses: 1, maximumPotionUses: 1,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    purpose: DirectSearchPurpose.RequiredPotionAudit));
            }
            catch (PotionPolicyUnsatisfiedException failure)
            {
                rejected = true;
                Require(failure.Message.Contains("flash-rejected:flash-potion-policy-no-route",
                        StringComparison.Ordinal),
                    $"potion-no-route: message={failure.Message}");
            }
            Require(rejected, "potion-no-route: On mode did not return PotionPolicyUnsatisfiedException");
            Require(lines.Any(line => line.Contains("FLASH_NO_ROUTE", StringComparison.Ordinal)),
                "potion-no-route: missing FLASH_NO_ROUTE diagnostic");
            Require(!lines.Any(line => line.Contains("FLASH_SEARCH_FAILED", StringComparison.Ordinal)),
                "potion-no-route: no-route must not emit FLASH_SEARCH_FAILED");
        }

        {
            List<string> lines = [];
            SearchPolicySnapshot offPolicy = basePolicy with
            {
                FlashMode = FlashSearchMode.Off,
                Diagnostics = new SearchDiagnosticsSink(
                    message => { lock (lines) lines.Add(message); }, static _ => { }),
            };
            SolverResult result = BeamSolveDispatch.Solve(Request(offPolicy));
            Require(result != null, "off-baseline: Off mode returned null");
            Require(!lines.Any(line => line.Contains("FLASH_SEARCH_FAILED", StringComparison.Ordinal)),
                "off-baseline: Off mode wrote FLASH_SEARCH_FAILED");
            Require(!lines.Any(line => line.Contains("FLASH_LEGACY", StringComparison.Ordinal)),
                "off-baseline: Off mode wrote FLASH_LEGACY");
        }

        Console.WriteLine($"FLASH_FAILURE_CHECKS status=Passed assertions={assertions}");
        return assertions;
    }
}
