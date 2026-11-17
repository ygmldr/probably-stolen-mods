# NIGHTLY_TEST_MATRIX — Nightly Batch A 回归项状态（最终）

> 规则：**空白 ≠ PASS**。没有真实运行记录的一律 `NOT_RUN`。
> 层级：`L0-static`（源码扫描/纯逻辑）· `L1-fake`（假实现/假端口）· `L2-integration-no-game`（真 loopback socket，无游戏）· `L3-real-game`（需要游戏进程，本批次一律 `NOT_RUN`）。
> 采集：2026-11-17 05:0x，build host；全量命令见 §复现。
> 本表只记录**本批次实际运行过**的结果；fake 证据不计入 live evidence。

## 全量数字

| 套件 | 结果 |
|---|---|
| C# 纯逻辑 | **750 passed / 0 failed** |
| 契约门禁（脚本级） | **53 passed / 0 failed** |
| 适配器契约 | **503 checks OK**（491 + C 模块 12） |
| Python agent | **365 OK** |
| 策略生成物 | ok（sha256 `e0da5a24…`） |
| Unity 目标编译 gate | **PASS**（`dotnet build` Build succeeded） |

## A — 模式策略（提交 `63ae0ef`，本次补审阅）

| ID | 层级 | 覆盖用例 | 结果 | 证据 |
|---|---|---|---|---|
| `PP-MODE-01a` | L0 | `RunModeTests.Missing_Run_Mode_Fails_Closed` / `Invalid_Run_Mode_Fails_Closed` / `BridgeConfig_Parses_And_Roundtrips_The_Mode` | **PASS** | 缺失/空白/非法 → `Locked` + 稳定错误码 |
| `PP-MODE-01b` | L0+L2 | `RunModeTests.Bootstrap_Plan_Excludes_Snapshot_Readers`、`EndpointGateTests.Bootstrap_State_Is_Refused_Before_The_Provider_Runs` / `Bootstrap_Events_Is_Refused_Before_The_Provider_Runs`、`test_tool_policy.test_bootstrap_never_reads_the_snapshot` | **PASS** | 6 别名被排除；裸 `/state` `/events` 403 且 provider 计数 0 |
| `PP-MODE-01c` | L0 | `ActionRouterTests.Mode_Refusal_*`、`RunModeTests.Play_Mode_Stays_Closed_Without_Live_Evidence` | **PASS** | Play 允许集为空；`refreshRevision` 计数 0 |
| `PP-MODE-01d` | L0 | `RunModeTests.Test_Mode_Requires_Explicit_Acknowledgement` / `Acknowledged_Test_Mode_Is_Permissive` | **PASS** | 未配置 ≠ Test |
| `PP-MODE-02a` | L0+L1 | `ModePolicyTests.Play_Mode_Denies_Unknown_Operations`、`test_client.test_mismatched_policy_refuses_without_sending` | **PARTIAL** | 未知 op 拒绝成立；C# 只能报告自身哈希，**不能检测**外部漂移 |
| `PP-MODE-02b` | L1 | `OperationTests.PP_MODE_02b_Mode_Change_Never_Redispatches_A_Cached_Action` | **PASS（本批次新增）** | 模式收紧后旧 `action_id` 被拒且 dispatch 不增加，已记录 completed 证据不被改写 |
| `PP-MODE-02c` | L2 | `EndpointGateTests.Locked_Refuses_State_And_Writes` / `Test_Mode_Keeps_The_Endpoints_Available`、`OperationEndpointTests.Locked_Mode_Refuses_Operation_Queries` | **PASS** | 裸 HTTP 与 MCP 同门禁；拒绝先于 provider/dispatch |
| `PP-MODE-02d` | L0 | `test_tool_policy`（纯度来自生成策略，不读 `readOnlyHint`） | **PARTIAL** | 无“把 hint 改 true 也不放宽”的负向用例（策略文件本身不用 hint） |

## B — 快照纯度 / LegalAction（提交 `6ab3333`）

