# NIGHTLY_B_SNAPSHOT_REVIEW — B 模块（快照纯度与 LegalAction 语义）

Status: **APPROVE**
Reviewing commit(s): B 本体 `6ab33339f963e28a81d177546e7b44a3374ff4c9`；
审阅发现的 B-1（legal_actions 缺 availability）已由修复提交 `7548ddb23a8fb9a0ea10a08740e2c82352891b07` 在代码侧修复；
文档收尾 `f7ae62c117995dffb43a56da70ddefd6254ccb5b`；**最终 HEAD `f7ae62c`**（审阅基准：最终提交，工作树除
`docs/reviews/` 外干净）。
Change scope: `src/Bridge/StateSnapshot.cs`、`src/Core/ClientSnapshotProjection.cs`、
`src/Core/LegalAction.cs`、`src/Core/SnapshotFieldMap.cs`、`src/Core/SnapshotRules.cs`、
`src/Core/PolicyCatalog.g.cs`、`agent/mcp_server.py`、`agent/debug_bundle.py`、`agent/fake_game.py`、
`agent/policy/tool_policy.json`、`test/PawnMind.Tests/{SnapshotPurityTests,LegalActionTests}.cs`、
`agent/tests/test_snapshot_compat.py`、`docs/audit/NIGHTLY_SNAPSHOT_CALLGRAPH.md`、
`docs/audit/STRICT_ALLOWLIST_DECISIONS.md`。

## Real evidence（本 Agent 亲自运行）

```text
$ ./test.sh PP_READ                # B 模块相关过滤运行（最终提交 f7ae62c）
passed=14 failed=0

$ ./test.sh                        # 最终提交 f7ae62c
passed=750 failed=0
contract gate tests: passed=53 failed=0
contract checks: 503
adapter contract OK                # 日志 /tmp/nightly_review_testsh_committed.log

$ .venv/bin/python -B -m unittest discover -s agent/tests   # 含 test_snapshot_compat.py 9 条
Ran 365 tests in 56.446s
OK                                 # 日志 /tmp/nightly_review_pytests_committed.log

$ .venv/bin/python tools/gen_policy_catalog.py --check
ok: ... PolicyCatalog.g.cs matches tool_policy.json (sha256 e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc)
```

**漂移测试灵敏度（亲自实验，不是静态推理）**：把仓库复制到 `/tmp/nr-copy`（未动仓库），在
`src/Bridge/StateSnapshot.cs` 的 `state["in_game"] = inGame;` 后注入一行
`state["drift_test_unregistered_key"] = true;`，再跑：

```text
$ ./test.sh PP_READ_03_Emitted_State_Keys_Are_Registered
FAIL SnapshotPurityTests.PP_READ_03_Emitted_State_Keys_Are_Registered
     AssertionException: unregistered state keys: drift_test_unregistered_key (expected=0, actual=1)
passed=0 failed=1
```

→ 漂移测试确实会因“新增未登记快照键”失败（不是空跑）；仓库本身未修改。

**其他亲自核对（读 diff/源码 + grep）**：

```text
$ git show 6ab3333 -- src/Bridge/StateSnapshot.cs src/Core/ClientSnapshotProjection.cs src/Core/LegalAction.cs
$ grep -rn "can_report|CanReport" src/ agent/ | grep -v _decomp  # 生产代码 0 命中；仅 docs 历史说明
$ grep -n "_client\." src/Bridge/StateSnapshot.cs                # 仅 11 个已登记 StoreClient 字段
$ grep -n "state\[\"candidate_actions\"\]|DescribeLegalActions" src/Bridge/StateSnapshot.cs
$ grep -n "Availability|IsAvailable|AsCandidate" src/Core/LegalAction.cs
```

## Invariants checked（逐条 PASS/FAIL/PARTIAL）

