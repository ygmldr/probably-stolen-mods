# PawnMind 首周经营 Skill（pawnmind-first-week）

> **状态：`OFFLINE_DRAFT`（离线草案；2026-11-17 Nightly Batch A / E，2026-11-19 R5-A 离线修订）。**
> 本 Skill 依据仓库已实现并冻结的 MCP 协议编写，**不包含任何真机验证结论**。
> 真机（L3）证据：`NOT_RUN`（无游戏进程、未读写存档、未安装 DLL）。
> 通关率 / 效率收益：`NOT_MEASURED`，本文件不给出任何跑分。
> R5-A 离线修订范围：A1 first-run prompt vs run-change、A2 sleep 异步收敛、A3 单笔成交证据（源码级 + C#/Python 单测），
> **均未获真机验证**；L3 一律 `NOT_RUN`。轨迹校验：`agent/trace_validator.py` `VALIDATOR_VERSION = "1.3.0"`，
> 当前 trace `schema_version: 2`；不带版本的历史 trace 按 v1 旧口径校验并给 `TRACE_SCHEMA_VERSION_MISSING` warning，不能被静默改写。
> R5-B1 离线同步（2026-11-19）：新增 `stage_negotiation_item`（玩家等价暂存，**在 recording 面**），
> 工具面 full=117 / recording=107；该工具只有源码 + C#/Python 单测证据，**真机 L3 仍 `NOT_RUN`**。

- 适用游戏：*Probably Stolen* **0.46D**（`VERSION 100-Rev13`；以 `get_state` 的版本字段与 bridge 的 MVID 门禁为准）。
- 适用工具面：`mcp_server.py --tools full`（**117** 个工具）或 `--tools recording`（**107** 个玩家等价工具，
  含 R5-B1 的 `stage_negotiation_item`）；版本以 `tool_policy.json` 的 `policy_sha256`
  （R5-B1 后 **`774fbfdd7b44ef5c6c7b616f885f1b624758b862b3e750497f8a39cc26763277`**；离线实测，run 时必须实算复核）
  与 `docs/audit/tools-inventory.json` 为准。工具数只作旁证，**哈希变化才算换基准**。
  本 Skill **只用首周最小闭环里已注册的工具**，未注册的工具名一律不要调用。
- 适用模式：`run_mode=play`（进入游戏后）或显式 `test`。`bootstrap` 只用于主菜单只读探测。
- 前置：`play_allowlist` 在 R5 启动基线（2026-11-18 实测）仍为空（无 live evidence），所以**真机 `play` 下写操作仍会被拒**——这是预期，不要改配置绕过；run 时以 `tool_policy.json` 实算为准。

---

## 0. 一分钟流程（每天照这个循环）

1. `get_state()` 观察：`in_game`、`day`、`shop_state`、`cash`、`rent`、`can_end_day`、`dialogue`、`prompt`。
2. 有原生弹窗 → `get_pending_prompt()` → 读懂文字后再 `answer_prompt()`。
3. `shop_state == "MORNING"` → `open_shop()`。
4. 顾客/议价 → `list_goods()` → `stage_negotiation_item()` → `confirm_trade()`；
   对话 → `advance_dialogue()` / `choose_dialogue()`。
