# 玩家等价实施计划（D0–D6）

- **状态**：Proposed
- **前置**：`docs/ADR-PLAYER-PARITY.md`、`docs/reviews/PLAYER_PARITY_RFC_REVIEW.md`、`docs/audit/PLAYER_ENTRY_MATRIX.csv`
- **基线**：commit `d0440ab`；C# 651 / 契约 490 / Python 234
- **总原则**：每一阶段有可证伪的退出门槛；**没有通过的阶段不得开始录像**。

---

## 阶段总览

| 阶段 | 主题 | 阻塞录像 | 预估 |
|---|---|---|---|
| **D0** | 基线归零 + 已知 P0 关闭 | ✅ | 0.5 天 |
| **D1** | `confirm_trade` 端到端 Operation 试点 | ✅ | 1–1.5 天 |
| **D2** | `end_day` / `sleep` / `open_shop` 接入 + 原生事件 | ✅ | 1 天 |
| **D3** | 只读纯度（`get_state` / `bargain_preview` / `get_clientele`） | ✅ | 1 天 |
| **D4** | 剩余 B/P1 缺口按业务域清扫 | ⬜ | 2–3 天 |
| **D5** | UI 差分 Oracle（L3） | ⬜ | 2–3 天 |
| **D6** | 录像门禁与最小 allowlist | ✅ | 0.5 天 |

---

## D0：基线归零（阻塞）

**任务**

1. `./build.sh` → 覆盖安装 → 重启游戏 → 记录 `/health` 的 `game_version` 与 MVID；
   确认 `dist` 与已安装 DLL 的 sha256 一致（当前不一致：`0a991b99` vs `e9e1bd07`）。
2. 跑基线：`./test.sh`、`python -m unittest discover -s agent/tests`、`tools/list` 两模式；
   把 commit / MVID / 计数写进 `docs/audit/README.md`。
3. 复核 `PLAYER_ENTRY_MATRIX.csv` 中 `unverified=yes` 的行，能静态确认的降级为 verified。
4. 确认 `bargain_preview` 不在 `strict_player` 的 allowlist；`get_state` 副作用登记为 D3 阻塞。

**退出**：`dist` 与安装一致；三条测试命令全绿；矩阵 0 个 `unverified`（或全部有取证计划）；
`/health` 返回的 MVID 与 `bridge.conf` 一致。

---

## D1：`confirm_trade` 端到端（阻塞）

**为什么先做它**：它是唯一"每天用很多次 + 已经出过卡死事故 + 有 7 个静默分支"的动作。

**任务**

1. Core 层：`TradeOutcome` 判定（纯逻辑、可测）：输入"成交前/后快照 + 分支谓词结果"，输出
   `completed | rejected(code) | uncertain`。
2. C# 侧：`ConfirmTrade` 改为三段式
   - 前置：`IsTradeLocked` / `clientIntent` / 分支谓词（BARTER、INFO、GUNSMITH、BUY/SELL 各自的 `CanXxx`）；
   - 动作：`OnOfferAcceptClick()` 一次；
   - 回读：`back`/`front`/`main` 的物品 **id 集合**、`isBusinessComplete`、`cash`（仅辅助）。
3. 响应增加 `status` / `effects` / `blocked_by`（ADR-PP-6）。
4. Python：`get_operation` / `wait_operation`；recorder 把 `status` 落库。
5. 测试：
   - L1 fake：`P0-TRADE-01/02/03/04`（正向、锁、以物易物现金不变、成交后对话未完 → `pending`）；
   - L2 真机负向（**需用户在场**）：锁住交易时调用 → 必须 `rejected` 且零副作用。

**退出**：`P0-TRADE-01..04` 在 fake 上全绿；真机负向用例至少 1 条通过；`confirm_trade` 不再可能
在原生静默 return 时返回 `ok:true`。

---

## D2：`end_day` / `sleep` / `open_shop`（阻塞）

**任务**

1. 接入 `FireOnShutterClosedEarly/Late`、`FireOnGoingSleepEarly/Late`、`FireOnHandlingNightlyServices*`
   作为完成信号；`sleep` 区分 `day_changed` 与 `save_unknown`（ADR-PP-4）。
