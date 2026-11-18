# R5_C_OFFLINE_REPORT — R5-C 离线执行与 fake 证据报告

> 采集：2026-11-18T21:12Z，Linux 主机（无 Windows、无游戏进程、无存档、无网络对外）。
> 本报告只写**实际运行**的结果。证据等级：**L1-fake**（假桥/纯逻辑，无 Unity）与
> **离线工具**（validator/recorder/executor）；**L3 真机一律 `NOT_RUN`**。
> **本报告不包含任何 LLM 通关率/成本/效率数字**；本轮没有运行真实 LLM pilot，
> 正式 Play benchmark 终态 **`BLOCKED_BY_PLAY_POLICY`**（`play_allowlist` 为空）。
> 关联：[`R5_LLM_PILOT_PROTOCOL.md`](../eval/R5_LLM_PILOT_PROTOCOL.md)、
> [`R5_C_AGENT_REVIEW.md`](../reviews/R5_C_AGENT_REVIEW.md)（独立 reviewer）、
> [`OVERNIGHT_LOG.md`](../audit/OVERNIGHT_LOG.md)。

## 0. 结论摘要

| 项 | 交付 | 证据等级 | 状态 |
|---|---|---|---|
| C1 不把 `_internal/week.py` 包装成 AI | 未包装；决策/执行/恢复/评测四层分离；`orchestrator.py` 的 `RulePolicy` 保持“基线 plumbing”定位未改 | 代码/文档核对 | **PASS（离线）** |
| C2 可信 JSONL 记录器 + MCP 集成 | `agent/trace_recorder.py`（append-only JSONL、fsync、恢复判定、导出 validator 轨迹）+ `--trace-recording` 集成 | L1 + 离线 | **PASS（L1）** |
| C3 LLM 最小决策接口 + 执行层 | `agent/decision_interface.py`（Decision 校验、rev/action_id 归执行层、恢复与预算、Play 门禁）+ 协议文档 | L1 | **PASS（L1）** |
| C4 fake 六情景 | `agent/fake_game.py` R5 契约扩展 + `agent/tests/test_fake_scenarios.py` 6 情景 11 测试 | L1-fake | **PASS（L1-fake）** |
| 真实 LLM 决策 / 真机 pilot | 未运行（无 LLM 宿主连接、无 Windows/游戏/授权） | — | **NOT_RUN** |
| 正式 Play benchmark | `play_allowlist=[]`，逐工具 live evidence 缺失 | — | **BLOCKED_BY_PLAY_POLICY** |

## 1. 本轮实际运行的门禁（C 收口）

| 套件 | 命令 | 退出码 | 结果 |
|---|---|---|---|
| Python 全量 | `.venv/bin/python -m unittest discover -s agent/tests` | **0** | **Ran 505 tests，OK**（R5 启动基线 377；+128；收口时 498，Reviewer-C WARN 修复新增 7 个断言/用例） |
| C# 全量 | `DOTNET=… REQUIRE_GAME=1 ./test.sh`（flock） | **0** | **passed=864 failed=0** |
| 脚本门禁 | 同上内嵌 | **0** | **passed=53 failed=0** |
| 适配器契约（L2） | 同上（`Assembly-CSharp.dll` sha256 `725cb999…`） | **0** | **contract checks=526 / adapter contract OK** |
| 策略目录一致 | `.venv/bin/python tools/gen_policy_catalog.py --check` | **0** | matches `774fbfdd7b44ef5c6c7b616f885f1b624758b862b3e750497f8a39cc26763277` |
| 适配器构建（真实 Unity 侧） | `dotnet build src/PawnMind.csproj -c Release -p:GameManaged=…` | **0** | Build succeeded |

聚焦测试（均本机实跑）：

| 模块 | 命令 | 结果 |
|---|---|---|
| 记录器 | `python -m unittest agent.tests.test_trace_recorder` | **44 OK**（含 W-C2-1 dispatch 轴） |
| MCP 集成 | `… test_mcp_entrypoint`（含 5 个新集成测试） | **8 OK** |
| 决策接口 | `… test_decision_interface` | **23 OK**（含 W-C3-1 空面 deny-all、W-C3-2 二次参数校验） |
| fake 六情景 | `… test_fake_scenarios` | **11 OK** |
| fake 契约（R5 面） | `… test_fake_game_contract` | **23 OK** |
| validator（1.3.0） | `… test_trace_validator` | **117 OK** |

## 2. C2 — 可信调用层记录器（append-only JSONL）

**实现**：`agent/trace_recorder.py`（stdlib only，离线）。包装 duck-typed bridge client
（`action/get_state/get_operation/wait_operation/wait_events`），每次调用写 intent（派发前）
与 result（派发后）事件，另记 recovery/stop/intervention；每条含 handoff §4 C2 字段
（run_id/sequence/wall_time/monotonic_elapsed_ms/actor/model_id/prompt_sha256/skill_sha256/
host_version/operation_id 或 action_id/op/args/observed_rev/policy_sha256/tool_surface/
recording_slot/request_status/response_status/execution_status/dispatch_status/delivery_status/
evidence/state_before_ref/state_after_ref/error/human_intervention/duration_ms）。

