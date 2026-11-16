# 第二轮外部审查：处置表（Round 2 Disposition）

- **审阅输入**：`PLAYER_PARITY_ROUND2_EXTERNAL_REVIEW.md`（2026-11-17，110 行）
- **处置基线**：commit `3fed61c` + 本轮文档/工具登记表改动（未提交部分见 §5）
- **原则**：逐条给 `agree / reject / needs_evidence`，并附**本轮在仓库内独立核实**的证据。
  凡是外部审查的推断，未经源码核实的，一律标 `needs_evidence`，不当作既成事实。
- **本轮只做**：RFC/ADR/计划修订 + 关键调用链核验 + 工具登记表。**不动** C# 业务逻辑（D0a 的代码留到下一轮）。

---

## 0. 总处置

| 类别 | 条数 | agree | reject | needs_evidence |
|---|---|---|---|---|
| A 正确决策 | 6 | 6 | 0 | 0（A5 附一条实测要求） |
| B 必须修改 | 4 | 3 | 0 | 1（B1 的严重度需真机仪表） |
| C 其他契约 | 7 | 5 | 0 | 2（C2、C5） |
| D 附件一致性 | 6 | 5 | 1（+14 的口径） | 0 |
| E 执行顺序 | 1 表 | 完全采纳（新增 D0a） | 0 | 0 |
| F 测试 ID | 9 | 全部采纳，映射见 §4 | 0 | 0 |
| G 本轮指令 | 7 | 7 | 0 | 0 |

**结论：接受 B1–B4 四个阻塞项，其中 B3、B4 已在本轮以代码/文档落地（见 §2）。B1 的修复方向接受，但严重度需要真机仪表才能定级，本轮按"可能是 P0"处理。**

---

## 1. 关键调用链核验（本轮实测）

### 1.1 `TryRefreshRevision` 是否走到副作用 getter —— **是**

```
ActionRouter.HandleWrite                      src/Bridge/ActionRouter.cs:198
  → TryRefreshRevision()                      :468-482
    → StateSnapshot.BuildJson()               src/Bridge/StateSnapshot.cs:18
      → BuildState()                          :30
        → GameState.GetBargainSnapshot()      StateSnapshot.cs:88（第一处）
        → GameState.GetBargainSnapshot()      StateSnapshot.cs:181（第二处，BuildFlags）
        → SafeCanReport(client)               StateSnapshot.cs:302/310
```

`GetBargainSnapshot()`（`src/Game/GameState.cs:692-703`）每次调用 4 次 `CanOffer*`；
`CanOfferDiscount()` 的第一行就是 `Refresh()`（`_decomp/BargainUIManager.cs:660-663`），
`CanOfferMarkup`/`CanOfferBuyingMarkup`/`CanDemandBuyingDiscount` 同构。
`SafeCanReport` → `StoreClient.CanReport()` → `BargainUIManager.IsCurrentClientCoerced()` → `Refresh()`。

**所以：每一次写动作的 rev 门禁，都会在派发前调用约 9 次 `Refresh()`，包括"stale_rev 被拒绝"这条路径。**
外部审查 B1 的描述成立。

### 1.2 但是 `Refresh()` 的破坏力是**有条件的**（对 B1 严重度的修正）

`Refresh()` 只在 `trackedClient != client` 时重置状态位（`_decomp/BargainUIManager.cs:1103-1126`）：
交换引用时才清 `discountOffered`/`markupOffered`/…（约 15 个）并 `ClearTradeRepMultiplierOverrides()`。

- 同一顾客的整个会话内，`GetClient()` 返回同一 `StoreClient` 实例 → `Refresh()` 是**空操作**。
- 顾客切换后第一次刷新才真正重置——这本来就是游戏的正常行为。

**结论**：不是"每次回读都重置谈判状态"，而是"回读路径上跑了一个**有能力重置**的函数，且我们无法证明它永不重置"。
因此外部审查要求"证明纯度或移除"依然成立，但**定级应为 P1（待真机仪表）**，而不是已确认的 P0。

### 1.3 `MarkResponseUndelivered` 会覆盖已确认的完成 —— **是**

