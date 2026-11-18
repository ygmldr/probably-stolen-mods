# R5-A TEST MATRIX — A1/A2/A3/A4 测试清单与真实运行结果

> 采集时间：2026-11-18T20:05Z
> 执行者：Coding Agent（Lead）——下表所有 `PASS` 均为**本机实际运行**，不是开发者转述。
> 证据分级：L1 = 纯逻辑/伪端口（无 Unity、无游戏进程）；L2 = 适配器契约（对
> `Assembly-CSharp.dll` 的反射契约，无进程）；L3 = Windows 真机（本轮 **全部 NOT_RUN**）。
> 相关提交：`c3f34bb6`（A1/A2/A3）、`5b7e2cff`（A4）。

## 0. 本轮 A 段门禁（整段运行结果）

| 套件 | 命令 | 退出码 | 结果 | 时间 |
|---|---|---|---|---|
| C# 纯逻辑（A 代码提交后） | `DOTNET=… REQUIRE_GAME=1 ./test.sh` | **0** | **passed=812 failed=0** | 6s |
| 脚本门禁 | 同上（内嵌 `test/ContractGate.Tests/run.sh`） | **0** | **passed=53 failed=0** | 同上 |
| 适配器契约（L2） | 同上（`REQUIRE_GAME=1`，`Assembly-CSharp.dll` sha256 `725cb999…`） | **0** | **contract checks=506，adapter contract OK** | 同上 |
| Python（A4 后） | `.venv/bin/python -m unittest discover -s agent/tests` | **0** | **Ran 415 tests，OK**（基线 377，+38） | 45.5s |
| Validator 聚焦（A4 后） | `.venv/bin/python -m unittest agent.tests.test_trace_validator` | **0** | **Ran 115 tests，OK**（基线 77，+38） | 0.01s |
| 策略目录一致性 | `.venv/bin/python tools/gen_policy_catalog.py --check` | **0** | policy sha256 未变（A 段不改工具面） | <1s |

> 说明：B2/B2b 提交后 C# 总数为 824/0（多出 R5_MODAL/R5_WIN 12 个测试），
> A 段自身的新增测试为 **18 C# + 38 Python**。

## 1. R5-A1 — first-run prompt 与 run-change 分类

| 测试 | 位置 | 覆盖点 | 级别 | 状态 |
|---|---|---|---|---|
| `R5_RUN_01_First_Run_Prompt_Does_Not_Block_The_Real_Start` | `test/PawnMind.Tests/R5RunChangeTests.cs` | prompt 确认（`run_change:"not_issued"`）→ 真实 start 不被 `run_change_pending` 拒绝；NoRun→runId 判 confirmed | L1 | **PASS** |
| `R5_RUN_02_The_Same_First_Run_Prompt_Is_Only_Confirmed_Once` | 同上 | 同一 prompt 只确认一次；重复调用 `prompt_already_confirmed`；两次结果都带 `run_change:"not_issued"`；native PromptCalls=1 | L1 | **PASS** |
| `R5_RUN_03_Dispatched_But_Unconfirmed_Start_Blocks_A_Second_Start` | 同上 | 真实 start 已派发但 run id 未变 → `requested`；第二个 start 被 `run_change_pending` 拒绝、dispatch 仅 1 次 | L1 | **PASS** |
| `R5_RUN_04_Unreadable_Run_Identity_Is_Not_A_Fake_Success` | 同上 | run id 读不到 → `unknown`、永不 confirmed；后续 start 仍被保护 | L1 | **PASS** |
| `R5_RUN_05_Real_Load_Protection_Still_Holds` | 同上 | 真实 load 的旧防护：requested + 不驱逐旧证据 + 第二个 load 被拒 | L1 | **PASS** |
| `R4_F1_01..R4_F1_14`（14 个） | `test/PawnMind.Tests/Round4OperationTests.cs` | R4 run-change 防护回归；仅 `R4_F1_01`（prompt 期待值）与 `R4_F1_12`（NoRun→NoRun 改为真实 start 语义）按 A1 新语义修订，保护断言未删/未弱化 | L1 | **PASS**（全量运行） |

**RED 证据（实现前）**：新增可注入时钟前先红 = C# 编译错误
`'RecordingHarness' does not contain a definition for 'ClockMs'` /
`'RecordingSaveService' does not contain a definition for 'ResetSleepTracking'`；
补齐 API 后行为红：`R5_RUN_01` 在旧代码下 `run_change_status` 出现 `requested` 且第二次
`confirm_new_game` 被 `run_change_pending` 拒绝（与 HANDOFF 描述的缺陷一致）。

