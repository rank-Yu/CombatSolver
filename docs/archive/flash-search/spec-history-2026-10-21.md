# Flash 规格历史（2026-10-21 冻结）

本卷保存 2026-10-21 从 [Flash 规格](../../flash-search/README.md) 移出的批次说明、修正前后数字与
当时的阶段计划。冻结后不再更新；当前行为只以现行规格与 [测试记录](../../flash-search/testing.md)
为准。每条注明原位置。

## 1. 阶段计划（原 `flash-search/README.md` 第 4 节）

| 阶段 | 内容 | 完成判据 |
| --- | --- | --- |
| F0 | 规格、规则、skill | 本目录、`.agents/skills/flash-search/`、AGENTS.md 修订合入分支 |
| F1 | 内核核心、根导入、差分探针；纵切内容：铁甲战士初始牌组、燃烧之血、第一幕弱怪 | 纵切 fixture 的一步穷举与多回合随机游走差分全部一致 |
| F2 | 内容扩展：角色牌池、通用 Power、怪物、遗物、药水、球、附魔 | 每项内容附差分证据；覆盖率统计显示准入战斗比例 |
| F3 | FlashBeam、路线物化、`BeamSolveDispatch`、Shadow 模式 | 离线宿主 Shadow 对照零校验失败，记录速度与质量 |
| F4 | On 模式、哨兵验收、PR | 同根、同政策、同预算路线质量不退化，搜索耗时下降 |

原文附注：F2 的批量内容实现可以交给外部 coding agent，按
[flash-search skill](../../../.agents/skills/flash-search/SKILL.md) 的任务卡合同交付并附差分证据。

## 2. 状态布局的批次变更与尺寸演进（原 `flash-search/state-layout.md`）

原 2.6 开头：“DX3 批次没有改变 `FlashBattle`/`FlashCard` 的尺寸，也没有新增
`FlashCounter`”。

原 2.7 开头：“EC 批次没有改变 `FlashBattle`/`FlashCard` 的尺寸，也没有新增
`FlashCounter`（`FlashCard` 仍 92 字节，`FlashBattle` 为当时的 12,600 字节；DX5 之后见 2.9）”。

原 2.9 开头：“DX5 只追加了一个跨回合卡值槽，没有新增 `FlashCounter`、`FlashCard` 尺寸不变”。
其中 `HistoryCourseCard` 的记录：“`FlashBattle` 从 12,600 增至 12,696 字节（92 字节值加 4 字节
尾部对齐）”。

原 2.7（PR 批次）开头：“本批没有改变 `FlashBattle`（CP 后为 12,600 字节；DX5 之后为 12,696，
见 2.9）或 `FlashCard`（92 字节），也没有新增 `FlashCounter`。新增内容使用既有槽位”。

原第 6 节“尺寸预算”的同一段包含尺寸演进：

> `FlashEnemy` 仍是 64 字节：`Rules` 占用原 `_padding` 的第一个字节，
> `PoweredAttackHitsThisTurn`（BeatIntoShape 的每目标本回合强化攻击命中数，0..255，溢出显式
> 拒绝）占用原 `_padding` 的第二个字节，`SteamPhase` 占用第三个字节（原 `_padding1` 的第一个
> 字节），最后一个字节继续填充。`FlashOrb` 为 8 字节（`Id` + `Aux`），DF 没有改变它的大小。
> `FlashCard` 为 92 字节（96 张，费用修饰容量 16，`DampenOriginalUpgrade`，`TransientMark` 复用
> 尾部填充；ID 复用 `Private` 账本）。TW 把 `DetachedCards` 从 2 扩到 3，`FlashBattle` 从 12320
> 增至 12408 字节；KB 在头部末尾追加 4 个 `decimal` 的 `DecimalVars`（64 字节），`FlashBattle`
> 增至 12472 字节，`FlashCard` 不变；CP 把已知敌人从 10 扩到 12（实测 10 且出现 9886 个子状态
> 在新增第 11 个敌人时被丢弃），`FlashBattle` 增至 12,600 字节。KA 的苦难/附魔只使用既有的卡牌
> 字段、`FlashCardFlags` 的最后一个空闲位和追加的两个 `FlashCounter`，没有改变任何区块尺寸；
> A3 的 Dampen 记录追加一个卡牌字段（`DampenOriginalUpgrade`）与一个计数器
> （`DampenCasterMask`），因此卡牌与战斗各增大 2 字节和 128 字节。

