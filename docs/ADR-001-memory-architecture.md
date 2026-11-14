# ADR-001（修订 A）：长期记忆与决策系统接入架构

- **状态**：已接受（P0 修订，用户有条件通过）
- **日期**：2026-11-15
- **基线 commit**：`8f64166`（截图取证特性之后）
- **前版**：P0 初审稿（已在本次修订中整体重写）
- **输入**：[CAPABILITY_AUDIT.md](<CAPABILITY_AUDIT.md>)、[P0_REMEDIATION.md](<P0_REMEDIATION.md>)、[架构规范 v1](<Probably_Stolen_Agent_Memory_Architecture_v1.md>)

---

## ADR-001：宿主路径选 A（保留 DSH，旁挂 Memory MCP）

**决策**：DSH 是唯一 Agent 宿主与唯一决策循环；新增独立 stdio MCP 服务承载记忆。**不实现** Orchestrator。

**理由**：DSH 已支持多 MCP 服务；自建循环等于重造宿主；规范 §2.1 警告不要有两个控制循环。

**代价**：`memory_bootstrap` 只能靠剧本硬性要求，不是程序强制。缓解：`memory_status` 暴露本次会话检索次数供事后审计。

**复审条件**：P5 显示“未检索决策率” > 20%。

---

## ADR-002：事件记录埋点在 Python 客户端，不在 Unity

**决策**：recorder 包裹 `BridgeClient` 的三个 HTTP 方法，不改 C# Mod。

**取证**：102 个 MCP 工具全部只经过 `client.action()`（96）、`client.get_state()`（5）、`client.wait_events()`（1），`agent/` 下无旁路 HTTP。

**代价**：记录不到游戏自身变化（顾客到访、自动存档、租金结算）→ 必须标 `capture_quality=partial`、`external_state_change`，**不得归因**。

**复审条件**：出现绕过 Python 客户端的消费者。

---

## ADR-003：SQLite 存储，但不用 FTS5 做中文召回

**决策**：SQLite 单文件（`play/memory/memory.db`），v1 不建 `memories_fts`；检索在 Python 层做硬过滤 + 标签/实体匹配 + 子串匹配。

**理由（实测）**：`trigram` 要求 ≥3 字符，查「租金」→ 0 条；`unicode61` 不切分 CJK。静默漏召回比慢更危险。

**复审条件**：记忆数 > 10k 或评测召回不达标。

---

## ADR-004（修订）：作用域近似 + **内部 timeline_id**

**决策**：在 P0 初稿基础上新增 `timeline_id`，用于隔离读档/回档/槽复用产生的历史分支。

### 作用域字段

| 规范字段 | 我们的映射 | 可得性 |
|---|---|---|
| `game_id` | `probably_stolen` | 恒定 |
| `game_version` | `/health.game_version` + MVID | 随时可读 |
| `instance_id` | 配置项，默认 `local-01` | 恒定 |
| `save_id` | `slot:<configured_slot>`（**近似，非游戏 UUID**） | 配置可得 |
| `run_id` | `binding.run_id` | **仅绑定后可得** |
| `agent_session_id` | 会话 uuid | 可得 |
| **`timeline_id`** | **本系统生成，见下** | 系统自有 |

### timeline_id 规则（本次新增）

```
on_bind(slot, run_id):
    timeline_id = uuid4()
    parent_timeline_id = last_timeline_for_slot(slot, run_id)  # 可能为 null
    reason = "new_game"

on_load(slot):
    if load 后 run_id/run_number 与当前绑定一致:
        reason = "rollback"          # 同一 run 回档
    else:
        reason = "external_replace"  # 槽内容被换过（例如用户自己换了档）
    timeline_id = uuid4()
    parent_timeline_id = 当前 timeline
    # 旧 timeline 永久保留，不删除、不合并
```

**约束**

1. 每条事件与每条记忆都带 `timeline_id`；检索默认只查当前 timeline。
2. **跨 timeline 的现金、库存、任务进度一律不可见**（对应规范 §4.1 的禁止项）。
3. 跨 timeline 只允许共享 `scope=game_version` 的机制级经验，且必须显式请求。
4. 槽复用（删档后同槽重开）通过 `run_id` 不同被识别为 `external_replace`，不继承旧进度。
5. `timeline_id` 是**我方概念**，游戏不知道它；ADR 明确声明这是补充而非游戏事实。

**理由**：规范要求 `fork_from_event_id` 与 lineage 防泄漏，但游戏不提供存档树。本地生成 timeline 是最小可行替代；没有它，回档后的旧记忆会被当成“当前事实”，是规范明令禁止的。

---

## ADR-005（修订）：Evidence Integrity 与 Claim Support 分离

**决策**：保留确定性规则校验，**不引入第二个 LLM Validator**（`memory_validator.md` 保留但不作为 v1 必需组件）。校验必须区分两件不同的事。

