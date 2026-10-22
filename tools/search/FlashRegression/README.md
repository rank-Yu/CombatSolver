# Flash 回归工具

`flash_regression.py` 是 Flash 搜索后端已提交的回归入口。它把原先散在
`.local/tool-tasks/flash-search/` 的构建、差分、候选扫描、基准、协调器对照、设置兼容检查与牌组生成
收进一个跨平台 Python 入口，供审阅者在仓库内复跑证据。工具只调用
`OfflineSearchHarness`，不重新实现搜索或战斗语义；宿主里的 Flash 探针（差分、动作集合、候选扫描、
路线追踪、设置兼容检查）集中在 `tools/search/OfflineSearchHarness/Flash/`。

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

# 设置持久化兼容（旧 Shadow/数值形式读成 Off，On 序列化不变）
python3 -B tools/search/FlashRegression/flash_regression.py compat --label compat

# 固定 40 根质量语料的开启模式无头整场（Linux；默认 2 条通道）
python3 -B tools/search/FlashRegression/flash_regression.py headless \
  --label hl --lanes 2 \
  --sts2-game-root '<游戏目录>' --ritsu-workshop-root '<RitsuLib 工坊目录>'

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
`SUMMARY failed=0 walkFailed=0` 时退出 1。`--steps <N>` 设置每条游走的步数上限
（`OFFLINE_HARNESS_FLASH_DIFF_STEPS`，默认 60）；`--debug` 打印 K4 参考差异的非 pure 历史条目。

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

输出 `flash-trace.txt`。`--flags` 接受 `band,base,power`；药水参数用 `--max-potions` /
`--min-potions` / `--potion-policy`，incumbent 用 `--incumbent-deficit`+`--incumbent-turn`，
固定前缀调度基线用 `--reset-baseline`。`trace` 只运行宿主，不构建、不跑 K0；它不接受
`--jobs`/`--skip-build`/`--no-k0`。

### `scan`

与 `diff` 相同的根集合与分片规则，但每根只运行一次 Flash：`OFFLINE_HARNESS_FLASH_SOLVE=1`、
`OFFLINE_HARNESS_FLASH_CANDIDATE_SCAN=<N>`，把排序前 N 条最终候选路线交现有引擎逐条核对，
不再运行常规侧或暖跑。参数为 `--milestone M1 --budget-ms 10000 --profile Low --beam 24
--nodes 2000 --dop 1`，单根超时 300 秒。`SUMMARY.txt` 每行记录
`exit=`、`admitted=`、`rejection=`、`routes=`、`mismatches=` 与首个错误。验收要求
`admitted=true` 且 `mismatches=0`；准入拒绝或缺少准入结果都按失败处理。

### `coordinator`

读取 [质量语料](../../../coverage/corpora/flash-quality/README.md) 的 120 个牌组（五个角色各 24，
第一、二、三幕各 8）。每根在
`--search-mode Coordinator --use-portfolio --profile Medium --budget-ms 120000` 下分别跑
`Off` 与 `On`（`OFFLINE_HARNESS_WARMUP_SEARCHES=1`），单次超时 1800 秒。`On` 按 `--profile`
取 Flash 自己的 Beam 宽度与节点上限（Medium 为 240 / 480,000），软时间与分支上限沿用该档位；
`Off` 使用常规档位，`--profile Custom` 不替换。

好/同/差的比较键取求解器 `RouteQualityPolicy` 的 `PotionPolicy` 投影的前四项，依次为：

1. 胜负（胜者优）；
2. 存活（存活者优）；
3. 死亡豁免次数（少者优）；
4. `strategicHpDeficit + potionStrategicCost`（小者优）。

求解器自己还用结束回合（早者优）做最后一级比较，但失血与用药代价相同时，早一回合或晚一回合结束对玩家
没有差别，所以结束回合不决定好/差：这类根计为“同”，另在 `turn_only` 中计数。`qcompare` 判断两次运行
是否逐项一致时仍用含结束回合的完整键。

即药水路线不能靠 `strategicHpDeficit` 单独看起来更好，必须把 `potionStrategicCost` 计入。
`TABLE.txt` 逐根列出 `cost`、`strategicHpDeficit`、`potionStrategicCost`、回合、墙钟和
`better/same/worse`，末行给出 `both_won`（两种模式都获胜的根数）、`cost_delta`（这些根上 On−Off 的合计）、
`on_only_wins`/`off_only_wins`（只有一方获胜的根数）、`potion_cost_diff`（药水成本不同的根数）、
胜负合计与墙钟合计；输掉的战斗的代价与获胜的不可比，所以不计入 `cost_delta`；`STRATEGIC.txt` 逐根列出同一键下的
`strategic`/`potion`/`deathSaves`/`turn`/`lost` 变化；`TABLE-GROUPS.txt` 是 `table` 的
按角色/按幕/按遭遇类型表。单模式运行（`--off-baseline` 默认只跑 `On`，或 `--modes Off`）
只写 `TABLE.txt` 的单模式逐根汇总，不生成 `STRATEGIC.txt`/`TABLE-GROUPS.txt`。

