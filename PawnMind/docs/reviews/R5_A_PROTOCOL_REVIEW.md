# R5_A_PROTOCOL_REVIEW — R5-A（A1–A4）独立协议审计

> 审计者：`reviewer-a`（fresh context，非实现者，未参与 R5-A 任何代码/测试编写）。
> 审计时间：2026-11-18T20:06Z – 20:40Z（Linux 主机，无 Windows、无游戏进程、无存档操作、无网络）。
> 分支：`r5/overnight`。
> 审计目标快照：
>
> | 快照 | commit | 用途 |
> |---|---|---|
> | A1/A2/A3 代码+测试 | `c3f34bb6` | A 阶段主提交 |
> | A4 validator/Skill/benchmark/known-limitations | `5b7e2cff` | A4 文档与协议提交（parent 链含已合入的 B2 `a0ac8848`） |
> | A1 复审缺口修复（A-phase follow-up，Lead 确认纳入 A 审计范围） | `c3196301` | 修 F-A1-1 + `R5_RUN_06` |
>
> 三点说明：
> 1. 审计期间主工作树被其他 teammate 持续编辑（B1 staging / C2 recorder 等 19+ 个未提交改动），因此**所有测试与复现都在独立 `git worktree` 快照上运行**，未使用主工作树。worktree 路径：`_internal/review-a-worktree`（5b7e2cff）、`_internal/review-a-c3`（c3f34bb6）、`_internal/review-a-fix`（c3196301）。
> 2. 本审计唯一写入的文件就是本 review 文档；未修改生产代码/测试，未 `git add`/`commit`，未构建 `dist/`，未运行 `build.sh`。
> 3. 证据分级：**VERIFIED** = 本次审计实际运行/实际读源码并给出文件行号；**REPORTED** = 开发者/队友声明，本次未独立复现；**NOT_RUN** = 环境/授权不允许（Windows L3、真机、存档、Workshop、扫描器）。

---

## 0. 结论摘要（Verdict）

| 项 | 结论 | 一句话依据 |
|---|---|---|
| **A1** 首次开局 prompt vs run-change | **PASS**（含 1 个审计发现并已修复复验） | 主路径语义正确、R5_RUN_01..06 全绿、R4 防护未弱化。审计发现 **F-A1-1**：post-dispatch 状态不可读分支仍会让 prompt 确认永久阻塞真实开局（pre-fix `5b7e2cff` 可复现 FAIL）；`c3196301` 修复后原复现脚本转为 PASS、全量门禁 825/0。 |
| **A2** sleep verifier / `get_operation("*")` | **PASS** | 无 count-based failure；注入式单调时钟；`uncertain` 非终态且继续阻塞新 sleep action id；后续 day 前进可恢复 completed；truncation 字段与 run-change reset 均落地，R5_SLEEP_01..06 全绿。 |
| **A3** 单笔成交证据 + 预算预检 | **PASS** | 完成判据 = `called && error==null && client_unchanged && (items_transferred \|\| cash_moved)`；`business_complete` 仅归因；R4 partial trade 回归保留；预算预检与反编译源码中的 native 谓词逐字段一致，且发生在 dispatch 前；`TradeServiceTests` 既有断言零删改（numstat 215/0）。 |
| **A4** validator/Skill/benchmark 漂移 | **PASS**（3 个文档精度 WARN，见 §5.5） | validator `1.2.0` / trace schema `2` 与 A3 实际字段名逐项对上；legacy v1 迁移策略 fail-closed 且有 warning；conflict relation 校准符合“独立/弹窗可放行、证据不足一律 ERROR”；6 fixture 精确 code set 与退出码复现；Skill §5.2/§1.2 核心漂移已修；benchmark `spec_version` 与实测环境已分离。W-A4-1（load 响应 `run_change` 文档超前于代码）等不影响 validator 判据，建议 B/C 文档 pass 一并修正。 |

**A 阶段退出门槛判读：** 既有 C#/Python/contract/policy 测试本次全部实际通过；新增反例测试通过；A1 的旧风险被修且审计当场又抓出一个同族缺口并已修复复验——可放行进入 B/C，但 `W-A4-1` 建议随文档 pass 收口。

### 0.1 本次实际运行的完整门禁记录（VERIFIED）

C# 命令（每个快照都在独立 worktree 内；注意 worktree 从 git blob 还原后 `test.sh`/`run.sh` 无执行位，见 §7-W-ENV-1，审计时在**临时 worktree 内** `chmod +x` 仅用于本次执行，未改仓库文件）：

```bash
cd <worktree>/PawnMind
export DOTNET=/root/.dotnet/dotnet \
       PAWNMIND_NEWTONSOFT_DLL=/root/work/game_managed/Newtonsoft.Json.dll \
       GAME_ASSEMBLY=/root/work/game_managed/Assembly-CSharp.dll \
       REQUIRE_GAME=1
flock /root/work/game_mods/_internal/pawnmind-test.lock ./test.sh
```

| 快照 | exit | 纯逻辑 | 脚本门禁 | 适配器契约 | 原始日志 |
|---|---|---|---|---|---|
| `c3f34bb6`（A 提交） | **0** | **passed=812 failed=0** | **passed=53 failed=0** | **contract checks=506 / adapter contract OK** | `_internal/logs/review_a_c3_cs.log` |
| `5b7e2cff`（A4，含 B2） | **0** | **passed=820 failed=0** | **passed=53 failed=0** | **contract checks=515 / adapter contract OK** | `_internal/logs/review_a_cs.log` |
| `c3196301`（A1 fix，含 B2/B2b） | **0** | **passed=825 failed=0** | **passed=53 failed=0** | **contract checks=515 / adapter contract OK** | `_internal/logs/review_a_fix_cs.log` |

计数差分可解释（防止“数字变大即完成”式误读）：
`794`（R5 启动基线）→`812` = +18 个 R5-A1/A2/A3 测试（5+6+7）；`812`→`820` = 已合入的 B2 加 8 个 R5_MODAL 测试；`820`→`824`（`643003c9` B2b 加 4 个 R5_WIN 测试）；`824`→**`825`** = `c3196301` 的 `R5_RUN_06`。契约 `506`→`515` = B2 新增 9 条 adapter contract 检查。`git show --numstat c3f34bb6` 与 `5b7e2cff` 均与这些数字一致。

Python 命令（用主仓库 venv 解释器，但 `cwd`/`PYTHONPATH` 指到 worktree，保证导入 worktree 的 `agent` 包；worktree 本身无 `.venv`）：

