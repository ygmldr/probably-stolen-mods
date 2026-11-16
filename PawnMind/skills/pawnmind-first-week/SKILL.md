# PawnMind 首周经营 Skill（pawnmind-first-week）

> **状态：`OFFLINE_DRAFT`（离线草案，2026-11-17 Nightly Batch A / E 模块，D+C 合入后修订）。**
> 本 Skill 依据仓库已实现并冻结的 MCP 协议编写，**不包含任何真机验证结论**。
> 真机（L3）证据：`NOT_RUN`（无游戏进程、未读写存档、未安装 DLL）。
> 通关率 / 效率收益：`NOT_MEASURED`，本文件不给出任何跑分。

- 适用游戏：*Probably Stolen* **0.46D**（`VERSION 100-Rev13`；以 `get_state` 的版本字段与 bridge 的 MVID 门禁为准）。
- 适用工具面：`mcp_server.py --tools full`（**116** 个工具）或 `--tools recording`（**106** 个玩家等价工具）；
  版本以 `tool_policy.json` 的 `policy_sha256`（当前 **`e0da5a24…`**）与 `docs/audit/tools-inventory.json` 为准。
  本 Skill **只用首周最小闭环里已注册的工具**，未注册的工具名一律不要调用。
- 适用模式：`run_mode=play`（进入游戏后）或显式 `test`。`bootstrap` 只用于主菜单只读探测。
- 前置：`play_allowlist` 目前为空（无 live evidence），所以**真机 `play` 下写操作仍会被拒**——这是预期，不要改配置绕过。

---

## 0. 一分钟流程（每天照这个循环）

1. `get_state()` 观察：`in_game`、`day`、`shop_state`、`cash`、`rent`、`can_end_day`、`dialogue`、`prompt`。
2. 有原生弹窗 → `get_pending_prompt()` → 读懂文字后再 `answer_prompt()`。
3. `shop_state == "MORNING"` → `open_shop()`。
4. 顾客/议价 → `list_goods()` / `confirm_trade()`；对话 → `advance_dialogue()` / `choose_dialogue()`。
5. 顾客处理完、没有待接待 → `end_day()`；**只有 `shop_closed_after == true` 才算关店完成**（关店≠换天）。
6. `sleep()`（必须在 `end_day` 之后）→ 用 `wait_for_events()` 或 `get_state()` 等真实 `day` 变化；
   `day_changed` 与 `save_evidence` 分开判断，再 `get_day_report()` 读晨报。
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
2. `get_legal_actions()` —— 返回 `{rev, legal_actions, candidate_actions}`；`legal_actions` 只含
   `availability="available"` 的项。
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
| `client` | 当前顾客：`name`、`intent`（如 `RENT`、`BARTER`）、`is_wanted`、`reported`、`budget` |
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

## 3. 操作协议：两轴状态（写动作必读，D 模块已冻结）

每个写动作的响应都带以下字段：

| 字段 | 取值 / 含义 |
|---|---|
| `operation_id` | 本次操作 id（即 `action_id`），用于 `get_operation()` / `wait_operation()` 回查 |
| `execution_status` | `prepared \| running \| completed \| rejected \| failed \| uncertain` |
| `delivery_status` | `pending_delivery \| delivered \| undelivered`（只描述“调用方有没有拿到响应”） |
| `dispatched` | 是否真的派发了原生调用；`false` = 派发前被拒（含 `execution_status:"rejected"`） |

规则：

1. **`ok:true` 只代表 `execution_status:"completed"`**。
   `ok:false` 不一定是失败：`running`/`uncertain` 必须查 operation。
2. 重发**同一 `action_id`** 时，若之前已完成，返回原完成证据 + `delivery_status:"undelivered"` +
   `redelivered:true`（不会重新执行，也**不会**把 completed 改写成 unknown）。
3. 同一个 op 存在非终态操作时，再用**新 `action_id`** 发起同一 op 会返回
   `error:"operation_pending"` + `blocking_operation_id` + `next`（不重复派发）；
   必须先查 `blocking_operation_id`。
4. `get_operation(operation_id)` 返回：
   `known` / `ok` / `operation_id` / `op` / `execution_status` / `delivery_status` / `dispatched` /
   `error` / `rev` / `result_rev` / `next`。
   - `ok:true` 仅当 `execution_status:"completed"`；非终态时 `next` 给出下一步建议。
   - 未知 id：`ok:false` + `known:false` + `error:"unknown_operation"`；
     若期间发生 new game / 读档 / 进程重启，还会带 `process_restarted:true`，**禁止自动重放**。
