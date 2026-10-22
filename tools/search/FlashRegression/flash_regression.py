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
QUALITY = ROOT / "coverage/corpora/flash-quality"
KERNEL_CHECKS = ROOT / "tools/testing/checks/FlashKernelChecks"
DOTNET = shutil.which("dotnet") or "dotnet"

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
        roots += fixture_roots(args.walks)
    if only in ('all', 'quality'):
        roots += quality_roots(args.quality_walks)
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
    mismatches = re.search(r'\bmismatches=(\d+)', line)
    return not mismatches or int(mismatches.group(1)) != 0


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
    parser.add_argument('--quality-walks', type=int, default=48, help='walks per quality-corpus root (default 48)')
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


def solver_key(quality: dict) -> tuple:
    """The coordinator's comparison key, in the order RouteQualityPolicy.PotionPolicy reads it.

    Win -> survive -> fewer death-save uses -> smaller (strategic deficit + potion strategic cost)
    -> earlier combat end. The old (won, strategicHpDeficit, turn) key omitted the potion cost and
    the survival/death-save terms; a potion route could therefore look better while costing more.
    """
    turn = quality.get('combatEndedTurn')
    return (
        0 if quality.get('won') else 1,
        0 if quality.get('survives', True) else 1,
        int(quality.get('deathSaveUseCount') or 0),
        solver_cost(quality),
        turn if turn is not None else 99,
    )


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
    __slots__ = ('roots', 'good', 'same', 'worse', 'cost_delta', 'off_wins', 'on_wins',
                 'off_wall', 'on_wall', 'missing', 'failed')

    def __init__(self) -> None:
        self.roots = 0
        self.good = self.same = self.worse = 0
        self.cost_delta = 0
        self.off_wins = self.on_wins = 0
        self.off_wall = self.on_wall = 0.0
        self.missing = self.failed = 0


def _group_table(title: str, groups: dict[str, _GroupRow]) -> list[str]:
    lines = [f'{title}']
    lines.append(f'  {"key":20} {"roots":>5} {"good":>4} {"same":>4} {"worse":>5} '
                 f'{"cost_delta":>10} {"off_won":>7} {"on_won":>6} '
                 f'{"off_wall":>9} {"on_wall":>9} {"missing":>7} {"failed":>6}')
    for key in sorted(groups):
        row = groups[key]
        lines.append(f'  {key:20} {row.roots:5d} {row.good:4d} {row.same:4d} {row.worse:5d} '
                     f'{row.cost_delta:+10d} {row.off_wins:7d} {row.on_wins:6d} '
                     f'{row.off_wall:9.2f} {row.on_wall:9.2f} {row.missing:7d} {row.failed:6d}')
    return lines