```bash
cd <worktree>/PawnMind
PYTHONPATH=<worktree>/PawnMind /root/work/game_mods/PawnMind/.venv/bin/python \
  -m unittest discover -s agent/tests
```

| 快照 | exit | 结果 |
|---|---|---|
| `5b7e2cff` | **0** | **Ran 415 tests — OK**（基线 377，+38） |
| `c3196301` | **0** | **Ran 415 tests — OK**（A1 fix 只改 C#，Python 无变化） |

聚焦测试（`5b7e2cff`）：`-p 'test_trace_validator.py'` → **Ran 115 — OK**（基线 77，+38）；`-p 'test_skill_tool_refs.py'` → **Ran 16 — OK**。两者均 exit 0，与 A4 提交信息及 `R5_A_TEST_MATRIX.md` §0 声明一致。

Fixture CLI（逐文件 `python agent/trace_validator.py <file>`，`5b7e2cff` 与 `c3196301` 结果相同；**审计者自行核对映射，不采信给定列表**）：

| fixture | exit | 实际 codes（CLI JSON） |
|---|---|---|
| `trade_partial_ok.json` | **0** | `[]` |
| `trade_visit_complete_no_effect_invalid.json` | **1** | `TRADE_EVIDENCE_MISSING`（`reason=no_trade_effect`） |
| `pending_same_item_retry_conflict.json` | **1** | `CONFLICTING_WRITE_WHILE_PENDING`（`relation=same_item`） |
| `pending_different_item_ok.json` | **0** | `[]` |
| `prompt_confirm_then_real_start_ok.json` | **0** | `[]` |
| `sleep_async_converges_ok.json` | **0** | `[]` |

即实测为 **4×exit 0 + 2×exit 1**，且与 `agent/tests/test_trace_validator.py::TraceFixtureTests.EXPECTED_CODES` 的精确 code set 断言一致（§6.2）。

### 0.2 环境（VERIFIED）

| 项 | 值 |
|---|---|
| 主机 | `Linux 6.17.13-2-pve x86_64` |
| dotnet | `/root/.dotnet/dotnet` `9.0.321` |
| Python | `3.12.3`（`PawnMind/.venv`） |
| 游戏程序集 | `/root/work/game_managed/Assembly-CSharp.dll` sha256 `725cb999d019dff1…`（与 R5_BASELINE 一致） |
| Newtonsoft | `a56146202232958f…` |
| 游戏进程 / Windows / 存档写 | **无 / 不可用 / 未接触**，所有 L3 一律 `NOT_RUN` |
| 反编译参考（A3 谓词核对） | 本机 `_decomp/`（gitignored，不入库、不复制大段代码；仅引用类型/方法/结论，符合仓库 AGENTS §5） |

---

## 1. A1 — first-run prompt 与 run-change 分类

### 1.1 读了什么

- `src/Bridge/ActionRouter.cs`：`HandleWrite` 全文（`193–335`）、`TryRefreshRevision`（`1003–1017`）、`VerifyRunChange`/`ApplyRunChange`（`566–607`）、`GetOperationJson`（`397–473`）、`DecorateResponse`；
- `src/Bridge/ActionResult.cs`：`SuccessRaw` 的 claim 解析（`110–190`）；
- `src/Bridge/RecordingSaveService.cs`：`ConfirmNewGame`（`131–219`）、`ConfirmPrompt`（`444–507`）、`StartSuccess`（`617–633`）、`MenuFailure`/`Failure`（`862–905`）、`Load`（`558–615`）；
- `src/PawnMindMod.cs`：`DispatchAction` claim 分支（`260–278`）、`ResetRunBoundEvidence`（`288–300`）；
- `src/Core/RunChangePolicy.cs` 全文（`Classify` 的 NoRun/Unknown 语义，`75–99`）；`src/Core/OperationStatus.cs`（`RunChangeRequested/RunChange/RunIdBefore`）；
- 测试：`R5RunChangeTests.cs` 全部（含 `c3196301` 新增 `R5_RUN_06`）、`Round4OperationTests.cs` 中 `R4_F1_01`（`71`）、`R4_F1_08`（`300`）、`R4_F1_12`（`823`）、`R4_F1_13`（`848`）；
- commit diff：`c3f34bb6`（A1 部分）、`c3196301` 全部（fix）。

### 1.2 验证结论（逐条对应 handoff A1 目标语义）

1. **prompt 确认不登记 run change（主路径）** — VERIFIED。
   - 服务侧：`ConfirmPrompt` 成功行显式 `run_change = "not_issued"`（`RecordingSaveService.cs:500`）；`MenuFailure`（`862`）对所有“未派发跨局动作”的菜单拒绝都带 `not_issued`；只有 `StartNewGame()` 已派发/可能派发（含异常、未知）才 `issued`（`187–188`、`204–209`、`216`、`624`）。
   - Router 侧：`ApplyRunChangeClaim` 只接受精确的 `"not_issued"`（`ActionResult.cs:177–190`），清除 `RunChangeRequested/RunChange/RunIdBefore`（`ActionRouter.cs:583–598`）。
   - `R5_RUN_01` 实测 PASS：prompt 响应无 `run_change_status`/`run_change_confirmed`；随后真实 start 不被 `run_change_pending` 拒绝，`NoRun → run id` 判 `confirmed`，且确认后旧证据被驱逐（`prompt-1` unknown）。
2. **只有实际 start/load 保留盲重试防护** — VERIFIED。
   - `R5_RUN_03`（已派发未确认 → `requested`，第二 start 被 `run_change_pending` 拒绝、native dispatch 仍为 1）、`R5_RUN_04`（run id 不可读 → `unknown`，永不 confirmed，第二次 start 仍被保护）、`R5_RUN_05`（真实 load 旧防护：requested + 不驱逐旧证据 + 第二次 load 被拒）全部 PASS。
   - `ActionGate.FindPendingFor`/`FindUnconfirmedRunChange` 的“may have run 且非终态才阻塞”语义未变（`ActionGate.cs:251–292`）。