5. `wait_operation(operation_id, timeout_ms)`：Python 侧有界轮询（≤10s），不阻塞 Unity 主线程。
   - 终态返回终态 payload；
   - 超时返回 `ok:false` + `status:"pending"` + `execution_status` + `operation_id` + `next`
     （另带 `timed_out:true`），**绝不是假成功**。
6. `pending/uncertain/completed` **只能**来自 bridge 的 operation 两轴状态（写响应或
   `get_operation()`/`wait_operation()`）。模型自己的话、自然语言总结、`claim_text`、
   `action_history` 都不是证据。
7. `uncertain` 操作**不得用新的 `action_id` 重试**：同 op+args 必须复用原 `action_id`，
   或先用 `get_operation()` / `wait_operation()` 查清状态。**盲重试 = 允许双扣款/双成交，是硬违规。**
8. `pending` 操作存在时，不允许发出冲突写动作（不同 op 或不同 `rev`）；
   读（`get_state()` 等）与同一 `action_id` 的幂等重放允许。
9. `delivery_status:"undelivered"`：响应丢失但执行事实可能已成立——用原 `action_id` 重查/重放拿回原证据
   （会带 `redelivered:true`），不要新建 id。
10. 进程重启不承诺跨进程 exactly-once；durable intent 只证明“曾发出过”，不证明“执行到哪一步”。

---

## 4. 开店与顾客

1. `get_state()` 确认 `shop_state == "MORNING"` 且无 `prompt`。
2. `open_shop()`（带 `rev`）。被拒（`shutter_busy` / `busy_arrest` / `busy_shootout` /
   `shop_not_morning` / `cannot_open_shop` / `open_shop_failed`）时**不要连点**：
   等一拍（`wait_for_events()` 或重新 `get_state()`）再决定。
3. 顾客到柜台：`get_state()` 的 `client` / `client_items`。
4. 顾客在等但需要下一位 → `call_next_customer()`（`can_call_next` 为真时才试）。
5. 收尾：没有顾客/对话后，`end_day()` 之前必须再看一眼 `waiting_customers` 与 `prompt`。

---

## 5. 普通交易（首周核心收入，C 模块已冻结证据协议）

### 5.1 流程

1. **看货**：`list_goods(scope="all")` → 每项 `id/identifier/name/value/owned/sellable`。
2. **对账基线**：`get_state()` 记 `cash`、`inventory_count`、`back_counter_count`。
3. **买**：`confirm_trade(item_id=<id>, side="buy", rev=<state.rev>)`。
4. **卖**：先 `list_inventory(container="main")` 拿 `item_id`，再
   `confirm_trade(item_id=<id>, side="sell", rev=<新 rev>)`。
5. **回读验证**：再 `get_state()`，核对现金/库存/柜面变化；以物易物可能**零现金变化**。
6. 只有 `execution_status:"completed"` 才算成交；**成交完成 ≠ 顾客工作流完成**，
   后续对话用 `advance_dialogue()` / `choose_dialogue()` 继续，交易 completed/uncertain 都不阻塞对话。

### 5.2 `confirm_trade` 成功证据（唯一可信来源）

`completed` 必须同时具备：

- `business_complete: true`（**同一顾客**的业务完成）
- `client_unchanged: true`（顾客身份未变，不能用别的顾客状态背书）

响应同时带以下证据字段（用于审计与对账）：

| 字段 | 含义 |
|---|---|
| `dispatched` | 成功时为 `true`；派发前拒绝为 `false`（派发 0 次） |
| `item_id` / `side` | 请求的物品与方向 |
| `staged_item_id` | 谈判桌上被接受的物品 |
| `client_intent` | 当前顾客意图（BUY/SELL/BARTER/INFORMATION_DEALER/GUNSMITH…） |
| `items_transferred` | 物品归属/桌面是否真的转移（辅助证据） |
| `cash_before` / `cash_after` | 整数现金前后值；`cash_changed` 表示是否变化（**不能**用现金变化当唯一成功判据） |
| `evidence` | 明细：`called`、`error`、`business_complete`、`client_unchanged`、`items_transferred`、`cash_before`、`cash_after`、`staged_item_id_after` |

### 5.3 拒绝与“静默失败”分类

派发前拒绝（`dispatched:false`、`execution_status:"rejected"`，世界无副作用）：
`trade_locked`（B10 被逮捕/枪战锁定）、`intent_undefined`、`business_complete`（B11 业务已完成）、
`item_not_staged`（B12 物品不在桌上）、`barter_not_acceptable`、`not_introduced`（B13）、
`cannot_afford`（B14）、`gun_order_not_accepted`（B15）、`cannot_buy_from_table`、
`client_refuses_item`（B16）、`bad_side`、`item_not_found`、`no_negotiation`、`not_owned`、
`already_owned`、`trade_read_failed`。
处理：接受拒绝，重新读状态、重新规划；不要重复发送同一请求“确认一下”。