| ID | 层级 | 覆盖用例 | 结果 | 证据 |
|---|---|---|---|---|
| `PP-READ-01-fake` | L1 | `SnapshotPurityTests.PP_READ_01_Fake_Ten_Client_Projections_Never_Probe_Impure` / `PP_READ_01_Fake_Projection_Omits_Can_Report_And_Reports_Pure_Fields` | **PASS** | 10 次投影 0 次 impure 探针；无 `can_report`，有 `reported`/`is_wanted` |
| `PP-READ-01-real` | L3 | 需要 Unity 运行时 `Refresh()` 计数插桩 | **NOT_RUN** | 见 `NEXT_LIVE_SESSION.md` §4 |
| `PP-READ-02-fake` | L1 | `SnapshotPurityTests.PP_READ_02_Stale_Rev_Does_Not_Dispatch_Or_Mutate` | **PASS** | stale_rev：dispatch=0、world mutation=0、快照构建=1 |
| `PP-READ-03` | L0 | `SnapshotPurityTests.PP_READ_03_*`（9 条：禁用 token / GameState 调用登记 / state 键 / client 段 / 适配器成员 / run_summary+legal / item 键 / service 调用 / 不得 emit impure 键） | **PASS** | 字段源全树见 `docs/audit/NIGHTLY_SNAPSHOT_CALLGRAPH.md`；未知不默认 pure |
| `PP-READ-04` | L0+L1 | `SnapshotPurityTests.PP_READ_04_Snapshot_Reader_Aliases_Match_The_Policy_Catalog`、`test_tool_policy.test_snapshot_reader_aliases_match_the_server_source` | **PASS** | 6 别名 / 生成物 / 源码三方一致；裸 endpoint 由 L2 门禁测试覆盖 |
| `PP-READ-05` | L0 | `LegalActionTests.Bargain_Directions_Are_Candidates_Not_Legal` | **PASS** | bargain 四向 `availability=unknown` / `requires=native_guard`；`legal_actions` 不再含它们 |
| `PP-READ-06` | L1 | `agent/tests/test_snapshot_compat.py`（8 条） | **PASS** | 旧快照缺 `candidate_actions`/`can_report` 时 MCP 与 debug bundle 稳定降级、无 KeyError |
| `PP-READ-07` | L1 | `SnapshotPurityTests.PP_READ_07_Fake_Client_Switch_Does_Not_Reset_Bargain_State` | **PASS（fake）** | 切换顾客 10 次读投影不触发 impure 探针；真实 Unity 仅 `NOT_RUN` |
| 残留 UNKNOWN | L3 | 见 callgraph §8 | **NOT_RUN** | prompt/report/calendar/tutorial 适配器、`GetNegociatedValue` 条件 debug 写、`Dialogue.IsDisabled` 闭包、`CanSellThisItem` 全链 |

## C — 交易守卫与证据（提交 `f09314e`）

| ID | 层级 | 覆盖用例 | 结果 | 证据 |
|---|---|---|---|---|
| `PP-TRADE-01` | L1 | `TradeServiceTests.PP_TRADE_01_Normal_Trade_Completes_Before_The_Dialogue` | **PASS** | business_complete + client_unchanged → completed，1 次原生点击 |
| `PP-TRADE-02` | L1 | `TradeServiceTests.PP_TRADE_02_Same_Action_Id_Never_Repeats_The_Trade` | **PASS** | 同 action_id 重试不重复点击（AcceptCalls 恒 1），不重复预检 |
| `PP-TRADE-03a` | L1 | `PP_TRADE_03a_B10_Trade_Locked_Never_Clicks` | **PASS** | `trade_locked`，AcceptCalls=0 |
| `PP-TRADE-03b` | L1 | `PP_TRADE_03b_B11_Business_Already_Complete_Never_Clicks` | **PASS** | `business_complete` / `intent_undefined`，0 次 |
| `PP-TRADE-03c` | L1 | `PP_TRADE_03c_B12_Staged_Item_And_Direction_Must_Match` | **PASS** | `item_not_staged` / `not_owned`（方向来自原生 staged 归属），0 次 |
| `PP-TRADE-03d` | L1 | `PP_TRADE_03d_B13_Barter_Conditions_Are_Prechecked` | **PASS** | `barter_not_acceptable` / `not_introduced`，0 次 |
| `PP-TRADE-03e` | L1 | `PP_TRADE_03e_B14_Information_Dealer_Cost_Is_Prechecked` | **PASS** | `cannot_afford` / `not_introduced`，0 次 |
| `PP-TRADE-03f` | L1 | `PP_TRADE_03f_B15_Gunsmith_Order_Is_Prechecked` | **PASS** | `gun_order_not_accepted`，0 次 |
| `PP-TRADE-03g` | L1 | `PP_TRADE_03g_B16_Buy_And_Sell_Native_Gates_Are_Prechecked` | **PASS** | `cannot_buy_from_table` / `client_refuses_item`，0 次 |
| `PP-TRADE-04` | L1 | `PP_TRADE_04_Dialogue_Is_A_Separate_Workflow` / `PP_TRADE_04_Uncommitted_Trade_Is_Not_Reported_Completed` | **PASS** | completed 与 uncertain 都不阻塞 `advance_dialogue`；未提交不报 completed |
| `PP-TRADE-05` | L1 | `PP_TRADE_05_Barter_Completes_Without_Cash_Movement` | **PASS** | 现金不变（cash_changed=false）+ items_transferred=true + completed |
| `PP-TRADE-06` | L1 | `PP_TRADE_06_Silent_Native_Return_Is_Uncertain_Not_Ok` | **PASS** | 原生静默 return → uncertain，绝不 ok:true |
| `PP-TRADE-07` | L1 | `PP_TRADE_07_Native_Exception_Is_Uncertain` | **PASS** | 异常 → uncertain（不假失败也不假成功） |
| `PP-TRADE-08` | L1 | `PP_TRADE_08_Another_Client_Cannot_Back_This_Trade` | **PASS** | 顾客身份变化 → `client_changed`，不背书 |
| `PP-TRADE-09` | L1 | `PP_TRADE_09_Ok_Only_For_Completed` | **PASS** | 未调用 → uncertain/ok:false |
| `PP-TRADE-real` | L3 | 真机点击/以物易物/枪单/情报商 | **NOT_RUN** | 需游戏进程；见 NEXT_LIVE_SESSION |

