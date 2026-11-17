# 外部审阅回应（2026-11-17，基线 `580f200`）

> 审阅来源：独立审阅，范围 PawnMind（C# ActionRouter/ActionGate/Operation、RunMode/Policy、
> Trade/EndDay、Python MCP/Client、CI、Manifest）。
> 处理提交：`509bfd1`（F1–F6 修复与回归）。本文件逐条给出**核实结论**与证据，不复述审阅原文。

## 总览

| 编号 | 优先级 | 审阅结论 | 核实 | 处置 |
|---|---|---|---|---|
| F0 | P0 | Python CI 失败 | **已过时** | 基线 `580f200` 之后已修复（`003a3f4` + `4cd6ba2`），CI 在 `4cd6ba2`、`05ecf01` 均绿 |
| F1 | P0 候选 | 切换存档清空当前 operation | **成立** | 已修（`509bfd1`），新增回归 |
| F2 | P1 | end_day verifier 把非 OPEN 判完成 | **成立** | 已修，`ReadbackProvesEndDay` + 10 条断言 |
| F3 | P1 | 晋升后缓存重试自相矛盾 | **成立** | 已修，旧状态字段清除 + `initial_result` |
| F4 | P1 | `dispatched` 由执行状态推断 | **成立** | 已修，显式传递；JSON 与记录同源 |
| F5 | P2 | `tools/list` 不随模式过滤 | **成立** | 已实现 `--mode` 过滤（C# 仍是最终边界） |
| F6 | P2 | rev 契约测试漏检 | **成立** | 已重写为策略驱动 + 纯函数自测 |
| F7 | 发布 | 兼容性与 L3 | **部分新增证据** | `files` 运行时不需要、扫描器预测仍需；官方扫描器 `NOT_RUN` |

## F0 — GitHub Python CI 失败（已过时）

审阅绑定的 `580f200` 确实红：363 tests / 4 errors，原因是测试把解释器写死成
`PawnMind/.venv/bin/python`（干净 clone 无 venv），以及依赖 gitignore 的
`test-artifacts/audit-bridge.conf`。

修复记录（均早于本回应）：

| 提交 | 内容 |
|---|---|
| `003a3f4` | 三处 `command=sys.executable` |
| `4cd6ba2` | schema 探针改为运行时生成临时 `bridge.conf` |
| `05ecf01` | README 增加两个 CI 徽章 |

证据：`4cd6ba2`、`05ecf01` 的 `python-tests` 与 `secret-scan` 均 `success`
（本地干净 clone 复跑：371 tests OK）。**当前 HEAD 的 CI 是绿的**，请不要用 `580f200` 的运行结论。

## F1 — 运行切换会清空正在完成的操作（成立，已修）

核实路径：`PawnMindMod.DispatchAction` 在 recording 分支里、`recordingResult.Ok` 时直接
`router.ResetOperations()` → `ActionGate.ClearOperations()` → 随后 `HandleWrite` 的
`_gate.Complete(actionId, …)` 因为查不到条目直接返回。当前 `action_id` 的响应与幂等确实丢失。

修复：

- `ActionResult.RunChanged` 标记，`DispatchAction` 只做标记（并立即 `ResetRentReceipts`）。
- `ActionGate.ClearOperationsExcept(keep)`：驱逐旧 run 记录、保留当前条目。
- `ActionRouter.HandleWrite` 在 `Complete` **之后**才驱逐（正常与 `outcome_unknown` 两条分支都覆盖）。
- 运行切换判定移到 `DispatchAction` 包装层，recording 与普通 dispatch 都生效。

回归：`OperationTests.F1_Run_Change_Evicts_Old_Operations_But_Keeps_Current`
（当前 id 可查、旧 id 失效、同 id 不二次派发）。

## F2 — end_day verifier 把非 OPEN 判完成（成立，已修）

原实现 `if (DayEndPolicy.IsOpenState(after.ShopState)) return record;`，因此 `MORNING`、
`null`、`Readable=false` 之外的任何非 OPEN 都被判 `Completed`；`ReadShopDaySnapshot` 在
`!PlayerStore.HasInstance` 时还返回 `Readable=true, InGame=false`。

修复：`DayEndPolicy.ReadbackProvesEndDay(after)` = `Readable && InGame && IsClosed(ShopState)`，
`VerifyOperation` 改用该判定（跨天不再被当作 end_day 完成）。

回归：`DayEndPolicyTests.Readback_Needs_A_Real_Closed_State`（OPEN / CLOSING / MORNING / null /
空串 / 不在游戏内 / 不可读 / null 快照，共 10 条断言）。

## F3 — 晋升后缓存重试自相矛盾（成立，已修）

`GetOperationJson` 的 verifier 更新 `OperationRecord.Execution`，但 `ActionGate.Update`
不更新缓存响应体；重试走 `DecorateCached` 把 `ok` 改成 true，却保留 `pending:true`、
`error:"shop_still_open"`、`shop_closed_after:false`。

修复（选定"当前状态可演进 + 原始证据保留"契约）：

