# ROUND3_DISPOSITION — 对《下一阶段工程任务书》的逐项处置

- **输入**：`PawnMind/docs/PAWNMIND_AGENT_BRIEF_NEXT_PHASE_2026-11-17.md`（422 行，Round 3 Candidate 1）
- **执行范围**：仅第 7 节「第一批」——审阅 + S0/S2 静态原型，**未改任何业务逻辑，未启用录制**
- **基线**：`f68f60e`（含本批改动前的 HEAD）
- **证据等级**：`F` 文件可证实 / `R` 仓库需验证（本轮已核）/ `E` 实机需验证（本轮 `NOT_RUN`）/ `H` 外部假设

| 结论 | 条数 |
|---|---|
| `ACCEPT` | 27 |
| `ACCEPT + 标注` | 6 |
| `NEEDS_EVIDENCE` | 9 |
| `REJECT` | 2 |

---

## A. §0 执行契约（7 条）—— 全部 `ACCEPT`

| # | 条款 | 处置 | 落实证据 |
|---|---|---|---|
| A1 | 先核对事实，再决策，再最小差异实现 | ACCEPT | 本轮先做 `BASELINE_CURRENT.md`，未动业务代码 |
| A2 | 不碰真实存档 / 不用 debug、`act`、`press` | ACCEPT | `tool_policy.json` 把 10 个特权工具标 `privileged` + `deny` |
| A3 | durable intent 先落盘后派发；unknown ≠ failed；不换 `action_id` 重试 | ACCEPT | 已有 recorder fail-closed + `ActionGate`；文档 `ADR-PP-2` 两轴改造待 S3 |
| A4 | 不新建第二套 Agent 主循环 | ACCEPT | 无新增 orchestrator/planner；只加策略源 |
| A5 | strict 默认拒绝，缺证据不放行；MCP 与 C# 同策略 | ACCEPT（部分落地） | 策略源已 `deny` 默认；**C# 侧未落地**（见 C2） |
| A6 | 不以“测试全绿”替代真机语义验证 | ACCEPT | 报告分列 fake/contract/live，live 全 `NOT_RUN` |
| A7 | 设计与代码不符时提交 `REJECT/MODIFY/NOT_RUN`，不为遵循文档重写项目 | ACCEPT | 见 D/E 两条 `REJECT` |

---

## B. §1.2 对 `tool_policy.py` 的独立检查 —— 全部 `ACCEPT`（缺陷属实）

| 指认 | 本轮核实 | 处置 |
|---|---|---|
| `purity()` 对 `readOnlyHint=null` 默认 `write`，大量读被误分类 | **属实**：114/114 的 `readOnlyHint` 均为 null，旧实现把 54 个读标成 write | ACCEPT，已重写 |
| `strict_allowlist()` 没有证据/版本/开放缺陷门禁 | **属实**：只按 purity 过滤 | ACCEPT，改为声明式策略 + `validate()` |
| `strict` 不含 `get_state` 却无“何时、凭何证据重新准入” | **属实** | ACCEPT：`get_state` 标 `d0a_pending` + `needs_evidence`，写进策略文件与决策表 |
| `strict` 仍含 `bargain` / `buy_wholesale` / `commissary_buy` / `move_item` | **属实**：它们矩阵状态 `open` | ACCEPT：改为 `needs_evidence`，不进 `play` |
| 附件统计口径（114/104、strict=100、差集四项） | **属实**，与 `tools-inventory.json` 一致 | ACCEPT |

> 任务书的判断准确："错误不是先禁用，而是未安排何时凭何证据重新准入。"本轮把"重新准入"写成 `status` + `evidence` 字段。

---

## C. §1.3 待复核的仓库判断（9 条）