3. **run id 不可读不产生假成功** — VERIFIED。`SafeReadRunId` 异常返回 `null`（`ActionRouter.cs:609–621`）；`RunChangePolicy.Classify` 对 `null/empty` 返回 `unknown`（`RunChangePolicy.cs:77–79`）；`R5_RUN_04` 断言 `unknown` + `run_change_confirmed=false`。
4. **R4_F1_01..14 防护未被弱化** — VERIFIED（diff 审计）。
   - `git show c3f34bb6 -- PawnMind/test/PawnMind.Tests/Round4OperationTests.cs` 只改动两个测试：`R4_F1_01`（预期从“prompt 是 requested”改为“prompt 根本不是 run change”，但**保留**“旧 operation 证据必须存活”的保护断言）与 `R4_F1_12`（NoRun→NoRun 的 `requested` 语义**保留**，仅把 fake 从“prompt_confirmed+WithRunChangeRequested”换成“真实 start + issued”的诚实输入）。其余 `R4_F1_02..14` 零改动；`R4_F1_08`（未确认时第二 run-change 被拒）与 `R4_F1_13`（confirmed 后释放）原样通过。
   - `FirstWeekSleepTests` 中被改的是 `R4-SLEEP-02`（属 A2 的预期漂移，见 §3，且新断言更强：uncertain 而非 failed）；`TradeServiceTests` 为纯新增（`--numstat` 215/0）。

### 1.3 审计发现 F-A1-1（真实缺口，pre-fix FAIL@`5b7e2cff`，已修复@`c3196301`）

**现象：** `ActionRouter.HandleWrite` 原来只在**正常完成路径**应用 `not_issued` claim（`5b7e2cff` `ActionRouter.cs:309–323`）。当 dispatch 之后 `TryRefreshRevision` 失败（`state_unavailable` 提前返回分支，`ActionRouter.cs:282–302`）时：
- prompt 确认结果虽已明确 `run_change:"not_issued"`，但 op 仍保留 `RunChangeRequested=true` 且 execution 停在 `uncertain`（非终态）；
- 后续第一次真实 `confirm_new_game` 同时被两个门拦住：`FindPendingFor`（同 op 名、may-have-run、非终态）→ `operation_pending`；以及 `FindUnconfirmedRunChange` → `run_change_pending`；
- 该 op 没有 verifier 能把它推进为终态，`get_operation` 也永远只能看到 `requested`，于是本进程内**永久阻塞真实开局**（正是 handoff A1 明确禁止的“不可因 prompt 永久阻塞实际开局”）。

**最小复现（隔离 harness，不改仓库代码/测试）：** 将 `5b7e2cff` 的 `src/` 与既有 `test/PawnMind.Tests` 原样复制到 `_internal/repro-a1`，只新增一个独立测试文件（等价于 `R5_RUN_06` 的极简版），用**可变的 revision 委托**构造 `ActionRouter`：dispatch 第一次 `confirm_new_game` 时把 revision 置为 `-1`（模拟 post-dispatch 快照不可读）并返回 `prompt_confirmed:true + run_change:"not_issued"`；恢复正常 revision 后再发第二个 action id 的真实 start：

```csharp
long revision = 3;
ActionRouter router = new ActionRouter(
    new ActionGate(),
    delegate { return revision; },                       // current revision
    delegate(string op, JObject request)
    {
        if (op != "confirm_new_game") return ActionResult.Success(op);
        revision = -1;                                   // post-dispatch refresh fails
        return ActionResult.SuccessRaw(op,
            "{\"ok\":true,\"dispatched\":true,\"prompt_confirmed\":true,"
            + "\"run_change\":\"not_issued\"}");
    },
    new ActionHistory(),
    delegate { return revision; },                       // refresh revision
    Token, delegate { return OpenGate(); }, null, null, null,
    delegate { return RunChangePolicy.NoRun; }, null);

router.Handle("{\"op\":\"confirm_new_game\",\"action_id\":\"prompt-1\",\"rev\":3}");
revision = 3;
string start = router.Handle("{\"op\":\"confirm_new_game\",\"action_id\":\"start-1\",\"rev\":3}");
// pre-fix 期望：start 被拒绝；修复后期望：start 被派发
```

pre-fix（`5b7e2cff`）实测输出（关键片段）：

```json
PROMPT: {"ok":false,"error":"outcome_unknown",...,"execution_status":"uncertain",
         "run_change_status":"requested","run_change_confirmed":false}
START : {"ok":false,"error":"operation_pending",...,"blocking_operation_id":"prompt-1",
         "blocking_same_call":true}
```

即 prompt 被错误地报告为 `run_change_status:"requested"`，且第二次真实 start 没被派发。

**修复 `c3196301`（Lead 确认纳入 A 审计范围）复核：**
- `ActionRouter.ApplyRunChangeClaim(...)` 同时被正常路径（`:316`）与 lost-revision 分支（`:288`）调用；
- `OperationRecord.ClaimedNotIssued` 标记（`OperationStatus.cs:174–184`，`Copy()` 保留 `:222`）；
- `ActionGate.FindPendingFor` 对 `ClaimedNotIssued` 跳过（`ActionGate.cs:261–269`）——但**仅**该显式 claim 的 op 被跳过；真实 start/load 不设置此标记，仍由 `FindUnconfirmedRunChange`（`ActionGate.cs:279–292`）按 `RunChangeRequested` 阻塞；
- 新测试 `R5_RUN_06_Prompt_Confirmation_Does_Not_Block_After_A_Refresh_Failure`（`R5RunChangeTests.cs:199`）覆盖同一场景，全量套件中 PASS。

**修复后复跑同一隔离 harness（`c3196301` 源码）结果：**

```json
PROMPT: {"ok":false,"error":"outcome_unknown",...,"execution_status":"uncertain"}   // 不再有 run_change_status
START : {"ok":false,"error":"outcome_unknown",...,"operation_id":"start-1",
         "dispatched":true}                                                          // 已被派发，不再 operation_pending
PASS ReproA1EdgeTests.A1_Edge_Prompt_With_ResponseRevision_Unavailable_Blocks_Real_Start
passed=1 failed=0
```

结论：**F-A1-1 已修复且审计者独立复验 PASS**。修复没有放宽真实 start/load 防护（`R5_RUN_03/04/05`、`R4_F1_08/12/13` 同套件全绿，且 claim 来源仅是服务端精确的 `not_issued`，无法由外部伪造触发）。

### 1.4 A1 残留风险

- **R-A1-1（低，设计内）**：真实 start/load 若在 post-dispatch 状态不可读分支停在 `uncertain`，仍会按防盲重试原则阻塞后续 run-change，需要人工介入/重启；这与 handoff “无安全恢复证据则人工干预”一致，属预期而非缺陷。
- **R-A1-2（测试覆盖）**：router 层对“菜单拒绝（`not_issued`）后不阻塞下一次 run-change”只有服务层 `R5_RUN_02` + 正常成功路径 `R5_RUN_01` 覆盖；拒绝路径由同一 helper 保证，风险低。
- **W-A4-1 关联**：`load` 响应不携带 `run_change` 字段（见 §5.5），因此文档中“load 响应带 `run_change:"issued"`”不成立；协议行为本身保守正确。
- 真机 first-run prompt / 真实开局：**L3 NOT_RUN**。