5. 顾客处理完、没有待接待 → `end_day()`；**只有 `shop_closed_after == true` 才算关店完成**（关店≠换天）。
6. `sleep()`（必须在 `end_day` 之后）→ 用 `wait_for_events()` / `get_state()` / `get_operation()` 等**可观测证据**：
   `day` 不变只是**尚未观测到完成（inconclusive）**，不是失败；`day_changed` 与 `save_evidence` 分开判断；
   超时无强证据时如实标 `uncertain`（`sleep_unconfirmed`），**不要**用新 `action_id` 再发一次 `sleep()`。
   再 `get_day_report()` 读晨报。
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
6. `confirm_new_game()` 有**两个语义不同的子动作**，以响应里的 `run_change` 字段区分：
   - **first-run prompt 确认（默认路径）**：菜单有 first-run prompt 时，`confirm_new_game()` 只确认提示，
     响应 `prompt_confirmed:true` + `run_change:"not_issued"`。**这不是开局、不是 run-changing 动作**，
     不触发跨局保护、也不会永久阻塞后续开局；确认后可再次 `confirm_new_game()` 真正开始新局。
     旧 bridge 轨迹可能在该响应上残留 `run_change_status:"requested"`，**以 `prompt_confirmed` / `run_change` 为准**。
   - **真正新局 start**：只有原生 `StartNewGame()` 被派发（或可能已派发）时才 `run_change:"issued"`，
     此时才进入防盲重试保护；响应可能带 `run_change_status`（`requested` → `unknown` / `confirmed`）。
     `run_change_confirmed:false`（含 `requested`/`unknown`）时**只用同一个 `action_id` 查 `get_operation()`**，
     直到 `run_change_status:"confirmed"`；**禁止**用新 `action_id` 再发 start。
   - 无论哪种，都回读 `get_recording_save_status()` 与 `get_new_game_options()`；step 1–5 的菜单选择不要重做。
   - 旧口径“`confirm_new_game` 整局只调一次”已废止：它把 first-run prompt 与 run start 混为一谈。
7. 若要读回一个已绑定的专用档：`load_recording_game()`（必须先由人把游戏退回主菜单；只读绑定槽）。
   响应 `run_change:"issued"` + `run_change_status` 通常先是 `"requested"`：
   **不要**再发第二个 `action_id`；用同一个 `action_id` 轮 `get_operation()` 直到
   `run_change_status:"confirmed"`（顶层会出现 `completion_evidence`）。run identity 不可读 = `unknown`，
   不等于没发生、也不等于已换局，只能查询不能自动重放。
8. `get_state()` 确认 `in_game:true`、`day`、`run_summary`，然后进入 §2。

**不要**调用通用 `act()` / `press()`，不要调用 `new_game()` / `load_game()` / `save_game()`：
它们不在录制面（`--tools recording`）里，绕过菜单与槽位策略属于违规路径。
`press` 是 `run_mode=test` 的 test-only 通道（且只对白名单控件放行），**正式 Play 永远不依赖 `press`**；
如果某个工作流看起来“必须 press 才能过”，立即停止并报告，**不要**建议在 Play 打开 `press`。

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
| `negotiation_open` | 谈判面板是否真的打开（`stage_negotiation_item` completed 的必要条件之一） |
| `negotiated_item` | 当前谈判桌上被接受的物品（id/identifier/name/value…）；`staging` uncertain 后用它判定真实落点，**不要**靠猜 |
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
| `dispatch_status` | `not_dispatched \| dispatched \| unknown`：是否真的调用了原生。`unknown` **不是**"没调用" |
| `dispatched` | 与上一行同源的兼容布尔：`true` / `false` / **`null`**（`null` 即 unknown） |
| `run_change` | 仅 `confirm_new_game` / `load_game` / `load_recording_game`：`"issued"` = 真的派发（或可能已派发）跨局原生动作；`"not_issued"` = 未派发跨局动作（含 first-run prompt 确认与菜单拒绝）。**只有 `issued` 才进入跨局防盲重试** |
| `visit_complete` | 仅 `confirm_trade`：这位顾客**整趟**逛完了没有。`false` 表示这笔成交了、但顾客可能还要买；**它不证明单笔成交** |

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
5. `get_operation("*")`：列出**非终态**操作，**列表有上限（最多 20 条）**：
   `{ok, count, total_non_terminal, truncated, truncation_note, operations:[{operation_id, op,
   execution_status, delivery_status, dispatch_status, revoked…}]}`。
   - `truncated:true` 表示还有非终态操作没列出（`total_non_terminal > count`）：**不能把列表当完整证据**，
     也不能因为“没看到某个操作”就认为它已结案；已知/怀疑卡住时用已知 `operation_id` 直接 `get_operation(id)`。
   - 旧构建可能没有 `truncated` 字段；`count == 20` 时要按“可能被截断”处理。
   - 该查询**只读**，不会推进或改写操作状态；拿到 id 后按真实读回/verifier 结果收敛，**没有“轮询几次就安全”的固定次数**。