| 不变量 | 结论 | 依据 |
|---|---|---|
| `client.can_report` 从快照删除（消除 `CanReport→Refresh()` 纯读副作用） | **PASS（L0-static）** | `StateSnapshot.DescribeClient` 经 `ClientSnapshotProjection` 只投影 11 个纯字段；生产代码无 `CanReport`；`PP_READ_01_Fake_Projection_Omits_Can_Report_*`、`Snapshot_Emits_No_Impure_Key` |
| 快照构建不得调用已知 impure 入口（`CanReport`/`CanOffer*`/`Refresh`/`RNG.Roll`/…） | **PASS（L0-static）** | `ForbiddenTokens` 扫描（先剥离 `//` 注释）扫描 `StateSnapshot.cs`、`GameState.cs`，命中即失败；`PP_READ_03_Forbidden_Calls_Absent_From_Snapshot_Build` |
| `GameState.*` 调用全部登记且不得为 impure | **PASS** | `PP_READ_03_Every_GameState_Call_Is_Registered`（还断言扫描到 ≥20 个真实调用，防扫描器空跑） |
| `state` / `client` / `run_summary` / `item` / `legal_action` 每个输出键都登记 | **PASS** | 5 组 `SnapshotPurityTests`；本 Agent 用注入实验证明“未登记键会红” |
| 未知 ≠ pure（prompt/report/calendar/tutorial/item value/dialogue 等） | **PASS（诚实分级）** | `SnapshotFieldMap` 对这些标 `Unknown`；callgraph §5/§8 明确列出 UNKNOWN 与理由 |
| `legal_actions` 只含“原生 guard 已接受”的动作，bargain 四向不冒充 legal | **PASS** | `LegalAction.AsCandidate("native_guard")`；`LegalActionBuilder.Available/Candidates`；`Bargain_Directions_Are_Candidates_Not_Legal` 断言 4 个 candidate、available 中 0 个 bargain |
| `candidate_actions` 序列化 `availability="unknown"` + `requires="native_guard"` | **PASS** | `DescribeLegalActions(..., candidates:true)`；`ReportedAvailability` |
| Python `get_legal_actions` 返回两段并对旧快照稳定降级（无 KeyError） | **PASS** | `_legal_action_payload` 容错；`test_snapshot_compat.py` 8 条（legacy 缺字段/None/畸形类型/非 dict） |
| 旧 `client.can_report` 消费者不崩 | **PASS** | debug_bundle 改用 `reported`；Python 消费者不读 `can_report`；`test_legacy_client_without_can_report_does_not_raise` |
| 快照读取别名（6 个）与策略目录一致，`get_operation` 不属于 snapshot reader | **PASS** | `PP_READ_04_Snapshot_Reader_Aliases_Match_The_Policy_Catalog`；`PolicyCatalog.SnapshotReaders` 6 项；`IsSnapshotReader("get_operation")==false` |
| stale `rev` 拒绝后无世界副作用 | **PASS（L1-fake）** | `PP_READ_02_Stale_Rev_Does_Not_Dispatch_Or_Mutate`：dispatch=0、mutation=0 |
| 顾客切换的纯读不会触发 impure 探针 | **PASS（L1-fake）** | `PP_READ_07_Fake_Client_Switch_Does_Not_Reset_Bargain_State` |
| UNKNOWN 项没有被写成 PASS | **PASS** | callgraph §8 逐条 NOT_RUN/UNKNOWN；测试等级标注 L0/L1，未冒充 L3 |

## Critical findings

无 Critical / High。

### B-1（Low，**已在 `7548ddb` 修复**）`legal_actions` 行缺 `availability` 字段

- **原缺陷**（`63ae0ef`/`6ab3333` 时点）：`DescribeLegalActions` 只在 `candidates==true` 时写
  `availability` / `requires`；`legal_actions`（available 行）只有 `action`/`args`/`hint`，与
  `SKILL.md:69,82,95,438`、`BENCHMARK_SPEC` §11 的 “`availability="available"`” 不一致；
  `agent/fake_game.py` 的 `_legal_actions()` 同样没有该字段，`test_snapshot_compat.py` 只钉了 candidate 的
  availability。
