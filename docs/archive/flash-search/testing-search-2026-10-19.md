# Flash 测试记录（搜索演进：评估移植、宽度基准、incumbent 剪枝）

[返回 Flash 归档](README.md)

归档日期：2026-10-19。本卷保留 S1 评估移植与 K4、48 根宽度与状态池基准、SB incumbent 下界剪枝三节的完整表格与分析，章节编号沿用原测试记录。当前默认值与协调器层面的证据见 [Flash 测试记录](../../flash-search/testing.md)。

### 2.2 评估移植与 K4（S1，2026-10-18）

`FlashScorer` 移植现有搜索的状态评估（`CombatBeamSolver.Snapshot`）与中途排序（`BeamRankScore`），`FlashScoreBridge` 在根上编译逐内容数值表并冻结请求设置；FlashBeam 改用它们排序。新增路径计数 `LongTermResourceValue`、`AngerCopiesGenerated`、`DeathSaveRelicHpRestored`、`DeathSavePotionHpRestored`、`DeathSaveUseCount`、`AutomaticPotionUseCount`。

K4 证据：22 个自然遭遇根与 57 个固定根（每根 32 条游走）中投影一致的全部状态，评估逐字段相等，`Score` 逐位相等；没有根因设置被拒绝。

评估暂不移植、因而在根上拒绝的设置：非零成长额度、非空遗物计数目标（遗物策略默认开启，持有计数类遗物的局会被拒绝并回落）、第三幕首领策略、开发策略、第三方战略效果与移除估值登记、`Profile` 的仅基础分/连续威胁/权重扰动/上下文排序、偷窃遭遇、瀑布巨人与遗忘者的伤害特例。

端到端对照（FLASHM1，预算 10 秒，同宽度同节点）：

| 根 | 现有搜索失血 / 结束回合 / 耗时 | Flash 失血 / 结束回合 / 端到端耗时（其中搜索） |
| --- | --- | --- |
| `NIBBITS_NORMAL` | 29 / 8 / 587 ms | 37 / 8 / 218 ms（87 ms） |
| `SLIMES_NORMAL` | 19 / 8 / 773 ms | 20 / 12 / 304 ms（157 ms） |
| `RUBY_RAIDERS_NORMAL` | 35 / 5 / 544 ms | 37 / 5 / 280 ms（151 ms） |