6. `wait_operation(operation_id, timeout_ms)`：Python 侧有界轮询（≤10s），不阻塞 Unity 主线程。
   - 终态返回终态 payload；
   - 超时返回 `ok:false` + `status:"pending"` + `execution_status` + `operation_id` + `next`
     （另带 `timed_out:true`），**绝不是假成功**。
6. `pending/uncertain/completed` **只能**来自 bridge 的 operation 两轴状态（写响应或
   `get_operation()`/`wait_operation()`）。模型自己的话、自然语言总结、`claim_text`、
   `action_history` 都不是证据。
7. `uncertain` 操作**不得用新的 `action_id` 重试**：同 op+args 必须复用原 `action_id`，
   或先用 `get_operation()` / `wait_operation()` 查清状态。**盲重试 = 允许双扣款/双成交，是硬违规。**
   `dispatch_status:"unknown"`（`dispatched:null`）就是这一类：原生可能已经执行，
   即使 `ok:false` 也**不能**当成"安全拒绝"重试；响应会带 `next` 说明。
   只有 `dispatch_status:"not_dispatched"` + `execution_status:"rejected"` 才是"世界无变化"。
8. `pending` 操作存在时，写动作按**冲突关系**判断（validator `CONFLICTING_WRITE_WHILE_PENDING` 同口径）：
   - `same_op`：同一个原生调用（call-scoped op 为 `confirm_trade` / `stage_negotiation_item`：
     op+参数相同 → 冲突；其它 op 按 op 名）→ 冲突；
   - `same_item`：两个写动作命中同一个 `item_id` → 冲突；
   - `run_changing`：有跨局请求在飞行或已请求未 confirmed → 冲突（见规则 11）；
   - `modal_resolution`：`answer_prompt` / `choose_dialogue` / `advance_dialogue` / `close_windows`
     去解一个**别类** pending 操作等待的弹窗/对话 → 允许；两个对话类写互相竞争同一输入 → 冲突；
   - `independent`：call-scoped 交易且 item 可证明不同 → 允许；
   - **证据不足一律按冲突/保守处理（`unknown`），不是“全放行”**。
   读（`get_state()` 等）与同一 `action_id` 的幂等重放永远允许。
9. `delivery_status:"undelivered"`：响应丢失但执行事实可能已成立——用原 `action_id` 重查/重放拿回原证据
   （会带 `redelivered:true`），不要新建 id。
10. 进程重启不承诺跨进程 exactly-once；durable intent 只证明“曾发出过”，不证明“执行到哪一步”。
11. **跨局（run change）语义（R5/A1）**：first-run prompt 确认是独立 UI 操作，不是 native run start；
    以响应 `run_change` 为准（`not_issued` vs `issued`）。`issued` 且 `run_change_confirmed:false`
    （`requested`/`unknown`）时只能查询，不能新 `action_id` 重发；`run_change_status:"confirmed"`
    只由**真实 run identity 读回**产生，revision/day/scene 变化不能替代 run identity。
    prompt-only 结果**不带** `run_change_status`/`run_change_confirmed`。

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

### 5.1 流程（R5-B1：先暂存、再成交）

**先暂存商品，再确认成交。** 谈判面板不会自己开：用
`stage_negotiation_item(item_id=<id>, rev=<state.rev>)` 把商品放到称重/后柜台——它与玩家拖拽走**同一原生路径**
（不直接改 `currentNegociatedItem`、归属、顾客意图或价格），**在 recording 面**（recording=107 包含它）。

暂存 `completed` 的必要条件（全部为真，缺一不能当成功）：

- `execution_status:"completed"`；
- `staged_item_id_after == item_id`（桌上的就是请求的那件）；
- `item_on_weighted_counter == true`（物品真的在称重台上）；
- `negotiation_open == true`（谈判面板真的打开）；
- `client_identity_unchanged == true`（同一顾客；不能拿别的顾客状态背书）。

