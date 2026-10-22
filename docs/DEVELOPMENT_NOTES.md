# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

## 下一版本（开发中）

### Flash 搜索后端（默认关闭）

新增可选的结构体状态内核（`src/Flash/Kernel/`、`src/Flash/Content/`）与旧引擎衔接层（`src/Flash/Bridge/`）。默认关闭；设置 > 性能 > 搜索预算 >「Flash 快速搜索（实验）」可选择关闭或启用。关闭时玩家可见行为不变。启用模式先运行 Flash：Flash 判定没有满足药水策略的路线时，返回与现有搜索同形的无路线结果；准入阶段遇到 Flash 尚未支持的卡牌、Power 等内容（`admission-unsupported-content`）或无法投影的状态（`admission-unrepresentable-state`）时，让整次协调器搜索改用换档前的常规档位从头重跑，不停止部署、不关闭全自动、不导出失败包，每场战斗最多提示一次内容名称；其余情况（根状态超容量、内核边界、逐步回放核对不一致、药水约束复核不一致、未实现请求、根阶段等）仍停止本回合部署、关闭全自动、提示切回关闭模式，问题包标注 `FlashSearchFailed` 与具体失败类别；回合准备搜索按同一分类处理，原生选择留给玩家作答。无人测试在这些失败上直接判 Failed。覆盖五个角色卡池、296 个遗物、65 种药水、三幕已实现遭遇与第三幕首领策略，以及事件卡牌与事件遭遇；未支持的内容在准入阶段按类别处理（其中不支持内容与不可表示状态改走常规搜索）。开启模式使用 Flash 自己的档位预算：低/中/高/极高档的 Beam 宽度与节点上限分别为 180/240/360/540 与 240,000/480,000/1,000,000/2,000,000，软时间与分支上限等其余字段沿用常规档位；关闭模式不变，自定义档位沿用玩家填写的数值。当前同政策、同预算的质量对照（五个角色、三幕均衡的 120 个生成牌组）：开启相对关闭 31 好 / 72 同 / 17 差，胜场 87 → 91，总耗时约为关闭模式的 44%；按角色与幕次的细分和变差根见测试记录。旧 100 根语料为 29/60/11、胜场 70 → 74。规格、失败类别与请求参数见 [Flash 搜索](flash-search/README.md) 与 [搜索与接入](flash-search/search.md)，覆盖、逐根证据与未验证项见 [Flash 测试记录](flash-search/testing.md)。

### Flash 准入不支持内容改用常规搜索（FB）

`BeamSolveDispatch` 的开启模式分支只对两类准入拒绝请求整次回落：`admission-unsupported-content`（卡牌、Power、遗物、药水、球、怪物、附魔、苦难或生成闭包没有 Flash 定义）与 `admission-unrepresentable-state`（第三方订阅者、未登记模型等无法投影的状态）。分发点不再自己调用常规求解器，而是抛 Search 层的 `FlashFallbackRequiredException`（携带 `FlashFallbackNotice`）。`CombatSearchCoordinator.Solve` 只捕获这一个异常：先确认这次 Flash 尝试没有发布可采用路线、路线采纳种子或路线预览，再写一行 `[CombatSolver/Flash] FLASH_FALLBACK reason=<类别> detail=<第一个不支持的内容或状态> purpose=<用途>`，并用 `FlashMode = Off`、`Profile = SearchPolicySnapshot.RegularProfile` 把整次搜索从头跑一遍；新尝试新建请求时钟与预算账本，复用同一个取消令牌。`RegularProfile` 由 `SolverSettings.ApplyFlashPerformance` 在换档时保存，自定义档位两者相同；缺失时显式失败，不按 Flash 档位或比例缩放兜底。准入只取决于根内容（含生成闭包），实际回落发生在第一个 Flash 请求、发布任何结果之前；若发布检查判定已经发布，则不重跑，改写 `FLASH_SEARCH_FAILED reason=<同一类别>` 并抛 `FlashSearchFailedException`。Runtime 主搜索与回合准备搜索都走同一协调器入口。

