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
import threading
import time
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
HARNESS = ROOT / ".local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll"
DEFAULT_OUT = ROOT / ".local/flash-regression"
FIXTURES = ROOT / "coverage/fixtures/cards"
FALLBACK_FIXTURE = ROOT / "coverage/fixtures/search/flash-fallback-soulbound-power.json"
QUALITY = ROOT / "coverage/corpora/flash-quality"
KERNEL_CHECKS = ROOT / "tools/testing/checks/FlashKernelChecks"
HEADLESS_SCRIPT = ROOT / "tools/testing/run-unattended-test.sh"
DOTNET = shutil.which("dotnet") or "dotnet"

SURVEY_KINDS = ('Monster', 'Elite', 'Boss')
FLASH_LOG_LINE = re.compile(
    r"\[CombatSolver/Flash\] (\S+) purpose=PrimaryBeam (?:incumbent=\S+ )?flash=\[status=(\S+) ms=(\d+)"
)

HEADLESS_CHARACTERS = ('IRONCLAD', 'SILENT', 'DEFECT', 'REGENT', 'NECROBINDER')
HEADLESS_INDEXES = (0, 1, 2, 9, 10, 11, 19, 20)
DEFAULT_HEADLESS_IDS = tuple(f'{character}-{index:03d}'
                            for character in HEADLESS_CHARACTERS for index in HEADLESS_INDEXES)
HEADLESS_ARTIFACTS = ('result.json', 'runtime-events.json', 'search-result.json', 'difference.json',
                      'policy.json', 'timings.json')
NATIVE_OUTCOME_RE = re.compile(
    r'NativeOutcome:combatEnded=(True|False):turn=(\d+):hp=(-?\d+):maxHp=(\d+)')
UNEXPECTED_REPLANS_RE = re.compile(r'UnexpectedReplans:(\d+)')
BUG_REPORT_CHECK_RE = re.compile(r'BugReport[^:]*:([^:]+[.]zip)')
SEARCH_FAILED_REASON_RE = re.compile(r'FLASH_SEARCH_FAILED reason=(\S+)')


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


def fixture_roots(walks: int) -> list[Root]:
    roots = []
    for index, path in enumerate(sorted(FIXTURES.glob('flash-*.json'))):
        data = json.loads(path.read_text(encoding='utf-8-sig'))
        roots.append(Root(path.stem, data['encounterId'], str(data['seed']), 'IRONCLAD', 0, path, walks,
                          'fixture', index))
    return roots


def quality_roots(walks: int) -> list[Root]:
    roots = []
    for index, request in enumerate(sorted(QUALITY.glob('*/request.json'))):
        spec = json.loads((request.parent / 'spec.json').read_text(encoding='utf-8-sig'))
        roots.append(Root(f"quality-{request.parent.name}", character=spec['characterId'],
                          act_index=int(spec.get('actIndex', 0)), request=request, walks=walks,
                          category='quality', category_index=index, spec=spec))
    return roots


def resolve_roots(args: argparse.Namespace) -> list[Root]:
    only = args.only
    roots: list[Root] = []
    if only in ('all', 'fixtures'):
        roots += fixture_roots(getattr(args, 'walks', 32))
    if only in ('all', 'quality'):
        roots += quality_roots(getattr(args, 'quality_walks', 48))
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
                timeout: float | None = None, log: Path | None = None, append: bool = False) -> int:
    try:
        if log is not None:
            log.parent.mkdir(parents=True, exist_ok=True)
            with log.open('ab' if append else 'wb') as stream:
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


def fresh_dir(path: Path) -> Path:
    """Recreate a per-root output directory. The harness appends one log folder per process, so a reused
    directory would mix an earlier run's diagnostics into this run's evidence."""
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(parents=True)
    return path


def run_roots(roots: list[Root], out: Path, jobs: int, command_for, *, env_for, timeout: float) -> list[str]:
    results: dict[str, str] = {}

    def work(root: Root) -> tuple[str, str]:
        outdir = fresh_dir(out / root.name)
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
    for key in ('failed', 'walkFailed', 'actionSetFailed'):
        found = re.search(rf'\b{key}=(\d+)', summary)
        if not found or int(found.group(1)) != 0:
            return True
    return False


def scan_line_failed(line: str) -> bool:
    match = re.match(r'\S+ exit=(\d+) ', line)
    if not match or int(match.group(1)) != 0:
        return True
    admitted = re.search(r'\badmitted=(true|false)\b', line)
    mismatches = re.search(r'\bmismatches=(\d+)', line)
    routes = re.search(r'\broutes=(\d+)', line)
    return (admitted is None or admitted.group(1) != 'true'
            or mismatches is None or int(mismatches.group(1)) != 0
            or routes is None)


def compare_baseline(new_path: Path, base_path: Path) -> tuple[list[str], str]:
    fail_keys = ('failed', 'walkFailed', 'actionSetFailed', 'scoreFailed', 'boundFailed',
                 'traitFailed', 'pocketwatchFailed', 'declinedFailed', 'unsupported')
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


def add_common(parser: argparse.ArgumentParser, *, no_k0: bool = True) -> None:
    parser.add_argument('--label', default=None, help='output directory name under .local/flash-regression')
    parser.add_argument('--out', type=Path, default=None, help='explicit output directory (wins over --label)')
    parser.add_argument('--jobs', type=int, default=4, help='maximum concurrent harness processes (default 4)')
    parser.add_argument('--dotnet', default=DOTNET, help='dotnet executable')
    parser.add_argument('--harness', type=Path, default=HARNESS, help='OfflineSearchHarness.dll')
    parser.add_argument('--skip-build', action='store_true', help='use the existing Release build')
    if no_k0:
        parser.add_argument('--no-k0', action='store_true', help='skip FlashKernelChecks')


def output_dir(args: argparse.Namespace, suffix: str) -> Path:
    if args.out:
        return args.out.resolve()
    label = args.label or suffix
    return (DEFAULT_OUT / label).resolve()


def add_root_options(parser: argparse.ArgumentParser, *, walks: bool = True,
                     quality_walks: bool = True) -> None:
    if walks:
        parser.add_argument('--walks', type=int, default=32, help='random walks per root (default 32)')
    if quality_walks:
        parser.add_argument('--quality-walks', type=int, default=48,
                            help='walks per quality-corpus root (default 48)')
    parser.add_argument('--only', choices=('all', 'fixtures', 'quality'), default='all')
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
        env['OFFLINE_HARNESS_FLASH_DIFF_STEPS'] = str(args.steps)
        if args.debug:
            env['OFFLINE_HARNESS_FLASH_DEBUG'] = '1'
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
        admitted = solve.get('flashAdmitted') is True
        rejection = str(solve.get('flashRejection') or '-')[:160].replace('\n', ' ')
        error = str(solve.get('error') or data.get('error') or '')[:160].replace('\n', ' ')
        return (f"admitted={str(admitted).lower()} rejection={rejection} "
                f"routes={scan.get('routes', 0)} mismatches={scan.get('mismatches', 0)} error={error}")
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
        outdir = fresh_dir(out / root.name)
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


def quality_key(quality: dict) -> tuple:
    """The good/same/worse comparison key: RouteQualityPolicy.PotionPolicy without the end turn.

    Win -> survive -> fewer death-save uses -> smaller (strategic deficit + potion strategic cost).
    A route that only ends earlier or later at the same HP and potion cost is not better or worse
    for the player, so the combat-end turn is reported separately (``turn_only``) and never decides
    the mark. The old (won, strategicHpDeficit, turn) key omitted the potion cost and the
    survival/death-save terms; a potion route could therefore look better while costing more.
    """
    return (
        0 if quality.get('won') else 1,
        0 if quality.get('survives', True) else 1,
        int(quality.get('deathSaveUseCount') or 0),
        solver_cost(quality),
    )


def solver_key(quality: dict) -> tuple:
    """The full solver key (quality key, then earlier combat end) for run-to-run identity checks."""
    turn = quality.get('combatEndedTurn')
    return quality_key(quality) + (turn if turn is not None else 99,)


def _turn_only(off_quality: dict, on_quality: dict) -> bool:
    """Same quality key, different combat-end turn: informational, counted as same."""
    return (quality_key(off_quality) == quality_key(on_quality)
            and off_quality.get('combatEndedTurn') != on_quality.get('combatEndedTurn'))


def solver_cost(quality: dict) -> int:
    return int(quality.get('strategicHpDeficit') or 0) + int(quality.get('potionStrategicCost') or 0)


def flash_failed(run_dir: Path) -> bool:
    """Whether the On run emitted the stable FLASH_SEARCH_FAILED event (log scan, read-only)."""
    log_dir = run_dir / 'On' / 'logs'
    if not log_dir.is_dir():
        return False
    needle = b'FLASH_SEARCH_FAILED'
    for path in log_dir.rglob('process.jsonl'):
        with path.open('rb') as stream:
            while chunk := stream.read(1 << 20):
                if needle in chunk:
                    return True
    return False


