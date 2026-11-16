# PawnMind 首周经营 Skill（pawnmind-first-week）

> **状态：`OFFLINE_DRAFT`（离线草案，2026-11-17 Nightly Batch A / E 模块）。**
> 本 Skill 依据仓库内已实现或本期冻结的 MCP 协议编写，**不包含任何真机验证结论**。
> 真机（L3）证据：`NOT_RUN`（无游戏进程、未读写存档、未安装 DLL）。
> 通关率 / 效率收益：`NOT_MEASURED`，本文件不给出任何跑分。

- 适用游戏：*Probably Stolen* **0.46D**（`VERSION 100-Rev13`；以 `get_state` 的版本字段与 bridge 的 MVID 门禁为准）。
- 适用工具面：`mcp_server.py --tools full`（起草时 114 个工具）或 `--tools recording`（104 个玩家等价工具）；
  版本以 `tool_policy.json` 的 `policy_sha256` 与 `docs/audit/tools-inventory.json` 为准，不写死数量。
  本 Skill **只用首周最小闭环里已注册的工具**，未注册的工具名一律不要调用。
- 适用模式：`run_mode=play`（进入游戏后）或显式 `test`。`bootstrap` 只用于主菜单只读探测。
- 前置：`play_allowlist` 目前为空（无 live evidence），所以**真机 `play` 下写操作仍会被拒**——这是预期，不要改配置绕过。

---

## 0. 一分钟流程（每天照这个循环）

1. `get_state()` 观察：`in_game`、`day`、`shop_state`、`cash`、`rent`、`can_end_day`、`dialogue`、`prompt`。
2. 有原生弹窗 → `get_pending_prompt()` → 读懂文字后再 `answer_prompt()`。
3. `shop_state == "MORNING"` → `open_shop()`。
4. 顾客/议价 → `list_goods()` / `confirm_trade()`；对话 → `advance_dialogue()` / `choose_dialogue()`。
5. 顾客处理完、没有待接待 → `end_day()`；`readback.day_advanced` 必须为 `false`（关店≠换天）。
6. `sleep()`（必须在 `end_day` 之后）→ 用 `wait_for_events()` 或 `get_state()` 等真实 `day` 变化，
   再 `get_day_report()` 读晨报。换天与落盘要分开判断。
7. 第一周结束（`rent.days_left == 0`、房东 `client.intent == "RENT"`）→ 走对话付租，
   **只认外部结算回执**（见 §8）。
8. 出现任何 `pending`/`uncertain` → `get_operation()` / `wait_operation()` 查询，**禁止用新的 action_id 盲重试**。

任何一步失败：先按 §9 分类，再按 §10 停止条件决定是否停机。**不要用模型自述代替状态证据。**

---

## 1. 启动与进入游戏

### 1.1 会话开场（主菜单 / 已在游戏中都要先做）

1. `ping()`：确认 bridge 存活（`pong:true`）。失败 → `bridge_unavailable`，停机。
2. `get_state()`：拿 `rev`、`in_game`、`shop_state`、`run_summary`。
3. `get_run_summary()`（可选）：快速确认 `day/cash/rent_days_left`。
4. `get_recording_save_status()`（仅录制/专用槽流程）：确认 `recording_mode`、绑定槽位与 `bound/slot_matches/run_matches`。
   **不要尝试任何其他存档槽**；不读、不写玩家存档。

### 1.2 从主菜单开一局（仅限用户已授权的专用录制槽）

只用原生菜单流程，工具按顺序单步调用，每步之间回读：

1. `get_new_game_options()` → 读 `phase`、可见选项、`page`、`selected_id`。
2. `open_new_game_menu()` → 再用 `get_new_game_options()` 回读 `phase`。
3. `select_new_game_option()`：一次只点一个场景/特质/难度卡；回读 `selected_id` / `perks.selected_count` / `used_points`。
4. `new_game_continue()`：逐页推进（场景 → 特质 → 难度）。
5. `get_recording_save_status()`：确认录制已 armed 且专用槽是游戏下一个自动槽。
6. `confirm_new_game()`：**整局只调一次**。触发原生 `InitialSave()`（真实写盘），调用后回读
   `get_recording_save_status()` 与 `get_new_game_options()`，不要重试。
7. 若要读回一个已绑定的专用档：`load_recording_game()`（必须先由人把游戏退回主菜单；只读绑定槽）。
8. `get_state()` 确认 `in_game:true`、`day`、`run_summary`，然后进入 §2。

