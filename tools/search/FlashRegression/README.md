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

# 全量差分回归（410 根；输出 .local/flash-regression/diff）
python3 -B tools/search/FlashRegression/flash_regression.py diff --label diff --jobs 8

# 只跑一个固定根或一个质量牌组
python3 -B tools/search/FlashRegression/flash_regression.py diff \
  --label one --only fixtures --root flash-dx5-pen-nib-sly
python3 -B tools/search/FlashRegression/flash_regression.py diff \
  --label one --only quality --root quality-SILENT-007

# 最终候选路线扫描
python3 -B tools/search/FlashRegression/flash_regression.py scan --label scan --candidates 8 --jobs 8

# 120 根协调器 Off/On 质量对照（8 个并行进程）；Off 可复用旧结果目录
python3 -B tools/search/FlashRegression/flash_regression.py coordinator --label coord --jobs 8
python3 -B tools/search/FlashRegression/flash_regression.py coordinator \
  --label coord-on --off-baseline .local/flash-regression/coord

# K6：逐层保留集合对照（120 根 × 实际运行成员；默认 8 个并行进程）
python3 -B tools/search/FlashRegression/flash_regression.py retention --label k6 --jobs 8
python3 -B tools/search/FlashRegression/flash_regression.py retention \
  --label k6-b0 --jobs 8 --shards 3 --shard 0

# 只读已有协调器结果的合计/按角色/按幕/按遭遇类型表（不重跑协调器）
python3 -B tools/search/FlashRegression/flash_regression.py table \
  .local/flash-regression/qf4m-q120 --corpus coverage/corpora/flash-quality

# 按质量语料口径生成一个角色的牌组（只写 spec/request，不运行）
python3 -B tools/search/FlashRegression/flash_regression.py survey \
  --character SILENT --out .local/flash-regression/survey-silent
