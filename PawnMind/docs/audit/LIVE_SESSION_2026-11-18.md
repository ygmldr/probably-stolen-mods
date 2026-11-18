# LIVE_SESSION_2026-11-18 — 第一次真实开局与首笔成交

> 执行者：用户授权后，由 Agent 通过本地 HTTP 桥驱动（`run_mode=test`）。
> 游戏：*Probably Stolen* 0.46D，`VERSION 100-Rev13` / MVID `911508a6-…`。
> 安装：`6adef8ac…`（Round 4 构建）。
> 证据等级：**LIVE**（真实游戏进程、真实存档槽 43）。
> 未做：没有读写其它存档槽；没有 `new_game`（走的是原生菜单 + `confirm_new_game`）。

## 1. 做成了什么

| 步骤 | 结果 |
|---|---|
| 原生开局菜单（specialization → perks → difficulty） | 全部 `completed` / `dispatched` |
| 特质 | `saving`(+150) / `proficient_scavenger` / `lucky`，3/3 点 |
| 难度 | `normal` |
| `confirm_new_game` | 写进槽 43；Day 1，现金 700，租金 200（6 天） |
| `open_shop` | `MORNING → OPEN` |
| 上货 | `move_item` ×7 → 后台货架 7 件 |
| 首笔成交 | 星际客买走「泥炭合成肉」，**现金 700 → 729** |

## 2. 三个真机缺陷（本轮已修其中两个）

### 2.1 交易证据粒度错误 → 交易死锁（已修）

**现象**：成交后响应是

```json
{"ok": false, "error": "outcome_unverified", "detail": "business_not_complete",
 "execution_status": "uncertain", "dispatch_status": "dispatched", "staged_item_id": 13}
```

而同一时刻 `/state` 显示现金 +29、货架少一件。随后第二笔立刻被挡：

```json
{"error": "operation_pending", "execution_status": "uncertain"}
```

**根因**：`TradeService` 把 `business_complete`（顾客**整趟**逛完）当成"**这一笔**成交"的判据。
顾客想买 3 件时，第一件卖完 `business_complete` 仍为 false → 每笔部分成交都 `uncertain`；
而未终态的 `confirm_trade` 被 `ActionGate.FindPendingFor` 按 D4 规则挡住 →
**一笔正常成交把交易功能锁死到下次重启 Mod**。

**修法**（`src/Bridge/TradeService.cs`）：
`tradeProven = items_transferred && client_unchanged`；
`BusinessComplete || tradeProven` 即 `completed`，并把两个事实分开输出：
`business_complete` / `visit_complete`（整趟）+ `items_transferred`（这一笔）。

**测试**：`R4_TRADE_01/02/03`（后者专门钉死"成交后不再阻塞下一笔"）。

### 2.2 谈判面板不会自己开（已记录，未加新工具）

**现象**：`confirm_trade` → `{"error":"no_negotiation","execution_status":"rejected",
"dispatch_status":"not_dispatched"}`。

**原因**：面板由原生的"把商品放上称重台"（`PlayerStore.AddedItemToWeightedArea` →
`HandleAutoNegoUIDisplay` → `NegociationUIManager.OpenUI()`）触发。桥里没有"打开谈判"工具。

**实测可用的等价动作**（玩家就是拖一下）：

```
move_item(item, "main")   # 取下
move_item(item, "back")   # 放回 → 触发原生 OpenUI()
```

重放后 `negotiation_open:true`、`negotiated_item` 指向该商品，随后 `confirm_trade` 成功。

**OPEN**：建议后续加一等公民 `open_negotiation(item_id)`；本轮不加，因为会让公开工具面
从 116/106 变成 117/107，并改动 policy 哈希，需要单独一轮审计。当前把上述两步写进 Skill。

### 2.2b 覆盖层关不掉（已修，范围极窄）

教程覆盖层（`CanvasScreenLayer2/_ControlOverLay`）和「对话与打字机设置」
（`_DialogueOptionUI`）没有对应的类型化工具，而 `press` 被录制策略**在所有状态下**
拒绝（`recording_press_forbidden`）。`dismiss_tutorial_hints` 只能关掉"以后是否提示"
这个开关，关不掉屏幕上已有的覆盖物。

**修法**（`src/Core/UiPressPolicy.cs`）：

