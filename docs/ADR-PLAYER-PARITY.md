# ADR-PP-001：玩家等价执行协议（Operation / Journal / 主线程 / Hook / 模式）

- **状态**：Proposed（评审通过前不实施）
- **日期**：2026-11-17
- **关联**：`docs/rfc/PawnMind_Player_Parity_RFC_v1.md`（输入）、`docs/reviews/PLAYER_PARITY_RFC_REVIEW.md`（审阅结论）
- **基线**：commit `d0440ab`，*Probably Stolen* 0.46D，C# 651 / 契约 490 / Python 234

---

## 背景

MCP 工具直接调用原生方法，多次出现"桥能做玩家做不出的事"（跳过房东、双开刷奖励、送走房东、
成交后顾客卡死）。根因不是单个 `if` 写漏，而是**合法性、执行、证据三件事没有分层**：
原生 `void` 方法静默 return 被我们当成成功；异步协程在返回后才改状态；只读 getter 带副作用。

本 ADR 只决定**边界与所有权**，不决定具体实现细节。

---

## ADR-PP-1：Operation 状态归 C#，durable intent 归 Python

**决定**

| 事实 | 权威 | 存储 | 生命周期 |
|---|---|---|---|
| 发出过什么请求 | Python recorder | SQLite（events 库，与 intent 同事务） | 跨进程重启 |
| 原生是否执行、执行到哪一步 | C# `ActionGate`（扩展后） | 进程内存 | 进程重启即丢失 |
| 业务效果是否发生 | C# 观察层（状态 + ModHook 事件） | 随响应返回，Python 落库 | 随 operation 记录 |

**理由**：跨 Python SQLite 与 Unity 世界不存在原子事务（RFC §3.2 的判断正确）。
把"最终效果"权威放在 C#（唯一能看游戏的地方），把"发过什么"放在 Python（唯一能跨重启的地方），
两者职责不重叠，避免两套状态机互相矛盾。

**后果**

- 进程重启后，未结案的 operation 只能是 `uncertain`，**不得**自动重放。
- 不承诺跨进程 exactly-once；声称即错误。

**拒绝的替代方案**：C# 侧持久化 operation 表（要处理 Unity 崩溃时的半写、存档绑定、多实例）；
在 D1–D2 阶段收益不足。

---

## ADR-PP-2：不新建 OperationTracker，扩展现有 `ActionGate`

**决定**：`ActionGate.Entry` 增加 `status ∈ {prepared, dispatching, running, completed, rejected, uncertain}`、
`phase`、`effects`、`accepted_rev`、`deadline`；`MarkResponseUndelivered` 复用为 `uncertain` 迁移。

**理由**：`ActionGate` 已具备 action_id 生命周期、结果缓存、重复/冲突检测与"未送达改写"
（`src/Core/ActionGate.cs:19-90`、`src/Bridge/ActionRouter.cs:213-227`）。
再建一个 tracker 会出现两套状态机与两处超时判定。

**后果**：`ActionGate` 的单元测试需要扩展为状态机测试（纯 Core，可测）；Bridge 层只做映射。

---

## ADR-PP-3：合法性校验与原生调用必须在同一个主线程连续步骤

**决定**：`rev` 校验、版本门禁、业务守卫、原生调用、首次证据采集，全部在
`MainThreadPump.Update → BridgeServer.Tick → ActionRouter.HandleWrite` 这一条同步调用链内完成。
**禁止**在 HTTP 线程做任何预校验（包括"先查一下 can_end_day"）。

**现状**：已满足（`src/PawnMindMod.cs:135`、`src/Bridge/MainThreadPump.cs:22`、
`src/Bridge/ActionRouter.cs:165-210`）。本 ADR 把它从"实现巧合"升级为**约束**，并加回归测试
（`P0-REV-01`：stale rev 在派发前被拒，且世界零副作用）。

**后果**：任何需要在派发前等待的逻辑（例如"等动画结束"）都必须放到 Python 侧的
`wait_operation`，不能阻塞主线程。

---

## ADR-PP-4：Hook 准入——只读、白名单、可证伪

**决定**

1. 生产路径**只允许**订阅游戏自带的 `ModHook.FireOn*`（只读回调），共 44 个，白名单如下：
   `FireOnShutterClosedEarly/Late`、`FireOnShutterOpenedEarly/Late`、`FireOnGoingSleepEarly/Late`、
   `FireOnHandlingNightlyServicesEarly/Late`、`FireOnGenerateCustomer*`、
   `FireOnPlaceInventorInventoryItemEarly/Late`、`FireOnLeavingStore*`、`FireOnReturningStore*`、
   `FireOnAfterhour*`、`FireOnGameLoaded*`。
2. **Harmony 只用于测试装置**（L3 UI 探针、覆盖率统计），不进生产路径。
3. 任何 hook 必须配一条**负向测试**：在"守卫拒绝"的路径上不得触发；否则它证明的只是"方法被进入"。

**理由**：`FireOn*` 没有交易事件（见审阅 §5.2），所以 hook 只能覆盖开关店/睡觉/夜间/摆货；
交易必须靠后置条件。把 hook 当业务成功证明是 RFC §5.4 自己警告过的错误。

**后果**：交易域的证据链更长（物品 id 集合 + `isBusinessComplete` + 顾客离场），但不需要 Harmony。