证据等级：主路径 **VERIFIED**（测试实跑 + 源码 + 独立复现）；RED 序列（实现前编译/行为红）为 **REPORTED**（未在历史提交上重放全部红态；但 F-A1-1 的 pre-fix 红态由审计者独立复现，属 VERIFIED）。

---

## 2. A2 — sleep verifier 与 `get_operation("*")`

### 2.1 读了什么与跑了什么

- `src/Bridge/RecordingSaveService.cs`：`TryResolveSleep`（`265–297`）、`SleepObservationWindowMs`（`304`）、`ResetSleepTracking`（`311–317`）、constructor 注入时钟（`28–58`）、`Sleep`（`319–422`）的 dispatch/窗口起点/非终态返回；
- `src/Core/ActionGate.cs`：`ListNonTerminal`（`299–313`）、`CountNonTerminal`（`315–332`）、`FindPendingFor`（`251–272`）；
- `src/Core/OperationStatus.cs`：`IsTerminal`（`44–47`，只含 completed/rejected/failed）；
- `src/Bridge/ActionRouter.cs`：`ListOperationsJson` 的 `count/total_non_terminal/truncated/truncation_note`（`475–515`）、`GetOperationJson` 的 verifier 路径（`397–473`，仅非终态记录进入 verifier）；
- `src/PawnMindMod.cs`：`_operationVerifier` 注入（`:162`）、`VerifyOperation/VerifySleep`（`902–963`）、`ResetRunBoundEvidence` 里 `ResetSleepTracking()`（`288–300`）；
- 测试：`R5SleepVerifierTests.cs` 全部（`43/65/98/160/191/225`）、`FirstWeekSleepTests.cs` 改写后的 `Sleep_Verifier_Timeout_Is_Uncertain_Not_Failed`（`:82`）。

### 2.2 逐条验证

| Handoff A2 要求 | 结论 | 证据 |
|---|---|---|
| 无 count-based failure | VERIFIED | `sleepPolls`/`SleepVerifierPolls` 已从源码删除（全库 grep 仅历史文档提及）；判据改为 `clockMs()-sleepIssuedAtMs < SleepObservationWindowMs`（`RecordingSaveService.cs:287`）。`R5_SLEEP_01`：三次零间隔读全部 inconclusive。 |
| 可注入单调时钟 | VERIFIED | 构造参数 `Func<long> clockMs`，默认 `Stopwatch`（`9–58`）；测试用 `RecordingHarness.ClockMs` 注入（`RecordingTestFakes.cs` 5/1 改动）。 |
| 窗口内 Day 增长 → completed | VERIFIED | `now.Day > sleepDayBefore` → completed（`:274–282`）；`R5_SLEEP_02` PASS（含 5s 中点仍 inconclusive、6s day 增长 completed）。 |
| 超时无强证据 → uncertain 而非 failed | VERIFIED | `:289–296` 返回 `ExecutionStatus.Uncertain` / `sleep_unconfirmed`；`R5_SLEEP_03` 断言 `execution_status=uncertain`、`error=sleep_unconfirmed`、`completion_evidence` 为空。 |
| uncertain 非终态且继续阻塞新 sleep action id | VERIFIED | `IsTerminal` 不含 uncertain（`OperationStatus.cs:44–47`）；`FindPendingFor` 对 may-have-run 非终态 op 继续拒绝（`ActionGate.cs:251–272`）；`R5_SLEEP_03` 第二次 `sleep` 新 id 实测 `operation_pending`、native `SleepCalls=1`。 |
| 后续观测 day 前进可恢复 completed | VERIFIED | `sleepTimedOut` 状态下只接受 day 前进作为恢复证据（`:274–282` 先于 `:285`）；`R5_SLEEP_03` 在 day=4 后 `get_operation("sleep-a")` → `completed` 且带 `completion_evidence`。 |
| `get_operation("*")` 暴露 count/total_non_terminal/truncated | VERIFIED | `ListOperationsJson`（`ActionRouter.cs:500–514`）+ `CountNonTerminal`（`ActionGate.cs:315–332`）；`R5_SLEEP_05` 26 个非终态 → `count=20,total_non_terminal=26,truncated=true`，卡住 sleep id 仍可查。 |
| `get_operation(id)` 无游戏写副作用 | VERIFIED（源码） | 查询路径只做 `Refresh()`/读状态 + 更新内存 operation 记录（`ActionRouter.cs:397–473`、`PawnMindMod.cs:902–963`）；不派发 native 动作。 |
| run change 时 ResetSleepTracking 已接线 | VERIFIED（源码）/ 单元测试覆盖服务层 | `PawnMindMod.cs:168` 把 `ResetRunBoundEvidence` 注册为 `onRunChangeConfirmed`，`:288–300` 调 `recording.ResetSleepTracking()`；`R5_SLEEP_06` 直接单测服务层语义（reset 后 day=5 不误判 completed）。**注**：Mod 层“run 确认 → callback”的端到端接线只有源码证据（Mod 层无 L1 测试），标 VERIFIED(源码)。 |
| 仅“可证明未派发”才允许安全重试 | VERIFIED | `R5_SLEEP_04`：`native_not_called` + `dispatched:false` → 新 id 重试不被 pending 阻塞，native 调 2 次（首+重试各 1）。 |

### 2.3 A2 残留风险

- **R-A2-1（设计默认值，L3 未校准）**：`SleepObservationWindowMs=10000` 是保守默认；真机黑屏实际时长未知，过短→过早 uncertain（安全但不便），过长→卡住更久。handoff 已要求真机记录 sleep→day 耗时，**L3 NOT_RUN**。
- **R-A2-2（进程重启未知）**：operation cache 与 sleep 跟踪均在内存；进程重启后旧 sleep 不可查询，`get_operation` 返回 `unknown_operation`（禁止自动重放），但无法证明旧 sleep 是否已在 native 侧执行——与 handoff 允许的“未知则人工干预”一致，**L3 NOT_RUN**。
- **R-A2-3（测试表述）**：`R5_RUN_06` 之外，`R5_SLEEP_06` 只验证 service reset，不验证 `ApplyRunChange → callback` 的真实串联（Mod 层）。风险低（单行接线）。

证据等级：L1 行为 **VERIFIED**；真机异步时序/双推进 **NOT_RUN**。

---

## 3. A3 — 单笔成交证据 + 预算预检

### 3.1 读了什么