def spec_metadata(corpus: Path | None, root_name: str) -> tuple[str, str, str]:
    if corpus is None:
        return 'unknown', 'unknown', 'unknown'
    path = corpus / root_name / 'spec.json'
    if not path.is_file():
        return 'unknown', 'unknown', 'unknown'
    spec = json.loads(path.read_text(encoding='utf-8-sig'))
    character = str(spec.get('characterId') or 'unknown')
    act = spec.get('actIndex')
    act_label = f'Act {int(act) + 1}' if isinstance(act, int) else 'unknown'
    kind = str(spec.get('encounterKind') or 'unknown')
    return character, act_label, kind


class _GroupRow:
    __slots__ = ('roots', 'good', 'same', 'worse', 'turn_only', 'both_won', 'cost_delta', 'on_only',
                 'off_only', 'off_wins', 'on_wins', 'off_wall', 'on_wall', 'missing', 'failed')

    def __init__(self) -> None:
        self.roots = 0
        self.good = self.same = self.worse = self.turn_only = 0
        self.both_won = self.cost_delta = 0
        self.on_only = self.off_only = 0
        self.off_wins = self.on_wins = 0
        self.off_wall = self.on_wall = 0.0
        self.missing = self.failed = 0


def _group_table(title: str, groups: dict[str, _GroupRow]) -> list[str]:
    lines = [f'{title}']
    lines.append(f'  {"key":20} {"roots":>5} {"good":>4} {"same":>4} {"worse":>5} {"turn_only":>9} '
                 f'{"both_won":>8} {"cost_delta":>10} {"on_only":>7} {"off_only":>8} {"off_won":>7} {"on_won":>6} '
                 f'{"off_wall":>9} {"on_wall":>9} {"missing":>7} {"failed":>6}')
    for key in sorted(groups):
        row = groups[key]
        lines.append(f'  {key:20} {row.roots:5d} {row.good:4d} {row.same:4d} {row.worse:5d} '
                     f'{row.turn_only:9d} {row.both_won:8d} {row.cost_delta:+10d} {row.on_only:7d} {row.off_only:8d} '
                     f'{row.off_wins:7d} {row.on_wins:6d} '
                     f'{row.off_wall:9.2f} {row.on_wall:9.2f} {row.missing:7d} {row.failed:6d}')
    return lines


def quality_table(run_dir: Path, corpus: Path | None = None) -> str:
    """Read-only Off/On quality table under the solver's own PotionPolicy comparison key.

    Groups roots by the corpus spec.json fields (characterId, actIndex, encounterKind). The cost
    delta is sum(On strategicHpDeficit + On potionStrategicCost - the same Off sum) over the roots
    that both modes won (``both_won``); negative means On improved. A lost combat's cost is not
    comparable with a won one, so roots won by only one mode are counted as ``on_only``/``off_only``
    instead. Potion-cost-different roots are counted separately so a route that spends a potion is
    not mistaken for a better route.
    """
    run_dir = Path(run_dir)
    roots = sorted(path for path in run_dir.iterdir()
                   if path.is_dir() and ((path / 'Off').is_dir() or (path / 'On').is_dir()))
    by_character: dict[str, _GroupRow] = {}
    by_act: dict[str, _GroupRow] = {}
    by_kind: dict[str, _GroupRow] = {}
    total = _GroupRow()
    compared = 0
    potion_cost_diff = 0
    worse: list[str] = []
    missing_roots: list[str] = []
    failed_roots: list[str] = []

    def add(group: dict[str, _GroupRow], key: str) -> _GroupRow:
        return group.setdefault(key, _GroupRow())

    for root in roots:
        character, act, kind = spec_metadata(corpus, root.name)
        row_char = add(by_character, character)
        row_act = add(by_act, act)
        row_kind = add(by_kind, kind)
        for row in (row_char, row_act, row_kind, total):
            row.roots += 1
        try:
            off_wall, off_q, _ = load_quality(root / 'Off')
            on_wall, on_q, _ = load_quality(root / 'On')
        except (OSError, ValueError, KeyError):
            for row in (row_char, row_act, row_kind, total):
                row.missing += 1
            missing_roots.append(root.name)
            continue
        compared += 1
        failed = flash_failed(root)
        if failed:
            failed_roots.append(root.name)
        off_key = quality_key(off_q)
        on_key = quality_key(on_q)
        mark = '+' if on_key < off_key else ('-' if on_key > off_key else '=')
        turn_only = _turn_only(off_q, on_q)
        off_cost = solver_cost(off_q)
        on_cost = solver_cost(on_q)
        delta = on_cost - off_cost
        potion_diff = int(off_q.get('potionStrategicCost') or 0) != int(
            on_q.get('potionStrategicCost') or 0)
        if potion_diff:
            potion_cost_diff += 1
        off_won = 1 if off_q.get('won') else 0
        on_won = 1 if on_q.get('won') else 0
        for row in (row_char, row_act, row_kind, total):
            if mark == '+':
                row.good += 1
            elif mark == '-':
                row.worse += 1
            else:
                row.same += 1
            row.turn_only += turn_only
            if off_won and on_won:
                row.both_won += 1
                row.cost_delta += delta
            elif on_won:
                row.on_only += 1
            elif off_won:
                row.off_only += 1
            row.off_wins += off_won
            row.on_wins += on_won
            row.off_wall += off_wall
            row.on_wall += on_wall
            if failed:
                row.failed += 1
        if mark == '-':
            worse.append(f'{root.name}: off={off_key} cost={off_cost} -> on={on_key} '
                         f'cost={on_cost} potion={int(off_q.get("potionStrategicCost") or 0)}'
                         f'->{int(on_q.get("potionStrategicCost") or 0)}')

    lines = [
        f'run_dir={run_dir} corpus={corpus if corpus is not None else "unknown"}',
        f'total roots={total.roots} compared={compared} good={total.good} same={total.same} '
        f'worse={total.worse} turn_only={total.turn_only} both_won={total.both_won} '
        f'cost_delta={total.cost_delta:+d} on_only_wins={total.on_only} off_only_wins={total.off_only} potion_cost_diff={potion_cost_diff} '
        f'off_won={total.off_wins} on_won={total.on_wins} '
        f'off_wall={total.off_wall:.2f}s on_wall={total.on_wall:.2f}s '
        f'missing={total.missing} failed={total.failed}',
        '',
    ]
    lines += _group_table('by character', by_character)
    lines.append('')
    lines += _group_table('by act', by_act)
    lines.append('')
    lines += _group_table('by encounter', by_kind)
    lines.append('')
    lines.append('worse roots:')
    lines += [f'  {line}' for line in worse] or ['  (none)']
    if missing_roots:
        lines.append('missing roots: ' + ', '.join(missing_roots))
    if failed_roots:
        lines.append('FLASH_SEARCH_FAILED roots: ' + ', '.join(failed_roots))
    return '\n'.join(lines)


def coordinator_table(out: Path) -> str:
    rows: list[str] = []
    total = {'Off': 0.0, 'On': 0.0}
    better = same = worse = adopted = lost = 0
    both_won = on_only = off_only = turn_only = 0
    potion_cost_diff = 0

    for root in sorted(path for path in out.iterdir() if path.is_dir()):
        try:
            (off_wall, off_quality, _), (on_wall, on_quality, flash) = (
                load_quality(root / 'Off'), load_quality(root / 'On'))
        except (OSError, ValueError, KeyError) as error:
            rows.append(f'{root.name:40} ERROR {error}')
            continue
        off_key = quality_key(off_quality)
        on_key = quality_key(on_quality)
        mark = '=' if off_key == on_key else ('+' if on_key < off_key else '-')
        better += mark == '+'
        same += mark == '='
        worse += mark == '-'
        turn_only += _turn_only(off_quality, on_quality)
        adopted += flash.startswith('FLASH_ADOPTED')
        total['Off'] += off_wall
        total['On'] += on_wall
        off_cost = solver_cost(off_quality)
        on_cost = solver_cost(on_quality)
        if off_quality.get('won') and on_quality.get('won'):
            both_won += 1
            lost += on_cost - off_cost
        elif on_quality.get('won'):
            on_only += 1
        elif off_quality.get('won'):
            off_only += 1
        if int(off_quality.get('potionStrategicCost') or 0) != int(
                on_quality.get('potionStrategicCost') or 0):
            potion_cost_diff += 1
        rows.append(f"{root.name:40} {mark} off cost={off_cost:>3} "
                    f"strategic={off_quality['strategicHpDeficit']:>3} "
                    f"potion={int(off_quality.get('potionStrategicCost') or 0):>3} "
                    f"turn={off_quality['combatEndedTurn']!s:>3} wall={off_wall:6.2f}s | "
                    f"on cost={on_cost:>3} strategic={on_quality['strategicHpDeficit']:>3} "
                    f"potion={int(on_quality.get('potionStrategicCost') or 0):>3} "
                    f"turn={on_quality['combatEndedTurn']!s:>3} wall={on_wall:6.2f}s {flash}")
    rows.append(f"on better={better} same={same} worse={worse} turn_only={turn_only} "
                f"both_won={both_won} cost_delta={lost:+d} "
                f"on_only_wins={on_only} off_only_wins={off_only} "
                f"potion_cost_diff={potion_cost_diff} adopted={adopted} "
                f"off_wall={total['Off']:.1f}s on_wall={total['On']:.1f}s")
    (out / 'TABLE.txt').write_text('\n'.join(rows) + '\n', encoding='utf-8')
    return rows[-1]


