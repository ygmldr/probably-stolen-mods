# PawnMind · 深空当铺 AI 接口

给《深空当铺：可能是偷的》(Probably Stolen 0.46D) 做的本地 MCP 桥。
当前是 **M0**：只读状态 + 本机 HTTP，不含任何经营动作。

> 另有一个第三方实验项目 `probably-stolen-mcp-releases`（端口 15527）。
> 本项目默认端口 **15528**，两者可以共存，但不要同时启用。

## 现在能做什么

- 游戏内 Mod 启动一个只监听 `127.0.0.1` 的 HTTP 服务
- `GET /health`：存活检查（无需 token）
- `GET /state`：当前可见状态快照（需要 token）
  - 现金、房租倒计时、开局日、顾客是否到场
  - 当前顾客：名字、意图、现金、耐心、是否可砍价、阵营
  - 当前议价物品：名字、identifier、数量、当前估值
- `POST /action`：目前只支持 `{"op":"ping"}`，用于验证往返

M1（v0.2.0）新增动作：`open_shop`、`call_next_customer`、`advance_dialogue`、`choose_dialogue`、`end_day`、`bargain`，全部带 `rev` 校验和 `action_id` 幂等。

仍然**不读**隐藏字段（鉴定答案、隐藏身份），也还没有实现买卖成交（`confirm_trade` 在 M1.1）。

## 真机验证（2026-11-04，0.46D）

v0.2.0 在真实存档上跑通：

```
初始 day=1 shop=MORNING cash=550
open_shop          → shop=OPEN  can_call_next=true
call_next_customer → 顾客=拾荒客，front_counter 12 件（11 件非玩家所有）
advance_dialogue   → 对话结束，endAction 把货物摆上前台
confirm_trade(id=111, side=buy)
                   → cash 550→538，front 12→11，物品从柜台消失
```

- 已验证：`open_shop`、`call_next_customer`、`advance_dialogue`、`confirm_trade(buy)`
- 待验证：`choose_dialogue`（选项）、`bargain`、`sell`、`end_day`
- 已确认的坑：`PlayerStore.OpenShutter()` 只改状态，必须走 `StoreShutterButton.OpenShutter()` 才会生成当日顾客；顾客货物由对话 `endAction` 摆到前台

## 夜间三地点（2026-11-10）

真机：

```
list_locations -> 垃圾倾倒场 / 配给站 / 杰克逊博士
go_out("dumping_ground") -> outside=true
scavenge -> 报废模组 x1，剩余 4 次，小伤 6% / 大伤 1.25%
move_item(55, main) -> 背包
return_home -> outside=false
go_out("dr_jackson") -> 25 件柜台货
go_out("commissary") -> 7 种配给 + redeemed_credit=0
commissary_buy -> insufficient_credit
```

- `list_locations` / `go_out` / `scavenge` / `return_home`
- `list_goods(scope=jackson)` 返回博士柜台货
- 补给站用 `redeemed_credit`（回收代币），不是现金；0 代币时 `insufficient_credit`
- `return_home` 是异步的，响应给 `return_requested`，用 `/state.outside` 确认

## 商品目录扩展（2026-11-10）

- `list_goods(scope=commissary)`：补给站库存（identifier/名称/价格/库存），真机 7 种货、库存 4–8
- `list_goods(scope=jackson)`：只在 `dr_jackson` 且 outside 时返回柜台货，否则 `not_at_location`
- `list_goods(scope=wholesale)`：只有当前顾客 `isOfferWholesale` 时返回，含折扣/整包价
- `commissary_buy(identifier)`：补给站购买；店内 `CanBuy=false` 返回 `cannot_buy`（需夜间到补给站）

## 砍价预览（2026-11-10）

`bargain_preview(direction, percent)` 返回：

- `success_chance / leave_chance / refuse_chance`（游戏原值）
- `price_before / price_after`
- `rep_multiplier`、`rep_per_1000`（倍率 × 2.5 × 阵营系数）、`rep_estimate_exact`
- 真机：拾荒客 Lower Level，`sell_markup 25` -> 50/20/30，`rep_per_1000=-2.48`

声望是**估算**（未计难度/Perk 的最终修正）；成交仍走游戏 `bargain`。

## 商品目录（2026-11-10）