## D — 两轴状态 / Operation / 异步（提交 `f09314e`）

| ID | 层级 | 覆盖用例 | 结果 | 证据 |
|---|---|---|---|---|
| `PP-ASYNC-01` | L1 | `OperationTests.PP_ASYNC_01_Write_Response_Carries_Both_Axes` / `_Pending_Write_Is_Running_Not_Completed` / `_Verifier_Advances_Running_To_Completed` | **PASS** | pending→running→verifier→completed；响应带两轴；ok 仅 completed |
| `PP-ASYNC-02` | L1+L2 | `OperationTests.PP_ASYNC_02_*`（3 条）、`BridgeTransportTests.Undelivered_Write_Keeps_Completed_Evidence_On_Retry` / `Oversized_Action_Response_Is_Recorded_As_Undelivered` | **PASS** | 丢响应只改投递轴；重试返回原完成证据 + `delivery_status:undelivered` + `redelivered` |
| `PP-ASYNC-03` | L1 | `OperationTests.PP_ASYNC_03_Gate_Clear_Forgets_Operations` / `PP_RECOVERY_01_Cleared_Operations_Report_A_Process_Restart` | **PASS** | run 变化清空记录，未知 id 带 `process_restarted`；Python durable intent 侧见 E 的 trace validator |
| `PP-ASYNC-04` | L1 | `OperationTests.PP_ASYNC_04_Pending_Operation_Blocks_The_Same_Op_Not_Other_Writes` / `_Same_Action_Id_Retry_Returns_The_Pending_Operation` | **PASS** | 同 op 新 action_id → `operation_pending`；无关写（对话）不被死锁 |
| `PP-ASYNC-05` | L1 | `OperationTests.PP_ASYNC_05_Unknown_Operation_Is_A_Structured_Read` / `_Bad_Operation_Id_Is_Refused_Without_Touching_State`、`agent/tests/test_operation_tools.py`（9 条） | **PASS** | 未知/bad id 结构化；`wait_operation` 超时 = ok:false + timed_out + status=pending + next，绝不假成功；轮询在 Python 侧 |
| `/operation` 端点 | L2 | `OperationEndpointTests`（7 条） | **PASS** | GET only；需 Bearer；Bootstrap 可查、Locked 403；不建快照（StateCalls=0） |
| `PP-SAVE-01` | L0+L1 | `OperationSemanticsTests.Sleep_Separates_Day_Change_From_Save_Evidence` / `_Sleep_Without_A_Day_Change_Is_Still_Running` / `_Sleep_With_Save_Evidence_Reports_Both_Facts`、`RecordingSaveServiceTests.Service_Sleep_Is_Allowed_Only_When_Bound` / `_Report_Is_Pending_Before_The_Async_Day_Change` | **PASS（离线语义）** | `day_changed` 与 `save_evidence` 分开；`last_save_time` 不算落盘证据 → `save_unknown` |
| `P0-LANDLORD-02-fake` | L0 | `DayEndPolicyTests.Important_Client_Blocks_EndDay_And_Sleep`、`RecordingSaveServiceTests.Service_Sleep_Refuses_While_The_Landlord_Waits` | **PASS** | 房东待接待时 sleep/end_day 均拒，原生 0 次 |
| `P0-OPEN-01-fake` | L0 | `OperationSemanticsTests.P0_OPEN_01_Repeated_Open_While_Shutter_Busy_Is_Refused` / `_Open_Outside_Morning_Is_Refused` | **PASS** | 动画锁/非 MORNING 重复 open_shop 拒绝 |
| `end_day` 完成语义 | L1 | `FirstWeekServiceDayTests.EndDay_Success_Reports_Readback` / `EndDay_Still_Open_After_Dispatch_Is_Pending` | **PASS** | 只有回读 CLOSED 才 completed；仍 OPEN → running/pending |
| `PP-RECOVERY-01` | L1+L3 | `OperationTests.PP_RECOVERY_01_*`（L1）+ 真机存档回滚 | **PARTIAL** | 代码侧清空/不再归属已验证；**真机回滚存档** `NOT_RUN` |
| 真实异步时序 | L3 | 快门动画/黑屏/落盘 | **NOT_RUN** | 需游戏进程 |

