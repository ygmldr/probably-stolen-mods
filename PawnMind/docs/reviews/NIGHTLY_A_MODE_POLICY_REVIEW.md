# NIGHTLY_A_MODE_POLICY_REVIEW — A 模块（S2 配置到执行的强制模式策略）

Status: **APPROVE**
（首轮审阅发现 1 个 High（A-1）与 1 个 Medium（A-2）；两者已由 Lead 在 `7548ddb` 修复并带回归测试。
本文件按**最终提交**复核通过：A-1 独立探针复测 3/3 拒绝、0 POST；A-2 改为 fail-closed。
待跟踪的只剩“C# 无法自证外部策略漂移”的 PARTIAL（设计边界）与若干文档/部署注意事项，不阻塞。）
Reviewing commit(s): A 本体 `63ae0eff2f11651b674c8fcdfaadc0de231e0642`；
修复 `7548ddb23a8fb9a0ea10a08740e2c82352891b07`（fail-closed policy fingerprint and review findings）；
文档收尾 `f7ae62c117995dffb43a56da70ddefd6254ccb5b`；**最终 HEAD `f7ae62c`**。
Change scope: `src/Core/ModePolicy.cs`、`src/Core/RunModeBinding.cs`、`src/Core/EndpointPolicy.cs`、
`src/Core/BridgeConfig.cs`、`src/Core/PolicyCatalog.g.cs`、`src/Bridge/BridgeServer.cs`、
`src/PawnMindMod.cs`、`tools/gen_policy_catalog.py`、`agent/tool_policy.py`、
`agent/pawnmind_client.py`、`agent/mcp_server.py`、`agent/tests/test_client.py`、
`agent/tests/test_tool_policy.py`、`test/PawnMind.Tests/{RunModeTests,EndpointGateTests,BridgeServerTests,BridgeTransportTests}.cs`；
修复提交另含 `agent/fake_game.py`、`agent/tests/{test_mcp_server,test_new_game_mcp,test_recording_save_mcp,test_orchestrator}.py`
（为 A-2 的 fail-closed 语义补 `/health.policy_sha256` 夹具）。

## Real evidence（本 Agent 亲自运行，非照抄实现者报告）

环境：CT101 `/root/work/game_mods/PawnMind`，`set -a; . .env.server; set +a`；时间 2026-11-16 20:39–21:05 UTC。

```text
# 最终提交（f7ae62c）全量：
$ ./test.sh
passed=750 failed=0
== contract gate tests ==
contract gate tests: passed=53 failed=0
contract checks: 503
adapter contract OK                            # 日志 /tmp/nightly_review_testsh_committed.log

$ .venv/bin/python -B -m unittest discover -s agent/tests
Ran 365 tests ... OK                           # 最终提交；日志 /tmp/nightly_review_pytests_committed.log
                                               # （A-1/A-2 回归各 1 条 + E 同步的 compat 1 条已计入）

$ .venv/bin/python tools/gen_policy_catalog.py --check
ok: .../src/Core/PolicyCatalog.g.cs matches tool_policy.json (sha256 e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc)
EXIT=0

$ $DOTNET build src/PawnMind.csproj -c Release -p:GameManaged=/root/work/game_managed
Build succeeded. 0 Warning(s) 0 Error(s)
$ $DOTNET build src/PawnMind.csproj -c Release -t:Rebuild -p:GameManaged=/root/work/game_managed
Build succeeded. 0 Warning(s) 0 Error(s)
src/bin/Release/netstandard2.1/PawnMind.dll = d29209a4...87eec (338,432 B)   # 两次构建同哈希（确定性）
```

**A-1 独立复现（修复前）与复核（修复后）**——用本地 fake `/health` 的独立探针（脚本在 `/tmp`，未写仓库）：