- `list_goods(scope=client|sellable|all)`：前台/后台商品 + 当前顾客（名字/意图/预算/是否可砍价）
- 真机：拾荒客 SELL → `client` 返回 8 件货（id/名称/价格/container），买一件后 cash 550→538、front 9→8、库存 14→15
- 补给站/博士/批发 scope 尚未接

## 自动摆放失败重排（2026-11-10）

- 新增 `RepackSolver`：带 4 向旋转、阻挡格、节点上限的回溯 packer（纯逻辑，4 项单测）
- `move_item` 自动摆放失败时：收集目标容器可移动物品 + 新物品，重排；成功则逐个 `UncheckedAccept`，失败整体回滚到原父容器/原形状
- 不依赖 CompactSort，避免跨 Mod 反射/能力声明
- 真机：展示柜 7x5 摆成 6x4 满块（3x3 + 两个 3x2 + 3x1），再自动放入 2x2；重排后 5 件全部落位，布局无重叠

## 整包二维布局（2026-11-10）

`get_layout(container)` 现在返回：

- `width/height`：格子尺寸
- `rows`：每格最上层 item id（0 为空）
- `display`：每格一个符号的二维图，`.` 为空（LLM 直接看）
- `symbols`：`id -> 符号`
- `cells`：`[{x,y,id,layer}]`，保留叠层
- `items`：物品列表（含 `mask`/`cells`）

实测 24x10 背包：

```
 0 AAABBCCDDEEEFFFGGHHIIJJK
 1 AAABBCCDDLLLMM.GGHHIIJJK
 2 AAANNN.DDLLLMM..........
 3 ...NNN...LLLMM..........
 4 ........................
 ...
```

## 形状表示：不是矩形（2026-11-10）

- **位置**：`modifiedShape.minX/minY`（包围盒左上角）
- **宽高**：`globalWidth/globalHeight`，只是包围盒
- **占格/碰撞**：真实 `GridShape` 的逐格 `Get(x,y)`（带 layer），不是矩形
- **接口**：`list_inventory`/`get_item` 新增 `mask`（每行 `'.'` 空、`1..9` 层号）和 `cells`（x/y/layer）
- **布局**：`get_layout` 按真实格子填 item id，0 为空
- **放置**：`move_item(x,y,rotate)` 用 `GridShapeBuilder` + `CheckShapePlacement`，异形容器/旋转都按真实形状校验

## 真机验证：指定格子 + 旋转 + 布局（2026-11-10）

在全新测试档上：

```
背包网格 24x10
腰包 id=43 原位置 (3,2) 3x2
move_item(43, main, x=22, y=7, rotate=true)
-> 变 2x3，位于 (22,7)
get_layout 行 7..9 的列 22..23 都是 43
```

新增：

- `move_item` 支持 `x/y/turns=0..3`（0/90/180/270）；同容器内重排也允许
- `get_layout(container)`：返回网格宽高 + 每格 item id 的二维数组 + 物品列表
- `list_inventory` 的 `x/y` 改为真实 `modifiedShape.minX/minY`（之前误用了 transform origin，全是 0）

## 真机验证：背包与移动（2026-11-10）

在全新档上实测：

```
list_containers -> main=14, 其余 0
list_inventory(main, page=1, size=5) -> 14 件，含 id/名称/坐标/尺寸
get_item(12) -> 储藏区
move_item(12, showcase) -> ok
list_containers -> main=13, showcase=1
move_item(12, showcase) -> same_container（拒绝）
```

- `debug_pulse` / `debug_dump` HTTP 动作可用；`debug_dump` 写出 `ModData/pawnmind/debug-state.json`
- **不再注入任何系统按键**；F7 只保留为游戏窗口聚焦时的人工快捷键
- `debug_pulse` 的日志行这次没出现在 `Player.log`（dump 文件正常），下次切 ModLog/文件确认

## 游戏内调试快捷键（2026-11-10）

- **F7**：打一条诊断到 `Player.log`：`[PawnMind] debug rev=... day=... shop=... cash=... client=... legal=...`
- **Shift+F7**：把完整 `/state` 写入 `ModData/pawnmind/debug-state.json`
- 实现在 `MainThreadPump.Update()` 里轮询 `Input.GetKeyDown`，不建线程/协程，兼容新扫描器
- 纯逻辑 `HotkeyLatch`（边沿 + 0.4s 冷却）有单测；避开 F8 控制台 / F9 开发菜单