**不要**调用通用 `act()` / `press()`，不要调用 `new_game()` / `load_game()` / `save_game()`：
它们不在录制面（`--tools recording`）里，绕过菜单与槽位策略属于违规路径。

---

## 2. 状态观察（每个动作前必做）

### 2.1 读取顺序

1. `get_state()` —— 唯一权威快照（`rev` 是序列化指纹计数，不是隐藏状态/RNG 版本号）。
2. `get_legal_actions()` —— 只含 `availability="available"` 的项；返回 `{rev, legal_actions}`。
3. 需要局部细节时再读：`list_goods()`、`list_inventory()`、`get_item()`、`get_calendar()`、`get_day_report()`。
4. 一次只做**一个**写动作，动作后必须重新 `get_state()` 回读；不要连发多个写。

### 2.2 `get_state` 关键字段

| 字段 | 含义 / 用法 |
|---|---|
| `in_game` | `false` = 主菜单；此状态下 `client/rent/run_summary` 为 `null` |
| `day` / `starting_day` | 当前天；首周目标通常到 `day == 8` |
| `shop_state` | `MORNING` → `open_shop()`；`OPEN` → 正常经营；`CLOSED` → 只能 `sleep()` |
| `cash` | 现金；任何交易后与回执/`list_goods` 对账 |
| `rent` | 见 §8：`days_left`、`due`、`can_pay`、`rent_paid_total`、`rent_weeks_paid/missed`、`last_payment` |
| `legal_actions` | **已证实可做**的动作（`availability="available"`） |
| `candidate_actions` | **候选**动作：`availability="unknown"`、`requires="native_guard"`，需要实际调用时由原生守卫确认；**不是**合法证明 |
| `client` | 当前顾客：`name`、`intent`（如 `RENT`）、`is_wanted`、`reported`、`budget` |
| `prompt` | 非 `null` = 原生确认框占用输入；先 `get_pending_prompt()` |
| `dialogue` | `available/choice/ended/typing/choices[]`；`choices[i].enabled` 决定可选项 |
| `dialogue.readable == false` | 读不到对话：**不要**假设内容，停机或等下一拍 |
| `waiting_customers` | 排队顾客数；影响 `end_day()` 是否会被拒 |
| `can_end_day` | 当前是否允许关店 |
| `report` / `calendar_next` | 晨报与日历投影；`readable:false` 时不要猜 |

### 2.3 `legal_actions`（available）与 `candidate_actions`（unknown）的区别

- `legal_actions` 里每一项形如
  `{"action": "confirm_trade", "args": {"item_id": 101, "side": "buy"}, "availability": "available"}`：
  **可以直接调用**（仍要带正确 `rev`，仍可能被更细的原生守卫拒绝）。
- `candidate_actions` 里每一项形如
  `{"action": "bargain", "args": {"direction": "sell_discount"}, "availability": "unknown", "requires": "native_guard"}`：
  这是“可能需要原生确认”的候选，**不是**可执行承诺。例如谈价面板打开时四个方向都可能出现，
  但最终仍可能返回 `unavailable`。
- 规则：**只看 `legal_actions` 做计划；`candidate_actions` 只能作为“可以尝试”的线索**，
  尝试失败（`unavailable`/`error`）就接受拒绝并重新读状态，不要把它当 bug 硬试。
- 旧版“举报可用性”字段已从协议删除（它依赖带副作用的原生谓词）；判断是否可举报只用纯字段
  `client.reported` / `client.is_wanted`，再以 `report_client()` 的实际返回为准
  （`already_reported` / `cannot_report` / `is_wanted`）。

### 2.4 `rev` 纪律

- 每个写动作都必须带 `get_state().rev`（或刚读到的最新 `rev`）。
- `stale_rev` = 世界已变、请求**未派发**：重新 `get_state()`，重新决策，再发一次；不要沿用旧参数硬发。
- 不要自己创建/复用 `action_id`；可信调用层在发出前生成并持久记录 `run_id + sequence + action_id + op + args + rev`。

---

## 3. 操作协议：两轴状态（写动作必读）

每个写动作的响应新增两个轴：

- `operation_id`：本次操作 id，用于回查。
- `execution_status ∈ prepared | running | completed | rejected | failed | uncertain`
- `delivery_status ∈ pending_delivery | delivered | undelivered`

规则：