`ActionRouter.MarkResponseUndelivered`（`src/Bridge/ActionRouter.cs:244-287`）对已缓存的响应执行
`_gate.Replace(actionId, 原响应, 新的 outcome_unknown)`，并写历史 `result_status=outcome_unknown`。
也就是说：**业务已 completed，但 HTTP 响应没送达 → 缓存被改写成 unknown**。
保守（不会误报成功），但丢失了"其实已经完成"这个事实。C1 成立。

### 1.4 模式门禁的现状 —— 部分有，普通 op 没有

- 已有：`RecordingDispatchGuard` 拦截 `act→debug`、录制期 `debug_*`、并把
  `new_game/load_game/save_game/press/sleep` 收进录制策略（`src/Bridge/RecordingDispatchGuard.cs:20-110`）。
- 缺口：`act` 在 `PawnMindMod.cs:239-248` 会把内部 op **重新派发**到 `DispatchAction`，
  普通业务 op（例如 `bargain_preview`）没有模式判定。直连 HTTP 可越过 MCP 的 `--tools`。
- 本轮新增 `agent/tool_policy.py`，把"每个模式允许哪些工具"变成**一份可测试的注册表**（见 §2.3）。

### 1.5 工具清单口径 —— 以真实 `tools/list` 为准

| 项 | 实测 | 证据 |
|---|---|---|
| full | **114** | `audit/tools-inventory.json` |
| recording | **104** | 同上 |
| 差集 | 10 | 同上 |
| recording ⊂ full | 是 | `agent/tests/test_tool_policy.py::test_recording_is_a_subset_of_full` |
| `get_operation` / `wait_operation` | **不存在**（计划新增） | 同上 |
| `bargain_preview` 在 recording 中 | **是**（本轮已在 strict 名单中排除） | `tool_policy.STRICT_DENY` |

---

## 2. 逐条处置

### A. 正确决策（6/6 agree）

| # | 内容 | 处置 | 备注 |
|---|---|---|---|
| A1 | 复用 `ActionGate` 而非新 tracker | **agree** | ADR-PP-2 已如此；本轮补：`Entry` 需增加 `execution_status`/`delivery_status` 两轴 |
| A2 | durable intent 在 Python，执行证据在 C#，不承诺 exactly-once | **agree** | ADR-PP-1 |
| A3 | rev 与守卫在同一主线程同步链 | **agree + 修正** | 链本身成立（`MainThreadPump.Update → Tick → HandleWrite`），但该链里含副作用 getter（§1.1）。修复顺序按 B1 提前 |
| A4 | 模式白名单由 C# 负责，`act` 展开后再校验 | **agree** | 现状见 §1.4；本轮先落地注册表，C# 侧 `ModePolicy` 在下一轮 |
| A5 | `ok:true` 只表示完成 | **agree** | 但 DSH 对 `ok:false + status:pending` 的处理未见实测 → 列入 C2 |
| A6 | 先做负向实机测试，L3 作为后续能力 | **agree** | 计划 v2 把 L3 移出阻塞项，但"被放行的工具必须有真机用例"变成硬门槛 |

### B. 必须修改（4 条）

#### B1 / 阻塞：读快照副作用在 rev 门禁链上 —— **agree（含严重度修正）**

- **证据**：§1.1 完整调用链；§1.2 的 `Refresh()` 条件性。
- **本轮动作（文档）**：计划 v2 新增 **D0a**，排在 D1 之前；ADR 增补"快照纯度"为写路径的前置条件。
- **修正后的验收**（比外部审查更精确）：
  - 静态：`/state` 与 `TryRefreshRevision` 路径上的 `Refresh()` 调用数 = **0**（用 Core 可测的"快照构建器"抽象，
    在 fake 上断言调用次数，而不是断言 rev 不变）；
  - 真机：`PP-READ-01`（连读 10 次，谈价状态位/UI/RNG 计数不变）、`PP-READ-02`（过期 rev 拒绝后同样不变）。
- **为什么不能只测 rev**：`rev` 是序列化指纹（`StateSnapshot.cs:11-25`），隐藏状态变了它也不变——外部审查这一点完全正确。
- **不做**：不承诺"立刻把 `Refresh()` 全部拿掉"，因为 `CanOffer*` 的语义依赖它；正确做法是**不在快照里调用**，
  改由"玩家打开谈价面板时才需要"的懒加载字段提供。