- `src/Bridge/TradeService.cs` 全文（243 行）：pre-dispatch `Read`/guard/`Accept` 顺序（`73–111`）、逐笔证据判定（`127–146`）、completed 行（`148–169`）、refusal（`172–184`）、uncertain 行（`186–224`）、evidence 明细（`226–241`）；
- `src/Core/TradeGuard.cs` 全文：新输入字段（`81–88`）、预算 mirror 判定（`161–172`）；
- `src/Game/GameTradePort.cs`：预算字段读取（`49–58`）、`SafeNegociatedValue` fail-closed（`61–79`）、`Accept` 的 evidence（`81–108`）；
- `test/PawnMind.Tests/TradeServiceTests.cs`：全部既有 `PP_TRADE_*`/`R4_TRADE_*`（含 `R4_TRADE_01` partial sale `:311`）与新增 `R5_TRADE_01..07`（`563/592/628/655/683/706/747`）；
- 反编译只读核对（本机 `_decomp/`，gitignored）：`StoreClient.HasBudgetLeftToBuy`、`StoreClient.GetBudget`、`PlayerStore.CanSellThisItem`/`SellItemFromTable`、`GameState` 适配读取；
- `git show --numstat c3f34bb6` 确认 `TradeServiceTests.cs` 215 insertions / **0 deletions**；`PP_TRADE_*`、`R4_TRADE_*` 断言零删改。

### 3.2 逐条验证

| Handoff A3 要求 | 结论 | 证据 |
|---|---|---|
| 成功 = called && no error && client_unchanged && (items_transferred \|\| cash_moved) | VERIFIED | `TradeService.cs:101–146`：`Called`/`Error` 先判，`tradeProven = ItemsTransferred \|\| cashMoved`（`:127–128`），`ClientUnchanged`（`:130`），最后 `!tradeProven → uncertain`（`:137–146`）。注意执行顺序：`client_unchanged` 先于 `tradeProven`，语义等价。 |
| `business_complete` 仅归因，不得单独证明 | VERIFIED | `business_complete` 已不再参与成功判据，只在响应中作为 attribution 输出（`:156–157`）；`R5_TRADE_01`（business_complete=true + 零移动 → uncertain/no_trade_evidence）、`R5_TRADE_05`（换顾客 → client_changed）PASS。 |
| R4 partial trade 仍 completed | VERIFIED | `R5_TRADE_02`（同顾客连续两笔 partial sale 各自 completed）、既有 `R4_TRADE_01`（business_complete=false + items_transferred=true → completed）PASS；旧断言未删。 |
| 买入方向 staged pointer 不变 + 现金变化 = 证据 | VERIFIED | `R5_TRADE_03`（items_transferred=false、cash 700→675 → completed，`evidence.staged_item_id_after` 仍为买入 item）PASS。 |
| barter 零现金 | VERIFIED | `R5_TRADE_04`（items=true、cash=false → completed、`cash_moved=false`、`cash_changed=false`）PASS。 |
| 预算预检只用玩家可见字段 | VERIFIED | `GameTradePort.cs:55–57`：`client.useClientBudget`、`GameState.GetClientBudget(client)`、`SafeNegociatedValue(staged)`；前两者即快照 `client.uses_budget/budget` 字段（`StateSnapshot.cs:323/331`、`SnapshotFieldMap.cs:179/187/240`），三者均有既有 adapter contract 覆盖（`PawnMind.AdapterTests/Program.cs:113/131/325`）。 |
| 预算预检与 native 谓词一致 | VERIFIED（对反编译源码） | native：`StoreClient.HasBudgetLeftToBuy(GameItem)` = `!useClientBudget → true; GetNegociatedValue() > clientBudget → false`（`_decomp/StoreClient.cs:684–696`）；`PlayerStore.CanSellThisItem` 在 `useClientBudget` 时调用它（`_decomp/PlayerStore.cs:2626`），`SellItemFromTable → CanSellItemFromTable(ignoreBudget=false) → CanSellThisItem`（`:2789–2795`、`:2641–2644`）。C# mirror：`selling && ClientUsesBudget && StagedItemValue > ClientBudget`（`TradeGuard.cs:168–172`）——字段/比较方向/严格大于完全一致；`GameState.GetClientBudget` 与 native `GetBudget()` 均返回同一 `clientBudget` 字段，`GetCurrentClient()` 与 native `currentClientInstance.GetClientBlueprint()` 是同一 `StoreClient` 实例（`GameState.cs:102–114`、`_decomp/StoreClientInstance.cs:22–25`）。`GameState.CanSellItem` 特意用 `CanSellThisItem(item, true)`（ignore budget，`GameState.cs:754`），所以新预检不被 B16 遮蔽、确实可达。 |
| 拒绝发生在 dispatch 之前 | VERIFIED | `TradeService.Confirm`：`Read → TradeGuardPolicy.Evaluate → Refusal` 全部在 `_port.Accept()` 之前（`:75–99`）；`R5_TRADE_06` 断言 `error=client_budget_insufficient`、`dispatched:false`，且边界 `value == budget` 可成交、买方向不被顾客预算拦截。 |
| 缺预算不猜、fail-closed | VERIFIED | 价格读取异常 → `long.MaxValue` → 卖方向必然拒绝（`GameTradePort.cs:68–79`）；`useClientBudget=false` 时不比较（与 native 一致）。 |
| `R5_TRADE_07` uncertain 不可新 ID 盲重试 | VERIFIED | 同 op+item+args 指纹阻塞：`R5_TRADE_07` 第二次新 id → `operation_pending`，native click 仍 1。 |
| 既有断言未弱化 | VERIFIED | numstat 215/0；`PP_TRADE_*`/`R4_TRADE_*` 原样全绿（见 §0.1 套件 825/0）。 |

### 3.3 A3 残留风险

- **R-A3-1（低）**：成功证据只要求“现金动了”，不校验方向（卖应增、买应减）。由于证据窗口内只有一次 native accept，且 `ClientUnchanged` 同实例，实际误归因概率很低，但仍是理论缺口；建议后续加 direction 断言或至少在 evidence 中显式记录 `cash_direction`。
- **R-A3-2（测试细节）**：`R5_TRADE_06` 的不足预算分支只断言响应 `dispatched:false`，未直接断言 `port.AcceptCalls == 0`（另外两个边界分支有断言）。代码顺序保证不 dispatch，属测试表达瑕疵而非行为风险。
- **R-A3-3（handoff 已知）**：`TryReinspectBeforeSale` 等其它 native silent-return 分支仍无 guard（与 R4 相同）；真机预算拒绝/partial trade 行为 **L3 NOT_RUN**。
- `business_complete` 在 `TradeGuard` 里仍会使 confirm_trade 被拒（B11 旧行为，R4 既有）；本审计未发现 A3 改动与它冲突（测试 fake 已绕开该前置）。