def quality_table(run_dir: Path, corpus: Path | None = None) -> str:
    """Read-only Off/On quality table under the solver's own PotionPolicy comparison key.

    Groups roots by the corpus spec.json fields (characterId, actIndex, encounterKind). The cost
    delta is sum(On strategicHpDeficit + On potionStrategicCost - the same Off sum); negative means
    On improved. Potion-cost-different roots are counted separately so a route that spends a potion
    is not mistaken for a better route.
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
        off_key = solver_key(off_q)
        on_key = solver_key(on_q)
        mark = '+' if on_key < off_key else ('-' if on_key > off_key else '=')
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
            row.cost_delta += delta
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
        f'worse={total.worse} cost_delta={total.cost_delta:+d} potion_cost_diff={potion_cost_diff} '
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
    potion_cost_diff = 0

    for root in sorted(path for path in out.iterdir() if path.is_dir()):
        try:
            (off_wall, off_quality, _), (on_wall, on_quality, flash) = (
                load_quality(root / 'Off'), load_quality(root / 'On'))
        except (OSError, ValueError, KeyError) as error:
            rows.append(f'{root.name:40} ERROR {error}')
            continue
        off_key = solver_key(off_quality)
        on_key = solver_key(on_quality)
        mark = '=' if off_key == on_key else ('+' if on_key < off_key else '-')
        better += mark == '+'
        same += mark == '='
        worse += mark == '-'
        adopted += flash.startswith('FLASH_ADOPTED')
        total['Off'] += off_wall
        total['On'] += on_wall
        off_cost = solver_cost(off_quality)
        on_cost = solver_cost(on_quality)
        lost += on_cost - off_cost
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
    rows.append(f"on better={better} same={same} worse={worse} cost_delta={lost:+d} "
                f"potion_cost_diff={potion_cost_diff} adopted={adopted} "
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
        off_key = solver_key(off)
        on_key = solver_key(on)
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


def cmd_table(args: argparse.Namespace) -> int:
    corpus = args.corpus.resolve() if args.corpus else None
    print(quality_table(args.run_dir.resolve(), corpus))
    return 0


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
    print(coordinator_table(out))
    print(strategic_table(out))
    corpus = Path(args.quality_dir).resolve() if args.quality_dir else None
    table = quality_table(out, corpus)
    (out / 'TABLE-GROUPS.txt').write_text(table + '\n', encoding='utf-8')
    print(table)
    if args.compare:
        qcompare(out, Path(args.compare).resolve(), args.compare_mode)
    return 0


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

    coordinator = sub.add_parser('coordinator', help='Off/On coordinator comparison on the quality corpus')
    add_common(coordinator)
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
    add_common(trace)
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
    trace.add_argument('--side', default='both', choices=('both', 'flash', 'existing'))
    trace.add_argument('--timeout', type=float, default=480)
    trace.set_defaults(func=cmd_trace)

    retention = sub.add_parser('retention',
                                help='K6 per-boundary retained-set comparison on the quality corpus')
    add_common(retention)
    add_root_options(retention)
    retention.set_defaults(jobs=8)
    retention.add_argument('--baseline', type=Path, default=RETENTION_BASELINE,
                           help='off120 coordinator baseline with <id>/Off/harness-result.json')
    retention.add_argument('--ids', nargs='+', default=None, help='quality corpus directory names')
    retention.add_argument('--budget-ms', type=int, default=120000)
    retention.add_argument('--top', type=int, default=8, help='divergent states reported per side (default 8)')
    retention.add_argument('--timeout', type=float, default=900, help='per-run timeout seconds (default 900)')
    retention.add_argument('--merge', nargs='+', type=Path, default=None,
                           help='merge existing retention SUMMARY.json directories instead of running')
    retention.set_defaults(func=cmd_retention)

    table = sub.add_parser('table', help='read-only Off/On quality table under the solver comparison key')
    table.add_argument('run_dir', type=Path, help='a coordinator output directory with <id>/{Off,On}/quality.json')
    table.add_argument('--corpus', type=Path, default=None,
                       help='corpus with <id>/spec.json (characterId, actIndex, encounterKind)')
    table.set_defaults(func=cmd_table)

    survey = sub.add_parser('survey', help='generate quality-corpus decks (coverage/corpora/flash-quality)')
    add_common(survey)
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
    return args.func(args)


# ---------------------------------------------------------------------------
# K6 retention-set comparison
# ---------------------------------------------------------------------------

RETENTION_BASELINE = DEFAULT_OUT / 'off120-base'


def _retention_member_label(member: dict) -> str:
    label = f"w{int(member['BeamWidth'])}"
    if member.get('AggressivePowerCommitment'):
        label += '+power'
    if member.get('SecondRankBand'):
        label += '+band'
    if member.get('BaseScoreOnly'):
        label += '+base'
    if member.get('OffensiveRefinement'):
        label += '+offense'
    if member.get('BoundedRefinement'):
        label += '+bounded'
    return label


def _retention_members(baseline: Path, root_id: str) -> list[dict]:
    path = baseline / root_id / 'Off' / 'harness-result.json'
    if not path.is_file():
        return []
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    members = (data.get('solverMetrics') or {}).get('PortfolioMembers') or []
    return [member for member in members if member.get('Ran')]


def _retention_env(member: dict, label: str, outdir: Path, top: int) -> dict[str, str]:
    env = baseline_environment()
    env['OFFLINE_HARNESS_RETENTION_NODE_BUDGET'] = str(int(member.get('NodeBudget') or 120000))
    env['OFFLINE_HARNESS_RETENTION_DIFF'] = '1'
    env['OFFLINE_HARNESS_RETENTION_LABEL'] = label
    env['OFFLINE_HARNESS_RETENTION_OUT'] = str((outdir / 'retention-diff.json').resolve())
    env['OFFLINE_HARNESS_RETENTION_TOP'] = str(top)
    env['OFFLINE_HARNESS_RETENTION_PURPOSE'] = 'PrimaryBeam'
    flags = []
    if member.get('SecondRankBand'):
        flags.append('band')
    if member.get('BaseScoreOnly'):
        flags.append('base')
    if member.get('AggressivePowerCommitment'):
        flags.append('power')
    if flags:
        env['OFFLINE_HARNESS_RETENTION_FLAGS'] = ','.join(flags)
    if member.get('OffensiveRefinement'):
        env['OFFLINE_HARNESS_RETENTION_BEAM_WEIGHT'] = 'EnemyHp:1.5'
    return env


def _reason_key(category: str, detail: dict) -> str:
    last = (detail.get('lastAction') or '-').replace(' ', '')
    if category == 'A':
        card = last.split(':', 3)[-1] if last.count(':') >= 2 else ''
        copies = (detail.get('rootHandCounts') or {}).get(card, 0)
        suffix = ':duplicate-card-copy' if copies > 1 else ':action-enumeration'
        return f'A:not-generated:{last}{suffix}'
    if category == 'B':
        return f"B:{detail.get('otherStatus') or 'rejected'}:{last}"
    if category == 'C':
        if detail.get('otherRank') is None:
            return f'C:generated-not-in-pool:{last}'
        return f'C:rank-or-tie:{last}'
    if category == 'D':
        left = detail.get('presentScore')
        right = detail.get('otherScore')
        if isinstance(left, (int, float)) and isinstance(right, (int, float)):
            return f'D:score-differs:{last}:delta={left - right:g}'
        return f'D:score-differs:{last}'
    if category == 'E':
        def one_line(value: object) -> str:
            text = ' '.join(str(value or '-').split())
            return text if len(text) <= 96 else text[:93] + '...'
        return 'E:required:' + one_line(detail.get('presentLabel')) + '->' + one_line(detail.get('otherLabel'))
    return f'{category}:{detail.get("reason") or "-"}'


def _retention_record_from_payload(root_id: str, label: str, code: int, payload: dict) -> dict:
    record = {
        'root': root_id,
        'member': label,
        'exit': code,
        'status': 'missing',
        'firstCategory': None,
        'firstKind': None,
        'turn': None,
        'depth': None,
        'routeIdentical': None,
        'unprojectable': payload.get('unprojectable', 0),
        'reasons': [],
        'error': None,
    }
    comparison = payload.get('comparison') or {}
    record['status'] = payload.get('status') or comparison.get('status') or 'unknown'
    record['error'] = payload.get('error')
    record['routeIdentical'] = comparison.get('routeIdentical') if isinstance(comparison, dict) else None
    first = comparison.get('firstDivergence') if isinstance(comparison, dict) else None
    if isinstance(first, dict):
        record['firstCategory'] = first.get('category')
        record['firstKind'] = first.get('kind')
        record['turn'] = first.get('turn')
        record['depth'] = first.get('depth')
        for side in ('oldOnly', 'flashOnly'):
            for detail in first.get(side) or []:
                category = detail.get('category') or record['firstCategory']
                detail['rootHandCounts'] = payload.get('rootHandCounts') or {}
                record['reasons'].append(_reason_key(category, detail))
    if record['unprojectable']:
        record['observedCategory'] = record['firstCategory']
        record['status'] = 'incomparable'
        record['firstCategory'] = None
        record['firstKind'] = None
        record['turn'] = None
        record['depth'] = None
        record['reasons'] = []
    rest = {key: value for key, value in payload.items()
            if key not in ('label', 'member', 'comparison', 'rootHandCounts')}
    record['summary'] = rest
    return record


def _retention_record(root_id: str, member: dict, outdir: Path, code: int) -> dict:
    label = _retention_member_label(member)
    path = outdir / 'retention-diff.json'
    if not path.is_file():
        return {
            'root': root_id, 'member': label, 'exit': code, 'status': 'missing',
            'firstCategory': None, 'firstKind': None, 'turn': None, 'depth': None,
            'routeIdentical': None, 'unprojectable': None, 'reasons': [],
            'error': 'missing retention-diff.json',
        }
    payload = json.loads(path.read_text(encoding='utf-8-sig'))
    return _retention_record_from_payload(root_id, label, code, payload)


def _retention_aggregate(records: list[dict]) -> dict:
    members: dict[str, dict] = {}
    categories: dict[str, int] = {}
    turns: dict[str, int] = {}
    depths: dict[str, int] = {}
    reasons: dict[str, dict] = {}
    for record in records:
        member = record['member']
        entry = members.setdefault(member, {
            'runs': 0, 'identical': 0, 'divergent': 0, 'incomparable': 0, 'error': 0,
            'routeIdentical': 0, 'categories': {}, 'turns': {}, 'depths': {},
        })
        entry['runs'] += 1
        status = record['status']
        if status != 'incomparable' and record.get('routeIdentical'):
            entry['routeIdentical'] += 1
        if status == 'identical':
            entry['identical'] += 1
        elif status == 'incomparable':
            entry['incomparable'] += 1
            continue
        elif status == 'error' or record['exit'] != 0:
            entry['error'] += 1
        else:
            entry['divergent'] += 1
        category = record.get('firstCategory')
        if category:
            entry['categories'][category] = entry['categories'].get(category, 0) + 1
            categories[category] = categories.get(category, 0) + 1
            turn_key = str(record.get('turn'))
            depth_key = str(record.get('depth'))
            entry['turns'][turn_key] = entry['turns'].get(turn_key, 0) + 1
            entry['depths'][depth_key] = entry['depths'].get(depth_key, 0) + 1
        if record.get('turn') is not None:
            key = str(record['turn'])
            turns[key] = turns.get(key, 0) + 1
        if record.get('depth') is not None:
            key = str(record['depth'])
            depths[key] = depths.get(key, 0) + 1
        for reason in record.get('reasons') or []:
            bucket = reasons.setdefault(reason, {'count': 0, 'members': set(), 'roots': set()})
            bucket['count'] += 1
            bucket['members'].add(member)
            bucket['roots'].add(record['root'])
    reason_rows = [
        {
            'reason': reason,
            'count': value['count'],
            'members': sorted(value['members']),
            'roots': sorted(value['roots']),
            'representativeRoot': sorted(value['roots'])[0],
        }
        for reason, value in sorted(reasons.items(), key=lambda item: (-item[1]['count'], item[0]))
    ]
    return {
        'members': members,
        'categories': categories,
        'turns': turns,
        'depths': depths,
        'reasons': reason_rows,
    }


def _retention_summary_lines(records: list[dict], aggregate: dict) -> list[str]:
    lines = []
    lines.append('MEMBER SUMMARY')
    lines.append('member\truns\tidentical\trouteSame\tdivergent\tincomparable\terror\tA\tB\tC\tD\tE\tF\tG')
    for member, entry in sorted(aggregate['members'].items()):
        categories = entry['categories']
        lines.append('\t'.join([
            member, str(entry['runs']), str(entry['identical']), str(entry['routeIdentical']),
            str(entry['divergent']), str(entry['incomparable']), str(entry['error']),
            *[str(categories.get(category, 0)) for category in 'ABCDEFG'],
        ]))
    lines.append('')
    lines.append('FIRST DIVERGENCE CATEGORY')
    lines.append('category\tcount')
    for category, count in sorted(aggregate['categories'].items()):
        lines.append(f'{category}\t{count}')
    lines.append('')
    lines.append('TURN / LAYER DEPTH OF FIRST DIVERGENCE')
    lines.append('turn\tcount')
    for turn, count in sorted(aggregate['turns'].items(), key=lambda item: int(item[0])):
        lines.append(f'{turn}\t{count}')
    lines.append('depth\tcount')
    for depth, count in sorted(aggregate['depths'].items(), key=lambda item: int(item[0])):
        lines.append(f'{depth}\t{count}')
    lines.append('')
    lines.append('MEMBER TURN / DEPTH')
    lines.append('member\tturn:count\tdepth:count')
    for member, entry in sorted(aggregate['members'].items()):
        turns = ','.join(f'{key}:{value}' for key, value in sorted(
            entry['turns'].items(), key=lambda item: int(item[0])))
        depths = ','.join(f'{key}:{value}' for key, value in sorted(
            entry['depths'].items(), key=lambda item: int(item[0])))
        lines.append(f"{member}\t{turns or '-'}\t{depths or '-'}")
    lines.append('')
    lines.append('MOST COMMON CONCRETE REASONS (top-N per first divergence)')
    lines.append('count\tmembers\troots\treason\trepresentative')
    for row in aggregate['reasons'][:40]:
        lines.append('\t'.join([
            str(row['count']), str(len(row['members'])), str(len(row['roots'])),
            row['reason'], row['representativeRoot'],
        ]))
    lines.append('')
    lines.append('PER RUN')
    lines.append('root\tmember\texit\tstatus\tcategory\tkind\tturn\tdepth\treasons\terror')
    for record in sorted(records, key=lambda item: (item['root'], item['member'])):
        lines.append('\t'.join([
            record['root'], record['member'], str(record['exit']), record['status'],
            str(record.get('firstCategory') or '-'), str(record.get('firstKind') or '-'),
            str(record.get('turn') if record.get('turn') is not None else '-'),
            str(record.get('depth') if record.get('depth') is not None else '-'),
            ','.join(record.get('reasons') or []) or '-', record.get('error') or '-',
        ]))
    return lines


def cmd_retention(args: argparse.Namespace) -> int:
    if args.merge:
        out = output_dir(args, 'k6-retention-merged')
        out.mkdir(parents=True, exist_ok=True)
        records: list[dict] = []
        for directory in args.merge:
            directory = Path(directory).resolve()
            runs = sorted(directory.glob('*/*/retention-diff.json'))
            if runs:
                for path in runs:
                    payload = json.loads(path.read_text(encoding='utf-8-sig'))
                    record = _retention_record_from_payload(
                        path.parent.parent.name, path.parent.name, 0, payload)
                    records.append({key: value for key, value in record.items() if key != 'summary'})
                continue
            path = directory / 'SUMMARY.json'
            if not path.is_file():
                raise SystemExit(f'missing retention runs or SUMMARY.json: {directory}')
            payload = json.loads(path.read_text(encoding='utf-8-sig'))
            records.extend(payload.get('records') or [])
        aggregate = _retention_aggregate(records)
        lines = _retention_summary_lines(records, aggregate)
        write_summary(out / 'SUMMARY.txt', lines + ['DONE'])
        (out / 'SUMMARY.json').write_text(json.dumps({
            'jobs': args.jobs,
            'budgetMilliseconds': args.budget_ms,
            'tasks': len(records),
            'aggregate': aggregate,
            'records': records,
        }, indent=1, ensure_ascii=False) + '\n', encoding='utf-8')
        print(f'merged {len(records)} runs under {out}')
        return 0 if all(record['exit'] == 0 and record['status'] != 'error' for record in records) else 1
    if args.only == 'fixtures':
        raise SystemExit('retention 只支持质量语料（--only quality 或 all）')
    roots = quality_roots(args.quality_walks)
    if args.ids:
        wanted = set(args.ids)
        roots = [root for root in roots if root.request.parent.name in wanted]
    if args.root:
        roots = [root for root in roots if any(token in root.name for token in args.root)]
    shards = max(1, args.shards)
    if args.shard is not None:
        if not 0 <= args.shard < shards:
            raise SystemExit(f'--shard must be in [0, {shards})')
        roots = [root for index, root in enumerate(roots) if index % shards == args.shard]
    out = output_dir(args, 'k6-retention')
    if not prepare(args, out, k0=True):
        return 1

    baseline = args.baseline.resolve()
    tasks: list[tuple[Root, dict]] = []
    missing: list[str] = []
    for root in roots:
        root_id = root.request.parent.name
        members = _retention_members(baseline, root_id)
        if not members:
            missing.append(root_id)
            continue
        for member in members:
            tasks.append((root, member))
    if missing:
        write_summary(out / 'SUMMARY.txt', [f'BASELINE MISSING {root}' for root in missing])
        print(f'baseline missing for {len(missing)} roots', file=sys.stderr)
        return 1

    records: list[dict] = []
    lock = __import__('threading').Lock()
    started = time.time()

    def work(task: tuple[Root, dict]) -> dict:
        root, member = task
        root_id = root.request.parent.name
        label = _retention_member_label(member)
        outdir = fresh_dir(out / root_id / label)
        env = _retention_env(member, label, outdir, args.top)
        command = [
            args.dotnet, str(args.harness), '--request', str(root.request),
            '--label', f'{root_id}-{label}', '--profile', 'Medium',
            '--beam', str(int(member['BeamWidth'])),
            '--nodes', str(max(100, int(member.get('NodeBudget') or 120000))),
            '--budget-ms', str(args.budget_ms), '--dop', '1',
            '--search-mode', 'Evaluate', '--milestone', 'M1', '--out', str(outdir),
        ]
        code = run_command(command, cwd=ROOT, env=env, timeout=args.timeout,
                           log=outdir / 'stdout.txt')
        return _retention_record(root_id, member, outdir, code)

    total = len(tasks)
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as executor:
        futures = {executor.submit(work, task): task for task in tasks}
        for done, future in enumerate(concurrent.futures.as_completed(futures), 1):
            record = future.result()
            records.append(record)
            elapsed = time.time() - started
            print(f"[{done}/{total}] {record['root']} {record['member']} "
                  f"{record['status']} {elapsed:7.1f}s", flush=True)

    aggregate = _retention_aggregate(records)
    lines = _retention_summary_lines(records, aggregate)
    write_summary(out / 'SUMMARY.txt', lines + ['DONE'])
    payload = {
        'baseline': _relative(baseline),
        'jobs': args.jobs,
        'budgetMilliseconds': args.budget_ms,
        'tasks': len(records),
        'aggregate': aggregate,
        'records': [{key: value for key, value in record.items() if key != 'summary'}
                     for record in records],
    }
    (out / 'SUMMARY.json').write_text(json.dumps(payload, indent=1, ensure_ascii=False) + '\n',
                                      encoding='utf-8')
    print(f'wrote {len(records)} runs under {out}')
    return 0 if all(record['exit'] == 0 and record['status'] != 'error' for record in records) else 1


if __name__ == '__main__':
    sys.exit(main())
