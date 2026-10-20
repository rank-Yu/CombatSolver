using CombatSolver;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

namespace OfflineSearchHarness;

/// <summary>Everything the existing search reads from the request root of a turn-setup search.</summary>
internal sealed record CapturedTurnSetupRoot(
    CombatRootSnapshot Root,
    SolverDisplayNames Names,
    BattleDamageSnapshot Damage,
    SolverSettingsSnapshot Settings,
    SearchPolicySnapshot Policy);

/// <summary>
/// Captures the player/Start root at the exact <c>CombatManager.SetupPlayerTurn</c> entry point, i.e.
/// before the native setup page runs. The real mod's <c>PlayerTurnSetupPatches</c> captures the same
/// point; the offline host has no overlay/controller session, so it installs this small Harmony prefix
/// instead and lets the native setup finish with a deterministic selector.
/// </summary>
internal static class TurnSetupRootCapture
{
    private static Harmony? _harmony;

    public static bool Enabled { get; set; }

    public static CapturedTurnSetupRoot? Captured { get; private set; }

    public static void Install()
    {
        if (_harmony != null)
            return;
        _harmony = new Harmony("CombatSolver.OfflineSearchHarness.TurnSetupRootCapture");
        _harmony.Patch(
            AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn"),
            prefix: new HarmonyMethod(typeof(TurnSetupRootCapture), nameof(Prefix)));
    }

    private static bool Prefix(CombatManager __instance)
    {
        if (!Enabled || Captured != null)
            return true;
        CombatState? combat = __instance.DebugOnlyGetState();
        if (combat == null)
            return true;
        Player? player = LocalContext.GetMe(combat);
        if (player?.PlayerCombatState is not { Phase: PlayerTurnPhase.Start })
            return true;
        HarnessLog.Trace("turn_setup_root.capture");
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings, combat, includeTurnSetup: true,
            theftPolicy: SolverController.ResolveTheftPolicy(combat));
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat, settings.PredictPotionReward);
        Captured = new CapturedTurnSetupRoot(root, names, damage, settings, policy);
        HarnessLog.Trace(
            $"turn_setup_root.captured turn={player.PlayerCombatState.TurnNumber} " +
            $"phase={player.PlayerCombatState.Phase} side={combat.CurrentSide}");
        return true;
    }
}