Runtime 通过同一 `SearchDiagnosticsSink` 新增的类型化事件收到改用记录：每场战斗最多在消息横幅提示一次“本场有 Flash 尚未支持的内容（<游戏内名称>），改用常规搜索”（中英同步；名称由 UI 层按内容 ID 取游戏内本地化名，取不到时用 ID），不打断或阻塞部署；每次搜索的改用另记入问题包的 `FlashFallback` 分类（类别与内容），但不触发上传建议、不导出新问题包。搜索层不读取 `SolverSettings.Current` 或 UI。`FLASH_FALLBACK` 是每次协调器搜索一行，不是每个请求一行。

仍失败：根容量、内核不支持、核对不一致、药水约束复核不一致、未登记搜索用途、根阶段、多人/Mock/废弃内容、待处理选择、未实现请求与其它准入类别。合同 `flash_regression.py fallback` 在一个超过常规 Beam 宽度的 `SOULBOUND_POWER` 根上验证 On 整次改用常规档位后的路线、质量、`ExpandedNodes`/`TransitionCount` 与 Off 相同，日志恰好一行 `FLASH_FALLBACK` 且不写 `FLASH_SEARCH_FAILED`；常规求解器强制套上 Flash profile 的工作量不同（敏感性）；预置采纳种子后回落不重跑；同一根上未登记搜索用途仍以 `admission-unsupported-request` 失败。

验证：Release 构建 0 警告；K0 `contracts=Passed`；结构、文档、工具、覆盖四项门禁通过。`fallback` 合同输出 `status=Passed route=True quality=True work=True fallback_lines=1 off_work=1843/5855 flash_profile_work=6203/17954 sensitivity=True published_blocked=True rejection=admission-unsupported-request`。开启模式 120 根（`coordinator --off-baseline`）全部准入，`route.json`、`quality.json`、`SelectedExpanded`、`SelectedTransitions` 与主线 `cl2-on120` 逐根相同；总展开/转移 119/120 相同，只有 REGENT-013（120 秒时间预算根，每次运行会变）为 253,260/6,598,600 对 253,712/6,796,322。关闭模式 120 根与 `off120-base` 对照：`quality.json` 120/120 相同，`SelectedExpanded`/`SelectedTransitions` 与总展开/转移均为 118/120，差异只在 IRONCLAD-013 与 REGENT-013 两个时间预算根；日志无 Flash 行。未做可见游戏提示排版验收。

### Flash 终局排序键顺序与质量表口径（CL2）

Flash 终局比较把政策边界原因排在可选药水数之前，与常规 `FinalPlanOrdering.Select` 相同（此前两者顺序相反）。卖血键是请求已提交的卖血加路线的未来卖血；FlashBeam 没有路线级未来卖血，同一请求内各路线相同，因此不参与比较。120 根开启模式与改动前逐根相同。`flash_regression.py table` 与协调器汇总的 `cost_delta` 改为只统计两种模式都获胜的根，只有一方获胜的根另计 `on_only_wins`/`off_only_wins`：120 根两边都胜的 87 根上合计 −28，只有 Flash 获胜 4 根、只有常规获胜 0 根。

### Flash 精简与清理（CL1）

按只读审计删除不再维护的 Flash 分支与工具，生产搜索行为保持不变：

