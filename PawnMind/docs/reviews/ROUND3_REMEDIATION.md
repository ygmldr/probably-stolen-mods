# ROUND3_REMEDIATION — 对第二轮复审（R3-01…R3-05）的整改

- **输入**：外部复审 R3-01…R3-05（2026-11-17）
- **范围**：R3-01/02/03 代码整改 + S1（D0a）实现；R3-04/05 体现为策略结构与文档
- **结论**：**离线 PASS；实机全部 `NOT_RUN`**。`play` 模式仍不可启用，未录制、未碰存档。
- **基线**：整改前 `2bfdc30` / 报告 `236844c`；整改后见提交记录

---

## 1. R3-01 Bootstrap 与 `get_state` 矛盾 —— `ACCEPT`，已修

**问题**：文字说 bootstrap 不含 `get_state`，逐工具表却标 `Y`。

**决定**：bootstrap **不允许**任何快照读取（`get_state`、`get_run_summary`、`get_legal_actions`、
`wait_for_change`、`wait_for_events`、`wait_until`）。

**程序强制**（不是文字约定）：

```python
# agent/tool_policy.py
SNAPSHOT_READERS = frozenset({...})
# validate(): "bootstrap must not read the state snapshot" 会拒绝该组合
```

**测试**：

- `test_bootstrap_never_reads_the_snapshot`：`bootstrap ∩ SNAPSHOT_READERS == ∅`，且 `get_state.modes.bootstrap == False`
- `validate()` 在两者同时为真时返回错误（CI 会用 `validate()`）

**说明**：主菜单阶段观察用 `get_new_game_options` / `get_recording_save_status`，不读 `/state`。

---

## 2. R3-02 快照读取别名 —— `ACCEPT`，已修并加漂移测试

**核实**：`mcp_server.py` 中经 `client.get_state()` / `client.wait_events()` 读取快照的工具共 6 个：

```
get_state, get_run_summary, get_legal_actions, wait_for_change, wait_for_events, wait_until
```

`wait_for_events` 走 `GET /events`，而 `/events` 在 `BridgeServer.HandleEvents`（`:755-765`）
同样调用 `_stateProvider()` = `StateSnapshot.BuildJson()`，所以它也是快照读者。

**处置**：

1. 新增常量 `SNAPSHOT_READERS`（6 个）；
2. `test_snapshot_reader_aliases_match_the_server_source` 逐行解析 `@tool` 函数体，
   断言集合完全相等 → **新增别名会让测试失败**（`PP-READ-04`）；
3. D0a 修复作用在 `StateSnapshot` 层，因此 6 个入口一并覆盖，不是只修 `get_state`。

---

## 3. R3-03 校验顺序 —— `ACCEPT`，已实现"预快照 + 最终转发"双层

**问题**：`DispatchAction` 在 `TryRefreshRevision()` 之后执行，只在 dispatch 里检查无法保证零副作用。

**已实现**：

| 层 | 位置 | 作用 |
|---|---|---|
| 预快照授权 | `ActionRouter.Handle`，**在缓存探测与 `TryRefreshRevision` 之前** | 拒绝时根本不构建快照 |
| 最终内部转发 | `PawnMindMod.DispatchAction` 顶部（`act` 内层也会经过） | 防止 `act` 重新派发绕过外层策略 |

两者都调用同一个 `Core.ModePolicy.Refusal(op)`；策略来源一致。

**测试**：

- `ModePolicyTests`（4 条）：test 全放行、play 未知拒绝、bootstrap 更窄、重配替换。
- `ActionRouterTests.Mode_Refusal_Happens_Before_Any_State_Refresh`：拒绝 `save_game` 时
  `refreshRevision` 调用计数为 **0**。
- `ActionRouterTests.Mode_Refusal_Also_Covers_Reads`：`bargain_preview` 同样在 0 次刷新时被拒。

**未完成**：`ModePolicy` 尚未接 `bridge.conf`（当前默认 `Test`，行为与改造前一致）；
配置接线与 `PP-MODE-01` 的 HTTP 负向测试留在 S2。

---

## 4. R3-04 候选白名单 ≠ 实机放行 —— `ACCEPT`，已拆成两层

```python
candidate_allowlist(policy)  # 静态允许，73 个，仅用于报告
play_allowlist(policy)       # 静态允许 且 live_evidence 非空 → 当前 0 个
```