A→B→A 切换、同一顾客连续多笔都支持：对同一顾客重复 `stage_negotiation_item` 换成另一件商品即可。
旧的 `move_item(item,"main") → move_item(item,"back")` 两步“开面板”写法**不再是推荐路径**（历史兼容才保留）。

**暂存被拒（派发前，`dispatched:false` + `execution_status:"rejected"`，世界无副作用）** 稳定错误码：
`not_in_game`、`no_client`、`client_unreadable`、`staging_read_failed`、`negotiation_unreadable`、
`modal_open`、`item_not_found`、`item_sold`、`item_unreadable`、`item_unreachable`、`not_owned`、
`already_owned`、`intent_undefined`、`back_counter_unreadable`、`back_counter_full`。
处理：接受拒绝，按错误语义重新读状态（换商品/先处理弹窗/等顾客），**不要**重复发同一请求“确认一下”。

**暂存 uncertain（最危险）**：`error:"outcome_unverified"`（静默原生返回、readback 不匹配、
`negotiation_not_open` 等）或 `error:"client_changed"`，`execution_status:"uncertain"`、`dispatched:true`：

- 只能查 `get_operation()` / `wait_operation()`，并回读 `get_state().negotiated_item` 与 `negotiation_open`；
- **禁止**用新 `action_id` 重试**同一 item**：`stage_negotiation_item` 与 `confirm_trade` 一样是 call-scoped，
  同 item+args 重试会被 `operation_pending` 拒绝，且可能重复原生动作；
- 换**不同 item** 是允许的（不同原生调用，新 `action_id`）；
- uncertain 期间**不要**直接 `confirm_trade()`（先查询/回读收敛，确认面板状态与桌上到底是哪件商品）；
- 只有 `dispatch_status:"not_dispatched"` + `execution_status:"rejected"` 才是“世界无变化”。

1. **看货**：`list_goods(scope="all")` → 每项 `id/identifier/name/value/owned/sellable`。
2. **暂存**：`stage_negotiation_item(item_id=<id>, rev=<state.rev>)`，按上面的 completed/uncertain 规则判定；
   面板没开时 `confirm_trade` 会返回 `error:"no_negotiation"`（rejected、未派发、世界无变化），此时先暂存。
3. **对账基线**：`get_state()` 记 `cash`、`inventory_count`、`back_counter_count`、`negotiated_item`。
4. **买**：`confirm_trade(item_id=<id>, side="buy", rev=<state.rev>)`（暂存 completed 之后）。
5. **卖**：先 `list_inventory(container="main")` 拿 `item_id`，暂存后再
   `confirm_trade(item_id=<id>, side="sell", rev=<新 rev>)`。
6. **回读验证**：再 `get_state()`，核对现金/库存/柜面变化；以物易物可能**零现金变化**。
7. 只有 `execution_status:"completed"` **且满足 §5.2 单笔证据**才算成交；**成交完成 ≠ 顾客工作流完成**，
   后续对话用 `advance_dialogue()` / `choose_dialogue()` 继续，交易 completed/uncertain 都不阻塞对话。

### 5.2 `confirm_trade` 成功证据（唯一可信来源，R5/A3 口径）

`completed`（`ok:true`）必须同时具备**同一顾客、同一笔**的成交证据：

- `client_unchanged: true`（顾客身份未变；不能用别的顾客状态背书）；
- `items_transferred: true` **或** `cash_moved: true`：
  - 现金变化可以证明买/卖本笔，但**不能**只凭现金变化判断整趟业务；
  - 以物易物/换物可以**零现金变化**，此时靠 `items_transferred`；
- `evidence` 对象要与顶层证据一致：`called:true`、`client_unchanged:true`，
  且 `evidence.items_transferred` / cash 前后值不与顶层声明矛盾。

**整趟访问完成 ≠ 单笔成交。** `business_complete` / `visit_complete` 只是归因上下文（auxiliary）：
`business_complete:true` 但 `items_transferred:false` 且 `cash_moved:false` = **本笔没有成交证据**
（旧实现曾把它当成功，是 R5/A3 修掉的 bug），必须返回 `uncertain`，不能报 `completed`。
反向必须保留：R4 已真机验证的 partial trade 仍然有效——`business_complete:false`（顾客还要继续买）
+ 本笔 item/cash 证据 = `completed`，不能退回“必须整趟完成”的旧逻辑。