- `FlashSearchMode` 只保留 `Off`/`On`（`On` 的持久化值仍为 `2`/字符串 `"On"`）。旧设置、检查点与问题包里的 `"Shadow"` 和任何数值形式在读取时归一化为 `Off`，新增 `flash_regression.py compat` 合同覆盖两条；设置界面只剩关闭/启用。
- 删除 25% 进攻席位（`OffensiveShare`、`ShortSearchOffensiveShareBudgetMs`、敌人 HP 比较器与调用点参数），层内保留按排名取满 Beam。
- 删除 K6 逐层保留集合工具（`RetentionDiffProbe`、`FlashRetention`、`flash_regression.py retention`、`FlashBeamOptions.Retention`、常规搜索的保留集合观察器与 `OFFLINE_HARNESS_RETENTION_*`）；路线追踪仍用的 `SearchPathObserver` 与 `RetentionLabels` 保留。源码从 `5365e89d` 找回。
- 删除失败注入测试（`FlashFailureInjection`、`FlashFailureChecks`、`OFFLINE_HARNESS_FLASH_FAILURE_CHECKS`）；保留 `UnattendedTestRunner.FlashFailure` 的 DevelopmentStrategy 端到端失败合同。
- 删除零调用方成员与 10 个 `Search` 反向 `using CombatSolver.Flash.Bridge`；删除未抛出的 `FlashRejection.ThirdParty`/`admission-third-party` token（第三方未知内容按具体对象走 `admission-unsupported-content`，无法投影的订阅者状态走 `admission-unrepresentable-state`）；`FromRejection` 补 `RootPhase` 映射；`auditable` 白名单改为穷举，On 模式下新的 `DirectSearchPurpose` 明确失败。
- 离线工具：`scan` 每根只跑一次 Flash 并输出准入结果（拒绝即失败）；`coordinator` 有失败或缺失根时退出码非 0；删除各子命令接受但不生效的选项；`FLASH_DIFF_STEPS`/`FLASH_DEBUG` 接入 `diff --steps/--debug`，`TRACE_INCUMBENT_*`/`TRACE_RESET_BASELINE` 接入 `trace` 的 CLI。K6 已删除，`testing.md` 的已完成批次移入 [批次测试记录](archive/flash-search/testing-batches-2026-10-22.md)。验证：Release 构建 0 警告，K0 与结构、文档、工具、覆盖四项门禁通过；开启模式 120 根与 FW 基线的 route/quality/`SelectedExpanded`/`SelectedTransitions` 逐根相同，关闭模式 120 根质量逐根相同、工作量仅 IRONCLAD-013 与 REGENT-013 两个时间预算根有出入；候选扫描 410 根 / 2,008 条候选 / 0 不一致；设置兼容合同 23 项断言通过。

### Flash 开启模式使用自己的档位宽度与节点上限（FW）

开启模式不再沿用常规档位的 Beam 宽度与节点上限。`SolverPerformanceValues` 旁增加每个档位的 Flash profile（只替换 `BeamWidth`/`MaxExpandedNodes`，软时间与分支上限等沿用常规 profile）；`SolverSettingsSnapshot` 带上当前档位，`SolverController.CaptureSearchPolicy` 在快照构造完成后用 `SolverSettings.ApplyFlashPerformance` 替换，协调器按替换后的 profile 计算成员宽度和共享节点预算，FlashBeam 直接使用请求 profile（QW 已是如此）。换档前的常规 profile 同时保存在 `SearchPolicySnapshot.RegularProfile`，供 FB 的准入回落整次重跑；重复换档保留第一次的常规值。关闭模式原样返回；自定义档位不替换，沿用玩家填写的值；检查点录制的精确 profile 也按自定义处理。离线宿主在 `OFFLINE_HARNESS_FLASH_MODE=On` 时用同一函数按 `--profile` 替换，`--profile Custom`/`Off` 不变。

关闭模式对照：替换只在 `FlashMode == On` 执行，Off 的 profile 选择不变。用关闭模式重跑 IRONCLAD-020、SILENT-007 两个根，`quality.json` 与主线 `off120-base` 逐字段相同；`SearchWorkAttributions` 的 `ExpandedNodes`/`Transitions`/`ChoiceBranches`/`RecordedSolverCount` 合计分别为 16,189/58,720/0/8 与 21,274/73,045/4,571/14，逐项相同。

120 根：31/72/17、失血 + 药水成本差 +100、胜场 87 → 91、总耗时 1671 s → 735 s；旧 100 根：29/60/11、+241、70 → 74、2030 s → 680 s。与 QW 宽度（120 根 19/78/23、−8、87 → 85、241 s；旧 100 根无结果）和 QF5 的宽度实验（120 根 29/74/17、−17、87 → 89、701 s；旧 100 根按旧口径 31/60/9、−165、647 s）并列见测试记录 2.1/2.2。Release 构建 0 警告，K0 与四项门禁通过；未改 Flash 内核、内容与搜索策略。

