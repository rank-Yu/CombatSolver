#!/usr/bin/env python3
"""Reject method bodies for the shared probe/execution helpers in CombatBeamSolver."""
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
SIGNATURE = re.compile(
    r"^\s{4}(?:private|internal|public|protected|static).*\b([A-Z][A-Za-z0-9_]*)\s*\("
)


def main(root: Path) -> int:
    violations: list[str] = []
    for path in sorted(root.glob("src/Search/CombatBeamSolver*.cs")):
        lines = path.read_text(encoding="utf-8").splitlines()
        for index, line in enumerate(lines):
            match = SIGNATURE.match(line)
            if not match or match.group(1) not in NAMES:
                continue
            name = match.group(1)
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
            if saw_body or not saw_arrow:
                violations.append(f"{path}:{index + 1}: {name} still has a method body")
    if violations:
        print("\n".join(violations))
        return 1
    print("PROBE_BOUNDARIES_OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()))