def strategic_table(out: Path) -> str:
    lines: list[str] = []
    for root in sorted(path for path in out.iterdir() if path.is_dir()):
        try:
            off = json.loads((root / 'Off/quality.json').read_text(encoding='utf-8-sig'))['quality']
            on = json.loads((root / 'On/quality.json').read_text(encoding='utf-8-sig'))['quality']
        except (OSError, ValueError, KeyError):
            continue
        off_key = quality_key(off)
        on_key = quality_key(on)
        mark = '=' if off_key == on_key else ('+' if on_key < off_key else '-')
        lines.append(f"{root.name} {mark} cost {solver_cost(off)}->{solver_cost(on)} "
                     f"strategic {off['strategicHpDeficit']}->{on['strategicHpDeficit']} "
                     f"potion {int(off.get('potionStrategicCost') or 0)}->"
                     f"{int(on.get('potionStrategicCost') or 0)} "
                     f"deathsaves {int(off.get('deathSaveUseCount') or 0)}->"
                     f"{int(on.get('deathSaveUseCount') or 0)} "
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
        old_key = solver_key(old)
        new_key = solver_key(new)
        if old_key != new_key:
            diff += 1
            print(root.name, old_key, '->', new_key)
    print(f'total {total} diff {diff}')
    return total, diff


def cmd_fallback(args: argparse.Namespace) -> int:
    """On-mode admission fallback contract on a root with Flash-unsupported content.

    One On harness process runs the Off baseline, the On fallback and a non-admission rejection
    through the real dispatch point; the check JSON reports route/quality/work equality and the
    stable event lines. The host log of the normal On search must contain FLASH_FALLBACK and no
    FLASH_SEARCH_FAILED.
    """
    out = output_dir(args, 'fallback')
    if not prepare(args, out, k0=True):
        return 1
    outdir = fresh_dir(out / 'On')
    env = baseline_environment()
    env['OFFLINE_HARNESS_FLASH_MODE'] = 'On'
    env['OFFLINE_HARNESS_FLASH_FALLBACK_CHECKS'] = '1'
    command = [args.dotnet, str(args.harness), '--request', str(args.fixture),
               '--label', 'fallback', '--profile', args.profile, '--beam', str(args.beam),
               '--nodes', str(args.nodes), '--budget-ms', str(args.budget_ms),
               '--search-mode', 'Coordinator', '--out', str(outdir)]
    code = run_command(command, cwd=ROOT, env=env, timeout=args.timeout,
                       log=outdir / 'stdout.txt')
    checks: dict = {}
    check_path = outdir / 'flash-fallback-checks.json'
    if check_path.is_file():
        checks = json.loads(check_path.read_text(encoding='utf-8-sig'))
    log_text = ''
    logs = outdir / 'logs'
    if logs.is_dir():
        log_text = '\n'.join(path.read_text(encoding='utf-8', errors='replace')
                             for path in logs.rglob('process.jsonl'))
    fallback_count = log_text.count('FLASH_FALLBACK')
    fallback_failed = 'FLASH_SEARCH_FAILED' in log_text
    passed = (code == 0
              and checks.get('status') == 'Passed'
              and checks.get('routeSame') is True
              and checks.get('qualitySame') is True
              and checks.get('workSame') is True
              and checks.get('sensitivityDiffers') is True
              and checks.get('publishedFallbackBlocked') is True
              and len(checks.get('fallbackLines') or []) == 1
              and checks.get('fallbackNoticeCount') == 1
              and checks.get('rejectionToken') == 'admission-unsupported-request'
              and fallback_count == 1
              and not fallback_failed)
    off_work = checks.get('offWork') or {}
    flash_work = checks.get('sensitivityWork') or {}
    line = (f"fallback exit={code} status={checks.get('status', 'Missing')} "
            f"route={checks.get('routeSame')} quality={checks.get('qualitySame')} "
            f"work={checks.get('workSame')} fallback_lines={fallback_count} "
            f"off_work={off_work.get('ExpandedNodes', '-')}/{off_work.get('TransitionCount', '-')} "
            f"flash_profile_work={flash_work.get('ExpandedNodes', '-')}/"
            f"{flash_work.get('TransitionCount', '-')} sensitivity={checks.get('sensitivityDiffers')} "
            f"published_blocked={checks.get('publishedFallbackBlocked')} "
            f"failed={fallback_failed} rejection={checks.get('rejectionToken', '-')}")
    write_summary(out / 'SUMMARY.txt', [line, 'DONE'])
    print(line)
    if checks.get('fallbackLines'):
        for fallback in checks['fallbackLines']:
            print(f'  {fallback}')
    print(f'wrote {out / "SUMMARY.txt"}')
    return 0 if passed else 1


def cmd_compat(args: argparse.Namespace) -> int:
    """Run the persisted Flash-mode compatibility contract (removed Shadow -> Off; On unchanged)."""
    out = output_dir(args, 'compat')
    if not prepare(args, out, k0=False):
        return 1
    log = out / 'compat.txt'
    code = run_command([args.dotnet, str(args.harness), '--check-flash-mode-compat'],
                       cwd=ROOT, env=baseline_environment(), timeout=120, log=log)
    text = log.read_text(encoding='utf-8', errors='replace') if log.is_file() else ''
    print(text.strip())
    return 0 if code == 0 and 'status=Passed' in text else 1


def cmd_table(args: argparse.Namespace) -> int:
    corpus = args.corpus.resolve() if args.corpus else None
    print(quality_table(args.run_dir.resolve(), corpus))
    return 0


def single_mode_table(out: Path, mode: str) -> str:
    """Read-only per-root quality for a coordinator run that executed only one mode."""
    rows: list[str] = []
    total_wall = 0.0
    total_cost = 0
    for root in sorted(path for path in out.iterdir() if path.is_dir()):
        try:
            wall, quality, _ = load_quality(root / mode)
        except (OSError, ValueError, KeyError) as error:
            rows.append(f'{root.name:40} MISSING {error}')
            continue
        total_wall += wall
        total_cost += solver_cost(quality)
        rows.append(f"{root.name:40} cost={solver_cost(quality):>3} "
                    f"strategic={quality['strategicHpDeficit']:>3} "
                    f"potion={int(quality.get('potionStrategicCost') or 0):>3} "
                    f"turn={quality['combatEndedTurn']!s:>3} "
                    f"won={str(bool(quality.get('won'))).lower():5} wall={wall:6.2f}s")
    summary = f'mode={mode} roots={len(rows)} cost_total={total_cost} wall={total_wall:.1f}s'
    rows.append(summary)
    (out / 'TABLE.txt').write_text('\n'.join(rows) + '\n', encoding='utf-8')
    return summary


def coordinator_missing(out: Path, roots: list[tuple[str, Path]], modes: list[str]) -> list[str]:
    return [name for name, _ in roots
            if any(not (out / name / mode / 'quality.json').is_file() for mode in modes)]


def coordinator_failed(out: Path, roots: list[tuple[str, Path]], modes: list[str]) -> list[str]:
    if 'On' not in modes:
        return []
    return [name for name, _ in roots if flash_failed(out / name)]


def coordinator_roots(args: argparse.Namespace) -> list[tuple[str, Path]]:
    roots: list[tuple[str, Path]] = []
    if args.quality_dir:
        roots += [(path.parent.name, path) for path in sorted(Path(args.quality_dir).glob('*/request.json'))]
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
        outdir = fresh_dir(out / name / mode)
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
    if args.compare:
        qcompare(out, Path(args.compare).resolve(), args.compare_mode)
    if len(modes) == 1:
        print(single_mode_table(out, modes[0]))
    else:
        print(coordinator_table(out))
        print(strategic_table(out))
        corpus = Path(args.quality_dir).resolve() if args.quality_dir else None
        table = quality_table(out, corpus)
        (out / 'TABLE-GROUPS.txt').write_text(table + '\n', encoding='utf-8')
        print(table)
    missing = coordinator_missing(out, roots, modes)
    failed = coordinator_failed(out, roots, modes)
    print(f'missing_roots={len(missing)} failed_roots={len(failed)}')
    return 1 if missing or failed else 0


def survey_spec(character: str, index: int, act: int) -> dict:
    """One quality-corpus deck. The encounter kind follows index % 3; potions and colorless cards step with
    index // 3, so every encounter kind sees decks with 0, 1 and 2 of each."""
    return {
        'schemaVersion': 1,
        'seed': f'FLASH-QUALITY-{character}-{index:03d}',
        'characterId': character,
        'ascension': 10,
        'actIndex': act,
        'encounterKind': SURVEY_KINDS[index % 3],
        'includeStartingDeck': True,
        'includeStartingRelics': True,
        'includeAscendersBane': True,
        'characterCards': {'count': 6 + index % 7, 'upgradeLevels': 1},
        'colorlessCards': {'count': (index // 3 + 1) % 3},
        'relics': {'count': 2 + index % 4},
        'potions': {'count': (index // 3) % 3},
        'mode': 'Setup',
    }


def survey_act(index: int, count: int) -> int:
    """Consecutive thirds of the indexes take Acts 1, 2 and 3 (actIndex 0/1/2)."""
    return min(2, index * 3 // count)


def cmd_survey(args: argparse.Namespace) -> int:
    out = output_dir(args, f'survey-{args.character.lower()}')
    out.mkdir(parents=True, exist_ok=True)
    roots = []
    for index in range(args.count):
        act = survey_act(index, args.count)
        identifier = f'{args.character}-{index:03d}'
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


def cmd_trace(args: argparse.Namespace) -> int:
    """Run one request with the offline Flash route trace enabled.

    The target route is an existing-engine PlanAction list (for example a coordinator
    `route.json`). The harness reconstructs the same request, maps the route onto Flash
    actions, and reports every generation/dedup/rank/lane/bound/prune step. The existing
    solver runs the same request with a state-key path observer.
    """
    request = args.request
    if request is None:
        request = args.quality_dir / args.id / 'request.json'
    if not request.is_file():
        raise SystemExit(f'missing request: {request}')
    route = args.route or args.quality_dir / args.id / 'route.json'
    if not route.is_file():
        raise SystemExit(f'missing route: {route}')
    out = args.out or (OUT / 'trace' / f"{args.id}-{args.purpose}")
    out.mkdir(parents=True, exist_ok=True)
    env = baseline_environment()
    env['OFFLINE_HARNESS_FLASH_TRACE_ROUTE'] = str(route.resolve())
    env['OFFLINE_HARNESS_FLASH_TRACE_PURPOSE'] = args.purpose
    env['OFFLINE_HARNESS_FLASH_TRACE_CONTINUATION'] = '1' if args.continuation else '0'
    env['OFFLINE_HARNESS_FLASH_TRACE_PREFIX'] = str(args.prefix)
    env['OFFLINE_HARNESS_FLASH_TRACE_BEAM'] = str(args.beam)
    env['OFFLINE_HARNESS_FLASH_TRACE_NODES'] = str(args.nodes)
    env['OFFLINE_HARNESS_FLASH_TRACE_SIDE'] = args.side
    env['OFFLINE_HARNESS_FLASH_TRACE_OUT'] = str((out / 'flash-trace.txt').resolve())
    if args.flags:
        env['OFFLINE_HARNESS_FLASH_TRACE_FLAGS'] = args.flags
    if args.max_potions is not None:
        env['OFFLINE_HARNESS_FLASH_TRACE_MAX_POTIONS'] = str(args.max_potions)
    if args.min_potions is not None:
        env['OFFLINE_HARNESS_FLASH_TRACE_MIN_POTIONS'] = str(args.min_potions)
    if args.potion_policy:
        env['OFFLINE_HARNESS_FLASH_TRACE_POTION_POLICY'] = args.potion_policy
    if args.incumbent_deficit is not None:
        env['OFFLINE_HARNESS_FLASH_TRACE_INCUMBENT_DEFICIT'] = str(args.incumbent_deficit)
        if args.incumbent_turn is None:
            raise SystemExit('--incumbent-deficit requires --incumbent-turn')
    if args.incumbent_turn is not None:
        env['OFFLINE_HARNESS_FLASH_TRACE_INCUMBENT_TURN'] = str(args.incumbent_turn)
    if args.reset_baseline:
        env['OFFLINE_HARNESS_FLASH_TRACE_RESET_BASELINE'] = '1'
    command = [args.dotnet, str(args.harness), '--request', str(request),
               '--label', args.id, '--profile', 'Medium', '--beam', str(args.beam),
               '--nodes', str(args.nodes), '--budget-ms', str(args.budget_ms),
               '--search-mode', 'Evaluate', '--out', str(out)]
    code = run_command(command, cwd=ROOT, env=env, timeout=args.timeout,
                       log=out / 'stdout.txt')
    print(f'exit={code} trace={out / "flash-trace.txt"}')
    trace = out / 'flash-trace.txt'
    if trace.is_file():
        for line in trace.read_text(encoding='utf-8').splitlines()[:80]:
            print(line)
    return 0 if code == 0 else 1

# ---------------------------------------------------------------------------
# headless: full-battle deployment on Linux headless instances
# ---------------------------------------------------------------------------


def _headless_read_json(path: Path):
    try:
        return json.loads(path.read_text(encoding='utf-8-sig'))
    except (OSError, ValueError):
        return None


def _headless_safe_label(value: str) -> str:
    name = re.sub(r'[^A-Za-z0-9._-]+', '-', value.strip()).strip('.-')
    return (name or 'headless')[:48]


def _headless_selected_ids(args: argparse.Namespace) -> tuple[list[str], list[str]]:
    raw: list[str] = []
    if args.ids:
        for token in args.ids:
            raw.extend(part.strip() for part in token.split(',') if part.strip())
    else:
        raw = list(DEFAULT_HEADLESS_IDS)
    selected: list[str] = []
    seen: set[str] = set()
    for identifier in raw:
        if identifier not in seen:
            seen.add(identifier)
            selected.append(identifier)
    corpus = args.quality_dir.resolve()
    missing = [identifier for identifier in selected
               if not (corpus / identifier / 'spec.json').is_file()]
    return selected, missing


def _headless_instance_root() -> Path:
    return (ROOT / '.local/headless-instances').resolve()


def _headless_remove_instance(instance_dir: Path) -> None:
    root = _headless_instance_root()
    target = instance_dir.resolve()
    if target.parent != root:
        raise RuntimeError(f'refusing to remove unexpected instance path: {target}')
    if target.exists():
        shutil.rmtree(target)


def _headless_environment(args: argparse.Namespace, instance_dir: Path, host_root: Path,
                          xdg_data_home: Path | None) -> dict[str, str]:
    env = baseline_environment()
    env['COMBATSOLVER_HEADLESS_HOST_ROOT'] = str(host_root)
    env['COMBATSOLVER_HEADLESS_ROOT'] = str(instance_dir)
    if xdg_data_home is not None:
        env['XDG_DATA_HOME'] = str(xdg_data_home)
    return env


def _headless_launcher(args: argparse.Namespace, instance_name: str) -> list[str]:
    command = ['bash', str(HEADLESS_SCRIPT),
               '--headless-instance', instance_name,
               '--headless-execution-mode', 'parallel',
               '--headless-cpu-reservation', str(args.headless_cpu_reservation),
               '--headless-memory-reservation-mib', str(args.headless_memory_reservation_mib)]
    for option, value in (('--sts2-game-root', args.sts2_game_root),
                          ('--ritsu-workshop-root', args.ritsu_workshop_root),
                          ('--combat-solver-build-dir', args.combat_solver_build_dir)):
        if value:
            command += [option, str(value)]
    return command


def _headless_write_settings(instance_dir: Path, mode: str) -> None:
    data = instance_dir / 'data/SlayTheSpire2'
    data.mkdir(parents=True, exist_ok=True)
    (data / 'combat_solver_settings.json').write_text(
        json.dumps({'flashSearchMode': mode}, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def _headless_clear_run_state(instance_dir: Path) -> None:
    data = instance_dir / 'data/SlayTheSpire2'
    data.mkdir(parents=True, exist_ok=True)
    for name in ('combat_solver_test_result.json', 'combat_solver_test_ready.json',
                 'runtime-events.json', 'search-result.json', 'difference.json',
                 'policy.json', 'timings.json'):
        try:
            (data / name).unlink()
        except FileNotFoundError:
            pass
    for name in ('combat-solver-routes', 'combat-solver-test-bug-reports'):
        shutil.rmtree(data / name, ignore_errors=True)


def _headless_build(args: argparse.Namespace, out: Path) -> tuple[bool, str]:
    out.mkdir(parents=True, exist_ok=True)
    log = out / 'build.txt'
    command = [args.dotnet, 'build', 'CombatSolver.csproj', '-c', 'Release',
               '-p:CopyModOnBuild=false', '-p:NuGetAudit=false', '-v', 'q', '-nologo']
    code = run_command(command, cwd=ROOT, log=log)
    if code != 0:
        return False, 'BUILD FAILED'
    return True, ''


def _headless_dll(args: argparse.Namespace) -> Path:
    if args.combat_solver_build_dir:
        return Path(args.combat_solver_build_dir).expanduser().resolve() / 'CombatSolver.dll'
    return ROOT / '.godot/mono/temp/bin/Release/CombatSolver.dll'


class _HeadlessEvidenceWatcher:
    """Copy instance-local evidence (report ZIPs and launcher/game logs) while the run is alive.

    The last run of a lane uses --cleanup-instance-on-exit, which deletes the whole instance as
    soon as the launcher exits. Polling during the run keeps the report bundle and logs without
    changing the launcher's cleanup contract.
    """

    def __init__(self, instance_dir: Path, evidence: Path) -> None:
        self.instance_dir = instance_dir
        self.evidence = evidence
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, daemon=True)

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._thread.join(timeout=10)

    def _loop(self) -> None:
        while not self._stop.is_set():
            self._copy_once()
            self._stop.wait(0.25)
        self._copy_once()

    def _copy_once(self) -> None:
        data = self.instance_dir / 'data/SlayTheSpire2'
        reports = data / 'combat-solver-test-bug-reports'
        if reports.is_dir():
            destination = self.evidence / 'reports'
            try:
                destination.mkdir(parents=True, exist_ok=True)
                for path in sorted(reports.glob('*.zip')):
                    target = destination / path.name
                    if target.exists() and target.stat().st_size == path.stat().st_size:
                        continue
                    temporary = destination / (path.name + '.tmp')
                    shutil.copy2(path, temporary)
                    if _headless_zip_valid(temporary):
                        os.replace(temporary, target)
                    else:
                        temporary.unlink(missing_ok=True)
            except OSError:
                pass
        logs = self.evidence / 'instance-logs'
        try:
            logs.mkdir(parents=True, exist_ok=True)
            for name in ('godot-headless.log', 'launcher.log'):
                source = self.instance_dir / name
                if not source.is_file():
                    continue
                target = logs / name
                if not target.exists() or target.stat().st_size != source.stat().st_size:
                    shutil.copy2(source, target)
        except OSError:
            pass
        for name in ('runtime-events.json', 'search-result.json', 'difference.json',
                     'policy.json', 'timings.json'):
            source = data / name
            if source.is_file() and not (self.evidence / name).exists():
                try:
                    shutil.copy2(source, self.evidence / name)
                except OSError:
                    pass
        for source, name in ((data / 'combat_solver_settings.json', 'instance-settings.json'),
                             (data / 'combat_solver_test_request.json', 'launcher-request.json')):
            if source.is_file() and not (self.evidence / name).exists():
                try:
                    shutil.copy2(source, self.evidence / name)
                except OSError:
                    pass
        result = data / 'combat_solver_test_result.json'
        if result.is_file() and not (self.evidence / 'result.json').exists():
            try:
                shutil.copy2(result, self.evidence / 'result.json')
            except OSError:
                pass


def _headless_zip_valid(path: Path) -> bool:
    try:
        with zipfile.ZipFile(path) as archive:
            return bool(archive.namelist())
    except (OSError, zipfile.BadZipFile):
        return False


def _headless_extract_zip(zip_path: Path, target: Path) -> None:
    if target.exists():
        shutil.rmtree(target)
    target.mkdir(parents=True)
    try:
        with zipfile.ZipFile(zip_path) as archive:
            root = target.resolve()
            for info in archive.infolist():
                member = (target / info.filename).resolve()
                if member != root and root not in member.parents:
                    continue
                archive.extract(info, target)
    except (OSError, zipfile.BadZipFile):
        pass


def _headless_copy_evidence(instance_dir: Path, evidence: Path) -> None:
    evidence.mkdir(parents=True, exist_ok=True)
    data = instance_dir / 'data/SlayTheSpire2'
    if data.is_dir():
        for name in ('runtime-events.json', 'search-result.json', 'difference.json',
                     'policy.json', 'timings.json'):
            source = data / name
            destination = evidence / name
            if source.is_file() and not destination.exists():
                try:
                    shutil.copy2(source, destination)
                except OSError:
                    pass
        if not (evidence / 'result.json').exists() and (data / 'combat_solver_test_result.json').is_file():
            try:
                shutil.copy2(data / 'combat_solver_test_result.json', evidence / 'result.json')
            except OSError:
                pass
        if (data / 'combat_solver_test_request.json').is_file():
            try:
                shutil.copy2(data / 'combat_solver_test_request.json', evidence / 'launcher-request.json')
            except OSError:
                pass
        if (data / 'combat_solver_settings.json').is_file():
            try:
                shutil.copy2(data / 'combat_solver_settings.json', evidence / 'instance-settings.json')
            except OSError:
                pass
    logs = evidence / 'instance-logs'
    if instance_dir.is_dir():
        logs.mkdir(parents=True, exist_ok=True)
        for name in ('godot-headless.log', 'launcher.log'):
            source = instance_dir / name
            if source.is_file():
                try:
                    shutil.copy2(source, logs / name)
                except OSError:
                    pass
    # Prefer the report named by the result; fall back to the latest copied ZIP.
    report_name = ''
    result = _headless_read_json(evidence / 'result.json')
    if isinstance(result, dict):
        for check in result.get('completedChecks') or []:
            match = BUG_REPORT_CHECK_RE.search(str(check))
            if match:
                report_name = Path(match.group(1)).name
                break
    reports = evidence / 'reports'
    candidates = sorted(reports.glob('*.zip')) if reports.is_dir() else []
    selected = None
    if report_name:
        selected = reports / report_name if (reports / report_name).is_file() else None
    if selected is None and candidates:
        selected = candidates[-1]
    if selected is not None:
        destination = evidence / 'report.zip'
        try:
            shutil.copy2(selected, destination)
            _headless_extract_zip(destination, evidence / 'report')
        except OSError:
            pass


def _headless_log_lines(evidence: Path) -> list[str]:
    runtime: list[str] = []
    events = _headless_read_json(evidence / 'runtime-events.json')
    if isinstance(events, list):
        for entry in events:
            if isinstance(entry, dict):
                message = entry.get('message') or entry.get('Message')
                if isinstance(message, str):
                    runtime.append(message)
    report: list[str] = []
    report_dir = evidence / 'report'
    if report_dir.is_dir():
        for path in sorted(report_dir.rglob('*.jsonl')):
            for line in path.read_text(encoding='utf-8', errors='replace').splitlines():
                try:
                    parsed = json.loads(line)
                    message = parsed.get('Message') or parsed.get('message') if isinstance(parsed, dict) else None
                    report.append(message if isinstance(message, str) else line)
                except ValueError:
                    report.append(line)
        for path in sorted(report_dir.rglob('*.log')):
            report.extend(path.read_text(encoding='utf-8', errors='replace').splitlines())

    def flash_count(lines: list[str]) -> int:
        return sum('[CombatSolver/Flash]' in line for line in lines)

    return report if flash_count(report) >= flash_count(runtime) else runtime


def _headless_native_outcome(result: dict):
    outcome = None
    for check in result.get('completedChecks') or []:
        match = NATIVE_OUTCOME_RE.search(str(check))
        if match:
            outcome = {
                'combatEnded': match.group(1) == 'True',
                'turn': int(match.group(2)),
                'hp': int(match.group(3)),
                'maxHp': int(match.group(4)),
            }
    return outcome


def _headless_replans(result: dict):
    value = None
    for check in result.get('completedChecks') or []:
        match = UNEXPECTED_REPLANS_RE.search(str(check))
        if match:
            value = int(match.group(1))
    return value


def _headless_find_baseline(baseline: Path, identifier: str) -> Path | None:
    for candidate in (baseline / identifier / 'On' / 'evidence',
                      baseline / identifier / 'evidence',
                      baseline / identifier):
        if (candidate / 'generated-scenario.resolved.json').is_file():
            return candidate
    return None


def _headless_compare_scenarios(current: Path, baseline: Path) -> list[str]:
    problems: list[str] = []
    for name in ('resolved', 'loadout'):
        now = _headless_read_json(current / f'generated-scenario.{name}.json')
        old = _headless_read_json(baseline / f'generated-scenario.{name}.json')
        if now is None or old is None:
            problems.append(f'baseline-{name}-missing')
            continue
        for key in sorted(set(now) | set(old)):
            if name == 'resolved' and key == 'mode':
                continue
            if now.get(key) != old.get(key):
                problems.append(f'{name}.{key}')
                if len(problems) >= 8:
                    return problems
    return problems


def _headless_scenario_check(evidence: Path, baseline: Path | None, identifier: str) -> str:
    problems: list[str] = []
    request = _headless_read_json(evidence / 'generated-scenario.request.json')
    resolved = _headless_read_json(evidence / 'generated-scenario.resolved.json')
    loadout = _headless_read_json(evidence / 'generated-scenario.loadout.json')
    opening = _headless_read_json(evidence / 'generated-scenario.opening.json')
    if not isinstance(request, dict):
        problems.append('missing-request')
    else:
        if request.get('cards'):
            problems.append('injected-cards')
        if request.get('preserveNativeCombatStateForTest') is not True:
            problems.append('native-state-not-preserved')
        for key in ('initialEnemyMaxHps', 'initialEnemyCurrentHps', 'initialEnemyBlocks',
                    'initialPlayerHp', 'initialPlayerMaxHp'):
            if request.get(key):
                problems.append(f'{key}-set')
    if not isinstance(resolved, dict):
        problems.append('missing-resolved')
    elif resolved.get('mode') != 'Deploy':
        problems.append('mode-not-deploy')
    if not isinstance(loadout, dict):
        problems.append('missing-loadout')
    else:
        if (isinstance(resolved, dict) and resolved.get('playerCurrentHp') is None
                and loadout.get('playerHp') != loadout.get('playerMaxHp')):
            problems.append('player-not-at-full-hp')
    if not isinstance(opening, dict):
        problems.append('missing-opening')
    else:
        continuation = str(opening.get('continuation') or '')
        for segment in continuation.split(';'):
            if not re.match(r'E\d+=', segment):
                continue
            parts = segment.split('=', 1)[1].split('/')
            if len(parts) < 6:
                continue
            try:
                hp, max_hp = int(parts[3]), int(parts[4])
            except ValueError:
                problems.append('enemy-hp-unparsed')
                break
            if hp == 1 and max_hp > 1:
                problems.append('enemy-hp-injected')
                break
            if hp > max_hp:
                problems.append('enemy-hp-invalid')
                break
    if baseline is not None:
        base = _headless_find_baseline(baseline, identifier)
        if base is None:
            problems.append('baseline-missing')
        else:
            problems.extend(_headless_compare_scenarios(evidence, base))
    return 'ok' if not problems else 'mismatch:' + ','.join(problems[:8])


def _headless_analyze(evidence: Path, baseline: Path | None, identifier: str) -> dict:
    result = _headless_read_json(evidence / 'result.json')
    if not isinstance(result, dict):
        result = {}
    error = str(result.get('error') or '')
    lines = _headless_log_lines(evidence)
    adopted = sum('FLASH_ADOPTED' in line for line in lines)
    verified = sum('FLASH_ADOPTED' in line and 'status=verified' in line for line in lines)
    failed_lines = [line for line in lines if 'FLASH_SEARCH_FAILED' in line]
    if not failed_lines and 'FLASH_SEARCH_FAILED' in error:
        failed_lines = [error]
    search_failed = len(failed_lines)
    reasons = sorted({match.group(1) for line in failed_lines
                      for match in [SEARCH_FAILED_REASON_RE.search(line)] if match})
    verify_mismatch = sum('FLASH_VERIFY_MISMATCH' in line for line in lines)
    if verify_mismatch == 0 and 'FLASH_VERIFY_MISMATCH' in error:
        verify_mismatch = 1
    preview_mismatch = sum('FLASH_PREVIEW_MISMATCH' in line for line in lines)
    native = _headless_native_outcome(result)
    replans = _headless_replans(result)
    if replans is None:
        match = re.search(r'战斗出现 (\d+) 次非预期重算', error)
        if match:
            replans = int(match.group(1))
    if (native is None and '仍存在未死亡敌人' in error
            and (result.get('combatEnded') is True or result.get('stage') == 'wait_combat_end')):
        # The deployment lock path reports player death as Failed before the post-combat
        # NativeOutcome check runs. The executor raises this error only after the native combat
        # reached IsOverOrEnding, so living enemies mean the player died. A death at the player's
        # own end of turn can still leave CombatManager.IsInProgress set when result.json is
        # written (combatEnded=false), hence the wait_combat_end stage also counts.
        # Take the turn from result.json and max HP from the actual loadout, never solverMetrics.
        loadout = _headless_read_json(evidence / 'generated-scenario.loadout.json')
        native = {
            'combatEnded': True,
            'turn': int(result.get('finishedTurn') or 0),
            'hp': 0,
            'maxHp': int(loadout.get('playerMaxHp') or 0) if isinstance(loadout, dict) else 0,
        }
    scenario = _headless_scenario_check(evidence, baseline, identifier)
    status = str(result.get('status') or 'no-result')
    if search_failed:
        outcome = 'failed'
    elif native is None or not native['combatEnded']:
        outcome = 'no-result'
    elif native['hp'] <= 0:
        if status == 'Passed' or '仍存在未死亡敌人' in error or not error:
            outcome = 'death'
        else:
            outcome = 'failed'
    elif status == 'Passed':
        outcome = 'win'
    elif status == 'Failed':
        outcome = 'failed'
    else:
        outcome = 'no-result'
    ok = (outcome in ('win', 'death') and search_failed == 0 and verify_mismatch == 0
          and replans == 0 and scenario == 'ok')
    detail = error.replace('\n', ' ')[:240]
    if reasons:
        detail = (detail + ' ' if detail else '') + 'reason=' + ','.join(reasons)
    if scenario != 'ok':
        detail = (detail + ' ' if detail else '') + 'scenario=' + scenario
    return {
        'id': identifier,
        'mode': '',
        'status': status,
        'outcome': outcome,
        'turn': native['turn'] if native else None,
        'hp': native['hp'] if native else None,
        'max_hp': native['maxHp'] if native else None,
        'adopted': adopted,
        'verified': verified,
        'search_failed': search_failed,
        'verify_mismatch': verify_mismatch,
        'preview_mismatch': preview_mismatch,
        'replans': replans,
        'scenario': scenario,
        'detail': detail,
        'ok': ok,
        'exit': None,
        'cleanup': '-',
        'evidence': _relative(evidence),
    }


def _headless_command(args: argparse.Namespace, identifier: str, scenario: Path, evidence: Path,
                      instance_name: str, cleanup: bool) -> list[str]:
    command = _headless_launcher(args, instance_name) + [
        '--scenario-id', identifier,
        '--generated-scenario-path', str(scenario),
        '--flash-search-mode-for-test', args.mode,
        '--cards-json', '[]',
        '--evidence-directory', str(evidence),
        '--headless-fast-mode-for-test', 'Instant',
        '--deployment-fast-mode-for-test', 'Instant',
        '--deployment-inter-action-delay-seconds-for-test', '0',
        '--expected-unexpected-replans-at-most', '0',
        '--search-budget-override-milliseconds', str(args.search_budget_ms),
        '--export-bug-report-after-combat',
        '--timeout-seconds', str(args.timeout_seconds),
    ]
    if cleanup:
        command.append('--cleanup-instance-on-exit')
    return command


def _headless_stop(args: argparse.Namespace, instance_name: str, env: dict[str, str], log: Path) -> None:
    try:
        run_command(_headless_launcher(args, instance_name) + ['--stop-instance'],
                    cwd=ROOT, env=env, timeout=120, log=log, append=True)
    except OSError:
        pass


def _headless_prepare_scenario(out: Path, corpus: Path, identifier: str) -> Path | None:
    source = corpus / identifier / 'spec.json'
    if not source.is_file():
        return None
    try:
        spec = json.loads(source.read_text(encoding='utf-8-sig'))
    except (OSError, ValueError):
        return None
    spec['mode'] = 'Deploy'
    target = out / identifier / 'scenario.json'
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(spec, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return target


def _headless_row_line(row: dict) -> str:
    def value(key):
        item = row.get(key)
        return '-' if item is None or item == '' else str(item)
    return '\t'.join([
        value('id'), value('mode'), value('status'), value('outcome'), value('turn'),
        value('hp'), value('max_hp'), value('adopted'), value('verified'),
        value('search_failed'), value('verify_mismatch'), value('preview_mismatch'),
        value('replans'), value('scenario'), value('exit'), value('cleanup'), value('evidence'),
    ])


def _headless_summary(rows: list[dict], lanes: list[dict]) -> str:
    header = ['id', 'mode', 'status', 'outcome', 'turn', 'hp', 'maxHp', 'adopted', 'verified',
              'searchFailed', 'verifyMismatch', 'previewMismatch', 'replans', 'scenario',
              'exit', 'cleanup', 'evidence']
    lines = ['\t'.join(header)]
    for row in rows:
        lines.append(_headless_row_line(row))
    ints = ('adopted', 'verified', 'search_failed', 'verify_mismatch', 'preview_mismatch',
            'replans')
    totals = {key: sum(row[key] for row in rows if isinstance(row.get(key), int)) for key in ints}
    totals['unknown_replans'] = sum(1 for row in rows if not isinstance(row.get('replans'), int))
    totals['win'] = sum(row['outcome'] == 'win' for row in rows)
    totals['death'] = sum(row['outcome'] == 'death' for row in rows)
    totals['failed'] = sum(row['outcome'] == 'failed' for row in rows)
    totals['no_result'] = sum(row['outcome'] == 'no-result' for row in rows)
    totals['scenario_mismatch'] = sum(row['scenario'] != 'ok' for row in rows)
    totals['launcher_nonzero'] = sum(bool(row.get('exit')) for row in rows)
    totals['cleanup_failures'] = sum(lane['cleanup'] != 'ok' for lane in lanes)
    lines.append('TOTAL\truns={runs}\twin={win}\tdeath={death}\tfailed={failed}\tnoResult={no_result}\t'
                 'adopted={adopted}\tverified={verified}\tsearchFailed={search_failed}\t'
                 'verifyMismatch={verify_mismatch}\tpreviewMismatch={preview_mismatch}\t'
                 'replans={replans}\tunknownReplans={unknown_replans}\t'
                 'scenarioMismatch={scenario_mismatch}\tlauncherNonzero={launcher_nonzero}\t'
                 'cleanupFailures={cleanup_failures}'.format(runs=len(rows), **totals))
    for lane in lanes:
        lines.append(f"LANE\t{lane['lane']}\tinstance={lane['instance']}\truns={lane['runs']}\t"
                     f"cleanup={lane['cleanup']}")
    failures = [row for row in rows if not row['ok']]
    if failures:
        lines.append('FAILURES')
        for row in failures:
            lines.append(f"  {row['id']} outcome={row['outcome']} status={row['status']} "
                         f"searchFailed={row['search_failed']} verifyMismatch={row['verify_mismatch']} "
                         f"replans={row['replans']} scenario={row['scenario']} detail={row['detail']}")
    ok = all(row['ok'] for row in rows) and all(lane['cleanup'] == 'ok' for lane in lanes)
    lines.append('RESULT ' + ('PASS' if ok else 'FAIL'))
    return '\n'.join(lines) + '\n'


def _run_headless_lane(lane: int, ids: list[str], args: argparse.Namespace, out: Path,
                       host_root: Path, xdg_data_home: Path | None,
                       print_lock: threading.Lock) -> tuple[list[dict], dict]:
    label = _headless_safe_label(args.label or 'headless')
    instance_name = f'{label}-{lane + 1}'
    instance_dir = _headless_instance_root() / instance_name
    env = _headless_environment(args, instance_dir, host_root, xdg_data_home)
    lane_log = out / f'lane-{lane + 1}.log'
    rows: list[dict] = []
    cleanup = 'ok'
    if instance_dir.exists():
        _headless_stop(args, instance_name, env, lane_log)
        try:
            _headless_remove_instance(instance_dir)
        except (OSError, RuntimeError) as error:
            with print_lock:
                print(f'lane {lane + 1}: stale instance cleanup failed: {error}', flush=True)
    for position, identifier in enumerate(ids):
        last = position == len(ids) - 1
        evidence = out / identifier / 'evidence'
        if evidence.exists():
            shutil.rmtree(evidence)
        evidence.mkdir(parents=True)
        scenario = _headless_prepare_scenario(out, args.quality_dir.resolve(), identifier)
        if scenario is None:
            row = {'id': identifier, 'mode': args.mode, 'status': 'no-scenario',
                   'outcome': 'no-result', 'turn': None, 'hp': None, 'max_hp': None,
                   'adopted': 0, 'verified': 0, 'search_failed': 0, 'verify_mismatch': 0,
                   'preview_mismatch': 0, 'replans': None, 'scenario': 'mismatch:missing-spec',
                   'detail': 'missing spec.json', 'ok': False, 'exit': None, 'cleanup': '-',
                   'evidence': _relative(evidence)}
            rows.append(row)
            continue
        _headless_clear_run_state(instance_dir)
        _headless_write_settings(instance_dir, args.mode)
        watcher = _HeadlessEvidenceWatcher(instance_dir, evidence)
        watcher.start()
        try:
            code = run_command(_headless_command(args, identifier, scenario, evidence,
                                                 instance_name, last),
                               cwd=ROOT, env=env, timeout=args.timeout_seconds + 180,
                               log=out / identifier / 'run.log')
        finally:
            watcher.stop()
        _headless_copy_evidence(instance_dir, evidence)
        row = _headless_analyze(evidence, args.scenario_baseline, identifier)
        row['mode'] = args.mode
        row['exit'] = code
        if last:
            if instance_dir.exists():
                _headless_stop(args, instance_name, env, lane_log)
                try:
                    _headless_remove_instance(instance_dir)
                    row['cleanup'] = 'removed-after-launcher'
                    cleanup = 'removed-after-launcher'
                except (OSError, RuntimeError):
                    row['cleanup'] = 'left'
                    cleanup = 'left'
            else:
                row['cleanup'] = 'ok'
        elif code != 0:
            _headless_stop(args, instance_name, env, lane_log)
        rows.append(row)
        with print_lock:
            print(f"[{lane + 1}] {identifier} {args.mode} exit={code} outcome={row['outcome']} "
                  f"turn={row['turn']} hp={row['hp']}/{row['max_hp']} "
                  f"adopted={row['adopted']} verified={row['verified']} "
                  f"searchFailed={row['search_failed']} verifyMismatch={row['verify_mismatch']} "
                  f"previewMismatch={row['preview_mismatch']} replans={row['replans']} "
                  f"scenario={row['scenario']}", flush=True)
    if instance_dir.exists():
        _headless_stop(args, instance_name, env, lane_log)
        try:
            _headless_remove_instance(instance_dir)
            if cleanup == 'ok':
                cleanup = 'removed-after-lane'
        except (OSError, RuntimeError):
            cleanup = 'left'
    return rows, {'lane': lane + 1, 'instance': instance_name, 'runs': len(ids), 'cleanup': cleanup}


def cmd_headless(args: argparse.Namespace) -> int:
    if not sys.platform.startswith('linux'):
        print('headless: the Linux run-unattended-test.sh entry is the only supported launcher; '
              'the PowerShell path is out of scope for this batch', file=sys.stderr)
        return 2
    if not HEADLESS_SCRIPT.is_file():
        print(f'headless: missing launcher {HEADLESS_SCRIPT}', file=sys.stderr)
        return 2
    ids, missing = _headless_selected_ids(args)
    if missing:
        print('headless: unknown quality-corpus ids: ' + ', '.join(missing), file=sys.stderr)
        return 2
    if not ids:
        print('headless: no ids selected', file=sys.stderr)
        return 2
    if args.lanes < 1:
        print('headless: --lanes must be >= 1', file=sys.stderr)
        return 2
    if args.timeout_seconds < 1:
        print('headless: --timeout-seconds must be >= 1', file=sys.stderr)
        return 2
    if args.search_budget_ms < 1:
        print('headless: --search-budget-ms must be >= 1', file=sys.stderr)
        return 2
    xdg_data_home = args.xdg_data_home
    if xdg_data_home is None:
        candidate = ROOT / '.local/headless-seed/data'
        if candidate.is_dir():
            xdg_data_home = candidate
    if xdg_data_home is not None:
        xdg_data_home = Path(xdg_data_home).expanduser().resolve()
        if not (xdg_data_home / 'SlayTheSpire2/default/1/settings.save').is_file():
            print(f'headless: --xdg-data-home is not a seeded SlayTheSpire2 profile: {xdg_data_home}',
                  file=sys.stderr)
            return 2
    host_root = Path(args.headless_host_root).expanduser().resolve() if args.headless_host_root \
        else Path(os.environ.get('COMBATSOLVER_HEADLESS_HOST_ROOT',
                                 str(ROOT / '.local/headless-host'))).expanduser().resolve()
    for option in ('sts2_game_root', 'ritsu_workshop_root', 'combat_solver_build_dir'):
        value = getattr(args, option)
        if value:
            setattr(args, option, Path(value).expanduser().resolve())
    if args.scenario_baseline is not None:
        args.scenario_baseline = Path(args.scenario_baseline).expanduser().resolve()
    out = output_dir(args, 'headless')
    out.mkdir(parents=True, exist_ok=True)
    if not args.skip_build:
        ok, detail = _headless_build(args, out)
        print(detail or 'BUILD OK', flush=True)
        if not ok:
            (out / 'SUMMARY.txt').write_text('BUILD FAILED\n', encoding='utf-8')
            return 1
    dll = _headless_dll(args)
    if not dll.is_file():
        print(f'headless: CombatSolver.dll not found: {dll}', file=sys.stderr)
        (out / 'SUMMARY.txt').write_text(f'COMBATSOLVER DLL MISSING {dll}\n', encoding='utf-8')
        return 1
    lanes = min(max(1, args.lanes), len(ids))
    assignments = [ids[index::lanes] for index in range(lanes)]
    print(f'headless: {len(ids)} runs, lanes={lanes}, mode={args.mode}, '
          f'timeout={args.timeout_seconds}s, host={host_root}', flush=True)
    rows_by_id: dict[str, dict] = {}
    lane_rows: list[dict] = []
    print_lock = threading.Lock()
    with concurrent.futures.ThreadPoolExecutor(max_workers=lanes) as executor:
        futures = {}
        for index, lane in enumerate(assignments):
            future = executor.submit(_run_headless_lane, index, lane, args, out, host_root,
                                     xdg_data_home, print_lock)
            futures[future] = (index, lane)
        for future in concurrent.futures.as_completed(futures):
            index, lane_ids = futures[future]
            try:
                rows, lane = future.result()
            except Exception as error:  # orchestration boundary: report the lane, keep the summary
                instance_name = f"{_headless_safe_label(args.label or 'headless')}-{index + 1}"
                instance_dir = _headless_instance_root() / instance_name
                lane = {'lane': index + 1, 'instance': instance_name, 'runs': len(lane_ids),
                        'cleanup': 'failed'}
                try:
                    _headless_stop(args, instance_name,
                                   _headless_environment(args, instance_dir, host_root, xdg_data_home),
                                   out / f'lane-{index + 1}.log')
                    _headless_remove_instance(instance_dir)
                    lane['cleanup'] = 'removed-after-error'
                except (OSError, RuntimeError):
                    lane['cleanup'] = 'left'
                rows = []
                for identifier in lane_ids:
                    rows.append({
                        'id': identifier, 'mode': args.mode, 'status': 'lane-error',
                        'outcome': 'no-result', 'turn': None, 'hp': None, 'max_hp': None,
                        'adopted': 0, 'verified': 0, 'search_failed': 0, 'verify_mismatch': 0,
                        'preview_mismatch': 0, 'replans': None, 'scenario': 'mismatch:lane-error',
                        'detail': f'{type(error).__name__}: {error}'[:240], 'ok': False,
                        'exit': None, 'cleanup': 'failed',
                        'evidence': _relative(out / identifier / 'evidence')})
                with print_lock:
                    print(f'lane {index + 1} failed: {type(error).__name__}: {error}', flush=True)
            lane_rows.append(lane)
            for row in rows:
                rows_by_id[row['id']] = row
    ordered = []
    for identifier in ids:
        row = rows_by_id.get(identifier)
        if row is None:
            row = {'id': identifier, 'mode': args.mode, 'status': 'missing', 'outcome': 'no-result',
                   'turn': None, 'hp': None, 'max_hp': None, 'adopted': 0, 'verified': 0,
                   'search_failed': 0, 'verify_mismatch': 0, 'preview_mismatch': 0,
                   'replans': None, 'scenario': 'mismatch:not-run', 'detail': 'lane did not return a row',
                   'ok': False, 'exit': None, 'cleanup': '-',
                   'evidence': _relative(out / identifier / 'evidence')}
        ordered.append(row)
    lane_rows.sort(key=lambda item: item['lane'])
    summary = _headless_summary(ordered, lane_rows)
    (out / 'SUMMARY.txt').write_text(summary, encoding='utf-8')
    (out / 'results.json').write_text(json.dumps(ordered, ensure_ascii=False, indent=2) + '\n',
                                      encoding='utf-8')
    print(summary, flush=True)
    return 0 if summary.rstrip().endswith('RESULT PASS') else 1

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
    diff.add_argument('--steps', type=int, default=60,
                      help='OFFLINE_HARNESS_FLASH_DIFF_STEPS per walk (default 60)')
    diff.add_argument('--debug', action='store_true',
                      help='OFFLINE_HARNESS_FLASH_DEBUG: print non-pure history entries on a K4 mismatch')
    diff.set_defaults(func=cmd_diff)

    scan = sub.add_parser('scan', help='Flash solve plus candidate-route replay scan')
    add_common(scan)
    add_root_options(scan, walks=False, quality_walks=False)
    scan.add_argument('--candidates', type=int, default=8, help='OFFLINE_HARNESS_FLASH_CANDIDATE_SCAN (default 8)')
    scan.add_argument('--budget-ms', type=int, default=10000, help='per-root search budget (default 10000)')
    scan.set_defaults(func=cmd_scan)

    coordinator = sub.add_parser('coordinator', help='Off/On coordinator comparison on the quality corpus')
    add_common(coordinator, no_k0=False)
    coordinator.add_argument('--quality-dir', type=Path, default=QUALITY,
                             help='quality corpus (default coverage/corpora/flash-quality, 120 roots)')
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

    trace = sub.add_parser('trace', help='trace a target route through one Flash request')
    trace.add_argument('--dotnet', default=DOTNET)
    trace.add_argument('--harness', type=Path, default=HARNESS)
    trace.add_argument('--out', type=Path, default=None)
    trace.add_argument('--id', required=True, help='quality corpus directory name (for request/route)')
    trace.add_argument('--quality-dir', type=Path, default=QUALITY)
    trace.add_argument('--request', type=Path, default=None, help='override request json')
    trace.add_argument('--route', type=Path, default=None, help='target route.json (default <quality-dir>/<id>/route.json)')
    trace.add_argument('--purpose', required=True, help='DirectSearchPurpose or ContinuationPurpose name')
    trace.add_argument('--continuation', action='store_true', help='purpose is a ContinuationPurpose')
    trace.add_argument('--prefix', type=int, default=0, help='fixed-prefix action count (default 0)')
    trace.add_argument('--beam', type=int, default=60, help='profile beam width (also used by Flash)')
    trace.add_argument('--nodes', type=int, default=120000)
    trace.add_argument('--budget-ms', type=int, default=120000)
    trace.add_argument('--flags', default='', help='comma list: band,base,power')
    trace.add_argument('--max-potions', type=int, default=None)
    trace.add_argument('--min-potions', type=int, default=None)
    trace.add_argument('--potion-policy', default=None, help='SolverPotionPolicy override')
    trace.add_argument('--incumbent-deficit', type=int, default=None,
                       help='primary incumbent strategic HP deficit for the traced request')
    trace.add_argument('--incumbent-turn', type=int, default=None,
                       help='primary incumbent combat-end turn (required with --incumbent-deficit)')
    trace.add_argument('--reset-baseline', action='store_true',
                       help='reset the fixed-prefix scheduling baseline')
    trace.add_argument('--side', default='both', choices=('both', 'flash', 'existing'))
    trace.add_argument('--timeout', type=float, default=480)
    trace.set_defaults(func=cmd_trace)

    headless = sub.add_parser(
        'headless',
        help='Linux full-battle deployment on the 40 fixed quality-corpus roots')
    headless.add_argument('--label', default='headless',
                          help='output directory and instance prefix (default headless)')
    headless.add_argument('--out', type=Path, default=None,
                          help='explicit output directory (wins over --label)')
    headless.add_argument('--dotnet', default=DOTNET, help='dotnet executable used for the Release build')
    headless.add_argument('--skip-build', action='store_true', help='use the existing Release build')
    headless.add_argument('--ids', nargs='+', default=None,
                          help='override the 40 root ids (space or comma separated corpus directory names)')
    headless.add_argument('--quality-dir', type=Path, default=QUALITY,
                          help='quality corpus with <id>/spec.json (default coverage/corpora/flash-quality)')
    headless.add_argument('--mode', choices=('On', 'Off'), default='On',
                          help='--flash-search-mode-for-test value (default On)')
    headless.add_argument('--lanes', type=int, default=2, help='parallel headless instances (default 2)')
    headless.add_argument('--timeout-seconds', type=int, default=600,
                          help='per-battle launcher timeout (default 600)')
    headless.add_argument('--search-budget-ms', type=int, default=120000,
                          help='per-search soft budget override (default 120000, matching the quality corpus)')
    headless.add_argument('--xdg-data-home', type=Path, default=None,
                          help='seeded XDG_DATA_HOME passed to the launcher '
                               '(default .local/headless-seed/data when present)')
    headless.add_argument('--headless-host-root', default=None,
                          help='shared COMBATSOLVER_HEADLESS_HOST_ROOT (default .local/headless-host)')
    headless.add_argument('--headless-cpu-reservation', type=int, default=4,
                          help='per-instance CPU reservation (default 4)')
    headless.add_argument('--headless-memory-reservation-mib', type=int, default=8192,
                          help='per-instance memory reservation in MiB (default 8192)')
    headless.add_argument('--sts2-game-root', default=None,
                          help='passed through to --sts2-game-root')
    headless.add_argument('--ritsu-workshop-root', default=None,
                          help='passed through to --ritsu-workshop-root')
    headless.add_argument('--combat-solver-build-dir', default=None,
                          help='passed through to --combat-solver-build-dir (use with --skip-build)')
    headless.add_argument('--scenario-baseline', type=Path, default=None,
                          help='prior evidence tree; compare each generated resolved/loadout scenario')
    headless.set_defaults(func=cmd_headless)

    fallback = sub.add_parser(
        'fallback',
        help='On-mode admission fallback contract: unsupported content uses the regular search, other rejection stays failed')
    add_common(fallback)
    fallback.add_argument('--fixture', type=Path, default=FALLBACK_FIXTURE,
                          help='request with Flash-unsupported content (default coverage/fixtures/search/...)')
    fallback.add_argument('--profile', default='Low',
                          help='performance preset controlling the regular/Flash profile split (default Low)')
    fallback.add_argument('--beam', type=int, default=45, help='regular profile beam width before the Flash swap')
    fallback.add_argument('--nodes', type=int, default=5000, help='regular profile node cap before the Flash swap')
    fallback.add_argument('--budget-ms', type=int, default=20000, help='per-process search budget (default 20000)')
    fallback.add_argument('--timeout', type=float, default=480, help='harness process timeout seconds (default 480)')
    fallback.set_defaults(func=cmd_fallback)

    compat = sub.add_parser('compat',
                            help='FlashSearchMode persistence contract: legacy Shadow/numeric values read Off, On round-trips')
    compat.add_argument('--label', default=None)
    compat.add_argument('--out', type=Path, default=None)
    compat.add_argument('--dotnet', default=DOTNET)
    compat.add_argument('--harness', type=Path, default=HARNESS)
    compat.add_argument('--skip-build', action='store_true')
    compat.set_defaults(func=cmd_compat)

    table = sub.add_parser('table', help='read-only Off/On quality table under the solver comparison key')
    table.add_argument('run_dir', type=Path, help='a coordinator output directory with <id>/{Off,On}/quality.json')
    table.add_argument('--corpus', type=Path, default=None,
                       help='corpus with <id>/spec.json (characterId, actIndex, encounterKind)')
    table.set_defaults(func=cmd_table)

    survey = sub.add_parser('survey', help='generate quality-corpus decks (coverage/corpora/flash-quality)')
    survey.add_argument('--label', default=None)
    survey.add_argument('--out', type=Path, default=None)
    survey.add_argument('--dotnet', default=DOTNET)
    survey.add_argument('--harness', type=Path, default=HARNESS)
    survey.add_argument('--skip-build', action='store_true')
    survey.add_argument('--character', required=True, help='IRONCLAD/SILENT/DEFECT/REGENT/NECROBINDER')
    survey.add_argument('--count', type=int, default=24, help='decks per character, split into Acts 1/2/3 (default 24)')
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
    if getattr(args, 'lanes', 1) < 1:
        parser.error('--lanes must be >= 1')
    if getattr(args, 'timeout_seconds', 1) < 1:
        parser.error('--timeout-seconds must be >= 1')
    return args.func(args)


if __name__ == '__main__':
    sys.exit(main())