**已由测试钉住的关键性质**（`agent/tests/test_trace_recorder.py` 44 项；收口时 41，Reviewer-C WARN 修复后 +3 dispatch 轴用例）：
1. 一行一事件、append-only；每事件 `flush + os.fsync`（测试 patch fsync 断言次数）。
2. token/auth/secret 不落盘：键名脱敏 + 已知 token 值全串替换 + 写前复查；
   测试断言 token 子串在文件中不存在。
3. 恢复判定表：intent 无 result / dispatch unknown / response lost / running / uncertain
   → `needs_operation_query`；只允许真实 `get_operation(id)`/`wait_operation` 或**原 action_id**
   幂等重放解析；无 id 或不可查 → `unknown` + stop，写操作本地拒绝（`recovery_pending`/`recovery_unknown`），
   **从不自动重放**。`get_operation("*")` 只记录，不当作解析证据。
4. 崩溃注入：crash-after-intent / crash-after-dispatch / response-lost /
   duplicate action_id / corrupted-line / session restart；损坏行容错且结构化告警。
5. 导出：`export_trace(path)` → `{"schema_version": 2, "steps": [...]}`，steps 由记录的
   request/result 字节 1:1 重建；模型文字/claims 不进入导出；校验记录的 sha256；不完整记录 fail-closed。
6. MCP 集成（`agent/mcp_server.py --trace-recording PATH`）：包装在 memory recorder 之后，
   `finally` 关闭；**打不开录制文件即启动失败（exit 5）**，绝不静默无记录运行；不带参数行为不变。

**Lead 亲自跑通的端到端 round-trip**（本机，L1-fake）：

```python
FakeGame(recording_mode="armed", recording_slot=17) → BridgeClient → TraceRecorder
  get_state / open_new_game_menu / select×2 / new_game_continue×2 / confirm_new_game / get_state / open_shop
export_trace → schema_version=2，10 steps
trace_validator.validate_trace → violations=[]（0 error / 0 warning）
```

## 3. C3 — 最小决策接口 + 可信执行层

`agent/decision_interface.py`（23 项测试；收口时 19，Reviewer-C WARN 修复后 +4 surface/参数复核用例）把 handoff §4 C3 的四条约束落为代码：

| 约束 | 实现 | 测试 |
|---|---|---|
| 模型只能 propose | `Decision.from_dict` 只接受 `goal/observation_refs/proposed_action/reason/expected_observation/stop_if` | 形状/空 reason/坏 action 拒绝 |
| 模型不得设置 `rev`/`action_id` | Decision 参数中出现即 `DecisionError`；执行层每步自生成 | `test_rejects_model_supplied_action_id_or_rev` |
| 不越权/不探隐藏字段 | 未知工具拒绝；`include_hidden_appraisal` 等参数键拒绝 | 2 项 |
| 执行层单次派发 + 回读 | `Executor.execute` 恰好一次 action，随后 `get_state` 回读 | `test_every_step_reports_the_new_observation` |
| `uncertain` 只查询不重试 | 记 `get_operation(op_id)` 一次后安全停止（stop=True） | `test_uncertain_queries_operations_and_never_re_dispatches` |
| `stale_rev` 同 id 重放一次 | 刷新 rev 后用**同一 action_id** 重放；仍 stale → 停止 | 2 项 |
| 预算安全停止 | max_actions / 连续 rejected / 连续 uncertain / 墙钟；超限停止并保留轨迹 | 5 项 |
| Play 门禁 | `play_allowlist` 为空 → `blocked_by_play_policy`，0 次调用 | `test_play_surface_is_closed_without_live_evidence` |

协议文档：`docs/eval/R5_LLM_PILOT_PROTOCOL.md`（执行/判定/预算/停止条件；成功判定只来自
调用层 JSONL + 真机回执 + validator；无任何编造的通过率/成本数字；L3 标 NOT_RUN；
Play 标 BLOCKED_BY_PLAY_POLICY）。`docs/audit/R5_NEXT_LIVE_SESSION.md` 给出 Windows 授权后的
逐步操作/证据/停止/回滚手册。

## 4. C4 — fake 六情景（deterministic L1-fake）

`agent/fake_game.py` 扩展为 R5 契约面（+1237 行），新增 host-only 夹具保证确定性：
`stock_item / arrive_customer / raise_negotiation_confirm / finish_sleep / advance_clock /
inject_native_fault`（无 wall-clock sleep）。`agent/tests/test_fake_scenarios.py` 11 个测试：

