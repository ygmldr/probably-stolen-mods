# STRICT_ALLOWLIST_DECISIONS — 逐工具放行决策（Round 3 复审后）

- **来源**：`agent/policy/tool_policy.json`（schema 2），由 `agent/tool_policy.py` 读取
- **依据**：真实 `tools/list`（114）+ `PLAYER_ENTRY_MATRIX.csv`（58 写 op）
- **决策分布**：`allow` 73 / `needs_evidence` 30 / `deny` 11
- **模式可导出**：bootstrap **48** / play 候选 **73** / test 114
- **`play_allowlist`（严格玩家模式）**：**0** —— 需要 live 证据，本轮全部 `NOT_RUN`

## 0. 判定规则（`tool_policy.validate()` 强制）

| 条件 | 决策 |
|---|---|
| 特权工具（`save_game`/`new_game`/`act`/`press`/debug/expedition） | `deny` |
| 读工具被审计标记 `mutating`（`bargain_preview`） | `deny` |
| `cached_impure`（`get_clientele`/`barter_status`） | `needs_evidence` |
| 写 op 在矩阵 `fixed` 且有回归测试 | `allow` |
| 写 op 在矩阵 `open`/`partial`/`unverified` | `needs_evidence` |
| 只读 op（recorder `READ_ONLY_OPS`）且无已知副作用 | `allow` |
| 快照读取别名（`SNAPSHOT_READERS`） | `allow`（D0a 后静态纯），但 **bootstrap 必须为 false** |
| 不在策略文件里的工具 | **`deny`**（默认拒绝） |

**两层门槛（R3-04）**

1. `candidate_allowlist()`：静态允许，只用于报告与测试，**不等于可以玩**；
2. `play_allowlist()`：静态允许 **且** 有 live 证据。当前为空。

## 1. R3-01 / R3-02 的处置

- **R3-01**：`get_state` 与全部快照读取别名在 bootstrap 中一律 `false`；`validate()` 会把
  "bootstrap + SNAPSHOT_READERS" 判为错误，测试 `test_bootstrap_never_reads_the_snapshot` 锁住。
  文本、表格、代码现在一致：**bootstrap 不读快照**。
- **R3-02**：`SNAPSHOT_READERS = {get_state, get_run_summary, get_legal_actions, wait_for_change,
  wait_for_events, wait_until}`，由 `test_snapshot_reader_aliases_match_the_server_source`
  逐行扫描 `mcp_server.py` 的 `@tool` 函数校验——新增别名会让测试失败（`PP-READ-04`）。

## 2. 逐工具表

## A. `allow`（候选；无 live 证据前不进 play）（73 个）

| tool | purity | status | decision | boot/play/test | live | reason |
|---|---|---|---|---|---|---|
| `answer_prompt` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `blur_new_game_option` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `capture_screenshot` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `choose_dialogue` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `commissary_buy` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `confirm_new_game` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `contraband_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `deliver_request` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `end_day` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `events_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `expedition_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `focus_new_game_selected_perk` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `get_action_history` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `get_calendar` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `get_day_report` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `get_item` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `get_layout` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `get_legal_actions` | pure | d0a_fixed | allow | -/Y/Y | - | snapshot path no longer reads Refresh()-backed getters (D0a); live PPT-READ evidence still missing |
| `get_new_game_options` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `get_pending_prompt` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `get_recording_save_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `get_run_summary` | pure | d0a_fixed | allow | -/Y/Y | - | snapshot path no longer reads Refresh()-backed getters (D0a); live PPT-READ evidence still missing |
| `get_state` | pure | d0a_fixed | allow | -/Y/Y | - | snapshot path no longer reads Refresh()-backed getters (D0a); live PPT-READ evidence still missing |
| `health_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `husbandry_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `inspect_ticket` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_analyzers` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_books` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_contacts` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_containers` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_factions` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_goods` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_interactions` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_inventory` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_labels` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_locations` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_machines` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_marks` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_requests` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_screenshots` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_services` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_signs` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_tickets` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_upgrades` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `list_wine` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `load_game` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `load_recording_game` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `lottery_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `machine_grow_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `machine_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `memory_bootstrap` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `memory_get` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `memory_retrieve` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `memory_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `new_game_continue` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `new_game_next_page` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `open_new_game_menu` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `open_shop` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `phone_state` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `ping` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `read_book` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `read_item` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `redeem_ticket` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `return_home` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `scavenge` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `security_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `select_new_game_option` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `set_pen_mode` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `toggle_service` | write | fixed | allow | -/Y/Y | - | guard fixed in 3d85963/3fed61c with regression tests |
| `wait_for_change` | pure | d0a_fixed | allow | -/Y/Y | - | snapshot path no longer reads Refresh()-backed getters (D0a); live PPT-READ evidence still missing |
| `wait_for_events` | pure | d0a_fixed | allow | -/Y/Y | - | snapshot path no longer reads Refresh()-backed getters (D0a); live PPT-READ evidence still missing |
| `wanted_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |
| `wine_status` | pure | implemented | allow | Y/Y/Y | - | read-only per recorder classification; no known side effect |

## B. `needs_evidence`（静态未放行）（30 个）

