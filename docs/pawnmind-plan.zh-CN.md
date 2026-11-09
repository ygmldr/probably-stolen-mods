# 深空掌柜 PawnMind · 实施计划

> 目标：给《深空当铺：可能是偷的》(0.46D) 做一个**能自己玩**的 MCP Mod。
> 形态：游戏内 Mod（C#，`network` 能力）+ 本机 Python MCP 服务 + 可选自主 agent 编排器。
> 周期：单人全职 4–6 周出 v0.1 可玩版；业余 2–3 个月。
> 原则：TDD、信息边界（不读隐藏字段）、可回滚、先跑通一天再谈策略。

---

## 1. 范围

### v0.1 要做（"能自己过一天"）

- 开店 → 叫客 → 对话 → 买卖/基础砍价 → 送客 → 关店 → 睡觉 → 次日回读
- 文本状态 + 14 个动作工具 + revision 防重放
- 一个 Python agent loop：观察 → 决策 → 动作 → 回读校验
- 3 个连续游戏日无人接管（不碰鼠标），账目对得上

### v0.1 不做

- 视觉鉴定（放 v0.3）、多职业、以物易物、复杂机器产线、无人值守多日经营
- 工坊发布（v0.4 再打包）
- 读隐藏字段（真假、顾客真实身份、水质组成）——永久不做

### 非目标

- 不做"自动点击器"或内存修改器；所有动作走游戏正常逻辑
- 不做绕过 ModLoader 能力扫描的事

---

## 2. 架构

```
┌───────────────────────────── Windows ─────────────────────────────┐
│  Probably Stolen (Unity 2021.3 / Mono)                            │
│  ┌──────────────────────────┐        ┌─────────────────────────┐  │
│  │ PawnMind Mod (C#)        │        │ 游戏主线程               │  │
│  │  HTTP :15527 (127.0.0.1) │ ─────► │ ActionQueue (Concurrent) │  │
│  │  token + revision        │ ◄───── │ StateBuilder (main only) │  │
│  └──────────────────────────┘        └─────────────────────────┘  │
│            ▲                                                      │
│            │ HTTP + Bearer token                                  │
│  ┌─────────┴────────────────┐       ┌──────────────────────────┐  │
│  │ Python MCP server (stdio)│ ◄───► │ 你的 AI 客户端 / 编排器   │  │
│  │  14 tools + resources    │       │  (Claude Code / 自建)     │  │
│  └──────────────────────────┘       └──────────────────────────┘  │
└───────────────────────────────────────────────────────────────────┘
```

为什么不是 Mod 直接实现 MCP：MCP stdio 要求客户端拉起服务进程，而 ModLoader **硬拒绝** `System.Diagnostics.Process`，Mod 不能起进程。所以 C# 只做本机 HTTP，协议与工具层放 Python。

为什么不是外部读内存/纯截屏：无官方支持、版本一更新就碎、且容易被当外挂。

---

## 3. 游戏侧设计（C#）

### 3.1 Mod 骨架

- ID：`pawnmind`；`manifest.xml` 声明 `<Capabilities><Capability>network</Capability></Capabilities>`
- 入口实现 `IMod`：`Init` 只存 manifest 和日志；`OnEnable` 订阅 hook；`OnDisable` 退订并停 HTTP
- 订阅：`ModHook.OnGameLoadedNormal`、`OnShutterOpenedEarly`、`OnShutterClosedEarly`、`OnWakingUpEarly`、`OnLeavingStoreEarly` / `OnReturningStoreEarly`
- 创建隐藏 `GameObject` + `MonoBehaviour` 作为 `Update` 泵（`DontDestroyOnLoad`），每帧排空动作队列

### 3.2 线程模型（必须）

Unity API 只能在主线程调用：

- HTTP 线程：解析请求 → 校验 token/revision → 把 `Func<ActionResult>` 入 `ConcurrentQueue`
- `Update`：出队 → 执行 → 结果写回 `TaskCompletionSource`
- HTTP 线程等待结果（超时 3s）→ 序列化返回
- 绝不在 HTTP 线程碰 `GameObject` / `PlayerStore`

### 3.3 HTTP 层