### Flash 逐层复刻常规搜索：尝试后撤回（K6、R1–R3）

为了让同档位下 Flash 每根都不劣于常规搜索，先做了逐层保留集合一致检查 K6，再分三批照常规移植候选生成、卖血惩罚、同分顺序、首步谱系分组与路由签名。同档位 120 根“同”由 78 增到 87–90，“差”只由 23 降到 21，部分批次的“好”由 19 降到 8，耗时最多增加约 40%。用户决定暂停这条路线，搜索策略撤回到 QW。K6 工具与保留集合观察器已在后续清理中删除，源码可从提交 `5365e89d` 找回；路线追踪仍使用的 `SearchPathObserver` 与 `SearchDiagnosticsSink.RetentionLabels` 保留。细节见 [Flash 批次测试记录](archive/flash-search/testing-batches-2026-10-22.md)。

### Flash 内核：Anger 复制与 Dampen 恢复顺序（QF5）

档位宽度（当时用已移除的 `OFFLINE_HARNESS_FLASH_PROFILE_SCALE=1` 得到与现行档位相同的宽度）下 `quality-IRONCLAD-019` 的协调器开启模式在第 18 步
核对失败：`PlayCard:ANGER#0->3` 的复制在旧引擎为 `ANGER+1`、Flash 为 `ANGER+0`。根因是现有引擎
`CorePowerSupport.ApplyCardPowers` 在镜像攻击之后、Anger 的复制补偿之前执行 `ApplyEnemyDeathPowers`：
本次攻击击杀最后一个 `DampenPower` 施法者时，原版 `DampenPower.AfterRemoved` 先把正在打出的 `CardModel`
升级回原等级，`Anger.OnPlay` 随后按该活对象 `CreateClone`。Flash 的 `Anger.OnPlay` 没有在这次攻击后运行
死亡结算，又用了离开手牌时的 `FlashExec.Card` 副本，于是复制出降级后的 `+0`。现改为攻击后先
`ApplyEnemyDeathPowers`，再 `SyncCurrentPlayCard` 从实际出牌行取当前值后复制。旧引擎与原版语义一致，
未改旧引擎；`Anger` 是唯一在 `CorePowerSupport` 的 death pass 之后克隆自己的卡，`AdaptiveStrike`/
`Undeath` 的克隆位于 `CardEffectSpecRegistry`（death pass 之前），顺序本来就一致。

新增固定根 `flash-qf5-anger-dampen-clone`：修正前 32 条游走 `failed=1 walkFailed=18`（首个差异旧
`Discard[0]=ANGER+1`、Flash `ANGER+0`），修正后 `failed=0 walkFailed=0 actionSetFailed=0`；
`quality-IRONCLAD-019` 默认 48 条游走同为零差异。档位宽度单根协调器开启模式 30 行 `FLASH_ADOPTED`，
没有 `FLASH_SEARCH_FAILED`。默认游走、动作集合检查与旧批次更宽 Beam 没发现它的原因见下面三条：该状态只位于
协调器 `OpeningPowerRouteMember` 的 `JUGGLING` 固定前缀之后，默认差分从原始根做随机游走没有走到
“Dampen 已施加、第二张 Anger 击杀最后施法者”的时点；动作集合只比较根/单步合法动作，不执行该步；
旧批次更宽 Beam 的候选与质量路线在相同预算下走了另一条不触发该时点的路线。离线宿主在
`harness-result.json`/`result.json` 新增 `flashFailure` 全量记录（成员、完整路线、步前状态、两侧步后
状态、全部差异），稳定日志行与 320 字符 `detail` 不变。

### Flash 宽度与节点上限直接使用档位值（QW）