原生 accept 调用返回但**没有可信完成证据**（静默 return / 异常 / 业务未完成）：
`error:"outcome_unverified"` + `execution_status:"uncertain"` + `dispatched:true`。
- 这是 `uncertain`，**不是**失败也不是成功；
- 只能查 `get_operation()` / `wait_operation()`，或复用原 `action_id`；
- **禁止新 `action_id` 重试**（可能已成交，双成交 = 硬违规）；
- 若 `client_changed`：成交可能发生在另一个顾客实例上，同样只能 uncertain 处理。

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

## 7. 关店与睡觉（两个动作、两种语义，D3 已冻结）

### 7.1 关店 `end_day()`

- 前置：`shop_state == "OPEN"`、无 `prompt`、`can_end_day` 为真、`waiting_customers == 0`（或明确决定放弃排队顾客）。
- 被拒：`cannot_end_day`（房东等重要顾客在队列，禁止关店）、`customers_waiting`（普通顾客在排队）、
  `client_still_present`、`shop_not_open`、`shutter_busy`。
  - **不要**为了绕过房东而 `sleep()` 或强制关店；房东必须接待。
  - `dismiss_waiting=true` 只在明确要放弃排队顾客时使用，并记录原因。
- **完成条件**：`execution_status:"completed"` **当且仅当**响应中 `shop_closed_after == true`。
  若动画仍在跑、回读仍 `OPEN`：返回 `ok:false` + `execution_status:"running"` + `pending:true` +
  `error:"shop_still_open"` + 下一步建议 → 查 `get_operation()` / `wait_operation()`，**不要重新派发**。
- 关店 ≠ 换天：`readback.day_advanced` 在 `end_day` 时应为 `false`。

### 7.2 睡觉 `sleep()`

- 前置：店铺已 `CLOSED`（`end_day()` 之后）；否则返回 `not_closed`。
- `sleep()` 触发原生异步黑屏序列，**返回时 day 可能尚未变**：
  - 用 `wait_for_events(since_rev=<当前 rev>)` 等到 revision 变化；
  - 或短暂后 `get_state()` 回读，直到 `day` 增加 / `shop_state` 回到 `MORNING`/`OPEN`；
  - 轮询要有上限，用本次回合的预算；不要死循环。
- 完成判定分**两个独立事实**：
  - **换天**：`day_changed == true`（或 `readback.day_advanced == true`，或后续 `get_state().day` 增加）；
    `day_changed == false` 且 `execution_status:"running"` = 异步仍在进行 → `get_operation()` / `wait_operation()`，
    不是失败也不是成功。
  - **落盘**：`save_evidence` ∈ `save_completed | save_unknown`；布尔快捷字段 `save_completed` / `save_unknown`。
    - `save_completed` 必须来自 bridge 的落盘证据；
    - **`last_save_time` 变化不算落盘证据**（它只能提示“可能有保存”，不构成证明）；
    - 只有时间/无证据时如实标 `save_unknown`，报告中写“未证实落盘”，不要写成功。
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
`recording_locked`、`recording_slot_mismatch`、`recording_run_mismatch`；
交易专用：`trade_locked`、`intent_undefined`、`business_complete`、`item_not_staged`、
`barter_not_acceptable`、`not_introduced`、`gun_order_not_accepted`、`cannot_buy_from_table`、
`client_refuses_item`、`bad_side`、`no_negotiation`、`trade_read_failed`。

处理：**接受拒绝**，按错误语义重新读状态、重新规划；不要重复发送同一请求来“确认一下”。
其中 `*_readback_mismatch` 明确要求停机，不重试。

### 9.3 异步/结果未知（最危险）

| 情况 | 处理 |
|---|---|
| `ok:false + execution_status:"pending"/"running"` | `get_operation()` / `wait_operation()` 等待；pending 期间只读 |
| `error:"operation_pending"` + `blocking_operation_id` | 同一 op 已有非终态操作；查 blocking id，**不要**新 action_id 发起 |
| `execution_status:"uncertain"` / `error:"outcome_unverified"` | 只查询 operation；**禁止新 action_id**；同 op+args 复用原 `action_id` |
| `outcome_unknown` / `client_changed` | 视为 uncertain；查 operation + 回读世界，人工/上层判定，不自动重放 |
| `unknown_operation`（`known:false`） | 记录“进程重启/run 变化/记录丢失”（可能带 `process_restarted:true`）；重新 `get_state()` 核对世界；不自动重放 |
| `delivery_status:"undelivered"` | 响应丢失但执行事实可能已成立：用原 `action_id` 重查/重放拿回原结果（`redelivered:true`），不要新建 id |
| `wait_operation` 超时（`ok:false + status:"pending"` + `next`） | 不是失败也不是成功；继续有界等待或停机记录 |

