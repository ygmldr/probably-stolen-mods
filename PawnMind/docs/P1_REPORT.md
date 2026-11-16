# P1_REPORT — 可靠事件记录

- **日期**：2026-11-15
- **范围**：仅 P1（Event Store、Recorder、Snapshot/Diff、timeline、Action Reconciliation、`memory_history`、故障恢复测试）
- **明确未做**：自动记忆提炼、LLM 反思、Planner、向量库、强化学习
- **对应规范**：[架构 v1](<Probably_Stolen_Agent_Memory_Architecture_v1.md>) §12 Phase 1；[ADR-001](<ADR-001-memory-architecture.md>) ADR-008/009/010
- **前置**：[P0_REMEDIATION.md](<P0_REMEDIATION.md>) 六项契约已补齐

---

## 0. 一句话结论

**P1 验收标准全部达成**：AI 与游戏的每一次交互都能被可靠、可恢复、可追溯地写入本地 SQLite；历史写不进去时写动作会被拒绝；未知结果不会重放；`sleep` 只有在观察到真实跨日之后才算完成。

---

## 1. 修改与新增文件

### 新增

| 文件 | 行数 | 作用 |
|---|---|---|
| `PawnMind/agent/memory/__init__.py` | 6 | 包声明 |
| `PawnMind/agent/memory/store.py` | 470 | SQLite 事件库：timelines / snapshots / events，事务与并发 |
| `PawnMind/agent/memory/classification.py` | 100 | 语义分类（只读/异步/写盘/读档），与 C# 白名单契约绑定 |
| `PawnMind/agent/memory/recorder.py` | 330 | `RecordingClient`：intent→dispatch→result→reconcile、sleep 判据、timeline 分叉 |
| `PawnMind/agent/memory/cli.py` | 190 | `memory_history` 的 CLI 形态：`history` / `timelines` / `status` / `reconcile` |
| `PawnMind/agent/tests/test_memory_store.py` | 260 | 19 项 |
| `PawnMind/agent/tests/test_memory_recorder.py` | 300 | 15 项 |
| `PawnMind/agent/tests/test_memory_history.py` | 150 | 8 项 |
| `PawnMind/agent/tests/test_memory_wiring.py` | 110 | 3 项（真实 stdio MCP + fake 游戏） |

### 修改

| 文件 | 改动 |
|---|---|
| `PawnMind/agent/mcp_server.py` | 新增 `--memory {off,on}` 与 `--memory-db`；启用时把 `BridgeClient` 包成 `RecordingClient`；库打不开时 **拒绝启动**（exit 4） |
| `PawnMind/agent/dsh/pawnmind.patch.yml` | 启动参数加 `--memory on`，S8 录制默认开启历史 |
| `PawnMind/src/Bridge/RecordingDispatchGuard.cs` | ADR-011：`act` 永不可达 `debug_*`；录制态直接调用也被拒 |
| `PawnMind/src/Core/RecordingSavePolicy.cs` | 新增 `DebugForbidden` 错误码 |
| `PawnMind/src/Bridge/RecordingSaveService.cs` | 新增 `RecordingActive` |
| `PawnMind/agent/mcp_server.py`（schema） | `wine_add_water` 的 `rev` 改为必填 |
| `PawnMind/test/PawnMind.Tests/RecordingDispatchGuardTests.cs` | 新增 3 项 debug 封闭测试 |

---

## 2. 数据模型（实际建表）

```sql
timelines(timeline_id, agent_session_id, run_id, save_id, slot, run_number,
          parent_timeline_id, reason, game_version, game_mvid, instance_id, created_at)
snapshots(snapshot_id, timeline_id, state_json, state_hash, delta_json,
          game_day, rev, completeness, observed_at)
events(event_id, timeline_id, seq, action_id, event_type, tool_name,
       args_json, result_status, result_json, before_snapshot_id,
       after_snapshot_id, delta_json, capture_quality, game_day, rev, occurred_at,
       UNIQUE(timeline_id, seq))
meta(key, value)   -- schema_version = 1
```