原文还记录：“K0 在同一台机器上测得 `copyOnly` 约 350 ns、`copyStep` 约 2.2 µs
（`HasIdentityContent=false` 的自然根路径）。若复制成本成为瓶颈，优先缩小 `FlashCard` 和卡牌容量，
而不是引入写时复制或分层状态。”

原第 6 节末尾的池实验：

> `RupturePower` 的 `Aux2`（曾经暂存伤害来源标记）已不再使用，伤害来源直接放在栈上的
> `FlashDamageResult`。搜索容器不属于状态布局；`FlashBattle` 在 DX5 之后为 12,696 字节。……
> 块大小取舍：……仍小于旧版一次倍增得到的百 MB 级数组。曾试过跨求解缓存 64 MiB 状态块；
> Medium 上能命中，但碎片 180 MB（基线 0.6 MB）、峰值 419 MB（分块无池 330–355 MB），故未保留，
> 当前每次 `Solve` 新建池。

原先的 LS 段落：“LS 没有新增计数器、Hook 位沿用 `FlashHookMaskExt` 的 `1UL << 16`，
`FlashCard`/`FlashBattle` 尺寸不变。”

原先的 CX 段落：“CX 没有新增 `FlashCounter`、`FlashBattle` 或 `FlashCard` 字段：CX 本身保持
`FlashBattle` 12320 字节（TW 随后把 `DetachedCards` 扩到 3、KB 与 CP 再扩；DX5 之后为 12,696，
见上），`FlashCard` 仍为 92 字节。”当时还写明 `Imbued` 快照只在栈上的 `FlashExec` 路径里，
稳定边界不留下任何值。

## 3. 内核语义的批次标记（原 `flash-search/kernel-semantics.md`）

- 原 3.3 条目名“生成卡与 A3 新增时机”。A3 的内容是 `AfterCardGeneratedForCombat` 的绝对下标与
  生成者 owner，以及 Aeonglass 的 Wither 升级计数。
- 原 3.4 条目名“CX 补齐的四个附魔”，指 `Slither`、`SlumberingEssence`、`PerfectFit` 与 `Imbued`。
- 原 3.4 条目“Power 与怪物”以“本轮实现 Plow、Ringing、Tangled、Smoggy、Vital Spark、Tainted、
  Hex 七个 Power，以及 Ceremonial Beast、Vine Shambler、Infested Prism、Living Fog、Gas Bomb
  的移动效果”开头。
- 原第 9 节以“A3 另用 `ModifyAttackDamage`/`ModifyAttackRepeats` 对应
  `AdjustMonsterMoveDamage` 与 `BranchMonsterAi.CurrentMove` 的动态伤害/命中数”说明运行时意图。
- 原第 13 节标题为“A3 生成”，并写“A3 另把 Fabricator 的 `fabricator_last_spawn`……
  TestSubject 的 `_respawns`/`_extraMultiClawCount`/`SecondFormHp`/`ThirdFormHp` 与 Aeonglass
  的两个字典计数投影为 AI 寄存器”。
- 原第 15 节标题为“额外回合与药水/遗物批次”，正文为“额外回合、本批 9 瓶药水与新增遗物的逐条
  机制拆到[药水与遗物](../../flash-search/kernel-potions-relics.md)，避免本文件超过体积上限”。
- 原 4.1 的自动预打出顺序写作“`Bombardment` → Mayhem 的计划自动打出 → 第 1 回合的 Imbued
  附魔卡（HistoryCourse/WhisperingEarring 仍无定义）”。这是旧限制；当前顺序是
  Bombardment → Mayhem → HistoryCourse → Imbued，WhisperingEarring 只保留最大能量。

## 4. 角色机制的批次标签（原 `flash-search/kernel-characters.md`）

- 1.1、2.1、4.1 的标题原带“（LS）”。LS 批次补入静默猎手、故障机器人、储君的剩余卡牌/遗物/
  药水与亡灵契约师的 SentryMode、CallOfTheVoid、Misery、Pagestorm、DrainPower、BigHat 等。
- 第 5 节原标题为“第三幕牌组的 DX3 修正”，来源写作“第三幕随机牌组 150 根的 K1/K2 差分；
  现有引擎位置逐条注在改动处”。这批修正是 Globe Head/GalvanicPower、MagiKnight/DampenPower、
  Queen/ChainsOfBindingPower、不宁提灯、Vambrace、Turret Operator/RampartPower、
  Axebot/StockPower、召唤物初始行动、HellraiserPower、攻击后抽牌的死亡结算与
  GigantificationPower。
