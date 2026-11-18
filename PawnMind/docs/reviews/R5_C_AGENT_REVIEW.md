# R5_C_AGENT_REVIEW — R5-C（C1–C4）独立审计

> 审计者：`reviewer-c`（fresh context，非实现者，未参与 R5-C 任何代码/测试编写；本次未修改生产代码/测试，未 `git add`/`commit`）。
> 审计时间：2026-11-18T21:10Z – 21:38Z（Linux 主机；无 Windows、无游戏进程、无存档操作、无对网络/Workshop/扫描器/dev 的调用）。
> 分支：`r5/overnight`。审计目标快照 **`243d1f91`**（R5-C4 head）：
>
> | 提交 | 内容 | 审计位置 |
> |---|---|---|
> | `2330180d` | C2 `agent/trace_recorder.py` + `test_trace_recorder.py` | 代码/测试 |
> | `6487048e` | C2 `--trace-recording` MCP 集成 + `test_mcp_entrypoint.py` | 代码/测试 |
> | `3d47f4b7` | C3 `agent/decision_interface.py` + `test_decision_interface.py` + 两份协议/真机手册 | 代码/测试/文档 |
> | `243d1f91` | C4 `agent/fake_game.py` R5 面 + `test_fake_scenarios.py`（6 情景） | 代码/测试 |
>
> 说明：
> 1. 全部测试与复现都在**独立 `git worktree`** 上跑（`git worktree add /root/work/game_mods/_internal/review-c-worktree 243d1f91`），主工作树在审计期间被其他 teammate 持续编辑（B 复审修复、C 离线报告等），不用于证据。
> 2. 审计期间仓库 HEAD 从 `243d1f91` 前进到 `f18e25b8`（`bfa3fbbe` C 离线报告、B 复审修复、文档收口）。实测 `243d1f91..f18e25b8` 对 C 实现文件（`trace_recorder.py`/`decision_interface.py`/`fake_game.py`/C 测试/validator/policy）**零改动**；`mcp_server.py` 只改了 3 处工具 docstring（`f18e25b8`），未触碰 C2 记录器集成；`src/Bridge/PromptService.cs` 的 +11 行属 B 复审修复。故本审计结论对 C 段成立，后文引用的行号均指 `243d1f91` 快照。
> 3. 证据分级：**VERIFIED** = 本次实际运行或实际读源码/文档并给出位置；**REPORTED** = 开发者在提交信息/`OVERNIGHT_LOG` 中的声明，本次未独立重放；**NOT_RUN** = 环境/授权不允许（Windows L3、真机、存档、Workshop、扫描器）。
> 4. 本文件是本次审计**唯一写入**的文件；未修改代码/测试/其他文档，未提交 git。

---

## 0. 结论摘要（Verdict）

| 项 | 结论 | 一句话依据 |
|---|---|---|
| **C1** 不把 `_internal/week.py` 包装成 AI / 四层分离 | **PASS**（离线） | 仓库无 `week.py`/无脚本策略包装；`orchestrator.py` 仍只有确定性 `RulePolicy`；决策（`Decision` 校验）→ 执行（`Executor` 持 rev/action_id、Play 门禁）→ 恢复（uncertain 只查、stale 同 id、`TraceRecorder` 崩溃恢复写拒绝）→ 评测（JSONL→`export_trace`→`trace_validator`）职责分离；模型经 `Decision.from_dict` 无法设置 `rev`/`action_id`/未知工具/隐藏字段参数（独立探针实测拒绝）。 |
| **C2** 可信 JSONL 记录器 + 导出 + MCP 集成 | **PASS**（L1 + 离线；2 个 WARN） | append-only 一行一事件、每事件 `flush+fsync`（源码 + patch fsync 计数测试）、token 不入盘（测试 + 审计者真实录制实测）；启动扫描覆盖 crash-after-intent/after-dispatch/response-lost/running/uncertain/孤儿 intent/损坏行/会话重启，**从不自动重放**，只接受真实 `get_operation(id)`/`wait_operation` 或**原 action_id** 幂等重放，`get_operation("*")` 只记录不解析；未决/UNKNOWN 期间写操作本地拒绝；导出由记录字节 1:1 重建（审计者实测 14/14 步字节一致）、sha256 被篡改即 fail-closed、不完整记录 fail-closed、模型文字/claims 不入导出；`--trace-recording` 打不开即 exit 5，缺席行为不变。WARN：W-C2-1 result 事件 `dispatch_status` 对 pre-dispatch 拒绝也写 `dispatched`；W-C2-2 协议 §2 要求的 `prompt_sha256/skill_sha256/真实 run_id` 未被 MCP 集成填充。 |
| **C3** LLM 最小决策接口 + 可信执行层 | **PASS**（L1；3 个 WARN） | `Decision.from_dict` 拒绝未知工具/`rev`/`action_id`/隐藏字段参数/嵌套值；执行层自生成 rev+action_id、norm 路径恰一次派发、`uncertain` 查询一次即 stop、`stale_rev` 用同一 id 重放一次后 stop、四类预算安全停止；Play 模式在 `play_allowlist=[]` 下 `blocked_by_play_policy` 且 0 调用（独立探针）。WARN：空 `allowed_tools` 被当作“不限制”（W-C3-1）；`execute()` 对直接构造的 `Decision` 不再做 schema 校验（W-C3-2）；协议文档仍写 validator v1.2.0（实际 1.3.0，W-C3-3）。 |
| **C4** 六个 deterministic L1-fake 情景 | **PASS**（L1-fake；1 个 WARN） | 六个必需情景在 `test_fake_scenarios.py` 中各有确定性回归（11 个用例，连跑两次同结果；无 wall-clock sleep；host-only 夹具不暴露为 HTTP op）；字段/拒绝码/证据语义与已提交 C# 契约逐项抽查一致（staging 15 拒绝码/detail、trade completed/refusal/uncertain+evidence、PromptJson 模态对象、RentReceipt 字段与 id、`PendingScope` 两个 call-scoped op、10s sleep 观察窗）；注入静默 native 时返回 `uncertain` 而非 `ok:true`。WARN：默认（非 `async_sleep`）fake sleep 仍返回旧同步 `ok:true`，与 R5 桥（running/pending）不一致，`R5_C_OFFLINE_REPORT.md` 的“绝不出桥不会输出的成功”绝对表述对默认模式不成立（W-C4-1）。 |
| 真实 LLM 决策循环 / 真机 pilot | **NOT_RUN** | 无 LLM 宿主连接、无 Windows/游戏/授权；C3 只提供接口与执行库。 |
| L3（真机）全部行为 | **NOT_RUN** | 无 Windows、无游戏进程、未安装 DLL、未读写存档。 |
| 正式 Play benchmark | **BLOCKED_BY_PLAY_POLICY** | `play_allowlist=[]`（审计者实算），逐工具 live evidence 缺失；不是 PASS，也不是 FAIL。 |

**C 阶段离线退出门槛判读：** 既有 C#/Python/contract/policy 全量门禁本次实际全绿；C1–C4 的声明与代码/测试/文档逐项可复核；未发现需要重开的 FAIL。上述 WARN 为下次协议/L3 前应修正的精度问题（详见 §5），不影响“离线交付可放行”。

### 0.1 本次实际运行的门禁与探针（VERIFIED）

C#（干净 worktree，脚本在 git index 已可执行，`./test.sh` 直接运行——A 段 W-ENV-1 的 `chmod +x` 修复生效，53 项脚本门禁确实执行而非被 `-x` 判定静默跳过）：

```bash
cd /root/work/game_mods/_internal/review-c-worktree/PawnMind
export DOTNET=/root/.dotnet/dotnet \
       PAWNMIND_NEWTONSOFT_DLL=/root/work/game_managed/Newtonsoft.Json.dll \
       GAME_ASSEMBLY=/root/work/game_managed/Assembly-CSharp.dll \
       REQUIRE_GAME=1
flock /root/work/game_mods/_internal/pawnmind-test.lock ./test.sh
# exit 0
# passed=864 failed=0
# contract gate tests: passed=53 failed=0
# contract checks: 526 / adapter contract OK
```

