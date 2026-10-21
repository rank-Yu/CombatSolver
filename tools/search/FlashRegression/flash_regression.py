#!/usr/bin/env python3
"""Run the maintained Flash search regression suites from one cross-platform entry point.

The tool owns the root definitions, the build/K0 preflight, sharding and a bounded
worker pool.  It only calls the existing OfflineSearchHarness executable; it does
not reimplement search or simulation.  See README.md for inputs, outputs and the
acceptance contract of each subcommand.
"""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
import re
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
HARNESS = ROOT / ".local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll"
DEFAULT_OUT = ROOT / ".local/flash-regression"
FIXTURES = ROOT / "coverage/fixtures/cards"
DECKS = ROOT / "coverage/corpora/flash-decks"
QUALITY = ROOT / "coverage/corpora/flash-quality"
ACT3 = ROOT / "coverage/corpora/flash-act3"
KERNEL_CHECKS = ROOT / "tools/testing/checks/FlashKernelChecks"
DOTNET = shutil.which("dotnet") or "dotnet"

# Natural encounter roots from the previous regress.sh: encounter, seed, character.
# The harness defaults to the Ironclad starter deck when character is empty.
NATURAL = [
    ('FUZZY_WURM_CRAWLER_WEAK', 'FLASH1', ''), ('NIBBITS_NORMAL', 'FLASH1', ''), ('NIBBITS_WEAK', 'FLASH1', ''), ('SHRINKER_BEETLE_WEAK', 'FLASHM1', ''),
    ('SLIMES_WEAK', 'FLASHM1', ''), ('SLIMES_NORMAL', 'FLASHM1', ''), ('FLYCONID_NORMAL', 'FLASHM1', ''), ('MAWLER_NORMAL', 'FLASHM1', ''),
    ('RUBY_RAIDERS_NORMAL', 'FLASHM1', ''), ('SNAPPING_JAXFRUIT_NORMAL', 'FLASHM1', ''), ('CUBEX_CONSTRUCT_NORMAL', 'FLASHM1', ''), ('SLITHERING_STRANGLER_NORMAL', 'FLASHM1', ''),
    ('OVERGROWTH_CRAWLERS', 'FLASHM1', ''), ('BYRDONIS_ELITE', 'FLASHM1', ''), ('CULTISTS_NORMAL', 'FLASHM1', ''), ('HAUNTED_SHIP_NORMAL', 'FLASHM1', ''),
    ('PUNCH_CONSTRUCT_NORMAL', 'FLASHM1', ''), ('SEAPUNK_WEAK', 'FLASHM1', ''), ('SEAPUNK_NORMAL', 'FLASHM1', ''), ('SEWER_CLAM_NORMAL', 'FLASHM1', ''),
    ('SLUDGE_SPINNER_WEAK', 'FLASHM1', ''), ('TOADPOLES_WEAK', 'FLASHM1', ''), ('INKLETS_NORMAL', 'FLASHM1', ''), ('BYGONE_EFFIGY_ELITE', 'FLASHM1', ''),
    ('CONSTRUCT_MENAGERIE_NORMAL', 'FLASHM1', ''), ('LAGAVULIN_MATRIARCH_BOSS', 'FLASHM1', ''), ('CORPSE_SLUGS_NORMAL', 'FLASHM1', ''), ('CORPSE_SLUGS_WEAK', 'FLASHM1', ''),
    ('TERROR_EEL_ELITE', 'FLASHM1', ''), ('FOGMOG_NORMAL', 'FLASHM1', ''), ('FOGMOG_NORMAL', 'FLASHM2', ''), ('TWO_TAILED_RATS_NORMAL', 'FLASHM1', ''),
    ('TWO_TAILED_RATS_NORMAL', 'FLASHM2', ''), ('CHOMPERS_NORMAL', 'FLASHM1', ''), ('CHOMPERS_NORMAL', 'FLASHM2', ''), ('ENTOMANCER_ELITE', 'FLASHM1', ''),
    ('ENTOMANCER_ELITE', 'FLASHM2', ''), ('EXOSKELETONS_NORMAL', 'FLASHM1', ''), ('EXOSKELETONS_NORMAL', 'FLASHM2', ''), ('EXOSKELETONS_WEAK', 'FLASHM1', ''),
    ('EXOSKELETONS_WEAK', 'FLASHM2', ''), ('MYTES_NORMAL', 'FLASHM1', ''), ('MYTES_NORMAL', 'FLASHM2', ''), ('OVICOPTER_NORMAL', 'FLASHM1', ''),
    ('OVICOPTER_NORMAL', 'FLASHM2', ''), ('SPINY_TOAD_NORMAL', 'FLASHM1', ''), ('SPINY_TOAD_NORMAL', 'FLASHM2', ''), ('THE_OBSCURA_NORMAL', 'FLASHM1', ''),
    ('THE_OBSCURA_NORMAL', 'FLASHM2', ''), ('TUNNELER_WEAK', 'FLASHM1', ''), ('TUNNELER_WEAK', 'FLASHM2', ''), ('THE_INSATIABLE_BOSS', 'FLASHM1', ''),
    ('THE_INSATIABLE_BOSS', 'FLASHM2', ''), ('FAKE_MERCHANT_EVENT_ENCOUNTER', 'FLASHM1', ''), ('FAKE_MERCHANT_EVENT_ENCOUNTER', 'FLASHM2', ''), ('BATTLEWORN_DUMMY_EVENT_V1_ENCOUNTER', 'FLASHM1', ''),
    ('BATTLEWORN_DUMMY_EVENT_V1_ENCOUNTER', 'FLASHM2', ''), ('BATTLEWORN_DUMMY_EVENT_V2_ENCOUNTER', 'FLASHM1', ''), ('BATTLEWORN_DUMMY_EVENT_V2_ENCOUNTER', 'FLASHM2', ''), ('BATTLEWORN_DUMMY_EVENT_V3_ENCOUNTER', 'FLASHM1', ''),
    ('BATTLEWORN_DUMMY_EVENT_V3_ENCOUNTER', 'FLASHM2', ''), ('MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER', 'FLASHM1', ''), ('MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER', 'FLASHM2', ''), ('THE_ARCHITECT_EVENT_ENCOUNTER', 'FLASHM1', ''),
    ('THE_ARCHITECT_EVENT_ENCOUNTER', 'FLASHM2', ''), ('KNOWLEDGE_DEMON_BOSS', 'FLASHM1', ''), ('KNOWLEDGE_DEMON_BOSS', 'FLASHM2', ''), ('THIEVING_HOPPER_WEAK', 'FLASHM1', ''),
    ('THIEVING_HOPPER_WEAK', 'FLASHM2', ''), ('GREMLIN_MERC_NORMAL', 'FLASHM1', ''), ('GREMLIN_MERC_NORMAL', 'FLASHM2', ''), ('WATERFALL_GIANT_BOSS', 'FLASHM1', ''),
    ('WATERFALL_GIANT_BOSS', 'FLASHM2', ''), ('NIBBITS_NORMAL', 'FLASHM1', 'SILENT'), ('NIBBITS_NORMAL', 'FLASHM2', 'SILENT'), ('SLIMES_NORMAL', 'FLASHM1', 'SILENT'),
    ('SLIMES_NORMAL', 'FLASHM2', 'SILENT'), ('CULTISTS_NORMAL', 'FLASHM1', 'SILENT'), ('CULTISTS_NORMAL', 'FLASHM2', 'SILENT'), ('NIBBITS_NORMAL', 'FLASHM1', 'DEFECT'),
    ('NIBBITS_NORMAL', 'FLASHM2', 'DEFECT'), ('SLIMES_NORMAL', 'FLASHM1', 'DEFECT'), ('SLIMES_NORMAL', 'FLASHM2', 'DEFECT'), ('CULTISTS_NORMAL', 'FLASHM1', 'DEFECT'),
    ('CULTISTS_NORMAL', 'FLASHM2', 'DEFECT'), ('NIBBITS_NORMAL', 'FLASHM1', 'REGENT'), ('NIBBITS_NORMAL', 'FLASHM2', 'REGENT'), ('SLIMES_NORMAL', 'FLASHM1', 'REGENT'),
    ('SLIMES_NORMAL', 'FLASHM2', 'REGENT'), ('CULTISTS_NORMAL', 'FLASHM1', 'REGENT'), ('CULTISTS_NORMAL', 'FLASHM2', 'REGENT'), ('NIBBITS_NORMAL', 'FLASHM1', 'NECROBINDER'),
    ('NIBBITS_NORMAL', 'FLASHM2', 'NECROBINDER'), ('SLIMES_NORMAL', 'FLASHM1', 'NECROBINDER'), ('SLIMES_NORMAL', 'FLASHM2', 'NECROBINDER'), ('CULTISTS_NORMAL', 'FLASHM1', 'NECROBINDER'),
    ('CULTISTS_NORMAL', 'FLASHM2', 'NECROBINDER'), ('BOWLBUGS_NORMAL', 'FLASHM1', ''), ('BOWLBUGS_NORMAL', 'FLASHM2', ''), ('BOWLBUGS_WEAK', 'FLASHM1', ''),
    ('BOWLBUGS_WEAK', 'FLASHM2', ''), ('DECIMILLIPEDE_ELITE', 'FLASHM1', ''), ('DECIMILLIPEDE_ELITE', 'FLASHM2', ''), ('HUNTER_KILLER_NORMAL', 'FLASHM1', ''),
    ('HUNTER_KILLER_NORMAL', 'FLASHM2', ''), ('KAISER_CRAB_BOSS', 'FLASHM1', ''), ('KAISER_CRAB_BOSS', 'FLASHM2', ''), ('LOUSE_PROGENITOR_NORMAL', 'FLASHM1', ''),
    ('LOUSE_PROGENITOR_NORMAL', 'FLASHM2', ''), ('PHROG_PARASITE_ELITE', 'FLASHM1', ''), ('PHROG_PARASITE_ELITE', 'FLASHM2', ''), ('SLUMBERING_BEETLE_NORMAL', 'FLASHM1', ''),
    ('SLUMBERING_BEETLE_NORMAL', 'FLASHM2', ''), ('CEREMONIAL_BEAST_BOSS', 'FLASHM1', ''), ('CEREMONIAL_BEAST_BOSS', 'FLASHM2', ''), ('INFESTED_PRISMS_ELITE', 'FLASHM1', ''),
    ('INFESTED_PRISMS_ELITE', 'FLASHM2', ''), ('LIVING_FOG_NORMAL', 'FLASHM1', ''), ('LIVING_FOG_NORMAL', 'FLASHM2', ''), ('VINE_SHAMBLER_NORMAL', 'FLASHM1', ''),
    ('VINE_SHAMBLER_NORMAL', 'FLASHM2', ''), ('AXEBOTS_NORMAL', 'FLASHM1', ''), ('AXEBOTS_NORMAL', 'FLASHM2', ''), ('CONSTRUCT_MENAGERIE_NORMAL', 'FLASHM2', ''),
    ('DEVOTED_SCULPTOR_WEAK', 'FLASHM1', ''), ('DEVOTED_SCULPTOR_WEAK', 'FLASHM2', ''), ('AEONGLASS_BOSS', 'FLASHM1', ''), ('AEONGLASS_BOSS', 'FLASHM2', ''),
    ('FABRICATOR_NORMAL', 'FLASHM1', ''), ('FABRICATOR_NORMAL', 'FLASHM2', ''), ('FROG_KNIGHT_NORMAL', 'FLASHM1', ''), ('FROG_KNIGHT_NORMAL', 'FLASHM2', ''),
    ('GLOBE_HEAD_NORMAL', 'FLASHM1', ''), ('GLOBE_HEAD_NORMAL', 'FLASHM2', ''), ('KNIGHTS_ELITE', 'FLASHM1', ''), ('KNIGHTS_ELITE', 'FLASHM2', ''),
    ('MECHA_KNIGHT_ELITE', 'FLASHM1', ''), ('MECHA_KNIGHT_ELITE', 'FLASHM2', ''), ('OWL_MAGISTRATE_NORMAL', 'FLASHM1', ''), ('OWL_MAGISTRATE_NORMAL', 'FLASHM2', ''),
    ('QUEEN_BOSS', 'FLASHM1', ''), ('QUEEN_BOSS', 'FLASHM2', ''), ('SCROLLS_OF_BITING_NORMAL', 'FLASHM1', ''), ('SCROLLS_OF_BITING_NORMAL', 'FLASHM2', ''),
    ('SCROLLS_OF_BITING_WEAK', 'FLASHM1', ''), ('SCROLLS_OF_BITING_WEAK', 'FLASHM2', ''), ('SLIMED_BERSERKER_NORMAL', 'FLASHM1', ''), ('SLIMED_BERSERKER_NORMAL', 'FLASHM2', ''),
    ('SOUL_NEXUS_ELITE', 'FLASHM1', ''), ('SOUL_NEXUS_ELITE', 'FLASHM2', ''), ('TEST_SUBJECT_BOSS', 'FLASHM1', ''), ('TEST_SUBJECT_BOSS', 'FLASHM2', ''),
    ('THE_LOST_AND_FORGOTTEN_NORMAL', 'FLASHM1', ''), ('THE_LOST_AND_FORGOTTEN_NORMAL', 'FLASHM2', ''), ('TURRET_OPERATOR_WEAK', 'FLASHM1', ''), ('TURRET_OPERATOR_WEAK', 'FLASHM2', ''),
]

