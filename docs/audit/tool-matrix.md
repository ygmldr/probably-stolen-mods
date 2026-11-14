# PawnMind MCP 工具矩阵（P0 附件，102 工具）

由真实 `tools/list` 枚举生成。**风险等级与真机验证状态是两列独立事实**：

- `risk`：语义分级，由代码路径与副作用决定，与技术成熟度无关。
- `evidence`：该工具**成功路径**在真机上的观察证据。`yes`=真机成功过；`partial`=只验证过错误路径或仅脚本覆盖；`no`=成功路径从未在真机出现；`read` 类为 `n/a`。

| 工具 | 改状态 | 风险 | rev | 录制集 | 真机证据 | 参数 |
|---|---|---|---|---|---|---|
| `bargain_preview` | no | read | - | yes | n/a | direction, percent |
| `barter_status` | no | read | - | yes | n/a | - |
| `capture_screenshot` | no | read | - | yes | n/a | reason |
| `contraband_status` | no | read | - | yes | n/a | - |
| `events_status` | no | read | - | yes | n/a | - |
| `expedition_status` | no | read | - | yes | n/a | - |
| `get_action_history` | no | read | - | yes | n/a | limit |
| `get_item` | no | read | - | yes | n/a | item_id |
| `get_layout` | no | read | - | yes | n/a | container |
| `get_legal_actions` | no | read | - | yes | n/a | - |
| `get_new_game_options` | no | read | - | yes | n/a | - |
| `get_recording_save_status` | no | read | - | yes | n/a | - |
| `get_run_summary` | no | read | - | yes | n/a | - |
| `get_state` | no | read | - | yes | n/a | - |
| `get_ui_snapshot` | no | read | required | no | n/a | rev |
| `health_status` | no | read | - | yes | n/a | - |
| `husbandry_status` | no | read | - | yes | n/a | - |
| `inspect_ticket` | no | read | - | yes | n/a | item_id |
| `list_analyzers` | no | read | - | yes | n/a | - |
| `list_books` | no | read | - | yes | n/a | - |
| `list_contacts` | no | read | - | yes | n/a | include_hidden |
| `list_containers` | no | read | - | yes | n/a | - |
| `list_factions` | no | read | - | yes | n/a | - |
| `list_goods` | no | read | - | yes | n/a | scope |
| `list_interactions` | no | read | - | yes | n/a | item_id |
| `list_inventory` | no | read | - | yes | n/a | container, page, page_size, search |
| `list_labels` | no | read | - | yes | n/a | item_id |
| `list_locations` | no | read | - | yes | n/a | - |
| `list_machines` | no | read | - | yes | n/a | - |
| `list_marks` | no | read | - | yes | n/a | - |
| `list_requests` | no | read | - | yes | n/a | - |
| `list_screenshots` | no | read | - | yes | n/a | limit |
| `list_services` | no | read | - | yes | n/a | - |
| `list_signs` | no | read | - | yes | n/a | - |
| `list_tickets` | no | read | - | yes | n/a | - |
| `list_upgrades` | no | read | - | yes | n/a | - |
| `list_wine` | no | read | - | yes | n/a | - |
| `lottery_status` | no | read | - | yes | n/a | - |
| `machine_grow_status` | no | read | - | yes | n/a | machine_id |
| `machine_status` | no | read | - | yes | n/a | machine_id |
| `phone_state` | no | read | - | yes | n/a | - |
| `ping` | no | read | - | yes | n/a | - |
| `read_book` | no | read | - | yes | n/a | book_id, page |
| `read_item` | no | read | - | yes | n/a | item_id |
| `security_status` | no | read | - | yes | n/a | - |
| `wait_for_change` | no | read | - | yes | n/a | since_rev, timeout_s |
| `wait_for_events` | no | read | - | yes | n/a | since_rev, timeout_ms |
| `wait_until` | no | read | - | no | n/a | condition, since_rev, timeout_s |
| `wanted_status` | no | read | - | yes | n/a | - |
| `wine_status` | no | read | - | yes | n/a | item_id |
| `blur_new_game_option` | yes | ui | required | yes | partial | id, rev |
| `focus_new_game_selected_perk` | yes | ui | required | yes | partial | id, rev |
| `new_game_continue` | yes | ui | required | yes | partial | rev |
| `new_game_next_page` | yes | ui | required | yes | partial | direction, rev, row |
| `open_new_game_menu` | yes | ui | required | yes | partial | rev |
| `select_new_game_option` | yes | ui | required | yes | partial | id, kind, rev |
| `act` | yes | low | required | no | partial | action, action_id, args, rev |
| `activate_item` | yes | low | required | yes | partial | item_id, rev |
| `advance_dialogue` | yes | low | required | yes | yes | rev |
| `analyze_item` | yes | low | required | yes | no | analyzer_id, rev, target_id |
| `barter_accept` | yes | low | required | yes | no | rev |
| `call` | yes | low | required | yes | no | number, rev |
| `call_next_customer` | yes | low | required | yes | yes | rev |
| `choose_dialogue` | yes | low | required | yes | yes | index, rev |
| `end_call` | yes | low | required | yes | no | rev |
| `end_day` | yes | low | required | yes | yes | rev |
| `go_out` | yes | low | required | yes | no | location, rev |
| `move_item` | yes | low | required | yes | no | item_id, rev, rotate, target, turns, x, y |
| `open_shop` | yes | low | required | yes | yes | rev |
| `phone_advance` | yes | low | required | yes | no | rev |
| `phone_choose` | yes | low | required | yes | no | index, rev |
| `return_home` | yes | low | required | yes | no | rev |
| `scavenge` | yes | low | required | yes | no | rev |
| `set_label` | yes | low | required | yes | no | category, condition_id, item_id, rev |
| `set_pen_mode` | yes | low | required | yes | partial | mode, pen_id, rev |
| `start_fermentation` | yes | low | required | yes | no | item_id, rev |
| `toggle_item` | yes | low | required | yes | partial | item_id, rev |
| `wine_add_water` | yes | low | present | no | partial | item_id, ml, rev |
| `advance_expedition` | yes | medium | required | yes | no | expedition_id, rev |
| `arrest_wanted` | yes | medium | required | yes | no | id, rev |
| `bargain` | yes | medium | required | yes | no | direction, percent, rev |
| `buy_upgrade` | yes | medium | required | yes | no | id, rev |
| `buy_wholesale` | yes | medium | required | yes | no | rev |
| `commissary_buy` | yes | medium | required | yes | no | identifier, rev |
| `confirm_trade` | yes | medium | required | yes | yes | item_id, rev, side |
| `deliver_request` | yes | medium | required | yes | no | id, rev |
| `dispatch_expedition` | yes | medium | required | yes | no | hireling_id, location_id, rev |
| `machine_collect` | yes | medium | required | yes | no | machine_id, rev, slot |
| `machine_insert` | yes | medium | required | yes | no | item_id, machine_id, rev, slot |
| `redeem_ticket` | yes | medium | required | yes | no | item_id, rev |
| `report_client` | yes | medium | required | yes | no | rev |
| `toggle_service` | yes | medium | required | yes | no | id, on, rev |
| `use_tool` | yes | medium | required | yes | no | rev, target_id, tool_id |
| `wine_add_ingredient` | yes | medium | required | yes | no | bottle_id, item_id, rev |
| `confirm_new_game` | yes | high | required | yes | partial | rev |
| `load_game` | yes | high | required | yes | partial | rev, slot |
| `load_recording_game` | yes | high | required | yes | partial | rev |
| `new_game` | yes | high | required | no | partial | rev |
| `press` | yes | high | required | no | partial | control_id, rev |
| `save_game` | yes | high | required | yes | partial | rev |
| `save_recording_game` | yes | high | required | yes | partial | rev |
| `sleep` | yes | high | required | yes | yes | rev |