1. **`ok:true` 只代表 `completed`**。`ok:false` 不一定是失败：
   - `ok:false + status/execution_status:"pending"`：操作仍在进行 → `get_operation()` 或 `wait_operation()`。
   - `ok:false + execution_status:"uncertain"`：可能已执行，证据不足 → 只能查 operation，或复用**原** `action_id`。
2. `pending/uncertain/completed` **只能**来自 bridge 的 operation 两轴状态（写响应或 `get_operation()`/`wait_operation()`）。
   模型自己的话、自然语言总结、`claim_text`、`action_history` 都不是证据。
3. `uncertain` 操作**不得用新的 `action_id` 重试**：同 op+args 必须复用原 `action_id`，
   或先用 `get_operation()` / `wait_operation()` 查清状态。**盲重试 = 允许双扣款/双成交，是硬违规。**
4. `pending` 操作存在时，不允许发出冲突写动作（不同 op 或不同 `rev`）；
   读（`get_state()` 等）与同一 `action_id` 的幂等重放允许。
5. `get_operation(operation_id)`：只读查询，返回两轴与证据；**不修改游戏**。
   `wait_operation(operation_id, timeout_ms)`：有界等待。
   - 超时返回 `ok:false + status:"pending" + operation_id` + 下一步建议（实现另带 `timed_out:true`），
     **不是成功**。
   - `known:false` + `error:"unknown_operation"`：进程重启/记录丢失，禁止自动重放。
   - 不要自己写循环轮询主线程；用这两个工具，遵守其 `next` 建议。
6. `error:"unknown_operation"`：bridge 进程重启后内存记录已丢失，**禁止自动重放**；
   先重新 `get_state()` 核对世界，再由上层决定是否重新发起（新意图、新 `action_id`，并记录人工判断）。
7. 进程重启不承诺跨进程 exactly-once；durable intent 只证明“曾发出过”，不证明“执行到哪一步”。

---

## 4. 开店与顾客

1. `get_state()` 确认 `shop_state == "MORNING"` 且无 `prompt`。
2. `open_shop()`（带 `rev`）。被拒（`shutter_busy` / 非 MORNING / 动画锁）时**不要连点**：
   等一拍（`wait_for_events()` 或重新 `get_state()`）再决定。
3. 顾客到柜台：`get_state()` 的 `client` / `client_items`。
4. 顾客在等但需要下一位 → `call_next_customer()`（`can_call_next` 为真时才试）。
5. 收尾：没有顾客/对话后，`end_day()` 之前必须再看一眼 `waiting_customers` 与 `prompt`。

---

## 5. 普通交易（首周核心收入）

1. **看货**：`list_goods(scope="all")` → 每项 `id/identifier/name/value/owned/sellable`。
2. **对账基线**：`get_state()` 记 `cash`、`inventory_count`、`back_counter_count`。
3. **买**：`confirm_trade(item_id=<id>, side="buy", rev=<state.rev>)`。
4. **卖**：需要先确认物品在自己背包且可卖：`list_inventory(container="main")` 拿 `item_id`，
   再 `confirm_trade(item_id=<id>, side="sell", rev=<新 rev>)`。
5. **回读验证**：再 `get_state()`，核对
   - `cash` 变化 == 可见价格（买减、卖加）；
   - `inventory_count` / `back_counter_count` 变化符合方向；
   - 必要时 `list_inventory()` 找到同一 `identifier`。
6. 以物易物可能**零现金变化**：不要只凭 cash 判断成交，必须同时看物品归属/计数变化。
7. 交易被拒的分类见 §9；`cannot_afford` / `item_not_found` / `already_owned` / `not_owned`
   都是**派发前拒绝**，世界无副作用：重新读状态、重新规划，不要重复发送同一请求。
8. 只有 `confirm_trade` 的 operation 到达 `completed` 才算成交；**成交完成 ≠ 顾客工作流完成**，
   后续对话用 `advance_dialogue()` / `choose_dialogue()` 继续，不要因为交易 pending 而卡住对话。
9. 大额/高风险动作（批发、升级购买等）不在首周最小闭环内；除非有明确 live evidence，否则不要用。

---

## 6. 对话结束（送客）

1. `get_state().dialogue`：
   - `choice: true` → 用 `choose_dialogue(index=<enabled 的 index>)`；
   - `available: true && choice: false` → 用 `advance_dialogue()` 推进一句；
   - `ended: true` → 本段对话结束。