- 第 6 节原标题为“事件内容的角色无关机制（EC）”。
- 当时 `UnsettlingLamp` 写作“仍无定义：它需要卡牌实例身份、`BeforePowerAmountChanged` 与
  `ModifyPowerAmountGivenMultiplicative` 的逐卡翻倍账本”。该限制后来由实例身份实现解除；
  当前定义在 `Content/Relics/IdentityRelics.cs`。
- `PenNib` 的锁存、`DrainPower` 的门控与事件怪物的 EventDefeat 原文分别带（DX5）、（DX4）、
  （DX5）标记；这些标记在现行正文中已去掉，语义不变。

## 5. 药水与遗物的批次来源（原 `flash-search/kernel-potions-relics.md`）

原标题为“Flash 内核语义：额外回合与药水/遗物批次”。引言原文：“本文件承接
[内核语义](../../flash-search/kernel-semantics.md) 第 15 节：额外回合管线，以及 PR 批次新增的 9 瓶药水与 34 个
遗物定义；DX5 追加 HISTORY_COURSE 与 Pen Nib 的锁存身份。通用执行上下文、Hook 派发、出牌、
伤害与回合流程仍以那份文档为准。”第 15 节标题原为“额外回合与 PR 内容批次”。

`HISTORY_COURSE` 条目原以“DX5 的 HISTORY_COURSE 与 Pen Nib”开头，并记录
“唯一新增的跨回合槽，`FlashBattle` 12,600 → 12,696 字节”。

## 6. 准入与校验的批次说明与证据（原 `flash-search/admission-and-verification.md`）