#### B2 / 阻塞：`confirm_trade` 的提交与对话不能共享一个 pending —— **agree**

- **采纳第一种方案**：`confirm_trade` 在成交证据充分时 `completed`，并返回
  `next_required_action="advance_dialogue"`、`workflow_phase="post_trade_dialogue"`；
  `client_departed` 属于**顾客工作流**的终态，不是成交 operation 的完成条件。
- **理由（本地证据）**：`ConfirmTrade` 只调用一次原生 `OnOfferAcceptClick` 就返回；
  送客由对话结束后 `DialogUIManager` 完成。若把"顾客离店"当 pending，则 `advance_dialogue` 会被冲突域锁住，
  形成协议死锁——外部审查的判断成立。
- **测试**：`PP-TRADE-01`（成交→对话→送客→可接客）、`PP-TRADE-02`（重复 `confirm_trade` 必拒且零转移）。
- **本轮动作**：计划 v2 的 D1 验收改写；ADR 增补"operation 完成 ≠ 工作流完成"。

#### B3 / 阻塞：`get_clientele` 不得进 strict —— **agree，本轮已落地**

- `agent/tool_policy.py::STRICT_DENY` 已包含 `get_clientele` 与 `bargain_preview`；
  `strict_allowlist()` 从注册表生成，不再手写。
- 测试 `test_strict_list_drops_unproven_reads_and_privileged_tools` 断言两者不在 strict 名单。
- **仍未做**：MCP 的 `--tools recording` 仍会注册 `get_clientele`（当前录制面），
  需要在 D0 把"录制面 = strict allowlist"这一步接上；本轮只在策略层禁止，未改注册表。

#### B4 / 阻塞：白名单必须由已验证工具生成 —— **agree**

- 计划 v2 的 D6 改为：`strict_allowlist = {tools with evidence_id, no open P0/P1 on main path, mode policy check}`，
  并且由 `tool_policy.strict_allowlist()` 生成、由测试比对，**不再手写清单**。
- 当前矩阵：58 写 op 中 `open` 30、`partial` 2、`unverified` 1 → 这些工具在修复前**不得**进入 strict。

### C. 其他契约（7 条）

| # | 内容 | 处置 | 本轮动作 / 证据 |
|---|---|---|---|
| C1 | `execution_status` 与 `delivery_status` 分离 | **agree** | §1.3 证据；ADR-PP-2 增补两轴，`Replace` 只改 `delivery_status`，保留 `execution_status=completed` |
| C2 | `ok:false,status:pending` 不能被 DSH 当 `isError` | **needs_evidence** | 未跑 DSH E2E；列入 D1 的 `PP-TRADE-04` 扩展，需真实宿主会话 |
| C3 | `ModePolicy` 在最终分发入口校验（含 `act` 展开） | **agree** | §1.4；本轮登记表 + 测试，C# 侧下一轮 |
| C4 | `sleep` 的 Hook 是过程信号，不等于落盘 | **agree** | 现有实现只看 `last_save_time`；D2 增 `save_completed` 与 `day_changed` 两字段，`UNKNOWN` 时不得宣称保存成功 |
| C5 | 快照字段的信息可见性 | **needs_evidence** | 已有 `HiddenInfoGuardTests`（隐藏字段不导出），但没有"字段级 visibility/source"证据；列入 D3 附带任务 |
| C6 | `not_run` 不能作为放行理由 | **agree** | 计划 v2 明确：白名单内工具的真机 P0 用例必须 PASS，否则禁用该工具 |
| C7 | 二进制不一致，修复前不得归因 | **agree** | 现状：已安装 `e9e1bd07…`，`dist/` 本轮重建为 `ad335987…`；D0 必须先重装并核对 `/health` |

### D. 附件一致性（6 条）

