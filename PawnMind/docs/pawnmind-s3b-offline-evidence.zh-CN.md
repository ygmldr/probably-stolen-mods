# PawnMind S3B：新局菜单的语义路由、分页与聚焦

**边界**：C# 与 Python 均已离线完成；**没有**启动游戏、没有调用真实 `/action`、没有创建/读取任何存档。所有新局写操作在生产装配中仍被 S4 占位策略**固定拒绝**，目前只有 `get_new_game_options` 可读。

## 交付

| 层 | 内容 |
|---|---|
| 语义工具（7 个） | `get_new_game_options`（读）、`open_new_game_menu`、`select_new_game_option(kind,id)`、`new_game_next_page(row,±1)`、`new_game_continue`、`focus_new_game_selected_perk(id)`、`blur_new_game_option(id)` |
| [C# 服务](<../src/Bridge/NewGameMenuService.cs>) | 每次写都先过槽策略函数（当前固定 `new_game_slot_policy_unavailable`）；动作只委托一次原生回调并回读校验；显式白名单 JSON，绝不输出原生 slot 参数、存档预览或候选注册表 |
| [Core 控制](<../src/Core/NewGameMenuControl.cs>) | 分页行、首局 prompt 的**可见文本**、控制端口与结果 DTO；`prompt_pending` 是终止状态，不当作已开始 |
| [Game 适配器](<../src/Game/NewGameMenuAdapter.cs>) | 打开=`MainMenuUIController.OnNewGameClick`（游戏内拒绝）；翻页=一次原生 `TurnPage`/`TurnSelectedPage`；继续=仅 scenario/perks 的原生 Continue，难度页直接 `difficulty_start_forbidden`；聚焦/失焦=对目标槽调用真实 `WildNetworkSlot.OnPointerEnter/OnPointerExit`，**不写**私有 `hovered/views` |
| 强制入口 | `ActionRouter` 读白名单只加 `get_new_game_options`；7 个写 op 全部要求 `rev` + `action_id`，并受版本门禁与幂等缓存约束；`act` 包裹仍走同一 dispatch |

## 同 ID 卡片的撤销路径（重点）

原生 `OnSlotClicked(id)` 先找 hovered 的同 ID 卡，再找第一个同 ID 卡；候选区的卡先构建。因此“刚选中、候选区同 ID 卡也在当页”时单次点击会落空。S3B 的处理是**两个各自单步、状态可见**的动作：

1. `focus_new_game_selected_perk(id)`：把真实 hover 移到 selected 行卡；若候选卡仍处于 hover，返回 `hover_conflict` 而不是偷偷清理。
2. `blur_new_game_option(id)`：对当前 hover 卡调用一次真实 pointer-exit。
3. 再次 `select_new_game_option(perk,id)` 才能撤销。

每一步都回读 `hovered_id`/`hovered_in_selected_row` 并写入结果，AI 必须看到中间态；这不是一个工具内的原子 hover+click。真机上鼠标事件竞争、真实 SlotView 状态仍待 S8 验证。

## 验证

- Red：先加测试后编译失败（缺 Control 端口/服务类型）；随后分批出现 17 项 Service 失败与 1 项 Session hover 失败，再最小实现转绿；主审另追加“过期页可见标志”和“重复选中一次 RemoveAll 删多项”两类负例，均先红后绿。
- Green（主代理独立复跑）：`REQUIRE_GAME=1 ./test.sh` → C# **304**、脚本门禁 **53**、适配器契约 **382**，`adapter contract OK`，`strict mode: REQUIRE_GAME=1`，**无 SKIP**；`dotnet build src -c Release` 0 警告/0 错误；Python **20** 项 OK、0 skipped；`git diff --check` 无错误。

## 明确缺口（不得当成已完成）

1. **所有新局写操作仍被占位策略拒绝**：S4 落地前，真机上 7 个写 op 全部 fail-closed，这是刻意设计，不是回归。
2. **首局 prompt 只报告不确认**：确认弹窗属于“一次原生新局写盘许可”，留 S4/S8；难度页 Continue 就是原生 Start，S3B 永久禁止。
3. **真实 UI 未跑**：分页页大小、可见元素、hover 行为目前以 Rev13 静态契约锁定；游戏更新会先让严格适配器红。
4. **首局空白配置仍锁特质**（`totalRunCount==0`），不要伪造解锁；S8 需与你确认最终运行环境。
5. 旧工具 `new_game`/`load_game` 尚未被 S4 策略统一约束，S4 必须让它们与 `act`/`press` 一起 fail-closed。
