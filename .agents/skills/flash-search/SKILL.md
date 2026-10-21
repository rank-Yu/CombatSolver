---
name: flash-search
description: 修改 Flash 搜索后端：src/Engine/InCombat/Flash 内核与内容定义、src/Search/FlashBeam 的准入/导入/搜索/物化、BeamSolveDispatch 与 Flash 模式，以及 Flash 差分测试与批量回归。
---

# Flash 搜索后端

## 适用边界

先读 [Flash 入口](../../../docs/flash-search/README.md) 和与任务相关的子文档；批量回归入口见 [Flash 回归工具](../../../tools/search/FlashRegression/README.md)。Flash 是受校验的派生实现，语义权威是现有引擎：

- Flash 与现有引擎不一致时，修 Flash。
- 发现现有引擎与原版不一致时，停下来转 `combat-semantic-change` 修现有引擎，Flash 再跟随；不要让 Flash "更正确"。
- 搜索政策和预算问题转 `search-performance-optimization`；Flash 不能用扩大预算掩盖语义差异。

## 硬约束

- 内核（`src/Engine/InCombat/Flash/`）只依赖 BCL：不写 `using MegaCrit`、`using Godot`、`using HarmonyLib`，不引用 `CombatSolver.*` 其他命名空间。
- 内核热路径不分配托管对象：不用 LINQ、`List<T>`、闭包、装箱、字符串拼接、`params` 数组。临时数据用 `stackalloc` 或 `FlashBattle` 内的槽位。
- 内容定义无字段状态；可变值只存在 `FlashBattle`。
- 原版数据里已有的数值从 `FlashRules` 读取，不硬编码；原版代码中的常量写进定义时注释出处（反编译文件路径与方法名）。
- 容量溢出抛 `FlashCapacityException`；未知内容或未实现的组合抛 `FlashUnsupportedException`。不截断、不跳过、不返回默认值。
- 不新增 `catch (Exception)`。

## 新增一项内容

1. 定位原版实现：`.local/decompiled/sts2-v0.111.0/` 下的同名模型类（只读参考，按需定向读取）。
2. 定位现有引擎的实际路径：卡牌看 `MethodMirrorRegistry` 的登记、`CardOnPlayInferrer` 推断、`CardEffectSpecRegistry` 与 `CorePowerSupport.ApplyCardPowers`；Power/遗物看 `HookMirrors` 与 `M/Hooks/**`；怪物行动看 `P/MonsterMoveEffects.cs`。**以现有引擎的执行顺序为准**，包括补偿效果在镜像效果之后执行这类偏差。
3. 写定义：放在 `Content/<类别>/` 对应文件，声明 `[FlashContent("<原版 Id>")]`，只覆写需要的 Hook。
4. 补规则编译：定义读取的每个数值都要在 Bridge 的规则编译中从原版模型取出。
5. 补 fixture：在 `coverage/fixtures/` 的对应主题下增加或扩展 fixture，让该内容在前几步内被触发。
6. 跑验证（见下），把命令与结果写进提交说明或任务交付。

## 验证

- 纯内核：编译并运行 `tools/testing/checks/FlashKernelChecks/`。
- 差分：`python3 -B tools/search/FlashRegression/flash_regression.py diff --label <标签> --only fixtures --root <fixture>`（或 `--only decks --root <牌组>`），工具负责 Release 构建、离线宿主与 K0，并运行到 `--milestone M1`；要求 `flash-diff.txt` 首行 `failed=0 walkFailed=0`。全量根集合用 `diff --walks 32`，第三幕语料用 `--only act3`。
- 批量回归：`scan`（候选路线扫描）、`bench`（48 根基准）与 `coordinator`（79 根 Off/On 对照）同样经该入口；口径、输出与验收条件见 [Flash 回归工具](../../../tools/search/FlashRegression/README.md)。直接设置 `OFFLINE_HARNESS_FLASH_DIFF=1` 只用于调试单根。
- 结构门禁：`./tools/inspection/verify-refactor-boundaries.sh`（Windows 用同名 `.ps1`）。
- 文档改动：`python3 -B tools/inspection/verify-documentation.py`。

只通过编译不算完成。差分出现差异时，报告第一处差异的字段路径、动作序列与种子。

## 批量任务卡

内容批量实现可以交给外部 coding agent。任务卡放在 `.local/tool-tasks/flash-search/`（不提交），每张卡写明：

- **目标**：要实现的内容 Id 列表。
- **参考**：原版反编译文件路径、现有引擎对应镜像或补偿的路径。
- **允许修改的文件**：通常只有对应 `Content/` 文件、规则编译中的对应条目和 fixture。
- **必须通过的命令**：上面的验证命令及要求的结果。
- **禁止事项**：本 skill 的硬约束；不修改内核核心、Bridge 结构、现有引擎和其他内容。
- **交付物**：改动文件列表、验证命令输出摘要、未完成项。

执行者在独立 git worktree 中工作。合入前由维护者审查定义是否与现有引擎路径一致、差分证据是否覆盖该内容。
