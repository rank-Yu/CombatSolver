# Flash 测试记录（KS、KM 与早期端到端，2026-10-18 至 2026-10-19）

[返回 Flash 归档](README.md)

归档日期：2026-10-19。本卷保留强制行动/眩晕（KS）与敌人生成（KM）两批内核与内容的证据、审查修正，以及 FlashBeam v1 端到端与第一版主搜索分发的测试，章节编号沿用原测试记录；其中的耗时、质量与跳过项反映当时源码。当前证据见 [Flash 测试记录](../../flash-search/testing.md)。

### 2.1 强制行动与眩晕（KS，2026-10-18）

内核新增 `ForceMonsterMove` 与 `ForceStunnedMove`。规则编译给每个怪物追加一个合成 STUNNED 节点（无攻击、必须执行一次），其动态后继存于 `FlashEnemy.ForcedMove`，布局不变。`PrepareMonsterMovesForNextRound` 按现有引擎处理“眩晕且跳过下次行动时不推进”与“本轮已行动后被强制时不推进”。必须先执行一次的行动不再在编译时拒绝；导入映射进行中的 STUNNED 及其后继。新增遭遇乐加维林族母（首领）、噬尸蛞蝓（普通/弱）、骇鳗（精英），Power 沉睡、贪食、尖啸、活力；固定根 `coverage/fixtures/cards/flash-ks-*.json`。FlashKernelChecks 新增强制行动合同：缺省后继、不写行动日志、执行后推进、已行动后不推进、稳定边界清除标志。

审查修正：外部 agent 用反射清空导入私有 Fork 中乐加维林的 `_isAwake` 条目，现改为由 `DescribeFlashUnrepresentableState` 只放过这一个已证明惰性的条目，不修改模拟器。依据：现有引擎只在状态指纹与续接文本中读它，行动选择读的是沉睡层数。活力的私有状态原先按类型名认领，并依赖 Fork 时的断言；现改为通过 `VigorPowerMirrors` 的类型化访问认领，投影时显式要求状态空闲。

证据：四个遭遇的 FLASHM1/FLASHM2 自然根与四个固定根（每根 48 条游走）零差异。临时计数显示游走中眩晕确实触发并推进：乐加维林两个自然根的 74 次唤醒全部进入后继；噬尸蛞蝓普通的 280 次触发中 259 次推进，其余发生在本轮已行动之后，按规则不推进。

强制行动、眩晕与必须先执行一次的行动已有内核支持，敌人生成见 2.6。仍跳过的 6 个第一幕遭遇及缺失能力如下；仪式兽、瀑布巨兽、异蛙寄生虫各自的强制行动来源随遭遇一起实现：

| 能力 | 遭遇 |
| --- | --- |
| 其他召唤物（内核已支持生成，召唤物定义未实现） | 异蛙寄生虫、穿着一件大衣的两只地精、邪恶气体 |
| 苦难/附魔与改费用 Hook | 仪式兽、藤蔓蹒跚者、邪恶气体 |
| 阻止战斗结束、死亡后不离场 | 异蛙寄生虫、瀑布巨兽、穿着一件大衣的两只地精 |
| 偷窃与评估侧 | 穿着一件大衣的两只地精（被偷资源未建模）；瀑布巨兽（动态攻击伤害与评估特例） |

### 2.2 FlashBeam 端到端（v1，离线宿主 `OFFLINE_HARNESS_FLASH_SOLVE=1`）

同一根先用现有 `CombatBeamSolver` 求解，再走 Flash：准入 → FlashBeam → 逐步旧引擎回放核对 → 固定前缀物化。两个根的 Flash 路线逐步核对全部一致，物化成功。宿主默认档位（Beam 24、2000 节点），种子 FLASH1：

| 根 | 现有求解 | Flash（宽度 ×1，进攻席位 25%） | Flash（宽度 ×4） |
| --- | --- | --- | --- |
| `FUZZY_WURM_CRAWLER_WEAK` | 失血 4，第 3 回合结束，约 360 ms | 失血 1，第 4 回合，冷启动约 120 ms，热运行 40–56 ms | 失血 1，第 4 回合，热运行约 110 ms |
| `NIBBITS_NORMAL` | 失血 28，第 5 回合，约 600 ms | 失血 42，第 5 回合，冷启动约 120 ms，热运行 60–80 ms | 失血 37，第 5 回合，热运行约 170 ms |

结论：管线与语义一致性已打通；FlashBeam v1 的排序只移植了现有评分的核心项，在多敌人根上路线质量明显低于现有搜索，尚不能开放。搜索内单步约 5–8 µs/转移，远高于内核基准 0.4 µs，开销主要在搜索层（每个子节点的威胁投影、比较器分配、父链回溯），属于待优化项。

