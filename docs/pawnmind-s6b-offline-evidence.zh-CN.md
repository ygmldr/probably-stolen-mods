# PawnMind S6B：AI 宿主接入与 fake 游戏端到端（离线证据）

**边界**：本轮没有启动游戏、没有打开菜单、没有调用真实 `/action`、没有安装或运行 OpenCode、没有联网、没有创建/读取/写入/复制/删除任何真实存档，也没有改动 Steam、`manifest.xml`、`dist/`、`build.sh` 或任何 C# 文件。所有验证均为 loopback 假桥 + 真 MCP stdio + 本地 Python 测试。

**结论先说**：

- ✅ **脚本策略**（确定性脚本，不是模型）在 **fake 游戏**上走完了：新局菜单四阶段（含翻页、撤销、禁用项）→ 绑定录制槽 → 7 天买卖与跨日 → 资金不足付租被拒 → 赚钱付租回执 → 存读档核对。共 **120** 次 MCP 调用。
- ✅ Python 全量 **60** 项通过、**0** skip（原 30 + 本轮 30）。
- ❌ 这**不是**“AI 已会玩”：没有安装 OpenCode、没有调用任何真实模型、没有接触真实游戏。真实宿主/模型/真机表现全部留 S8（见第 8 节）。

---

## 1. 交付物

| 文件 | 行数 | 作用 |
|---|---|---|
| [`agent/fake_game.py`](../PawnMind/agent/fake_game.py) | 1573 | 127.0.0.1 临时端口假游戏桥（测试替身）：`/health`、`/state`、`/action`、菜单、录制槽、首周、存读档 |
| [`agent/demo_agent.py`](../PawnMind/agent/demo_agent.py) | 805 | 脚本化“AI”策略：真 MCP stdio 客户端 + 只允许 19 个工具 + rev/action_id 纪律 + 停机语义 |
| [`agent/tests/test_fake_game_contract.py`](../PawnMind/agent/tests/test_fake_game_contract.py) | 736 | 假桥契约测试（21 项） |
| [`agent/tests/test_demo_agent_e2e.py`](../PawnMind/agent/tests/test_demo_agent_e2e.py) | 277 | 端到端 + 停机条件测试（7 项） |
| [`agent/tests/test_mcp_entrypoint.py`](../PawnMind/agent/tests/test_mcp_entrypoint.py) | 89 | 宿主入口 `python -m agent.mcp_server` 测试（2 项） |
| [`docs/pawnmind-ai-host-setup.zh-CN.md`](pawnmind-ai-host-setup.zh-CN.md) | 280 | 宿主接入说明（OpenCode 模板、模型、权限、自检、停止条件） |
| `agent/mcp_server.py` | +9 | 仅为 `python -m agent.mcp_server` 增加模块目录 bootstrap；工具面仍是 100 个，未改任何工具语义 |

**这是测试替身，不是游戏模拟器。** `fake_game.py` 头部与 `/health` 都写明 `fake: true`、`not the real game`；它不加载 Unity/程序集/存档，经济是罐头数据，**不得用于真机录制**。

---

## 2. 假桥与 C# 语义对照

JSON 键名与错误码取自 `src/Bridge/*`（`NewGameMenuService`、`NewGameMenuJson`、`RecordingSaveService`、`FirstWeekJson`、`FirstWeekService`、`ActionRouter`、`StateSnapshot`）。