## v0.4 正常经营：背包与移动（2026-11-10）

新增：

- `list_containers`：main/front/back/showcase/hidden/afterhour/trash/sold + 数量
- `list_inventory(container, search, page, page_size)`：分页/搜索，返回 id/名称/估值/数量/归属/锁定/格子坐标/尺寸
- `get_item(item_id)`：跨容器查一件物品的完整可见信息
- `move_item(item_id, target)`：移动到目标容器自动摆放；锁定物品/锁定容器/同容器/无空间拒绝

测试：C# **71 项** + 适配器契约全绿；Python 8 项全绿。

待做：指定格子+旋转、买卖目录、夜间、阅读、砍价声望预估。

## 扫描器兼容：去掉后台线程（2026-11-09）

游戏的新扫描器把 `threads` 列为 **retiring**（下个版本直接拒绝），命中项包括：

- `new Thread` / `Thread.Start`
- `ThreadPool.QueueUserWorkItem` 等
- `Task.Run` / `Task.Start` / `Task.Delay` / `TaskFactory.StartNew`
- `Timer` / `Parallel`
- `MonoBehaviour.StartCoroutine` / `Invoke` / `InvokeRepeating`

PawnMind 原来用后台线程 + `ThreadPool` 处理 HTTP，所以 Mods 菜单出现警告三角。现已改成：

- `TcpListener` 非阻塞 + `Update()` 里 `Tick()` 轮询
- 请求解析、动作执行、响应全部在 Unity 主线程完成
- 删除 `Thread` / `ThreadPool` / `Task` / 协程

重新构建后，Mods 菜单里 PawnMind 的警告三角已消失，`/state`、`act`、`press` 均正常。

> 紧凑整理（CompactSort）仍有同样警告：它用了协程/`Invoke`，同样需要改。

## v0.3：通用操作面（2026-11-09）

新增 7 个能力，已在新档原谅上验证：

- `legal_actions`：`/state` 直接给出当前合法动作 + 参数（菜单、开店、叫客、对话、买卖、砍价、睡觉、存档）
- `act(action, args, rev, action_id)`：通用入口，内部复用现有语义动作与 revision/幂等
- `get_ui_snapshot`：列出当前可点的 `TabletButton` / `Button`（id、文本、是否可用、层级路径）
- `press(control_id, rev)`：直接调用按钮的 `onClick`，不依赖系统鼠标注入；id 带快照 token，过期返回 `stale_snapshot`
- `get_action_history(limit)`：最近动作 + 参数 + ok/error + rev
- `get_run_summary`：日/店态/现金/房租/库存/当前顾客
- `wait_until(condition, timeout)`：等 `client_arrived`、`can_call_next`、`dialogue_choice`、`shop_open` 等条件

MCP 工具共 21 个。测试：C# 59 + 适配器契约全绿，Python 8 项全绿。

已验证片段：

```
menu legal = [new_game, load_game]
act new_game -> day 1 MORNING cash 550 inventory 14
ui snapshot -> [('b4ee14e27:0', '关闭提示')]
press 过期 id -> stale_snapshot
legal = [open_shop, save_game]
```

## 完整一天验证（2026-11-05）

在一个**全新存档**（`new_game`，APPRENTICE，未触碰任何旧档）上跑通：

```
day=1 MORNING cash=550 inventory=14
open_shop          → OPEN
call_next_customer → 拾荒客
advance_dialogue   → 对话结束，前台 10 件货
confirm_trade(buy id=57) → cash 535, front 9, inventory 15（物品真的进仓库）
sleep              → day=2 MORNING, cash 530, rent_days_left 5, inventory 15
```

`sleep` 走的是游戏完整的 `EndOfDayUIManager.OnEndDayButtonClick()`：关卷帘门 → 夜间服务结算 → `EndNight` → `BeginDay` → 自动存档 → 早晨报告。

新增动作：`new_game`、`load_game(slot)`、`save_game`、`sleep`。

## v0.2.1（离线迭代）

