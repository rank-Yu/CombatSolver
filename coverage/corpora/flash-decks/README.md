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