原始日志：`_internal/logs/review_c_cs.log`（1474 行，含 `CS_EXIT=0`）。C 段提交不含 C# 文件（`--numstat` 只有 `agent/` 与 `docs/`），C# 与 B 头一致；仍按门禁要求完整跑了一遍。

Python（主仓库 venv 解释器 + worktree 的 `PYTHONPATH`/cwd，保证导入 worktree 代码）：

```bash
cd /root/work/game_mods/_internal/review-c-worktree/PawnMind
PYTHONPATH=$PWD /root/work/game_mods/PawnMind/.venv/bin/python \
  -m unittest discover -s agent/tests
# exit 0；Ran 498 tests in 50.580s，OK（无 skipped 标记 => 0 skip）
```

原始日志：`_internal/logs/review_c_py.log`（`Ran 498 tests ... OK`，`PY_EXIT=0`）。

聚焦模块（同一 worktree，均 exit 0）：

| 模块 | 结果 | 与 C 段关系 |
|---|---|---|
| `test_trace_recorder` | **Ran 41 — OK** | C2 新测试 |
| `test_mcp_entrypoint` | **Ran 8 — OK** | C2 集成（含 flag/无 flag/不可写路径/记录器包在 memory 外层） |
| `test_decision_interface` | **Ran 19 — OK** | C3 新测试 |
| `test_fake_scenarios` | **Ran 11 — OK**（连续两次同结果） | C4 六情景 |
| `test_fake_game_contract` | **Ran 23 — OK** | C4 fake 契约面 |
| `test_trace_validator` | **Ran 117 — OK** | 导出轨迹的验收器（v1.3.0/schema 2） |
| `test_mcp_tools_mode` | **Ran 20 — OK** | 实际注册面 full=117 / recording=107 断言 |

策略/工具面（worktree 实算）：

| 命令/检查 | 结果 |
|---|---|
| `python tools/gen_policy_catalog.py --check` | **exit 0**；`PolicyCatalog.g.cs matches tool_policy.json (sha256 774fbfdd7b44ef5c6c7b616f885f1b624758b862b3e750497f8a39cc26763277)` |
| `tool_policy.load_policy()['tools']` | **117** |
| `mcp_server.RECORDING_TOOL_NAMES` | **107**，且为 policy 工具集子集 |
| `tool_policy.play_allowlist(policy)` | **[]**（空） |
| `tools-inventory.json` | `full=117 / recording=107` |

审计者自行跑通的记录器端到端（L1-fake，**不是** Lead/开发者的数字）：`FakeGame(recording_mode="armed", recording_slot=17)` → `BridgeClient` → `TraceRecorder`（手写 14 次调用：菜单开局、`stage_negotiation_item`、`confirm_trade`、多次 `get_state`、一次手动介入）→ `export_trace` → `agent/trace_validator.py <trace>`：

| 断言 | 实测 |
|---|---|
| JSONL 事件数 / 读取告警 | **29 事件 / 0 warning**（14 intent + 14 result + 1 intervention） |
| 桥 Bearer token 是否出现在录制文件 | **否**（`TOKEN_LEAKED=False`） |
| 导出 | `schema_version=2`，**14 steps**，含 `stage_negotiation_item`/`confirm_trade` |
| 导出步骤 vs 原始 intent/result 字节 | **1:1 全等（BYTE_1TO1=True）** |
| `python agent/trace_validator.py <trace>` | **exit 0**，`ok=True`，**0 error / 0 warning** |
| 不完整记录（intent 无 result）导出 | **fail-closed**（`TraceExportError: ... no complete response`） |
| 篡改一条已记录 result 后导出 | **fail-closed**（`recorded result bytes ... were modified`，sha256 校验） |
| `trace_recorder.py export` CLI | **exit 0**，`steps=14` |
| `mcp_server --trace-recording <不可写路径>` | **exit 5**，stderr 含 `refusing to run`，无残留文件 |

独立 C3 探针（审计者自写脚本，不经测试断言）：

| 探针 | 实测 |
|---|---|
| `Decision.from_dict` 带 `rev` / `action_id` / `include_hidden_appraisal` / `add_credit` | 全部 `DecisionError`（拒绝） |
| 未知工具 `week_wrapper` | 拒绝 |
| Play 模式（`play_allowlist=[]`）+ 合法工具 | `status=blocked`，`hint=blocked_by_play_policy`，**0 次 client 调用** |
| 空 `allowed_tools`（test 模式） | `status=completed`、**派发 1 次** → 见 W-C3-1 |
| `uncertain` 响应 | 恰 1 次派发 + 1 次 `get_operation` 查询 + `stop=True` |
| 连续 `stale_rev` | 刷新后同 `action_id` 重放共 2 次派发，第二次仍 stale → `stopped` |
| 连续 rejection 预算（阈值 2） | 第 3 次 `status=stopped`，`max_consecutive_rejections: 3 in a row` |

独立 C2 探针：

| 探针 | 实测 |
|---|---|
| `stale_rev` 拒绝（pre-dispatch）经记录器后的 result 事件 | `payload.ok=False, payload.dispatched=None`，但记录 `dispatch_status="dispatched"` → 见 W-C2-1 |
| 在 result 行旁注入模型文字 `claims/claim_text` 再导出 | 导出 JSON 中**不出现** claims/模型文字（`ANNOTATION_LEAKED=False`） |
| 把伪造 `claims` 塞进导出 step 再跑 validator | 被拒：`MODEL_SELF_REPORT_IS_NOT_EVIDENCE` + `REJECTED_CLAIMED_AS_SUCCESS` |
| 未决 uncertain 后：新 action id 写 / 读操作 | 写 **`recovery_pending` 拒绝**；`get_state` 仍可用 |

### 0.2 环境（VERIFIED）

| 项 | 值 |
|---|---|
| 主机 | Linux（无 Windows 子系统/无游戏进程） |
| dotnet | `/root/.dotnet/dotnet` `9.0.321` |
| Python | `3.12.3`（`PawnMind/.venv`） |
| 游戏程序集 | `/root/work/game_managed/Assembly-CSharp.dll` sha256 `725cb999d019dff1…`（与 R5_BASELINE 一致） |
| Newtonsoft | `/root/work/game_managed/Newtonsoft.Json.dll` sha256 `a56146202232958f…` |
| 审计 worktree | `_internal/review-c-worktree`，detached at `243d1f91`，`git status --porcelain` 为空 |

---

## 1. C1 — 不把 `_internal/week.py` 包装成 AI，四层分离

### 1.1 读了什么

- `git log --oneline`（含 C 段四个提交）与 `git show --numstat`：C 段改动只涉及 `agent/trace_recorder.py`、`agent/decision_interface.py`、`agent/fake_game.py`、对应测试、`mcp_server.py`（+64 行）与两份文档；**没有** `week.py`/orchestrator/策略脚本改动。
- `agent/orchestrator.py`：仍只有 `ToolCall` + `RulePolicy`（`:28`、`:40`），无 LLM/网络调用。
- `agent/decision_interface.py` 全文：决策/执行/恢复/预算职责边界（模块 docstring `1–19`、`Decision`、`Executor`）。
- `agent/trace_recorder.py` 全文：记录/恢复/导出职责。
- `agent/mcp_server.py` 的 `--trace-recording` 集成段（`1396–1429`）。
- 文档：`docs/eval/R5_LLM_PILOT_PROTOCOL.md` 全文、`docs/audit/R5_NEXT_LIVE_SESSION.md` 全文、`docs/pawnmind-known-limitations.zh-CN.md` §11、`docs/reports/R5_C_OFFLINE_REPORT.md`（`bfa3fbbe`，审计期间合入，见 §1.3）、`skills/pawnmind-first-week/SKILL.md` 顶部状态。
- 全库检索 `week.py` / `LLM` / `R5_C_OFFLINE_REPORT`。

