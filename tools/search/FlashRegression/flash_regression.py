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


if __name__ == '__main__':
    sys.exit(main())