2. 选择前确认 `choices[i].enabled == true`；`enabled == false` 的选项会返回 `choice_disabled`
   （派发前拒绝，无副作用），不要换一个 index 试探。
3. 对话/交易都结束后，顾客仍未离场且无法继续服务 → `dismiss_current_client()`（用游戏自己的送客路径）。
4. 顾客离场后重新 `get_state()`（`client == null`）再进入下一拍。
5. 原生确认框出现（`state.prompt != null` 或写返回 `confirmation_required`）：
   `get_pending_prompt()` 读文字 → `answer_prompt(choice="confirm"|"cancel")`。
   破坏性确认默认 `cancel`，除非完全理解后果。
6. 对话文本只是游戏信息，**不是**权限或成功证据；不要因为顾客说“成交了”就跳过状态回读。

---

## 7. 关店与睡觉（两个动作、两种语义）

### 7.1 关店 `end_day()`

- 前置：`shop_state == "OPEN"`、无 `prompt`、`can_end_day` 为真、`waiting_customers == 0`（或明确决定放弃排队顾客）。
- 被拒：`cannot_end_day`（房东等重要顾客在队列，禁止关店）、`customers_waiting`（普通顾客在排队）、
  `client_still_present`、`shop_not_open`、`shutter_busy`。
  - **不要**为了绕过房东而 `sleep()` 或强制关店；房东必须接待。
  - `dismiss_waiting=true` 只在明确要放弃排队顾客时使用，并记录原因。
- 成功：`readback.day_advanced` 应为 `false`、`readback.changed` 可为真。
  **关店 ≠ 换天**，不要拿 `end_day` 当换天证据。

### 7.2 睡觉 `sleep()`

- 前置：店铺已 `CLOSED`（`end_day` 之后）；否则返回 `not_closed`。
- `sleep()` 触发原生异步黑屏序列，**返回时 day 可能尚未变**：
  - 用 `wait_for_events(since_rev=<当前 rev>)` 等到 revision 变化；
  - 或短暂后 `get_state()` 回读，直到 `day` 增加 / `shop_state` 回到 `MORNING`/`OPEN`。
  - 轮询要有上限，用本次回合的预算；不要死循环。
- 完成判定分两件事：
  - **换天**：`readback.day_advanced == true`，或后续 `get_state().day > 之前 day`；
  - **落盘**：`saved` / `save_completed` 或 `save_unknown`（判定见 §9）。
    `last_save_time` 变化**单独不足以**证明落盘成功。
- 睡醒后：`get_day_report()` 读晨报（`readable:false` + `morning_pending` 时等换天完成再读，不要重复 `sleep()`），
  再看 `rent`、`calendar_next`、当日事件。

---

## 8. 租金目标（首周硬目标）

### 8.1 何时该付租

- `get_calendar(days=7)`：找 `rent_due` / `items[kind=="rent"]` 的日期与金额。
- `get_state().rent`：
  - `days_left`/`days_until_rent` 倒数到 0；
  - 房东到访：`client.intent == "RENT"` 且 `dialogue.available/choice`；
  - `can_pay` 为真（`cash >= due`）才可能付；付租选项由 `choose_dialogue()` 的可见 `enabled` 决定。
- 现金不足：先正常经营赚钱；**不要**调用任何“加钱/改钱/跳过租金”的路径，也不要用存档/读档刷钱。
- 房东在队列时：`end_day()` / `sleep()` 都会被拒（`cannot_end_day`）。必须先把房东接待完。

### 8.2 付租动作

1. `get_state()` 找到房东对话的 `dialogue.choices[]`，按**可见文本**识别付租选项（例如含 "Rent"/"租金" 的项；
   index 不要写死，随本地化/顾客状态而变）。
2. 确认该选项 `enabled == true`；若 `false`，说明 `can_pay` 不足，先赚钱，**不要**试探。
3. `choose_dialogue(index=<该选项 index>, rev=<state.rev>)`。
4. 若返回 `choice_disabled` / `no_dialogue`：派发前拒绝，世界无副作用；重新读状态，不要换 index 猜。

### 8.3 租金成功的唯一证据（**只认外部可验证的结算证据**）

租金成功必须至少满足以下之一，且与之前状态对账：

- **写响应回执**：`choose_dialogue` 返回 `last_rent_payment`（或 `rent_receipt` / `settlement_event`），
  其中包含可核对的 `id`、`amount`、`cash_before/cash_after`（或 `cash_delta`）、`rent_days_left_after` 等字段；