| 指出 | 处置 | 核实结果 |
|---|---|---|
| full 114 / recording 104，差集 10 | **agree** | 与 `tools-inventory.json` 一致 |
| "相对上一轮 100 个，实际新增 14" | **reject（口径）** | 本地记录：加 `dismiss_current_client` 前 full=109，加上后 110；再加 4 个记忆工具 = 114。**没有 100 这个基线**，请以 git 历史为准。结论"新增不止 4 个"本身成立（110→114 是 4 个，但 109→114 是 5 个；取决于起点） |
| `tool-matrix(2).md` 只有 102 个具名工具 | **agree** | 该文件是旧产物，已由 `audit/PLAYER_ENTRY_MATRIX.csv`（62 行，逐写 op）与 `tools-inventory.json`（114 工具）取代 |
| recording 仍含 `bargain_preview` | **agree** | 本轮在策略层排除；注册表仍导出，待 D0 决定是否从 recording 工具集移除 |
| `get_operation`/`wait_operation` 不存在 | **agree** | 计划新增，不计入现况 |
| tools JSON 缺完整 schema | **agree，已修** | 已重新生成 `tools-inventory.json`，含 `description`/`required`/`properties`/`readOnlyHint` |

### E / F / G

- **E（执行顺序）**：完全采纳，**新增 D0a**（快照纯度）作为 D1 前置；D2 的 `not_run` 退出条件按 C6 收紧。
- **F（测试 ID）**：9 条全部采纳，映射见 §4。
- **G（本轮指令）**：7 条全部执行。产出 `ROUND2_DISPOSITION.md`、`ADR-PLAYER-PARITY-v2.md`、
  `PLAYER_PARITY_IMPLEMENTATION-v2.md`；`get_clientele` 移出 strict；D0a 顺序调整；
  `confirm_trade` 两边界定义；工具登记表 + 一致性测试。

---

## 3. 本轮实际改了什么

| 文件 | 类型 | 内容 |
|---|---|---|
| `ROUND2_DISPOSITION.md` | 新增 | 本文 |
| `ADR-PLAYER-PARITY-v2.md` | 新增 | 在 v1 基础上：D0a、两轴状态、workflow vs operation、allowlist 生成 |
| `plans/PLAYER_PARITY_IMPLEMENTATION-v2.md` | 新增 | D0–D6 修订版（含 D0a、C6 收紧） |
| `agent/tool_policy.py` | 新增 | 纯度/特权/严格名单的**唯一**注册表 |
| `agent/tests/test_tool_policy.py` | 新增 | 6 条一致性测试（recording ⊂ full、strict 排除、deny 优先） |
| `audit/tools-inventory.json` | 新增 | 真实 `tools/list` 全量快照（含 schema 字段） |

**未做（明确记录）**：C# `ModePolicy`、D0a 代码修复、`get_operation`/`wait_operation`、真机测试。
这些都需要下一轮，且 D0a 需要真机仪表验证。

---

## 4. 测试 ID 映射

| 外部 ID | 本地对应 | 状态 |
|---|---|---|
| PP-READ-01 | `test_snapshot_purity`（待建，Core 层断言快照构建器调用 0 次 `Refresh`）+ 真机 | **待建** |
| PP-READ-02 | `P0-REV-01` 扩展（stale rev 拒绝后零副作用） | **待建** |
| PP-TRADE-01 | `PP-TRADE-01`（fake + 真机） | **待建** |
| PP-TRADE-02 | 同上，重复提交拒绝 | **待建** |
| PP-TRADE-03 | 7 个静默分支各一条具名拒绝 | **待建**（B10–B16） |
| PP-ASYNC-01 | `end_day` pending → `wait_operation` → completed | **待建** |
| PP-ASYNC-02 | completed + 响应未送达 → 保留 completed（两轴） | **待建**（当前会改成 unknown，§1.3） |
| PP-MODE-01 | 直连 HTTP / `act` 绕过 | **待建** |
| PP-TOOLS-01 | `test_tool_policy.py` 已覆盖集合一致性 | **部分完成** |
| PP-RECORD-01 | 预检脚本 + `/health` MVID 比对 | **部分完成**（脚本已有，本轮未跑） |

---

## 5. 未验证项（`not_run` / `UNKNOWN`）

1. 运行中游戏的 MVID 与版本（游戏未启动）。
2. `PP-READ-01/02` 的真机仪表（需要插桩或 UI 探针）。
3. DSH 对 `ok:false + status:pending` 的实际处理（C2）。
4. `get_state` 各 getter 的副作用全集（本轮只核实了 `CanOffer*` 与 `can_report`）。
5. `dist/` 与已安装 DLL 的差异来源（只知哈希/大小不同，未定位到具体改动）。