- 默认 `127.0.0.1:15527`，端口可配
- 认证：`Authorization: Bearer <token>`；token 首次启动随机生成，存 `ModData/pawnmind/bridge.json`（用 `ModHelper.WriteModDataFileText`，**不需要 `files` 能力**）
- 防 DNS rebinding：校验 `Host` 为 `127.0.0.1[:port]` 或 `localhost`
- 传输：先试 `HttpListener`；若 Windows URL ACL 报 `Access is denied`，改用 `TcpListener` + 极简 HTTP/1.1 解析（约 200 行，零依赖）
- 端点：
  - `GET /health` → `{ok, version, rev}`
  - `GET /state?since=<rev>` → 快照或增量
  - `POST /action` → `{action_id, op, args, rev}` → 结果
- 拒绝重定向/跨域；不监听 `0.0.0.0`

### 3.4 状态快照

```json
{
  "rev": 142,
  "day": 12,
  "shop_open": true,
  "cash": 1840,
  "rent_days_left": 3,
  "reputation": {"lower": 12, "upper": -3, "security": 0, "black_market": 5, "revolution": 0},
  "client": {
    "id": "c17", "name": "Mira", "faction": "lower",
    "intent": "SELL", "stage": "at_counter",
    "dialogue": ["..."], "dialogue_options": [],
    "offer": {"item_id": 9021, "side": "buy", "price": 120, "budget": 300},
    "bargain_available": true, "bargain_used": false
  },
  "counters": {"front": [9021], "display": []},
  "windows": ["trade_tablet"],
  "blocked": null
}
```

- 只放**玩家屏幕上可见**的信息；隐藏字段不读、不推断、不缓存
- 列表分页（每页 ≤ 30），`inspect_item` 按需取详情
- `rev` 每次状态变化 +1；动作必须带当前 `rev`，否则 `stale_rev` + 最新快照

### 3.5 动作层（v0.1，14 个）

| 工具 | 作用 | 关键校验 |
|---|---|---|
| `get_state` | 快照/增量 | — |
| `get_client` | 当前顾客详情 | 有顾客 |
| `list_items` | 分区/分页物品 | 区域合法 |
| `inspect_item` | 物品详情（可见） | 物品存在、可见 |
| `open_shop` | 开店 | 未开店、白天 |
| `call_next_customer` | 叫下一位 | 店内、队列非空 |
| `advance_dialogue` | 推进对话 | 对话处于等待 |
| `choose_dialogue` | 选项（索引+原文） | 选项匹配 |
| `confirm_trade` | 成交（item/side/price） | 所有权、金额、容量、rev |
| `bargain` | 砍价（方向+档位） | 每方向一次、按钮可用 |
| `move_item` | 搬运物品 | 容器已打开、空间、锁定 |
| `close_shop` | 关店（含确认） | 店内状态 |
| `sleep` | 睡觉/跨日（含确认） | 可结束当天 |
| `wait_for_change` | 长轮询直到 rev 变化 | 超时上限 30s |

返回契约：

```json
{"ok": true, "action_id": "...", "rev": 143, "effects": {"cash": -120}, "state_delta": {...}}
{"ok": false, "error": "stale_rev", "rev": 142, "hint": "re-read state"}
```

- `action_id` 幂等：重复提交返回首次结果，避免超时重发导致双花
- 所有写操作先校验后提交；失败不留半状态
- `dry_run` 配置：只校验不执行，用于测试

### 3.6 暂停策略

- 运行时发现 `PauseAllSource` / `IsPaused` / `UnPause` 等符号，M0 里确认能否由 Mod 主动暂停
- 能：交易/对话窗口打开时压入暂停源，AI 决策完弹出
- 不能：`Time.timeScale = 0` + 恢复；必须实测对顾客计时/动画的影响
- 配置项 `pause_on_decision`，默认开；关闭时接受"顾客可能等不及"

### 3.7 安全与审计

- 只监听 127.0.0.1；token 不落日志；`bridge.json` 权限最小化
- `ModData/pawnmind/audit.jsonl` 记录每个动作（时间、op、args 摘要、结果、rev）
- 金额上限、单日动作上限可配；超限返回 `limit_exceeded`
- 遇到未知窗口：返回 `blocked: "unknown_window"`，agent 停机等人工
- 不做任意代码执行、不读隐藏字段、不改钱/生成物品

---

## 4. MCP 服务（Python）

