#!/usr/bin/env python3
"""Reject duplicate method bodies for the shared probe/execution helpers."""
from __future__ import annotations

import re
import sys
from pathlib import Path

NAMES = [
    "AccumulateEnemyHpLost", "ActionChoicesForReplay", "AdvanceRound",
    "AdvanceRoundPlayerStart", "AllowsPotionUse", "AppendPendingChoice",
    "ApplyFixedPrefix", "ApplySoldHpPenalty", "BuildCandidate",
    "BuildPlayableCardKey", "BuildPrimaryCardChoiceSpec",
    "BuildRequiredEmptyChoiceSpec", "CanApplyFixedPrefixAction",
    "CaptureCardStateFingerprintForTesting", "CaptureRngStateFingerprintForTesting",
    "CaptureVoidFormOpportunityValueForTesting", "ClassifyActionOptionFamilies",
    "ClassifyCardTraits", "ClassifyPotionTraits", "ClassifyRoundTransitionTraits",
    "ContinuePlayerStart", "CreatePlannedCardChild", "CreatePlannedPotionChild",
    "EnumeratePlannedCardActions", "EnumeratePlannedPotionActions", "Expand",
    "ExplicitPotionUseCount", "FindCardForReplay", "FindCardOccurrence",
    "IsPureHistoryEntry", "IsStolenResourceRecoveryTarget", "OpensRevivalWindow",
    "PolicyActionToken", "PreparePlayerPlayPhase", "ProjectDiagnosticHits",
    "Replay", "ReplayAction", "ReplayTurnSetup", "ResolveRequestedPlayerTurnEnd",
    "ResolveTurnSetupChoices", "SelectActionCandidates", "SettleReplayActionBoundary",
    "Snapshot", "StableShuffleProjection",
]
# These three replay entry points plus the turn-setup root enumeration must have exactly one
# method body in the whole Search tree: the shared replay core. Every solver/probe occurrence
# has to be an expression-bodied forward.
SINGLE_BODY = {
    "Replay", "ReplayAction", "ReplayTurnSetup", "ResolveTurnSetupChoices",
    "BuildTurnSetupRoots",
}
SIGNATURE = re.compile(
    r"^\s{4}(?:private|internal|public|protected|static).*\b([A-Z][A-Za-z0-9_]*)\s*\("
)


def has_body(lines: list[str], index: int) -> tuple[bool, bool]:
    saw_arrow = False
    saw_body = False
    for cursor in range(index, min(index + 80, len(lines))):
        text = lines[cursor]
        arrow = text.find("=>")
        brace = text.find("{")
        if arrow >= 0 and (brace < 0 or arrow < brace):
            saw_arrow = True
            break
        if brace >= 0:
            saw_body = True
            break
        if cursor > index and text.strip().endswith(";"):
            break
    return saw_body, saw_arrow


def main(root: Path) -> int:
    violations: list[str] = []
    bodies: dict[str, list[str]] = {name: [] for name in SINGLE_BODY}
    solver_files = sorted(root.glob("src/Search/CombatBeamSolver*.cs"))
    core_files = sorted(root.glob("src/Search/Probes/*.cs"))
    for path in solver_files:
        lines = path.read_text(encoding="utf-8").splitlines()
        for index, line in enumerate(lines):
            match = SIGNATURE.match(line)
            if not match or match.group(1) not in NAMES:
                continue
            name = match.group(1)
            saw_body, saw_arrow = has_body(lines, index)
            if saw_body or not saw_arrow:
                violations.append(f"{path}:{index + 1}: {name} still has a method body")
            if name in bodies:
                bodies[name].append(f"{path}:{index + 1}: arrow forward")
    for path in core_files:
        lines = path.read_text(encoding="utf-8").splitlines()
        for index, line in enumerate(lines):
            match = SIGNATURE.match(line)
            if not match or match.group(1) not in SINGLE_BODY:
                continue
            name = match.group(1)
            saw_body, _ = has_body(lines, index)
            if saw_body:
                bodies[name].append(f"{path}:{index + 1}: body")
    # Multi-line signatures (return type on the previous line) are not covered by SIGNATURE.
    for path in core_files:
        lines = path.read_text(encoding="utf-8").splitlines()
        for index, line in enumerate(lines):
            if line.strip() != "BuildTurnSetupRoots()":
                continue
            saw_body, _ = has_body(lines, index)
            if saw_body:
                bodies["BuildTurnSetupRoots"].append(f"{path}:{index + 1}: body")
    for name, sites in sorted(bodies.items()):
        bodies_count = sum(1 for site in sites if site.endswith(": body"))
        if bodies_count != 1:
            violations.append(
                f"{name} must have exactly one method body, found {bodies_count}: "
                + "; ".join(sites))
    if violations:
        print("\n".join(violations))
        return 1
    print("PROBE_BOUNDARIES_OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()))