**实现要点**：`ActionResult` 新增三态 `run_change` claim（`issued`/`not_issued`/缺省）；
`RecordingSaveService` 的菜单路径（含 prompt 确认）返回 `not_issued`，仅
`StartNewGame()` 已派发（含异常/未知结果）返回 `issued`；`ActionRouter` 在
`not_issued` 时清除该 op 的 `RunChangeRequested`；无 claim 的 op 保持旧式按 op 名保守判定
（load/new_game 不受影响）。旧证据驱逐仍只在 run identity 确认后发生一次。

**L3 项：NOT_RUN** —— 真机首次 prompt 与真实开局的 native 行为未在本机验证。

## 2. R5-A2 — sleep verifier 与 get_operation("*")

| 测试 | 位置 | 覆盖点 | 级别 | 状态 |
|---|---|---|---|---|
| `R5_SLEEP_01_Three_Immediate_Readbacks_Prove_Nothing` | `test/PawnMind.Tests/R5SleepVerifierTests.cs` | 三次零间隔 readback 全部 inconclusive（不再判 failed） | L1 | **PASS** |
| `R5_SLEEP_02_Async_Day_Advance_Completes` | 同上 | 观察窗内 day 增长 → completed | L1 | **PASS** |
| `R5_SLEEP_03_Timeout_Is_Uncertain_And_Blocks_A_New_Action_Id` | 同上 | 过窗无强证据 → `uncertain`/`sleep_unconfirmed`；新 action_id 被 `operation_pending` 拒绝；之后观测到 day 增长仍可提升为 completed（安全恢复） | L1 | **PASS** |
| `R5_SLEEP_04_Not_Dispatched_Allows_A_Safe_Retry` | 同上 | `native_not_called` + `dispatched:false` → 安全重试，不阻塞 | L1 | **PASS** |
| `R5_SLEEP_05_Stuck_Sleep_Stays_Discoverable_And_Truncation_Is_Explicit` | 同上 | 26 个非终态时 `count=20,total_non_terminal=26,truncated=true`；卡住 sleep id 可 `get_operation` 解析 | L1 | **PASS** |
| `R5_SLEEP_06_Run_Change_Does_Not_Attribute_An_Old_Sleep` | 同上 | `ResetSleepTracking` 后旧 sleep 不会被新 run 的 day 数误判完成 | L1 | **PASS** |
| `Sleep_Verifier_Timeout_Is_Uncertain_Not_Failed` | `test/PawnMind.Tests/FirstWeekSleepTests.cs` | 取代 R4-SLEEP-02（旧断言“三次读后 failed”正是 A2 要修的缺陷，已按新语义改写并保留“不得假失败”意图） | L1 | **PASS** |

**RED 证据**：先红 = 编译缺 `ClockMs`/`ResetSleepTracking`；补 API 后行为红 =
`R5_SLEEP_01` 第三次 readback 返回 `failed/day_not_changed`（旧实现）、
`R5_SLEEP_03` 缺少 uncertain/truncation 字段、`R5_SLEEP_05` `truncated` 缺失。

**实现要点**：`SleepObservationWindowMs = 10000` + 注入式单调时钟
（`Stopwatch`，测试注入 fake clock）；`uncertain` 非终态、保留防重复派发；
`get_operation("*")` 增加 `count` / `total_non_terminal` / `truncated` / `truncation_note`
（`ActionGate.CountNonTerminal`）；`PawnMindMod.ResetRunBoundEvidence` 在 run 确认后调用
`ResetSleepTracking()`。

**L3 项：NOT_RUN** —— 真机异步黑屏真实时长/时序、睡眠双推进风险未在本机验证；
10s 观察窗是保守默认值，需真机校准。

## 3. R5-A3 — 单笔交易完成证据 + 预算预检