- Python 3.11+，`mcp` 官方 SDK，stdio 传输
- 复用 `FastMCP` 风格声明 14 个 tool；Pydantic 参数校验
- resources：`pawn://state`、`pawn://client/current`、`pawn://item/{id}`
- 配置发现：从 `%LOCALAPPDATA%Low/Questing Goose Studio/Probably Stolen/ModData/pawnmind/bridge.json` 读 token/port；也支持 `--config` 覆盖
- 不自动重试写操作；只对 `GET /state` 做 1 次重试
- 错误原样上抛（`stale_rev` / `window_blocked` / `not_owner`），让模型自己纠正
- 附 `--probe`：握手 + 读一次状态，用于安装自检

工具描述要写清"一次只做一步、动作后必须回读"，并在 server 层强制：`confirm_trade`/`bargain` 需要显式 `rev`。

---

## 5. Agent 编排（可选但推荐）

### 5.1 循环

```
while 未到停机条件:
    state = wait_for_change(rev) or get_state()
    decision = LLM(system_prompt, memory, state)
    result = call_tool(decision)
    verify(result, state)          # 钱、所有权、日期、rev
    memory.append(decision, result)
```

### 5.2 领域策略（写进 prompt / 规则层）

- 买卖：先看报价、预算、库存与利润率；不清楚就 `inspect_item`
- 砍价：每方向只能一次；10/25/50/75 档，需求越大越容易被拒/走人（10% 接受率约 80%，50% 起基本拒绝）
- 声誉：让利涨声望、强索掉声望；声望影响后续报价与剧情
- 鉴定：只报"可见信息 + 估值区间"，不猜隐藏真假；不确定就放弃或交给人
- 资金：保留房租/贷款缓冲；低于现金地板停止收购

### 5.3 记忆

- `memory/day-XX.json`：当日账本、成交、拒绝、顾客备注
- `memory/pricebook.json`：物品→成交价区间（自学习，不写死攻略）
- 跨日摘要 ≤ 2k token，避免上下文膨胀

### 5.4 护栏

- 最大步数/日、最大连续失败数、现金地板、单笔金额上限
- 遇到未知窗口/连续 `stale_rev`/工具异常 → 停机并输出"需要人工"
- 每日结束对账：现金变化 = 成交明细之和（容差 1）

### 5.5 评测

- 指标：日利润、成交数、拒绝率、声望变化、误判率（v0.3）、token/日、人工接管次数
- 场景：固定存档 + 固定种子 + 脚本化顾客序列（v0.2 起）
- 输出 `runs/<date>/report.json`，可对比不同 prompt/模型

---

## 6. 测试与质量门（沿用仓库 TDD 规则）

### 6.1 C# 纯逻辑（不依赖 Unity，每次必跑）

- `StateSnapshotTests`：fake 游戏对象 → JSON 快照（golden，ID 归一化）
- `RevisionTests`：变更递增、stale 拒绝、并发只允许一个写
- `ActionValidationTests`：所有权/容量/金额/窗口状态；失败不改状态
- `HttpRouterTests`：token、Host 校验、幂等、404/405
- `ActionQueueTests`：主线程泵、超时、异常隔离

### 6.2 适配器契约测试（引用 Assembly-CSharp）

- 反射断言关键类型/成员存在：`PlayerStore`、`StoreClientManager`、`StoreClientInstance`、`GameItem`、`TradeSheet`、`BargainUIManager`、`AppraisalHelper`
- 方法签名变化 → 测试红，提示 adapter 要改
- 找不到游戏 DLL 时 SKIP（与 CompactSort 的 AdapterTests 同策略）

### 6.3 Python 测试

- pytest + 假 HTTP 服务：工具 schema 快照、stale rev、token 失败、超时不重试
- `--probe` 自检

### 6.4 游戏内验收

- `test/IN_GAME_CHECKLIST.md`：安装 → 激活（确认 network）→ 测试档 → `curl /state` → 脚本跑一个顾客 → 睡觉 → 校验无复制/丢失/双花
- `mod-scan pawnmind`：`declared` 覆盖 `detected`，无 hard flag
- 每次发布前跑全量 `./test.sh` + `mod-scan`

---

## 7. 里程碑