```

## 子命令

### `build`

执行与旧脚本相同的三步：`dotnet build CombatSolver.csproj -c Release
-p:CopyModOnBuild=false`、`dotnet build tools/search/OfflineSearchHarness/... -c Release`、
`DOTNET_TieredCompilation=0 dotnet run --project tools/testing/checks/FlashKernelChecks -c Release`。
输出 `build.txt` 与 `k0.txt`。`diff` / `scan` 默认先跑这一步；`coordinator` 只做两次
Release 构建，不跑 K0。

### `diff`

K1 单步穷举与 K2 随机游走的差分回归，默认根集合：

| 组 | 数量 | 输入 |
| --- | --- | --- |
| 固定根 | 290 | `coverage/fixtures/cards/flash-*.json`，默认每根 32 条游走 |
| 质量语料 | 120 | `coverage/corpora/flash-quality/*/request.json`，默认每根 48 条游走（`--quality-walks`） |

默认集合共 410 根；`--only fixtures` / `--only quality` 只跑其中一组。每根运行
`OFFLINE_HARNESS_FLASH_DIFF=1 OFFLINE_HARNESS_FLASH_DIFF_WALKS=<walks>`，单根超时 600 秒，
结果写入 `<out>/<root>/`（每次运行前重建该目录）。`SUMMARY.txt` 每行是
`<root> exit=<code> <flash-diff.txt 首行>`，末行 `DONE`。`--baseline previous/SUMMARY.txt`
逐行比较：非零退出、缺少首行、`failed`/`walkFailed`/`scoreFailed`/`boundFailed`/`traitFailed`/
`pocketwatchFailed`/`declinedFailed`/`unsupported` 非零、以及 `pureRefMismatch` 增加都记为 `BAD`，
写入 `BASELINE.txt`，有 `BAD` 时进程退出 1。`diff` 在任一根退出非零、首行不是
`SUMMARY failed=0 walkFailed=0` 时退出 1；`scan` 在任一根退出非零或 `mismatches` 非零时退出 1。

分片与并发：`--jobs` 控制同时运行的宿主进程数（默认 4）；`--shards N` 按
“每组根的下标 mod N”分片，`--shard k` 只跑第 k 片。默认 `--shards 1`、不筛选。

### `trace`

对一个质量语料根运行单条请求的路线追踪：目标 `route.json` 来自关闭模式（或指定阶段），
工具把每一步映射到 Flash 动作，并报告目标状态在 Flash 侧的生成、去重顶替、排名截断、
必保泳道/分组、下界剪枝与回合结束压缩；同一请求同时跑现有求解器，报告目标状态每层的
原始排名、必保席位与选中席位。追踪默认关闭，不改变任何选择结果。

```bash
python3 -B tools/search/FlashRegression/flash_regression.py trace \
  --id DEFECT-012 --route .local/tool-tasks/flash-search/qr-evidence/DEFECT-012/coord-Off/route.json \
  --purpose SmartOpeningPotionPosterior --continuation --prefix 1 --beam 60 \
  --nodes 120000 --budget-ms 120000 --side both
```

输出 `flash-trace.txt`。`--flags` 接受 `band,base,power`，药水/incumbent 参数见
`--help`；`--skip-build` 复用现有 Release 构建。

### `scan`

与 `diff` 相同的根集合与分片规则，但每根改为 Flash 求解后把排序前 N 条最终候选路线交现有
引擎逐条核对：`OFFLINE_HARNESS_FLASH_SOLVE=1`、
`OFFLINE_HARNESS_FLASH_CANDIDATE_SCAN=<N>`，`--milestone M1 --budget-ms 10000 --profile Low
--beam 24 --nodes 2000 --dop 1`，单根超时 300 秒。`SUMMARY.txt` 每行记录
`exit=`、`routes=`、`mismatches=` 与首个错误。验收要求 `mismatches=0`。

### `coordinator`

读取 [质量语料](../../../coverage/corpora/flash-quality/README.md) 的 120 个牌组（五个角色各 24，
第一、二、三幕各 8）。每根在
`--search-mode Coordinator --use-portfolio --profile Medium --budget-ms 120000` 下分别跑
`Off` 与 `On`（`OFFLINE_HARNESS_WARMUP_SEARCHES=1`），单次超时 1800 秒。Flash 与常规搜索使用同一
档位的 Beam 宽度、节点上限与软时间。

比较键与求解器的 `RouteQualityPolicy` 的 `PotionPolicy` 投影一致，依次为：

1. 胜负（胜者优）；
2. 存活（存活者优）；
3. 死亡豁免次数（少者优）；
4. `strategicHpDeficit + potionStrategicCost`（小者优）；
5. 结束回合（早者优）。

即药水路线不能靠 `strategicHpDeficit` 单独看起来更好，必须把 `potionStrategicCost` 计入。
`TABLE.txt` 逐根列出 `cost`、`strategicHpDeficit`、`potionStrategicCost`、回合、墙钟和
`better/same/worse`，末行给出 `cost_delta`（On−Off 的合计）、`potion_cost_diff`（药水成本
不同的根数）、胜负合计与墙钟合计；`STRATEGIC.txt` 逐根列出同一键下的
`strategic`/`potion`/`deathSaves`/`turn`/`lost` 变化；`TABLE-GROUPS.txt` 是 `table` 的
按角色/按幕/按遭遇类型表。

`--off-baseline <dir>` 复用旧结果的 `<id>/Off`（复制到新输出目录），此时默认只跑 `On`；
`--ids` 限定根；`--modes Off,On` 可显式覆盖；`--quality-dir` 改用别的语料目录；
`--compare <dir> --compare-mode On` 逐根比较两份输出在同一 `PotionPolicy` 键下的结果。

### `retention`

K6 逐层保留集合对照：对质量语料的每个根，读取 `--baseline`（默认
`.local/flash-regression/off120-base`）里该根 `Off` 的 `PortfolioMembers`，只取 `Ran=true`
的成员，按成员的 `BeamWidth`/`SecondRankBand`/`BaseScoreOnly`/`AggressivePowerCommitment`/
`OffensiveRefinement`/`BoundedRefinement`/`NodeBudget` 重建同一请求，在 M1 分别运行常规
`CombatBeamSolver` 与 `FlashRouteMaterializer`。每次运行打开
`OFFLINE_HARNESS_RETENTION_DIFF=1`，工具只读 `retention-diff.json`，不重算搜索或状态键。

```bash
python3 -B tools/search/FlashRegression/flash_regression.py retention \
  --label k6 --jobs 8 --shards 3 --shard 0 \
  --baseline .local/flash-regression/off120-base
```

- `--ids` 限定质量语料目录名；`--root` 仍按子串过滤；`--shards/--shard` 与 `diff` 相同。
- `--top` 控制第一处分歧两侧各报告多少个状态（默认 8）；`--budget-ms` 默认 120000，
  两边使用同一条软时间。
- 有界精炼成员的 `NodeBudget` 可能小于设置校验下限 100：工具把 CLI `--nodes` 抬到 100，
  再用 `OFFLINE_HARNESS_RETENTION_NODE_BUDGET` 给两边精确预算，保证成员身份不被设置校验改写。
- 输出在 `--out` 或 `.local/flash-regression/<label>/`：每根/成员一个目录，含
  `retention-diff.json`；汇总写 `SUMMARY.txt` 与 `SUMMARY.json`，成员表列为
  `runs/identical/routeSame/divergent/incomparable/error` 和 A–G 分布。
  `incomparable` 表示该运行出现不可投影状态，不进入类别和回合/层深统计。
- `--merge <dir>...` 不重跑，直接从已有每根/成员 `retention-diff.json` 重新汇总；
  适合分片跑完后合并。
- 通过条件：所有进程 `exit=0` 且 `status` 不是 `error`。保留集合不同是测量结果，不是命令失败。
- 工具默认先构建 Release + 宿主 + K0；`--skip-build` 复用现有构建。

### `table`

只读一个已有协调器输出目录（`<id>/{Off,On}/quality.json`、`harness-result.json` 与 On 日志），
按求解器的 `PotionPolicy` 键输出：

- 合计：根数、好/同/差、`cost_delta`（On−Off 的战略失血+药水成本合计）、
  `potion_cost_diff`（药水成本不同的根数）、Off/On 胜场、两种模式墙钟合计、缺失根数、
  `FLASH_SEARCH_FAILED` 根数；
- 按 `spec.json` 的 `characterId`、`actIndex`、`encounterKind` 分组的同一组列；
- 变差根列表（Off/On 键、成本、药水成本变化）。

```bash
python3 -B tools/search/FlashRegression/flash_regression.py table \
  .local/flash-regression/qf4m-q120 --corpus coverage/corpora/flash-quality
```

`--corpus` 指向含 `<id>/spec.json` 的语料目录；省略时分组退化为 `unknown`。该子命令不构建、
不运行宿主，也不改结果目录。`coordinator` 结束时调用同一个 `quality_table` 函数并把结果写入
`TABLE-GROUPS.txt`。

已用三个历史目录核对：120 根 `29/74/17`、`cost_delta=-17`；79 根 `19/50/10`、`-60`；
100 根 `30/60/10`、`+226`（顺序为 好/同/差）。这些数字只用于口径核对，不是新的质量门禁。

### `survey`

按质量语料的固定口径为一个角色生成 `--count` 个牌组（默认 24）：

- 种子 `FLASH-QUALITY-<角色>-<NNN>`，目录名 `<角色>-<NNN>`；
- 编号按连续三等分取第一、二、三幕（24 个即各 8 个）；遭遇种类按 `i % 3` 取 Monster/Elite/Boss；
- 固定进阶 10，`includeStartingDeck`/`includeStartingRelics`/`includeAscendersBane` 为真；额外角色卡
  `6 + i % 7` 张（升级 1 级）、遗物 `2 + i % 4` 个；无色牌 `(i // 3 + 1) % 3` 张、药水 `(i // 3) % 3` 瓶，
  这两项不随遭遇种类同步变化，每种遭遇都有 0、1、2 瓶药水的牌组。

每个子目录写 `spec.json` 与 `request.json`，`generatedScenarioPath` 指向仓库相对路径。五个角色各跑
一次、`--out coverage/corpora/flash-quality` 即逐字节重建质量语料。`--run` 额外对每个根跑一次
1 条游走的差分探针，但不替代正式 `diff`。

## 输出与验收

| 子命令 | 输出 | 通过条件 |
| --- | --- | --- |
| `diff` | `SUMMARY.txt`、每根 `stdout.txt` / `flash-diff.txt`、可选 `BASELINE.txt` | 每根 `exit=0`，首行 `SUMMARY failed=0 walkFailed=0`，`actionSetFailed=0`，`unsupported=0`；有 `--baseline` 时 `bad=0` |
| `scan` | `SUMMARY.txt`、每根 `harness-result.json` | 每根 `exit=0`，`mismatches=0` |
| `coordinator` | `TABLE.txt`、`STRATEGIC.txt`、`TABLE-GROUPS.txt`、每根 `Off`/`On` | 每根 `FLASH_ADOPTED`，没有 `FLASH_SEARCH_FAILED`；数字按 `PotionPolicy` 键计入 |
| `table` | 标准输出的合计/分组/变差列表 | 只读；缺 `spec.json` 时归入 `unknown`，不伪造分组 |
| `trace` | `flash-trace.txt`、`stdout.txt` | 目标路线可映射；同指纹去重会 `re-anchored` 后继续，真正丢失点写 `cut`/`first-missing`（`seat replaced` 保持原语义） |
| `retention` | `SUMMARY.txt`、`SUMMARY.json`、每根/成员 `retention-diff.json` | 每进程 `exit=0` 且无 `status=error`；集合分歧按 A–G 汇总 |
| `survey` | 每根 `spec.json` / `request.json`、`SUMMARY.txt` | 路径可解析；`--run` 时探针输出可读 |

默认输出根目录是 `.local/flash-regression/<label>/`；`--out` 可指定别处。结果、日志与构建
产物都不进入源码提交。

## 已知边界

- `retention` 只比较同请求、同目的、同宽度/开关/节点/软时间/incumbent/药水政策时的逐边界保留集合与最终路线；当前覆盖 `PrimaryBeam` 普通回合搜索，不含 turn-setup 根、novelty、fixed-prefix continuation 与 incumbent 审计成员。它不修改搜索行为，也不构成正确性验收。第一处分歧的类别与具体原因见 `docs/OFFLINE_SEARCH_HARNESS.md`。
- `diff`/`scan` 只证明 Flash 与现有引擎一致；现有引擎与原版是否一致由既有的 actual/simulated
  严格差分负责。`diff` 的 `actionSetFailed` 是动作集合核对：同一状态下现有求解器计划动作与
  Flash 合法动作的规范化键（种类、卡牌/药水、目标、药水槽位、药水是否需要选择）；卡牌选择在
  展开时附加，由 K1 的逐动作选择请求对账。`coordinator` 只产质量与耗时指标，不做正确性验收。
- `coordinator` 的 Off/On 是同一预算下的单次运行；同代码重复测量时个别根有数点波动，
  不能把单根差当作回归。
- `coordinator` 的预算按墙钟计，记录的数字注明并行进程数。当前口径是 8 个并行进程
  （`--jobs 8`）；与串行相比每个进程都会变慢，79 根校准中 Off 总耗时 +21%、On +12%。
- 质量语料默认 48 条游走，固定根默认 32 条；`--quality-walks` / `--walks` 可覆盖。
- 工具参数跨平台，但游戏依赖与 Steam 目录仍由本机 `local.props`/环境决定；没有在 Windows
  上运行的记录时，不把 Linux 结果写成 Windows 结论。