| 契约 | C# 依据 | fake 行为 | 测试 |
|---|---|---|---|
| 只监听 loopback | `BridgeServer` | 绑定 `127.0.0.1:0`，随机端口 | `test_fake_only_listens_on_loopback…` |
| `/health` 免 token | `BridgeServer` | 返回 `{ok,name,fake,mode,note}` | 同上 |
| 其余需 Bearer | `TokenAuth` | 缺/错 token → 401 | `test_state_and_actions_require…` |
| 写需 `action_id` | `ActionGate` | 缺失 → `missing_action_id`；同 id 同参 → 录像回放；同 id 异参 → `action_conflict` | `test_action_id_is_idempotent…` |
| 写需当前 `rev` | `ActionRouter` | `missing_rev` / `bad_rev`（含 bool/字符串/负数）/ `stale_rev`，且不改状态 | `test_writes_require_action_id…` |
| 白名单外的 op | `ActionRouter` | `unknown_op`（`press`/`act`/`new_game`/`load_game`/`save_game`） | `test_unknown_or_forbidden_ops…` |
| 菜单 4 阶段 | `NewGameMenuPhase` | `closed → specialization → perks → difficulty` | `MenuContractTests` |
| 可见选项 | `NewGameMenuViewBuilder` | `options[].{id,display_name,description,cost,max_slot,selected,enabled,lock_reason,hovered}`；隐藏项不出现 | `test_scenario_phase_paginates…` |
| 分页 | `NewGameMenuService.NextPage` | `page/page_count/has_previous_page/has_next_page`；越界 `page_out_of_range`；非 perks 用 selected 行 `wrong_row` | 同上 + `test_selected_row_paging…` |
| continue 状态 | `ButtonJson` | `continue.{label,visible,enabled}`；closed 时 `{}` | 同上 |
| 非法选择 | `NewGameMenuRules` | `option_hidden` / `option_locked` / `option_not_on_page` / `unknown_option` / `wrong_phase` / `already_selected` / `insufficient_points` | 同上 |
| 撤销三步 | S3B hover 语义 | 已选中卡被同 id 候选卡遮住 → `ambiguous_target`；候选卡在 hover 时 focus → `hover_conflict`；`blur → focus → select` 才能撤销 | `test_perks_phase…` |
| 难度页 continue | `NewGameMenuErrors.DifficultyStartForbidden` | `new_game_continue` → `difficulty_start_forbidden` | 同上 |
| 录制三态 | `RecordingSaveService.StatusJson` | `disabled`（error `recording_unconfigured`）/ `armed`（`recording_not_bound`，无 `binding`）/ `bound`（`binding{slot,run_id,run_number,start_type,start_scenario_id}`） | `RecordingStatusContractTests` + `StartGameContractTests` |
| 状态白名单 | 同上 | 仅 `recording_mode/configured_slot/bound/binding/slot_matches/run_matches/last_save_time/error`，无其它槽/预览/路径 | `test_status_never_exposes…` |
| 一次新局 | `ConfirmNewGame` + `InitialSave` | difficulty 页 + 已选难度才允许；`recording_slot_not_next_free` 在原生 Start 前拒绝（不写盘）；成功后返回 `start_requested`/`bound`/`binding.slot`，`/state` 反映 `in_game`、`day`、`cash`、`rent`（真机 `/state` 本来就不含槽号，fake 保持一致，槽由 status 断言） | `test_confirm_new_game_binds…` / `test_slot_allocation_mismatch…` |
| 买/卖原子 | `confirm_trade` | 现金/库存同时更新；余额不足 `cannot_afford` 且零变化；`item_not_found`/`not_owned`/`already_owned`/`bad_side` | `test_trade_updates_cash_and_inventory_atomically` |
| 房东对话 | `FirstWeekService.ChooseDialogue` + `FirstWeekJson` | 现金不足时付租项 `enabled=false`；`choice_disabled` 且不扣款、不推进、不涨 rev；成功回执 `last_rent_payment`/`rent`/`dialogue_changed` | `test_disabled_rent_choice…` / `test_rent_payment_records_one_receipt…` |
| `end_day` | `FirstWeekService.EndDay` | 关店、`can_end_day=false`、`readback.day_advanced=false`；重复 → `cannot_end_day` | `test_end_day_and_sleep…` |
| `sleep` | `RecordingSaveService.Sleep` | Bound 才允许；跨日、租金倒计时 -1、自动保存 `last_save_time`、`saved=false/slept=true`、`readback.day_advanced=true` | 同上 |
| 回执只记一次 | `RentReceiptTracker` | 状态连读两次 `rent` 完全一致；再选付租 → `no_dialogue`，现金不变 | `test_rent_payment_records…` |
| 存/读 | `save/load_recording_game` | 只操作绑定槽；局内 load → `recording_already_in_game`；菜单下 save → `recording_not_in_game`；load 后现金/日期/已付总额还原、`last_payment` 归零（回执 tracker 重置） | `SaveLoadContractTests` |

### 刻意简化（不得当成游戏事实）

