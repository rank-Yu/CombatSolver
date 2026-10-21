# 第三幕生成牌组语料

本目录保存五个角色各 30 个第三幕生成牌组，共 150 个根，用于第三幕内容闭包、Dampen 与
第三幕首领策略的差分回归。每个子目录是一份 `spec.json` + `request.json`，角色目录为
`ironclad`、`silent`、`defect`、`regent`、`necrobinder`。

- **来源**：`.local/tool-tasks/flash-search/inputs/act3-<角色>/`，由旧 `act3-survey.sh` 以
  `ACT_OVERRIDE=2 deck-survey.sh act3-<角色> <角色> 30` 生成。
- **生成口径**：固定进阶 10、`actIndex = 2`，其余与 `deck-survey.sh` 相同：
  `includeStartingDeck`/`includeStartingRelics`/`includeAscendersBane` 为真；
  `characterCards.count = 6 + (i % 7)`、`colorlessCards.count = i % 3`、
  `relics.count = 2 + i % 4`、`potions.count = i % 3`；遭遇种类按 `i % 3` 取
  Monster/Elite/Boss，每个角色三类各 10 个；种子为 `FLASH-DECK-<角色>-<NN>`。
- **用途**：`flash_regression.py diff --only act3`（或 `--include-act3`）对本目录每个
  `request.json` 跑 48 条随机游走的 K1/K2/K4/K5 差分。默认 493 根回归不含这 150 根；加入后
  为 643 根。第三幕首领的评分由 `Act3BossStrategy` 覆盖。
- **复跑**：

  ```bash
  python3 -B tools/search/FlashRegression/flash_regression.py diff \
    --label act3 --only act3 --act3-walks 48
  python3 -B tools/search/FlashRegression/flash_regression.py diff \
    --label all --include-act3 --walks 32 --act3-walks 48
  ```

  只复跑一个角色或一个根时用 `--root act3-SILENT-` 或 `--root act3-SILENT-07`。
- **记录**：结果写入 `.local/flash-regression/`；本目录只保留输入。替换或新增牌组时同步
  `request.json` 内的仓库相对路径。

当前证据与未验证项见 [Flash 测试记录](../../../docs/flash-search/testing.md)。