| 阶段 | 内容 | 工期 | 验收 |
|---|---|---|---|
| **M0 Spike** | 最小 Mod：HTTP + `/state` + 主线程队列；验证 HttpListener/TcpListener、暂停 API、mod-scan | 2–3 天 | `curl` 能读到实时 JSON；游戏不崩；`mod-scan` 只有 network |
| **M1 Bridge** | 14 个工具 + revision + 校验 + Python MCP + 假服务测试 | 5–7 天 | 脚本能完成"叫客→对话→成交→送客" |
| **M2 Autonomy** | agent loop + 记忆 + 护栏 + 对账 | 5–7 天 | 连续 3 个游戏日不碰鼠标；账目对得上；0 复制/丢失 |
| **M3 Appraisal** | 物品截图（RenderTexture + `ModHelper` 写文件，尽量不申请 `files`）+ 多模态估值 + 评分回读 | 7–10 天 | 20 件鉴定样本：Perfect ≥ 40%，MAE ≤ 12%；不读隐藏字段 |
| **M4 Package** | 配置 UI、文档、安装脚本、工坊打包、干净安装验证 | 3–5 天 | 新机器按文档 15 分钟装好；工坊包不含 Python/密钥 |

合计约 22–32 个工作日（全职）。M0/M1 是硬门槛，M3 是差异化，M4 可延后。

---

## 8. 目录结构

```
PawnMind/
  manifest.xml
  src/
    PawnMindMod.cs
    Bridge/HttpServer.cs
    Bridge/ActionQueue.cs
    Bridge/StateBuilder.cs
    Bridge/Actions/*.cs
    Game/Adapters/*.cs
  test/
    PawnMind.Tests/            # 纯逻辑
    PawnMind.AdapterTests/     # 引用 Assembly-CSharp
    IN_GAME_CHECKLIST.md
  agent/
    mcp_server/server.py
    mcp_server/tools.py
    orchestrator/loop.py
    orchestrator/prompts/
    tests/
  docs/
  build.sh
  test.sh
```

---

## 9. 风险

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| 反射路径被更新打断 | 高 | 高 | adapter 隔离 + 契约测试 + 优先官方 hook |
| Unity 主线程死锁/卡顿 | 中 | 高 | 严格队列 + 超时 + 不阻塞 Update |
| HttpListener 权限问题 | 中 | 中 | TcpListener 兜底（M0 验证） |
| 暂停 API 不可用 | 中 | 中 | timeScale 兜底 + 产品上接受等待 |
| LLM 误操作毁档 | 高 | 高 | 备份、dry_run、金额上限、rev、对账 |
| 上下文/成本失控 | 中 | 中 | 摘要 + 分页 + wait_for_change + 步数上限 |
| 能力提示吓退玩家 | 中 | 中 | 开源、文档、只连 127.0.0.1 |
| 鉴定不可靠 | 高 | 中 | 只报区间/置信度；不确定交给人 |
| 版本/职业覆盖不足 | 高 | 中 | v0.1 只承诺一个职业一条线 |

---

## 10. 待拍板

1. **v0.1 目标**：自主过一天（推荐）还是"人在环里"的辅助工具？
2. **分发**：只自用，还是按工坊发布标准做（影响能力声明、文档、支持成本）？
3. **客户端**：自建 Python 编排器（推荐，能评测），还是只做 MCP 服务给 Claude Code/Codex 用？
4. **视觉鉴定**：v0.3 做（推荐，差异化），还是永远不做？
5. **命名**：`PawnMind` / 深空掌柜，是否可用？

---

## 11. 参考

- 官方 modding 指南：https://github.com/dragonpan2/probably-stolen-modding
- 现有 Bridge（对照，不 fork）：https://github.com/yidengdengdashi/probably-stolen-mcp-releases
- 砍价机制：https://probablystolenguide.com/wiki/bargaining/
- 鉴定机制（官方 guide 的 Appraisal Items 节）
- 本仓库 CompactSort 的 TDD/适配器测试范式：`CompactSort/AGENTS.md`

---

## 附录 A：接口清单（2026-11-04 反射核对，0.46D）

### A.1 官方 mod API（稳定面）