# Act 3 bosses need an explicit act index so Act3BossStrategy is selected.
ACT3_BOSSES = [
    ('AEONGLASS_BOSS', 'FLASHM1'), ('AEONGLASS_BOSS', 'FLASHM2'),
    ('QUEEN_BOSS', 'FLASHM1'), ('QUEEN_BOSS', 'FLASHM2'),
    ('TEST_SUBJECT_BOSS', 'FLASHM1'), ('TEST_SUBJECT_BOSS', 'FLASHM2'),
]

BENCH_ENCOUNTERS = [
    'FUZZY_WURM_CRAWLER_WEAK', 'NIBBITS_NORMAL', 'NIBBITS_WEAK', 'SHRINKER_BEETLE_WEAK',
    'SLIMES_WEAK', 'SLIMES_NORMAL', 'FLYCONID_NORMAL', 'MAWLER_NORMAL', 'RUBY_RAIDERS_NORMAL',
    'SNAPPING_JAXFRUIT_NORMAL', 'CUBEX_CONSTRUCT_NORMAL', 'SLITHERING_STRANGLER_NORMAL',
    'OVERGROWTH_CRAWLERS', 'BYRDONIS_ELITE', 'CULTISTS_NORMAL', 'HAUNTED_SHIP_NORMAL',
    'PUNCH_CONSTRUCT_NORMAL', 'SEAPUNK_WEAK', 'SEAPUNK_NORMAL', 'SEWER_CLAM_NORMAL',
    'SLUDGE_SPINNER_WEAK', 'TOADPOLES_WEAK', 'INKLETS_NORMAL', 'BYGONE_EFFIGY_ELITE',
]