响应同时带以下证据字段（用于审计与对账）：

| 字段 | 含义 |
|---|---|
| `dispatched` | 成功时为 `true`；派发前拒绝为 `false`（派发 0 次） |
| `item_id` / `side` | 请求的物品与方向 |
| `staged_item_id` | 谈判桌上被接受的物品 |
| `client_intent` | 当前顾客意图（BUY/SELL/BARTER/INFORMATION_DEALER/GUNSMITH…） |
| `business_complete` / `visit_complete` | 顾客**整趟**业务是否完成；**辅助归因，不是单笔成交判据** |
| `items_transferred` | 本笔物品归属/桌面是否真的转移（**成交证据之一**） |
| `cash_moved` / `cash_changed` | 本笔现金是否变化（**成交证据之一**；barter 可为 `false`） |
| `cash_before` / `cash_after` | 整数现金前后值（对账用；不能当唯一判据） |
| `evidence` | 明细：`called`、`error`、`business_complete`、`client_unchanged`、`items_transferred`、`cash_moved`、`cash_before`、`cash_after`、`staged_item_id_after` |

### 5.3 拒绝与“静默失败”分类

派发前拒绝（`dispatched:false`、`execution_status:"rejected"`，世界无副作用）：
`trade_locked`（B10 被逮捕/枪战锁定）、`intent_undefined`、`business_complete`（B11 业务已完成）、
`item_not_staged`（B12 物品不在桌上）、`barter_not_acceptable`、`not_introduced`（B13）、
`cannot_afford`（B14）、`gun_order_not_accepted`（B15）、`cannot_buy_from_table`、
`client_refuses_item`（B16）、`bad_side`、`item_not_found`、`no_negotiation`、`not_owned`、
`already_owned`、`trade_read_failed`。
处理：接受拒绝，重新读状态、重新规划；不要重复发送同一请求“确认一下”。

原生 accept 调用返回但**没有可信完成证据**（静默 return / 异常 / 业务未完成 / 本笔无 item/cash 证据）：
`error:"outcome_unverified"` + `execution_status:"uncertain"` + `dispatched:true`
（本笔无证据时 `detail:"no_trade_evidence"`，业务未完成时 `detail:"business_not_complete"`）。
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
- `sleep()` 触发原生异步黑屏序列，**返回时 day 可能尚未变**。R5/A2 语义（不要再用旧口径）：
  - **`day` 不变只是“尚未观测到完成”，是 inconclusive，不是失败**；`day_changed:false` + `execution_status:"running"`
    表示异步仍在进行；观察窗口内（bridge 侧 10s 单调时钟）不产生判决。
  - 收敛只能靠**可观测证据**：`readback.day_advanced:true`、后续 `get_state().day` 增加、
    或 `get_day_report()`/状态读回证明已换天；同时用 `get_operation()` / `wait_operation()` 查询同一 `action_id`。
  - 超过观察窗口仍无证据 → `execution_status:"uncertain"` + `error:"sleep_unconfirmed"`：
    **不是失败也不是成功**；uncertain 是非终态，同一 op 用新 `action_id` 再发会被 `operation_pending` 拒绝
    （防双推进）。后续真实观测到 day 增加仍可把该操作 promote 为 `completed`（安全恢复证据）。
  - 只有**可证明未派发**的原生 sleep（`error:"native_not_called"` + `dispatched:false`，世界无副作用）
    才允许换新 `action_id` 重试；`outcome_unknown`/`dispatched:null` **禁止**重试。
  - **禁止**“连续轮询 3 次后当失败/安全重试”的旧写法；轮询次数不是证据，有界等待 + 查询才是。
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
`client_refuses_item`、`bad_side`、`no_negotiation`、`trade_read_failed`；
暂存专用（R5-B1，全部 `dispatched:false`）：`not_in_game`、`no_client`、`client_unreadable`、
`staging_read_failed`、`negotiation_unreadable`、`modal_open`、`item_not_found`、`item_sold`、
`item_unreadable`、`item_unreachable`、`not_owned`、`already_owned`、`intent_undefined`、
`back_counter_unreadable`、`back_counter_full`。

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
| `sleep` 的 `error:"sleep_unconfirmed"` + `execution_status:"uncertain"` | 观察窗口内无换天证据：不是失败也不是成功；只查询/继续观测，**禁止新 action_id 重发**；后续观测到 day 增加可 promote `completed` |
| `run_change:"issued"` + `run_change_confirmed:false`（`requested`/`unknown`） | 跨局动作可能已派发：只用原 `action_id` 查 `get_operation()` 到 `run_change_status:"confirmed"`；**禁止**新 id 重发 start/load |

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
- 交易证据：`business_complete`、`visit_complete`（整趟，辅助归因）、`client_unchanged`、
  `items_transferred`、`cash_moved`、`cash_changed`、`cash_before`、`cash_after`、`staged_item_id`、`client_intent`、
  `evidence.{called,error,business_complete,client_unchanged,items_transferred,cash_moved,cash_before,cash_after,staged_item_id_after}`；
