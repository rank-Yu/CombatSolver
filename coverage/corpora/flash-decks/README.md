# 生成牌组语料

本目录保存 `.local/tool-tasks/flash-search/deck-survey.sh` 生成、经 Flash 准入并在 48 条随机游走上做过严格差分的各角色中期牌组。每个子目录是一份 `spec.json` + `request.json`，`request.json` 的 `generatedScenarioPath` 指向本目录内的规格。

- 来源：`deck-survey.sh` 对 `FLASH-DECK-IRONCLAD-<NN>` 种子生成；每个场景固定进阶 10、`includeStartingDeck`/`includeStartingRelics`/`includeAscendersBane` 为真，`characterCards.count = 6 + (i % 7)`、`colorlessCards.count = i % 3`、`relics.count = 2 + (i % 4)`、`potions.count = i % 3`，按 `i % 2` 选第一/二幕、按 `i % 3` 选普通/精英/首领。
- 收录范围：`deck-ironclad4` 调查中准入的 32 个牌组，加上任务表保留的 `IRONCLAD-46`（SecondWind 与 BurningSticks 回归）；它在本目录建立时尚未生成旧调查的 `flash-diff.txt`，修复后已与其余牌组同样准入。
- 用途：`.local/tool-tasks/flash-search/regress.sh` 对每个 `request.json` 跑 32 条随机游走的 K1/K2/K4/K5 差分；这是固定根之外的内容组合回归，不替代 `coverage/fixtures/` 的单机制 fixture。
- 复跑：`OFFLINE_HARNESS_FLASH_DIFF=1 OFFLINE_HARNESS_FLASH_DIFF_WALKS=32 dotnet <OfflineSearchHarness.dll> --request coverage/corpora/flash-decks/<ID>/request.json --milestone M1 --out <输出目录>`。
- 记录：结果写入 `.local/`；本目录只保留输入。新增或替换牌组时同步 `request.json` 内的仓库相对路径。
- 边界：`IRONCLAD-02` 与 `IRONCLAD-18` 含 Thrash；它的 `ModifyDamage` 小数结果在现有引擎里写入卡牌 `DynamicVars.Damage`，Flash 的整数卡牌变量无法精确表示，差分汇总会把该路径记作 `unsupported`，不是分歧。其余牌组在 32 条游走上为零差异。

## 多角色补充（LS 批次）

LS 批次在 `ls-silent-v4`、`ls-necrobinder-v3`、`ls-defect-v3` 三份随机牌组调查中分别准入 20、13、27 个牌组，并补入以下代表组合（每个角色不超过 8 个，优先覆盖本批修复点与剩余内容）：

- `SILENT-08/12/13/14/20/27`：覆盖 EchoingSlash、KnifeTrap、TheHunt、Burst、Mirage、ToolsOfTheTrade、Tingsha、BeltBuckle。
- `NECROBINDER-00/02/08/16`：`NECROBINDER-16` 是 MiniatureCannon 对 Osty 攻击加伤的修复根；另覆盖 Misery、SentryMode、Pagestorm 与 Osty/Doom 组合。
- `DEFECT-05/08/19/26/27/28`：五个是 LS 修复根（Scrape 重复牌弃置、Slippery 的 `AmountOnTurnStart`、MiniatureCannon/HandOfGreed 致命判定、Plasma 球评估、Omnislice 召唤死亡顺序），`DEFECT-19` 覆盖 EssenceOfDarkness。
- `REGENT-07`：既有储君准入组合，保持跨角色覆盖。

这些牌组与铁甲战士语料使用同一 `request.json` 格式；全量回归对每个请求跑 32 条随机游走，LS 批次另外对三份调查中全部准入牌组跑过 48 条游走（见 `.local/tool-tasks/flash-search/LS-report.md`）。

## 铁甲战士之外的角色（CX 批次）

CX 批次对五个角色各跑了一次同格式调查（`cx0-*` 为改动前，`cx1-*` 为改动后）：铁甲战士 60、静默猎手/故障机器人/储君/亡灵契约师各 30。改动前只有亡灵契约师的 6 个牌组被拒（`MELANCHOLY` 的局外 run-listener Hook），其余四角色已全部准入；改动后五角色全部准入。

