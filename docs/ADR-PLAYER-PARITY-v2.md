# ADR-PLAYER-PARITY-v2（对 v1 的修订）

- **状态**：Proposed（取代 v1 中被本文覆盖的条款；未提及的条款继续有效）
- **日期**：2026-11-17
- **输入**：`PLAYER_PARITY_ROUND2_EXTERNAL_REVIEW.md`、`docs/ROUND2_DISPOSITION.md`
- **基线**：commit `3fed61c` + 本轮登记表（`agent/tool_policy.py`、`docs/audit/tools-inventory.json`）

---

## 变更清单（与 v1 的差异）

| # | v1 条款 | v2 变更 | 原因 |
|---|---|---|---|
| 1 | ADR-PP-2：`ActionGate` 单一 `status` | 拆成 **`execution_status`** 与 **`delivery_status`** 两轴 | `MarkResponseUndelivered` 会把已完成的响应改写成 unknown（`ActionRouter.cs:244-287`），丢失事实 |
| 2 | ADR-PP-3：主线程连续步骤 | 增加**前置条件**：该链上的快照构建必须是纯的（D0a） | `TryRefreshRevision → BuildJson → CanOffer*/CanReport → Refresh()` |
| 3 | ADR-PP-7：`cached_impure` 允许进 strict | **禁止**；strict 只允许 `pure` 与 `write` | `get_clientele` 首读消耗 RNG，改变后续轨迹 |
| 4 | 无 | 新增 **ADR-PP-9**：operation 完成 ≠ 工作流完成（`confirm_trade`） | 否则 `advance_dialogue` 被 pending 锁死 |
| 5 | 无 | 新增 **ADR-PP-10**：allowlist 由"已验证工具"生成，禁止手写 | D6 曾把未验收工具写进白名单 |
| 6 | 无 | 新增 **ADR-PP-11**：`ModePolicy` 在最终分发入口，`act` 展开后仍校验 | 普通 op 目前无模式判定，直连 HTTP 可越过 `--tools` |
| 7 | 无 | 新增 **ADR-PP-12**：睡眠/关店的"完成"必须区分 `day_changed` 与 `save_completed` | `FireOnGoingSleep*` 是过程信号，不是落盘证明 |

---

## ADR-PP-2（修订）：两个状态轴

**决定**

```
execution_status ∈ {prepared, running, completed, failed, rejected, uncertain}
delivery_status  ∈ {pending_delivery, delivered, undelivered}
```

- `execution_status` 只由**游戏侧证据**推进，且单调（不回退）。
- `delivery_status` 只描述"调用方有没有拿到响应"。
- `MarkResponseUndelivered` 只允许把 `delivery_status` 改为 `undelivered`；**不得**改写 `execution_status`。
- 重试同一 `action_id` 时返回：`execution_status` + `delivery_status:"undelivered"` +
  `note:"previous response lost"`，而不是 `outcome_unknown`。

**例外**：若 `execution_status` 仍是 `running/uncertain`（没有完成证据），响应丢失后保持原状态，不额外降级。

**后果**：`outcome_unknown` 从"状态"降级为"对调用方的提示"，不再污染业务事实。

---

## ADR-PP-3（修订）：快照纯度是写路径的前置条件（D0a）

**决定**：在 D1 之前必须满足：

1. `/state` 与 `TryRefreshRevision` 的构建路径上，**`BargainUIManager.Refresh()` 调用次数 = 0**；
2. 需要 `CanOffer*` 的字段改为**仅当谈价面板已打开**时读取，或在面板打开时由原生先行刷新后的缓存值提供；
3. 用 Core 层的"快照源"抽象在 fake 上断言调用次数（不依赖真机），真机再用 `PP-READ-01/02` 复核。

**为什么不能只测 `rev`**：`rev` = 序列化指纹计数（`StateSnapshot.cs:11-25`），隐藏状态变化不会体现在 `rev` 上。

**已知严重度修正**：`Refresh()` 只在 `trackedClient != client` 时重置（`BargainUIManager.cs:1103-1126`），
同一顾客会话内是空操作。因此这是"有能力变更的读路径"，不是"每次必变"；定级 P1，待真机仪表。

---

## ADR-PP-7（修订）：严格模式只接受可证明的纯读

**决定**