```text
# 修复前（63ae0ef 代码，2026-11-16 20:50 实测）：
result1: {'ok': False, 'op': 'open_shop', 'error': 'policy_mismatch', ...}
result2: {'ok': True}
result3: {'ok': True}
POSTs that reached the bridge: ['/action', '/action']        # 第 2/3 次写被放行 → A-1 复现

# 修复后（7548ddb 代码，2026-11-16 21:0x 实测）：
A-1 mismatch results: [(False, 'policy_mismatch'), (False, 'policy_mismatch'), (False, 'policy_mismatch')]
A-1 POSTs: []
A-2 unverifiable: (False, 'policy_unverifiable')
A-2 POSTs: []
match: (True, None) POSTs: ['/action']                        # 正常匹配路径仍工作
```

静态/行为核对（亲自读 diff 与运行探针）：

```text
$ git show 63ae0ef -- src/Core/ModePolicy.cs src/Core/RunModeBinding.cs src/Core/EndpointPolicy.cs \
    src/Bridge/BridgeServer.cs src/PawnMindMod.cs agent/pawnmind_client.py agent/mcp_server.py    # 全文已读
$ git show 7548ddb -- agent/pawnmind_client.py agent/tests/test_client.py                      # A-1/A-2 修复 diff 已读
$ grep -rn "ModePolicy.Refusal|mode_forbidden|mode_locked" src/       # 仅 EndpointPolicy / ActionRouter(_authorize) / ModePolicy 自身
$ grep -n "TryRefreshRevision" src/Bridge/ActionRouter.cs            # _authorize 在 Handle() 顶部，先于 cache/refresh/Begin
```

## Invariants checked（逐条 PASS/FAIL/PARTIAL）

| 不变量 | 结论 | 依据 |
|---|---|---|
| 缺 `run_mode` / 空白 / 非法值 → 不进入 Test | **PASS** | `RunModeBinding.Evaluate` 全部返回 `Locked`（`run_mode_missing` / `run_mode_invalid`）；`RunModeTests.Missing_Run_Mode_Fails_Closed` / `Invalid_Run_Mode_Fails_Closed`；`BridgeConfig` 原样保留 typo 供报告 |
| `test` 必须显式 `run_mode=test` + `allow_test_mode=true` | **PASS** | `Test_Mode_Requires_Explicit_Acknowledgement`；非法 `allow_test_mode` 解析为 false → Locked（安全侧） |
| 无 MCP 动作可切换模式 / 改变策略 | **PASS** | 仅 `PawnMindMod.OnEnable` 调 `RunModeBinding.Apply`；全仓无 reload/switch op（静态扫描）；切换需重启 |
| 默认 Locked 且 `ModePolicy` 静态状态可重置（测试隔离） | **PASS** | `_mode = RunMode.Locked`、`Reset()`；`Default_Mode_Is_Locked` |
| 单一策略来源：C# 生成物 = Python `tool_policy.json` | **PASS** | `gen_policy_catalog.py --check` ok（sha `e0da5a24…`）；`test_tool_policy.test_generated_csharp_catalog_matches_the_policy` |
| `play_allowlist` 不自动取 candidate、保持 0 | **PASS** | `PolicyCatalog.PlayOps = { }`；`python play_allowlist == []`；`Play_Mode_Stays_Closed_Without_Live_Evidence` |
| C#/Python 指纹不一致 → 危险操作被拒（整个缓存窗口） | **PASS（修复后）** | `7548ddb`：`_policy_refusal` 缓存“结论”，mismatch 在 30s 窗口持续拒绝；新增 `test_policy_mismatch_is_not_forgotten_after_the_first_call`（3 次 action 全拒、0 POST）；本 Agent 独立探针 3/3 拒绝、0 POST |
| 缺失 `policy_sha256` 的桥 → fail-closed，不放行写 | **PASS（修复后，有意的行为变更）** | `7548ddb`：无指纹 → `policy_unverifiable` 且 0 POST；`test_bridge_without_a_fingerprint_fails_closed`；旧 Mod 构建将无法通过新 MCP 写入（与 dist/安装不一致的现状一致，安全优先——已写入报告/矩阵） |
| 裸 HTTP `/state`、`/events` 不能被 Bootstrap/Play 绕过 | **PASS** | `EndpointPolicy.Refusal(State/Events)` 在 `_stateProvider`/`HandleEvents` 之前 403；`EndpointGateTests` 断言 `StateCalls=0`（L2 loopback） |
| `/action` 的 per-op 授权先于 `TryRefreshRevision` / `ActionGate.Check` / `Begin` / 原生调用 | **PASS** | `ActionRouter.Handle` 顶部 `_authorize` 后才分流 read/write；`Mode_Refusal_Happens_Before_Any_State_Refresh`、`Mode_Refusal_Also_Covers_Reads` 断言 refresh=0 |
| Locked 模式整端拒绝 / `/health` 保持可读且报模式、错误、策略指纹 | **PASS** | `Locked_Refuses_State_And_Writes`、`Health_Stays_Open_And_Reports_The_Gate`；`/health` 含 `run_mode`/`run_mode_error`/`policy_schema`/`policy_sha256`/`play_ready=false`；`ScrubHealth` 会抹 token |
| 未知 op 默认拒绝；模式允许集不继承 | **PASS** | `ModePolicy.Refusal` 先判 Locked，再判 allowlist；Bootstrap 拒 6 个 snapshot reader（`Bootstrap_Plan_Excludes_Snapshot_Readers`） |
| `PP-MODE-02b`：模式收紧后旧 `action_id` 不重放、已完成证据不被改写 | **PASS** | `OperationTests.PP_MODE_02b_Mode_Change_Never_Redispatches_A_Cached_Action` |
| C# 能否自证外部（Python/其他客户端）策略漂移 | **PARTIAL（设计边界）** | C# 只能上报自身编译期 `policy_sha256`；漂移检测由 Python 客户端做。直接裸 HTTP 的第三方调用方不受 Python 侧指纹保护（仍受 C# 模式门禁/写 gate 限制）。报告 §12 已列 PARTIAL |
| 策略/生成物漂移测试会真的失败 | **PASS** | `--check` 对 stale 生成物返回非 0（`tools/gen_policy_catalog.py:main`）；`test_generated_csharp_catalog_matches_the_policy` 全绿；B 模块另有源码级漂移测试（见 B 审阅） |