### 1.2 逐条验证

| Handoff C1 要求 | 结论 | 证据 |
|---|---|---|
| 不把 `_internal/week.py` 包装成 AI | **VERIFIED** | `git ls-files` 无 `week.py`；`week.py` 只出现在历史证据/交接文档（`R5_BASELINE.md:115`、`SESSION_2026-11-18_FIRST_WEEK.md:158`、HANDOFF §0/§2），全部是“本机缺失/不得包装”的记录；C 段无任何 wrapper/新入口。 |
| 不内置“见到商品就卖/每天固定次数”的完整脚本策略并冒充 LLM | **VERIFIED** | `decision_interface.py` 无工具序列/策略常量（`ALLOWED` 等只在测试里）；`Executor` 每步只消费调用方给的一个 `Decision`，自身不做选择（docstring 明说 “It never decides what to do”）；`fake_game.py` 的六情景是 deterministic regression，不是策略评估。 |
| 决策层 = 提案 | **VERIFIED** | `Decision.from_dict`（`decision_interface.py:147–172`）只接受 handoff 文档形状；未知工具/空 reason/坏 action 拒绝。 |
| 执行层 = rev/action_id/policy gate/单次派发/回读 | **VERIFIED** | `Executor.execute`（`:337–434`）：自生成 `uuid` action_id（`:368`）、rev 来自最近一次 `get_state`（`:316–322`、`:362–366`）、Play 门禁先于派发（`:325–355`）、完成后 `get_state` 回读（`:390–395`）；独立探针确认模型无法自带 rev/action_id。 |
| 恢复层 = stale_rev / rejected / running / uncertain，uncertain 只查询 | **VERIFIED** | stale/uncertain 在 `Executor`（`:375–388`、`:406–419`），崩溃/重启恢复在 `TraceRecorder`（扫描、写拒绝、仅真查询/原 id 解析）；`uncertain` 路径实测 1 次派发 + 1 次 `get_operation` + stop。 |
| 评测层 = 只从调用层日志/真实回执判定 | **VERIFIED** | `export_trace` 只从记录的 intent/result 字节重建 steps（`trace_recorder.py:713–797`），模型文字/claims 不入导出（测试 + 独立探针）；`trace_validator` 才是判定器，且对“模型自述成功”报 `MODEL_SELF_REPORT_IS_NOT_EVIDENCE`/`REJECTED_CLAIMED_AS_SUCCESS`。 |
| 文档不把脚本运行/旧 `week.py` 运行写成 LLM 结果 | **VERIFIED** | `R5_LLM_PILOT_PROTOCOL.md` 首段即“**不是评测结果**…不包含任何 LLM 通关率/效率数字”；`R5_NEXT_LIVE_SESSION.md` 首段 `OFFLINE_PLAN`/“没有执行任何一条真机步骤”；`R5_C_OFFLINE_REPORT.md` 明写“没有运行真实 LLM pilot”；`known-limitations` §11.5 写“没有 LLM 真机 pilot”。 |
| 协议文档无编造通过率/成本数字，L3 标 NOT_RUN / Play 标 BLOCKED_BY_PLAY_POLICY | **VERIFIED** | `R5_LLM_PILOT_PROTOCOL.md:4–5、19–20、88–96、144–145`：无任何跑分/成本数字（预算上限是声明值，非测量值），L3 全 `NOT_RUN`，Play 终态 `BLOCKED_BY_PLAY_POLICY`；`R5_NEXT_LIVE_SESSION.md:205–213` 同口径。审计者独立实算 `play_allowlist=[]`。 |

### 1.3 说明与残留

- **R-C1-1（低，架构简化）**：handoff 要求“决策/执行/恢复/评测分离”。代码中没有独立的 Recovery 类：活动会话的 stale/uncertain/预算恢复在 `Executor` 内，崩溃/重启恢复在 `TraceRecorder` 内，二者职责清晰但同属执行侧；评测侧（export+validator）独立。属可接受的实现分层，但若后续把恢复逻辑抽成独立 driver，需保持“uncertain 只查、不重放”语义。
- **R-C1-2（范围）**：C3 只提供接口/库；真实 LLM 宿主（DSH/OpenCode）适配与真实决策循环未接入，`NOT_RUN`（`R5_C_OFFLINE_REPORT.md` §5 也如实列出）。
- 审计期间新增的 `docs/reports/R5_C_OFFLINE_REPORT.md`（`bfa3fbbe`）已读：对 L1/L3/Play 的分级与禁用项表述诚实，无 LLM 通过率/成本数字；唯一绝对化表述“fake 不会输出桥不会输出的成功形状”与默认模式 fake sleep 的旧同步行为不符（见 W-C4-1），已作为 WARN 记录，不影响 C1 判据。

---

## 2. C2 — 可信 append-only JSONL 记录器 + 导出 + MCP 集成

### 2.1 读了什么

- `agent/trace_recorder.py` 全文（1504 行）：`_EVENT_CORE_FIELDS`（`192–202`）、恢复判定表与文档（`50–79`）、tolerant reader `load_events`（`318–368`）、启动扫描 `analyze_events`（`559–630`）、解析规则 `_resolution_from_record`（`457–488`）、导出 `export_trace`（`713–797`）、校验和 `_verify_recorded_checksum`（`653–666`）、`TraceRecorder` 全部协议方法与写拒绝（`858–1504`）。
- `agent/tests/test_trace_recorder.py` 全文（1161 行，41 用例）：append 顺序/字段/时钟/字节稳定/append-only/fsync/秘密/注入 id/六类崩溃恢复/损坏行/intervention/stop/export 正反例/CLI。
- `agent/mcp_server.py`：`--trace-recording` argparse（`1329–1339`）、集成（`1396–1429`，含 `_recording_slot_from_env` `1271–1285`）、`finally` 关闭（`1444–1449`）。
- `agent/tests/test_mcp_entrypoint.py` 全文（8 用例，含 5 个 flag/无 flag/不可写/包裹层顺序集成）。
- 对照历史：A 段 `R5_A_PROTOCOL_REVIEW.md` 对 trace_validator/桥协议的口径。

### 2.2 逐条验证（handoff §4 C2 + 任务清单）