| 类别 | 代表接口 |
|---|---|
| 事件 | `ModHook.OnGameLoadedInit/Early/Normal/Late`、`OnShutterOpened/Early/Late`、`OnShutterClosed*`、`OnGenerateCustomer*`、`OnWakingUp*`、`OnGoingSleep*`、`OnLeavingStore*` / `OnReturningStore*`、`OnCreateTooltip*`、`OnHandlingNightlyServices*`、`OnAfterhour*`、`OnModItemDirectoryInit`、`OnLootRolled` |
| 物品 | `ModHelper.SpawnItem`、`MoveItemToFrontCounter/BackCounter/Inventory/DisplayCase/Trash/Afterhours`、`SetItemOwnerShip`、`SetClientSource` |
| 数据 | `ModHelper.GetConfig`、`SetModData/GetModData`、`ReadModDataFileText/Bytes`、`WriteModDataFileText/Bytes`、`GetModDataFolder` |
| 内容 | `AddAppraisalItem`、`AddLootEntry`、`AddStartingScenario`、`AddStartingPerk`、`AddDifficulty`、`AddNetworkUpgrade`、`AddAfterhourLocation`、`CreatePaper` |
| UI/调试 | `AddGameOptionSection/Toggle/Slider/Dropdown/KeyBind`、`AddDebugFunction`、`OpenPaper` |

### A.2 运行时反射面（游戏类型反射免费）

| 需求 | 接口 |
|---|---|
| 主单例 | `PlayerStore.Instance` |
| 现金 | `GetCash()`、`PayMoney(int,bool)` |
| 日循环 | `OpenShutter()`、`BeginDay()`、`EndDay()`、`CanEndDay()`、`GetRentDayCounter()`、`GetRentValue()` |
| 当前顾客 | `PlayerStore.currentClientInstance`（私有字段）→ `StoreClientInstance.storeClient` → `StoreClient` |
| 顾客字段 | `displayName`、`clientCash`、`useClientBudget`、`clientIntent`、`clientFaction`、`clientImpatience`、`isBargainAvailable`、`isWanted`、`isAug`、`mainDialogue` |
| 议价物 | `GetCurrentlyNegociatedItem()` |
| 物品 | `GameItem.name/identifier/unitCount/unitValue/GetCurrentValue()/GetNegociatedValue()/GetBaseValue()` |
| 成交 | `CanBuyItemFromTable`、`BuyItemFromTable`、`BuyItem`、`CanSellThisItem`、`SellItemFromTable`、`SellItem`、`BuyAllItemFromTable`、`SellAllItemFromTable` |
| 砍价 | `BargainUIManager.Instance`：`CanOfferDiscount/Markup`、`OfferDiscount(int)`、`OfferMarkup(int)`、`OfferBuyingMarkup(int)`、`DemandBuyingDiscount(int)`、`Blackmail()`、`ThreatenAug()`、`GetBargainSuccessChance(BargainType,int)`、`GetBargainLeaveChance(...)`、`GetPreBargainValue()`、`GetBargainTooltip` |
| 对话 | `Dialogue.plainText`、`choices`、`nextDialogue`；推进入口待 M0 定位 |
| 叫客/送客 | `TryCallNextClient()`、`CanNextClient()`、`GetClientInQueueCount()`、`DismissCurrentClient(bool,bool,bool)` |
| 库存 | `GetStoreInventory()`、`FindAllItem`、`IsPlayerOwnThisItem` |
| 存档 | `SaveGame()` |
| 报价 | `TradeSheet.Get*Shop()`、`PriceHelper` |
| 鉴定 | `AppraisalHelper`（含 `GetCorrectBaseValue`，**红线：不接给 AI**） |

### A.3 现有 Bridge 的接口

- 31 个 MCP tools（stdio），游戏侧 `POST http://127.0.0.1:15527/rpc`，body `{op, request_id, ...args}`，`Authorization: Bearer <token>`，8 秒超时。
- 超时返回 `bridge_unavailable_or_result_unknown`，要求回读状态、不要重发写操作。
- 可接入方式：作为 MCP client 接它的 server（干净但受限于 31 动作）；直接 POST `/rpc`（协议可见但闭源、可能变，不建议生产依赖）；改游戏侧不可行（未开源）。

### A.4 M0 必须验证的三件事

1. `PlayerStore.currentClientInstance` 在交易窗口期间是否稳定可读（反射字段，私有）。
2. 对话推进入口：`Dialogue` 数据可读，但推进方法在哪个窗口管理器上（现有 Bridge 已实现，说明可行）。
3. 暂停方式：`PauseAllSource` / `IsPaused` 不是可 `GetType` 的顶层类；先试 Harmony/`Time.timeScale`，并实测对 `clientImpatience` 的影响。