- `/state` 新增：`inventory`（购买后回读）、`dialogue.choices`（选项文本+索引+关联物品）、`bargain`（面板状态/可用方向/已用档位）、`client.budget`、柜台物品 `sellable`
- 新增 `save_game` 动作（`PlayerStore.SaveGame()`，无返回值，保存后回读状态确认）
- `choose_dialogue` 增加选项边界校验
- `/health` 的版本号改为从 manifest 注入，不再硬编码
- 测试：C# 47 项 + 适配器契约全绿；Python 8 项全绿
- 构建产物在 `dist/PawnMind/`，**没有复制到游戏目录**（你在玩，等退出后再装）

## 后续计划

- `load_game` / `new_game` 接入：已确认两者都是无参方法（`PlayerStore.LoadGame()` / `StartNewGame()`），依赖当前槽位/开局上下文，必须先做二次确认与存档备份，等你不玩时再实机验证
- `choose_dialogue` 索引映射、`bargain` 实际成功率
- 视觉鉴定

## M0 验证状态（2026-11-04）

- 游戏内加载成功，`ModLoader` 记录 `trust=Match caps=network`
- `GET /health` → `{"ok":true,"name":"pawnmind","version":"0.1.0"}`
- `GET /state`（主菜单）→ `{"in_game":false,...,"rev":0}`
- `POST /action {"op":"ping"}` → `{"ok":true,"pong":true,"rev":0}`
- 无 token → 401，未知路径 → 404
- 纯逻辑/HTTP 集成测试 34 项全绿；适配器契约 32 项全绿
- MCP stdio 握手通过：`tools: [get_state, ping, wait_for_change]`

尚未验证：载入存档后的 `/state`（现金、顾客、议价物）——需要进游戏点“继续/载入”。

## 构建

```bash
cd PawnMind
./build.sh
```

产物在 `dist/PawnMind/`：`manifest.xml` + `PawnMind.dll`。

## MCP 服务

```bash
python -m venv .venv
.venv/Scripts/python.exe -m pip install mcp
.venv/Scripts/python.exe agent/mcp_server.py --config "<...>/ModData/pawnmind/bridge.conf"
```

自检（不启动 MCP）：

```bash
.venv/Scripts/python.exe agent/mcp_server.py --config "<...>" --probe
```

MCP 工具：`get_state`、`ping`、`wait_for_change`、`open_shop`、`call_next_customer`、`advance_dialogue`、`choose_dialogue`、`end_day`、`bargain`。

## Agent 循环

`agent/orchestrator.py` 提供最小闭环（观察 → 决策 → 单步动作 → 回读）和确定性基线策略 `RulePolicy`：

```bash
.venv/Scripts/python.exe agent/orchestrator.py --config "<...>" --dry-run
.venv/Scripts/python.exe agent/orchestrator.py --config "<...>" --steps 8
```

LLM 策略只需实现 `decide(state) -> ToolCall | None`，复用同一个循环和护栏。

## 安装