| 要求 | 结论 | 证据 |
|---|---|---|
| append-only，一行一事件，含 handoff 字段 | **VERIFIED** | 文件 `"a"` 打开（`928–929`）；`_record` 用 `sort_keys` 紧凑 JSON（`1063–1066`）；`HANDOFF_FIELDS` 28 字段在每条事件存在（测试 `:344`），另加 `call_id`/`result`/`args_sha256`/`result_sha256`/`duplicate_action_id` 等。审计者真实录制 29 事件可逐行解析、0 warning。 |
| 每事件 flush + fsync（派发前 intent 已落盘） | **VERIFIED** | `_record`: `write` → `flush` → `os.fsync`（`1066–1068`）；`_begin_call` 在 `inner.action` 之前先 `_record`（`1113–1125`、`1471–1479`）。测试 `test_each_event_is_flushed_and_fsynced_and_readable_before_close` mock `os.fsync` 断言 4 事件 4 次、且未 close 前文件完整（`:419–430`）。 |
| token/bridge auth 绝不写入 | **VERIFIED** | 键名正则脱敏 `_redact`（`277–297`）+ 已知 secret 值全串清洗 `_scrub_text`（`270–274`）+ 写前 `assert_no_known_secrets`（`300–311`、`1065`）；测试覆盖 token/Authorization/nested/password、transport 异常消息、结果不被 mutate（`:450–509`）；MCP 集成测试实跑断言 fake token 不在文件（`test_mcp_entrypoint.py:183–186`）；审计者 e2e 实测 `TOKEN_LEAKED=False`。 |
| 崩溃恢复扫描：crash-after-intent / after-dispatch / response-lost / running / uncertain / 孤儿 intent / duplicate id / corrupted-truncated line / session restart | **VERIFIED** | 判定表见 docstring `50–79`；`CrashRecoveryTests`（`:597–824`）逐项：intent 无 result → `needs_operation_query` 且 `bridge.calls==[]`（`:598–628`）；dispatch 后 result `response_status=missing` → 需 operation 查询（`:630–651`）；live transport error → response_lost + 同 id 恢复（`:653–677`）；running → 真实 `get_operation(id)` 解析（`:679–706`）；`unknown_operation` → UNKNOWN + stop + 写拒绝（`:708–732`）；孤儿 intent（无 op_id 无 action_id）→ UNKNOWN + stop（`:734–744`）；损坏/截断行容错（`:830–876`）；session restart 序号延续且 clean（`:790–815`）。 |
| 恢复永不自动重放；只接受真实 `get_operation(id)`/`wait_operation` 或原 action_id | **VERIFIED** | `analyze_events` 是纯函数、不派发；`_resolution_from_record`（`:457–488`）：query 分支要求 `operation_id` 精确匹配且非 `"*"`，写分支要求 `op` 相同且 result 事件 `action_id` 等于原 id；`TraceRecorder.__init__` 扫描后只写一条 `recovery_scan` 事件（`:930–938`），测试断言重启后 `self.bridge.calls == []`。 |
| `get_operation("*")` 只记录、绝不解析 | **VERIFIED** | `_resolution_from_record` 对 `operation_id == "*"` 直接 `return None`（`:477–478`）；`_note_operation_query` 对 `"*"` 直接 return（`:1274–1275`）；测试 `test_get_operation_wildcard_is_logged_but_never_resolves`（`:746–768`）断言仍 `needs_operation_query` 且 wildcard intent 有记录。 |
| 未决/UNKNOWN 期间拒绝写操作（只放行读/原 id 重放） | **VERIFIED** | `_write_refusal`（`:1357–1382`）：unknown → `recovery_unknown`；有 pending → 仅允许 `_matching_pending(op, action_id)` 的原 id 重放，否则 `recovery_pending`；`action()` 在 `_begin_call`/派发前检查（`:1462–1468`）。测试 `:558–590`、`:817–823`；审计者独立探针实测写被拒、`get_state` 仍可用。 |
| duplicate action_id 记录但不额外派发 | **VERIFIED** | `_begin_call` 维护 `_seen_action_ids`，标 `duplicate_action_id/duplicate_of_call_id`（`:1103–1125`）；测试 `:533–556` 断言 2 次显式派发 + 第二次被标记。 |
| 导出由记录 request/result 字节 1:1 重建；模型文字/claims 不能泄漏 | **VERIFIED** | `export_trace` 只用 intent 的 `op/args` 与 result 的 `result` 构造 step（`:754–784`），不复制事件上的其他键；测试 `:991–1034` 与审计者独立探针 `ANNOTATION_LEAKED=False`。审计者 e2e 14/14 步与记录字节全等。 |
| 校验记录的 sha256（篡改即拒绝） | **VERIFIED** | `_verify_recorded_checksum`（`:653–666`）；recorder 每事件写 `args_sha256`/`result_sha256`（`:1058–1061`）；测试 `test_tampered_recorded_result_is_rejected_at_export`（`:1100–1117`）；审计者篡改一条 result 后导出 fail-closed。 |
| 不完整记录 fail-closed | **VERIFIED** | `export_trace` 收集 `unresolved`（无 result / `response_status != received` / result 非对象）后 raise `TraceExportError`（`:754–792`）；测试 `:1095–1098`；审计者 intent-only 录制导出 fail-closed。 |
| `--trace-recording` 打不开即 fail closed、不行为变化于缺席 | **VERIFIED** | 集成段 `1396–1429`：构造失败 `return 5` 且 stderr 明确；缺席时整块 if 不执行；`--probe` 在此之前退出（`1357–1359`）。测试 `test_mcp_entrypoint.py`：flag 记录 intent+result、script/package 双入口、无 flag 无任何文件、`--memory` 时包在 memory 外层（记录器只见 MCP 调用）、不可写路径 exit 非 0 且无残留（`:118–276`、`:66–90`）。审计者实测不可写路径 **exit 5**。 |
| 记录 get_operation/回查/人工介入/停止原因 | **VERIFIED** | `record_intervention`（`:1488–1495`）、`record_stop`（`:1497–1500`）、`_record_stop`（`:1229–1236`）；导出 metadata 汇总 `human_interventions/stop_reasons/recovery_decisions`（`:669–710`）；测试 `:882–905`。 |
| 事件字段真实反映隔离/权限上下文 | **VERIFIED** | `run_id/actor/model_id/prompt_sha256/skill_sha256/host_version/policy_sha256/tool_surface/recording_slot` 由构造参数写入每条事件（`:1029–1051`）；MCP 集成传入 actor=mcp、model_id（env）、policy sha、tool surface、host_version=mode、recording_slot（env）。见 W-C2-2 的两项缺口。 |

### 2.3 审计者端到端复跑（非报告数字）

见 §0.1 “记录器端到端”表：29 事件 / 0 warning / token 未泄漏 / 14 步 1:1 导出 / validator exit 0 且 0 error 0 warn / 不完整与篡改记录均 fail-closed / CLI export exit 0 / 不可写 `--trace-recording` exit 5 无残留。`FakeGame` 与 `BridgeClient` 均为本机进程内 127.0.0.1，无外网、无游戏。

### 2.4 WARN（不阻塞 C2，但应在 L3 前修）

- **W-C2-1（低）result 事件 `dispatch_status` 无条件写 `"dispatched"`。** `_finish_call` 只要收到 dict 响应就写 `DISPATCH_DISPATCHED`（`trace_recorder.py:1158–1173`，`:1166`），不参考响应自身的 `dispatched`/`dispatch_status`。审计者探针：一次 `stale_rev` pre-dispatch 拒绝（响应无 `dispatched` 字段）被记录为 `dispatch_status="dispatched"`、`execution_status=None`。影响面有限：该拒绝是 terminal-by-error，恢复扫描不把它当 pending，导出使用响应原始字节，validator 也不读该字段；但审计/报表若把该轴当“原生已派发”证据会误读。建议从响应派生（`dispatched`/`dispatch_status`，缺失时 `unknown`），或把该字段明确改名/注释为“HTTP 调用已发出”。
- **W-C2-2（中，L3 前置）协议 §2 强制字段未被 `--trace-recording` 填充。** `R5_LLM_PILOT_PROTOCOL.md:33–45` 要求每次 run 登记 `model_id/host_version/prompt_sha256/skill_sha256/policy_sha256/tool_surface/recording_slot/run_id`，并写“任何一项无法核对 → 不开始”。MCP 集成只传 actor/model_id/policy_sha256/tool_surface/host_version(mode)/recording_slot（`mcp_server.py:1412–1421`）；`prompt_sha256`/`skill_sha256` 保持 `None`，`run_id` 是记录器随机会话 id（`trace_recorder.py:893`），不是桥的真实 run identity。记录器本身支持这些字段，但**按当前文档启动的真实 pilot 无法满足协议 §2**。建议：MCP/宿主入口补 `PAWNMIND_RUN_ID`/`PAWNMIND_PROMPT_SHA256`/`PAWNMIND_SKILL_SHA256`（或显式说明由宿主 wrapper 填充并把 run_id 定义为桥 run identity），否则 L3 时轨迹无法与 run identity 对账。此项不改变 C2 离线判据，但直接关系到 handoff “评测层只认真实回执/可追溯”的前置。

### 2.5 C2 残留风险

