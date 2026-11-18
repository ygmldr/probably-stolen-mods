# ROUND4_DISPOSITION — 2026-11-18 外部执行指令（F1b/F4b/F3b/F5b/F7）

> 工作契约：`docs/PAWNMIND_ROUND4_AGENT_EXECUTION_BRIEF_2026-11-18.md`
> 复核基线：`4a5b8d1`（brief 指定）；接手时 HEAD 未变，无需 `git diff 4a5b8d1..HEAD`
> 代码提交：`fix(round4): confirm run changes and make dispatch explicit`（见 §7）
> 证据等级：SRC / OFFLINE / CI / LIVE / INFERRED；本轮 **LIVE = NOT_RUN**

## 0. 总览

| ID | 优先级 | 处置 | 证据 |
|---|---|---|---|
| **F1b** 提前驱逐旧 run 证据 | P0 | **FIXED** | SRC + OFFLINE（`R4-F1-01…09`） |
| **F4b** 派发事实靠存在性猜测 | P0 | **FIXED** | SRC + OFFLINE（`R4-F4-01…07`）+ 48 工具矩阵 |
| **F3b** 晋升重试证据自相矛盾 | P1 | **FIXED** | SRC + OFFLINE（`R4-F3-01…05`） |
| **F5b** MCP auto 回退到 test | P1 | **FIXED** | SRC + OFFLINE（`R4-F5-01…06`） |
| **F7** 官方 ModScanner 与发布兼容 | P0 | **BLOCKED** | SRC（静态）+ LIVE **NOT_RUN**；Gate C 见 §5 |

Gate：**A = PASS**、**B = PASS**、**C = BLOCKED**、**D = NOT GRANTED**（§6）。

---

## 1. F1b — 区分"跨局请求"和"跨局完成"

**观察到的事实（复核成立）**：`DispatchAction` 在 `result.Ok && IsRunChangeOp(op)` 时
立即 `ResetRentReceipts()` 并 `WithRunChanged()`；而 `confirm_new_game` 的
`prompt_confirmed:true`、`load_game` 的 `load_requested/load_pending`、
`new_game` 的 `start_requested/start_pending` 都可能 `ok:true` 却**尚未切换 run**。
`ActionRouter` 随即 `ClearOperationsExcept`，旧 run 的证据被提前丢弃。

**实现**（最小侵入，未新建模块）：

- `src/Core/RunChangePolicy.cs`：`RunChangeOps.IsRunChangeOp/Resolve`、
  `RunChangeStatus{NotRequested,Requested,Confirmed,Unknown}`、
  `RunChangePolicy.Classify(before, after)`。**唯一**的跨局 op 清单与判定函数。
- `OperationRecord`：`RunChangeRequested` / `RunChange` / `RunIdBefore` /
  `RunChangeApplied`。
- `ActionRouter`：派发**前**采样原生运行身份（`Func<string> readRunId`），
  派发**后**再采样并按 `Classify` 定状态；`ApplyRunChange` 只在
  `confirmed && !applied` 时驱逐并触发一次回调。
- `GetOperationJson`/`DecorateCached` 在异步完成时补上确认
  （`VerifyRunChange`），因此 `load_game` 的"稍后才切换"也能被确认。
- `PawnMindMod`：不再在请求时重置收据，改为注入 `ResetRunBoundEvidence` 回调，
  仅在确认后执行一次。
- `GameState.ReadRunId()`：读 `PlayerStore.runID`；不可读返回 `null`
  （→ `unknown`，**不**当作"没变"）。
- `ActionGate.FindUnconfirmedRunChange()`：未确认的跨局操作存在时，新的跨局写返回
  `run_change_pending`，杜绝"新 action_id 盲重试"。

**为什么不是 `if (!pending)`**：pending 只是"服务端的异步标记"，不是身份证据。
`R4_F1_07` 固定了"只有 rev 变化、身份不可读 ⇒ unknown，不驱逐"。

**残余（OPEN，记录而非隐瞒）**：未确认期间**普通写**不被阻止（避免加载画面死锁）。
风险与缓解见 `docs/audit/RUN_CHANGE_EVIDENCE.md` §5。

---

## 2. F4b — 派发证据显式化

**观察到的事实（复核成立）**：`Classify` 缺字段时用
`result.RawJson != null || result.Ok`；`DispatchedClaim` 缺字段/解析异常都返回 `true`；
`NewGameMenuJson.SelectJson/ActionResultJson` 完全不写 `dispatched`；
`Dispatch` 的 catch 返回 `Failure`（被读成"未派发"）。

**实现**：