`--off-baseline <dir>` 复用旧结果的 `<id>/Off`（复制到新输出目录），此时默认只跑 `On`；
`--ids` 限定根；`--modes Off,On` 可显式覆盖；`--quality-dir` 改用别的语料目录；
`--compare <dir> --compare-mode On` 逐根比较两份输出在同一 `PotionPolicy` 键下的结果。
有失败根（`FLASH_SEARCH_FAILED`）或缺失 `quality.json` 的根时命令以非 0 退出（缺失与失败
计数也写在 `TABLE-GROUPS.txt` 的合计行）。

### `headless`

Linux 无头整场入口：从质量语料固定挑 40 根，逐场调用
`tools/testing/run-unattended-test.sh` 做开启模式（或关闭模式对照）的全自动部署，直到战斗结束。
当前只接入 Bash 启动器，未接入 Windows PowerShell `.ps1`；在非 Linux 平台运行会直接拒绝。

默认 40 根写死在工具里：五个角色（`IRONCLAD`、`SILENT`、`DEFECT`、`REGENT`、`NECROBINDER`）
各取序号 `000 001 002`（第一幕普通/精英/首领）、`009 010 011`（第二幕普通/精英/首领）与
`019 020`（第三幕精英/首领）。`--ids` 覆盖清单（空格或逗号分隔的语料目录名）；`--quality-dir`
可改用别的语料目录。默认输出 `.local/flash-regression/headless/`，`--label`/`--out` 可改。跑全部
120 根时传 `--ids $(ls coverage/corpora/flash-quality | grep -E '^[A-Z]+-[0-9]{3}$')`。

每场从 `coverage/corpora/flash-quality/<id>/spec.json` 派生 `<out>/<id>/scenario.json`，把生成模式
改为 `Deploy`，再传 `--generated-scenario-path`、`--flash-search-mode-for-test On|Off`（`--mode`，默认
`On`）、`--cards-json []` 与 `--search-budget-override-milliseconds`（`--search-budget-ms`，默认
`120000`，与质量语料协调器同预算）。生成场景在固化请求里保留原生战斗状态，所以敌人生命是自然值，
手牌不会注入额外的打击；工具不传 `--enemy-current-hp`、`--initial-enemy-*` 或
`--initial-player-hp`。回合准备
根会在模式覆盖生效前捕获，因此在启动游戏前把 `flashSearchMode` 写入实例的
`data/SlayTheSpire2/combat_solver_settings.json`；每条通道模式固定，后续复用同一进程时设置保持一致。

并行与实例：

- `--lanes N`（默认 2）把清单轮转分配到 N 条通道，每通道顺序跑自己的一段。
- 所有通道共用同一个 `COMBATSOLVER_HEADLESS_HOST_ROOT`（默认 `.local/headless-host`，
  `--headless-host-root` 或同名环境变量可覆盖）。
- 每条通道一个实例，目录固定为 `.local/headless-instances/<label>-<通道号>`；通道内复用实例，每场
  结束先把证据拷出实例并清掉 `combat-solver-routes`、问题包与上一场结果，再跑下一场。
- 每条通道最后一场带 `--cleanup-instance-on-exit`。工具在该场结束后检查实例目录已删除；目录仍在时
  先停止实例再删除，并把该通道标成清理失败、命令退出码非 0。
- 证据放在 `<out>/<id>/evidence/`：`generated-scenario.resolved/loadout/opening/request.json`、
  `result.json`、`runtime-events.json`、`search-result.json`、`policy.json`、`timings.json`、
  `report.zip` 与解包后的 `report/`、`instance-logs/`。最后一场会在启动器删除实例前用轮询把
  报告包和实例日志拷出。启动器输出在 `<out>/<id>/run.log`。

汇总写 `<out>/SUMMARY.txt` 并打印，同时写机器可读的 `<out>/results.json`。每行是：

```text
id  mode  status  outcome  turn  hp  maxHp  adopted  verified  searchFailed
verifyMismatch  previewMismatch  replans  scenario  exit  cleanup  evidence
```