- **状态证据**：后续 `get_state()` 中 `rent.last_payment` 非空，或
  `rent.rent_paid_total` / `rent.rent_weeks_paid` 相对付租前**增加**。

对账要求：

- `amount == rent.due`（允许临时租金/分项时以可见 `rent_value` 为准并记录）；
- `cash_after == cash_before - amount`（或 `cash_delta == -amount`）；
- `rent_days_left_after > 0`（倒计时重置）；
- `rent_weeks_missed` **不得**增加（漏租周不能被当成已付）；
- 同一笔付款只能产生一条回执；重复 `choose_dialogue` 必须返回 `no_dialogue`/拒绝，不能重复扣款。

**模型自述“租金已付”“第一周完成了”不算证据**；`action_history` 也不是权威（C# 侧内存环形缓冲）。
没有回执/状态证据时：标记 `RENT_EVIDENCE_MISSING` → 停机核对，不要继续按成功推进。

---

## 9. 错误分类（先分类，再决定是否重试）

### 9.1 读/通用错误

| 错误码 | 含义 | 处理 |
|---|---|---|
| `bridge_unavailable` / 连接失败 | bridge 不可达 | 停机；检查进程/配置，不要重发写 |
| `unauthorized` | token/鉴权失败 | 停机；不重试 |
| `policy_mismatch` | MCP 与游戏内策略哈希不一致 | 停机；重启匹配构建，不要继续写 |
| `version_locked` | 游戏版本/MVID 不匹配写门禁 | 停机；记录 `NOT_RUN` |
| `stale_rev` | rev 已过期、**未派发** | 重新 `get_state()` → 重新决策 → 单次重试 |
| `prompt_pending` / `confirmation_required` | 原生弹窗占用输入 | `get_pending_prompt()` → `answer_prompt()` |
| `no_dialogue` / `dialogue_unavailable` | 当前没有可读对话 | 重新 `get_state()`；不要猜对话内容 |
| `invalid_argument` | 参数校验失败、未派发 | 修正参数（字段见返回 `field`），不要原样重试 |

### 9.2 首周业务拒绝（派发前，世界无副作用）

`cannot_afford`、`item_not_found`、`already_owned`、`not_owned`、`choice_disabled`、
`cannot_end_day`、`customers_waiting`、`not_closed`、`shop_not_open`、`shutter_busy`、
`client_still_present`、`window_readback_mismatch`、`tutorial_hints_readback_mismatch`、
`recording_locked`、`recording_slot_mismatch`、`recording_run_mismatch`。

处理：**接受拒绝**，按错误语义重新读状态、重新规划；不要重复发送同一请求来“确认一下”。
其中 `*_readback_mismatch` 明确要求停机，不重试。

### 9.3 异步/结果未知（最危险）

| 情况 | 处理 |
|---|---|
| `ok:false + execution_status:"pending"` | `get_operation()` / `wait_operation()` 等待；pending 期间只读 |
| `execution_status:"uncertain"` | 只查询 operation；**禁止新 action_id**；同 op+args 复用原 `action_id` |
| `outcome_unknown` | 视为 uncertain；查 operation + 回读世界，人工/上层判定，不自动重放 |
| `unknown_operation`（`known:false`） | 记录“进程重启/记录丢失”；重新 `get_state()` 核对世界；不自动重放 |
| `delivery_status:"undelivered"` | 响应丢失但执行事实可能已成立：用原 `action_id` 重查/重放拿回原结果，不要新建 id |
| `wait_operation` 超时（`ok:false + status:"pending"`） | 不是失败也不是成功；继续有界等待或停机记录 |

### 9.4 存档相关

`saved` 为真且带文件证据（如 `save_file`/`save_path`/`file_written`/`bytes_written`）才算落盘成功；
只有 `last_save_time` 变化 → `save_unknown`，报告写“未证实落盘”，不要写成功。

---

## 10. 停止条件（命中任一：立即停机并记录证据）

**硬停（必须人工/上层介入）**