- 暂存证据（R5-B1）：`item_id`、`staged_item_id_before`、`staged_item_id_after`、`negotiation_open`、
  `client_identity_unchanged`、`item_on_weighted_counter`、`native_primitive`、`evidence`，
  以及 uncertain 后的 `get_state().negotiated_item` 回读；
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
- 操作：`open_shop()`、`call_next_customer()`、`stage_negotiation_item()`、`confirm_trade()`、
  `advance_dialogue()`、`choose_dialogue()`、`dismiss_current_client()`、
  `get_pending_prompt()`、`answer_prompt()`、`close_windows()`。
- 日终：`end_day()`、`sleep()`、`wait_for_events()`。
- 操作协议（D 已注册）：`get_operation()`、`wait_operation()`。
- 开局（仅专用槽）：`get_new_game_options()`、`open_new_game_menu()`、
  `select_new_game_option()`、`new_game_continue()`、`confirm_new_game()`、
  `get_recording_save_status()`。

不在首周最小闭环、未经 live evidence 的工具（机器、酿酒、彩票、远征、bargain 等）不要主动使用。

---

## 13. schema 已核对（2026-11-18 Round 4；2026-11-19 R5-A/R5-B1 离线修订）

以下字段已与 `mcp_server.py` / `src/Bridge/` / `src/Core/` 实际实现核对
（Round 4 / `docs/reviews/ROUND4_DISPOSITION.md`；R5-A 的 A1/A2/A3 与 R5-B1 的 staging 只做源码 + 单测核对，
**真机 L3 仍 `NOT_RUN`**；policy sha 以 R5-B1 离线实测
`774fbfdd7b44ef5c6c7b616f885f1b624758b862b3e750497f8a39cc26763277` 为参照、run 时实算：
工具 full=117 / recording=107）：