- `press` 只在 `run_mode=test` 下放行，其余模式仍然一律 `press_forbidden`；
- 放行后还必须通过**三条路径的白名单**：`_ControlOverLay/CloseOverlayButton`、
  `_DialogueOptionUI/Panel/CloseButton`、`_DialogueOptionUI/Panel/ApplyButton`
  （Unity 的重名后缀 ` (1)` 会被忽略）；其他任何控件返回 `control_not_allowed`。
- 白名单是纯函数（Core），有独立单测；守卫侧另有"仅 test 放行"的回归测试。

**测试**：`UiPressPolicyTests`（4）、`RecordingDispatchGuardTests.Guard_Press_Is_Released_Only_In_Test_Mode`。

### 2.3 新开局无法确认"跨局"（已修，F1b 回归）

**现象**：`confirm_new_game` 返回 `run_change_status:"unknown"` 且永不改变。

**根因**：开局前 `ReadRunId()` 返回 `null`（主菜单无 `PlayerStore`），而 `RunChangePolicy.Classify`
要求"前后都可读且不同"才判 `confirmed` → 从主菜单开局这条路径**永远确认不了**。
后果不只是显示错误：该未确认操作会让 `FindUnconfirmedRunChange` 挡住之后所有
`load_game`（`run_change_pending`），本局内无法读档重来。

**修法**：
- `RunChangePolicy.NoRun` 哨兵，区分"确定没有 run"（主菜单）与"读不到"（加载中/异常）；
- `Classify`：`NoRun → real` = **confirmed**（之前根本没有 run，无可误归属）；
  `real → NoRun` = **unknown**（可能正在换档，不驱逐）；`NoRun → NoRun` = requested；
  任一为 `null` = unknown；
- `GameState.ReadRunId()` 在主菜单返回 `NoRun`。

**测试**：`R4_F1_10/11/12/13`。

## 3. 真机确认"闸门是对的"

| 场景 | 响应 | 判定 |
|---|---|---|
| 在难度页用 `new_game_continue` 想直接开局 | `error:"difficulty_start_forbidden"`, `rejected`, `not_dispatched` | ✅ 开局只能走 `confirm_new_game` |
| 面板没开就想成交 | `error:"no_negotiation"`, `rejected`, `not_dispatched` | ✅ 未派发不是失败 |
| 槽位配置 42 但游戏下一个自动槽是 43 | `recording_slot_not_next_free` 前先由 `get_recording_save_status` 暴露 `next_free_slot` | ✅ 预检可用 |

## 3.2 第 2–3 次会话：剩下两个结构性缺陷（已修）

### A. 一次 uncertain 会锁死整个 op 类（已修，收窄到"同一个调用"）

`ActionGate.FindPendingFor(op)` 只按 op 名匹配，所以一笔 `confirm_trade` 变 `uncertain`
之后，**换一件商品也不允许再交易**。真机复现：顾客预算用完后每点一次都静默拒绝，
一次连锁产生 5 个死操作，把 5 件商品锁在本会话里。

修法：`FindPendingFor(op, fingerprint)`，只有 `PendingScope.IsCallScoped(op)`（当前仅
`confirm_trade`）才按参数指纹匹配；`end_day`/`sleep` 等仍按 op 名保守阻塞
（`end_day` 带不同 flag 仍是同一次快门点击，已有回归测试钉住）。

### B. sleep 永远不会收敛（已修）

`sleep` 点击的是 `EndOfDayUIManager.OnEndDayButtonClick`。当"对话与打字机设置"
模态面板开着时，点击被吞掉 → 原生返回 `Called` 但什么都没发生 → `running` →
之后所有 `sleep` 都被 `operation_pending` 挡住，**会话彻底死掉**（复现两次）。

修法：`RecordingSaveService.TryResolveSleep`：
- 派发时记住 `day`；
- 回读 `day` 增长 → `completed`；
- 连续 3 次回读都没增长 → `failed("day_not_changed")`；
- `PawnMindMod.VerifyOperation` 对 `op=="sleep"` 调用它，`get_operation` 因此能把
  挂起的 sleep 判终态，解除阻塞。
测试：`Sleep_Verifier_Sees_The_Day_Advance`、`Sleep_Verifier_Gives_Up_After_Repeated_No_Op_Readbacks`。

## 3.3 第 4–6 次会话：仍未解决的问题

### C. `sleep` 被模态面板吞掉点击（**已回滚，前提错误**）