SURVEY_KINDS = ('Monster', 'Elite', 'Boss')
FLASH_LOG_LINE = re.compile(
    r"\[CombatSolver/Flash\] (\S+) purpose=PrimaryBeam (?:incumbent=\S+ )?flash=\[status=(\S+) ms=(\d+)"
)


@dataclass
class Root:
    name: str
    encounter: str = ''
    seed: str = ''
    character: str = ''
    act_index: int = 0
    request: Path | None = None
    walks: int = 32
    category: str = 'encounter'
    category_index: int = 0
    shard: int = 0
    spec: dict = field(default_factory=dict)


def _relative(path: Path) -> str:
    try:
        return path.resolve().relative_to(ROOT).as_posix()
    except ValueError:
        return str(path.resolve())


def natural_roots(walks: int) -> list[Root]:
    roots = []
    for index, (encounter, seed, character) in enumerate(NATURAL):
        name = f"enc-{encounter}-{seed}" + (f"-{character}" if character else '')
        roots.append(Root(name, encounter, seed, character or 'IRONCLAD', 0, None, walks, 'encounter', index))
    offset = len(roots)
    for index, (encounter, seed) in enumerate(ACT3_BOSSES):
        roots.append(Root(f"a3boss-{encounter}-{seed}", encounter, seed, 'IRONCLAD', 2, None, walks,
                          'act3boss', offset + index))
    return roots


def fixture_roots(walks: int) -> list[Root]:
    roots = []
    for index, path in enumerate(sorted(FIXTURES.glob('flash-*.json'))):
        data = json.loads(path.read_text(encoding='utf-8-sig'))
        roots.append(Root(path.stem, data['encounterId'], str(data['seed']), 'IRONCLAD', 0, path, walks,
                          'fixture', index))
    return roots


def deck_roots(walks: int) -> list[Root]:
    roots = []
    for index, request in enumerate(sorted(DECKS.glob('*/request.json'))):
        roots.append(Root(f"deck-{request.parent.name}", 'NIBBITS_NORMAL', 'FLASH1', 'IRONCLAD', 0, request,
                          walks, 'deck', index))
    return roots


def act3_roots(walks: int) -> list[Root]:
    roots = []
    for index, request in enumerate(sorted(ACT3.glob('*/*/request.json'))):
        spec = json.loads((request.parent / 'spec.json').read_text(encoding='utf-8-sig'))
        roots.append(Root(f"act3-{request.parent.name}", character=spec['characterId'],
                          act_index=int(spec.get('actIndex', 2)), request=request, walks=walks,
                          category='act3', category_index=index, spec=spec))
    return roots


def resolve_roots(args: argparse.Namespace) -> list[Root]:
    only = args.only
    roots: list[Root] = []
    if only in ('all', 'encounters'):
        roots += natural_roots(args.walks)
    if only in ('all', 'fixtures'):
        roots += fixture_roots(args.walks)
    if only in ('all', 'decks'):
        roots += deck_roots(args.walks)
    if only == 'act3' or args.include_act3:
        roots += act3_roots(args.act3_walks)
    shards = max(1, args.shards)
    for root in roots:
        root.shard = root.category_index % shards
    if args.shard is not None:
        if not 0 <= args.shard < shards:
            raise SystemExit(f"--shard must be in [0, {shards})")
        roots = [root for root in roots if root.shard == args.shard]
    if args.root:
        selected = []
        for root in roots:
            if root.name in args.root or any(token in root.name for token in args.root):
                selected.append(root)
        roots = selected
    return roots


def baseline_environment() -> dict[str, str]:
    env = os.environ.copy()
    env.setdefault('DOTNET_CLI_TELEMETRY_OPTOUT', '1')
    return env


def run_command(command: list[str], *, cwd: Path, env: dict[str, str] | None = None,
                timeout: float | None = None, log: Path | None = None) -> int:
    try:
        if log is not None:
            log.parent.mkdir(parents=True, exist_ok=True)
            with log.open('wb') as stream:
                completed = subprocess.run(command, cwd=cwd, env=env, stdout=stream,
                                           stderr=subprocess.STDOUT, timeout=timeout)
            return completed.returncode
        return subprocess.run(command, cwd=cwd, env=env, timeout=timeout).returncode
    except subprocess.TimeoutExpired:
        return 124
    except FileNotFoundError as error:
        print(f"missing executable: {error}", file=sys.stderr)
        return 127


