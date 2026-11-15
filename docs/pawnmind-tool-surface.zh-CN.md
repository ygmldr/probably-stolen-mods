# PawnMind 录制工具表（103 个）

本表由 `agent/mcp_server.py --tools recording` 实际注册的集合生成；
「真机」列取自试跑事件库（113 条事件 / 44 个不同 op），`✅ n` 表示真机成功调用过 n 次。

| # | 工具 | 读/写 | 参数 | 作用 | 真机 | 建议 |
|---|---|---|---|---|---|---|
| **观察** | | | | | | |
| 1 | `ping` | 读 | — | Round-trip check used to verify the in-game bridge is alive | ✅ 1 | 可用 |
| 2 | `get_state` | 读 | — |  | ✅ 19 | 可用 |
| 3 | `get_legal_actions` | 读 | — | Return the actions that are legal right now (state-derived,… | — | 可用 |
| 4 | `get_run_summary` | 读 | — | Compact run summary: day, shop state, cash, rent, inventory… | — | 可用 |
| 5 | `get_action_history` | 读 | limit | Recent actions with their args, ok flag and revision | — | 可用 |
| 6 | `wait_for_events` | 读 | since_rev, timeout_ms | Block until the game revision changes | ✅ 2 | 可用 |
| 7 | `wait_for_change` | 读 | since_rev, timeout_s | Block until the shop revision changes (customer arrives, di… | — | 可用 |

| **情报** | | | | | | |
| 8 | `get_day_report` | 读 | — | Read this morning's report lines: power/electricity, insura… | — | 可用 |
| 9 | `get_calendar` | 读 | days | Read the store calendar for the next `days` days (default 7… | — | 可用 |
| 10 | `get_clientele` | 读 | — | What kind of customers the display case attracts: showcase … | — | 可用 |

| **新局菜单** | | | | | | |
| 11 | `get_new_game_options` | 读 | — | Read the current native new-game menu: visible options, cur… | ✅ 1 | 可用 |
| 12 | `open_new_game_menu` | 写 | rev | Open the native new-game menu (one UI step) | ✅ 1 | 可用；未真机验证 → 首次按探路规则单步试 |
| 13 | `select_new_game_option` | 写 | kind, id, rev | Select one scenario/perk/difficulty card in the open menu (… | ✅ 6 | 可用；未真机验证 → 首次按探路规则单步试 |
| 14 | `new_game_next_page` | 写 | row, direction, rev | Turn one new-game page: row=available|selected, direction=-… | ✅ 4 | 可用；未真机验证 → 首次按探路规则单步试 |
| 15 | `new_game_continue` | 写 | rev | Continue one intermediate new-game page | ✅ 2 | 可用；未真机验证 → 首次按探路规则单步试 |
| 16 | `focus_new_game_selected_perk` | 写 | id, rev | Move native focus/hover onto one already-selected perk | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 17 | `blur_new_game_option` | 写 | id, rev | Trigger one native pointer-exit on the currently hovered pe… | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 18 | `confirm_new_game` | 写 | rev | Grant exactly one native new-game write permission for the … | ✅ 1 | ⚠️ 整局只调一次 |

| **取证** | | | | | | |
| 19 | `capture_screenshot` | 读 | reason | Capture the current game frame as evidence (JPEG, short sid… | — | 可用 |
| 20 | `list_screenshots` | 读 | limit | List recent evidence captures, newest first, with total_cou… | ✅ 1 | 可用 |

| **弹窗** | | | | | | |
| 21 | `get_pending_prompt` | 读 | — | Read the native confirmation modal that currently owns the … | — | 可用 |
| 22 | `answer_prompt` | 写 | choice, Field(
            description, json_schema_extra, "cancel"]})], rev | Press exactly one button of the open modal | — | 默认 cancel；先读 get_pending_prompt |

| **界面** | | | | | | |
| 23 | `close_windows` | 写 | rev | Close the note/book/contract/deed panels and hide floating … | — | 读完笔记/书就调；end_day 前必调 |

| **开店与客流** | | | | | | |
| 24 | `open_shop` | 写 | rev | Open the shop shutter | ✅ 2 | 可用；未真机验证 → 首次按探路规则单步试 |
| 25 | `call_next_customer` | 写 | rev | Call the next customer to the counter | ✅ 2 | 可用；未真机验证 → 首次按探路规则单步试 |
| 26 | `advance_dialogue` | 写 | rev | Advance the current non-choice dialogue line | ✅ 11 | 可用；未真机验证 → 首次按探路规则单步试 |
| 27 | `choose_dialogue` | 写 | index, rev | Pick a dialogue choice by index (0-based) | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 28 | `end_day` | 写 | rev | Close the day when the game allows it | ✅ 1 | 可用；未真机验证 → 首次按探路规则单步试 |
| 29 | `sleep` | 写 | rev | End the day: close the shutter, settle night services, save… | ✅ 4 | 可用；未真机验证 → 首次按探路规则单步试 |

| **交易与议价** | | | | | | |
| 30 | `confirm_trade` | 写 | item_id, side, rev | Buy (side=buy) or sell (side=sell) one item currently on th… | ✅ 1 | 可用；未真机验证 → 首次按探路规则单步试 |
| 31 | `list_goods` | 读 | scope | List goods on the front/back counters: scope=client (non-ow… | ✅ 1 | 可用 |
| 32 | `get_item` | 读 | item_id | Full visible details for one item id: container, value, cou… | ✅ 1 | 可用 |
| 33 | `buy_wholesale` | 写 | rev | Buy the current client's whole wholesale offer (requires is… | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 34 | `commissary_buy` | 写 | identifier, rev | Buy one item from the supply station (commissary) by identi… | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 35 | `bargain_preview` | 读 | direction, percent | Preview one bargain: success/leave/refuse chance, price bef… | ✅ 1 | 可用 |
| 36 | `bargain` | 写 | direction, percent, rev | One bargain offer: direction is sell_discount|sell_markup|b… | — | 可用；未真机验证 → 首次按探路规则单步试 |

| **背包与搬运** | | | | | | |
| 37 | `list_containers` | 读 | — | List player containers (main/front/back/showcase/hidden/aft… | ✅ 2 | 可用 |
| 38 | `list_inventory` | 读 | container, search, page, page_size | Paginated inventory for one container | — | 可用 |
| 39 | `move_item` | 写 | item_id, target, rev, x, y, rotate, turns | Move one item | ✅ 3 | 可用；未真机验证 → 首次按探路规则单步试 |
| 40 | `get_layout` | 读 | container | Return the grid layout (width/height, rows of item ids) and… | ✅ 2 | 可用 |
| 41 | `read_item` | 读 | item_id | Read one item's visible details plus book/document flags | ✅ 1 | 可用 |
| 42 | `list_interactions` | 读 | item_id | List available interactions for one item: activate/toggle/t… | ✅ 1 | 可用 |
| 43 | `activate_item` | 写 | item_id, rev | Activate an owned item (books, lottery tickets, scanners) | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 44 | `toggle_item` | 写 | item_id, rev | Toggle an owned item (gene scanner, machine switches) | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 45 | `use_tool` | 写 | tool_id, target_id, rev | Use an owned tool on a target item (screwdriver, UV, scanne… | ✅ 1 | 可用；未真机验证 → 首次按探路规则单步试 |

| **鉴定与标签** | | | | | | |
| 46 | `list_analyzers` | 读 | — | List owned analyzers (water strip/aquascan, chem scanner, p… | ✅ 1 | 可用 |
| 47 | `analyze_item` | 写 | analyzer_id, target_id, rev | Run an analyzer on a target and return its visible result f… | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 48 | `set_pen_mode` | 写 | pen_id, mode, rev | Set marking pen mode: 0 red, 1 purple, 2 green, 3 blue, 4 o… | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 49 | `list_marks` | 读 | — | List marked fish/animals with their color | — | 可用 |
| 50 | `list_labels` | 读 | item_id | List editable label categories and conditions for an item | ✅ 1 | 可用 |
| 51 | `set_label` | 写 | item_id, category, condition_id, rev | Set one editable label condition on an item | — | 可用；未真机验证 → 首次按探路规则单步试 |

| **书本** | | | | | | |
| 52 | `list_books` | 读 | — | List owned/unlocked books (vanilla BOOK_TAG and paper GUIDE… | ✅ 1 | 可用 |
| 53 | `read_book` | 读 | book_id, page | Read an owned book by item id or book id | ✅ 1 | 可用 |

| **机器与生产** | | | | | | |
| 54 | `list_machines` | 读 | — | List owned standard machines with state, progress and slots | ✅ 1 | 可用 |
| 55 | `machine_status` | 读 | machine_id | Show one machine's slots and contents | — | 可用 |
| 56 | `machine_grow_status` | 读 | machine_id | Hydroponic grow status: power/water/seed/progress | — | 可用 |
| 57 | `machine_insert` | 写 | machine_id, slot, item_id, rev | Insert an inventory item into a machine slot | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 58 | `machine_collect` | 写 | machine_id, slot, rev | Move items from a machine slot back to the backpack | — | 可用；未真机验证 → 首次按探路规则单步试 |

| **酿酒** | | | | | | |
| 59 | `list_wine` | 读 | — | List owned wine containers/bottles and finished state | ✅ 1 | 可用 |
| 60 | `wine_status` | 读 | item_id | Wine bottle fermentation/quality details | — | 可用 |
| 61 | `wine_add_ingredient` | 写 | bottle_id, item_id, rev | Move a berry/yeast into a wine bottle's ingredient grid | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 62 | `start_fermentation` | 写 | item_id, rev | Try to start fermentation on a wine bottle | — | 可用；未真机验证 → 首次按探路规则单步试 |

| **自动售货** | | | | | | |
| 63 | `list_upgrades` | 读 | — | List network/store upgrades with cost/unlock/prerequisite s… | ✅ 1 | 可用 |
| 64 | `buy_upgrade` | 写 | id, rev | Buy a network upgrade by id (also unlocks supplier phone co… | — | 可用；未真机验证 → 首次按探路规则单步试 |

| **彩票** | | | | | | |
| 65 | `lottery_status` | 读 | — | Lottery unlock state, edition, day and prize table | ✅ 1 | 可用 |
| 66 | `list_tickets` | 读 | — | List owned lottery tickets with number/edition/matches/prize | ✅ 1 | 可用 |
| 67 | `inspect_ticket` | 读 | item_id | Inspect one lottery ticket | ✅ 1 | 可用 |
| 68 | `redeem_ticket` | 写 | item_id, rev | Redeem a winning lottery ticket | — | 可用；未真机验证 → 首次按探路规则单步试 |

| **电话** | | | | | | |
| 69 | `list_contacts` | 读 | include_hidden | List phone contacts, state, cooldown and spend | ✅ 1 | 可用 |
| 70 | `call` | 写 | number, rev | Open the phone and dial a contact | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 71 | `phone_state` | 读 | — | Current phone call state and dialogue | — | 可用 |
| 72 | `phone_advance` | 写 | rev | Advance the phone dialogue by one step | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 73 | `phone_choose` | 写 | index, rev | Choose a phone dialogue option by index | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 74 | `end_call` | 写 | rev | End the current phone call | — | 可用；未真机验证 → 首次按探路规则单步试 |

| **夜间** | | | | | | |
| 75 | `list_locations` | 读 | — | List discovered night locations (dumping ground / commissar… | ✅ 1 | 可用 |
| 76 | `go_out` | 写 | location, rev | Leave the shop and travel to a night location | ✅ 1 | 可用；未真机验证 → 首次按探路规则单步试 |
| 77 | `scavenge` | 写 | rev | Scavenge at the dumping ground | ✅ 1 | 可用；未真机验证 → 首次按探路规则单步试 |
| 78 | `return_home` | 写 | rev | Return from a night location to the shop | ✅ 2 | 可用；未真机验证 → 首次按探路规则单步试 |

| **治安与违禁** | | | | | | |
| 79 | `contraband_status` | 读 | — | Owned contraband/stolen items with levels and risk flags | — | 可用 |
| 80 | `security_status` | 读 | — | Security evidence, crimes, case officer and current inspect… | — | 可用 |
| 81 | `wanted_status` | 读 | — | Wanted-person visit state and wanted list | — | 可用 |
| 82 | `report_client` | 写 | rev | Report the current customer (any reportable client | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 83 | `arrest_wanted` | 写 | id, rev | Report/arrest a wanted person by id | — | 可用；未真机验证 → 首次按探路规则单步试 |

| **委托与声望** | | | | | | |
| 84 | `list_requests` | 读 | — | Active customer/faction requests and rewards | ✅ 1 | 可用 |
| 85 | `deliver_request` | 写 | id, rev | Deliver a fillable StoreRequest and claim rewards | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 86 | `list_factions` | 读 | — | Faction reputation amounts, influence and every reputation … | — | 可用 |
| 87 | `barter_status` | 读 | — | Current client's barter offer and conditions | — | 可用 |
| 88 | `barter_accept` | 写 | rev | Accept the current client's barter offer when acceptable | — | 可用；未真机验证 → 首次按探路规则单步试 |

| **其它** | | | | | | |
| 89 | `list_services` | 读 | — | Nightly services (insurance, janitorial, power, informant) … | ✅ 1 | 可用 |
| 90 | `toggle_service` | 写 | id, on, rev | Toggle a nightly service on/off | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 91 | `husbandry_status` | 读 | — | Owned animals/fish with health/dead state | — | 可用 |
| 92 | `expedition_status` | 读 | — | Expedition unlock, locations, hirelings and ongoing runs | — | 可用 |
| 93 | `dispatch_expedition` | 写 | hireling_id, location_id, rev | Send a hireling to an unlocked expedition location | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 94 | `advance_expedition` | 写 | expedition_id, rev | Advance an ongoing expedition one step | — | 可用；未真机验证 → 首次按探路规则单步试 |
| 95 | `events_status` | 读 | — | Active and queued store events | — | 可用 |
| 96 | `list_signs` | 读 | — | Owned marketing signs | — | 可用 |
| 97 | `health_status` | 读 | — | Player wounds, luck, addiction timers | — | 可用 |

| **新局与存档** | | | | | | |
| 98 | `get_recording_save_status` | 读 | — | Read-only status for the dedicated recording slot | ✅ 3 | 可用 |
| 99 | `save_recording_game` | 写 | rev | Save once through the native save | — | ⚠️ 真写盘；UI 无手动存档，日常靠 sleep 自动存；只在要固定检查点时用 |
| 100 | `load_recording_game` | 写 | rev | Load the bound dedicated recording slot once | — | ⚠️ 必须人工先退回主菜单；只读绑定槽 |
| 101 | `save_game` | 写 | rev | Save the current run | — | ⚠️ 真写盘；UI 无手动存档，日常靠 sleep 自动存；只在要固定检查点时用 |
| 102 | `load_game` | 写 | slot, rev | Load a save slot from the main menu (slot ids are listed in… | — | ⚠️ 必须人工先退回主菜单；只读绑定槽 |

| **开局辅助** | | | | | | |
| 103 | `dismiss_tutorial_hints` | 写 | rev | Hide the tutorial hint bubbles only: turns PlayerStore.tuto… | — | 开局调一次 |