证据等级：L1 行为 **VERIFIED**；native 谓词一致性 **VERIFIED（只读反编译）**；真机 **NOT_RUN**。

---

## 4. A4 — validator / Skill / benchmark 协议漂移

### 4.1 读了什么与跑了什么

- `agent/trace_validator.py` 全文关键段：模块 doc（`38–158`）、版本常量（`165–189`）、协议词汇/关系表（`195–260`）、`_declared_schema_version`/`_valid_schema_version`（`388–401`）、`classify_pending_relation`（`432–485`）、`_trade_evidence_v1/v2`（`522–614`）、`_observe_write` 的 pending/refuse/retry 逻辑（`807–1068`）、`_track_run_change`（`1070–1109`）、`_check_trade_evidence`（`1111–1146`）、async day claim（`1411–1459`）、`validate_payload`（`1502–1537`）；
- `agent/tests/test_trace_validator.py`：schema/legacy/conflict/trade/fixture/CLI 测试（`1020–1662`），重点 `TraceSchemaVersionTests`、`ConflictRelationUnitTests`、`CalibratedPendingTests`、`TraceFixtureTests.EXPECTED_CODES`（`1598–1637`）；
- `agent/tests/fixtures/*.json` 6 个 fixture（全部 schema_version=2）；
- `skills/pawnmind-first-week/SKILL.md`（§1.2 `59–73`、§3 字段表 `147`、§5.2、§7.2、§9、§13）；
- `docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md`（§2 可复现性 `45–60`、`spec_version 2026-11-19-r5` `313`、BENCH-G3/G4/G5、§11 schema 表 `405–407`）；
- `docs/pawnmind-known-limitations.zh-CN.md`（§11 `119–178`）；
- 运行：`python agent/trace_validator.py <fixture>` ×6、`-m unittest ... -p 'test_trace_validator.py'`、`-p 'test_skill_tool_refs.py'`、全套 Python 415。

### 4.2 逐条验证

| Handoff A4 要求 | 结论 | 证据 |
|---|---|---|
| validator 规则匹配 A3 实际字段名 | VERIFIED | v2 规则（`trace_validator.py:550–614`）读取的顶层字段 `client_unchanged`/`items_transferred`/`cash_moved`（兼容 `cash_changed`、`cash_before/after`）与 `evidence.{called,client_unchanged,items_transferred,cash_moved,cash_before,cash_after}` 与 C# `TradeService.cs:148–169, 186–241` 输出的字段逐名一致；`business_complete`/`visit_complete` 仅作归因、不参与判据。 |
| 完成判据与 A3 语义一致 | VERIFIED | `_trade_evidence_v2`：`client_unchanged is True` + `(items_transferred is True or cash_moved is True)` + evidence 对象一致性；visit-complete 零移动被 flag。`trade_partial_ok` exit 0、`trade_visit_complete_no_effect_invalid` exit 1/`TRADE_EVIDENCE_MISSING` 实测吻合。 |
| schema v2 + legacy 迁移策略（不静默误读旧证据） | VERIFIED | `TRACE_SCHEMA_VERSION=2`、`LEGACY=1`（`:165–172`）；无版本→按 v1 规则校验，若 v1/v2 判决不同则加 `TRACE_SCHEMA_VERSION_MISSING`(warning)（`:1134–1146`）；显式 v1→v1 规则且不告警；不支持版本→仅返回 `TRACE_SCHEMA_VERSION_UNSUPPORTED`(error) 并停止解释（`:1515–1527`）。单测覆盖三种路径（`test_trace_validator.py:1068–1117`），全套 415 OK。 |
| CONFLICTING_WRITE_WHILE_PENDING 校准 | VERIFIED | 6 类关系显式枚举（`:205–217`）；只有 `independent`（call-scoped trade 且 item 可证明不同）与 `modal_resolution`（解别类 modal 的对话/prompt 写）允许，其余冲突、`unknown` 永远 ERROR（`:432–485`、`:1023–1068`）；与 bridge 的 `PendingScope.IsCallScoped` 仅 `confirm_trade` 对齐（`VALIDATOR CALL_SCOPED_OPS` vs `RunChangePolicy.cs:112–117`）。单测含 different-item 放行/same-item 冲突/unknown 保守/modal 放行/同 item 交易冲突，fixture 精确断言 relation=same_item。 |
| `press` / sleep / `get_operation("*")` 文档修订 | VERIFIED | Skill §7.2 明确“day 不变 = inconclusive，不是失败”“uncertain 非终态、拒绝新 id”“只有 native_not_called+dispatched:false 可安全重试”“禁止连续轮询 N 次即判失败”（`SKILL.md` §7.2）；§3 明确 `get_operation("*")` 上限 20 + `truncated` 语义；§1.2 明确 `press` 仅 test-only、正式 Play 永不依赖、若“必须 press”就停止报告。 |
| §5.2 business_complete 漂移与 §1.2 “整局只调一次”已修 | VERIFIED | §5.2 改成 `client_unchanged && (items_transferred \|\| cash_moved)` + evidence 一致，并明确 partial trade 仍有效（`SKILL.md` §5.2）；§1.2 拆分 prompt 确认/真实 start 两个子动作并写明“旧口径已废止”（`68–73`）。 |
| benchmark 规范区分 `spec_version` 与实测环境 | VERIFIED | `spec_version: 2026-11-19-r5`（`:313`）与“spec_version 只标识规范文本版本，不是实测环境”（`:7`）、§2 明示右列为历史起草基线、run 时实算（`:45–60`）；旧 HEAD/policy/tool 数均标注仅历史参考；BENCH-G3/G4/G5 与 `BENCH_TRADE_EVIDENCE_INVALID` 已同步 A3 口径。 |
| 6 fixture 断言精确 code set | VERIFIED | `EXPECTED_CODES` 精确逐 fixture（`test_trace_validator.py:1598–1607`）、`test_fixtures_validate_to_the_exact_code_set`/`test_fixture_cli_exit_codes_and_schema_version`（`:1614–1637`）；审计者自行 CLI 复跑得到 4×0+2×1，code 与 relation 均吻合（§0.1）。 |
| 独立单测数量/全量 Python | VERIFIED | validator 115 OK、skill-refs 16 OK、全量 415 OK，均 exit 0。 |

### 4.3 文档精度 WARN（不影响 A 阶段判据，但应随 B/C 文档 pass 修正）