结论：评估已与现有搜索一致，剩余质量差距来自搜索结构（去重保留、层内泳道、按层预算、终局比较器，见 [搜索与接入](../../flash-search/search.md#1-flashbeamsolver)）。

### 2.3 质量基准（48 根，2026-10-18）

`.local/tool-tasks/flash-search/bench.sh`（一次性工具，任务结束随 `.local` 清理）：24 个已准入自然遭遇 × 种子 FLASHM1/FLASHM2，铁甲战士初始牌组，宿主默认档位（Beam 24、2000 节点、预算 10 秒），同一根先跑现有 `CombatBeamSolver`，再跑 Flash（准入 → FlashBeam → 逐步核对 → 物化，取热运行耗时）。按“胜利、失血、结束回合”比较：

| Flash 配置 | 更好 / 相同 / 更差 | 失血合计差 | 总耗时：现有 → Flash |
| --- | --- | --- | --- |
| 评估移植后（S1） | 7 / 20 / 21 | +78 | 24.3 s → 5.0 s |
| 加终局比较器 | 7 / 22 / 19 | +81 | 24.4 s → 5.2 s |
| 同上，进攻席位 0 | 3 / 12 / 33 | +342 | 24.9 s → 5.7 s |
| 同上，宽度与节点 ×2 | 8 / 27 / 13 | +41 | 25.2 s → 8.0 s |
| 同上，宽度与节点 ×3 | 11 / 27 / 10 | +24 | 24.6 s → 9.4 s |
| 同上，×3，进攻席位 40% | 10 / 26 / 12 | +27 | 25.1 s → 9.7 s |
| 同上，×4 | 13 / 28 / 7 | −9 | 25.3 s → 11.4 s |
| ×4，状态池按需增长 | 13 / 28 / 7 | −9 | 24.3 s → 12.6 s |
| ×4，子状态在池槽内原地执行（当前默认） | 13 / 28 / 7 | −9 | 24.5 s → 10.5 s |

结论：终局比较器修正了“同失血但多拖回合”的选择；进攻席位不可去掉，加大到 40% 也没有收益；同宽度下 FlashBeam 的保留结构仍弱于现有搜索。宽度与节点 ×4 是第一个总失血优于现有搜索的档位，端到端耗时约为其 0.5，因此作为 `FlashRouteMaterializer.ProfileWidthScale` 的默认值（软时间预算不变）。

状态池原按最坏情况预分配（每层宽度 × 122 个 `FlashBattle`，宽度 96 时四个池约 290 MB），现改为按需增长：48 根的路线与改动前逐根相同，每次求解的托管分配平均 19.8 MB、最大 33.6 MB。两次运行的总耗时 11.4 s 与 12.6 s 之差在单根 ±300 ms 的波动之内，未单独确认。游戏内档位（宽度 45/90/135）未做基准。

### 2.4 主搜索 incumbent 下界剪枝（SB，2026-10-19）

`FlashBeamSolver` 移植现有主搜索成员的局部 HP 下界剪枝。`FlashBoundFeatures` 在根上冻结现有引擎给出的 `FutureHealPotential`、剩余治疗证书（Component / KnownNative）、战略首领减伤与初始 incumbent；求值只读 `FlashBattle` 和冻结表。完整胜利在终局提交时收紧 incumbent，每个动作层保留之后与回合边界保留之后各剪一次；被剪掉的席位不补。共享 `PrimaryIncumbents` 表不接入，初始 `PrimaryIncumbent` 由 `BeamSolveDispatch.SolveFlash` 传入。

K5 证据（全量回归 `regress.sh sb-regression 32`）：33 个自然根与 68 个固定根，98 个根 `bound=checked`、3 个根显式禁用（`flash-p3-b-block-exhaust`、`flash-p3-d-feed-fatal` 为 `bound=disabled(growth_targets)`，`flash-p9-offensive-powers` 为 `bound=disabled(regen_potion_policy)`）；每根 `failed=0`、`walkFailed=0`、`scoreFailed=0`、`boundFailed=0`，无 REJECTED。代表首行：

```
SUMMARY oneStep=6 failed=0 walkTransitions=594 walkFailed=0 terminals=32 choiceActions=0 choiceCapped=0 choiceDeadEnds=0 endings=Victory/None:32 score=checked scoreChecked=601 scoreFailed=0 scoreSkippedBoundary=0 bound=checked boundChecked=601 boundFailed=0
```

质量下降的 6 个根另做 128 条游走定向 K5：`FUZZY_WURM_CRAWLER_WEAK-FLASHM2`、`NIBBITS_WEAK-FLASHM1`、`PUNCH_CONSTRUCT_NORMAL-FLASHM1`、`SEAPUNK_WEAK-FLASHM1`、`SLIMES_WEAK-FLASHM1`、`SLIMES_WEAK-FLASHM2`，合计 `boundChecked=17043`、`boundFailed=0`，下界输入在这些状态上仍逐字段一致。

质量与耗时（同一台机器；基线为改动前构建，加剪枝为改动后构建；每行的现有搜索与 Flash 来自同一次运行。24 个自然遭遇 × FLASHM1/FLASHM2，预算 10 秒，宽度与节点 ×4）：

| Flash 配置 | 更好 / 相同 / 更差 | 失血合计差 | 现有耗时 → Flash 热耗时 | 扩展数合计 | 转移数合计 |
| --- | --- | --- | --- | --- | --- |
| 无剪枝（改动前默认） | 13 / 28 / 7 | −9 | 24.6 s → 11.2 s | 243,294 | 1,038,405 |
| 加 incumbent 下界剪枝 | 14 / 24 / 10 | −16 | 23.8 s → 5.5 s | 64,058 | 283,787 |

`searchMs` 合计从 10,018.7 ms 降到 4,319.8 ms，转移数减少 72.7%。协调器 Off/On（`coord.sh sb-coord 10000 M2 FLASHM1 FLASHM2`，`WARMUPS=2`）：

| 配置 | On 更好 / 相同 / 更差 | 失血合计差 | Off 总耗时 | On 总耗时 | On 模式 `flash_transitions` 合计 |
| --- | --- | --- | --- | --- | --- |
| 改动前 | 13 / 28 / 7 | −2 | 18.5 s | 17.8 s | 875,377 |
| 加剪枝 | 12 / 27 / 9 | −2 | 18.6 s | 15.4 s | 283,787 |

逐根变差分析：

- 相对无剪枝基线变差 6 根：`FUZZY_WURM_CRAWLER_WEAK-FLASHM2`、`NIBBITS_WEAK-FLASHM1`、`PUNCH_CONSTRUCT_NORMAL-FLASHM1`、`SEAPUNK_WEAK-FLASHM1`、`SLIMES_WEAK-FLASHM1`、`SLIMES_WEAK-FLASHM2`。其中前两根的新结果与现有搜索完全相同（4/3、4/4），只是无剪枝 Flash 曾找到更好的路线；后四根新结果比现有搜索多 3–5 点失血。
- 这四根的路线在第二回合左右就分叉：新 Flash 先找到一个较差但更早的完整胜利，收紧后的下界把原先的防守线剪掉；现有搜索在同样的 `ShouldPruneByPrimaryIncumbent` 语义下没有剪掉该线，因为它先找到/保留了另一条胜利，或者该线仍在现有保留泳道（多样性/Pareto）里。K5（含上述 128 条游走）证明两边的下界输入与最终 `StrategicHpLowerBound` 相同，所以差异来自 incumbent 的发现顺序与 FlashBeam 更简单的保留结构，而不是剪枝语义或近似值。
- 没有通过加宽 Beam、提高节点上限或关闭剪枝来掩盖；这些根在禁用剪枝的配置下仍会回到无剪枝基线结果。

未移植与显式禁用：

- 共享 `PrimaryIncumbents` 表（对照/启用隔离）：不接入。
- `root.UsesComponentHealingCertificate` 且根药水含 `RegenPotion` 时，Component 上界依赖药水策略；该根禁用剪枝（`regen_potion_policy`）。
- `policy.EffectiveHasGrowthTargets` 非空的根在现有搜索中本来就不用局部下界；Flash 显式禁用（`growth_targets`）。`InitialGrowthRewards` 非零或可达 FEED 时也禁用（`growth_rewards` / `feed_growth`）。
- `RemainingHealingUpperBound`（Regent/Silent/Defect 的证书）与 `KnownNativeHealingPotential` 的 `SwipePower` 分支未移植；对应根禁用（`remaining_certificate` / `swipe_power`）。
- Flash 没有 risk 通道；K5 直接比较现有快照的 `HasRisk`，任何被覆盖状态记录 risk 都会使 K5 失败。当前全部根为零差异。
