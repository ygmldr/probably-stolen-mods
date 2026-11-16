# NIGHTLY_D_OPERATION_REVIEW — D 模块（ActionGate 两轴 / Operation / 异步与存档回执）

Status: **APPROVE**
（本审阅首次复核发现 1 个 Medium 缺陷 D-1，Lead 已在工作树修复并补回归测试；本文件按**修复后**最终工作树复核通过。
剩余项为已声明的 PARTIAL/UNKNOWN 设计边界，不阻塞。）
Reviewing commit(s): D 本体 `f09314e3f5e53df43c6b8d27bd7796a8dc235844`（D+C 合并提交的 D 部分）；
D-1 修复 `7548ddb23a8fb9a0ea10a08740e2c82352891b07`；文档收尾 `f7ae62c117995dffb43a56da70ddefd6254ccb5b`；
**最终 HEAD `f7ae62c`**（审阅基准：最终提交，工作树除 `docs/reviews/` 外干净）。
Change scope: `src/Core/ActionGate.cs`、`src/Core/OperationStatus.cs`、`src/Core/ShopOperationSemantics.cs`、
`src/Bridge/ActionRouter.cs`、`src/Bridge/BridgeServer.cs`、`src/Bridge/FirstWeekService.cs`、
`src/Bridge/RecordingSaveService.cs`、`src/Bridge/GameActions.cs`（open_shop 守卫抽出）、
`src/Core/EndpointPolicy.cs`、`src/Core/RouteTable.cs`、`src/Core/QueryValues.cs`、`src/PawnMindMod.cs`、
`agent/mcp_server.py`、`agent/pawnmind_client.py`、`agent/policy/tool_policy.json`、
`test/PawnMind.Tests/{OperationTests,OperationEndpointTests,OperationSemanticsTests,ActionRouterTests,BridgeTransportTests,FirstWeekServiceTests,RecordingSaveServiceTests}.cs`、
`agent/tests/test_operation_tools.py`。

## Real evidence（本 Agent 亲自运行）

```text
$ ./test.sh                        # 最终工作树（含 Lead 的 D-1 修复 + 新测试）
passed=750 failed=0
contract gate tests: passed=53 failed=0
contract checks: 503
adapter contract OK                # 日志 /tmp/nightly_review_testsh_final.log

$ ./test.sh Rejected_Operation_Reports_Dispatched_False   # D-1 回归（本 Agent 单独跑）
PASS OperationTests.Rejected_Operation_Reports_Dispatched_False
passed=1 failed=0

$ ./test.sh PP_ASYNC               # 两轴/operation 相关过滤
passed=12 failed=0
$ ./test.sh Operation             # 关键字过滤（首次复核时 37 条；D-1 修复后含新用例）
passed=37 failed=0

$ .venv/bin/python -B -m unittest discover -s agent/tests   # 含 test_operation_tools.py
Ran 365 tests in 56.446s
OK                                 # 最终提交；日志 /tmp/nightly_review_pytests_committed.log

$ $DOTNET build src/PawnMind.csproj -c Release -p:GameManaged=/root/work/game_managed
Build succeeded. 0 Warning(s) 0 Error(s)
```

静态/行为核对（读 diff + 逐条对照测试）：

```text
$ git show f09314e -- src/Core/ActionGate.cs src/Bridge/ActionRouter.cs src/Bridge/BridgeServer.cs \
    src/Core/ShopOperationSemantics.cs src/Bridge/FirstWeekService.cs src/Bridge/RecordingSaveService.cs
$ git diff -- src/Core/ActionGate.cs test/PawnMind.Tests/OperationTests.cs   # D-1 修复 diff（见下）
# 修复后核心代码：entry.Operation.Dispatched = entry.Operation.Execution != ExecutionStatus.Rejected;
$ grep -n "GetOperationJson|VerifyOperation|RouteKind.Operation" src/Bridge/ActionRouter.cs src/Bridge/BridgeServer.cs src/PawnMindMod.cs
$ grep -n "PendingResponse|operation_pending|blocking_operation_id|next" src/Bridge/ActionRouter.cs
```