## E — 首周 Skill / 离线评测（提交 `299ecd4` + 字段同步）

| ID | 层级 | 覆盖用例 | 结果 | 证据 |
|---|---|---|---|---|
| `PP-SKILL-01` | L0 | `agent/tests/test_skill_tool_refs.py`（**16 条**，AST 校验工具名真实存在） | **PASS** | SKILL 引用工具全部存在于 116 工具注册表及 policy |
| `PP-TRACE-01` | L0 | `agent/tests/test_trace_validator.py`（**77 条**） | **PASS** | 18 类违规码：uncertain 新 id 重试、pending 冲突写、ok≠completed、自述成功、租金回执缺失/矛盾、换天/落盘分离等 |
| Benchmark 规范 | L0 | `docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md` | **PASS（文档）** | 成功判据/失败码/成本/时长/人工介入定义齐全 |
| Benchmark 执行 | L3 | 真机首周跑分 | **NOT_RUN** | 通关率/效率 = `NOT_MEASURED`（不伪造） |

## G — 兼容静态预审（提交 `f789a92` + 复核）

| ID | 层级 | 覆盖 | 结果 | 证据 |
|---|---|---|---|---|
| `PP-COMPAT-01` | L0-static | `docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md`（反射/线程/监听器/P-Invoke/打包 Python/Unity-in-Core/manifest/dist） | **PASS（静态）** | 每条 文件:行号 + 风险 + 替代建议 |
| `PP-MODSCAN-01` | L3 | 官方 `mod-scan` / `ScannerCheck` | **NOT_RUN** | 无游戏进程；命令见矩阵 §10 |
| `PP-BUILD-01` | L0 | 编译 gate（Linux/build host） | **PASS** | `dotnet build src/PawnMind.csproj -c Release -p:GameManaged=<game-managed>` → Build succeeded |
| `PP-INSTALL-01` | L3 | dist 打包 / 安装一致性 | **NOT_RUN** | 只记录不一致；未安装、未覆盖 |

## F — 独立审阅与集成

| 项 | 结果 |
|---|---|
| A/B/C/D/E/G 各自审阅 | 见 `docs/reviews/NIGHTLY_*_REVIEW.md`（独立 reviewer，fresh session） |
| 集成审阅 | `docs/reviews/NIGHTLY_F_INTEGRATION_REVIEW.md` |
| `play_allowlist` | 仍为 **0**（本批次未用任何 fake 证据升级） |

## 复现命令

```bash
cd <repo>/PawnMind
set -a; . .env.server; set +a

./test.sh                                                    # 750 + 门禁 53 + 契约 503
./test.sh PP_READ                                            # B 快照纯度
./test.sh PP_ASYNC                                           # D 两轴/异步
./test.sh PP_TRADE                                           # C 交易守卫
.venv/bin/python -B -m unittest discover -s agent/tests      # 365
.venv/bin/python tools/gen_policy_catalog.py --check         # 策略漂移
$DOTNET build src/PawnMind.csproj -c Release -p:GameManaged=<game-managed>  # 编译 gate
```