策略 schema 2 每个工具都有 `live_evidence: []` 与 `offline_tests: []`。
`test_live_ready_is_empty_until_real_machine_runs` 同时断言两件事：
`play_allowlist == []`、`candidate_allowlist` 非空（拒绝把"空"当成"就绪"）。

---

## 5. R3-05 证据链 —— `ACCEPT`，结构已具备，内容仍缺

每个工具条目现在包含：

| 字段 | 含义 |
|---|---|
| `status` | `fixed` / `d0a_fixed` / `open` / `partial` / `unverified` / `d0a_pending` |
| `reason` | 直接原因（人类可读） |
| `offline_tests` | 关联的离线用例名 |
| `live_evidence` | 真机证据（当前**全部为空**） |
| `purity` / `decision` / `modes` | 见 STRICT_ALLOWLIST_DECISIONS |

**诚实边界**：静态守卫修复（`3d85963`）**不等于**玩家语义验证；因此 `fixed` 只支撑
`candidate_allowlist`，不支撑 `play_allowlist`。

---

## 6. S1 / D0a 实现（本轮的实质代码改动）

**改动**

| 文件 | 改动 |
|---|---|
| `src/Core/SnapshotRules.cs` | 新增：`ImpureKeys`（9 个 Refresh 支撑的键）、`Bargain(open)`（唯一允许的 bargain 结构）、`Violations(keys)` |
| `src/Game/GameState.cs` | `GetBargainSnapshot()` 只返回 `{open}`；删除 `can_report` 与 `SafeCanReport` |
| `src/Bridge/StateSnapshot.cs` | BuildFlags 只读 `bargain.open`，不再读 4 个 `can_*` |
| `src/Core/LegalAction.cs` | 面板打开时提供 4 个 bargain 方向（由 `bargain` 动作裁决 `unavailable`）；移除 `save_game`（玩家不可达） |

**为什么这样能证明纯度**：快照路径上**不再存在**调用 `CanOffer*` / `CanReport` 的代码，
而 `SnapshotRules.ImpureKeys` 列出所有必须禁止的键；`SnapshotRulesTests` 断言
`Bargain()` 只产出 `open`，且对 9 个禁用键逐一判违规。

**未能证明的**：无法在离线环境计量 `BargainUIManager.Refresh()` 的真实调用次数
（需要 Unity 运行时插桩）→ 因此 `PP-READ-01/02` 仍是 `NOT_RUN`，`get_state`
只是"静态 d0a_fixed"，不是"实机已验证"。

---

## 7. 测试结果

```
$ cd PawnMind && .venv/bin/python -B -m unittest discover -s agent/tests
Ran 248 tests in 132.382s
OK

$ cd PawnMind && ./test.sh
passed=666 failed=0
contract checks: 491
adapter contract OK
contract gate tests: passed=53
```

| 用例 | 层 | 结果 |
|---|---|---|
| `SnapshotRulesTests`（3） | C# 离线 | **PASS** |
| `ModePolicyTests`（4） | C# 离线 | **PASS** |
| `ActionRouterTests.Mode_Refusal_*`（2） | C# 离线 | **PASS** |
| `LegalActionTests`（契约变更） | C# 离线 | **PASS** |
| `test_tool_policy.py`（14） | Python 离线 | **PASS**（含 `PP-READ-04`） |
| `PP-READ-01/02` | 实机 | **NOT_RUN** |
| `PP-MODE-01/02` | HTTP/实机 | **NOT_RUN** |
| `PP-TRADE-*`、`PP-ASYNC-*`、`P0-*`、`PP-RECORD-01`、`PP-MODSCAN-01` | 实机 | **NOT_RUN** |

---

## 8. 未解决 / 下一步

1. **`get_state` 实机纯度**：需要插桩或 UI 探针证明 `Refresh()` 计数为 0（`PP-READ-01`）。
2. **`ModePolicy` 未接配置**：默认 `Test`，需 `bridge.conf` 的 `run_mode` + 生成 allowlist。
3. **`act` 的完整绕过测试**：服务端已双层校验，但缺 HTTP 级负向用例（`PP-MODE-01`）。
4. **二进制不一致**：`dist` 与已安装 DLL 仍不同，重装后才能做任何真机结论。
5. **R3-04 的完整集合**：需先定义"首周主路径工具集"，再对该集合逐项要求 live 证据。

**唯一推荐起点**：`PP-READ-01`（实机插桩）+ 重装核对 `/health`，两者完成后才谈 S3。