- **W-A4-1（Skill/benchmark 说 load 响应带 `run_change`，代码没有）**：`SKILL.md:70–71` 写 `load_recording_game()` 响应 `run_change:"issued"` + `run_change_status`；`SKILL.md:147` 与 `FIRST_WEEK_BENCHMARK_SPEC.md:405` 也把 `run_change` 列为跨局写响应字段。实际 `RecordingSaveService.Load` 成功行（`RecordingSaveService.cs:603–614`）只有 `load_requested`，全库仅 prompt 行（`:500`）与 `StartSuccess`（`:624`）/Failure claim（`:898`）会写 `run_change`。**协议安全**：validator 对无 claim 的 load 回退 op-name 判定（`trace_validator.py:1086–1109`），行为保守正确；只是文档让调用方期待一个不存在的字段。建议二选一：给 `Load` 成功/失败行补 `run_change` claim（与 StartSuccess 对称），或把文档改为“load 的跨局事实由 `run_change_status` 表达”。**不影响 A1 修复与门禁。**
- **W-A4-2（known-limitations 与同快照代码不一致，Lead 已接受）**：`docs/pawnmind-known-limitations.zh-CN.md:170` 写“日终 typed 玩家等价动作（R5/B2）**未实现/未验证**（NOT_RUN）”，但 `a0ac8848`（B2 typed negotiation-tablet confirmation）是 `5b7e2cff` 的祖先，B2 的 R5_MODAL 测试与 adapter contract 已在该快照合入（本审计实测 contract 515、R5_MODAL 全绿）。Lead 说明 B2 只是先于 A4 docs commit 合入、A4 文档按当时理解书写，将在 B/C 文档 pass 修正措辞；本项记为 **WARN（已接受）**。同段“源码注释漂移由 Lead 负责同步”也已过时——`c3f34bb6` 已更新 `UiPressPolicy` 注释（描述第四个日终路径且标 test-only）。
- **W-A4-3（提交信息笔误，非交付物）**：`5b7e2cff` commit message 写 fixture CLI “0/0/0/0/0/1”，与其正文“4 exit 0, 2 exit 1”及实际测试/审计复跑（4×0+2×1）不符；代码、测试、fixture 均正确，仅提交信息序列笔误，记为 WARN。
- **W-ENV-1（仓库既有卫生问题，非 R5-A 引入）**：仓库 index 中 `test.sh`、`test/ContractGate.Tests/run.sh` 均为 `100644`（`git ls-tree` 实测），而 `test.sh` 用 `[ -x run.sh ]` 作为 gate 开关（`test.sh:71–77`）。在干净 Linux checkout/worktree 上：`./test.sh` 无法执行，`bash ./test.sh` 会**静默跳过** 53 项 contract gate（exit 仍 0）。审计在临时 worktree 内 `chmod +x` 后复跑才得到 `gate 53/0`；未改仓库文件。建议后续把脚本加执行位（`git update-index --chmod=+x`）或把 gate 判定从 `-x` 改为 `-f`，否则 CI/他人复跑可能悄悄少跑门禁。

证据等级：validator/Skill/benchmark 规则与文档修订 **VERIFIED**（实跑 + 文本核对）；RED 证据（A4 报告称实现前 13 红/15 红等）**REPORTED**（未在历史态重放）；真实首周轨迹校验 **NOT_RUN**（E2 原始轨迹缺失）。

---

## 5. “修复是否被藏起来”专项检查

审计重点是“错误被吞/断言只验一半/文档跑在代码前面”，逐项结论：

1. **过宽 try/catch 是否把错误吞成成功** — VERIFIED 无假成功：
   - `ActionRouter.TryRefreshRevision` catch → 返回 false，路由进入 `outcome_unknown`（`ActionRouter.cs:1003–1017`、`:282–302`），不会假完成；
   - `SafeReadRunId` catch → `null` → `Classify` 返回 `unknown`（`:609–621`、`RunChangePolicy.cs:77–79`）；
   - `TradeService` 对 `_port.Read`/`_port.Accept` 异常 → `Refusal(ReadFailed)` / `Uncertain(outcome_unverified, native_error)`（`TradeService.cs:76–99`），never ok:true；
   - `GameTradePort.SafeNegociatedValue` catch → `long.MaxValue`（fail-closed 拒绝卖单，`GameTradePort.cs:68–79`）；
   - `ActionResult.SuccessRaw` JSON 解析失败 → `bad_response` + `Ok=false`（`ActionResult.cs:124–153`）；`ReadRunChangeClaim` 只接受两个精确字符串，伪造/缺失 → 无 claim → 保守 op-name（`:177–190`）；
   - `ApplyRunChange` 的 callback catch 只吞 bookkeeping 异常，不改变已确认事实（`ActionRouter.cs:597–606`）；`Verify` 的 verifier catch 保持原记录、不伪造成功（`:546–557`）。
2. **测试是否只断言 `ok:false` 而不验 code** — VERIFIED 否：新增反例均断言稳定 code/status，例如 `R5_TRADE_01` 断言 `outcome_unverified` + `detail=no_trade_evidence` + `business_complete` 归因；`R5_SLEEP_01/03` 断言 `execution/error`；`R5_TRADE_05` 断言 `client_changed`；`R5_TRADE_06` 断言 `client_budget_insufficient`；A4 负例断言精确 code set（含 relation）。
3. **终态/非终态是否搞反** — VERIFIED：`IsTerminal = completed|rejected|failed`（`OperationStatus.cs:44–47`）；`uncertain` 保持非终态并由 R5_SLEEP_03 实测证明会阻塞新 id、且后续可 promote；A2 无任何 count-based `failed` 路径残留（源码 grep）。
4. **validator 字段名与 C# 响应是否漂移** — VERIFIED：v2 规则字段与 `TradeService`/`Router` 实际输出逐名对齐（§4.2）；fixture 由 validator 本机 CLI 实测；未发现 `TRADE_EVIDENCE_MISSING` 对合法 partial trade 误报（`trade_partial_ok` exit 0）。
5. **固定恢复/防盲重试是否被削弱** — VERIFIED：`R5_RUN_03/04/05`、`R4_F1_08/12/13` 全绿；`c3196301` 的 `FindPendingFor` skip **仅**对 `ClaimedNotIssued`（服务显式 `not_issued`）生效，真实 start/load 无该标记；`R5_TRADE_07` 证明 uncertain trade 的同指纹新 id 仍被拒。
6. **文档是否跑在代码前面** — 发现 2 个 WARN（W-A4-1 load `run_change`；W-A4-2 known-limitations B2 表述），见 §4.3；均不影响协议判据。
7. **测试只测 happy path？** — VERIFIED 否：A1 覆盖拒绝/未确认/不可读/load；A2 覆盖超时/未派发/截断/跨局 reset；A3 覆盖 visit-complete 无效果/换顾客/预算不足/边界/盲重试；A4 覆盖 legacy/unsupported/6 类 relation/正反 fixture。