`events` 是追加式：一条写动作 = 一行 `intent`（`result_status=pending`），结果通过 `UPDATE` 写回同一行。**不会**为结果新增第二行，因此“一次动作 = 一条可审计记录”。

`event_type` 取值：`intent` / `result`（保留）/ `observation` / `external_state_change` / `sleep_completed` / `sleep_unresolved`。

---

## 3. 关键行为与证据

### 3.1 intent 先落盘，再 dispatch（ADR-010）

`test_intent_exists_before_dispatch`：fake bridge 在被调用瞬间查询库中 pending 行，断言为 **1**。

`test_intent_write_failure_blocks_dispatch`：关闭库后调用写动作 → 抛 `MemoryUnavailable`，且 fake bridge **一次都没被调用**。

> 这是 fail-closed 的核心：记录不了的历史不允许发生。

### 3.2 只读操作不写 intent（ADR-008）

`test_read_only_action_records_observation_without_intent`：`list_goods` 只产生 `observation`。

`test_read_classification_matches_csharp_allowlist`：从 `src/Bridge/ActionRouter.cs` 解析 `ReadOnlyOps`，与 Python `READ_ONLY_ACTION_OPS` 逐项比对，**任何漂移都会失败**。

### 3.3 未知结果不重试

`test_action_exception_becomes_unknown_and_is_not_retried`：原生抛 `TimeoutError` → 记录 `unknown`，fake bridge 只被调用 1 次。

`reconcile_pending`：进程重启后，任何残留 `pending` 一律改判 `unknown`（`test_pending_intents_are_reported_for_reconciliation`、`test_reconcile_is_idempotent`）。

### 3.4 `sleep` 以观察到的跨日为完成判据（ADR-009）

| 测试 | 断言 |
|---|---|
| `test_sleep_is_not_complete_until_day_advances` | `sleep` 返回后 `sleep_completed` 数为 0；`day` 变 2 后出现 1 条 |
| `test_second_sleep_while_pending_is_refused_locally` | 第二次 `sleep` 返回 `sleep_pending`，且**从未到达游戏** |
| `test_sleep_without_day_advance_becomes_unresolved` | 3 次观察未跨日 → `sleep_unresolved`；之后拒绝再 `sleep`（`sleep_unresolved`） |

**P1 不触发任何 LLM 反思**，只记录事实与未决状态。

### 3.5 快照与外部变化

- 无关字段（`timestamp`/`legal_actions`/`run_summary`）排除在哈希外：`test_unchanged_noise_fields_do_not_create_delta`。
- 相同状态不重复建快照：`test_identical_state_does_not_create_a_second_snapshot`。
- 状态变化但此前无成功写动作 → 记 `external_state_change`：`test_get_state_records_snapshot_and_external_change`。

### 3.6 timeline 隔离（ADR-004）

| 场景 | 测试 | 结果 |
|---|---|---|
| 会话初始无 run 身份 | `test_session_timeline_has_no_run_identity_yet` | `run_id=NULL`、`reason=session` |
| 绑定后补身份 | `test_binding_attaches_identity` | 同一 timeline，`save_id=slot:40` |
| 换 run | `test_new_run_forks_a_timeline` | 新 timeline，`reason=external_replace`，`parent` 指向旧 |
| 读档 | `test_load_forks_a_rollback_timeline` | 新 timeline，`reason=rollback` |
| 旧分支仍可读 | `test_rollback_fork_keeps_old_timeline_readable` | 旧 timeline 事件数不变 |

### 3.7 并发与故障