Flash 不再把请求 profile 的 Beam 宽度与全局节点上限乘以桥接层常数，也不再按宽度倍数折算共享节点预算。
删除 `FlashRouteMaterializer.ProfileWidthScale`、`ProfileWidthScaleOverrideForTesting`、
`EffectiveProfileWidthScale` 与 `Solve/SolveCore` 的 `profileScale` 参数链；`FlashBeamOptions` 的宽度与节点
上限直接取 `profile.BeamWidth`/`profile.MaxExpandedNodes`，软时间仍取同一 profile。工作贡献的
`BudgetExpandedNodes` 改用默认值，即实际 `ExpandedNodes`；`FlashWorkAccounting` 的宽度倍数与
`BeamSolveDispatch.ProfileScale` 一并删除。窄开局、早期侦察与两个新颖度成员原本就不放大，行为不变。
离线宿主删除 `OFFLINE_HARNESS_FLASH_PROFILE_SCALE`、`OFFLINE_HARNESS_FLASH_TRACE_PROFILE_SCALE` 与
`OFFLINE_HARNESS_FLASH_WIDTH_SCALE`；宽度实验改用已有的 `--profile`/`--beam`/`--nodes`/`--budget-ms`。
Flash 特有的 25% 进攻席位当时保持不变；后续 CL1 已删除该泳道，层内保留按排名取满 Beam。

验收：120 根协调器开启模式与同代码旧开关 `PROFILE_SCALE=1` 的结果逐根相同（`cmp-runs.py` 120/120；
路线、完整 `quality.json`、`SelectedExpanded`、`SelectedTransitions` 与 `FLASH_ADOPTED` 的逐根
`flash_expanded`/`flash_transitions` 合计也全部一致，合计 6,066,815 / 34,235,126，共 2,812 行）。
质量表 19 好 / 78 同 / 23 差、`cost_delta=-8`、胜场 87 → 85、总耗时 1671 s → 241 s，没有
`FLASH_SEARCH_FAILED`。Release 构建 0 警告；K0 通过，结构、文档、工具与覆盖四项门禁通过。文档与设置界面
同步改为“与常规搜索使用同一档位”，界面支持范围按当前单人原版内容更新。该“同一档位”口径已被 FW 取代：
开启模式现在使用 Flash 自己的档位宽度与节点上限，关闭模式仍保持本文的常规档位口径。

### Flash 选牌药水 X 费筛选（QF4）

`TouchOfInsanity` 的 Flash 选项谓词把 `EnergyCostWithModifiers`/`StarCostWithModifiers`
（对 X 费返回当前能量/星能，等价于实付值）当成原版 `CardModel.CostsEnergyOrStars` 的 global
分支，X 费牌因此在资源大于 0 时多出旧引擎没有的选项，后续选择会落到不同的牌。现按原版
`CardModel.cs:1579-1595` 的 `!EnergyCost.CostsX` / `!HasStarCostX` 前置条件排除 X 费的 global
项，local 能量项对 X 费保持占位 0（X 星费的 local 仍按 `CurrentStarCost`，临时星费大于 0 时
保留原版可选中行为）；Unplayable 与费用被改为 0 的牌仍按原版取两支并集，回放数不参与
费用判断。其余三种 QP1 药水与 `DropletOfPrecognition` 都没有费用筛选谓词，选项来源、顺序与
`PotionChoiceSupport.GetSpec` 一致，本轮无同类修改。

新增固定根 `flash-qf4-touch-of-insanity-xcost`（手牌含 `WHIRLWIND` 与普通耗能牌）：
修正前 `failed=3 walkFailed=17`（首个差异 `old=2 flash=3`），修正后 32 条游走
`failed=0 walkFailed=0 actionSetFailed=0`；四个 `flash-qp1-*` 固定根与
`quality-NECROBINDER-022` 默认游走数同为零差异，候选扫描 `routes=7 mismatches=0`。合入后在最终源码上：默认差分 409 根零差异，候选扫描 409 根 2,194 条候选零不一致（`flash-sk-a-discard-sly` 仍是已知内核边界），120 根质量与 QP2 逐根相同（见 [Flash 测试记录](flash-search/testing.md) 2.1）。

### Flash 六维 Pareto 去重与质量基线（QP2）