- 三态 `DispatchStatus{NotDispatched,Dispatched,Unknown}`（Core）。
- `ActionResult.Dispatch`：`Success()`→dispatched、`Failure()`→not_dispatched、
  新 `Failed()`→dispatched、新 `UnknownDispatch()`→unknown；
  `SuccessRaw` 只认 JSON 布尔，缺失/非法 = **unknown**。
- `Classify` 只读 `result.Dispatch`，删除 `DispatchedClaim`。
- `ExecutionStatus.Classify(..., string dispatch)`：`!ok && unknown → uncertain`
  （**不再** `rejected`），`!ok && dispatched → failed`，`!ok && not_dispatched → rejected`。
- 线格式：`dispatch_status` + `dispatched`（true/false/**null**）。
- `FindPendingFor` 用 `MayHaveRun`：unknown 与 dispatched 一样阻止重复原生调用。
- `ActionRouter.Dispatch` catch → `UnknownDispatch`。
- **48 个写工具的生产者全部显式声明**（`docs/audit/RAW_DISPATCH_AUDIT.md` §2）；
  `GameActions` 6 处、`InteractionService` 3 处调用后失败由 `Failure` 改成 `Failed`；
  `NewGameMenuService/Session` 新增 `Dispatched` 字段并在调用端口前/后正确置位；
  `NightReturnService` 的 `return_home` 成功行补上标记。

**Red 证据**（把三处语义临时还原成旧实现后运行，见 §7 的脚本说明）：

```
R4-F4-03  expected=uncertain, actual=failed        （旧：缺字段→true→failed）
R4-F4-04  expected=uncertain, actual=failed        （旧：非布尔→true→failed）
R4-F4-05  missing "dispatched":false               （旧：菜单 JSON 无该字段）
R4-F4-06  expected=unknown,  actual=not_dispatched （旧：异常→Failure）
```

---

## 3. F3b — 晋升后的重试证据契约

**观察到的事实（复核成立）**：`DecorateCached` 每次重试都从旧缓存重新装饰；
`shop_closed_after` 被无条件写成 `true`，而同层的 `readback`/`can_end_day_after`
仍是派发时的快照 → 模型同时看到"已完成"和"OPEN"。

**本轮契约（brief §4.2 的第一选项）**：

1. 晋升 = 缓存体里存在 `pending|uncertain|unknown` 且当前为 completed。
2. 晋升时把**整份派发时响应**原样放进 `initial_result`（`initial_result_note` 说明其语义），
   顶层**只**保留可证事实：`ok`/`op`/`operation_id`/`execution_status`/
   `delivery_status`/`dispatch_status`/`dispatched`/`completion_evidence`。
3. 不再伪造 `shop_closed_after:true`；完成证据来自 verifier 的
   `completion_evidence = {source:"shop_day_readback", shop_state:"CLOSED", readable, in_game, observed_rev}`。
4. 因为总是从**不可变缓存体**重建，重试是确定性的；`initial_result` 会先删掉自身
   （防御）因此 10 次重试不增长、不嵌套。
5. `delivery_status` 仍独立于执行轴（保留 F3 原修复）。

**与上轮文档的差异（重要）**：上轮"`initial_result` 只出现一次"的表述**不准确**，
本轮改文档而不是改行为：**晋升后每一次相同 `action_id` 的重试都会带同一份
`initial_result`**，它是"派发时证据"，不是"一次性出现"。

**Red 证据**：

```
R4-F3-01  顶层仍残留 pending            （旧：不重建）
R4-F3-02  顶层仍带派发时 readback        （旧：只把 shop_closed_after 改 true）
R4-F3-04  initial_result 缺失            （旧：根本没有该字段）
R4-F3-05  NullReference（initial_result 为 null）
```

---

## 4. F5b — MCP 模式故障关闭

**观察到的事实（复核成立）**：`_detect_mode` 在 bridge 不可达/健康未知时返回 `test`，
而 `allowed_tools_for_mode("test")` 返回 `None`（不过滤）→ 116 工具全暴露。

**实现**（`agent/mcp_server.py`）：

- 新增 `LOCKED_MODE = "locked"`；`allowed_tools_for_mode("locked") → set()`（只剩本地 memory 工具）。
- `_resolve_mode(client, requested)`：
  - `auto` → bridge 报告的 mode；不可达/缺失/未知 → `locked`；
  - 显式 mode → **必须**与 bridge 报告一致，否则 `locked`。
- `test` 不再能"离线开启"：必须有 `--mode test` **且** bridge 真的报告 `test`。
- `_ModeGuard`：非本地工具调用前复查 `/health`（默认 2 秒缓存），
  模式漂移时返回 `{"ok":false,"error":"mode_changed", ...}`，
  要求重启 MCP，而不是继续用过期注册表；`functools.wraps` 保证 schema 不变。
- 启动时 `stderr` 打印锁定原因。

**Red 证据**（把 `allowed_tools_for_mode`/`_resolve_mode`/`_ModeGuard` 还原成旧实现后运行）：

```
R4-F5-01  不可达 bridge 仍暴露 116 工具
R4-F5-02  缺失/未知 run_mode 仍暴露 116 工具
R4-F5-04  --mode test 覆盖了 bridge 的 play
R4-F5-05  漂移后仍继续调用（拿到 policy_unverifiable 而不是 mode_changed）
```

`R4-F5-03`（play 空 allowlist）与 `R4-F5-06`（bootstrap 隐藏 6 个快照读者）
是**回归守卫**：旧实现也通过，保留是为了防止未来放宽。

**测试夹具改造**：因为"注册面取决于 bridge 报告的 mode"，所有依赖 bridge 的 Python
测试夹具现在必须报告 `run_mode`（`agent/tests/mode_fixture.py` + `FakeGame.health_payload`）。
这不是放宽断言，而是让夹具符合新的握手契约。

**未改动 C# 权限**：`ModePolicy`、`PlayOps`、`play_allowlist`（仍为 **0**）一行未动；
C# 仍是最终门禁。

---

## 5. F7 — 官方 ModScanner

- `mod-scan` 原始输出：**NOT_RUN**（Agent 不启动游戏；用户按 F8 时 `dev.txt` 缺失，
  已补建，待重启后采集）。
- **`files` 预测已被 LIVE 确认**：游戏加载器的审计行在 2026-11-18 23:21 报告
  `caps=files, network, reflection-external`，而 `manifest.xml` 只声明后两者；
  当前能运行仅因 `trust=DeveloperMode`。普通玩家机器上 `detected ⊄ declared` → 拒绝激活。
- 静态预测与完整 disposition：`docs/audit/MOD_SCANNER_ROUND4.md`（§3 预测、§5 LIVE）。
- `reflection-external` 已声明，但 `NewGameMenuAdapter.cs:400` 的
  `view.GetType()` 是 computed type，属于官方**计划于 2026-12-10 拒绝**的能力。
  `SlotView` 是私有嵌套类（`_decomp/NewGamePerksUI.cs:7`），没有 `typeof()` 替代。
- 已安装 DLL 已在本轮更新为构建产物（`6adef8ac…` / MVID `4e3a88c5…`，与 `dist` 一致，
  游戏未运行、未启动、未碰存档）→ **R4-F7-04 PASS**；旧版备份于
  `_internal/install-backup-2026-11-18/`。
- **Gate C = BLOCKED**，`Workshop = BLOCKED`。不得用静态分析替代。

---

## 6. Gate 判定

| Gate | 条件 | 结果 |
|---|---|---|
| **A** 操作事实正确性 | `R4-F1-*`、`R4-F4-*` 全绿；无提前清理；无派发猜测；旧 F1/F4 回归通过 | **PASS** |
| **B** 协议与工具暴露 | `R4-F3-*`、`R4-F5-*` 全绿；重试证据无矛盾；MCP 故障关闭；C# 门禁未改 | **PASS** |
| **C** 发布兼容 | 官方 `mod-scan` 完整证据 + 已安装 DLL 匹配 | **BLOCKED**（DLL 匹配已满足，扫描证据仍缺） |
| **D** Play / 首周租金 | 既定 live gates 独立通过 | **NOT GRANTED**（本轮默认不授予） |

`play_allowlist` 仍为空；未因本轮全绿而填入任何 `live_evidence`。

---

## 7. 复现

```bash
cd PawnMind
./test.sh                                  # 775 passed / 0 failed；门禁 53
./test.sh Round4                           # 21 个 R4 用例
.venv/bin/python -B -m unittest discover -s agent/tests   # 377 OK
.venv/bin/python tools/gen_policy_catalog.py --check
PAWNMIND_BUILD_ONLY=1 ./build.sh           # 编译门禁（含 src/Game）
cd .. && python3 -B scripts/security/scan_git_secrets.py
```

Red 证据的采集方式（不修改历史，只是临时还原语义后运行、再还原）：

- F4b/F3b：把 `ActionResult.SuccessRaw` 的默认值改回 `Dispatched`、把
  `Classify` 的 unknown 归一到 dispatched、把 catch 改回 `Failure`、
  关掉 `initial_result` 重建与 `completion_evidence`、移除菜单 JSON 的 `dispatched`，
  运行 `./test.sh Round4` 并记录 9 个失败，然后还原。
- F5b：把 `allowed_tools_for_mode`/`_resolve_mode`/`_ModeGuard` 还原为旧实现，
  运行 `ModeLockdownTests` 记录 4 个失败，然后还原。

两处的还原都已撤回；仓库中不存在"测试专用旁路"。