def build_all(out: Path, *, dotnet: str, harness: Path, k0: bool) -> tuple[bool, str]:
    out.mkdir(parents=True, exist_ok=True)
    build_log = out / 'build.txt'
    commands = [
        [dotnet, 'build', 'CombatSolver.csproj', '-c', 'Release', '-p:CopyModOnBuild=false',
         '-p:NuGetAudit=false', '-v', 'q', '-nologo'],
        [dotnet, 'build', 'tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj', '-c', 'Release',
         '-p:NuGetAudit=false', '-v', 'q', '-nologo'],
    ]
    build_log.write_text('', encoding='utf-8')
    for command in commands:
        code = run_command(command, cwd=ROOT, log=build_log)
        if code != 0:
            return False, 'BUILD FAILED'
    if not harness.is_file():
        return False, f'HARNESS MISSING {harness}'
    contract = ''
    if k0:
        env = baseline_environment()
        env['DOTNET_TieredCompilation'] = '0'
        code = run_command([dotnet, 'run', '--project', str(KERNEL_CHECKS), '-c', 'Release'],
                           cwd=ROOT, env=env, log=out / 'k0.txt')
        text = (out / 'k0.txt').read_text(encoding='utf-8', errors='replace')
        match = re.search(r'"contracts"\s*:\s*"([A-Za-z]+)"', text)
        contract = f'K0 exit={code} ' + (match.group(0) if match else 'NO-CONTRACTS')
        if code != 0:
            return False, contract
    return True, contract


def harness_command(harness: Path, root: Root, outdir: Path, *, dotnet: str = DOTNET,
                    extra: list[str] | None = None) -> list[str]:
    command = [dotnet, str(harness)]
    if root.encounter:
        command += ['--encounter', root.encounter, '--seed', root.seed]
        if root.character:
            command += ['--character', root.character]
        if root.act_index and root.request is None:
            command += ['--act-index', str(root.act_index)]
    if root.request is not None:
        command += ['--request', str(root.request)]
    command += ['--milestone', 'M1', '--out', str(outdir)]
    if extra:
        command += extra
    return command


def run_roots(roots: list[Root], out: Path, jobs: int, command_for, *, env_for, timeout: float) -> list[str]:
    results: dict[str, str] = {}

    def work(root: Root) -> tuple[str, str]:
        outdir = out / root.name
        outdir.mkdir(parents=True, exist_ok=True)
        env = env_for(root)
        code = run_command(command_for(root, outdir), cwd=ROOT, env=env, timeout=timeout,
                           log=outdir / 'stdout.txt')
        return root.name, summarize(root, outdir, code)

    total = len(roots)
    started = time.time()
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, jobs)) as executor:
        futures = {executor.submit(work, root): root for root in roots}
        for done, future in enumerate(concurrent.futures.as_completed(futures), 1):
            name, line = future.result()
            results[name] = line
            elapsed = time.time() - started
            print(f'[{done}/{total}] {name} {elapsed:7.1f}s', flush=True)
    return [results[root.name] for root in roots]


def summarize(root: Root, outdir: Path, code: int) -> str:
    first = 'NO-DIFF-OUTPUT'
    diff = outdir / 'flash-diff.txt'
    if diff.is_file():
        with diff.open(encoding='utf-8', errors='replace') as stream:
            first = stream.readline().strip() or 'EMPTY-DIFF-OUTPUT'
    return f'{root.name} exit={code} {first}'


def parse_summary(path: Path) -> dict[str, tuple[int, dict[str, str], str]]:
    rows: dict[str, tuple[int, dict[str, str], str]] = {}
    for line in path.read_text(encoding='utf-8', errors='replace').splitlines():
        match = re.match(r'(\S+) exit=(\d+) (.*)', line.strip())
        if not match or match.group(1) == 'K0':
            continue
        values = dict(re.findall(r'(\w+)=(\S+)', match.group(3)))
        rows[match.group(1)] = (int(match.group(2)), values, match.group(3))
    return rows


def diff_line_failed(line: str) -> bool:
    match = re.match(r'\S+ exit=(\d+) (.*)', line)
    if not match or int(match.group(1)) != 0:
        return True
    summary = match.group(2)
    if not summary.startswith('SUMMARY'):
        return True
    for key in ('failed', 'walkFailed'):
        found = re.search(rf'\b{key}=(\d+)', summary)
        if not found or int(found.group(1)) != 0:
            return True
    return False


def scan_line_failed(line: str) -> bool:
    match = re.match(r'\S+ exit=(\d+) ', line)
    if not match or int(match.group(1)) != 0:
        return True
    mismatches = re.search(r'\bmismatches=(\d+)', line)
    return not mismatches or int(mismatches.group(1)) != 0


def compare_baseline(new_path: Path, base_path: Path) -> tuple[list[str], str]:
    fail_keys = ('failed', 'walkFailed', 'scoreFailed', 'boundFailed', 'traitFailed',
                 'pocketwatchFailed', 'declinedFailed', 'unsupported')
    new = parse_summary(new_path)
    base = parse_summary(base_path)
    problems: list[str] = []
    for name, (code, values, raw) in sorted(new.items()):
        bad = []
        if code != 0:
            bad.append(f'exit={code}')
        if 'SUMMARY' not in raw:
            bad.append('no-summary')
        bad += [f'{key}={values[key]}' for key in fail_keys if values.get(key, '0') not in ('0',)]
        if name in base:
            old = int(base[name][1].get('pureRefMismatch', 0))
            cur = int(values.get('pureRefMismatch', 0))
            if cur > old:
                bad.append(f'pureRefMismatch {old}->{cur}')
        if bad:
            problems.append(f'BAD {name} ' + ' '.join(bad))
    summary = (f'rows={len(new)} baseline={len(base)} new-only={len(set(new) - set(base))} '
               f'missing={sorted(set(base) - set(new))} bad={len(problems)}')
    return problems, summary


def write_summary(path: Path, lines: list[str]) -> None:
    path.write_text('\n'.join(lines) + '\n', encoding='utf-8')