- **R-C2-1（低）** 导出对**没有 `*_sha256` 字段**的旧/手写事件跳过校验（`_verify_recorded_checksum` 对 `expected is None` 直接返回，`:655–657`）。这是为兼容 legacy/手写 fixture 的设计，但意味着 tamper-evidence 只覆盖 recorder 生成的行；对抗性防篡改本就需要 append-only 存储/异地副本（`R5_C_OFFLINE_REPORT.md` §5 亦如实说明）。
- **R-C2-2（低）** 记录器 docstring 写“parent directory created on demand”（`trace_recorder.py:872–873`），实际 `open()` 不创建父目录；行为仍是 fail-closed（不可写 → MCP exit 5 / 直接调用抛错），只是文档措辞略超实现。
- **R-C2-3（设计取舍）** 每个事件 `fsync`；`wait_events` 长轮询会放大文件体积/IO（Lead 离线报告 §6 已列）。

---

## 3. C3 — LLM 最小决策接口 + 可信执行层

### 3.1 读了什么

- `agent/decision_interface.py` 全文（458 行）：`FORBIDDEN_ARG_KEYS`（`44–57`）、`Decision.from_dict`（`147–172`）、`Budget`（`175–241`）、`classify_response`（`244–265`）、`Executor`（`284–458`）。
- `agent/tests/test_decision_interface.py` 全文（19 用例）：schema、预算、分类、executor（rev/action_id、uncertain、stale、rejections、play 门禁、回读）。
- `docs/eval/R5_LLM_PILOT_PROTOCOL.md`（四层、决策形状、预算、成功/失败、停止条件、Play 终态）、`docs/audit/R5_NEXT_LIVE_SESSION.md`（S1–S9 真机步骤，明确未执行）。
- 对照：handoff §4 C1/C3、§8 停止条件；`tool_policy.play_allowlist` 实现与 `tool_policy.json`。

### 3.2 逐条验证

| 要求 | 结论 | 证据 |
|---|---|---|
| Decision schema 拒绝未知工具 / `rev` / `action_id` / 隐藏字段参数 | **VERIFIED** | `known_tools()` 从 policy 读取，未知工具拒绝（`:156–158`）；`FORBIDDEN_ARG_KEYS` 含 action_id/rev/隐藏鉴定字段/add_credit 等（`:44–57`），命中即 `DecisionError`（`:123–127`）；嵌套 list/dict 值也拒绝。测试 `:98–138`；审计者独立探针四项全拒。 |
| 模型不能设置 rev/action_id；执行层自生成 | **VERIFIED** | `Executor.execute` 生成 `uuid` action_id（`:368`），rev 取自最近 `get_state`（`:316–322`、`:362–366`）；测试 `test_executor_owns_rev_and_action_id`/`test_model_cannot_choose_the_action_id`（`:194–212`）；审计者探针确认派发参数里的 rev/action_id 来自执行层。 |
| exactly one dispatch（正常路径） | **VERIFIED** | `execute` 每次只 `_act` 一次（`:372`）；成功/拒绝/running 均不再派发；测试断言 client 调用计数（如 `:223–227`）。 |
| `uncertain` → 只查询 + 停止，绝不重派发 | **VERIFIED** | `:406–419`：tick 预算后 `get_operation(op_id)` 一次 → `stopped=True`、`stop=True`；测试 `test_uncertain_queries_operations_and_never_re_dispatches`；审计者探针实测 1 次派发 + 1 次查询 + stop。 |
| `stale_rev` → 刷新 + 同一 id 重放一次，再 stale 即停 | **VERIFIED** | `:375–388`：refresh rev、复用同一 action_id 重放；仍 stale → budget 异常停止并带 stop_reason；测试 `:229–246`；独立探针 2 次同 id 后 stopped。 |
| 预算（动作/连续拒绝/连续 uncertain/墙钟）安全停止 | **VERIFIED** | `Budget`（`:175–241`）：`max_actions`、`max_consecutive_rejections`、`max_consecutive_uncertain`、`max_wall_seconds`；超限抛 `BudgetExceeded` → `_stop` 保留 request/response；测试 4 个预算用例（`:141–175`）+ rejection 停机（`:248–257`）。审计者独立验证 rejection 阈值停机。 |
| Play 模式：allowlist 为空 → `blocked_by_play_policy`（不是 PASS） | **VERIFIED** | `play_allowlist_from_policy()` 调 `tool_policy.play_allowlist`，实测 `[]`；`_surface_refusal`（`:325–335`）在 play 模式先查 allowlist，空表 → 所有工具 `blocked_by_play_policy`；`Executor.execute` 先拒绝再 tick，**0 次 client 调用**；测试 `:259–266`；独立探针 `PLAY_BLOCKED=True CALLS=0`。 |
| 每步把真实返回与新快照送回模型 | **VERIFIED** | `StepResult.observation` 为完成后重新 `get_state` 的结果（`:390–395`、`:268–282`）；测试 `:268–273`。 |
| 协议文档与执行层一致、无编造数字、L3 NOT_RUN / Play BLOCKED | **VERIFIED**（1 处版本漂移 W-C3-3） | `R5_LLM_PILOT_PROTOCOL.md` §3 与 `Decision` 字段一致；§4 预算默认值与 `DEFAULT_LIMITS` 一致（300/5/90min；实现 uncertain 连击更严为 3）；§5 判定表与 `trace_recorder`+`trace_validator` 一致；§7 停止条件覆盖 uncertain/记录器失败；无通过率/成本数字。 |
| 不越权访问隐藏字段/任意代码/文件/token | **VERIFIED**（`Decision` 层） | 决策层只产出工具名+标量 args；`FORBIDDEN_ARG_KEYS` 拒绝隐藏字段探针与 `add_credit/grant_item`；执行层走 bridge 白名单工具；记录器不写 token。 |

### 3.3 WARN（不阻塞 C3，但建议收口）

- **W-C3-1（低）空 `allowed_tools` 被当作“不限制”而非“拒绝全部”。** `_surface_refusal` 用 `if self.allowed_tools and decision.tool not in self.allowed_tools`（`:333`）；当调用方传入 `allowed_tools=set()`（例如想表达 locked/空工具面）时，任意 policy 内工具都放行。审计者探针：`Executor(client, allowed_tools=set(), mode="test")` 对 `open_shop` 返回 `completed` 且**派发 1 次**。Play 门禁不受影响（play 分支更早且 fail-closed），但“空 surface”与“不限制”在 fail-closed 哲学下应区分：建议空表 → `tool_not_allowed` 拒绝，显式 `None` 才表示不限制（并把该语义写进 docstring/tests）。
- **W-C3-2（低）`execute()` 只对 dict 决策做 schema 校验。** 传 dict 时走 `Decision.from_dict`（`:338–343`）；若宿主直接构造 `Decision(args={"rev":99})`，`execute` 不再复核 `FORBIDDEN_ARG_KEYS`，`request` 字典还会被 args 覆盖（`:369–370`），`_act` 的 TypeError 回退分支用 `payload.update(args)` 同样可覆盖 rev/action_id（`:436–445`）——只是当前生产 client（`BridgeClient`/`TraceRecorder` 均为 `action(op, **args)`）在这条路径上会 TypeError 而不是真正派发，故无现网可利用点。建议把同样的校验放到 `Decision.__init__`/`execute` 入口，避免宿主绕过文档路径。
- **W-C3-3（低，文档漂移）`R5_LLM_PILOT_PROTOCOL.md` 仍写 validator v1.2.0。** `:9` 与 `:41` 写 `v1.2.0`；实际 `agent/trace_validator.py:169` 为 `VALIDATOR_VERSION="1.3.0"`（R5-B1 后 call-scoped 加入 `stage_negotiation_item`，schema 仍 2）。`f18e25b8` 已把 `R5_NEXT_LIVE_SESSION.md:34` 改为 1.3.0，但漏了协议文档；pilot 前应同步，避免登记错误基线。
- **R-C3-1（设计范围）** 执行层对 `running` 只返回 hint（`get_operation/wait_operation` 由宿主调用），未实现自动 `wait_operation`/重连收敛 driver；与 handoff“不盲重试”一致，但意味着真实 run 的恢复回合由宿主负责，L3 前需要宿主侧实现（`NOT_RUN`）。
- **R-C3-2（观察）** `uncertain` 路径总是立即 stop，因此 `max_consecutive_uncertain` 在正常调用序列中不会被累加到阈值；这是防御性预算而非可达路径，不构成缺陷。