- 完成态重投递时移除 `pending` / `uncertain` / `unknown` / `error`；
- `shop_closed_after` 在完成态置 `true`；
- 原始响应（含旧状态）放入 `initial_result`，只在该次晋升时出现一次。

回归：`OperationTests.F3_Promoted_Retry_Drops_Stale_Pending_Facts`（同时断言 `dispatched`、
`dispatch == 1`、`initial_result` 存在）。

## F4 — `dispatched` 由执行状态推断（成立，已修）

`ActionGate.Complete` 原写法 `Dispatched = Execution != Rejected`，与响应里的
`dispatched:false` 可能矛盾；`Classify` 已经算出了真实声明却被丢弃。

修复：`Complete(..., bool dispatched)`；`ActionRouter` 把 `Classify` 的 `dispatched`
（来自服务的 `dispatched` 字段；裸 `Success` 视为已派发，裸 `Failure` 视为守卫拒绝）传进去，
`DecorateResponse` 与记录使用同一个值。

回归：`OperationTests.F4_Dispatched_Follows_The_Service_Claim`
（`pending:true + dispatched:false` 的记录与 JSON 必须一致）。

## F5 — `tools/list` 不随模式过滤（成立，已实现）

新增：

- `--mode auto|bootstrap|play|test`（默认 `auto`，也接受 `PAWNMIND_MODE`）；
- `allowed_tools_for_mode(mode)`：`play` 只用 `play_allowlist`（live evidence，当前为空），
  `bootstrap` 用 `allowlist(policy,"bootstrap")`，`test` 不过滤；
- `build_server(..., allowed_tools=…)` 在注册阶段过滤，`LOCAL_TOOL_NAMES`（memory_*）永不被隐藏；
- `auto` 读 `/health.run_mode`，桥不可达时回落到 `test`（保持既有行为）。

**C# 仍是最终安全边界**：这层只影响模型能看到什么，不放宽任何写操作。

回归：`test_mcp_tools_mode` 新增 4 条（play 只剩本地工具、bootstrap 不含 6 个快照读取别名、
test 仍 116、`allowed_tools_for_mode` 与策略一致）。

## F6 — rev 契约测试漏检（成立，已修）

原测试 `if "rev" not in props: continue`，因此完全漏掉 rev 的写工具会被跳过。

修复：目标改为从 `tool_policy.json` 的 `purity == write` 生成；缺失 `rev`、`rev` 非必填、
工具未注册三种情况都算违规；并把判定抽成纯函数 `_rev_offenders`，新增其自身单元测试
（缺 rev / rev 可选 / 合法 / 未注册），证明检查器确实会失败。

现状：48 个写工具全部要求 `rev`（修复前也没有反例，属覆盖漏洞而非已发生的线上缺陷）。

## F7 — 发布兼容与 L3（部分新增证据）

- `manifest.xml` 的 `files` 能力：需要区分两层，结论**不是**"肯定不需要"。
  - 运行时层：`ReadModDataFileText` / `WriteModDataFileText` / `ListModDataFiles` 等
    （`_decomp/ModHelper.cs:1489-1556`）都限制在 Mod 自己的数据目录内，**没有能力门禁**；
    只有任意路径访问才需要 `files`。
  - 扫描器层：`_decomp/ModScanner.cs` 的 FileTypes 规则按**程序集引用**判定，
    `System.IO.Directory` / `FileInfo` 出现在 `src/Game/ScreenshotService.cs:196-207`
    （统计截图目录占用），因此"声明缺 `files` 会被拒绝启用"的预测（矩阵 C1）**仍然成立**，
    与本仓库的文件访问是否越界无关。
  - 可行的消除方式（待扫描器证实后再做，避免为未验证规则改生产代码）：
    用 `ModHelper.ListModDataFiles` + `ReadModDataFileBytes` 替换那 3 处 `System.IO` 调用，
    使程序集不再引用 `Directory`/`FileInfo`。
- 官方 ModScanner 仍未运行（`NOT_RUN`），因此"扫描器是否静态误报 `System.IO`"无法证明。
- `dist` 与已安装 DLL 仍不一致；`play_allowlist` 仍为 0；L3 实机项全部 `NOT_RUN`。
- 结论：F7 保持 `OPEN`，需要在实机会话中跑一次官方扫描器并记录输出。

## 未处理 / 需要决策

1. `C-2`（B12 原生方向语义）、`C-3`（顾客实例切换）仍依赖真机，安全侧拒绝。
2. `initial_result` 与 `candidate_actions` 等字段需要同步进 `SKILL.md`/benchmark 的 schema 说明
   （本提交未改文档，避免与代码再漂移，计划在实机前统一）。
3. 备份/回滚：本次未改动 `dist`。

## 可复现命令

```bash
cd PawnMind
./test.sh                          # C# 754 / 门禁 53 / 契约 503
.venv/bin/python -B -m unittest discover -s agent/tests   # 371 OK
.venv/bin/python tools/gen_policy_catalog.py --check
python3 -B scripts/security/scan_git_secrets.py           # 仓库根目录
```