### 两个独立判定

| 判定 | 回答的问题 | 输入 | 通过条件 |
|---|---|---|---|
| **Evidence Integrity（证据完整性）** | 这条记忆引用的证据**真的存在且合法**吗？ | `event_id` 集合 | 全部事件存在、属于同一 `timeline_id`、`capture_quality` 达标、未被标记为伪造 |
| **Claim Support（主张支持度）** | 这些证据**足以支持这个结论**吗？ | 记忆 `kind` + `conditions` + 证据数量与类型 | 见下表 |

**关键约束**：Evidence Integrity 通过 **不等于** Claim Support 通过。反过来也一样。

### Claim Support 规则（v1）

| kind | 最低支持 | 最高可达状态 |
|---|---|---|
| `fact` / `price_observation` | 1 条 `complete` 证据，且观测值直接出现在结果里 | `verified`（仅限该次观察范围） |
| `mechanism` / `strategy` | **2 条独立事件** 或 1 条事件 + 1 条 `external` 来源 | 最多 `hypothesis` |
| `lesson` | 2 条独立事件（不同时间/不同对象） | 最多 `hypothesis` |
| `procedure` | 1 条事件 + 成功回读 | 最多 `hypothesis` |
| `npc_profile` | 1 条 `complete` 事件 | `verified`（该 NPC 该次行为） |
| `warning` | 1 条 `complete` 事件（失败/拒绝） | `verified`（该条件范围内） |

**两条独立事件不能自动证明因果。** 仅有相关性观察的 `mechanism`/`strategy` **必须保持 `hypothesis`**，且必须记录 `competing_explanations`；只有满足下列之一才可复核后升级：

- 受控对比（同条件、仅一个变量不同，两次以上）；
- 游戏内权威机制文本（`source_type=in_game_text`，且记录书名/页/版本）；
- 官方更新说明（`source_type=official`，记录 URL、抓取日期、适用版本）。

### 外部资料要求

任何 `source_type=external` 的记忆必须写全：

```json
{"source_type":"external","url":"…","retrieved_at":"…","trust":"medium|low",
 "applies_to_version":"VERSION 100-Rev13","claimed_by":"…"}
```

- 缺任一字段 → 只能 `hypothesis`，且 `trust` 不得为 `high`。
- `applies_to_version` 与当前版本不符 → `stale`，默认不返回。

### conditions 强制

`kind ∈ {mechanism, lesson, strategy, procedure}` 且 `conditions` 为空 → 强制 `hypothesis`，不得晋升。

---

## ADR-006：不做 Planner，`play/AGENTS.md` 就是决策循环

**决策**：不实现目标树与独立 Planner；程序只提供事件记录与记忆读写。

**代价**：无法程序化强制“先检索后行动”（见 ADR-001 复审条件）。

---

## ADR-007：明确不做的部分

向量库 / 图数据库 / 跨版本 `game_family` 记忆 / 独立反思 Worker / v1 的 A-B-C 评测 / 改 C# 游戏代码。

---

## ADR-008（新增）：Recorder 的记录判定**不按 HTTP 方法**

**决策**：是否记录、记录成什么类型，由**语义分类表**决定，不由 `GET`/`POST` 决定。

**理由**：`/action` 上同时存在 42 个只读 op 和 52 个改状态 op；按方法分类会漏记或误记。

### 分类规则（按优先级）

1. **只读 op**（`ActionRouter.ReadOnlyOps` 40 项 + MCP 侧 8 项 `/state`·`/events` 读）→ `event_type=observation`，不产生 `intent`，不参与对账。
2. **改状态 op** → 产生 `intent`（先落盘）→ 调用 → `result` → 可选后置快照 → `reconcile`。
3. **异步完成 op**（`sleep`）：见 ADR-009。
4. **写盘 op**（`confirm_new_game`、`save_game`、`save_recording_game`）：结果必须回读确认，`unknown` 一律不重试。
5. **只读但会推进状态**：当前无已知案例；若发现，登记为 `observation_with_effect` 并在审计中单列。

### 必须处理的四类边界

| 边界 | 处理 |
|---|---|
| 数据库故障 | `intent` 写失败 → **拒绝执行该动作**（fail closed）；`result` 写失败 → 内存缓冲 + 重试，超过上限则标记 `capture_quality=partial` 并在 `memory_status` 暴露丢事件计数 |
| 跨进程并发 | 单一 writer（文件锁 `fcntl.flock` 或 SQLite `BEGIN IMMEDIATE`）；`seq` 由数据库分配，不用内存计数器 |
| 短生命周期 | MCP 子进程每次会话重建 → 所有状态落盘，内存只做缓存 |
| 未知结果 | `unknown` 立即落盘并阻止同 `action_id` 重放；由人工/下一次只读观察 reconcile |