| # | 情景 | 结果 | 关键断言（实测） |
|---|---|---|---|
| 1 | 正常多笔交易 | **PASS** | 顾客 1 依次 stage A→B，两笔 `confirm_trade` completed（`items_transferred`/`cash_moved`/`client_unchanged`）；现金 25→47→157→192，库存 3→0 |
| 2 | 买不起/静默拒绝 | **PASS** | 预算 10 < 价 22 → `client_budget_insufficient`、`dispatched:false`、0 次 native click、无移动；注入静默 native accept → `uncertain/outcome_unverified`（无伪造证据），同 item+side 重试 → `operation_pending` |
| 3 | 不同商品 staging | **PASS** | `staged_item_id_before/after`：0→A、A→B、B→A；`negotiation_open`/`client_identity_unchanged` true；`no_client`/`item_not_found` fail-closed 且 0 click；uncertain 同 item 新 id → `operation_pending`，不同 item → completed |
| 4 | 日终模态 | **PASS** | `get_pending_prompt` → `kind:"negotiation_confirm"`、`target:"negotiation_tablet_confirm"`、body/labels/enabled 可读（`/state.prompt` 同对象）；`answer_prompt(confirm)` → `prompt_closed:true` + `state.shop_state:"CLOSED"`、1 次 native click；关闭时回答 → `no_prompt`、0 click |
| 5 | 异步 sleep | **PASS** | `sleep` → pending/running；立即 `get_operation` inconclusive（全程无 `failed`/`day_not_changed`）；宿主 `finish_sleep()` 后 → `completed` + completion_evidence；**模拟 10s 窗**：5s running、11s → `sleep_unconfirmed` uncertain 非终态、新 sleep id → `operation_pending`、之后 day 前进恢复 completed；`get_operation("*")` 20 条/总数 21/`truncated:true`/`truncation_note` |
| 6 | 首周租金 | **PASS** | `end_day+sleep`×7 到房东；`choose_dialogue(0)` → 回执 `rent_1_100`：amount 100、cash 200→100（`cash_delta:-100`）、`weeks_paid_delta:1`、`paid_at_rev` = 派发 rev、`detection_complete:true`；后续 +30 交易不改变回执字节，现金 130 |

fake 与桥一致性：R5 路径（staging / typed modal / R5 交易证据 / 异步 sleep 模型 / 租金回执）的
字段名/拒绝码/uncertain 语义逐项照已提交 C# 契约实现；注入静默 native 时返回 uncertain 而非 ok。
**范围说明（Reviewer C W-C4-1）**：fake 的旧版默认 sleep 路径（`async_sleep=False`，预 R5 S6B 遗留）
仍返回即时 `ok:true/completed`；R5 异步模型是显式开启（情景 5 使用），因此上面的“不会输出桥不会
输出的成功形状”只对 R5 契约面成立，旧默认 sleep 属于历史 fake 行为，不是本轮 R5 证据。TDD red 证据：动手前 9 个测试
失败（缺 `async_sleep`/host 夹具、`get_pending_prompt` 未注册、旧 `rent-0001` 回执、
静默 native 交易伪造 `ok:true`），确认“fake 缺行为”后才扩展。

## 5. 明确未做 / 不夸大

| 项 | 状态 | 说明 |
|---|---|---|
| 真实 LLM 决策循环（接宿主/模型） | **NOT_RUN** | 无 LLM 宿主连接、无游戏；C3 只提供接口与执行层 |
| L3 TEST_MODE_PILOT / PLAY_BENCHMARK | **NOT_RUN / BLOCKED_BY_PLAY_POLICY** | `play_allowlist=[]`；无 Windows/授权 |
| 模型策略质量（通关率、成本、Token） | **NOT_MEASURED** | 不测量、不编造 |
| fake 六情景的统计意义 | 不存在 | 只是 deterministic regression，不是策略评估 |
| 真机 staging/模态/sleep/预算行为 | **NOT_RUN** | 见 R5_B_GAMEPLAY_REVIEW / R5_NEXT_LIVE_SESSION |
| 历史首周运行轨迹复核 | **NOT_RUN** | 原始轨迹缺失（E2） |
| 记录器日志的对抗性防篡改 | 仅 tamper-evidence | sha256 校验无密钥；需要 append-only 存储 + 异地副本才谈对抗性 |

## 6. 残留风险（C 段）

1. 记录器 fsync 每事件（~ms 级）：长轮询 `wait_events` 会放大文件体积，这是审计设计取舍。
2. 严格恢复门（`strict_recovery=True`）在任一未决操作时拒绝所有新写，比 validator 的
   校准关系更保守；需要时可显式关闭，但默认 fail-closed。
3. C3 执行层对 `uncertain` 的处理是“查询一次 + 停止等人工”，未实现自动多轮收敛——与 handoff
   “不得盲重试”一致，但意味着真实 run 可能需要人工介入计数。
4. C3 目前只接 duck-typed client；接 DSH/OpenCode MCP 宿主的真实适配属于 L3 工作（NOT_RUN）。
5. fake 的 staging/business_complete/租金均为 canned 数据；只验证“桥语义形状”，不验证真机物理/时序。
6. 预算中的 token/成本只留接口（`note_tokens`），真实模型 token 计量需宿主提供（NOT_RUN）。
