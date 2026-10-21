# Flash 模块目录规则

本目录是可选的 Flash 搜索后端，规格见 `docs/flash-search/`，工作流程见 `.agents/skills/flash-search/SKILL.md`。以下规则补充仓库根 AGENTS.md。

| 子目录 | 命名空间 | 职责 |
| --- | --- | --- |
| `Kernel/` | `CombatSolver.Flash.Kernel` | 结构体状态 `FlashBattle`、回合与出牌管线、结算与规则表 |
| `Content/` | `CombatSolver.Flash.Content.*` | 卡牌、Power、遗物、药水、球、附魔与怪物定义 |
| `Search/` | `CombatSolver.Flash.Search`（含 `.Scoring`） | `FlashBeamSolver` 与状态评估 |
| `Bridge/` | `CombatSolver.Flash.Bridge` | 准入、规则编译、旧引擎状态导入、动作与选择翻译、路线核对与物化、开启模式失败约定 |

依赖方向：`Bridge → Search → Kernel/Content`。协调器只经 `src/Search/BeamSolveDispatch.cs` 进入本模块；原搜索类给 Flash 开放的读取接口留在 `src/Search` 的分部文件中（`*.Flash.cs`、`SimulatedCombatState.FlashProjection.cs`）。

## Kernel 与 Content

- 只依赖 BCL：不引用 MegaCrit、Godot、HarmonyLib，也不引用 `CombatSolver` 的其他命名空间（本模块的 `Search`、`Bridge` 也不行）。
- 语义以现有引擎为准；改动任何结算顺序或内容定义都要有与现有引擎的差分证据。
- 热路径不分配托管对象；内容定义没有字段状态，可变值只存在 `FlashBattle`。
- 原版数据中已有的数值从 `FlashRules` 读取，不硬编码。
- 溢出抛 `FlashCapacityException`，未知内容抛 `FlashUnsupportedException`；不截断、不跳过、不返回默认值，不写 `catch (Exception)`。
- 修改 `FlashBattle` 字段时同步 `docs/flash-search/state-layout.md` 与布局尺寸断言。
- `FlashHookMask` 的 64 位已用满：新 Hook 加在 `FlashHookMaskExt`，不复用、不重排已有位。

## Search 与 Bridge

- 两者都遵守 `src/Search` 的搜索层规则（结构门禁把它们计入 Search 文件）。
- 只有 `Bridge/` 可以读取旧引擎内部状态和原版模型；`FlashBeamSolver` 只操作 `FlashBattle` 与 `FlashRules`。
- 不引用 `BeamSolveDispatch`、`CombatSearchCoordinator`、`CombatBeamSolver`、`SolverSettings.Current`、UI 或 Runtime 单例；外部能力通过 `SearchPolicySnapshot` 与请求参数注入。
- 准入只判断不修改；拒绝必须给出稳定原因码。
- 只捕获 `FlashUnsupportedException`、`FlashCapacityException`、校验失败与取消。子状态级的 `FlashCapacityException` 在 `FlashBeamSolver.ExpandAction` 内作为搜索边界丢弃该分支并计数，回合结束层按容量压缩继续，不交出半成品路线；根导入/根评分抛出的容量、`FlashUnsupportedException`、校验失败与取消让整个 Flash 求解作废，开启模式下以 `FLASH_SEARCH_FAILED` 失败，不回落原搜索。
- 交给协调器的结果必须由旧引擎回放物化，不能由 Flash 状态直接拼装 `SolverResult`；物化走共享 `SearchRouteMaterializer`，回放只使用 `SearchReplayCore`/`OpeningProbe`。