---

## ADR-009（新增）：`sleep` 等异步动作的完成判据

**决策**：`sleep` 的 `result` 只表示**请求已下发**；完成必须由**观察**确认。

```
sleep 返回 slept=true
  → 写 event(dispatched, result=sleep_requested)
  → 轮询 /state（或 wait_for_events）直到 day > 采样前的 day
      → 观察到跨日：写 event(sleep_completed, delta.day=+1)
      → 超时仍未跨日：写 event(sleep_unresolved)，禁止再次 sleep、
        禁止记为成功，交人工核对
```

**硬约束**

- 不得把 `day_advanced=false` 判为失败（原生是 0.5s 黑屏协程）。
- 不得因超时重发 `sleep`。
- 同一天出现第二次 `sleep` 请求 → 记 `suspicious_repeat` 并拒绝执行。
- **P1 不触发任何 LLM 反思**；只记录事实与未决状态。

---

## ADR-010（新增）：action_id 生命周期与执行顺序

**决策**：固定五步，顺序不可交换。

```
1. 生成 action_id (uuid4)
2. 写 intent 行（run/timeline/tool/args/rev/action_id/status=pending）  ← 必须先落盘
3. 发起 HTTP 调用（携带同一 action_id）
4. 写 result 行（status: success | rejected | failed | unknown）
5. 可选：读后置 /state 写 snapshot 与 delta；对账
```

**规则**

- **intent 必须在 dispatch 之前持久化**。若 intent 写失败 → 不发起调用（fail closed）。这样崩溃后一定能看到“有一条可能已下发的动作”。
- `action_id` 由客户端生成并复用为 `idempotency_key`；服务端幂等缓存不跨重启，因此**重启后的重放风险由事件日志承担**：启动时扫描 `status=pending` 的记录，转为 `unknown` 并要求核对。
- `unknown` 不得自动重试，也不得换新 `action_id` 重放。
- 只读 op 不写 intent（避免日志膨胀），但可写 `observation` 行。

---

## ADR-011（新增）：`act` 与 `debug_*` 的封闭

**决策**：`act` 泛化入口**永远**不能到达 5 个 `debug_*`；录制态下**直接调用** `debug_*` 也被拒绝。

**背景（P0 修订发现的真实漏洞）**：`act` 在 C# 中递归调用 `DispatchAction(inner)`，而 `RecordingDispatchGuard.IsProtected` 只覆盖 8 个 op，`debug_*` 不在其中 → `act{action:"debug_grant"}` 可直达作弊入口。

**实现**（[RecordingDispatchGuard.cs](<../PawnMind/src/Bridge/RecordingDispatchGuard.cs>)）：

- `IsDebugOp` 列出 5 个 op；
- `viaAct && IsDebugOp` → `recording_act_wrapper_forbidden`；
- 直接调用且 `service.RecordingActive` → `recording_debug_forbidden`；
- 录制策略为 `Disabled` 时保留给本地开发。

**验证**：`Guard_Act_Cannot_Reach_Debug_Ops` 等 3 项测试（见 [P0_REMEDIATION.md](<P0_REMEDIATION.md>)）。

---

## ADR-012（新增）：工具风险等级与真机验证状态分离

**决策**：矩阵中 `risk` 与 `evidence` 是两列独立事实，不得互相推导。

- `risk` 由代码路径与副作用决定（read / ui / low / medium / high）。
- `evidence` 由真机观察决定（`yes` / `partial` / `no` / `n/a`）。
- 一个 `high` 工具可以 `evidence=yes`，一个 `low` 工具也可以 `evidence=no`。

**同时修复**：`wine_add_water` 的 `rev` 原为可选（`rev: int = None`），与“写动作必须带 rev”矛盾；已改为必填，并新增契约测试 `test_write_rev_required.py` 防止回归。

---

## 附录：决策一览

| 编号 | 决策 |
|---|---|
| ADR-001 | 路径 A：DSH + 旁挂 Memory MCP |
| ADR-002 | 埋点在 Python `BridgeClient` |
| ADR-003 | SQLite 无 FTS5 |
| ADR-004 | 作用域近似 + **timeline_id** 隔离分支 |
| ADR-005 | **Evidence Integrity ≠ Claim Support**；规则校验；因果需受控证据 |
| ADR-006 | 不另做 Planner |
| ADR-007 | 不做向量库/图库/跨版本 |
| ADR-008 | Recorder 按**语义**分类，不按 HTTP 方法 |
| ADR-009 | `sleep` 以**观察到的跨日**为完成判据 |
| ADR-010 | intent 先落盘，再 dispatch；`unknown` 不重放 |
| ADR-011 | `act` 永不可达 `debug_*` |
| ADR-012 | 风险等级与真机证据分离；`rev` 必填 |