新增代表牌组（48 条随机游走 K1/K2/K4/K5 零差异，`unsupported=0`）：

- `NECROBINDER-06/24/26`：三个都含 `MELANCHOLY`，覆盖局外牌组副本的准入例外与 `AfterDeath` 费用下调。`NECROBINDER-24` 另覆盖 `BONE_SHARDS` 在 Osty 已死时跳过攻击与格挡（现有引擎 `BoneShardsOnPlay` 的 `CheckMissingWithAnim` 门控）这一新准入根暴露的修正；`NECROBINDER-26` 是首领战，48 条游走里带 85 个选择动作的组合。

其余 3 个新准入牌组（`NECROBINDER-12/15/28`）不在本目录，但同样跑过 48 条游走零差异。

## 第三幕随机牌组（DX3 批次）

DX3 批次用 `ACT_OVERRIDE=2 deck-survey.sh act3-<角色> <角色> 30` 为五个角色各生成 30 个第三幕牌组（`actIndex = 2`，普通/精英/首领），并用 48 条随机游走对 150 个根做差分。修复前有 33 个根不干净：16 个因 Dampen 把升级牌降为 0 级后规则表缺少基础等级而报 `UnsupportedClosure`，其余是下列内核语义分歧。修复后 150 个根除第三幕首领的 `score=rejected`（A3B 批次负责 `Act3BossStrategy` 评估移植）外全部零差异。

新增代表牌组（每个角色 2 个，48 条游走 K1/K2/K4/K5 零差异，`unsupported=0`）：

- `IRONCLAD-19`（精英）：MagiKnight 的 Dampen 与降级后的 Rampage 累计伤害；`IRONCLAD-23`（首领）：测试体的复活窗口与 Hellraiser 的“无可击中敌人”计数分支。
- `DEFECT-20`（首领）：GigantificationPotion 的 `PowerAmountPredictionState` 在玩家死亡后的回写；`DEFECT-24`（普通）：组装师召唤的机器人在敌方回合中断前就采用状态机初始行动。
- `NECROBINDER-13`（精英）：降级后的 TheScythe 累计伤害；`NECROBINDER-27`（普通）：Globe Head 的 GalvanicPower 改打牌的所有者（玩家）。
- `REGENT-00`（普通）：Living Shield 离场后 RampartPower 仍给 Turret Operator 格挡；`REGENT-02`（首领）：ChainsOfBindingPower 对克隆牌的束缚标记。
- `SILENT-23`（首领）：Envenom/不宁提灯/蛇骷髅的施加来源（`cardSource: null` 不翻倍）；`SILENT-29`（首领）：Vambrace 在 Sly 嵌套自动打出后的锁存。

这些牌组与既有语料使用同一 `request.json` 格式；`regress.sh` 对每个请求跑 K1/K2/K4/K5 差分。

## 评估杂质与 HexPower（K4C 批次）

K4C 批次修正了 `FlashScorer` 的牌库杂质判定：现有引擎用 `card.HasKeyword(State, Ethereal)` 读取回合末离手例外，而 `HexPower` 会通过 `TryModifyKeywordsInCombat` 让所有 `Hexed` 牌获得 `Ethereal`；Flash 此前只读卡上的本地关键字，把 SpectralKnight 施加 `Hex` 后手牌里的 `DEBRIS` 误计为长期杂质（K1/K2 状态逐字节一致，只有 K4 的 `LiveDeckClutter` 与 `Score` 偏差）。修正为同一 battle-aware `FlashKernel.HasKeyword` 后，第三幕 REGENT-04 的 33 个 K4 差异清零；Pagestorm 的 `EtherealDrawTriggers` 投影也改用同一读取口径。

- `REGENT-04`（精英，KNIGHTS_ELITE）：`CRASH_LANDING` 补满手牌生成的 `DEBRIS` 被 SpectralKnight 的 `HEX` 变成 `Hexed`，48 条游走 K1/K2/K4/K5 零差异、`unsupported=0`，`scoreFailed=0`、`boundFailed=0`。根上另有 `ANCHOR`、`MUMMIFIED_HAND` 与 `GAMBLERS_BREW`，`EndTurn(choices=1)` 的回合开始选择照常覆盖。
