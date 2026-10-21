# Flash 回归工具

`flash_regression.py` 是 Flash 搜索后端已提交的回归入口。它把原先散在
`.local/tool-tasks/flash-search/` 的构建、差分、候选扫描、基准、协调器对照与牌组生成
收进一个跨平台 Python 入口，供审阅者在仓库内复跑证据。工具只调用
`OfflineSearchHarness`，不重新实现搜索或战斗语义。

## 依赖

- Python 3.10+，无第三方包。
- .NET 9 SDK。
- 与 `OfflineSearchHarness` 相同的游戏依赖：`local.props` 或自动探测的 Steam / `Sts2Dir` /
  `Sts2DataDir` / RitsuLib 目录。路径解析见
  [离线搜索宿主](../../../docs/OFFLINE_SEARCH_HARNESS.md) 与
  [工具构建规则](../../README.md#构建与产物)。
- Linux 的 `dotnet` 可在 `PATH`；Windows 使用 `dotnet.exe`。也可用 `--dotnet` 指定。

工具不写 `.ps1` / `.sh`。所有路径相对仓库根，构建产物按
`tools/Directory.Build.props` 写入 `.local/tool-build/`。

## 快速开始

```bash
# Release 模组 + OfflineSearchHarness + K0 纯内核合同
python3 -B tools/search/FlashRegression/flash_regression.py build

# 全量差分回归（600 根，默认 4 并发；输出 .local/flash-regression/diff）
python3 -B tools/search/FlashRegression/flash_regression.py diff --label diff --walks 32

# 只跑一个固定根和一个牌组
python3 -B tools/search/FlashRegression/flash_regression.py diff \
  --label one --only fixtures --root flash-dx5-pen-nib-sly
python3 -B tools/search/FlashRegression/flash_regression.py diff \
  --label one --only decks --root deck-SILENT-13

# 最终候选路线扫描
python3 -B tools/search/FlashRegression/flash_regression.py scan --label scan --candidates 8

# 100 根协调器 Off/On 质量对照（8 个并行进程）；Off 可复用旧结果目录
python3 -B tools/search/FlashRegression/flash_regression.py coordinator --label coord --jobs 8
python3 -B tools/search/FlashRegression/flash_regression.py coordinator \
  --label coord-on --off-baseline .local/flash-regression/coord

# 生成第三幕牌组语料（只写 spec/request，不运行）
python3 -B tools/search/FlashRegression/flash_regression.py survey \
  --character SILENT --count 30 --act 2 --out .local/flash-regression/survey-silent-act3
```

## 子命令

### `build`

执行与旧脚本相同的三步：`dotnet build CombatSolver.csproj -c Release
-p:CopyModOnBuild=false`、`dotnet build tools/search/OfflineSearchHarness/... -c Release`、
`DOTNET_TieredCompilation=0 dotnet run --project tools/testing/checks/FlashKernelChecks -c Release`。
输出 `build.txt` 与 `k0.txt`。`diff` / `scan` 默认先跑这一步；`coordinator` 只做两次
Release 构建，不跑 K0。

### `diff`

K1 单步穷举与 K2 随机游走的差分回归。根集合与旧 `regress.sh` 相同：

| 组 | 数量 | 输入 |
| --- | --- | --- |
| 自然遭遇 | 156 | 五个角色的初始牌组、三幕已实现遭遇的 `FLASHM1`/`FLASHM2`（铁甲战士另有 `FLASH1`） |
| 第三幕首领 | 6 | `AEONGLASS_BOSS`、`QUEEN_BOSS`、`TEST_SUBJECT_BOSS`，`--act-index 2` |
| 固定根 | 274 | `coverage/fixtures/cards/flash-*.json` |
| 生成牌组 | 64 | `coverage/corpora/flash-decks/*/request.json` |
| 质量语料（默认包含） | 100 | `coverage/corpora/flash-quality/*/request.json`；默认每根 48 条游走 |
| 第三幕语料（可选） | 150 | `--include-act3` 或 `--only act3`；默认每根 48 条游走 |

完整默认集合为 600 根；加入第三幕语料后为 750 根。`--only all`（默认）已经包含
`coverage/corpora/flash-quality` 的 100 根；`--only quality` 只跑这组质量语料。每根运行
`OFFLINE_HARNESS_FLASH_DIFF=1 OFFLINE_HARNESS_FLASH_DIFF_WALKS=<walks>`，单根超时 600 秒，
结果写入 `<out>/<root>/`。`SUMMARY.txt` 每行是
`<root> exit=<code> <flash-diff.txt 首行>`，末行 `DONE`。`--baseline previous/SUMMARY.txt`
按旧回归脚本的逐行比较口径：非零退出、缺少首行、`failed`/`walkFailed`/`scoreFailed`/
`boundFailed`/`traitFailed`/`pocketwatchFailed`/`declinedFailed`/`unsupported` 非零、
以及 `pureRefMismatch` 增加都记为 `BAD`，写入 `BASELINE.txt`，有 `BAD` 时进程退出 1。`diff` 在任一根退出非零、首行不是 `SUMMARY failed=0 walkFailed=0` 时退出 1；`scan` 在任一根退出非零或 `mismatches` 非零时退出 1。

分片与并发：`--jobs` 控制同时运行的宿主进程数（默认 4）；`--shards N` 按旧脚本的
“每组根的下标 mod N”分片，`--shard k` 只跑第 k 片。默认 `--shards 1`、不筛选。

### `scan`

与 `diff` 相同的根集合与分片规则，但每根改为 Flash 求解后把排序前 N 条最终候选路线交现有
引擎逐条核对：`OFFLINE_HARNESS_FLASH_SOLVE=1`、
`OFFLINE_HARNESS_FLASH_CANDIDATE_SCAN=<N>`，`--milestone M1 --budget-ms 10000 --profile Low
--beam 24 --nodes 2000 --dop 1`，单根超时 300 秒。`SUMMARY.txt` 每行记录
`exit=`、`routes=`、`mismatches=` 与首个错误。验收要求 `mismatches=0`。

### `coordinator`

默认读取 [质量语料](../../../coverage/corpora/flash-quality/README.md) 的 100 个牌组（五个角色各 20，
第一、二、三幕 7/7/6）。`--deck-dir coverage/corpora/flash-decks` 可另加差分语料中的牌组（默认只取
`--character` 指定角色、非第三幕的根）。每根在
`--search-mode Coordinator --use-portfolio --profile Medium --budget-ms 120000` 下分别跑
`Off` 与 `On`（`OFFLINE_HARNESS_WARMUP_SEARCHES=1`），单次超时 1800 秒。`TABLE.txt` 汇总
`better/same/worse`、战略失血差、采用数与 Off/On 总耗时；`STRATEGIC.txt` 逐根列出
`strategicHpDeficit` 变化。

`--off-baseline <dir>` 复用旧结果的 `<id>/Off`（复制到新输出目录），此时默认只跑 `On`；
`--ids` 限定根；`--modes Off,On` 可显式覆盖；`--include-act3-decks` 把 `--deck-dir` 中的第三幕
根也加入；`--compare <dir> --compare-mode On` 按旧 `qcmp.py` 比较两份输出的逐根
`(won, strategicHpDeficit, combatEndedTurn)`。

### `survey`

按旧 `deck-survey.sh` / `act3-survey.sh` 的口径生成生成牌组语料：固定进阶 10、
`includeStartingDeck`/`includeStartingRelics`/`includeAscendersBane` 为真、
`characterCards.count = 6 + (i % 7)`、`colorlessCards.count = i % 3`、
`relics.count = 2 + i % 4`、`potions.count = i % 3`，遭遇种类按 `i % 3` 取
Monster/Elite/Boss。`--act -1` 按 `i % 2` 交替第一/二幕，`--act 2` 生成第三幕语料。
`--act-blocks 7,7,6` 按连续序号块依次分配第一/二/三幕（覆盖 `--act`）；`--seed-prefix` 改种子前缀
（种子为 `<前缀>-<角色>-<NN>`，默认 `FLASH-DECK`）；`--vary-extras` 让药水与无色牌按 `i // 3`
变化，不再与遭遇种类同步（旧口径下普通战总是 0 瓶药水、首领战总是 2 瓶）。质量语料用
`--count 20 --act-blocks 7,7,6 --seed-prefix FLASH-QUALITY --vary-extras` 生成。
每个子目录写 `spec.json` 与 `request.json`，`generatedScenarioPath` 指向仓库相对路径。
`--run` 额外对每个根跑一次 1 条游走的差分探针，但不替代正式 `diff`。

## 输出与验收

| 子命令 | 输出 | 通过条件 |
| --- | --- | --- |
| `diff` | `SUMMARY.txt`、每根 `stdout.txt` / `flash-diff.txt`、可选 `BASELINE.txt` | 每根 `exit=0`，首行 `SUMMARY failed=0 walkFailed=0`，`unsupported=0`；有 `--baseline` 时 `bad=0` |
| `scan` | `SUMMARY.txt`、每根 `harness-result.json` | 每根 `exit=0`，`mismatches=0` |
| `coordinator` | `TABLE.txt`、`STRATEGIC.txt`、每根 `Off`/`On` | 每根 `FLASH_ADOPTED`，没有 `FLASH_SEARCH_FAILED`；数字按实际结果计入 |
| `survey` | 每根 `spec.json` / `request.json`、`SUMMARY.txt` | 路径可解析；`--run` 时探针输出可读 |

默认输出根目录是 `.local/flash-regression/<label>/`；`--out` 可指定别处。结果、日志与构建
产物都不进入源码提交。`diff` 与旧 `regress.sh` 的 `SUMMARY.txt` 行格式兼容，可直接把旧结果
作为 `--baseline`。

## 已知边界

- `diff`/`scan` 只证明 Flash 与现有引擎一致；现有引擎与原版是否一致由既有的 actual/simulated
  严格差分负责。`coordinator` 只产质量与耗时指标，不做正确性验收。
- `coordinator` 的 Off/On 是同一预算下的单次运行；同代码重复测量时个别根有数点波动，
  不能把单根差当作回归。
- `coordinator` 的预算按墙钟计，记录的数字注明并行进程数。当前口径是 8 个并行进程
  （`--jobs 8`）；与串行相比每个进程都会变慢，79 根校准中 Off 总耗时 +21%、On +12%。
- 第三幕语料默认 48 条游走，其余根默认 32 条；`--walks` / `--act3-walks` 可覆盖。
- 工具参数跨平台，但游戏依赖与 Steam 目录仍由本机 `local.props`/环境决定；没有在 Windows
  上运行的记录时，不把 Linux 结果写成 Windows 结论。