2. `open_shop`：补 UI 可达性检查（`Button.interactable`）或明确记录"未验证"。
3. 真机用例：`P0-LANDLORD-02`（房东在队 → `sleep`/`end_day` 都拒）、`P0-OPEN-01`（动画期重复开门）、
   `P0-ASYNC-01`（关门跨帧 → `pending` 而非 `no_effect`）、`P0-ASYNC-02`（换天但保存未知）。

**退出**：4 条真机用例通过或有明确 `not_run` 理由；`sleep` 的响应能区分"跨日"与"已保存"。

---

## D3：只读纯度（阻塞）

**任务**

1. `get_state`：把 `CanOffer*`（`StateSnapshot.cs:88`、`:181-188`）与 `can_report`（`:312`）
   从快照中摘除或改为缓存值；每次 `/state` 的 `Refresh()` 调用数必须为 **0**（加计数器测试）。
2. `bargain_preview`：`strict_player` 下不注册；`assisted` 下保留并返回 `impure: true`。
3. `get_clientele`：首次返回 `rng_consumed: true`，并写入 `cached_at_day`。
4. 用例：`P0-READ-01`（连续 10 次 `get_state`，`rev` 与谈价状态位不变）、`P0-READ-02`（同一天重复读 clientele 不消耗 RNG）。

**退出**：`P0-READ-01/02` 通过；快照路径的 `Refresh()` 计数为 0。

---

## D4：剩余缺口按业务域清扫（不阻塞）

顺序（来自矩阵，非字母序）：

1. **交易族**：B18 `buy_wholesale` 收尾、B26 `barter_accept` 入口、B17 锁。
2. **物品族**：B29 `MayRemove`、B30 `MayPlayerInsertInto`、机器（B31–B33）。
3. **夜间族**：B23 六门禁、B24 背包、B25。
4. **对话族**：B43 打字机/自动推进、B39 `end_call`。
5. 其余 P2（B12/B19/B21/B27/B28/B34–B38/B40–B42）。

**退出**：矩阵中 `status=open` 的高风险行清零；每个修复有对应 `B<n>` 的回归测试。

---

## D5：UI 差分 Oracle（不阻塞）

**任务**

1. 建 `PointerInputModule.inputOverride` + `BaseInput` 探针（Unity 内测试装置）。
2. 对 `confirm_trade` / `end_day` / `sleep` / `open_shop` / `dismiss_current_client`
   各做一条"真实点击 vs MCP"的差分用例。
3. 负向优先：先做"按钮灰化 → MCP 必须拒绝"，再做正向对照。

**退出**：5 条差分用例可重复运行；失败时能给出 `reference_trace` vs `mcp_trace`。

---

## D6：录像门禁（阻塞）

**最小 allowlist（`strict_player`）**

- 观察：`ping` `get_state` `get_legal_actions` `get_run_summary` `get_action_history`
- 等待：`wait_for_events` `wait_for_change` `wait_operation` `get_operation`
- 情报：`get_day_report` `get_calendar` `get_clientele`（`cached_impure`）
- 开局：`get_new_game_options` `open_new_game_menu` `select_new_game_option` `new_game_continue`
  `new_game_next_page` `focus_new_game_selected_perk` `blur_new_game_option` `confirm_new_game`
- 存档：`get_recording_save_status` `load_game` `load_recording_game`（只在主菜单）
- 店铺：`open_shop` `end_day` `sleep` `call_next_customer` `dismiss_current_client`
- 交易：`confirm_trade` `list_goods` `get_item` `buy_wholesale` `commissary_buy` `bargain`
- 对话：`advance_dialogue` `choose_dialogue`
- 搬运：`list_containers` `list_inventory` `move_item` `get_layout`
- 取证：`capture_screenshot` `list_screenshots`
- 弹窗：`get_pending_prompt` `answer_prompt`
- 界面：`close_windows`
- 记忆（只读）：`memory_bootstrap` `memory_retrieve` `memory_get` `memory_status`

**明确排除**：`bargain_preview`（D3 前）、`save_game`、`new_game`、`act`、`press`。

**退出**：设置页/配置写完，预检脚本 `RESULT: READY`；`tools/list` 与该清单逐项一致（脚本比对）。

---

## 不在本计划内

- 跨进程 exactly-once（ADR-PP-1 明确拒绝）。
- 全量 58 个 op 的 UI 差分（D5 只做 5 个试点）。
- 记忆 P3 的自动提炼（与录像解耦）。