- 原 2.1 标题为“EC：明确分类的不支持内容与事件内容投影”。
- 原 2.2 标题为“DX5：HistoryCourse 的跨回合槽、Pen Nib 锁存与 EventDefeat”。
- 原第 1 节在运行时值意图段写：“HS4 审计原版全部攻击意图，另外两个运行时值意图
  （TheForgotten DREAD、WaterfallGiant PRESSURE_GUN）在根上不重复计入。”HS4 的完整证据
  （三类超时原因、TestSubject MULTI_CLAW 多打一下、`_extraMultiClawCount`、16 条游走失败到 0）
  保留在 [测试记录 2.2](../../flash-search/testing.md#22-批次记录)。
- 原 2.1 的显式类别证据：“事件卡组对照中把 `BEACON_OF_HOPE` 注入战斗牌堆，`flash-diff.txt`
  首行为 `REJECTED MultiPlayer: BEACON_OF_HOPE 是多人专用内容…`”。
- 原 2 节的批次段落标题：P16（实例型 Power 与卡值投影）、DF（球与卡牌私有状态投影）、
  ID（实例身份投影）、RG（储君投影）、MB（Knowledge Demon、Automation、Sloth 与
  EntropicBrew）、KB（小数卡牌变量、BeatDown 与对象身份）、TW（偷窃与瀑布巨兽）、
  DX3（第三幕牌组投影）、NB（亡灵契约师投影）、KA（卡牌苦难与附魔投影）、CX（附魔与
  MELANCHOLY 准入）、PR（9 瓶药水与 37 个遗物中的 34 个定义）。
- 原 KB 段末写“其余卡池补齐带来的准入变化见任务报告”；该任务报告未随仓库提交。
- 原附魔段写“已实现 18 个附魔：……；Imbued、Slither、SlumberingEssence、PerfectFit 仍按
  未支持附魔拒绝（分别需要自动预打出、抽牌随机费用、BeforeFlush、按实例的洗牌顺序）”。
  这是 CX 之前的限制；当前 22 个附魔全部有定义。
- 原 PR 段写“离线场景宿主为本批 `FLASH-PR-*` fixture 在开局窗口安装 `__FIRST__` 卡牌选择器”。
- 原请求基线段标题为“请求根冻结的规则基线（DX4）”。

## 7. 搜索与接入的移植记录、测量与缺陷（原 `flash-search/search.md`）

### 7.1 RS1 保留结构

原文：“RS1 批次把下列结构补进 FlashBeam，全部按 profile 的 W（`SecondRankBandWidth`，不是
Flash 的 ×4 池宽）计算席位”。下列结构为路线标签、持续铺垫与焦点目标分组、药水使用配额与谱系、
选项路由配额与 option-leader 轮转、stand-pat 探测、Pareto、有序牌堆与回合边界整理；仍写明
偷窃资源领先者与 `FindBestTurnBoundaryHand` 已在 RS1 移植，`FindBestLane` 的路线标签过滤已恢复。

持续铺垫分组原测量：“实测改成 `StrategicSetupTraits` 后 `DEFECT-C-05` 至少用药从 30 变 40、
IRONCLAD-17 不改善，故本批保留持续项并记录该差异”。

### 7.2 回合开始选择的清零缺陷

原文：“回合开始的选择作答使用 `taken` 位图记录已选下标；该 `stackalloc bool` 缓冲区必须先清零。
未清零时残留的 `true` 字节会跳过选项，1/5 的请求可能得到空选择，重放时与同一 `EntropyPower`
请求的 1–1 约束不一致（DEFECT-29、SILENT-23、SILENT-28 曾整次失败）；修正为 `taken.Clear()` 后
不再按效果类型跳过。”

### 7.3 状态池与容量测量

原文：“状态块固定 64 槽（64 × 12,600 B = 806,400 B）……两个可增长池 `next`/`ended` 各自使用
同一个按字节计的容量预算：状态上限为 `PoolStateBudgetBytes / sizeof(FlashBattle)`
（`PoolStateBudgetBytes = 200 MiB`，12,600 B 状态约 16,644 个）向下取整到 64 槽块，即 16,640 个
状态（209,664,000 B ≈ 199.95 MiB）……REGENT-V-06 实测单回合可到 434 层、约 10.4 万个终止
子状态……曾试过按进程缓存 64 MiB 状态块：块在 Medium 上确实命中（SILENT-C-13 每次求解约
160–300 块），但保留 64 MiB 后碎片 180 MB（分块无池 30–36 MB、基线 0.6 MB）、峰值 419 MB
（分块无池 330–355 MB），故未保留，当前每次 `Solve` 新建池。”当前状态为 12,696 字节，上限
16,512 个状态（209,636,352 B）。

### 7.4 SP2 的药水无路线复核

原文：“SP2 复核了 DX4 在第三幕五个牌组上看到的 16 个 `flash-potion-policy-no-route` 梯度层：
修正后只剩 `NECROBINDER-07` 的 2 个与 `SILENT-28` 的 2 个，都是现有求解器在同一请求上
`SMART_POTION_GRADIENT ... route_missing=true` 的双方无路线；其余 12 个由 Flash 找到路线。
`NECROBINDER-22` 的最终 61 胜来自同一生成药水搜索空间参与后的 `TurnBoundaryRescue`，
`REGENT-23` 的最终 63 胜来自梯度第 4 层（修正前该根 66 胜/12 回合，修正后 63 胜/9 回合）。”

### 7.5 开发策略与批次依赖

原文：“本批只在关闭模式支持开发策略：开启模式在分发点直接写
`FLASH_SEARCH_FAILED reason=development-strategy` 并停止部署，不猜测、不回落；将来若定义只含
Flash 特征的输入/接口，再由协调者决定放开。”当前正文改为“当前只在关闭模式支持”。
原文还写“计划成员与计划阶段的任意正深度侦察（PC）同样查看 `FlashMode`”；PC 只是来源批次标记。

## 8. 第三幕语料的批次数字（来自 `coverage/corpora/flash-decks/README.md` 当时记录）

DX3 批次用 `ACT_OVERRIDE=2 deck-survey.sh act3-<角色> <角色> 30` 为五个角色各生成 30 个第三幕
牌组（`actIndex = 2`，普通/精英/首领），并用 48 条随机游走对 150 个根做差分。修复前有 33 个根
不干净：16 个因 Dampen 把升级牌降为 0 级后规则表缺少基础等级而报 `UnsupportedClosure`，其余是
Globe Head/GalvanicPower、MagiKnight/DampenPower、Queen/ChainsOfBindingPower、不宁提灯、
Vambrace、Turret Operator/RampartPower、Axebot/StockPower、召唤物初始行动、HellraiserPower、
攻击后抽牌的死亡结算与 GigantificationPower 等内核语义分歧。修复后 150 个根除第三幕首领的
`score=rejected`（A3B 批次负责 `Act3BossStrategy` 评估移植）外全部零差异。该批的代表牌组与
48 条游走结论仍在 flash-decks 语料说明（`coverage/corpora/flash-decks/`，已于 2026-10-21 删除，见 Git 历史）。

## 9. 与现行测试记录的关系

本卷只保存“当时怎么改、数字是多少”的上下文。现行覆盖、验收数字与未验证项以
[Flash 测试记录](../../flash-search/testing.md) 2.1 为准；可复跑入口以
[Flash 回归工具](../../../tools/search/FlashRegression/README.md) 为准。本卷不表示任何旧结论已在
当前源码上重跑。