1. `bridge_unavailable`、`unauthorized`、`policy_mismatch`、`version_locked`。
2. `outcome_unknown` / `execution_status:"uncertain"` 且 `get_operation()` 查不到结果（`unknown_operation`）。
3. `uncertain` 操作被要求用新的 `action_id` 重试时（协议禁止，直接停）。
4. 租金回执缺失、金额/现金对账不一致、`rent_weeks_missed` 增加却被描述为已付。
5. 现金/库存出现无法用可见交易解释的变化（疑似重复扣款/双成交）。
6. 原生弹窗文字与预期不符、或 `answer_prompt()` 会做出不可逆破坏性操作。
7. 连续 3 次 `stale_rev`，或连续 3 个回合没有任何状态进展。
8. `wait_operation()`/等待换天在预算内仍未结案。
9. 尝试访问非专用存档、绕过菜单、`act()`/`press()` 任意 UI、debug/test-only 路径。
10. 本回合动作数/等待次数/时间预算耗尽（预算在开局由用户确认）。

**软停（记录后可由上层决定继续）**

- `end_day()` 被房东拒绝 → 先接待房东；接待不了则停。
- 顾客无法服务且 `dismiss_current_client()` 也被拒 → 停（不硬点 UI）。
- `get_clientele()` 等非纯读情报工具：strict/录像模式不应使用（读取可能消耗 RNG）。

---

## 11. 证据与记录（每个回合）

可信调用层（不是模型）必须记录：

- `run_id + sequence + action_id + op + args + rev`（写动作发出前持久化）；
- 响应：`operation_id`、`execution_status`、`delivery_status`、`error`、关键 `readback`/`receipt`；
- 动作前后关键状态摘要：`day`、`shop_state`、`cash`、`inventory_count`、`back_counter_count`、
  `rent.days_left`、`rent.rent_paid_total`、`rent.last_payment.id`；
- 关键证据：租金回执、`readback.day_advanced`、`saved`/`save_unknown`；
- 停止原因与最后一条成功证据。

这些记录可交给 `agent/trace_validator.py` 离线校验（见该模块 docstring 与
`docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md`）。**trace 校验 PASS 只说明协议自洽，不等于真机成功。**

---

## 12. 最小工具集（首周闭环）

- 观察：`ping()`、`get_state()`、`get_run_summary()`、`get_legal_actions()`、
  `list_goods()`、`list_inventory()`、`get_item()`、`get_calendar()`、`get_day_report()`。
- 操作：`open_shop()`、`call_next_customer()`、`confirm_trade()`、
  `advance_dialogue()`、`choose_dialogue()`、`dismiss_current_client()`、
  `get_pending_prompt()`、`answer_prompt()`、`close_windows()`。
- 日终：`end_day()`、`sleep()`、`wait_for_events()`。
- 操作协议：`get_operation()`、`wait_operation()`（D 协议新增）。
- 开局（仅专用槽）：`get_new_game_options()`、`open_new_game_menu()`、
  `select_new_game_option()`、`new_game_continue()`、`confirm_new_game()`、
  `get_recording_save_status()`。

不在首周最小闭环、未经 live evidence 的工具（机器、酿酒、彩票、远征、bargain 等）不要主动使用。

---

## 13. schema 依赖待核对（与 Lead 的 D/B 实现同步）

本 Skill 按 **2026-11-17 冻结协议**编写；实现落地时若字段不同，以 Lead 同步为准，本文件相应更新：

1. `get_operation()` / `wait_operation()` 是否已注册（起草时 HEAD 尚未注册；D 模块落地后确认实际注册名与
   响应字段；工具清单 lint 的 `PENDING_CONTRACT_TOOLS` 例外届时即可删除）。
2. 写响应是否统一带 `operation_id` + `execution_status` + `delivery_status`；`ok:true` 是否严格只在 `completed`。
3. `wait_operation` 超时返回是否确为 `ok:false + status:"pending" + operation_id` + 下一步建议
   （并行实现测试中另带 `timed_out:true`）；未知 operation 是否为 `known:false` + `error:"unknown_operation"`。
4. `get_state.legal_actions` 是否已只保留 `availability="available"`；
   `candidate_actions` 是否带 `availability="unknown"` + `requires="native_guard"`。
5. 举报相关字段是否已切到纯字段 `reported`/`is_wanted`（旧的可举报性字段应已删除）。
6. 租金回执字段名（`last_rent_payment` / `rent.last_payment`）与字段集是否与本文一致；
   若改为 `settlement_event`/`rent_receipt`，更新 §8.3 与 trace validator 的别名表。
7. 换天/落盘字段：`readback.day_advanced`、`saved`、`save_completed`/`save_unknown`
   以及文件证据字段的实际名称。
8. 工具数（full/recording）与 `policy_sha256` 变化；本文不写死数量作为判据。