- 交易不经过顾客谈判/议价：买=按当日报价买下柜台物品，卖=按物品可见价值卖出；`buy` 后物品价值切换为可卖价。
- 初始背包为空、租金固定 100、7 天一期、房东在倒计时归零后的次日清晨到访。
- 菜单页面大小 4、特质页 4+2、点数上限 3、基础槽 4（Rev13 风格，但不是从真实 UI 读出来的）。

---

## 3. 端到端路径（fake + 真 MCP stdio）

`test_demo_agent_e2e.test_scripted_agent_plays_the_first_week_over_real_mcp_stdio`：

1. 起 fake（`armed`，槽 17，随机端口）→ 写临时 `bridge.conf` → 以 `sys.executable mcp_server.py --config …` 启动**真实** MCP server → `ClientSession.initialize()`。
2. `ping` → `get_state`（主菜单）→ `get_new_game_options`（`closed`）→ `new_game_continue` 被拒（`menu_closed`）。
3. `open_new_game_menu` → 场景页第 0/2 页；`new_game_next_page(+1)` 翻页、再 `(-1)` 翻回；`(-1)` 越界被拒（`page_out_of_range`）；选 `scavenger` 并回读 `selected_id`。
4. `new_game_continue` → 特质页；选 `tough`（2 点）→ 再点被拒（`ambiguous_target`）→ `blur` → `focus` → 再点撤销（`action=remove`，点数归零）；选 `saving`；翻页 → 选 `handy`（共 2 点、3 张卡）。
5. `new_game_continue` → 难度页；点禁用难度被拒（`option_locked`）；选 `apprentice`；`new_game_continue` → `difficulty_start_forbidden`。
6. `get_recording_save_status`（`armed`，`configured_slot=17`）→ `confirm_new_game` → `start_requested`、`bound`、`binding.slot=17`；`/state`：`in_game=true`、`day=1`、`cash=25`、`rent.due=100`、`days_left=7`。
7. 7 天 ×（买最便宜报价 → 卖回 → `end_day`（`day_advanced=false`）→ `sleep`（`day_advanced=true`，自动保存））；第 1 天额外用买不起的报价验证 `cannot_afford` 后现金/库存/rev 均不变。
8. 第 8 天：`rent.days_left=0`、房东对话、付租项 `enabled=false`；点付租 → `choice_disabled`，现金/rent/对话文本/rev 全不变。
9. 再买卖一轮 → 现金够 → 付租成功：回执 `amount=100`、`cash_before=105`、`cash_after=5`、`rent_days_left_after=7`、`source=dialogue_choice`；连读两次 `rent` 一致；再点 → `no_dialogue`。
10. `end_day` → `sleep` → `/state`：`day=9`、`rent.days_left=6`、`rent.rent_paid_total=100`、`rent_weeks_paid=1`；`get_recording_save_status`：`bound`、槽 17、`slot_matches/run_matches` 为真。
11. `save_recording_game` → `saved=true`、`last_save_time`、`binding` 不变；**宿主步骤**（`HostActions.return_to_menu()`，非 MCP 工具，模拟 S8 的“人把游戏退回主菜单”）→ `load_recording_game` → `/state` 还原 day/cash/已付总额，`rent.last_payment=null`；`binding` 与保存前逐字段一致。
12. 报告：`stop_reason=completed`、120 步 trace、汇总（现金对账 `start − buy + sell − rent = cash`）、trace 内不含 token。

测试对 fake 的请求日志另做断言：每个写动作都带 Bearer、唯一 `action_id`、整数 `rev`；读动作不带 `rev`；出现的 op 全部在 19 个允许项内；`act/press/new_game/load_game/save_game` 从未出现。

### 停机条件（离线可测）

| 场景 | 断言 |
|---|---|
| `stale_rev` | 停在该步，**不重试**（调用序列到 `open_new_game_menu` 为止，只出现一次） |
| `outcome_unknown` | 停，不二次写 |
| 其它意外错误码 | `DemoAbort("unexpected_error", <code>)` |
| allowlist 外工具 | 在任何 MCP 调用前拒绝 |

---

## 4. TDD 证据

### Red