**D-1 独立复现（修复前，/tmp 副本探针）与修复后复核**：

```text
# 修复前（本 Agent 在 /tmp 副本注入探针）：
PROBE response={"ok":false,"error":"trade_locked","dispatched":false,...,"execution_status":"rejected"}
PROBE execution=rejected dispatched=True ok=False
FAIL ... "rejected op must not claim dispatched (expected=False, actual=True)"

# 修复后（正式工作树，直接跑 Lead 新增的回归测试 + 本 Agent 素质核对）：
PASS OperationTests.Rejected_Operation_Reports_Dispatched_False
```

## Invariants checked（逐条 PASS/FAIL/PARTIAL）

| 不变量 | 结论 | 依据 |
|---|---|---|
| 两轴状态：execution ∈ prepared/running/completed/rejected/failed/uncertain；delivery ∈ pending_delivery/delivered/undelivered | **PASS** | `OperationStatus.cs` 常量 + `IsValid`；`PP_ASYNC_02_Gate_Tracks_Execution_And_Delivery_Axes` |
| `MarkResponseUndelivered` 只改投递轴，不覆盖 completed 证据 | **PASS** | `ActionGate.MarkUndelivered` 只写 Delivery；`Complete` 保留 Response/Execution；`PP_ASYNC_02_Gate_MarkUndelivered_Keeps_Completed_Execution`、`BridgeTransportTests.Undelivered_Write_Keeps_Completed_Evidence_On_Retry`（L2：400 KiB 响应中途断连 → 重试 ok:true + undelivered + redelivered，dispatch 仍 1 次） |
| 超大响应无法投递 → execution 仍 completed 且可经 `/operation` 查询（不再改写 outcome_unknown） | **PASS（L2）** | `Oversized_Action_Response_Is_Recorded_As_Undelivered`：over pending limit → `UndeliveredResponses>=1`、operation `ok=true/completed/undelivered`、无 `outcome_unknown`、dispatch=1 |
| `ok:true` 仅当 `execution_status=completed` | **PASS** | `DecorateResponse` 用 `ExecutionStatus.IsSuccess`；`PP_ASYNC_01_Write_Response_Carries_Both_Axes`；pending → ok:false/running |
| 同 `action_id` 重试返回原证据、不重派发；同 op 新 id → `operation_pending`（含 blocking id + next），无关写不被死锁 | **PASS** | `PP_ASYNC_04_*` 两条；`OperationPending` 载荷 |
| `GET /operation?id=` 只读、需 Bearer、GET-only、不构建快照、不派发 | **PASS（L2）** | `OperationEndpointTests` 7 条：`StateCalls=0`；无 token→401；POST→405；Bootstrap 可查、Locked 403；`GET /operation` 走 `_operationProvider` 而非 action handler |
| 未知/非法 operation id 结构化返回；run 变化清空记录并带 `process_restarted` | **PASS（run 变化）/ PARTIAL（真进程重启见 D-2）** | `PP_ASYNC_05_*`、`PP_RECOVERY_01_Cleared_Operations_Report_A_Process_Restart` |
| `wait_operation` 在 Python 侧有界轮询、不阻塞 Unity 主线程、超时返回 pending 不假成功 | **PASS（L1）** | `poll_operation` + `WAIT_OPERATION_MAX_MS=10000`；`test_operation_tools.py` 9 条（fake clock：terminal/timeout/unknown/fetch-error）；Unity 主线程无任何等待（Python 端循环） |
| `end_day`：仅回读 CLOSED 才 completed；仍 OPEN → running/pending，不 no_effect | **PASS（L1-fake）** | `ShopOperationSemantics.EndDayExecution`（`DayEndPolicy.IsClosed` 严格）；`FirstWeekService.EndDay` 用该分类；`EndDay_Still_Open_After_Dispatch_Is_Pending`；`EndDay_Success_Reports_Readback` |
| `sleep`：`day_changed` 与 `save_evidence` 分离；`last_save_time` 不算落盘证据 | **PASS / PARTIAL（见 D-3）** | `ShopOperationSemantics.Sleep` 永不互相推导；`RecordingSaveService` 传 `SaveUnknown` 且 `row["ok"]=completed 才 true`；`Sleep_Separates_Day_Change_From_Save_Evidence`、`Service_Sleep_Report_Is_Pending_Before_The_Async_Day_Change` |
| 拒绝（未派发）在 operation 记录里也不得声称 `dispatched:true`（D-1） | **PASS（修复后）** | `ActionGate.Complete`：`Dispatched = Execution != Rejected`；新增 `Rejected_Operation_Reports_Dispatched_False`（本 Agent 独立跑 PASS） |
| 运行/时间线变化后旧 op 证据不能错归属；跨进程不承诺 exactly-once、不自动重放 | **PASS（离线语义）** | `ResetOperations()` 清空 + `process_restarted`；`IsRunChangeOp` 覆盖 `new_game/load_game/load_recording_game/confirm_new_game`；真实存档回滚 `NOT_RUN` |
| open_shop 动画锁/非 MORNING 重复调用被拒（P0-OPEN-01） | **PASS（L0）** | `OpenShopPolicy` + `OperationSemanticsTests.P0_OPEN_01_*`；`GameActions.OpenShop` 调用同源 Core 规则 |
| 不可为模拟成功而 force day/settle rent/write save | **PASS（静态）** | `ShopOperationSemantics` 纯分类；无 force/settle 代码路径；未触碰存档 |

