# STRICT_ALLOWLIST_DECISIONS — 逐工具放行决策

- **来源**：`agent/policy/tool_policy.json`（sha256 `4e1ce849…`），由 `agent/tool_policy.py` 读取
- **生成依据**：`docs/audit/tools-inventory.json`（真实 `tools/list`，114 项）
  + `docs/audit/PLAYER_ENTRY_MATRIX.csv`（58 写 op 的守卫状态）
- **工具数**：114（与 full 一一对应；recording = 104 是其子集）
- **决策分布**：`allow` 72 / `needs_evidence` 31 / `deny` 11
- **`live_ready`**：**0** → 因此 `play`（strict_player）**当前不可启用**

## 0. 判定规则（代码即规则，`tool_policy.validate()` 会拒绝违规条目）

| 条件 | 决策 |
|---|---|
| 特权工具（`save_game`/`new_game`/`act`/`press`/debug/expedition） | `deny`，任何模式（test 除外） |
| 读工具被审计标记 `mutating`（如 `bargain_preview`） | `deny` |
| 读工具 `cached_impure`（`get_clientele`/`barter_status`） | `needs_evidence` |
| `get_state`（快照 getter 会 `Refresh()`） | `needs_evidence`，play 禁止（D0a 待办） |
| 写 op 在矩阵中 `fixed` 且有回归测试 | `allow` |
| 写 op 在矩阵中 `open`/`partial`/`unverified` | `needs_evidence` |
| 只读 op（recorder 的 `READ_ONLY_OPS`）且无已知副作用 | `allow` |
| 不在策略文件里的工具 | **`deny`**（默认拒绝，新增工具必须先登记） |

**模式语义**

- `bootstrap`：只读热启动（53 个），不含 `get_state`（见下）；
- `play`：严格玩家等价（72 个候选），**必须有 live 证据才真正启用**；
- `test`：全部 114 个，只允许在丢弃档使用。

## 1. `get_state` 例外（必须先读）

`get_state` 的 purity 是 `cached_impure`：快照构建会走 `CanOffer*` → `BargainUIManager.Refresh()`
（`ActionRouter.cs:198 → StateSnapshot.cs:88/:181 → GameState.cs:692-703`）。
`Refresh()` 只在 `trackedClient != client` 时重置谈判状态位（`BargainUIManager.cs:1103-1126`），
因此是"有能力变更的读路径"，不是"每次必变"。

处置：`bootstrap` 放行、`play` 禁止、`status = d0a_pending`。
**重新准入条件**：D0a 落地后，`play` 快照路径的 `Refresh()` 调用数 = 0 且 `PP-READ-01/02` 通过。
不允许用"把它标成 pure"绕过。

## 2. 逐工具表

## A. `allow`（72 个，候选放行；仍需 live 证据才可用于 play）

| tool | purity | status | bootstrap/play/test | reason |
|---|---|---|---|---|
| `answer_prompt` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `blur_new_game_option` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `capture_screenshot` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `choose_dialogue` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `commissary_buy` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `confirm_new_game` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `contraband_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `deliver_request` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `end_day` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `events_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `expedition_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `focus_new_game_selected_perk` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `get_action_history` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `get_calendar` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `get_day_report` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `get_item` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `get_layout` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `get_legal_actions` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `get_new_game_options` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `get_pending_prompt` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `get_recording_save_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `get_run_summary` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `health_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `husbandry_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `inspect_ticket` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_analyzers` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_books` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_contacts` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_containers` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_factions` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_goods` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_interactions` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_inventory` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_labels` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_locations` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_machines` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_marks` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_requests` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_screenshots` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_services` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_signs` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_tickets` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_upgrades` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `list_wine` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `load_game` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `load_recording_game` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `lottery_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `machine_grow_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `machine_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `memory_bootstrap` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `memory_get` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `memory_retrieve` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `memory_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `new_game_continue` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `new_game_next_page` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `open_new_game_menu` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `open_shop` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `phone_state` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `ping` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `read_book` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `read_item` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `redeem_ticket` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `return_home` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `scavenge` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `security_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `select_new_game_option` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `set_pen_mode` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `toggle_service` | write | fixed | -/Y/Y | guard fixed in 3d85963/3fed61c with regression tests |
| `wait_for_change` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `wait_for_events` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `wanted_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |
| `wine_status` | pure | implemented | Y/Y/Y | read-only per recorder classification; no known side effect |

## B. `needs_evidence`（31 个，静态放行但不进 play）