Flash 池去重命中时按现有 `TranspositionFrontier` 的六维标签（药水数、药水战略成本、未来卖血、
累计失血、动作数、分数）保留非支配标签，代表改用现有 `IsBetterSearchNode`（分数降序、动作数
升序），不再只留 Flash 排序值最高者；标签数不设上限，`FutureSoldHp` 在 Flash 恒 0 是残差。
120 根开启模式由 28/76/16、失血差 +34、胜场 88、On 701 s 变为 29/75/16、+10、胜场 89、
On 678 s；8 个目标根中 IRONCLAD-016 从 80 降到 53，其余不变。单根首步谱系签名实验在 120 根
变为 27/76/17、+45，已撤回。路由签名的 `ContextId` 仍恒为 0，牌堆与敌人分布键仍取选择前的父状态
（现有搜索取选择完成后的状态）。stand-pat 诊断显示 IRONCLAD-014 的目标在进入
stand-pat 分组前已被同指纹去重代表顶替，不是探测/比较器差异。`SMART_POTION_GRADIENT` 日志
新增 `flash_expanded`/`flash_elapsed_ms`，原 `expanded`/`elapsed_ms` 保留为物化回放统计。
验证：K0 Passed、四项门禁 OK、全量差分与动作集合 `actionSetFailed=0`、10 个质量根 48 条游走
零差异；详见 QP2 报告（`.local`）。

同批试过两项更完整的移植，120 根质量都与上面逐项相同，均未采用：整次求解范围的准入/展开
转置表（照现有 `_run.Transpositions`/`ExpandedTranspositions`，两表合计 100 万条上限）在很多
请求中打满上限，每条约 100 B，单次求解约 100 MB 托管内存，与 Flash 预分配、不产生 GC 的池
设计相冲突；路由签名改取选择完成后的状态，实现上要对每个路由选择多执行一遍动作。

### Flash 选牌药水与动作集合核对（QP1）

`LiquidMemories`、`Ashwater`、`GamblersBrew`、`TouchOfInsanity` 的 Flash `OnUse` 原先为空实现，
现按现有引擎 `PotionChoiceSupport.GetSpec/Apply` 的选项、来源牌堆、数量与效果实现：
`LiquidMemories` 从弃牌堆精确取 1 张、本回合免费后回手；`Ashwater` 从手牌取 0..N 张消耗；
`GamblersBrew` 从手牌取 0..N 张弃置再抽同数；`TouchOfInsanity` 从手牌中耗能/耗星卡精确取 1 张、
本场免费。空选项也发一个零选选择请求（与旧引擎 `RequiresChoice` 一致）。同批把 `FlashCard`
的临时星能费用从单条目扩展为当前条目加最多两个同成本旧条目，修复两类免费药水叠加后
`AfterCardPlayedCleanup`/回合末清理的投影差异；`FlashCard`/`FlashBattle` 尺寸不变（K0 92/12,696）。
另修正 `DropletOfPrecognition` 抽牌堆为空时仍应请求选择（动作集合核对发现）。

差分探针新增动作集合核对：在同一状态比较现有求解器计划动作与 Flash 合法动作的规范化键
（种类、卡牌/药水、目标、药水槽位、药水是否需要选择）；卡牌选择在展开时附加，由 K1 的
逐动作选择请求对账，不进入动作键。`actionSetFailed` 纳入 `diff` 失败条件与基线比较。
四个固定根 `flash-qp1-liquid-memories`、`flash-qp1-ashwater`、`flash-qp1-gamblers-brew`、
`flash-qp1-touch-of-insanity` 修正前动作集合失败 47/47/46/45，修正后均为 0；固定根与 120 个
质量牌组全量动作集合核对见 QP1 报告。

`AddRequired` 的来源标签默认关闭，仅离线追踪打开：由 `SearchDiagnosticsSink.RetentionLabels`
控制在 `GlobalRetention` 中输出 `requiredLabel`。关闭时 DEFECT-012 开启模式协调器路线、
总展开 242219、总转移 1621887 与证据逐位相同。

### 致命击杀判定改读分支状态