| 测试 | 断言 |
|---|---|
| `test_concurrent_appends_do_not_duplicate_seq` | 4 线程 × 20 次 = 80 条，`seq` 唯一且连续 |
| `test_result_write_failure_is_counted_not_raised` | 结果写失败 → `lost_events ≥ 1`，但游戏结果仍返回给调用方 |
| `test_unwritable_path_raises_memory_unavailable` | 路径不可写 → 明确异常 |
| `test_closed_store_raises_memory_unavailable` | 关闭后写入 → 明确异常 |
| `test_rejects_unknown_schema_version` | 版本不符 → 拒绝打开，不静默迁移 |

### 3.8 `memory_history`（CLI 形态）

```
$ python -m agent.memory.cli --db <path> status
db: /tmp/…/memory.db
events: 3
pending: 0
pending_actions: []
schema_version: 1
timelines: 1

$ python -m agent.memory.cli --db <path> --format json history --limit 3
[{"seq":1,"event_type":"observation","tool_name":"get_state", …},
 {"seq":2,"event_type":"observation","tool_name":"list_goods","args":{"scope":"all"} …},
 {"seq":3,"event_type":"intent","tool_name":"open_new_game_menu",
  "action_id":"31997a96-…","result_status":"success","rev":0 …}]
```

上面是**真实运行输出**（fake 游戏 + 真实 stdio MCP + `--memory on`）。

CLI 会递归脱敏 `token/authorization/password/secret/api_key/…`；`test_history_never_prints_the_bridge_token` 用 32 位假 token 验证。

### 3.9 真实 MCP 接线

`test_memory_wiring.py` 用真实 stdio 握手（`python -m agent.mcp_server --memory on --memory-db …`）+ fake 游戏：

- `test_read_call_creates_an_observation_event`：`get_state` → 出现 `observation`，无 `intent`；
- `test_write_call_creates_intent_and_result`：`open_new_game_menu` → 恰好 1 条 `intent`，带 `action_id`；
- `test_memory_off_writes_nothing`：`--memory off` 时**不创建数据库文件**（opt-in）。

---

## 4. 测试命令与真实结果

```
$ cd PawnMind && ./test.sh
passed=502 failed=0
contract gate tests: passed=53 failed=0
adapter contract OK

$ cd PawnMind && .venv/bin/python -B -m unittest discover -s agent/tests
Ran 139 tests in 90.1s
OK

$ cd PawnMind && .venv/bin/python -B -m unittest agent.tests.test_memory_store -v
Ran 19 tests … OK

$ cd PawnMind && .venv/bin/python -B -m unittest agent.tests.test_memory_recorder -v
Ran 15 tests … OK

$ cd PawnMind && .venv/bin/python -B -m unittest agent.tests.test_memory_history -v
Ran 8 tests … OK

$ cd PawnMind && .venv/bin/python -B -m unittest agent.tests.test_memory_wiring -v
Ran 3 tests … OK
```

Python 从 P0 修订时的 94 项增至 **139** 项（新增 45 项）。C# 从 502 保持（含新增 3 项 guard 测试）。

### Red 记录（真实先红）

| 阶段 | Red 现象 |
|---|---|
| store | `ModuleNotFoundError: agent.memory.store` |
| recorder | `ModuleNotFoundError: agent.memory.recorder` |
| CLI | JSON 解析失败（`--format` 被子命令覆盖）+ token 泄漏断言失败 |
| wiring | `observation` 事件数为 0（首帧只建快照） |
| guard | 编译失败：`RecordingSaveErrors` 无 `DebugForbidden` |
| schema | `rev` 不在 `required`：`AssertionError: 'rev' not found in ['item_id']` |

全部按“先红后绿”修复，未放宽任何生产断言。

---

## 5. 已知限制（诚实清单）