---

## 4. C4 — 六个 deterministic L1-fake 情景

### 4.1 读了什么

- `agent/tests/test_fake_scenarios.py` 全文（851 行，11 用例，6 个情景类）。
- `agent/tests/test_fake_game_contract.py` 的 R5 面新增（`R5SurfaceContractTests`）。
- `agent/fake_game.py` R5 增量（`git show 243d1f91 -- agent/fake_game.py`，+1183/−54）：host-only 夹具（`465–597`）、状态投影（`604–682`）、操作信封（`877–1097`）、交易（`2154–2335`）、staging（`2337–2445`）、typed modal（`2447–2500`）、租金（`2502–2567`）、sleep（`2634–2745`）。
- 对照 C# 契约（只读源码，不复制大段实现）：`src/Bridge/TradeService.cs`（completed/refusal/uncertain/EvidenceJson）、`src/Core/TradeGuard.cs`（Evaluate 顺序与错误码）、`src/Bridge/NegotiationStagingService.cs`（completed/refusal/uncertain 行字段、DetailFor）、`src/Core/NegotiationStagingGuard.cs`（`StagingPrimitives`、容器集合）、`src/Bridge/PromptJson.cs`（PromptObject/StateSummary）、`src/Bridge/PromptService.cs`（answer 成功/失败行）、`src/Bridge/FirstWeekJson.cs`（Receipt 字段）、`src/Core/Rent.cs`（receipt id/字段）、`src/Bridge/RecordingSaveService.cs` + `src/Core/ShopOperationSemantics.cs`（sleep 观察窗/派发语义）、`src/Core/RunChangePolicy.cs`（`PendingScope.IsCallScoped`）。
- 文档标注：`test_fake_scenarios.py` 模块 docstring 标明 **L1-fake**、`L3 NOT_RUN`；`R5_C_OFFLINE_REPORT.md` §4/§5；`known-limitations` §11.5。

### 4.2 六个情景（本机实跑，`Ran 11 tests — OK`，连跑两次一致）

| # | 情景 | 实测要点（对应测试） |
|---|---|---|
| 1 | 正常多笔交易 | 同一顾客 stage A→B、两笔 `confirm_trade` completed（`items_transferred`/`cash_moved`/`client_unchanged`），第三笔给下一位顾客；现金 25→47→157→192、库存 3→0、`get_operation` 回查 completed、3 次 native click（`NormalMultiTradeScenarioTests`）。 |
| 2 | 买不起/静默拒绝 | 顾客预算 10 < 价 22 → `client_budget_insufficient`、`dispatched:false`、native click 0、无任何伪造证据字段；注入静默 native accept → `uncertain/outcome_unverified`（非 ok），同 item+side 新 id 重试 → `operation_pending`（`ClientCannotAffordScenarioTests`）。 |
| 3 | 不同商品 staging | A→B→A 的 `staged_item_id_before/after` 正确、`negotiation_open`/`client_identity_unchanged`/`item_on_weighted_counter` true；`no_client`/`item_not_found` fail-closed 且 0 click；uncertain 同 item 新 id → `operation_pending`，不同 item 仍 completed（`DifferentItemStagingScenarioTests`）。 |
| 4 | 日终模态 | `get_pending_prompt` 返回 `kind:"negotiation_confirm"`、`target:"negotiation_tablet_confirm"`、body/labels/enabled；`/state.prompt` 同对象；`answer_prompt(confirm)` → `prompt_closed:true` + `state.shop_state:"CLOSED"` + 1 次 native click；关闭后回答 → `no_prompt`、0 额外 click（`DayEndModalScenarioTests`）。 |
| 5 | 异步 sleep | sleep → running/pending，立即/重复 `get_operation` 均 inconclusive（无 `failed`/`day_not_changed`）；host `finish_sleep()` 后 completed + `completion_evidence.source=day_readback`；模拟 5s running、11s（>10s 窗）→ `sleep_unconfirmed` uncertain 非终态、新 id 被 `operation_pending` 拒绝、之后 day 前进恢复 completed；`get_operation("*")` 21 非终态 → count 20/total 21/truncated/note（`AsyncSleepScenarioTests`）。 |
| 6 | 首周租金 | `end_day+sleep`×7 到房东；`choose_dialogue(0)` 回执 `rent_1_100`：amount 100、cash 200→100（`cash_delta:-100`）、`weeks_paid_delta:1`、`paid_at_rev`=派发 rev、`detection_complete:true`；之后 +30 交易不改变回执字节，现金 130、rent 状态一致（`FirstWeekRentScenarioTests`）。 |

确定性：模块内无 `time.sleep`/`time.time`（grep 无命中）；异步转移由 host 夹具 + 模拟时钟驱动；两次运行均 `Ran 11 OK`。host-only 夹具（`stock_item/arrive_customer/raise_negotiation_confirm/finish_sleep/advance_clock/inject_native_fault`）不在 `_write` HTTP dispatch map（`fake_game.py:1639–1670`）也不在 `_read` 表，**不可由“模型”经 HTTP 调用**，只供测试进程使用。

### 4.3 fake ↔ 已提交 C# 契约抽查（字段级）

| 契约面 | 抽查结论 |
|---|---|
| `stage_negotiation_item` completed 行 | fake 输出的 `ok/op/dispatched/execution_status/item_id/staged_item_id_before/staged_item_id_after/negotiation_open/client_identity_unchanged/item_on_weighted_counter/client_intent/native_primitive/evidence` 与 `NegotiationStagingService.cs`（completed 行）逐名一致；`evidence` 子字段（`called/error/staged_item_id_before/staged_item_id_after_readable/staged_item_id_after/item_on_weighted_counter/negotiation_readable_after/negotiation_open_after/client_identity_unchanged/native_primitive`）一致。 |
| staging 拒绝/uncertain | 15 个拒绝码的 `detail` 字符串逐项来自 `DetailFor`（`STAGE_REFUSAL_DETAILS`）；uncertain 行的 `staged_item_id_after/negotiation_open/client_identity_unchanged` 在不可读时为 `None`、`next` 文案一致。 |
| native primitive | `weighted_area_add` / `weighted_area_remove_add` 与 `StagingPrimitives` 一致；`PendingScope` 的 call-scoped 集合（`confirm_trade` + `stage_negotiation_item`）与 `RunChangePolicy.cs` 一致。 |
| `confirm_trade` completed/refusal/uncertain | fake 三行与 `TradeService.cs` 对应行字段一致（completed 的 `client_unchanged/staged_item_id/cash_*`、refusal 仅 7 字段无证据、uncertain 的 `unknown:true`+`evidence`+`next`）；`evidence` 子字段与 `EvidenceJson` 一致；`business_complete` 仅归因、非成功判据。 |
| 预算预检 | fake 在 dispatch 前按 `selling && uses_budget && price > budget` 拒绝（与 `TradeGuardPolicy` 的 `client_budget_insufficient` 顺序/方向一致）；拒绝无证据字段。 |
| typed modal | `_prompt_object` 与 `PromptJson.PromptObject` 的 9 个键一致（`kind/target/body/confirm_text/cancel_text/single_button/confirm_enabled/cancel_enabled/source_op`）；answer 成功行与 `PromptService` 的 `ok/op/dispatched/choice/prompt_closed/prompt=null/state` 一致；`state` 回读键与 `PromptJson.StateSummary` 一致。 |
| 租金回执 | fake 回执字段与 `FirstWeekJson.Receipt` 逐名一致（含 `id/amount/cash_*/rent_days_left_*/paid_at_rev/source/day/weeks_paid_delta/rent_weeks_*/due_before/day_before/detection_complete`）；id 生成式 `"rent_%d_%d"` 与 `Rent.cs` 一致（首笔 `rent_1_100`）。 |
| sleep | fake `async_sleep=True` 模型与 `RecordingSaveService.Sleep` + `ShopOperationSemantics.Sleep` 一致：day 未变 → running/pending、跨 10s 窗无证据 → uncertain/`sleep_unconfirmed` 非终态、day 前进 → completed+evidence；`SLEEP_OBSERVATION_WINDOW_MS=10000` 与 C# `SleepObservationWindowMs` 一致。 |