- **修复（`7548ddb`）**：`StateSnapshot.DescribeLegalActions` 对**所有行**输出
  `row["availability"] = action.ReportedAvailability`（legal = `"available"`；candidate = `"unknown"`），
  candidate 另带 `requires`；`fake_game` 的 legal 行同步 `availability:"available"`；
  `test_snapshot_compat.py` 新增 `test_fake_legal_rows_carry_availability_available`（9 条测试）。
- **复核**：本 Agent 读修复 diff 确认；`test_snapshot_compat.py` 单独跑 `Ran 9 tests ... OK`；
  全量 Python 365 OK；该字段是向后兼容的额外字段（不改变安全性，也不影响旧消费者）。
- **残留**：无阻塞项。Python `_legal_action_payload` 原样透传，不需要补字段（C# 已输出）。

### B-2（Low，测试扫描器局限，记录非阻塞）

- `SnapshotPurityTests` 是**源码正则扫描**：`GameState\.([A-Za-z_]\w*)`、`variable\["key"\]\s*=`。
  跨行调用（`GameState.\nGetDay()`）、或经 helper 间接调用（`ReportService.State` → adapter → 原生）
  不会被登记检查；后者只能靠 callgraph 的人工 UNKNOWN 清单兜底（已如实列出）。
- `ForbiddenTokenHits` 只剥离 `//` 行注释；块注释中含 `Refresh(` 会误报（安全方向、非漏报），
  `CanReport (` 中插空格则会漏报（构造性规避才会触发，正常代码不会）。
- 建议下一批次把“间接调用”用 AST/调用链 smoke test 或声明式服务调用表补强；本次不构成阻塞。

## Potential regressions

1. 旧消费者若依赖 `legal_actions` 含 bargain 四项（例如外部脚本/旧版 demo agent）会少看到它们；现在必须
   读 `candidate_actions`。仓库内 Python/MCP/skill 已同步，外部消费者（用户自写脚本）需一次性迁移说明；
   E 的 SKILL 已写清区别。
2. `client` 段少了 `can_report` 字段；旧脚本若 `state["client"]["can_report"]` 取值会 KeyError。Python 侧
   已容错，但外部脚本无兼容层。删除原因是安全（纯读副作用），属有意破坏性变更，应在 release note 写明。
3. `SnapshotFieldMap` 把 `prompt/report/calendar_next/tutorial_hints` 标 Unknown 但仍参与快照；若真机发现
   它们有副作用，需要再迁移（callgraph §8 已登记）。
4. `value` 字段走的 `GetNegociatedValue()` 在 debug 价格面板打开时会写 UI 文本（UNKNOWN）；正常游玩看不到，
   但若未来默认打开 debug 面板，纯读会有 UI 副作用。

## NOT_RUN / UNKNOWN

- **L3-real-game 全部 NOT_RUN**：Unity 运行时 `Refresh()` 调用计数（PP-READ-01-real）需要插桩构建 + 游戏进程；
  未启动游戏/未读写存档。
- **UNKNOWN**（callgraph §8 原文）：prompt/report/calendar/tutorial 四个适配器读链、`GetNegociatedValue`
  的 debug 面板条件写、`Dialogue.IsDisabled` 注入谓词闭包、`CanSellThisItem` 完整谓词链、顾客实例身份稳定性。
- **UNKNOWN**：`get_legal_actions` 在真实谈价面板下的四项实际可交易性——不能在无游戏环境验证；
  代码只保证“不把 candidate 说成 legal”。

## Reviewer independence

本文件作者为独立审阅 Agent `nightly-reviewer`（fresh spawn 会话，未参与实现；实现者为 Lead）。
未参与 B 模块代码/测试/文档的编写，未继承实现者会话。审阅仅读生产代码、跑测试与在 `/tmp` 副本做一次
漂移灵敏度实验（仓库未改）；唯一写入为 `docs/reviews/`。审阅期间工作树被他方并发修改（E/G 字段同步、
Lead 收尾与 `7548ddb` 修复），本文件最终按 **HEAD `f7ae62c`** 复核；`src/` 的 B 相关代码在 `7548ddb` 中仅
按 B-1 建议增加了 legal 行 `availability` 输出（已复核 diff），其余不变。所引数字均来自本会话亲自运行
（日志路径见上）。