## Critical findings

### A-1（High，**已修复**）Python 策略指纹守卫被 30 s 缓存绕过

- **原缺陷**（`agent/pawnmind_client.py` `_policy_refusal`）：`_policy_checked_at` 在比较前赋值，缓存命中分支
  直接 `return None`（视为匹配）；mismatch 只挡第一次调用。
- **复现**：见 §Real evidence 修复前探针（第 2/3 次 `ok:true`，2 个 `/action` POST 到达桥）。
- **修复（`7548ddb`）**：缓存“结论”（`_policy_refusal_until` + `_policy_refusal_code`），mismatch/unverifiable
  在整个 30s 窗口持续拒绝；新增回归测试 `test_policy_mismatch_is_not_forgotten_after_the_first_call`。
- **复核**：本 Agent 独立探针 3/3 拒绝、0 POST；修复前/后行为差异与本 Agent 原复现一致；全量 Python 365 OK。

### A-2（Medium，**已修复为 fail-closed 的有意行为变更**）无指纹桥不再被当作兼容放行

- **原缺陷**：桥不报 `policy_sha256` 时按“旧版兼容”放行写（旧测试 `test_bridge_without_a_fingerprint_does_not_block`
  固化了该行为）；在 `dist` STALE、已安装 DLL 版本未知的现状下，“新 MCP + 旧 C#”混合部署会绕过指纹校验。
- **修复（`7548ddb`）**：无指纹 → `policy_unverifiable`，0 POST；测试改为 `test_bridge_without_a_fingerprint_fails_closed`；
  `fake_game.py` 与受影响的 MCP 测试夹具统一补报 canonical `policy_sha256`，全量恢复绿。