---

## 6. 证据分级汇总

### 6.1 VERIFIED（本次实际运行或实际读源码+行号）

- 门禁：`c3f34bb6` 812/0 + gate 53/0 + contract 506；`5b7e2cff` 820/0 + 53/0 + 515；`c3196301` 825/0 + 53/0 + 515；全部 exit 0。
- Python：`5b7e2cff`/`c3196301` 均 415 OK exit 0；validator 115 OK、skill-refs 16 OK。
- Fixture CLI 6 个：4×0 + 2×1，codes/relation 与期望一致（审计者自行核对）。
- A1：R5_RUN_01..06 全绿；F-A1-1 pre-fix 独立复现（`5b7e2cff`）与 post-fix 独立复验（`c3196301`）；R4 防护回归全绿；claim 解析与 gate 跳过范围源码核对。
- A2：R5_SLEEP_01..06 全绿；无 count-based failure（源码）；注入时钟/truncation/uncertain 非终态/reset 接线（源码）。
- A3：R5_TRADE_01..07 与既有 PP/R4 交易测试全绿；完成判据/预算预检源码；native 预算谓词与 `_decomp` 反编译源码逐字段核对；`TradeServiceTests` diff 215/0。
- A4：validator 1.2.0/schema 2 规则、legacy 策略、conflict 校准、run_change 集成、6 fixture 精确断言、Skill §1.2/§5.2 修订、benchmark spec 分离——全部实跑/实读文本核对；`spec_version` 与历史基线分离文本核对。
- 环境：主机/dotnet/python/DLL sha 实测与 R5_BASELINE 一致。

### 6.2 REPORTED（开发者声明，本次未独立复现）

- 各测试的 **RED 证据**（实现前失败的具体报错序列）：A1 部分由审计者用 pre-fix 快照独立复现（F-A1-1）；其余（A2/A3/A4 的“先红”记录）为开发者报告，未在历史态重放。
- `c3196301` commit message 中“含另一 teammate 未提交 B1 staging 的工作树跑出 861/0”：**REPORTED**；审计在干净 `c3196301` 上实测 825/0（数字差异可由未提交 B1 测试解释，但不作为 A 阶段证据）。
- R4 真机结论（partial trade、买卖两向、租约回执等）：**REPORTED**，来自历史报告，本次无原始轨迹可复核（R5_BASELINE E1/E2）。
- `R5_A_TEST_MATRIX.md`（`d796faee` 提交，晚于被审计 A 提交）中的数字：其 A 段数字（812/415/115/53/506）与本次实测一致；其中 `R5_TRADE_06` “0 次 native click”表述略超测试实际断言（见 R-A3-2）。

### 6.3 NOT_RUN（环境/授权）

- 所有 Windows L3：真机 first-run prompt/真实开局/load、sleep 异步黑屏真实时序与双推进、真实预算拒绝与 partial trade、日终模态、扫描器/Workshop、存档写、游戏进程内行为。
- trace_validator 对**真实首周轨迹**的校验（历史原始轨迹缺失，E2）。
- `_internal/week.py` 复核（缺失，E3）。

---

## 7. 残留风险与建议（汇总）

| ID | 级别 | 说明 | 建议 |
|---|---|---|---|
| R-A1-1 | 低/设计内 | 真实 start/load 在 post-dispatch 状态不可读时保持 uncertain 并阻塞后续 run-change（防盲重试），需人工/重启 | 保持；在 Skill/运维文档写明“查询无解即停机人工介入” |
| R-A1-2 | 低 | router 层“菜单拒绝后不阻塞下一次 run-change”只有服务层测试覆盖 | 可加一条 router 级 refused-prompt 用例 |
| R-A2-1 | 中（L3） | 10s 观察窗未经真机校准 | L3 记录 sleep→day 实际耗时后再定窗口 |
| R-A2-2 | 中（L3） | 进程重启后旧 sleep/operation 不可观测 | 维持 unknown_operation + 人工介入，勿自动重放 |
| R-A2-3 | 低 | Mod 层 run-confirm→ResetSleepTracking 接线无端到端单测 | 加一条 Mod 层（或 callback 注入）串联测试 |
| R-A3-1 | 低 | 现金变化证据不校验方向 | 增加 direction 断言或 evidence 字段 |
| R-A3-2 | 低 | `R5_TRADE_06` 未直接断言 `AcceptCalls==0` | 补 `Assert.Equal(0, port.AcceptCalls, ...)` |
| R-A3-3 | R4 既有（L3） | `TryReinspectBeforeSale` 等 silent-return 分支无 guard | 记录为已知限制，L3 抽验 |
| W-A4-1 | 文档 | Skill/benchmark 称 load 响应带 `run_change`，代码未发 | 补 Load claim 或改文档措辞 |
| W-A4-2 | 文档 | known-limitations §11.4 称 B2 typed 日终“未实现”，与同快照代码不符；Lead 已接受，B/C 文档 pass 修正 | 修正措辞（实现=已合入，L3=NOT_RUN） |
| W-A4-3 | 提交信息 | A4 commit message fixture 退出码序列笔误 | 无需回改历史，可在后续说明 |
| W-ENV-1 | 仓库卫生（既有） | `test.sh`/`run.sh` 在 git index 中非可执行；干净 checkout 会静默跳过 53 项 gate | `git update-index --chmod=+x` 或 gate 改用 `-f` |

---

## 8. 审计判定

- **A1 PASS**（主路径 + 修复 c3196301 复验；审计发现 F-A1-1 pre-fix FAIL 已闭环）。
- **A2 PASS**（L1；L3 NOT_RUN）。
- **A3 PASS**（L1 + native 谓词反编译核对；L3 NOT_RUN）。
- **A4 PASS**（协议/文档漂移主体已修；3 个文档/提交信息 WARN + 1 个仓库卫生 NOTE，均不阻塞 A 阶段门禁）。
- **A 阶段退出门槛：满足**（含 A1 follow-up 修复）。B/C 阶段建议在文档 pass 中收口 W-A4-1/W-A4-2，并在真机授权前不要宣称任何 L3 结论。

> 审计声明：本文件为 reviewer-a 本次唯一写入；未修改生产代码/测试，未提交 git。所有“VERIFIED”均可在本次会话命令与 `_internal/logs/` 原始输出中复核；“REPORTED/NOT_RUN”均按上表显式区分，未把开发者数字当作自己执行的结果。