def add_common(parser: argparse.ArgumentParser) -> None:
    parser.add_argument('--label', default=None, help='output directory name under .local/flash-regression')
    parser.add_argument('--out', type=Path, default=None, help='explicit output directory (wins over --label)')
    parser.add_argument('--jobs', type=int, default=4, help='maximum concurrent harness processes (default 4)')
    parser.add_argument('--dotnet', default=DOTNET, help='dotnet executable')
    parser.add_argument('--harness', type=Path, default=HARNESS, help='OfflineSearchHarness.dll')
    parser.add_argument('--skip-build', action='store_true', help='use the existing Release build')
    parser.add_argument('--no-k0', action='store_true', help='skip FlashKernelChecks')


def output_dir(args: argparse.Namespace, suffix: str) -> Path:
    if args.out:
        return args.out.resolve()
    label = args.label or suffix
    return (DEFAULT_OUT / label).resolve()


def add_root_options(parser: argparse.ArgumentParser) -> None:
    parser.add_argument('--walks', type=int, default=32, help='random walks per root (default 32)')
    parser.add_argument('--act3-walks', type=int, default=48, help='walks for --include-act3 roots (default 48)')
    parser.add_argument('--only', choices=('all', 'encounters', 'fixtures', 'decks', 'act3'), default='all')
    parser.add_argument('--include-act3', action='store_true', help='add coverage/corpora/flash-act3')
    parser.add_argument('--shards', type=int, default=1, help='split roots into this many shards')
    parser.add_argument('--shard', type=int, default=None, help='run only this shard (0-based)')
    parser.add_argument('--root', action='append', default=[], help='substring/name filter; repeatable')


def prepare(args: argparse.Namespace, out: Path, *, k0: bool) -> bool:
    if args.skip_build:
        return True
    ok, contract = build_all(out, dotnet=args.dotnet, harness=args.harness, k0=k0 and not args.no_k0)
    if contract:
        print(contract, flush=True)
    if not ok:
        write_summary(out / 'SUMMARY.txt', [contract or 'BUILD FAILED'])
        print(contract or 'BUILD FAILED', file=sys.stderr)
    return ok


def cmd_build(args: argparse.Namespace) -> int:
    out = output_dir(args, 'build')
    ok, contract = build_all(out, dotnet=args.dotnet, harness=args.harness, k0=not args.no_k0)
    print(contract or ('BUILD_OK' if ok else 'BUILD FAILED'))
    return 0 if ok else 1


def cmd_diff(args: argparse.Namespace) -> int:
    out = output_dir(args, 'diff')
    if not prepare(args, out, k0=True):
        return 1
    roots = resolve_roots(args)
    if not roots:
        print('no roots selected', file=sys.stderr)
        return 2
    print(f'diff: {len(roots)} roots, jobs={args.jobs}, walks={args.walks}', flush=True)

    def env_for(root: Root) -> dict[str, str]:
        env = baseline_environment()
        env['OFFLINE_HARNESS_FLASH_DIFF'] = '1'
        env['OFFLINE_HARNESS_FLASH_DIFF_WALKS'] = str(root.walks)
        return env

    lines = run_roots(roots, out, args.jobs,
                      lambda root, outdir: harness_command(args.harness, root, outdir, dotnet=args.dotnet),
                      env_for=env_for, timeout=600)
    k0_line = ''
    k0_path = out / 'k0.txt'
    if k0_path.is_file() and not args.skip_build and not args.no_k0:
        text = k0_path.read_text(encoding='utf-8', errors='replace')
        match = re.search(r'"contracts"\s*:\s*"([A-Za-z]+)"', text)
        k0_line = 'K0 exit=0 ' + (match.group(0) if match else '')
    write_summary(out / 'SUMMARY.txt', ([k0_line] if k0_line else []) + lines + ['DONE'])
    failed = [line for line in lines if diff_line_failed(line)]
    if args.baseline:
        problems, summary = compare_baseline(out / 'SUMMARY.txt', args.baseline)
        print(summary)
        for problem in problems:
            print(problem)
        if problems:
            failed.append('baseline')
        (out / 'BASELINE.txt').write_text('\n'.join([summary] + problems) + '\n', encoding='utf-8')
    print(f'wrote {out / "SUMMARY.txt"}')
    return 1 if failed else 0


def scan_row(path: Path) -> str:
    try:
        data = json.loads(path.read_text(encoding='utf-8-sig'))
        solve = data.get('flashSolve') or {}
        scan = solve.get('flashCandidateScan') or {}
        error = str(solve.get('error') or data.get('error') or '')[:160].replace('\n', ' ')
        return f"routes={scan.get('routes', 0)} mismatches={scan.get('mismatches', 0)} error={error}"
    except (OSError, ValueError) as error:
        return f'NO-RESULT {type(error).__name__}'


def cmd_scan(args: argparse.Namespace) -> int:
    out = output_dir(args, 'scan')
    if not prepare(args, out, k0=True):
        return 1
    roots = resolve_roots(args)
    if not roots:
        print('no roots selected', file=sys.stderr)
        return 2
    print(f'scan: {len(roots)} roots, jobs={args.jobs}, candidates={args.candidates}', flush=True)

    def env_for(_: Root) -> dict[str, str]:
        env = baseline_environment()
        env['OFFLINE_HARNESS_FLASH_SOLVE'] = '1'
        env['OFFLINE_HARNESS_FLASH_CANDIDATE_SCAN'] = str(args.candidates)
        return env

    def command(root: Root, outdir: Path) -> list[str]:
        return harness_command(args.harness, root, outdir, dotnet=args.dotnet, extra=[
            '--budget-ms', str(args.budget_ms), '--profile', 'Low', '--beam', '24',
            '--nodes', '2000', '--dop', '1'])

    results: dict[str, str] = {}
    total = len(roots)
    started = time.time()

    def work(root: Root) -> tuple[str, str]:
        outdir = out / root.name
        outdir.mkdir(parents=True, exist_ok=True)
        code = run_command(command(root, outdir), cwd=ROOT, env=env_for(root), timeout=300,
                           log=outdir / 'stdout.txt')
        return root.name, f'{root.name} exit={code} {scan_row(outdir / "harness-result.json")}'

    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as executor:
        futures = {executor.submit(work, root): root for root in roots}
        for done, future in enumerate(concurrent.futures.as_completed(futures), 1):
            name, line = future.result()
            results[name] = line
            print(f'[{done}/{total}] {name} {time.time() - started:7.1f}s', flush=True)
    lines = [results[root.name] for root in roots]
    k0_line = ''
    k0_path = out / 'k0.txt'
    if k0_path.is_file() and not args.skip_build and not args.no_k0:
        text = k0_path.read_text(encoding='utf-8', errors='replace')
        match = re.search(r'"contracts"\s*:\s*"([A-Za-z]+)"', text)
        k0_line = 'K0 exit=0 ' + (match.group(0) if match else '')
    write_summary(out / 'SUMMARY.txt', ([k0_line] if k0_line else []) + lines + ['DONE'])
    print(f'wrote {out / "SUMMARY.txt"}')
    return 1 if any(scan_line_failed(line) for line in lines) else 0