---

## ADR-PP-5：三种模式由 C# 强制，`tools/list` 只是第一道

**决定**

| 模式 | 谁能进 | 能力 |
|---|---|---|
| `strict_player`（录像） | 配置 + 绑定槽 | 仅玩家可达动作；只读纯度达标；`bargain_preview` 等非纯读**不注册** |
| `assisted`（调试） | 本机 + token | 额外 `save_game`/`load_game`/诊断；不得用于录像 |
| `test` | 显式配置 + 非玩家存档 | `allow_test_items`、`debug_*` |

强制点（缺一不可）：
1. MCP `--tools` 白名单（已存在）；
2. **C# 侧 op allowlist**：`/action` 对模式外 op 返回 `mode_forbidden`，**不依赖 MCP 隐藏**；
3. 写门禁：游戏版本 + MVID 精确匹配（已存在）；
4. 配置文件权限：模式写在 `bridge.conf`，改动需重启游戏（不可由工具修改）。

**现状核对（不能夸大缺口）**：C# 已有 `RecordingDispatchGuard`，它
（a）拦截 `act` 包装到 debug/cheat op，
（b）在录制启用时禁掉 `debug_*`，
（c）把 `new_game`/`load_game`/`save_game`/`press`/`sleep` 收进录制策略
（`src/Bridge/RecordingDispatchGuard.cs:20-110`）。所以"直接打 HTTP 就能存档/作弊"**不成立**。

**真实缺口**：普通业务 op（`bargain_preview`、`confirm_trade` 之外的那一百来个）**没有模式判定**；
且 `act` 会在 `DispatchAction` 里重新派发到任意普通 op（`src/PawnMindMod.cs:239-248`）。
因此 MCP 的 `--tools` 是普通 op 的**唯一**限制，绕过 MCP 直连 HTTP 即可调用被隐藏的普通 op。
本 ADR 要求把"普通 op 的模式 allowlist"补到 C# 侧。

**后果**：新增一个 `ModePolicy`（Core，可测）+ 一份每个模式的 op 清单（版本化）。

---

## ADR-PP-6：`ok` 只表示 `completed`；`pending` 不伪装成功

**决定**

```json
{ "ok": true,  "status": "completed", "operation_id": "…", "effects": ["trade_committed"] }
{ "ok": false, "status": "pending",   "operation_id": "…", "phase": "post_trade_dialogue",
  "next_poll_after_ms": 250 }
{ "ok": false, "status": "rejected",  "error": "trade_locked", "blocked_by": ["IsTradeLocked"] }
{ "ok": false, "status": "uncertain", "error": "outcome_unknown", "operation_id": "…" }
```

**理由**（与 RFC §4.3 分歧）：RFC 建议 `ok:true` 表示"已受理"。但现有 104 个工具、试玩剧本、
记忆系统、以及 DSH 的提示词都把 `ok:true` 当完成；改成"受理"会让模型跳过 `status` 直接继续。
**兼容性应由新字段承担，而不是改变旧字段语义。**

**后果**：模型侧需要一段明确说明（`ok:false + status:pending ≠ 失败`，应 `wait_operation`）。
旧客户端（若存在）在 `pending` 上会看到 `ok:false`，行为是"保守停下"，不是"误判成功"。

---

## ADR-PP-7：只读纯度分级，非纯读不得进入 `strict_player`

**决定**：把读工具分三级。

| 级 | 定义 | 例子 | strict |
|---|---|---|---|
| `pure` | 不改变任何游戏状态/RNG/UI | `read_book`、`list_goods`（已核实纯算术 S6） | ✅ |
| `cached_impure` | 首次有界副作用，之后命中缓存 | `get_clientele`（每日首次 `RNG.Roll`×2，S3） | ⚠️ 允许，但首次返回 `rng_consumed:true` |
| `mutating` | 每次都改 UI/状态 | `bargain_preview`（S1）、**`get_state` 的 `CanOffer*`/`can_report`（S2/S4）** | ❌ 先修，修不好就禁用 |

**必须修的**：`get_state` 是热路径，每次 9 次 `Refresh()` 会重置谈价状态位与口碑倍率。
在 D3 之前，`strict_player` 无法声称"观察不改变玩法"。

**理由**：INV-05 若允许"标记为非纯但仍可调用"，等于没有约束。

---

## ADR-PP-8：不引入 MCP Tasks；用 `get_operation` / `wait_operation`

**决定**：新增两个 MCP 工具，后端是 `GET /operations/{action_id}`；
`wait_operation` 在 Python 侧有界等待（≤5s），不占 Unity 主线程。

**理由**：MCP Tasks 是扩展能力，DSH/当前 SDK 未验证支持（RFC §4.6 同判断）。
两个普通工具即可覆盖需求。

**后果**：`wait_for_events` 保留（等游戏事件），`wait_operation` 专用于等某个操作结案；
两者职责不同，不合并。

---

## 未决问题

1. `get_state` 的 `CanOffer*` 能否改为缓存值（需要确认这些 getter 是否真的只依赖已缓存字段）。
2. `strict_player` 是否允许 `capture_screenshot`（它不改变游戏状态，但会读画面，可能拍到存档名）。
3. 录像用的最小 allowlist 是否包含记忆四工具（只读，但会写 memories.db）。
