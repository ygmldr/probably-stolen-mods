# R5 LLM PILOT PROTOCOL — 首周 LLM 自主经营试验协议（离线定义）

> 状态：`OFFLINE_PROTOCOL`（2026-11-18，R5-C）。
> **不是评测结果**：本文件不包含任何 LLM 通关率/效率数字；本轮所有 L3 项均为
> `NOT_RUN`，正式 Play benchmark 终态为 `BLOCKED_BY_PLAY_POLICY`（`play_allowlist` 为空）。
> 关联：[`../../skills/pawnmind-first-week/SKILL.md`](../../skills/pawnmind-first-week/SKILL.md)、
> [`R5_HANDOFF_2026-11-19.md`](../reviews/R5_HANDOFF_2026-11-19.md)、
> [`FIRST_WEEK_BENCHMARK_SPEC.md`](FIRST_WEEK_BENCHMARK_SPEC.md)、
> `agent/trace_validator.py`（v1.2.0 / schema v2）、`agent/trace_recorder.py`（C2）。

## 0. 目的与边界

本协议规定“让真实 LLM 在专用新局完成一局首周经营”的**执行与判定**方式，用于回答：

1. LLM 能否只凭玩家可见状态 + 首周 Skill + 工具回执，自行选择动作完成首周（Day 1→Day 8、付掉租金）；
2. 过程是否留下**可复核**的调用层证据（原始请求/回执 JSONL → validator 轨迹），
   而不是模型自述。

**不回答**：通关率、效率收益、多模型排名、RAG/长期记忆、强化学习。1 次成功只能称
“一次可行性证据”，不是“100% 通关率”。

## 1. 三层结构（严格分离）

| 层 | 职责 | 禁止 |
|---|---|---|
| **LLM 决策层** | 读玩家可见状态、Skill、当前目标；自主选择动作与参数（买卖、议价、送客、关店、付租金）；给出理由 | 不得访问隐藏字段（鉴定答案、隐藏身份、真实水质、隐藏财富）；不得预编排“每天固定动作” |
| **可信执行层** | 校验工具名、`rev`、`action_id`、操作状态、policy SHA、run mode、存档绑定；先记 intent 再派发；执行后回读 | 模型不得自行设置 `action_id`/`rev` 或绕过权限门禁；不得用模型文字当回执 |
| **恢复层** | 处理 `stale_rev`、`rejected`、`running`、`uncertain`、工具超时、重连、非终态列表 | `uncertain` 时默认只查询（`get_operation`/`wait_operation`/`get_state`），不得盲重试；进程重启后不可查询的操作标 UNKNOWN 并停止 |
| **评测层** | 只从调用层 JSONL 与真实游戏回执判结果 | 不得根据模型总结/`claim_text` 判成功 |

## 2. 模型与宿主登记（每次 run 必须记录）

| 字段 | 说明 |
|---|---|
| `model_id` | 模型/版本标识（宿主上报）；同一 run 不更换 |
| `host_version` | DSH / OpenCode / MCP 宿主版本 |
| `prompt_sha256` | 系统提示 + 回合指令的规范化哈希 |
| `skill_sha256` | `SKILL.md` 文件哈希 |
| `policy_sha256` | `/health.policy_sha256` 与 MCP 侧一致 |
| `tool_surface` | `full` / `recording` + 工具数（B1 后 full=117 / recording=107；以实际注册为准） |
| `trace_schema_version` | 当前 `2`（validator v1.2.0） |
| `recording_slot` | 专用槽绑定（只允许用户批准的槽） |
| `run_id` | 开局后由桥的真实 run identity 确认 |

> 任何一项无法核对 → **不开始**。这是 D0 前置。

## 3. 决策接口（模型每回合输出）

```json
{
  "goal": "本回合想达成什么（一句话）",
  "observation_refs": ["get_state.rev=123", "list_goods.total=7"],
  "proposed_action": { "tool": "stage_negotiation_item", "args": { "item_id": 56 } },
  "reason": "为什么选它（基于哪个可观察事实）",
  "expected_observation": "执行后应看到什么（可核对）",
  "stop_if": "什么情况下停止/换动作"
}
```

这只是**内部建议结构**，不改变 MCP 对外 schema。执行层规则：

- 工具必须在当前 `tool_surface` 且 policy 允许；不允许的工具直接拒绝并记录。
- `rev` 由执行层从最近一次状态读回填入；`action_id` 由执行层生成（每回合一枚，重放时同 id）。
- 模型不能直接读写 `_internal`、存档、token、日志文件。
- 每步执行后把**真实返回**与新快照送回模型；模型不能修改已记录的轨迹。
- 单步工具原则：一个工具一次只做一件事并回读（AGENTS.md）。

## 4. 预算与安全停止

每 run 必须声明并强制执行（达到即安全停止、保存轨迹、写停止原因）：

| 预算 | 建议默认 | 说明 |
|---|---|---|
| 单局动作数 | ≤ 300（可调，须在 run 前声明） | 含读操作；超过即停 |
| 连续被拒/`uncertain` | ≤ 5 | 连续卡死即停，交人工 |
| token / 成本 | 由宿主设定并记录 | 不可测 → `NOT_MEASURED` |
| 墙钟时长 | ≤ 90 分钟 | 超时停止 |
| 单回合 | 1 个写动作 + 若干读 | 不允许多写连发 |