| 主题 | 已核对 schema |
|---|---|
| 写响应 | `operation_id`、`execution_status`、`delivery_status`、`dispatch_status`、`dispatched`（true/false/**null**）、跨局时 `run_change_status`/`run_change_confirmed`/`run_change`（`"issued"`/`"not_issued"`；R5/A1）、晋升后 `completion_evidence`/`initial_result`；`ok:true` 仅 completed；重复 ID → 原证据 + `delivery_status:"undelivered"` + `redelivered:true`；重复调用被挡 → `operation_pending`/`run_change_pending` + `blocking_operation_id`（**未派发，不产生新 operation**） |
| 操作查询 | `get_operation` 返回 `known/ok/operation_id/op/execution_status/delivery_status/dispatch_status/dispatched/run_change_status/run_change_confirmed/completion_evidence/error/rev/result_rev/next`；未知 id `known:false` + `unknown_operation`（run 变化后带 `process_restarted:true`）；`wait_operation` 超时 `ok:false + status:"pending" + operation_id + execution_status + next`；`get_operation("*")` 上限 20 条 + `count`/`total_non_terminal`/`truncated`/`truncation_note`（旧构建可能无 truncated 字段） |
| 交易证据 | `business_complete`、`visit_complete`（整趟，辅助归因）、`client_unchanged`、`items_transferred`、`cash_moved`、`cash_before`、`cash_after`（整数）、`cash_changed`、`staged_item_id`、`client_intent`、`evidence.{called,error,business_complete,client_unchanged,items_transferred,cash_moved,cash_before,cash_after,staged_item_id_after}`；完成判据 = `client_unchanged` + (items 或 cash)；拒绝时 `dispatched:false`；静默 return/无本笔证据 → `error:"outcome_unverified"` + `execution_status:"uncertain"`（`detail:"no_trade_evidence"`/`"business_not_complete"`） |
| 暂存（R5-B1） | `stage_negotiation_item(item_id, rev)`：完成判据 = `execution_status:"completed"` + `staged_item_id_after == item_id` + `item_on_weighted_counter:true` + `negotiation_open:true` + `client_identity_unchanged:true`；另有 `staged_item_id_before`、`client_intent`、`native_primitive`、`evidence.{called,error,staged_item_id_before,staged_item_id_after_readable,staged_item_id_after,item_on_weighted_counter,negotiation_readable_after,negotiation_open_after,client_identity_unchanged,native_primitive}`；拒绝 `dispatched:false` + 稳定错误码；silent/mismatch → `uncertain` + `next`（read `get_state.negotiated_item`，不要同 item 新 id 重试，不同 item 允许）。`Core.PendingScope.IsCallScoped` 覆盖它 |
| 关店 | `execution_status:"completed"` ⇔ `shop_closed_after:true`；仍 OPEN → `ok:false + pending:true + execution_status:"running" + error:"shop_still_open"` |
| 睡觉 | `day_changed`、`save_evidence`（`save_completed`/`save_unknown`）、`save_completed`、`save_unknown`；`last_save_time` 不算落盘证据；`day_changed:false` = running/pending/inconclusive（**不是失败**）；**日终面板没开时 `error:"end_of_day_panel_closed"` + `not_dispatched"`（世界没动，可换新 id 重试）**；R5/A2：无 count-based failure，窗口（10s 单调时钟）内无判决，超时无证据 → `uncertain` + `error:"sleep_unconfirmed"`（非终态、拒绝新 id 重发；后续 day 增加可 promote `completed`）；只有 `native_not_called`+`dispatched:false` 可安全重试 |
| 快照 | `legal_actions`（`availability="available"`）与 `candidate_actions`（`availability="unknown"`、`requires="native_guard"`）；举报用纯字段 `reported`/`is_wanted` |
| 工具面 | full = 117，recording = 107；`get_operation` / `wait_operation` / `stage_negotiation_item` 均已注册，且三者都在 recording 面；工具数只作旁证，以 `policy_sha256` 为准（R5-B1 后 `774fbfdd…`） |
| 轨迹校验 | `agent/trace_validator.py` `VALIDATOR_VERSION = "1.3.0"`；trace `schema_version: 2` 为当前口径；无版本历史 trace 按 v1 旧口径校验并给 `TRACE_SCHEMA_VERSION_MISSING` warning；未知版本 `TRACE_SCHEMA_VERSION_UNSUPPORTED` 直接拒绝；call-scoped op 集合 = `confirm_trade` + `stage_negotiation_item`（同 item+args pending 重试 = `CONFLICTING_WRITE_WHILE_PENDING`，不同 item 允许） |
| MCP 模式 | 注册面 = bridge 报告的 `run_mode`；`auto` 在 bridge 不可达/未知时 **locked**（只留本地 memory 工具），显式 `--mode test` 也必须 bridge 报告 `test`；运行中模式漂移 → `error:"mode_changed"`，需重启 MCP |

若后续再改冻结协议：本表随 Lead 的协议变更同步；**不要再引入“待核对”字段**。