1. 先写 `test_fake_game_contract.py` → `.venv/bin/python -m unittest discover -s agent/tests -p "test_fake_game_contract.py"` → `ModuleNotFoundError: No module named 'fake_game'`。
2. 先写 `test_demo_agent_e2e.py` → `ModuleNotFoundError: No module named 'demo_agent'`。
3. 先写 `test_mcp_entrypoint.py`（在 fake/demo 已实现、`mcp_server.py` 未加 bootstrap 时）：
   - `python -m agent.mcp_server --probe` → `ModuleNotFoundError: No module named 'pawnmind_client'`，退出码 1；
   - stdio 测试 → `MCPError: Connection closed`。
4. 实现过程中的真实失败（首轮 21 项里 5 fail + 1 error），逐个处理：
   - 假桥 `confirm_new_game` 在菜单关闭时返回 `recording_menu_unavailable`，而 C# `RecordingSaveService` 对“可读但非难度页”统一返回 `recording_wrong_menu_phase` → **改 fake**（不是改断言）。
   - 测试数据自身错误：2 点 + 2 点恰好等于上限（改为先花 1+1 再验 `insufficient_points`）；已被买走的报价不能再用于验证 `not_owned`（改用仍在柜台的贵价报价）；`armed` 状态本来就没有 `binding` 键（改为断言子集 + 未绑定无 binding）；“快速推进到房东”漏了交易（补 7 轮买卖）。
5. `fake_game.serve_forever` 默认 0.5s 轮询让 21 项跑了 13.5s → 改 `poll_interval=0.02`，降到 ~1.2s（不改语义）。

### Green（本机可复跑）

```bash
cd /mnt/d/code/game_mods/PawnMind
.venv/bin/python -m unittest discover -s agent/tests -p "test_fake_game_contract.py"   # 21 OK ~1.2s
.venv/bin/python -m unittest discover -s agent/tests -p "test_demo_agent_e2e.py"       #  7 OK ~7.5s
.venv/bin/python -m unittest discover -s agent/tests -p "test_mcp_entrypoint.py"       #  2 OK ~6.9s
.venv/bin/python -m unittest discover -s agent/tests                                  # 60 OK 0 skip ~69s
```

| 套件 | 结果 |
|---|---|
| `test_fake_game_contract` | 21 / 0 fail |
| `test_demo_agent_e2e` | 7 / 0 fail |
| `test_mcp_entrypoint` | 2 / 0 fail |
| **Python 全量** | **60 / 0 fail / 0 skip**（S6A 基线 30 → 现在 60） |

未跑 C#：本轮没有改 `src/`、`test/`、`build.sh`、`manifest.xml`，C# 456 / 脚本 53 / 适配器 392 的结论沿用 S6A 主代理复跑。

---

## 5. 需求对照（S6B 任务卡）

| 要求 | 状态 | 证据 |
|---|---|---|
| `/health`、`/state`、`/action`（Bearer + `action_id` 幂等 + rev） | ✅ | `TransportContractTests` |
| 菜单四阶段、可见项含 `display_name/enabled/selected`、分页与 continue | ✅ | `MenuContractTests` |
| `open_new_game_menu` / `select_new_game_option`（禁用/离页/未知拒绝 + 成功回读） | ✅ | 同上 |
| `new_game_next_page` / `new_game_continue`（difficulty → `difficulty_start_forbidden`） | ✅ | 同上 |
| `confirm_new_game`（Armed→Bound、`start_requested`；`/state` 反映 in_game；绑定槽由 `get_recording_save_status` 断言） | ✅ | `StartGameContractTests` |
| `get_recording_save_status` 未配置/未绑定/Bound 三态 | ✅ | `RecordingStatusContractTests` + `StartGameContractTests` |
| `confirm_trade` 买卖原子、`cannot_afford` 零变化 | ✅ | `TradeContractTests` |
| `choose_dialogue` 租金项资金不足 `enabled=false`、`choice_disabled` 不推进 | ✅ | `FirstWeekContractTests` |
| `end_day`、`sleep`（跨日 + 自动保存 + day/rent 回读） | ✅ | 同上 |
| `rent` 字段 + `last_payment`、同一笔只记一次 | ✅ | 同上 |
| 脚本策略只走 MCP、真 stdio、7 天 + 付租失败/成功 + 存读档 | ✅ | `test_scripted_agent_plays…` |
| 显式声明测试替身、不得用于真机 | ✅ | fake 头部、`/health.fake`、本文件、宿主文档 |