- **复核**：本 Agent 独立探针确认 `policy_unverifiable` 且 0 POST；匹配路径仍 `ok:true` + 1 POST。
- **行为变更声明**：旧 Mod 构建（无指纹）将不能通过新 MCP 写入——与“DLL/MCP 必须同版本安装”的现状一致，
  安全优先；建议在 `WORKSHOP_INSTALL_DRAFT` 补一句（见 G 审阅 G-3）。

### A-3（Low，设计确认）`/health` 之外的低风险端点与 `ping` 的边界

- `GET /health` 无 token 是设计（A2 允许）；只暴露模式/策略/版本/MVID/写门状态，`ScrubHealth` 抹 token，
  未发现隐藏数据。
- `ActionRouter.Handle` 对 `ping` 在 `_authorize` 之前返回；但 Locked 时 `EndpointPolicy.Action` 已整端 403，
  Bootstrap/Play 允许集语义一致，不构成绕过；记录为设计边界。
- 未发现 `candidate_allowlist` → `play_allowlist` 的代码路径。

## Potential regressions

1. `run_mode=play` 当前 `PlayOps` 为空，`/state`、`/operation`、`/action` 全部 403；`/health` 显示
   `play_ready=false`。预期（无 live evidence），但用户若照旧流程用 `play` 启动会感觉“全 lock”；
   `NEXT_LIVE_SESSION.md`/SKILL 已提示，安装草稿建议首跑 `bootstrap`。
2. Locked 模式 `/action` 连 `ping` 一起 403（报告已声明有意）；宿主健康检查应改用 `/health`。
3. **A-2 行为变更**：无 `policy_sha256` 的旧 DLL 会被新 MCP 拒绝写（`policy_unverifiable`）；升级外层 MCP
   而没升级游戏内 DLL 的用户会看到写全被拒。需在 release note/安装草稿写明“DLL 与 MCP 同版本安装”。
4. `_policy_refusal` 每 30s 一次额外 `/health`（可忽略）；桥不可达时不缓存 pass，下一次会重新检查（正确方向）。
5. `allow_test_mode=1` 仅在 `run_mode=test` 时生效；只配 `allow_test_mode` 忘记 `run_mode` 会 `mode_locked`，
   日志有指引（`Debug.LogError`）。

## NOT_RUN / UNKNOWN

- **L3-real-game 全部 NOT_RUN**：未启动游戏，未验证真实 `bridge.conf`（ModData 路径）、真实 `/health`
  MVID/版本、真实 Locked/Bootstrap/Play 切换与写门。需要：游戏 + 用户授权 + 与 MCP 同版本的 DLL 安装。
- **UNKNOWN**：`dist`/已安装 DLL 与最终 HEAD 的对应关系（G 审计判定 STALE，安装目录不可访问）；因此
  “游戏内 mode gate 行为”不可归因于本提交。
- **UNKNOWN**：DSH 宿主的真实 MCP E2E 中 `policy_mismatch`/`policy_unverifiable` 的呈现——需宿主录制。
- **PARTIAL**：C# 无法检测外部调用方（裸 HTTP）的策略漂移（见上表）；Python 侧已覆盖。
- A-1/A-2 已用 L1-fake 覆盖；L2 只覆盖模式端点；L3 无游戏。

## Reviewer independence

本文件作者为 Team 中的独立审阅 Agent `nightly-reviewer`：**fresh spawn 会话**（`provider: spawn`,
`context: fresh`），在实现完成后由 Lead 启动；未参与 A 模块任何实现/测试/文档编写，未继承实现会话上下文。
A 实现者为 Lead。本会话与实现会话是不同 agent、不同会话。审阅期间只读生产代码/测试/文档，唯一写入为
`docs/reviews/` 下 7 个审阅文件；所有数字与复现均在本会话亲自运行（命令与日志路径见上）。A-1 由本会话先于修复
独立发现并复现，修复后再次独立复测（3/3 拒绝、0 POST）。最终复核基准：`HEAD f7ae62c`（含 `7548ddb` 修复）。