| tool | purity | status | decision | boot/play/test | live | reason |
|---|---|---|---|---|---|---|
| `activate_item` | write | open | needs_evidence | -/-/Y | - | open guard gap: - |
| `advance_dialogue` | write | open | needs_evidence | -/-/Y | - | open guard gap: B43 |
| `analyze_item` | write | open | needs_evidence | -/-/Y | - | open guard gap: B42 |
| `arrest_wanted` | write | open | needs_evidence | -/-/Y | - | open guard gap: - |
| `bargain` | write | open | needs_evidence | -/-/Y | - | open guard gap: - |
| `barter_accept` | write | open | needs_evidence | -/-/Y | - | open guard gap: B26 |
| `barter_status` | cached_impure | needs_evidence | needs_evidence | -/-/Y | - | read path mutates UI/RNG (audit S1-S4) or purity unproven |
| `buy_upgrade` | write | open | needs_evidence | -/-/Y | - | open guard gap: B27|B28 |
| `buy_wholesale` | write | open | needs_evidence | -/-/Y | - | open guard gap: B17|B18|B19 |
| `call` | write | open | needs_evidence | -/-/Y | - | open guard gap: B40 |
| `call_next_customer` | write | open | needs_evidence | -/-/Y | - | open guard gap: - |
| `close_windows` | write | open | needs_evidence | -/-/Y | - | open guard gap: - |
| `confirm_trade` | write | open | needs_evidence | -/-/Y | - | open guard gap: B10|B11|B12|B13|B14|B15|B16 |
| `dismiss_current_client` | write | partial | needs_evidence | -/-/Y | - | open guard gap: B20|B21|B22 |
| `dismiss_tutorial_hints` | write | unverified | needs_evidence | -/-/Y | - | open guard gap: - |
| `end_call` | write | open | needs_evidence | -/-/Y | - | open guard gap: B39 |
| `get_clientele` | cached_impure | needs_evidence | needs_evidence | -/-/Y | - | read path mutates UI/RNG (audit S1-S4) or purity unproven |
| `go_out` | write | open | needs_evidence | -/-/Y | - | open guard gap: B23|B24|B25 |
| `machine_collect` | write | open | needs_evidence | -/-/Y | - | open guard gap: B33 |
| `machine_insert` | write | open | needs_evidence | -/-/Y | - | open guard gap: B32 |
| `move_item` | write | open | needs_evidence | -/-/Y | - | open guard gap: B29|B30|B31 |
| `phone_advance` | write | open | needs_evidence | -/-/Y | - | open guard gap: B38 |
| `phone_choose` | write | open | needs_evidence | -/-/Y | - | open guard gap: B38 |
| `report_client` | write | open | needs_evidence | -/-/Y | - | open guard gap: B41 |
| `set_label` | write | open | needs_evidence | -/-/Y | - | open guard gap: B37 |
| `sleep` | write | partial | needs_evidence | -/-/Y | - | open guard gap: B8|B9 |
| `start_fermentation` | write | open | needs_evidence | -/-/Y | - | open guard gap: B36 |
| `toggle_item` | write | open | needs_evidence | -/-/Y | - | open guard gap: - |
| `use_tool` | write | open | needs_evidence | -/-/Y | - | open guard gap: - |
| `wine_add_ingredient` | write | open | needs_evidence | -/-/Y | - | open guard gap: B30 |

## C. `deny`（11 个）

| tool | purity | status | decision | boot/play/test | live | reason |
|---|---|---|---|---|---|---|
| `act` | privileged | denied | deny | -/-/Y | - | no player-reachable path; diagnostic/privileged entry |
| `advance_expedition` | privileged | denied | deny | -/-/Y | - | no player-reachable path; diagnostic/privileged entry |
| `bargain_preview` | mutating | needs_evidence | deny | -/-/Y | - | read path mutates UI/RNG (audit S1-S4) or purity unproven |
| `dispatch_expedition` | privileged | denied | deny | -/-/Y | - | no player-reachable path; diagnostic/privileged entry |
| `get_ui_snapshot` | privileged | denied | deny | -/-/Y | - | no player-reachable path; diagnostic/privileged entry |
| `new_game` | privileged | denied | deny | -/-/Y | - | no player-reachable path; diagnostic/privileged entry |
| `press` | privileged | denied | deny | -/-/Y | - | no player-reachable path; diagnostic/privileged entry |
| `save_game` | privileged | denied | deny | -/-/Y | - | no player-reachable path; diagnostic/privileged entry |
| `save_recording_game` | privileged | denied | deny | -/-/Y | - | no player-reachable path; diagnostic/privileged entry |
| `wait_until` | privileged | denied | deny | -/-/Y | - | no player-reachable path; diagnostic/privileged entry |
| `wine_add_water` | privileged | denied | deny | -/-/Y | - | no player-reachable path; diagnostic/privileged entry |

## 3. 启用 `play` 的前置（缺一不可）

1. `live_evidence` 非空：主路径每个工具至少一条真机证据，且 `PP-RECORD-01` 版本/MVID/DLL 一致；
2. `PP-READ-01/02/04` 真机通过（离线结构修复不等于实机验证）；
3. `PP-MODE-01`：MCP 隐藏、直连 HTTP、`act` 三条路径都被 C# 拒绝；
4. `PP-TOOLS-01/02/03` 在 CI 常绿。

在此之前，本文件的 `allow` 只代表"静态可放行"，不代表已验证、更不代表可录制。