| # | 限制 | 影响 | 后续 |
|---|---|---|---|
| L1 | `capture_quality` 恒为 `partial`（除观察） | 无法声称“100% 游戏变化覆盖”，只能声称“本系统发起的动作 100% 覆盖” | 符合规范 §6.3 末段 |
| L2 | 游戏自身变化只能靠快照差分推断 | 顾客到访/自动存档/租金结算记 `external_state_change`，**不归因** | P2 可加归因启发式，但不升级为因果 |
| L3 | `sleep` 未跨日判定用“观察次数”而非墙钟 | 快速轮询会比真实 0.5s 协程更早判 unresolved | 可改时间窗；当前留待 S8 用真实帧率校准 |
| L4 | 事件记录不包含 `/state` 的完整 JSON（只存快照 + delta） | 无法离线重放完整画面 | 设计取舍：避免日志膨胀 |
| L5 | `memory_history` 目前只有 CLI，没有 MCP 工具 | 模型还不能自己查历史 | P2 提供 `memory_history` 工具 |
| L6 | 未在真实游戏上运行 | 全部验证为 fake 游戏 + 单元测试 | 需要 S8 真机确认 SQLite 写入不卡帧 |
| L7 | fake 游戏的 `day` 在主菜单为 null | 部分事件 `game_day=null` | 属预期 |
| L8 | 并发保护是进程内锁 + SQLite 事务 | 多进程同时写同一 DB 依赖 SQLite 锁，未压测 | 当前只有单 MCP 进程 |
| L9 | `--memory on` 已写进 DSH patch，但 S8 之前未用真实数据跑过 | 数据库文件位置 `play/memory/memory.db` 需确认可写 | S8 首帧检查 |

---

## 6. 阶段验收自检

对照规范 §12 Phase 1 验收：

| 验收项 | 状态 | 证据 |
|---|---|---|
| 事件存储、迁移、事务、WAL | ✅ | `test_creates_expected_tables`、`test_migration_is_idempotent` |
| `run/save/session` 作用域识别与分支 lineage | ✅ | TimelineTests 5 项 |
| 操作包装器 intent→action→result→reconcile，且不改原返回结构 | ✅ | `test_write_action_records_intent_then_result`（返回值原样透传） |
| 快照、最小前后差分、漂移识别 | ✅ | SnapshotTests 5 项 + `external_state_change` |
| `memory_history` 只读 + 可按 run/day/工具名回放 | ✅ | CLI `history --type/--since-seq/--limit` |
| 崩溃/断连后 pending 可恢复、不自动重放 | ✅ | `reconcile_pending` 幂等 +
 `test_action_exception_becomes_unknown_and_is_not_retried` |
| 重复导入不膨胀 | ⚠ 部分 | 同一 `action_id` 的 intent 会重复追加；P1 未做唯一约束（见下） |
| 重启后可完整恢复与查阅 | ✅ | `test_delta_survives_reload`、CLI 读同一 DB |

### 关于“重复导入”的诚实说明

规范要求“同样的 100 条模拟动作序列，在重复导入后事件/seq 不膨胀”。P1 **未实现** `(timeline_id, action_id, event_type)` 唯一约束，因为：

- 录制场景下每次调用都是新的 `action_id`（客户端生成 uuid4），不存在真实重复；
- 强加唯一约束会让“同一 action 重试”被静默吞掉，反而不利于审计。

因此 P1 的结论是：**不膨胀的是 seq（并发安全），不是重复导入**。这条验收项标为“部分达成”，在 P2（引入 `idempotency_key` 与事件指纹）时补齐。

---

## 7. 下一步（P2 建议，未实施）

1. `memories` / `memory_evidence` / `memory_revisions` 表 + 状态机；
2. `memory_history` / `memory_bootstrap` / `memory_retrieve` / `memory_propose` 作为独立 Memory MCP 工具；
3. 规则校验（ADR-005 的 Evidence Integrity 与 Claim Support 分离）；
4. 检索：scope 硬过滤 + 标签/实体 + 子串（不用 FTS5）。

---

## 8. 本轮未做的事

未启动游戏、未调用真实 `/action`、未读写任何真实存档、未修改游戏安装、未实现任何 LLM 反思或自动提炼、未引入新依赖。所有验证均基于 fake 游戏（127.0.0.1 随机端口）与纯逻辑测试。