---

## 6. 宿主接入文档

[`pawnmind-ai-host-setup.zh-CN.md`](pawnmind-ai-host-setup.zh-CN.md) 包含：OpenCode MCP 配置模板（stdio、`PYTHONPATH`、`--config` 绝对路径、无 token）、模型选择与订阅不确定性、最小权限建议（19 工具 allowlist、禁 shell/网络/子代理/Code Mode）、开机自检（`ping`、HTTP `/health`、`get_state`、`get_new_game_options`、`get_recording_save_status`）、停机条件与预算护栏、离线演示命令。

文中所有配置键名都标了 **⚠ S8 核对**，并写明“本机未安装 OpenCode、未曾加载验证”。模板里的启动命令形态（`python -m agent.mcp_server --config …`）是**唯一被测试覆盖**的部分。

---

## 7. 已知缺口与不实声明风险

1. **不是 LLM**：`demo_agent.py` 是确定性脚本，决策是写死的偏好列表；它证明的是调用层与工具纪律，不是模型能力。
2. **fake ≠ 游戏**：交易无顾客谈判、无议价、初始背包为空、租金/报价是罐头数字。**不得**用这份证据声称“首周付租已通过”。
3. **未安装/未运行 OpenCode**，模板键名与权限键未验证；模型 ID 与 Go 订阅可用性/条款未验证。
4. **未做服务端工具裁剪**：`mcp_server.py` 仍注册 100 个工具；录制收窄目前只能靠宿主 allowlist + 客户端 `ALLOWED_OPS`。建议 S7/S8 加显式 `--tools recording` 开关（先写测试）。
5. **`days_left` 口径**：脚本用 `rent.days_left == 0` 判断房东到访；S5 已注明 `days_left`（`GetRentDayCounter`）与 `days_until_rent` 在付款瞬间可能不同，真机需以画面/日历核对。
6. **存读档的“回主菜单”是宿主步骤**：脚本通过 `HostActions.return_to_menu()` 等待（测试里由 harness 调 fake）；真实桥没有“回主菜单”工具，S8 必须由人按 runbook 完成，模型只负责随后的 `load_recording_game`。
7. **真机上的初始背包非空**：脚本的现金/库存断言已改成增量口径，但“按 identifier 找到刚买入的那一件”在真实物品堆叠/重名下仍可能失败——失败即停机是预期行为。
8. **WSL/Windows 解释器差异**：`.venv` 是 WSL 的 Linux venv；若宿主跑在 Windows，需要另建 venv，文档已写明。

---

## 8. 留到 S8（离线无法证明）

1. OpenCode 实际版本、配置键名、工具列表、Code Mode/权限是否按预期拒绝 shell/文件/网络。
2. `opencode-go/deepseek-v4.1-flash` 在账号下是否可用、条款是否允许、额度与计费。
3. 真实模型能否只靠可见信息完成首周付租；脚本路径不代表模型能做到。
4. 真实游戏的新局菜单（文本/顺序/禁用态/分页）、`confirm_new_game` 的原生写盘、录制槽绑定回读。
5. 真实桥的长轮询/超时/断线、`outcome_unknown` 后调用层是否真的不重试。
6. 真实 `state.rent.days_left` vs `days_until_rent`、`sleep` 的黑屏协程时序与自动保存完成时刻。
7. 隔离环境、专用槽号、一次新局写盘许可（必须用户批准）。

---

## 9. 未触碰

未启动游戏、未打开菜单、未调用真实 `/action`、未安装/运行 OpenCode、未联网、未写 `dist/`、未改 C#/`build.sh`/`manifest.xml`/`test.sh`/Steam/云同步、未读写任何真实存档、未生成 `_decomp/` 产物。

本轮改动文件：新增 `PawnMind/agent/fake_game.py`、`PawnMind/agent/demo_agent.py`、`PawnMind/agent/tests/test_fake_game_contract.py`、`PawnMind/agent/tests/test_demo_agent_e2e.py`、`PawnMind/agent/tests/test_mcp_entrypoint.py`、`docs/pawnmind-ai-host-setup.zh-CN.md`、本文件；修改 `PawnMind/agent/mcp_server.py`（仅 9 行 import bootstrap）。