### 9.4 存档 / 关店 / 换天

- `save_evidence:"save_completed"` = 可报告的落盘完成；`save_evidence:"save_unknown"` = 如实“未证实落盘”。
- `last_save_time` 单独变化 → 仍按 `save_unknown`，不得写成功。
- `end_day` 的 `execution_status:"completed"` 只认 `shop_closed_after:true`；
  `pending:true + error:"shop_still_open"` 是 running，不是失败也不是成功。
- `sleep` 的 `day_changed` 与落盘是两件事：换天不代表落盘成功，落盘也不代表换天。

---

## 10. 停止条件（命中任一：立即停机并记录证据）

**硬停（必须人工/上层介入）**

1. `bridge_unavailable`、`unauthorized`、`policy_mismatch`、`version_locked`。
2. `outcome_unknown` / `outcome_unverified` / `execution_status:"uncertain"` 且 `get_operation()` 查不到结果
   （`unknown_operation` / `known:false`）。
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
- 响应两轴 + 派发事实：`operation_id`、`execution_status`、`delivery_status`、`dispatched`、
  `error`、`redelivered`、`next`；
- 交易证据：`business_complete`、`client_unchanged`、`items_transferred`、`cash_before`、`cash_after`、
  `cash_changed`、`staged_item_id`、`client_intent`、
  `evidence.{called,error,business_complete,client_unchanged,items_transferred,cash_before,cash_after,staged_item_id_after}`；
- 关店/换天/落盘：`shop_closed_after`、`readback.day_advanced`、`day_changed`、`save_evidence`、
  `save_completed`、`save_unknown`；
- 动作前后关键状态摘要：`day`、`shop_state`、`cash`、`inventory_count`、`back_counter_count`、
  `rent.days_left`、`rent.rent_paid_total`、`rent.last_payment.id`；
- 租金回执、停止原因与最后一条成功证据。

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
- 操作协议（D 已注册）：`get_operation()`、`wait_operation()`。
- 开局（仅专用槽）：`get_new_game_options()`、`open_new_game_menu()`、
  `select_new_game_option()`、`new_game_continue()`、`confirm_new_game()`、
  `get_recording_save_status()`。

不在首周最小闭环、未经 live evidence 的工具（机器、酿酒、彩票、远征、bargain 等）不要主动使用。

---

## 13. schema 已核对（最终，2026-11-17 D+C 合入后）

以下字段已与 `mcp_server.py` / `src/Bridge/` / `src/Core/` 实际实现核对
（HEAD `f09314e` 的 D+C，policy sha `e0da5a24…`，工具 full=116 / recording=106）：

| 主题 | 已核对 schema |
|---|---|
| 写响应 | `operation_id`、`execution_status`、`delivery_status`、`dispatched`；`ok:true` 仅 completed；重复 ID → 原证据 + `delivery_status:"undelivered"` + `redelivered:true` |
| 操作查询 | `get_operation` 返回 `known/ok/operation_id/op/execution_status/delivery_status/dispatched/error/rev/result_rev/next`；未知 id `known:false` + `unknown_operation`（run 变化后带 `process_restarted:true`）；`wait_operation` 超时 `ok:false + status:"pending" + operation_id + execution_status + next` |
| 交易证据 | `business_complete`、`client_unchanged`、`items_transferred`、`cash_before`、`cash_after`（整数）、`cash_changed`、`staged_item_id`、`client_intent`、`evidence.{called,error,business_complete,client_unchanged,items_transferred,cash_before,cash_after,staged_item_id_after}`；拒绝时 `dispatched:false`；静默 return → `error:"outcome_unverified"` + `execution_status:"uncertain"` |
| 关店 | `execution_status:"completed"` ⇔ `shop_closed_after:true`；仍 OPEN → `ok:false + pending:true + execution_status:"running" + error:"shop_still_open"` |
| 睡觉 | `day_changed`、`save_evidence`（`save_completed`/`save_unknown`）、`save_completed`、`save_unknown`；`last_save_time` 不算落盘证据；`day_changed:false` = running/pending |
| 快照 | `legal_actions`（`availability="available"`）与 `candidate_actions`（`availability="unknown"`、`requires="native_guard"`）；举报用纯字段 `reported`/`is_wanted` |
| 工具面 | full = 116，recording = 106；`get_operation` / `wait_operation` 已注册且均在 recording 面 |

若后续再改冻结协议：本表随 Lead 的协议变更同步；**不要再引入“待核对”字段**。