停止时保留：JSONL、导出轨迹、最后状态、停止原因、人工介入记录。

## 5. 成功 / 失败判定（只用调用层证据）

一次 run 的判定以**调用层 JSONL → `trace_validator`** + 真实回执为准：

| 判定 | 条件 |
|---|---|
| `PASS_FIRST_WEEK` | 真实到达 Day 8（`get_state.day` + `day_changed` 证据）；首周租金支付回执完整（`last_rent_payment` 含 `id/amount/cash_delta/weeks_paid_delta/paid_at_rev`）；validator 0 ERROR |
| `PARTIAL` | 完成若干天/若干成交，但未达 Day 8 或租金未付；原因与证据齐全 |
| `FAIL` | 游戏内真实失败（破产/死亡/关键死锁）且有回执证据 |
| `UNKNOWN` | 轨迹缺失/记录器失败/无法查询操作 → 不允许统计为成功 |
| `BLOCKED_BY_PLAY_POLICY` | Play 门禁未通过（当前 `play_allowlist` 为空）→ 不是 PASS，也不是 FAIL |
| `L3 TEST_MODE_PILOT` | 允许 `run_mode=test` 的 pilot；若使用 `press` 必须标 `TEST_MODE_ONLY`，不得作为正式 benchmark |

**统计纪律**：至少固定环境/模型/Skill/工具面后跑多个独立 run，事先声明样本数与失败口径，
才可报告通过率与不确定性区间。1/1 只能写成“一次可行性证据”。

## 6. 执行流程（必须在用户批准后）

### 6.1 前置（D0）
1. 用户书面批准：专用新槽、新局、真实写操作、启动/关闭范围、最大运行时间、是否允许重启。
2. 核验：游戏版本 0.46D / MVID、Mod SHA 与安装 SHA、`/health` policy SHA 与 MCP 一致、
   HEAD/dirty diff、工具面、模型 ID、Skill SHA。
3. 备份现有 DLL；记录备份路径与字节 SHA。**不改存档、不清理旧槽、不覆盖槽 53。**
4. `dev.txt` 不因自动化测试默认重建；如官方 scanner 需要，单独授权且事后删除。

### 6.2 开局（L3）
- 走原生主菜单：先确认 first-run prompt（`run_change:"not_issued"`，不是开局），
  再真实 `confirm_new_game`（`run_change:"issued"`，run identity 确认后 old evidence 驱逐一次）。
- 记录专用槽原生索引与绑定；`recording_slot` 必须等于游戏下一个自动分配槽。

### 6.3 主循环
- 每回合：`get_state`（或快照）→ 有限动作集 → 1 个写动作 → 回读 → 记录。
- 日终：优先 typed 路径（`get_pending_prompt`/`answer_prompt` 的
  `negotiation_confirm`；`close_windows` typed overlay dismissal），**正式 Play 不用 `press`**；
  `end_day` 收敛看 `shop_closed_after`；`sleep` 只按真实 day 变化判完成，过窗无证据 = uncertain。
- 交易：`stage_negotiation_item`（B1）确定当前议价商品；`confirm_trade` 只按单笔证据判成；
  预算预检拒绝优于原生静默失败。
- 每步写入 C2 记录器（append-only JSONL，含 intent/result/recovery/stop/intervention）。

### 6.4 收尾
1. 停止游戏进程（用户授权范围内）；不改存档。
2. 导出轨迹 + `trace_validator`（0 ERROR，所有 WARN 逐条披露）。
3. 报告：run 级 token/成本/时长/动作数/失败/拒绝/恢复/人工介入；不可测写 `NOT_MEASURED`。
4. 证据归档：原始 JSONL、导出轨迹、`/health`、工具清单、版本/MVID/SHA、最终状态与回执。

## 7. 停止条件（立即停自动写、保留轨迹、要求人工检查）

- 无法核对版本/MVID/policy SHA/录制槽绑定/目标 run identity；
- `dispatched=unknown` 或 `execution_status=uncertain` 且无法可靠收敛；
- sleep 可能已执行但 Day 不可读（重复推进风险）；
- 交易现金/物品与证据不一致，或重复交易风险；
- 弹窗文本不可读或无法确认原生控件身份；
- 工具权限越界、非专用存档可能受影响、意外 dev/test 功能被开放；
- 达到预算（动作/成本/时长/连续失败）；
- 无法保存可信轨迹或记录器失败。

## 8. 与 benchmark/spec 的关系

- 本协议描述 pilot（可行性）执行法；正式 benchmark 的判定见
  [`FIRST_WEEK_BENCHMARK_SPEC.md`](FIRST_WEEK_BENCHMARK_SPEC.md)（`spec_version 2026-11-19-r5`）。
- 正式 benchmark 只接受 `trace_validator` schema v2 轨迹；旧轨迹按 v1 规则校验并告警，
  不得静默按新口径判分。
- 当前状态（本轮）：`L1-fake` 场景与记录器离线可测；`L3 TEST_MODE_PILOT` / `L3 PLAY_BENCHMARK`
  均 **NOT_RUN**；Play 终态 **BLOCKED_BY_PLAY_POLICY**。