### 2.3 主搜索分发（Off/Shadow/On，协调器模式，2026-10-18）

离线宿主 `--search-mode Coordinator --budget-ms 10000`，宿主默认档位：

| 根 | 模式 | 结果 |
| --- | --- | --- |
| `NIBBITS_NORMAL` / FLASH1 | Off | 总扩展 1627、总转移 4896、最终失血 26 |
| 同上 | Shadow | 与 Off 完全相同（扩展、转移、分数、失血、求解器诊断行数）；对照行：旧主搜索失血 28、约 520 ms，Flash 失血 42、约 140 ms（搜索 94 ms），逐步核对通过 |
| 同上 | On | 采用 Flash 主搜索结果（失血 42），后续精炼与审计仍用旧后端，最终失血 28 |
| `flash-c1-choice-cards` | On | 采用 Flash 结果，零失血，协调器在战损目标处停止；总耗时 0.56 s |
| `flash-k3-basic-potions` | Shadow / On | 旧主搜索失血 27，Flash 失血 45（含药水动作），核对通过；两种模式最终失血都是 10 |
| 静默猎手初始牌组 / `NIBBITS_WEAK` | On | 准入拒绝 `UnsupportedCard STRIKE_SILENT`，回落旧后端 |

结论：分发、隔离、回落与药水覆盖均按设计工作。FlashBeam 主搜索的路线质量仍明显低于旧主搜索；当前只替换主搜索成员，整次请求的大部分耗时在旧后端的精炼与审计里，所以 On 的总耗时收益有限。

### 2.6 敌人生成与召唤（KM，2026-10-19）

内核新增 `SpawnMonster`、`NextFreeSlot`/`LastFreeSlot` 与 `BeginIllusionRevive`。每个 `FlashEnemy` 用 `Rules` 记录怪物规则表条目；根已知敌人占前 `RootEnemyCount` 项，之后每个可生成类型一项。可生成类型的状态机、静态值与初始私有整数由 `FlashRulesCompiler` 在根模拟器的私有 Fork 上执行 `MonsterSpawnSupport.Create` + `AddPredictedMonster` + `PreparePredictedMonster` 后编译，不手抄。`FlashBattle` 布局不变（`FlashEnemy` 仍是 64 字节，`FlashBattle` 6240 字节）：`Rules` 占用原填充的第一个字节，`Slot` 改存遭遇槽下标（无槽名 = 255），`NextCombatId` 用于生成敌人的 CombatId。

怪物私有整数改为显式认领：`FlashMonsterDef.AiRegisterNames` 按声明顺序映射到 `FlashEnemy.Ai`，`InertMonsterIntNames` 认领只被状态指纹/续接文本读取的条目；其余 `_monsterIntStates` 条目在校验时拒绝。乐加维林族母的 `_isAwake` 由定义声明，`DescribeFlashUnrepresentableState` 不再整体拒绝 `_monsterIntStates`。

内容：雾菇与双尾鼠的两个第一幕遭遇，以及召唤物 `EyeWithTeeth`、Power `IllusionPower`（幻象死亡后强制合成 `REVIVE_MOVE`，回满生命后按最近行动推进；尸体保留增益与临时减益）和 `MinionPower` 的次要敌人语义。双尾鼠的 `_turnsUntilSummonable`/`_callForBackupCount` 走 AI 寄存器：三个攻击/尖啸行动递减前者，召唤在 `LastFreeSlot` 生成新鼠并把所有鼠的计数同步为最大值 + 1，分支权重按 `CanTwoTailedRatSummon` 切换。

K0 新增生成合同：CombatId 递增、可取值耗尽与 HashSet 枚举顺序下的生命抽样、遭遇槽稳定名单顺序、`NextFreeSlot`/`LastFreeSlot` 的 null/空串区别、玩家回合立即掷初始行动与敌人回合待掷、幻象复活节点与动态后继。

证据：`FOGMOG_NORMAL` 与 `TWO_TAILED_RATS_NORMAL` 的 FLASHM1/FLASHM2 自然根，以及固定根 `coverage/fixtures/cards/flash-km-fogmog-normal.json`、`flash-km-two-tailed-rats-normal.json`，每根 48 条游走全部零差异（K1/K2/K4）。临时计数显示游走中召唤确实发生：雾菇自然根 48 条游走共生成 48 只幻象、幻象复活 263 次、召唤者死亡 18 次；雾菇固定根（玩家 3000 生命）48 条游走共生成 48 只幻象、复活 120 次、召唤者死亡 48 次；双尾鼠自然根生成 118 只、固定根生成 130 只，固定根有 3114 个边界状态的鼠计数达到 3（上限）且 102 个边界状态没有空槽。临时计数代码已删除。