| # | 判断 | 处置 | 本轮核实 |
|---|---|---|---|
| C1 | `TryRefreshRevision → BuildJson → CanOffer*/CanReport → Refresh()` | **ACCEPT** | 调用链属实：`ActionRouter.cs:198 → :468 → StateSnapshot.cs:18 → :88/:181/:302`；`BargainUIManager.cs:660-663` 首行即 `Refresh()` |
| C2 | 上述副作用是“有条件”的 | **ACCEPT（修正严重度）** | `Refresh()` 仅在 `trackedClient != client` 时重置（`:1103-1126`）→ P1/待仪表，不是已确认 P0 |
| C3 | `rev` 是快照指纹，不含 UI/RNG/隐藏状态 | **ACCEPT** | `StateSnapshot.cs:11-25` |
| C4 | `ActionGate` 进程内缓存；`MarkResponseUndelivered` 可能覆盖已完成 | **ACCEPT** | `ActionRouter.cs:244-287` 确实 `Replace` 为 unknown |
| C5 | `confirm_trade` 已走 `OnOfferAcceptClick`，仍有 B10–B16 | **ACCEPT** | `GameActions.ConfirmTrade` 校验后调 `AcceptNegotiationOffer`；7 个分支未前置检查 |
| C6 | `sleep` 已通过 `DayEndPolicy` 复用 `CanEndDay()` | **ACCEPT** | `RecordingSaveService.cs:254` → `DayEndPolicy.cs:31-41`；测试 `RecordingSaveServiceTests.cs:387` |
| C7 | `ModePolicy` 只覆盖部分入口，普通 op 与 `act` 重派发需最终校验 | **ACCEPT** | `RecordingDispatchGuard.cs:20-110` 覆盖 debug/存档；`PawnMindMod.cs:239-248` 的 `act` 无模式判定 |
| C8 | 构建 DLL 与已安装 DLL 哈希不一致 | **ACCEPT** | `dist` `ad335987…`(296,960B) vs 安装 `e9e1bd07…`(295,424B) |
| C9 | D5 可后置，但 P0 必须保留针对性真机验证 | **ACCEPT** | 计划 v3 保持此顺序 |

---

## D. §2 两条产品轨道 —— `ACCEPT`（含一条 `REJECT` 的范围声明）

- **主轨 R（研究型可验证 Agent）**：`ACCEPT`，本轮不启动次轨。
- **次轨 P（Workshop Beta）**：`ACCEPT` 作为后续条件项，**本轮不做**。
- **`REJECT`**：不接受"先证明工具覆盖广"作为价值主张——与 §6 的"优先产品指标"一致，工具数不进对外的成功指标。

---

## E. §3 阶段与依赖（S0–S7）—— `ACCEPT`（附两处 `MODIFY`）

| 阶段 | 处置 | 修改点 |
|---|---|---|
| S0 基线冻结 | **ACCEPT** | 本轮产出 `BASELINE_CURRENT.md` |
| S1 / D0a 快照纯度 | **ACCEPT** | 前置为"play 模式启用"的硬条件（与任务书一致） |
| S2 / ModePolicy | **ACCEPT + MODIFY** | 本轮只交付**声明式策略源 + 生成器 + 测试**；C# `ModePolicy` 给接口与插入点，不改业务代码 |
| S3 / D1 交易 | **ACCEPT** | 按 ADR-PP-9：成交 `completed` + `next_required_action`，`client_departed` 属工作流 |
| S4 / D2 跨日 | **ACCEPT + MODIFY** | 增加 `save_completed` 与 `day_changed` 两字段，禁止合成一个 `ok` |
| S5 / 录像 | **ACCEPT** | 未过 S1–S4 不启动 |
| S6 / 分发 | `NEEDS_EVIDENCE` | 需干净 Windows 环境与官方 Scanner |
| S7 / 记忆增益 | `NEEDS_EVIDENCE` | 需 A/B 设计；本轮不投入 |

**施工纪律**（"`get_state` 未纯时不得把完整 strict 接给真实 Agent"）**完全接受**，已写入策略：`play` 模式对 `get_state` 为 `false`。

---

## F. §4 回归矩阵 —— `ACCEPT`（22 个 ID 全部登记）

| 组 | ID | 本轮状态 |
|---|---|---|
| 工具/策略 | `PP-TOOLS-01/02/03` | **部分 PASS**：`PP-TOOLS-01/02` 由 `test_tool_policy.py`（12 条）覆盖；`PP-TOOLS-03` 由策略 `needs_evidence` 覆盖 |
| 模式 | `PP-MODE-01/02` | `NOT_RUN`（需 C# ModePolicy） |
| 读纯度 | `PP-READ-01/02/03` | `NOT_RUN`（D0a 未做） |
| 交易 | `PP-TRADE-01/02/03/04` | `NOT_RUN` |
| 异步 | `PP-ASYNC-01/02`、`PP-SAVE-01` | `NOT_RUN` |
| 店铺 | `P0-LANDLORD-02`、`P0-OPEN-01` | `NOT_RUN`（真机） |
| 发布 | `PP-RECORD-01`、`PP-MODSCAN-01` | `NOT_RUN` / `NOT_RUN` |
| 端到端 | `PP-PLAY-01` | `NOT_RUN` |