| tool | purity | status | bootstrap/play/test | reason |
|---|---|---|---|---|
| `activate_item` | write | open | -/-/Y | open guard gap: - |
| `advance_dialogue` | write | open | -/-/Y | open guard gap: B43 |
| `analyze_item` | write | open | -/-/Y | open guard gap: B42 |
| `arrest_wanted` | write | open | -/-/Y | open guard gap: - |
| `bargain` | write | open | -/-/Y | open guard gap: - |
| `barter_accept` | write | open | -/-/Y | open guard gap: B26 |
| `barter_status` | cached_impure | needs_evidence | -/-/Y | read path mutates UI/RNG (audit S1-S4) or purity unproven |
| `buy_upgrade` | write | open | -/-/Y | open guard gap: B27|B28 |
| `buy_wholesale` | write | open | -/-/Y | open guard gap: B17|B18|B19 |
| `call` | write | open | -/-/Y | open guard gap: B40 |
| `call_next_customer` | write | open | -/-/Y | open guard gap: - |
| `close_windows` | write | open | -/-/Y | open guard gap: - |
| `confirm_trade` | write | open | -/-/Y | open guard gap: B10|B11|B12|B13|B14|B15|B16 |
| `dismiss_current_client` | write | partial | -/-/Y | open guard gap: B20|B21|B22 |
| `dismiss_tutorial_hints` | write | unverified | -/-/Y | open guard gap: - |
| `end_call` | write | open | -/-/Y | open guard gap: B39 |
| `get_clientele` | cached_impure | needs_evidence | -/-/Y | read path mutates UI/RNG (audit S1-S4) or purity unproven |
| `get_state` | cached_impure | d0a_pending | Y/-/Y | snapshot getters call BargainUIManager.Refresh(); blocked until D0a |
| `go_out` | write | open | -/-/Y | open guard gap: B23|B24|B25 |
| `machine_collect` | write | open | -/-/Y | open guard gap: B33 |
| `machine_insert` | write | open | -/-/Y | open guard gap: B32 |
| `move_item` | write | open | -/-/Y | open guard gap: B29|B30|B31 |
| `phone_advance` | write | open | -/-/Y | open guard gap: B38 |
| `phone_choose` | write | open | -/-/Y | open guard gap: B38 |
| `report_client` | write | open | -/-/Y | open guard gap: B41 |
| `set_label` | write | open | -/-/Y | open guard gap: B37 |
| `sleep` | write | partial | -/-/Y | open guard gap: B8|B9 |
| `start_fermentation` | write | open | -/-/Y | open guard gap: B36 |
| `toggle_item` | write | open | -/-/Y | open guard gap: - |
| `use_tool` | write | open | -/-/Y | open guard gap: - |
| `wine_add_ingredient` | write | open | -/-/Y | open guard gap: B30 |

## C. `deny`（11 个）

| tool | purity | status | bootstrap/play/test | reason |
|---|---|---|---|---|
| `act` | privileged | denied | -/-/Y | no player-reachable path; diagnostic/privileged entry |
| `advance_expedition` | privileged | denied | -/-/Y | no player-reachable path; diagnostic/privileged entry |
| `bargain_preview` | mutating | needs_evidence | -/-/Y | read path mutates UI/RNG (audit S1-S4) or purity unproven |
| `dispatch_expedition` | privileged | denied | -/-/Y | no player-reachable path; diagnostic/privileged entry |
| `get_ui_snapshot` | privileged | denied | -/-/Y | no player-reachable path; diagnostic/privileged entry |
| `new_game` | privileged | denied | -/-/Y | no player-reachable path; diagnostic/privileged entry |
| `press` | privileged | denied | -/-/Y | no player-reachable path; diagnostic/privileged entry |
| `save_game` | privileged | denied | -/-/Y | no player-reachable path; diagnostic/privileged entry |
| `save_recording_game` | privileged | denied | -/-/Y | no player-reachable path; diagnostic/privileged entry |
| `wait_until` | privileged | denied | -/-/Y | no player-reachable path; diagnostic/privileged entry |
| `wine_add_water` | privileged | denied | -/-/Y | no player-reachable path; diagnostic/privileged entry |
## 3. 启用 strict 的前置（缺一不可）

1. `live_ready` 非空：每个要用的写工具至少一条真机证据（`PP-RECORD-01` 版本/MVID 相符）；
2. `get_state` 的 `status` 从 `d0a_pending` 变为 `fixed`（D0a 完成）；
3. `PP-MODE-01` 通过：MCP 隐藏、直连 HTTP、`act` 三条路径都被 C# 拒绝；
4. `PP-TOOLS-01/02/03` 在 CI 中常绿（集合一致性 + 未知默认拒绝 + open 缺陷不放行）。

在此之前，本文件的所有 `allow` 只表示"候选放行"，**不表示已验证**。