`outcome` 取 `win`、`death`、`failed` 或 `no-result`；`turn`/`hp`/`maxHp` 取
`NativeOutcome:combatEnded=...` 完成检查。玩家战死时执行器可能先以“仍存在未死亡敌人”结束、不写
`NativeOutcome`。执行器只在原生战斗进入结束状态（`IsOverOrEnding`）后才报这条错误；玩家在自己回合
结束时战死，写结果时游戏可能还没离开战斗，`result.json` 的 `combatEnded` 仍为 `false`、`stage` 为
`wait_combat_end`。工具把这两种情况都按原生结束处理，用 `result.json` 的 `FinishedTurn` 与
实际 `generated-scenario.loadout.json` 的最大生命记为 `death`、HP 0，不读取 `solverMetrics.finalHp`。
`adopted` 是 `FLASH_ADOPTED` 行数，`verified` 是其中 `status=verified` 的行数；
`searchFailed`、`verifyMismatch`、`previewMismatch` 与 `replans` 分别统计
`FLASH_SEARCH_FAILED`、`FLASH_VERIFY_MISMATCH`、`FLASH_PREVIEW_MISMATCH` 与 `UnexpectedReplans:N`。
`scenario` 检查请求 `cards=[]`、原生状态保留、未设置生命覆盖、玩家满血开局、敌人生命不是注入的 1，
并可选和 `--scenario-baseline` 的旧证据逐字段比较（忽略生成模式 `mode`）。

验收条件：每场 `outcome` 为 `win` 或 `death`，`searchFailed=0`、`verifyMismatch=0`、`replans=0`、
`scenario=ok`，且每条通道 `cleanup=ok`。以下情况命令退出码非 0：`FLASH_SEARCH_FAILED`、核对不一致、
计划外重算、没有结果（缺少原生结束状态）、场景不一致、通道清理失败或通道异常。预览不一致只汇总，
单独出现时不改变退出码。玩家战死本身不是失败；它的启动器退出码可能是 1（`result.json` 为
`Failed`），工具在 `exit` 列记录而不据此改判。

路径全部透传，不写死个人目录：`--sts2-game-root`、`--ritsu-workshop-root`、
`--combat-solver-build-dir`。`--xdg-data-home` 设置启动器的 `XDG_DATA_HOME`，用作复制玩家档的
种子目录；vps4 的默认值是仓库内 `.local/headless-seed/data`（存在时），其中必须有
`SlayTheSpire2/default/1/settings.save`。工具默认先做一次 Release 构建（`-p:CopyModOnBuild=false -p:NuGetAudit=false`），
`--skip-build` 复用已有 DLL；使用冻结目录时与 `--combat-solver-build-dir` 一起传 `--skip-build`。

两根场景一致性核对示例（基线是离线宿主对同一 `spec.json` 的解析证据）：

```bash
python3 -B tools/search/FlashRegression/flash_regression.py headless \
  --label hl-check --ids IRONCLAD-000 SILENT-000 --lanes 2 --timeout-seconds 300 \
  --search-budget-ms 120000 \
  --sts2-game-root '<游戏目录>' --ritsu-workshop-root '<RitsuLib 工坊目录>' \
  --scenario-baseline '<旧证据根>'
```

`--scenario-baseline` 接受 `<根>/<id>/On/evidence`、`<根>/<id>/evidence` 或 `<根>/<id>` 布局；每根逐字段
比较 `generated-scenario.resolved.json`（忽略 `mode`）与 `generated-scenario.loadout.json`，缺基线或
字段不同都写入 `scenario=mismatch:...` 并使命令失败。

### `compat`

设置持久化兼容合同：用离线宿主加载旧设置 JSON、检查点/问题包策略 JSON 与设置序列化器，断言
`"Shadow"`、大小写变体与任何数值枚举形式都读成 `Off` 且不抛反序列化异常，`On` 仍序列化为
字符串 `"On"` 并可往返。命令先执行 Release 构建（`--skip-build` 可复用），然后运行
`OfflineSearchHarness --check-flash-mode-compat`；输出末行 `FLASH_MODE_COMPAT status=Passed
assertions=<N>`，非 0 退出或缺少该行即失败。不启动游戏搜索，可重复运行。

```bash
python3 -B tools/search/FlashRegression/flash_regression.py compat --label compat
```

### `fallback`

开启模式准入改用合同。固定输入为
`coverage/fixtures/search/flash-fallback-soulbound-power.json`：玩家携带原版尚无 Flash 定义的
`SOULBOUND_POWER`，根展开量超过常规 Beam 宽度，能区分常规与 Flash 档位。命令先执行 Release
构建与 K0（`--skip-build`/`--no-k0` 可复用），然后跑一次 On 离线宿主；宿主在正常搜索前通过
真实的 `CombatSearchCoordinator.Solve` 依次执行：