def bench_key(value: dict) -> tuple:
    return (not value.get('won'), value.get('projectedBattleHpLost'), value.get('combatEndedTurn') or 99)


def bench_table(out: Path, rows: list[str], totals: dict) -> None:
    better, same, worse = totals['better'], totals['same'], totals['worse']
    rows.append(f"flash better={better} same={same} worse={worse} total_lost_delta={totals['lost']:+d} "
                f"old_ms={totals['old_ms']:.0f} flash_warm_ms={totals['flash_ms']:.0f} "
                f"alloc_mb_max={max(totals['allocs'] or [0]):.1f} "
                f"alloc_mb_mean={sum(totals['allocs']) / max(1, len(totals['allocs'])):.1f}")
    (out / 'TABLE.txt').write_text('\n'.join(rows) + '\n', encoding='utf-8')
    print(rows[-1])


def cmd_bench(args: argparse.Namespace) -> int:
    out = output_dir(args, 'bench')
    if not prepare(args, out, k0=False):
        return 1
    seeds = args.seeds or ['FLASHM1', 'FLASHM2']
    encounters = args.encounter or BENCH_ENCOUNTERS
    roots = []
    for encounter in encounters:
        for seed in seeds:
            roots.append(Root(f'{encounter}-{seed}', encounter, seed, 'IRONCLAD', 0, None, 0, 'bench'))
    print(f'bench: {len(roots)} roots, jobs={args.jobs}, budget={args.budget_ms}ms', flush=True)

    def env_for(_: Root) -> dict[str, str]:
        env = baseline_environment()
        env['OFFLINE_HARNESS_FLASH_SOLVE'] = '1'
        return env

    def run_one(root: Root) -> None:
        outdir = out / root.name
        outdir.mkdir(parents=True, exist_ok=True)
        command = [args.dotnet, str(args.harness), '--character', 'IRONCLAD', '--encounter', root.encounter,
                   '--seed', root.seed, '--milestone', 'M1', '--budget-ms', str(args.budget_ms),
                   '--out', str(outdir)]
        code = run_command(command, cwd=ROOT, env=env_for(root), timeout=900, log=outdir / 'stdout.txt')
        if code != 0:
            (outdir / 'exit.txt').write_text(str(code), encoding='utf-8')

    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as executor:
        list(executor.map(run_one, roots))

    rows: list[str] = []
    totals = {'better': 0, 'same': 0, 'worse': 0, 'lost': 0, 'old_ms': 0.0, 'flash_ms': 0.0, 'allocs': []}
    for directory in sorted(path for path in out.iterdir() if path.is_dir()):
        try:
            solve = json.loads((directory / 'harness-result.json').read_text(encoding='utf-8-sig')).get('flashSolve') or {}
        except (OSError, ValueError) as error:
            rows.append(f'{directory.name:42} ERROR {error}')
            continue
        if solve.get('flashRejection'):
            rows.append(f'{directory.name:42} REJECTED {solve["flashRejection"]}')
            continue
        if 'flash' not in solve:
            rows.append(f'{directory.name:42} NO-RESULT')
            continue
        existing, flash, search = solve['existing'], solve['flash'], solve['flashSearch']
        mark = '=' if bench_key(existing) == bench_key(flash) else ('+' if bench_key(flash) < bench_key(existing) else '-')
        totals['better'] += mark == '+'
        totals['worse'] += mark == '-'
        totals['same'] += mark == '='
        warm = min(solve.get('flashWarmWallMs') or [flash['wallMs']])
        totals['old_ms'] += existing['wallMs']
        totals['flash_ms'] += warm
        totals['allocs'].append(min(solve.get('flashWarmAllocatedBytes') or [0]) / 1e6)
        totals['lost'] += (flash.get('projectedBattleHpLost') or 0) - (existing.get('projectedBattleHpLost') or 0)
        rows.append(f"{directory.name:42} {mark} old won={existing.get('won')!s:5} "
                    f"lost={existing.get('projectedBattleHpLost')!s:>3} turn={existing.get('combatEndedTurn')!s:>4} "
                    f"ms={existing['wallMs']:6.0f} | flash won={flash.get('won')!s:5} "
                    f"lost={flash.get('projectedBattleHpLost')!s:>3} turn={flash.get('combatEndedTurn')!s:>4} "
                    f"warm_ms={warm:6.0f} search_ms={search['searchMs']:6.1f}")
    bench_table(out, rows, totals)
    return 0


def load_quality(directory: Path) -> tuple[float, dict, str]:
    result = json.loads((directory / 'harness-result.json').read_text(encoding='utf-8-sig'))
    quality = json.loads((directory / 'quality.json').read_text(encoding='utf-8-sig'))['quality']
    flash = ''
    for path in (directory / 'logs').rglob('process.jsonl'):
        for line in path.read_text(encoding='utf-8', errors='replace').splitlines():
            match = FLASH_LOG_LINE.search(line)
            if match:
                flash = f'{match.group(1)}:{match.group(2)}:{match.group(3)}ms'
    return result['wallSeconds'], quality, flash