### 4.4 WARN（不阻塞 C4，但需在报告/后续收口）

- **W-C4-1（低，表述精度/遗留模式）默认 `async_sleep=False` 的 fake sleep 仍返回旧同步 `ok:true`，与 R5 桥的 running/pending 不一致。** `_sleep` 同步分支直接 `day += 1` 并 `return {"ok": True, ... "readback": {"day_advanced": True}}`（`fake_game.py:2707–2745`）；而 R5 的 `RecordingSaveService.Sleep` 在 day 未立即前进时经 `ShopOperationSemantics.Sleep` 返回 `Running`，`SaveSuccess` 设 `ok=false/pending=true`（源码对照）。该旧模式被现有 S6B 测试与 scenario 6 的“快进到租金日”夹具使用，`test_fake_game_contract` 也明确“默认保持 S6B 同步模型、async 为 opt-in”。因此 scenario 5 的 “异步 sleep”契约仍被真实镜像，但 `R5_C_OFFLINE_REPORT.md:114` 的绝对表述“fake 不会输出桥不会输出的成功形状”对默认模式**不成立**；建议二选一：把离线报告改为“R5 情景（async_sleep 模式）不会输出…；默认模式保留 S6B 同步 sleep 仅作夹具”，或最终把 async 设为默认并迁移旧测试。其它五个情景未发现“桥不会输出的成功”。

---

## 5. 发现汇总（WARN/NOTE，均未到 FAIL）

| ID | 级别 | 一句话 | 建议 |
|---|---|---|---|
| W-C2-1 | 低 | result 事件 `dispatch_status` 对 pre-dispatch 拒绝也记 `dispatched` | 从响应 `dispatched/dispatch_status` 派生，缺失记 `unknown`；或改字段语义并在文档标注 |
| W-C2-2 | 中（L3 前置） | 协议 §2 的 `run_id`(真实 run identity)/`prompt_sha256`/`skill_sha256` 未被 `--trace-recording` 填充 | MCP/宿主入口补 env/参数；明确 run_id 定义 |
| W-C3-1 | 低 | `Executor` 空 `allowed_tools` = 不限制（fail-open） | 空表拒绝全部，`None` 才表示不限制 |
| W-C3-2 | 低 | `execute()` 不复核直接构造的 `Decision` args；`_act` 回退可覆盖 rev/action_id | 在 `Decision.__init__`/`execute` 入口统一校验 |
| W-C3-3 | 低（文档） | `R5_LLM_PILOT_PROTOCOL.md:9,41` 仍写 validator v1.2.0（实际 1.3.0） | 同步为 1.3.0（`R5_NEXT_LIVE_SESSION` 已在 f18e25b8 修正） |
| W-C4-1 | 低（表述/遗留） | 默认 fake sleep 仍同步 `ok:true`，离线报告“绝不输出桥不会输出的成功”绝对化 | 修报告措辞或改 async 为默认 |
| R-C2-2 | 低（文档） | 记录器 docstring 称自动创建父目录，实际 `open()` 不创建 | 修 docstring（行为本身 fail-closed） |

无 FAIL：未发现“把脚本运行当 LLM 结果”“恢复静默自动重放”“导出把错误降级为通过”“模型能设置 rev/action_id/绕过 Play 门禁”“fake 在六个 R5 情景中伪造桥不会给的完成”的可复现证据。

---

## 6. 证据分级汇总

### 6.1 VERIFIED（本次实际运行或读源码/文档并定位）

- 门禁：C# `passed=864 failed=0` + gate `53/0` + `contract 526`（exit 0）；Python `Ran 498 OK`（exit 0，0 skip）；聚焦 41/8/19/11/23/117/20 全 OK；`gen_policy_catalog --check` exit 0 + sha `774fbfdd…26763277`；117/107/`play_allowlist=[]` 实算。
- 记录器 e2e：29 事件/0 warning、token 未落盘、14 步 1:1 导出、validator exit 0 且 0 error/0 warn、不完整/篡改记录 export fail-closed、CLI export exit 0、不可写 `--trace-recording` exit 5 无残留。
- C2 独立探针：pre-dispatch 拒绝的 dispatch_status 现象（W-C2-1）、模型文字不入导出、伪造 claims 被 validator 拒（`MODEL_SELF_REPORT_IS_NOT_EVIDENCE`/`REJECTED_CLAIMED_AS_SUCCESS`）、未决期间写拒绝+读可用。
- C3 独立探针：schema 拒绝 rev/action_id/隐藏字段/未知工具、Play `blocked_by_play_policy` 0 调用、空 surface fail-open（W-C3-1）、uncertain 单派发+查询+stop、stale 同 id 两派发后 stop、rejection 预算 stop。
- C4：六情景 11 用例连跑两次 OK、无 wall-clock、host-only 夹具不经 HTTP；fake 与 C# 契约的字段级抽查（§4.3）；实测字段值（现金 25→47→157→192、回执 `rent_1_100`、sleep 10s 窗、`get_operation("*")` 20/21/truncated）。
- C1：无 `week.py` 包装、`RulePolicy` 未变、C 段 numstat 无策略脚本、协议文档无编造数字且 L3/Play 标注正确。

### 6.2 REPORTED（开发者/Lead 声明，本次未独立复现）

- 各提交的 **TDD RED 证据**（例如 C4 提交信息“动手前 9 个测试失败”、C2 提交信息“skeleton NotImplementedError”）——本次未在历史红态重放；仅审计者自己的独立探针属 VERIFIED。
- `OVERNIGHT_LOG.md` 中 C 段数字（C# 864/526、Python 498、B1 等）：与本次独立实测的 C#/Python 数字一致，但作为历史记录仍标 REPORTED（本次证据以 §0.1 为准）。
- `R5_C_OFFLINE_REPORT.md` 中 Lead 自跑的 recorder round-trip（“10 steps”）——本次未复现该具体脚本；审计者用自己的 14 步 round-trip 独立验证同一性质。
- 真实 LLM 策略质量/通关率/成本：**没有任何可用测量**（`NOT_MEASURED`），不得引用。

### 6.3 NOT_RUN（环境/授权）

- L3 全部：真实游戏内的 staging / typed modal / overlay / sleep 异步时序 / 预算拒绝 / 多笔真实交易 / 租金支付；Windows、DLL 安装、存档、Workshop、官方 scanner、`dev.txt`。
- 真实 LLM 决策循环与宿主（DSH/OpenCode）适配。
- 历史首周原始轨迹复核（E2 缺失）、`_internal/week.py` 复核（E3 缺失）。
- 记录器在真实桥长轮询/断线重连下的行为；tamper-evidence 以外的对抗性防篡改（需要 append-only 存储/异地副本）。

---

## 7. 审计判定