| 级 | strict_player | assisted | test |
|---|---|---|---|
| `pure` | ✅ | ✅ | ✅ |
| `write`（玩家可达） | ✅ | ✅ | ✅ |
| `cached_impure` | ❌ | ✅（返回 `rng_consumed`/`cached_at_day`） | ✅ |
| `mutating` | ❌ | ✅（返回 `impure:true`） | ✅ |
| `privileged` | ❌ | ❌ | ✅ |

**落地**：`agent/tool_policy.py` 是唯一注册表；`strict_allowlist()` 从全量工具生成；
`agent/tests/test_tool_policy.py` 断言 `bargain_preview`/`get_clientele`/`save_game`/`new_game`/`act`/`press`
不在 strict，且 strict ⊆ recording。

**代价**：录像期间模型拿不到"今天会吸引哪些顾客"的预测（`get_clientele`）。
这是有意的取舍：宁可少一个情报，也不让读取改变 RNG 轨迹。

---

## ADR-PP-9：operation 完成 ≠ 工作流完成

**决定**：`confirm_trade` 的生命周期到**成交提交**为止。

```json
{
  "ok": true,
  "status": "completed",
  "effects": ["trade_committed"],
  "workflow": {
    "phase": "post_trade_dialogue",
    "next_required_action": "advance_dialogue",
    "terminal": "client_departed"
  }
}
```

- 成交证据充分 → `completed`，即使收尾对话还没播完。
- `client_departed` 是**顾客工作流**的终态，不属于该 operation 的完成条件。
- 冲突域只锁"再次 `confirm_trade`"，**不锁** `advance_dialogue`/`choose_dialogue`。
- 若成交本身异步未确认 → `running`，此时仍不锁对话（对话不可能早于成交出现）。

**理由**：模型必须手动推进对话才能送客（现有流程），把两者塞进同一个 pending 会互相等待。

---

## ADR-PP-10：allowlist 由证据生成

**决定**：strict 白名单 = 满足以下**全部**条件的工具集合，由代码生成，不手写：

1. 在 `tool_policy` 中有 purity 分类；
2. 不在 `PRIVILEGED` / `STRICT_DENY`；
3. 在 entry matrix 中 `status ∈ {fixed, unverified-但已被真机正向用例覆盖}`；
4. 有至少一条具名回归用例（`PP-*` 或 `P0-*`）；
5. 属于一周租金主路径，或明确标注"非必需但已验收"。

**后果**：`buy_wholesale`、`barter_accept`、`commissary_buy`、`bargain` 等当前 `open` 的工具，
在修复并通过用例前**不会**出现在 strict 名单里。

---

## ADR-PP-11：ModePolicy 在最终分发入口

**决定**：`DispatchAction`（`src/PawnMindMod.cs`）入口处做一次规范化 + 模式校验：

```
op → normalize(op, args) → ModePolicy.Allows(mode, op) → dispatch
```

- `act` 展开后的目标 op 必须再走一次同一个 `Allows`。
- 拒绝时返回 `mode_forbidden`，且**不产生任何副作用**（在快照刷新之前返回）。
- MCP 的 `--tools` 只是第一道；C# 是最终闸门。

**现状**：`RecordingDispatchGuard` 已覆盖 debug/存档类；普通业务 op 仍无判定（见 disposition §1.4）。

---

## ADR-PP-12：完成信号分层

**决定**

| 动作 | 过程信号 | 完成信号 | 无法确认时 |
|---|---|---|---|
| `end_day` | `FireOnShutterClosedEarly/Late` | 顾客离场 + `storeState == CLOSED` | `running` |
| `sleep` | `FireOnGoingSleep*`、`FireOnHandlingNightlyServices*` | `day + 1` **且** `last_save_time` 前进 | `save_unknown`（不得宣称已保存） |
| `open_shop` | `FireOnShutterOpened*` | `storeState == OPEN` 且 `lockShutterButtonFlag` 释放 | `running` |

`save_completed` 与 `day_changed` 必须是**两个字段**，不能合成一个 `ok`。

---

## 未决

1. `CanOffer*` 能否改为"面板打开时缓存"，取决于这些 getter 是否还依赖其它原生刷新。
2. DSH 对 `status:pending` 的处理需要一次真实 E2E（disposition C2）。
3. `get_state` 其余 getter 的副作用尚未逐个核实（只核实了 `CanOffer*`/`CanReport`）。