`EndOfDayUIManager.OnEndDayButtonClick` 在「对话与打字机设置」
（`CanvasScreenLayer2/_DialogueOptionUI`）模态面板开着时**静默无效**。
该面板会在每天/每位顾客后重新出现，脚本里的 `press` 能关掉它，
但只要有一次 `sleep` 在面板打开时发出，就会得到一个"点过但世界没变"的操作。

`TryResolveSleep`（本轮新增）能在 3 次回读后把它判成 `failed("day_not_changed")`，
但**前提是能拿到那个 op 的 id**——见 D。

~~修法：`RecordingGameState.EndOfDayReady` + 派发前检查。~~

**已全部回滚。** 装机实测：加上这个检查后每一次 `sleep` 都被拒
`end_of_day_panel_closed`，而之前同样的局面睡眠是成功的。原因在反编译里：

```csharp
public bool IsOpen { get { if (UI != null) return UI.activeSelf; return false; } }
```

`UI` 是序列化字段，游戏里没有接线，永远为 null → `IsOpen` **恒为 false**。
用它当"面板是否可点"的判据等于把正常路径全禁掉。

教训：契约测试只能证明成员存在，证明不了它在这个版本里被赋值。
用某个新读数字段去**否决**一条本来可用的路径前，必须先在真机上确认它在
"已知可用"的情形下确实为真。

保留下来的是 D（`get_operation("*")` + sleep verifier），它才是这个问题的正解：
不预判，而是让卡住的操作可发现、可收敛。

### D. 卡住的操作会掉出 history，再也拿不到 id（**已修**）

`sleep` / `save_game` / `load_game` 等由 `RecordingDispatchGuard` 处理的操作
**不出现在 `get_action_history`**。于是：
- 一旦某个 `sleep` 卡在非终态，`FindPendingFor` 会挡住后续所有 `sleep`；
- 而 agent 拿不到它的 `action_id`，无法用 `get_operation` 触发收敛 → **会话彻底不可恢复**。

真实原因不是"守卫不写历史"，而是 **history 是有界的（100 条）**：一个营业日有上百次
写操作，卡住的 `sleep` 很快被挤出去，`action_id` 就再也找不回来了。

修法：`get_operation("*")` 返回**非终态操作**列表（最多 20 条，只读）。
拿到 id 后连续 `get_operation(id)` 两三次，`sleep` 的 verifier 就会把它收敛成
`completed` 或 `failed("day_not_changed")`，阻塞随之解除。
实现：`ActionGate.ListNonTerminal` + `ActionRouter.ListOperationsJson`。
测试：`R4_F1_14_Non_Terminal_Operations_Are_Listable`。

## 3.4 首周完成（2026-11-19 02:52，槽 53）

**第一次由桥独立跑完 7 天并付掉租金。**

| 项 | 值 |
|---|---|
| 槽 / 局 | 53 |
| Day 1 起始现金 | 700 |
| Day 8 现金 | 88 |
| 租金 | **已付 200**，`rent_paid_total: 200`、`rent_weeks_paid: 1`、`rent_weeks_missed: 0` |
| 支付凭据 | `last_payment.id = rent_1_200`，293 → 93，`source: "dialogue_choice"`，`detection_complete: true`，`paid_at_rev: 417` |
| 下周租金 | 209（涨了），`days_left: 6` |
| 总成交 | 约 45 笔（买/卖各半），逐日 8/16/8/0/0/0/0 |

第 7 天的支付走的是房东对话：桥在 `choose_dialogue` 里选了付钱那一项，
现金扣 200、`rent_days_left` 从 0 变成 7（下一周），并落了一条完整凭据。

### 还剩下的交易质量问题

Day 4 之后几乎每笔都 `no_negotiation` / `item_not_staged`：脚本的"把商品拖到
议价台"两步法在**同一顾客连续多笔**时不可靠——`staged item` 不会因为
`move main → back` 而切换。这是脚本层问题，不是桥的判定问题（拒绝是正确且安全的）。
下一步建议做一等公民 `open_negotiation(item_id)`，或让脚本在每次交易前先读
`negotiated_item` 的名称做校验。

## 4. 复现

```bash
cd PawnMind
./test.sh R4_F1        # 13 tests
./test.sh R4_TRADE     # 3 tests
./test.sh              # 782 passed / 0 failed
```

真机侧的完整命令序列见 `skills/pawnmind-first-week/SKILL.md` §4–§5。