- **C1 PASS（离线）**：无脚本策略冒充 AI；四层职责分离；模型不能设置 rev/action_id；文档不把脚本运行当 LLM 结果；协议无编造数字并正确标注 L3 NOT_RUN / Play BLOCKED。
- **C2 PASS（L1 + 离线）**：记录器/恢复/导出/集成均按 handoff 语义实现并有测试 + 审计者独立 e2e 证据；2 个 WARN（W-C2-1 低、W-C2-2 L3 前置）需在 L3 前收口。
- **C3 PASS（L1）**：schema/执行/恢复/预算/Play 门禁逐项可复核；3 个 WARN（空 surface fail-open、Decision 复核缺口、协议版本漂移）均为低风险收口项。
- **C4 PASS（L1-fake）**：六个必需情景存在、确定、与已提交 C# 契约字段级一致、不伪造桥不会给的完成（唯一例外是默认 legacy sync sleep 的表述问题 W-C4-1，已隔离在 opt-in async 模型之外）。
- **L1-fake 与 L3 的边界（显式）**：本轮 C 段全部结论仅建立在 **L1-fake/纯逻辑/离线工具**上；**真实游戏（L3）全部 `NOT_RUN`**。本审计不把任何 fake/测试结果表述为真实游戏行为，也不把 C2/C3/C4 的 GREEN 当作 LLM pilot 证据。
- **正式 Play benchmark 终态：`BLOCKED_BY_PLAY_POLICY`**（`play_allowlist=[]`，逐工具 live evidence 缺失），**不是 PASS，也不是 FAIL**；本轮没有任何真实 LLM pilot 运行，也没有 `L3 TEST_MODE_PILOT`。
- **C 阶段离线退出门槛：满足**（可放行进入最终汇总/停止等待 Windows 授权）。若 Lead 决定在 L3 前修 WARN，本审计建议至少修 W-C2-2（run identity/prompt/skill 登记）与 W-C3-1（空 surface fail-open）。

> 审计声明：本文件为 reviewer-c 本次唯一写入；未修改生产代码/测试，未 `git add`/`commit`，未构建 `dist/`。所有“VERIFIED”均可在本次会话命令与 `_internal/logs/review_c_cs.log`、`_internal/logs/review_c_py.log`（gitignored 原始输出）中复核；“REPORTED/NOT_RUN”按 §6 显式区分，未把开发者/Lead 的数字当作自己执行的结果。

---

## 9. Addendum（post-WARN-fix 复验，`9680e3ba`）

> 追加时间：2026-11-18T21:38Z。触发：Lead 收到本审计 WARN 后提交 `9680e3ba fix(r5-c): address reviewer-C offline warnings`（改动 `agent/trace_recorder.py`、`agent/decision_interface.py`、对应测试与两份文档；**该提交无 C# 文件**）。复验 worktree：`_internal/review-c-warnfix`（detached `9680e3ba`，`git status` 干净）。本节只追加，不修改上文原始审计目标（`243d1f91`）的记录。

### 9.1 WARN 关闭情况（审计者独立复验）

| ID | 原问题 | 修复证据（`9680e3ba`） | 审计者独立复验 | 状态 |
|---|---|---|---|---|
| **W-C2-1** | result 事件 `dispatch_status` 无条件 `dispatched` | 新 `_dispatch_claim(payload)`：写路径按响应 `dispatched`（true→`dispatched`，false→`not_dispatched`，缺失/类型错→`unknown`），读路径仍 `dispatched`；`DispatchAxisTests` 3 例 | 探针：`stale_rev` 拒绝→`not_dispatched`；无 claim→`unknown`；`dispatched:true`→`dispatched`；读→`dispatched`，全部 MATCH；post-fix e2e 写 op 轴全正确 | **CLOSED** |
| **W-C3-1** | 空 `allowed_tools` fail-open | `Executor.__init__`：`None` 才表示 unrestricted，空 iterable=`set()`（deny-all）；`_surface_refusal` 相应区分；2 个新用例 | 探针：空 surface→`BLOCKED`/0 calls；`None`→`COMPLETED`/1 call，MATCH | **CLOSED** |
| **W-C3-2** | 直接构造的 `Decision` 绕过参数校验 | `execute()` 先对 `decision.args` 重新执行 `_check_args`（拒绝执行层保留键/隐藏字段探针）；未知工具在 `_surface_refusal` → `tool_not_allowed`（surface BLOCK，不派发）；2 个新用例 | 探针：`Decision(args={"rev":9})`→`REJECTED`/0 calls；未知工具→`BLOCKED`/0 calls，MATCH | **CLOSED** |
| **W-C3-3** | 协议文档仍写 validator v1.2.0 | `R5_LLM_PILOT_PROTOCOL.md:9,41` 改为 v1.3.0 | 实测 `agent/trace_validator.py:169 VALIDATOR_VERSION="1.3.0"`；grep 协议两处均为 1.3.0 | **CLOSED** |
| **W-C2-2** | 协议 §2 强制字段未被 `--trace-recording` 自动填充 | 协议 §2 新增“记录器已自动填写的字段”清单，并明确 `prompt_sha256`/`skill_sha256`/桥 `run_id` 必须由宿主提供并单独存档（L3 前待办，不是“已记录”） | 文本核对；实现未变（按设计） | **PARTIALLY CLOSED**：文档已如实；L3 宿主集成项保留 |
| **W-C4-1** | 默认 fake sleep 与 R5 桥不一致 + 报告绝对表述 | `R5_C_OFFLINE_REPORT.md` §4 增加范围说明（默认 `async_sleep=False` 为预 R5 S6B 遗留即时 `ok:true`；R5 异步模型是 opt-in，情景 5 使用）；fake 代码未改（无需改） | 文本核对 | **CLOSED（措辞）** |
| R-C2-2 | recorder docstring 称自动创建父目录（实际 `open()` 不创建，行为本身 fail-closed） | 本提交未改 | 复读 docstring 仍未修改 | **OPEN（低，文档 nit）** |

### 9.2 新 tip 全量门禁（VERIFIED，clean worktree of `9680e3ba`）

| 套件 | 命令 | exit | 结果 |
|---|---|---|---|
| Python 全量 | `PYTHONPATH=$PWD .venv/bin/python -m unittest discover -s agent/tests` | **0** | **Ran 505 tests，OK**（0 skip；`243d1f91` 为 498，+7 新测试） |
| 记录器 | `… -m unittest agent.tests.test_trace_recorder` | **0** | **Ran 44 — OK**（含 3 个 `DispatchAxisTests`） |
| 决策接口 | `… -m unittest agent.tests.test_decision_interface` | **0** | **Ran 23 — OK**（含 4 个 surface guard 用例） |
| validator | `… -m unittest agent.tests.test_trace_validator` | **0** | **Ran 117 — OK** |
| fixtures CLI（8 个） | `python agent/trace_validator.py <fixture>` | — | **5×exit 0（codes=[]）+ 3×exit 1**：`pending_same_item_retry_conflict`/`pending_stage_same_item_retry_conflict` → `CONFLICTING_WRITE_WHILE_PENDING`；`trade_visit_complete_no_effect_invalid` → `TRADE_EVIDENCE_MISSING` |
| C#（该提交无 C# 改动；仍复跑作当前 tip 的背景证据） | `flock … REQUIRE_GAME=1 ./test.sh`（env 同前） | **0** | **passed=864 failed=0；gate 53/0；contract checks=526 / OK**（与 `243d1f91` 一致；该区间另含 B 复审 C# 修复，仍全绿） |
| post-fix recorder e2e | FakeGame→TraceRecorder→export→validator | **0** | 28 事件 / **14 steps** / validator **0 error 0 warn**；写 op `dispatch_status` 全正确；token 未落盘 |

原始日志：`_internal/logs/review_c_warnfix_py.log`、`_internal/logs/review_c_warnfix_cs.log`。

### 9.3 判定

- 未发现新缺陷：`_dispatch_claim` 只影响记录事件的 dispatch 轴（恢复/导出分别使用 execution/response 轴与响应原始字节），44 个记录器测试 + post-fix e2e 证明未回归；空 surface deny-all 与 Decision 二次校验没有削弱既有测试语义。
- **Verdict 不变：C1 PASS / C2 PASS / C3 PASS / C4 PASS（均为离线/L1/L1-fake）；L3 全部 `NOT_RUN`；正式 Play 终态 `BLOCKED_BY_PLAY_POLICY`。** 6 个 WARN 中 5 个关闭（W-C2-2 由“协议未覆盖”转为“文档如实 + L3 宿接待办”），R-C2-2 低文档 nit 仍开；均不影响 C 段判据。
- 本节唯一写入为本文件追加；未改代码/测试，未 `git add`/`commit`。
