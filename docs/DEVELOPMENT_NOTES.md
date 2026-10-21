# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

## 下一版本（开发中）

### Flash 搜索后端（默认关闭）

新增可选的结构体状态内核（`src/Flash/Kernel/`、`src/Flash/Content/`）与旧引擎衔接层（`src/Flash/Bridge/`）。默认关闭；设置 > 性能 > 搜索预算 >「Flash 快速搜索（实验）」可选择关闭、对照或启用。关闭时玩家可见行为不变；对照只记录一行对照日志，结果、工作量和诊断与关闭相同。启用模式只运行 Flash，不回落现有搜索：Flash 判定没有满足药水策略的路线时，返回与现有搜索同形的无路线结果；遇到不支持的内容、根状态超容量、内核边界、逐步回放核对不一致或药水约束复核不一致时，停止本回合部署、关闭全自动、提示切回关闭模式，问题包标注 `FlashSearchFailed` 与具体失败类别；回合准备搜索失败按同一约定处理，原生选择留给玩家作答。无人测试在这些失败上直接判 Failed。覆盖五个角色卡池、296 个遗物、65 种药水、三幕已实现遭遇与第三幕首领策略，以及事件卡牌与事件遭遇；未支持的内容在准入阶段按类别拒绝。当前同政策、同预算的质量对照（五个角色、三幕均衡的 120 个生成牌组）：开启相对关闭 28 好 / 76 同 / 16 差，胜场 87 → 88，总耗时约为关闭模式的 42%；按角色与幕次的细分和变差根见测试记录。规格、失败类别与请求参数见 [Flash 搜索](flash-search/README.md) 与 [搜索与接入](flash-search/search.md)，覆盖、逐根证据与未验证项见 [Flash 测试记录](flash-search/testing.md)。

### Flash 六维 Pareto 去重与质量基线（QP2）

Flash 池去重命中时按现有 `TranspositionFrontier` 的六维标签（药水数、药水战略成本、未来卖血、
累计失血、动作数、分数）保留非支配标签，代表改用现有 `IsBetterSearchNode`（分数降序、动作数
升序），不再只留 Flash 排序值最高者；标签数不设上限，`FutureSoldHp` 在 Flash 恒 0 是残差。
120 根开启模式由 28/76/16、失血差 +34、胜场 88、On 701 s 变为 29/75/16、+10、胜场 89、
On 678 s；8 个目标根中 IRONCLAD-016 从 80 降到 53，其余不变。单根首步谱系签名实验在 120 根
变为 27/76/17、+45，已撤回。路由 `ContextId`/`UnorderedPileKey` 未移植：Flash 搜索期间没有与
旧引擎同构的字符串上下文，需后续在 Bridge 提供。stand-pat 诊断显示 IRONCLAD-014 的目标在进入
stand-pat 分组前已被同指纹去重代表顶替，不是探测/比较器差异。`SMART_POTION_GRADIENT` 日志
新增 `flash_expanded`/`flash_elapsed_ms`，原 `expanded`/`elapsed_ms` 保留为物化回放统计。
验证：K0 Passed、四项门禁 OK、全量差分与动作集合 `actionSetFailed=0`、10 个质量根 48 条游走
零差异；详见 QP2 报告（`.local`）。

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