1. 退出游戏。
2. 把 `dist/PawnMind` 整个文件夹复制到：

   `...\steamapps\common\Probably Stolen\Mods\PawnMind\`

   确保第一层有 `manifest.xml` 和 `PawnMind.dll`。
3. 启动游戏 → 主菜单「模组」→ 启用 `PawnMind · AI Bridge`。
4. 首次启用会提示 `network`（访问互联网）能力；本 Mod 只连本机回环地址。
5. 按提示重启游戏。

## 连接

首次启用后生成连接配置：

```
%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\pawnmind\bridge.conf
```

格式：

```
# pawnmind bridge config v1
port=15528
token=<64 hex chars>
```

**不要公开或截图这个文件。**

自检：

```bash
python tools/probe.py
python tools/smoke_actions.py   # 开店 + 叫客，逐步回读；只在测试档跑
```

手动：

```bash
curl http://127.0.0.1:15528/health
curl -H "Authorization: Bearer <token>" http://127.0.0.1:15528/state
```

## 酿酒全流程（2026-11-10）

```
grant wine_bottle + wine_berry + wine_yeast
wine_add_water(750ml) -> ml=750
wine_add_ingredient(berry) / (yeast) -> ok
start_fermentation -> started
sleep x2 -> 荧光莓果酿, finished=true, quality_tier=2, extract=8‰
```

## 水培全流程（2026-11-10）

```
灌满储水罐 20000ml -> insert 电池/水/种子
sleep -> 生长中 50%
sleep -> 电力不足（电池耗尽）
换电池 -> sleep -> 产出 7 个营养果/大型营养果
machine_collect(slot4) -> 回背包
```

## 水培（2026-11-10）

- `machine_grow_status`：state/progress/grow_status/water/seed
- 真机：水培 249 放电池/水/种子，返回 `水量不足`（瓶装水/空储水罐都不够）
- 槽位 0 电池、2 水、3 种子、4 产出

## 机器（2026-11-10）

- `list_machines`：按 `STANDARD_MACHINE_TAG` 枚举；返回 state/progress/slots
- `machine_status`：槽位与内容
- `machine_insert` / `machine_collect`：槽位放入/取回
- 真机：熔炉 5 槽、水培 6 槽（STATE_READY）
- 熔炉实机全链路：slot0 电池 + slot2 两份废金属 → 睡一觉 → slot3 金属锭 → `machine_collect` 回背包
- 机器在**日结/睡觉**时 tick，不是实时；背包里也会处理
- `junk` 需要垃圾处理模块，普通废金属不需要

## 远征（2026-11-10）

- `debug_unlock_expedition`（test-only）：解锁并生成 Juan
- `dispatch_expedition(hireling_id, location_id)`：派遣到 dumping_grounds
- `advance_expedition(expedition_id)`：推进一步
- 真机：解锁→Juan→派遣→turn=12→首次 advance ok；第二次 advance 偶发 NRE（游戏 POI 逻辑）

## P10 世界快照（2026-11-10）

- `expedition_status`：unlocked、地点、雇佣兵、进行中远征
- `events_status`：活跃事件/未来事件数
- `list_signs`：自有营销招牌
- 真机：第 15 天 expedition 未解锁、1 个地点；2 个活跃事件；2 块招牌

## P9 快照/动作（2026-11-10）

- `list_factions` / `health_status` / `list_requests` / `wanted_status` / `barter_status`
- `deliver_request(id)` / `barter_accept()` / `arrest_wanted(id)`
- 真机：第 15 天 5 派系、健康正常、请求/通缉为空；错误路径 `request_not_found`/`no_barter_offer`/`unknown_wanted`

## P6/P7 扩展（2026-11-10）

- 通用交互：`list_interactions` / `activate_item` / `toggle_item` / `use_tool`
- 分析仪：`list_analyzers` / `analyze_item`（水/化/植物/基因）
- 标签：`list_labels` / `set_label`（瓶装水 沟渠水→纯水 真机）
- 标记笔：`set_pen_mode` / `list_marks`（罗非鱼绿色）
- 电话：`list_contacts` / `call` / `phone_state` / `phone_advance` / `phone_choose` / `end_call`
- 供应商：买 `BREWER` 后 8815 进入 Introduction；睡 4 天后可订 8 份啤酒（次日送达）
- 彩票：`lottery_status` / `list_tickets` / `inspect_ticket` / `redeem_ticket`
- 网络升级：`list_upgrades` / `buy_upgrade`

## 安全边界

- 只绑定 `127.0.0.1`，拒绝非本机 `Host`
- 除 `/health` 外都需要 Bearer token
- 不读隐藏字段、不改钱、不生成物品、不执行任意代码
- 所有游戏对象访问都排在 Unity 主线程执行

## 开发

```bash
./test.sh        # 纯逻辑单测 + 适配器契约测试
./build.sh       # 先跑测试，再构建 dist
```

- `src/Core/`：无 Unity 依赖，纯逻辑（配置、HTTP 解析、鉴权、revision、路由、响应）
- `src/Bridge/`：HTTP 服务、主线程泵、状态快照、动作路由
- `src/Game/`：游戏运行时适配（反射/强类型）
- `test/PawnMind.Tests/`：纯逻辑测试
- `test/PawnMind.AdapterTests/`：对 `Assembly-CSharp.dll` 的契约测试

## 路线

- M0（当前）：只读状态 + ping
- M1：14 个动作（交易、砍价、对话、叫客/送客、开关店、睡觉）+ revision
- M2：agent loop、记忆、护栏、对账
- M3：视觉鉴定
- M4：打包/工坊