def coordinator_table(out: Path) -> str:
    rows: list[str] = []
    total = {'Off': 0.0, 'On': 0.0}
    better = same = worse = adopted = lost = 0

    def key(quality: dict) -> tuple:
        return (not quality['won'], quality['projectedBattleHpLost'], quality['combatEndedTurn'] or 99)

    for root in sorted(path for path in out.iterdir() if path.is_dir()):
        try:
            (off_wall, off_quality, _), (on_wall, on_quality, flash) = (
                load_quality(root / 'Off'), load_quality(root / 'On'))
        except (OSError, ValueError, KeyError) as error:
            rows.append(f'{root.name:40} ERROR {error}')
            continue
        mark = '=' if key(off_quality) == key(on_quality) else ('+' if key(on_quality) < key(off_quality) else '-')
        better += mark == '+'
        same += mark == '='
        worse += mark == '-'
        adopted += flash.startswith('FLASH_ADOPTED')
        total['Off'] += off_wall
        total['On'] += on_wall
        lost += on_quality['projectedBattleHpLost'] - off_quality['projectedBattleHpLost']
        rows.append(f"{root.name:40} {mark} off lost={off_quality['projectedBattleHpLost']:>3} "
                    f"turn={off_quality['combatEndedTurn']!s:>3} wall={off_wall:6.2f}s | "
                    f"on lost={on_quality['projectedBattleHpLost']:>3} "
                    f"turn={on_quality['combatEndedTurn']!s:>3} wall={on_wall:6.2f}s {flash}")
    rows.append(f"on better={better} same={same} worse={worse} lost_delta={lost:+d} adopted={adopted} "
                f"off_wall={total['Off']:.1f}s on_wall={total['On']:.1f}s")
    (out / 'TABLE.txt').write_text('\n'.join(rows) + '\n', encoding='utf-8')
    return rows[-1]


def strategic_table(out: Path) -> str:
    lines: list[str] = []
    for root in sorted(path for path in out.glob('*-[0-9][0-9]') if path.is_dir()):
        try:
            off = json.loads((root / 'Off/quality.json').read_text(encoding='utf-8-sig'))['quality']
            on = json.loads((root / 'On/quality.json').read_text(encoding='utf-8-sig'))['quality']
        except (OSError, ValueError, KeyError):
            continue
        key = lambda value: (not value['won'], value['strategicHpDeficit'], value['combatEndedTurn'] or 99)
        mark = '=' if key(off) == key(on) else ('+' if key(on) < key(off) else '-')
        lines.append(f"{root.name} {mark} deficit {off['strategicHpDeficit']}->{on['strategicHpDeficit']} "
                     f"turn {off['combatEndedTurn']}->{on['combatEndedTurn']} "
                     f"lost {off['projectedBattleHpLost']}->{on['projectedBattleHpLost']}")
    (out / 'STRATEGIC.txt').write_text('\n'.join(lines) + ('\n' if lines else ''), encoding='utf-8')
    return f'strategic rows={len(lines)}'


def qcompare(left: Path, right: Path, mode: str) -> tuple[int, int]:
    total = diff = 0
    for root in sorted(path for path in left.iterdir() if path.is_dir()):
        left_quality = root / mode / 'quality.json'
        right_quality = right / root.name / mode / 'quality.json'
        if not left_quality.is_file() or not right_quality.is_file():
            continue
        total += 1
        old = json.loads(left_quality.read_text(encoding='utf-8-sig'))
        new = json.loads(right_quality.read_text(encoding='utf-8-sig'))
        old_key = (old.get('won'), old.get('strategicHpDeficit'), old.get('combatEndedTurn'))
        new_key = (new.get('won'), new.get('strategicHpDeficit'), new.get('combatEndedTurn'))
        if old_key != new_key:
            diff += 1
            print(root.name, old_key, '->', new_key)
    print(f'total {total} diff {diff}')
    return total, diff


def coordinator_roots(args: argparse.Namespace) -> list[tuple[str, Path]]:
    roots: list[tuple[str, Path]] = []
    if args.quality_dir:
        roots += [(path.parent.name, path) for path in sorted(Path(args.quality_dir).glob('*/request.json'))]
    if args.deck_dir:
        for request in sorted(Path(args.deck_dir).glob('*/request.json')):
            spec = json.loads((request.parent / 'spec.json').read_text(encoding='utf-8-sig'))
            if not args.include_act3_decks and int(spec.get('actIndex', 0)) == 2:
                continue
            if args.character and spec.get('characterId') != args.character:
                continue
            roots.append((request.parent.name, request))
    if args.ids:
        selected = set(args.ids)
        roots = [root for root in roots if root[0] in selected]
    return roots


def cmd_coordinator(args: argparse.Namespace) -> int:
    out = output_dir(args, 'coordinator')
    if not prepare(args, out, k0=False):
        return 1
    roots = coordinator_roots(args)
    if not roots:
        print('no roots selected', file=sys.stderr)
        return 2
    baseline = Path(args.off_baseline).resolve() if args.off_baseline else None
    modes = ['Off', 'On']
    if baseline:
        modes = ['On']
        for name, _ in roots:
            source = baseline / name / 'Off'
            target = out / name / 'Off'
            if not source.is_dir():
                print(f'baseline missing {source}', file=sys.stderr)
                return 2
            if not target.exists():
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copytree(source, target)
    if args.modes:
        modes = [mode.strip() for mode in args.modes.split(',') if mode.strip()]
    print(f'coordinator: {len(roots)} roots, modes={modes}, jobs={args.jobs}, budget={args.budget_ms}ms', flush=True)

    def env_for(mode: str):
        def build(_: Root) -> dict[str, str]:
            env = baseline_environment()
            env['OFFLINE_HARNESS_WARMUP_SEARCHES'] = str(args.warmups)
            env['OFFLINE_HARNESS_FLASH_MODE'] = mode
            return env
        return build

    total = len(roots) * len(modes)
    done = 0
    started = time.time()

    def run_one(name: str, request: Path, mode: str) -> None:
        outdir = out / name / mode
        outdir.mkdir(parents=True, exist_ok=True)
        command = [args.dotnet, str(args.harness), '--request', str(request), '--milestone', 'M2',
                   '--search-mode', 'Coordinator', '--use-portfolio', '--profile', 'Medium',
                   '--budget-ms', str(args.budget_ms), '--out', str(outdir)]
        run_command(command, cwd=ROOT, env=env_for(mode)(None), timeout=1800, log=outdir / 'stdout.txt')

    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as executor:
        futures = []
        for name, request in roots:
            for mode in modes:
                futures.append(executor.submit(run_one, name, request, mode))
        for future in concurrent.futures.as_completed(futures):
            future.result()
            done += 1
            print(f'[{done}/{total}] {time.time() - started:7.1f}s', flush=True)
    print(coordinator_table(out))
    print(strategic_table(out))
    if args.compare:
        qcompare(out, Path(args.compare).resolve(), args.compare_mode)
    return 0


def survey_spec(character: str, index: int, act: int) -> dict:
    return {
        'schemaVersion': 1,
        'seed': f'FLASH-DECK-{character}-{index:02d}',
        'characterId': character,
        'ascension': 10,
        'actIndex': act,
        'encounterKind': SURVEY_KINDS[index % 3],
        'includeStartingDeck': True,
        'includeStartingRelics': True,
        'includeAscendersBane': True,
        'characterCards': {'count': 6 + index % 7, 'upgradeLevels': 1},
        'colorlessCards': {'count': index % 3},
        'relics': {'count': 2 + index % 4},
        'potions': {'count': index % 3},
        'mode': 'Setup',
    }