- Off：常规档位基线（默认 Low 档、Beam 45 / 5,000 节点）；
- On：`BeamSolveDispatch` 抛 `FlashFallbackRequiredException`，协调器写一行 `FLASH_FALLBACK`
  后用 `RegularProfile`（45 / 5,000）整次重跑；
- 敏感性：`FlashMode = Off` 但 `Profile` 使用 Flash 档位（180 / 240,000），工作量必须与 Off 不同；
- 已发布：预置路线采纳种子后再触发回落，必须写同一 reason 的 `FLASH_SEARCH_FAILED`、不写
  `FLASH_FALLBACK`、不重跑；
- On + 未登记 `DirectSearchPurpose`：断言仍抛 `FlashSearchFailedException`
  （`admission-unsupported-request`），且不写 `FLASH_FALLBACK`。

通过条件是 `flash-fallback-checks.json` 的 `status=Passed`、`routeSame`/`qualitySame`/`workSame`
全为 true、`sensitivityDiffers=true`、`publishedFallbackBlocked=true`、恰好一条 `FLASH_FALLBACK`、
失败类别为 `admission-unsupported-request`；同时正常 On 搜索的宿主日志必须恰好一行
`FLASH_FALLBACK` 且不含 `FLASH_SEARCH_FAILED`。输出 `SUMMARY.txt` 与
`flash-fallback-checks.json`。

```bash
python3 -B tools/search/FlashRegression/flash_regression.py fallback --label fallback --jobs 8
```

### `table`

只读一个已有协调器输出目录（`<id>/{Off,On}/quality.json`、`harness-result.json` 与 On 日志），
按求解器的 `PotionPolicy` 键输出：

- 合计：根数、好/同/差、`turn_only`（只差结束回合的根数，已计入“同”）、`both_won`、`cost_delta`（两种模式都获胜的根上 On−Off 的战略失血+药水成本合计）、
  `on_only_wins`/`off_only_wins`、`potion_cost_diff`（药水成本不同的根数）、Off/On 胜场、两种模式墙钟合计、缺失根数、
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

口径核对：CL2 合入后的 120 根为 `29/81/10`、`turn_only=9`、`both_won=87`、`cost_delta=-28`、`on_only_wins=4`、
`off_only_wins=0`（顺序为 好/同/差）。这些数字只用于口径核对，不是新的质量门禁。

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
| `scan` | `SUMMARY.txt`、每根 `harness-result.json` | 每根 `exit=0`、`admitted=true`、`mismatches=0` |
| `coordinator` | `TABLE.txt`、`STRATEGIC.txt`、`TABLE-GROUPS.txt`、每根 `Off`/`On` | 每根 `FLASH_ADOPTED`，没有 `FLASH_SEARCH_FAILED`，没有缺失根；数字按 `PotionPolicy` 键计入；不满足时退出码非 0 |
| `headless` | `SUMMARY.txt`、`results.json`、每根 `evidence/` | 每根 `outcome=win/death`、`searchFailed=0`、`verifyMismatch=0`、`replans=0`、`scenario=ok`；每条通道清理完成 |
| `compat` | `compat.txt` | `FLASH_MODE_COMPAT status=Passed` |
| `fallback` | `SUMMARY.txt`、`flash-fallback-checks.json`、On `logs/` | `status=Passed`，Off/On 路线、质量与工作量相同，`sensitivityDiffers=true`，恰好一行 `FLASH_FALLBACK` 且不写 `FLASH_SEARCH_FAILED`，已发布回落不重跑，未登记搜索用途仍失败 |
| `table` | 标准输出的合计/分组/变差列表 | 只读；缺 `spec.json` 时归入 `unknown`，不伪造分组 |
| `trace` | `flash-trace.txt`、`stdout.txt` | 目标路线可映射；同指纹去重会 `re-anchored` 后继续，真正丢失点写 `cut`/`first-missing`（`seat replaced` 保持原语义） |
| `survey` | 每根 `spec.json` / `request.json`、`SUMMARY.txt` | 路径可解析；`--run` 时探针输出可读 |

默认输出根目录是 `.local/flash-regression/<label>/`；`--out` 可指定别处。结果、日志与构建
产物都不进入源码提交。

## 已知边界

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
- `headless` 只支持 Linux 的 Bash 启动器，未接入 Windows 的 `.ps1` 入口。它统计的
  `previewMismatch` 只报告进度预览候选的回放差异，不作为失败条件；最终路线仍以
  `verifyMismatch` 与 `FLASH_SEARCH_FAILED` 为准。玩家战死的 `result.json` 可能是 `Failed`
  且启动器退出码为 1，判定以工具汇总的 `outcome` 与证据为准。