---

## G. §5 发布兼容性 —— `NEEDS_EVIDENCE`

- 任务书说官方规则计划 2026-12-10 收紧 `reflection-external` / `dynamic-code` / `native` / `threads`。
  这是**文档中的计划**（`H`），本轮无法验证部署结果。
- 本轮**没有**运行官方 Mod Scanner，也**没有**提交扫描输出 → `PP-MODSCAN-01 = NOT_RUN`。
- 不得据此宣称"发布就绪"。

---

## H. §6 竞品观察 —— `ACCEPT`（附 `REJECT`）

- `ACCEPT`：指标优先（首周完成率、事故回归率、非法副作用数、人工接管次数、首次连接成功率、成本），
  不以工具数量/订阅数作为成功指标。
- **`REJECT`**：任务书里出现的"2–4 周窗口""Python 劝退 90%""领先 3–9 倍"等数字，**不写入任何对外结论**；
  本轮未做独立测量。

---

## I. §7 第一批交付 —— 逐项状态

| 要求 | 状态 | 产物 |
|---|---|---|
| 1. 逐项处置 | **DONE** | 本文 |
| 2. 基线 | **DONE** | `docs/audit/BASELINE_CURRENT.md` |
| 3. 修正后的策略源或生成器 | **DONE** | `agent/policy/tool_policy.json` + `agent/tool_policy.py` |
| 4. 扩展测试 | **DONE** | `agent/tests/test_tool_policy.py`（12 条） |
| 5. 逐工具 allow/deny/needs_evidence | **DONE** | `docs/audit/STRICT_ALLOWLIST_DECISIONS.md` |
| 6. ModePolicy 最小接口/插入点 | **DONE（设计）** | 见下 §J；**未改 C#** |
| 7. 报告 | **DONE** | `docs/reports/ROUND3_REPORT.md` |

**硬停止点遵守情况**：未启用真人录制；未宣称 strict 可用（`live_ready` 为空）；未覆盖任何存档。

---

## J. §7.6 `ModePolicy` 最小接口与插入点（设计，未实现）

```csharp
// src/Core/ModePolicy.cs  —— 纯逻辑，可单测
public enum RunMode { Bootstrap, Play, Test }
public static class ModePolicy {
    // allowlist 由 policy.json 生成后注入；未知 op 一律 false
    public static bool Allows(RunMode mode, string op);
    public static string RefusalCode { get; }   // "mode_forbidden"
}
```

插入点（按优先级）：

1. `PawnMindMod.DispatchAction` 入口：`normalize(op, args)` → `ModePolicy.Allows` → dispatch。
2. `act` 分支（`src/PawnMindMod.cs:239-248`）：展开 `inner` 后**再次** `Allows`。
3. `ActionRouter.HandleWrite` 之前：拒绝必须在 `TryRefreshRevision()` **之前**返回，保证零副作用。
4. `RecordingDispatchGuard`：保留现有 `debug_*`/存档拦截，作为第二层。

测试脚本（待实现）：`agent/tests/test_mode_policy.py` 对 HTTP 层发 `bargain_preview`/`save_game`/`act{action:"confirm_trade"}`
三种路径，断言 `mode_forbidden` 且状态未变（`PP-MODE-01`）。

---

## K. `NEEDS_EVIDENCE` 清单（9 条）

1. 运行中游戏 MVID / `/health`。
2. `PP-READ-01/02` 真机或插桩仪表。
3. `PP-READ-03` 主路径 getter 全集审查。
4. `PP-TRADE-01..04`。
5. `PP-ASYNC-01/02`、`PP-SAVE-01`。
6. `P0-LANDLORD-02`、`P0-OPEN-01` 真机负向。
7. `PP-MODE-01/02`（需 C# 落地）。
8. `PP-MODSCAN-01` 官方扫描器输出。
9. DSH 对 `status:pending` 的真实处理（`PP-TRADE-04`）。