## Critical findings

### D-1（Medium，**已在最终工作树修复并由本 Agent 复核**）拒绝操作的 `GET /operation.dispatched` 与响应矛盾

- **原缺陷**：`ActionGate.Complete` 无条件 `entry.Operation.Dispatched = true`；HTTP 响应经
  `DecorateResponse` 对 Rejected 覆写 `dispatched:false`，但缓存记录仍是 true → `get_operation` 返回
  `execution_status:rejected + dispatched:true`，与协议/文档（rejected = 派发前拒绝）矛盾。
- **复现（修复前）**：见 §Real evidence 探针输出；任何 guard 拒绝（如 `trade_locked`）都可触发。
- **影响**：AI/Skill 会把“确定未派发”的拒绝误判为“可能已派发”，导致不必要的 uncertain/人工介入；
  审计轨迹与响应不一致。不产生错误成交。
- **修复与复核**：`entry.Operation.Dispatched = entry.Operation.Execution != ExecutionStatus.Rejected`；
  新增 C# 测试 `Rejected_Operation_Reports_Dispatched_False`（本 Agent 单独跑 PASS，全量 750/0 绿）。
- **残留**：`Classify` 对“已派发但返回裸 `ActionResult.Failure`（无 RawJson）”仍按 `dispatched=false`
  处理（旧保守逻辑）→ operation 记录会 `dispatched=false + execution_status=failed/uncertain`；方向安全
  （不伪造派发），记录为 Low 观察，无需本批次修。

### D-2（Low/文档，**保留，无需代码改动**）`process_restarted` 只表示“进程内缓存被清空/run 变化”

- 语义边界：`process_restarted:true` 由 `_operationsCleared || ActionGate.ClearCount>0` 触发，即
  “本进程内发生过 new_game/load_game 等 run 变化”。真正的 Mod/进程重启后，新 `ActionGate` 是空的，未知 id
  只能返回 `unknown_operation + known:false`（无法区分“重启”与“打错 id”），**不会**带该标志。