| 测试 | 位置 | 覆盖点 | 级别 | 状态 |
|---|---|---|---|---|
| `R5_TRADE_01_Visit_Complete_Without_Per_Trade_Evidence_Is_Uncertain` | `test/PawnMind.Tests/TradeServiceTests.cs` | `business_complete=true` 但无物品/现金移动 → `uncertain`（`no_trade_evidence`），不再 ok:true | L1 | **PASS** |
| `R5_TRADE_02_Repeated_Partial_Sales_To_The_Same_Client_Each_Complete` | 同上 | 同一顾客多笔 partial sale：每笔 `business_complete=false` 仍 completed | L1 | **PASS** |
| `R5_TRADE_03_Buy_Is_Proven_By_Cash_When_The_Staged_Pointer_Stays` | 同上 | 买入方向 staged 指针不变、现金移动 → completed | L1 | **PASS** |
| `R5_TRADE_04_Barter_Completes_On_Items_With_Zero_Cash` | 同上 | 以物易物零现金、物品移动 → completed | L1 | **PASS** |
| `R5_TRADE_05_A_Different_Client_Cannot_Back_The_Trade` | 同上 | 换顾客 → `client_changed`，不判成功 | L1 | **PASS** |
| `R5_TRADE_06_Insufficient_Client_Budget_Refuses_Before_The_Accept` | 同上 | 卖方向预算不足 → `client_budget_insufficient`、`dispatched:false`、0 次 native click；边界 `value==budget` 可成交；买方向不被顾客预算拦截 | L1 | **PASS** |
| `R5_TRADE_07_Uncertain_Trade_Cannot_Be_Blind_Retried_With_A_New_Id` | 同上 | 同 item+args 的 uncertain 在新 action_id 下被 `operation_pending` 拒绝、native click 仍为 1 | L1 | **PASS** |
| `PP_TRADE_*` / `R4_TRADE_*`（26 个） | 同上 | 既有交易守护回归，断言未改动 | L1 | **PASS** |

**RED 证据（实现前，3 个失败，均与缺陷对应）**：
`R5_TRADE_01`：旧代码 `ok:true/completed` 于 business_complete=true + 无移动；
`R5_TRADE_06`：旧代码忽略预算事实并派发 native click；
`R5_TRADE_07`：旧代码首笔就判 completed（未走 uncertain 指纹阻塞）。
`R5_TRADE_02..05` 在实现前即为通过（R4 行为回归护栏，非新增修复）。

**实现要点**：完成判据 = `called && error==null && client_unchanged && (items_transferred||cash_moved)`；
`business_complete`/`visit_complete` 仅作整趟归因；uncertain 行补充真实的顶层证据字段但
永不升级为成功。预算预检只用玩家可见值：`StoreClient.useClientBudget`、
`GameState.GetClientBudget`（同一 `client.budget` 快照字段）、
`GameItem.GetNegociatedValue()`（议价台显示值），读不到价格 fail-closed。
证据链来自 `_decomp`：`OnOfferAcceptClick → TrySellItem → PlayerStore.SellItemFromTable →
CanSellItemFromTable(ignoreBudget:false) → StoreClient.HasBudgetLeftToBuy`。

**L3 项：NOT_RUN** —— 真实 native budget 拒绝路径、真机 partial trade 行为未在本机验证；
`TryReinspectBeforeSale` 等其它 silent-return 分支仍无守卫（与 R4 相同）。

## 4. R5-A4 — validator / Skill / benchmark 协议漂移

| 项 | 位置 | 覆盖点 | 级别 | 状态 |
|---|---|---|---|---|
| `TRADE_EVIDENCE_MISSING` v2 规则 | `agent/trace_validator.py`（`VALIDATOR_VERSION 1.2.0`, `TRACE_SCHEMA_VERSION 2`） | completed confirm_trade 需 `client_unchanged && (items_transferred||cash_moved)` + evidence 对象一致；business_complete 不再必需；visit-complete 无移动被 flag | L1 | **PASS** |
| 旧轨迹迁移策略 | 同上 | 无 `schema_version` → 按 v1 规则校验 + `TRACE_SCHEMA_VERSION_MISSING`（warning，规则判决不同时）；不支持版本 → `TRACE_SCHEMA_VERSION_UNSUPPORTED`（error，fail-closed） | L1 | **PASS** |
| `CONFLICTING_WRITE_WHILE_PENDING` 校准 | 同上 | 显式关系 `same_op/same_item/run_changing/modal_resolution/independent/unknown`；只有不同 item 的 call-scoped 交易与 modal-resolution 可放行；证据不足一律 ERROR | L1 | **PASS** |
| A1/A2 集成规则 | 同上 | `run_change` 枚举校验；`prompt_confirmed` 权威非 run-start；未确认 run change 阻塞后续写；重复调用拒绝不算 pending；观测读接受 day 变化 | L1 | **PASS** |
| 6 个正/反 fixture | `agent/tests/fixtures/` | 见 §5 实际 CLI 退出码 | L1 | **PASS** |
| SKILL.md 漂移 | `skills/pawnmind-first-week/SKILL.md` | §1.2 两种 confirm_new_game 子动作（撤销“整局一次”）；§5.2 单笔证据；§3 get_operation 截断字段；sleep 不再教“三次轮询后重试”；press 仅 test-only、Play 禁止 | 文档 | **PASS**（文本核对） |
| BENCHMARK_SPEC 漂移 | `docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md` | `spec_version 2026-11-19-r5` 与实测环境分离；旧 HEAD/policy/工具数标为起草基线；`BENCH_TRADE_EVIDENCE_INVALID` 承认 partial trade；G3–G5/错误码同步 | 文档 | **PASS**（文本核对） |
| known-limitations | `docs/pawnmind-known-limitations.zh-CN.md` | §1–§10 作为 2026-11-10 历史保留；新增 §11 R5-A 更新；L3 一律 NOT_RUN；未静默擦除旧事实 | 文档 | **PASS**（文本核对） |

