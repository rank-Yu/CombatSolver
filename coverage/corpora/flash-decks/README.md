# 生成牌组语料

本目录保存 `.local/tool-tasks/flash-search/deck-survey.sh` 生成、经 Flash 准入并在 48 条随机游走上做过严格差分的铁甲战士中期牌组。每个子目录是一份 `spec.json` + `request.json`，`request.json` 的 `generatedScenarioPath` 指向本目录内的规格。

- 来源：`deck-survey.sh` 对 `FLASH-DECK-IRONCLAD-<NN>` 种子生成；每个场景固定进阶 10、`includeStartingDeck`/`includeStartingRelics`/`includeAscendersBane` 为真，`characterCards.count = 6 + (i % 7)`、`colorlessCards.count = i % 3`、`relics.count = 2 + (i % 4)`、`potions.count = i % 3`，按 `i % 2` 选第一/二幕、按 `i % 3` 选普通/精英/首领。
- 收录范围：`deck-ironclad4` 调查中准入的 32 个牌组，加上任务表保留的 `IRONCLAD-46`（SecondWind 与 BurningSticks 回归）；它在本目录建立时尚未生成旧调查的 `flash-diff.txt`，修复后已与其余牌组同样准入。
- 用途：`.local/tool-tasks/flash-search/regress.sh` 对每个 `request.json` 跑 32 条随机游走的 K1/K2/K4/K5 差分；这是固定根之外的内容组合回归，不替代 `coverage/fixtures/` 的单机制 fixture。
- 复跑：`OFFLINE_HARNESS_FLASH_DIFF=1 OFFLINE_HARNESS_FLASH_DIFF_WALKS=32 dotnet <OfflineSearchHarness.dll> --request coverage/corpora/flash-decks/<ID>/request.json --milestone M1 --out <输出目录>`。
- 记录：结果写入 `.local/`；本目录只保留输入。新增或替换牌组时同步 `request.json` 内的仓库相对路径。
- 边界：`IRONCLAD-02` 与 `IRONCLAD-18` 含 Thrash；它的 `ModifyDamage` 小数结果在现有引擎里写入卡牌 `DynamicVars.Damage`，Flash 的整数卡牌变量无法精确表示，差分汇总会把该路径记作 `unsupported`，不是分歧。其余牌组在 32 条游走上为零差异。