def cmd_survey(args: argparse.Namespace) -> int:
    out = output_dir(args, f'survey-{args.character.lower()}-{args.act if args.act >= 0 else "mixed"}')
    out.mkdir(parents=True, exist_ok=True)
    roots = []
    for index in range(args.count):
        act = args.act if args.act >= 0 else index % 2
        identifier = f'{args.character}-{index:02d}'
        directory = out / identifier
        directory.mkdir(parents=True, exist_ok=True)
        spec = survey_spec(args.character, index, act)
        spec_path = directory / 'spec.json'
        spec_path.write_text(json.dumps(spec, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        request = {'schemaVersion': 1, 'scenarioId': identifier, 'cards': [],
                   'generatedScenarioPath': _relative(spec_path)}
        request_path = directory / 'request.json'
        request_path.write_text(json.dumps(request, ensure_ascii=False) + '\n', encoding='utf-8')
        roots.append(Root(identifier, character=args.character, act_index=act, request=request_path,
                          walks=1, category='survey', category_index=index, spec=spec))
    lines = []
    for root in roots:
        lines.append(f'{root.name} act={root.act_index} kind={root.spec["encounterKind"]}')
    if args.run:
        if not prepare(args, out, k0=False):
            return 1
        for root in roots:
            outdir = out / root.name
            env = baseline_environment()
            env['OFFLINE_HARNESS_FLASH_DIFF'] = '1'
            env['OFFLINE_HARNESS_FLASH_DIFF_WALKS'] = str(args.walks)
            command = harness_command(args.harness, root, outdir, dotnet=args.dotnet)
            code = run_command(command, cwd=ROOT, env=env, timeout=300, log=outdir / 'stdout.txt')
            first = 'NO-DIFF-OUTPUT'
            if (outdir / 'flash-diff.txt').is_file():
                first = (outdir / 'flash-diff.txt').read_text(encoding='utf-8', errors='replace').splitlines()[0]
            lines.append(f'{root.name} exit={code} {first}')
    write_summary(out / 'SUMMARY.txt', lines + ['DONE'])
    print(f'wrote {len(roots)} roots under {out}')
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)

    build = sub.add_parser('build', help='Release build, OfflineSearchHarness and K0')
    build.add_argument('--label', default=None)
    build.add_argument('--out', type=Path, default=None)
    build.add_argument('--dotnet', default=DOTNET)
    build.add_argument('--harness', type=Path, default=HARNESS)
    build.add_argument('--no-k0', action='store_true')
    build.set_defaults(func=cmd_build)

    diff = sub.add_parser('diff', help='K1/K2 differential regression over the full root set')
    add_common(diff)
    add_root_options(diff)
    diff.add_argument('--baseline', type=Path, default=None,
                      help='previous SUMMARY.txt; compare failures and pureRefMismatch row by row')
    diff.set_defaults(func=cmd_diff)

    scan = sub.add_parser('scan', help='Flash solve plus candidate-route replay scan')
    add_common(scan)
    add_root_options(scan)
    scan.add_argument('--candidates', type=int, default=8, help='OFFLINE_HARNESS_FLASH_CANDIDATE_SCAN (default 8)')
    scan.add_argument('--budget-ms', type=int, default=10000, help='per-root search budget (default 10000)')
    scan.set_defaults(func=cmd_scan)

    bench = sub.add_parser('bench', help='48-root quality benchmark (24 encounters x 2 seeds)')
    add_common(bench)
    bench.add_argument('--budget-ms', type=int, default=10000, help='per-root search budget (default 10000)')
    bench.add_argument('--seeds', nargs='+', default=None, help='default FLASHM1 FLASHM2')
    bench.add_argument('--encounter', action='append', default=[], help='limit to these encounters; repeatable')
    bench.set_defaults(func=cmd_bench)

    coordinator = sub.add_parser('coordinator', help='Off/On coordinator comparison (79 default roots)')
    add_common(coordinator)
    coordinator.add_argument('--quality-dir', type=Path, default=QUALITY,
                             help='four-character quality corpus (default coverage/corpora/flash-quality)')
    coordinator.add_argument('--deck-dir', type=Path, default=DECKS,
                             help='Ironclad deck corpus (default coverage/corpora/flash-decks)')
    coordinator.add_argument('--character', default='IRONCLAD', help='deck-dir character filter; empty disables')
    coordinator.add_argument('--include-act3-decks', action='store_true',
                             help='also run flash-decks roots with actIndex 2')
    coordinator.add_argument('--ids', nargs='+', default=None, help='run only these directory names')
    coordinator.add_argument('--modes', default=None, help='default Off,On; with --off-baseline default On')
    coordinator.add_argument('--off-baseline', type=Path, default=None,
                             help='previous result directory whose <id>/Off is reused')
    coordinator.add_argument('--budget-ms', type=int, default=120000)
    coordinator.add_argument('--warmups', type=int, default=1, help='OFFLINE_HARNESS_WARMUP_SEARCHES (default 1)')
    coordinator.add_argument('--compare', type=Path, default=None,
                             help='other coordinator output; run the per-root quality comparison')
    coordinator.add_argument('--compare-mode', default='On', choices=('Off', 'On'))
    coordinator.set_defaults(func=cmd_coordinator)

    survey = sub.add_parser('survey', help='generate generated-deck corpora in the deck-survey.sh format')
    add_common(survey)
    survey.add_argument('--character', required=True, help='IRONCLAD/SILENT/DEFECT/REGENT/NECROBINDER')
    survey.add_argument('--count', type=int, default=30)
    survey.add_argument('--act', type=int, default=-1,
                        help='fixed actIndex; -1 alternates 0/1 like deck-survey.sh, 2 builds Act 3')
    survey.add_argument('--run', action='store_true', help='also run a 1-walk differential probe per root')
    survey.add_argument('--walks', type=int, default=1, help='walks for --run (default 1)')
    survey.set_defaults(func=cmd_survey)
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if getattr(args, 'shards', 1) < 1:
        parser.error('--shards must be >= 1')
    if getattr(args, 'jobs', 1) < 1:
        parser.error('--jobs must be >= 1')
    return args.func(args)


if __name__ == '__main__':
    sys.exit(main())