**RED 证据（实现前，按 A4 报告的口径，本机已复跑最终态）**：v2 交易规则 13 红
（2 errors + 11 failures：v2 partial trade 被误报、visit-complete 无效果被误放行、版本策略缺失）；
冲突关系/run-change 15 红；异步 day 证据 1 红。最终 Python 全量 415 OK。

## 5. Fixture CLI 实测（本机）

```bash
cd PawnMind && for f in agent/tests/fixtures/*.json; do \\
  .venv/bin/python agent/trace_validator.py "$f"; echo exit=$?; done
```

| fixture | 期望 | 实测退出码 | 实测 codes |
|---|---|---|---|
| `trade_partial_ok.json` | 合法（R4 partial trade） | **0** | `[]` |
| `trade_visit_complete_no_effect_invalid.json` | 非法 | **1** | `TRADE_EVIDENCE_MISSING` |
| `pending_same_item_retry_conflict.json` | 冲突 | **1** | `CONFLICTING_WRITE_WHILE_PENDING`（relation=same_item） |
| `pending_different_item_ok.json` | 合法 | **0** | `[]` |
| `prompt_confirm_then_real_start_ok.json` | 合法 | **0** | `[]` |
| `sleep_async_converges_ok.json` | 合法 | **0** | `[]` |

## 6. 本轮 A 段明确未做 / NOT_RUN

| 项 | 状态 | 原因 |
|---|---|---|
| 所有 R5-A 的真机（L3）行为验证 | **NOT_RUN** | 无 Windows、无游戏进程（BRIEF §0） |
| 真机 first-run prompt / 真实开局 / 存档 load | **NOT_RUN** | 同上；不允许读写存档 |
| 真机 sleep 异步黑屏时序（10s 窗口是否足够） | **NOT_RUN** | 同上 |
| 真机交易预算拒绝与 partial trade | **NOT_RUN** | 同上 |
| trace_validator 对真实首周轨迹的校验 | **NOT_RUN** | E2：历史原始轨迹缺失（见 R5_BASELINE §4） |
| `_internal/week.py` 复核 | **NOT_RUN** | E3：脚本 gitignored 且本机不存在 |
| 独立 Reviewer A 意见 | 见 `docs/reviews/R5_A_PROTOCOL_REVIEW.md` | 由 fresh-context reviewer 出具 |

## 7. 已知残留风险（A 段）

1. A2 的 10s 观察窗是保守默认值，未在真机校准；过短 → 把慢黑屏判 uncertain（安全但不便）；
   过长 → 卡住更久。真机需记录实际 sleep→day 变化耗时。
2. A1 的 `run_change` claim 是新增**响应字段**（不是新工具）：Skill/benchmark 已同步；
   旧客户端若只认 `run_change_status` 不受影响（prompt 结果显示不再带该字段）。
3. A3 预算预检是 native 谓词的镜像，真机有效性未验证；`TryReinspectBeforeSale` 等其它
   silent-return 分支仍未覆盖（与 R4 相同）。
4. A4 的旧轨迹策略：无版本号轨迹只在“v1/v2 判决不同”时告警；历史轨迹缺失（E2）意味着
   该策略本轮无法用真实旧轨迹回归验证。
5. 本矩阵中所有 L1/L2 结论都不代表真机可用性；任何“已解决真实游戏”的说法都需要 L3 证据。