后台搜索判定 The Hunt、Feed、贪婪之手的击杀奖励与书本修复刀的治疗时，原经原版 `ReattachPower` 读取实机上千足虫节段的生命：搜索分支里其他节段已被击杀、实机上仍存活时，判为不致命并漏算奖励。现在由 `SimulatedCombatState.ShouldOwnerDeathTriggerFatal` 按分支状态计算；同时把 Surrounded/BackAttack 与 FrostOrb/Hibernate 的 Power 存在性读取改为分支查询。验证：The Hunt 与书本修复刀的 actual/simulated 严格差分修正前不一致、修正后完整快照一致；默认 79 根关闭模式只有 SILENT-C-13 改变（正确计入 The Hunt 奖励）；Flash 全量差分零差异。

### Doom 击杀的致命计数时机

后台搜索结算 Doom 击杀时，原先在击杀前统计书本修复刀的致命击杀数；原版是逐个击杀、每次完成死亡效果后，在全部击杀后才统计。同批被 Doom 击杀的多个千足虫节段因此被判为不致命、漏算治疗。现在按原版时序结算，搜索与 Flash 内核一致。验证：新增 `DOOM-FATAL-TIMING-V0111` actual/simulated 严格差分（修正前模拟治疗 0、实机 9，修正后完整快照一致）；默认 79 根关闭模式逐根不变。

### 本回合已完成攻击数的记账时机

后台搜索原在出牌的 `AfterCardPlayed` 之后才累加“本回合已完成攻击/Shiv 数”，而原版在 `AfterCardPlayed` 之前就记录这次出牌已完成。Calamity 在攻击后生成的 Stomp 因此少算一次减费，计划中的牌与实机手牌状态不一致，部署时找不到计划的牌并重新搜索。现在按原版时机累加，Flash 同步。验证：IRONCLAD-50 的无头整场计划外重算从 1 次变为 0 次；默认 79 根关闭模式只有该根改变（失血 74 → 49）。

### 战斗结束后导出的问题包

玩家战死后立即导出问题包时，导出可能仍把本场当作进行中的会话写入 `replay/current`。现在战斗已结束时先完成本场取证再导出，问题包与胜利结束时一样归入 `replay/recent`。

### Q015 路线保留（PR #227）

在既有 Beam 名额内，按用药数量保留已降低敌方格挡、仍有合法攻击且敌方剩余生命不超过可执行手牌估值的收尾续路；合法攻击数沿现有手牌遍历冻结。该估值用于保路资格，实际攻击由模拟器结算。Discard／DiscardAndDraw 上下文优先保留预计安全的进攻续路。Control 在原有结束回合探测名额中保留一个可能由延迟伤害获胜的代表，再由实际试算决定。旧敌退出且新敌入场的 AliveEnemyMask 变化进入 RevivalWindow 通道。状态等价、终局政策、预算和战斗语义沿用原实现。

Testing 在完整原生检查点已核验、当前手牌上限为默认 10 时迁移旧报告缺失的字段，其他续用字段继续严格比较。录制预测路径诊断支持显式指定候选池观察步数。贡献者的五主题自主搜索、原生部署、固定哨兵及未验证范围见[Q015 记录](issues/q015-route-quality.md)。

### 计划续搜复用完整胜利证明（PR #229）

严格组件认证根将已有及首成员新发现的完整胜利传给后续计划，沿原回复上界与政策门禁剪枝。Runtime 在重建 NoGC 的检查点请求一次压缩完整回收，确认新的完成信息与弱引用哨兵后重建区域。贡献者的 Linux 11 根交错性能数据、原生合同、失败及平台限制见[性能记录](performance/plan-witness-propagation-20261006.md)。

### Smart 药水审计独立成员时间（PR #230）

Smart 无强制用药、主结果无药且根有可搜索药水时，在主搜索耗尽时间后为首个可选药水层提供软预算的 1/5（1–30 秒）。该层按软预算保留结果，关联取消令牌提供 1/6 收尾时间，显式层数上限固定为一层；其他政策继续沿原入口执行。后置开局前缀补搜使用请求账本。已耗尽请求会增加这一层的实际耗时。根因、原包数据与验证范围见[Q011 记录](issues/q011-worldlines-20261006.md)。