- Lead 已在修复说明中确认该语义；SKILL/BENCHMARK 文案应按“run 变化会带；真重启只能表现为 unknown_operation”
  表述，安全方向没问题（不自动重放）。本项无需代码改动。
- 证据：`ActionRouter.GetOperationJson` 分支 + `PP_RECOVERY_01` 只测 run change；静态确认。

## Potential regressions

1. “同 op 存在非终态（含 uncertain）时新 action_id 一律 `operation_pending`”→ uncertain 操作在
   进程/run 存续期间会一直阻塞同 op 新请求（无人工清除/ack 机制），只能靠重启或 run 变化释放。
   对 `confirm_trade` 这是任务书要求（新 id 重复成交必须拒绝），方向安全；但对 `end_day` 之外的 op
   可能造成 workflow 卡死（对话等无关写不受影响，已验证）。需在 Skill/NEXT_LIVE_SESSION 写明“uncertain
   需人工判定后重启/读档恢复”。
2. `operation_pending` 响应不含 `operation_id` 字段（用 `blocking_operation_id` + `next`）；旧消费者若只找
   `operation_id` 会读不到 blocking id。SKILL 已写 `blocking_operation_id`；建议后续统一加别名。
3. `GET /operation` 在 Bootstrap 可用（协议必要），Play 模式因 `PlayOps` 为空而 403（预期）；未来
   play_allowlist 非空时需把 `get_operation` 纳入，否则 Play 下无法查询 operation。
4. `EndDay` 的 verifier（`VerifyOperation`）用 `!DayEndPolicy.IsOpenState` 判定完成：当前 `StoreState`
   枚举只有 CLOSED/OPEN/MORNING（`_decomp/PlayerStore.cs`），因此等价于“CLOSED 或已进入新一天”，
   与派发时严格 `IsClosed` 相比略宽松；语义上“新一天”也是完成。若未来加入过渡态需重新核对。
5. `wait_operation` 超时把 `uncertain` 也统一报成 `status:"pending"`（保留 `execution_status`）；可能让
   调用方误以为“还在跑”。建议 timeout 时若 `execution_status=="uncertain"` 用 `status:"uncertain"`。
   （低风险；SKILL 已要求看 `execution_status`。）

## NOT_RUN / UNKNOWN

- **L3-real-game 全部 NOT_RUN**：Unity 快门 2.75s 动画、睡觉黑屏换天、真实存档文件写入/落盘证据、
  真实 `GET /operation` 在游戏内主线程的表现、`wait_operation` 真实宿主 E2E、真实存档回滚后的 op 归属。
  需要：游戏进程 + 授权存档 + 新构建 DLL；本批次未启动游戏/未读写存档。
- **UNKNOWN**：Game 内 `StateSnapshot.CurrentRevision` 在 `VerifyOperation` 读回时的并发语义（均在主线程，
  静态一致）。
- **NOT_RUN**：DSH 真实 MCP host 的 `wait_operation` 语义（MCP 客户端是否把 `ok:false + status:pending`
  当异常）——需宿主机录制。

## 备注（最终工作树证据）

- 本文件按最终提交 `f7ae62c`（含 D-1 修复提交 `7548ddb`）复核；工作树除 `docs/reviews/` 外干净。
- 本 Agent 最终实测：C# **750 passed / 0 failed**、Python **365 OK**、门禁 53/0、契约 503、policy --check ok。

## Reviewer independence

独立审阅 Agent `nightly-reviewer`（fresh spawn 会话，未参与实现；D 实现者为 Lead）。未写任何 D 代码/测试；
D-1 由本会话先于 Lead 修复独立发现并复现（/tmp 副本探针，仓库未改），修复后本会话用新增 C# 测试与全量
750/0 复核。所有数字均来自本会话亲自运行。与实现者、E/G 作者为不同 agent/会话，非 self-review。
工作树在审阅期间持续被 Lead 修复/E/G 字段同步修改；本文件以最终工作树（见 §备注）为准。
