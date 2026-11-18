# ROUND4_REPORT — 2026-11-18 工程推进报告

> 契约：`docs/PAWNMIND_ROUND4_AGENT_EXECUTION_BRIEF_2026-11-18.md`
> 基线：`4a5b8d1d77a19cab8ddb76e0ba0216ac2da110e0`（接手时 HEAD 未变）
> 逐项处置：`docs/reviews/ROUND4_DISPOSITION.md`
> 证据等级：SRC / OFFLINE / CI / LIVE；本轮 **LIVE = NOT_RUN**

## 1. 一句话

F1b、F4b、F3b、F5b 已修（Gate A / Gate B PASS）；F7 因**没有实机扫描**且
**已安装 DLL ≠ 本轮构建**，Gate C 保持 **BLOCKED**；Gate D 未授予，
`play_allowlist` 仍为 **0**。

## 2. HEAD 与提交

| 内容 | 引用 |
|---|---|
| 基线 | `4a5b8d1` |
| 代码 + 测试 | `fix(round4): confirm run changes and make dispatch explicit` |
| 文档（本文） | `docs(round4): disposition, evidence and report` |
| 分支 | `main`（fast-forward，未 force-push） |

提交顺序与 brief §9 的对应关系：F1b/F4b/F3b/F5b 合并为一个可审计的语义提交
（它们共用 `ActionResult`/`OperationRecord` 的同一组结构体，拆开会产生不可编译的中间态），
文档单独一提。F7 无代码改动，只有静态证据与采集步骤。

## 3. 改动文件

**生产代码（C#）**

| 文件 | 作用 |
|---|---|
| `src/Core/RunChangePolicy.cs`（新） | 跨局 op 清单 + `requested/confirmed/unknown` 判定（单一来源） |
| `src/Core/OperationStatus.cs` | `DispatchStatus` 三态；`OperationRecord` 增加 `Dispatch`/`RunChange*`/`CompletionEvidence`；`ExecutionStatus.Classify` 改为接受三态 |
| `src/Core/ActionGate.cs` | `Complete`/`Begin`/`Remember` 使用三态；`FindPendingFor` 以 `MayHaveRun` 判断；新增 `FindUnconfirmedRunChange` |
| `src/Bridge/ActionResult.cs` | `Dispatch` 声明；`Success/Failure/Failed/UnknownDispatch`；`SuccessRaw` 只认 JSON 布尔 |
| `src/Bridge/ActionRouter.cs` | 运行身份前后采样、确认后才驱逐、`run_change_pending`；`DecorateResponse` 以记录为唯一来源；`initial_result` 重建；`dispatch_status`/`dispatched:null`；异常→unknown |
| `src/Bridge/NewGameMenuService.cs` / `src/Core/NewGameMenu*.cs` | `Dispatched` 字段 + 在两处 JSON 生产者里输出 |
| `src/Bridge/{Analyzer,GameActions,Interaction,Label,Lottery,Machine,MarkingPen,Night,NightReturn,P9Action,Phone,ServiceStatus,Upgrade,Wine}Service.cs` | 每个写分支显式声明派发事实；调用后失败改 `Failed` |
| `src/Game/GameState.cs` | `ReadRunId()`（不序列化、不给模型） |
| `src/PawnMindMod.cs` | 去掉请求时的提前清理；注入 `readRunId` + `ResetRunBoundEvidence`；`end_day` 晋升写 `completion_evidence` |
| `agent/mcp_server.py` | `locked` 模式、`_resolve_mode` 故障关闭、`_ModeGuard` 漂移守卫、`get_operation` 文档 |

**测试**

| 文件 | 作用 |
|---|---|
| `test/PawnMind.Tests/Round4OperationTests.cs`（新） | 21 条 R4 用例（F1b 9 / F4b 7 / F3b 5） |
| `test/PawnMind.Tests/OperationTests.cs` | 旧 F1 用例改为"必须观察到身份变化"；gate 用例改三态 |
| `agent/tests/test_mcp_tools_mode.py` | 6 条 `ModeLockdownTests`；夹具改为真实 `/health` 握手 |
| `agent/tests/mode_fixture.py`（新） | 可切换模式的 loopback bridge 替身 |
| `agent/tests/{test_mcp_server,test_new_game_mcp,test_recording_save_mcp,test_orchestrator,test_write_rev_required}.py`、`agent/fake_game.py` | 夹具报告 `run_mode`（新握手契约所需） |

**工具与文档**

- `scripts/audit/dll_fingerprint.py`（新）：SHA256 + CLI MVID（ECMA-335 `#GUID`），
  并用仓库内已记录的 0.46D MVID `911508a6…` 交叉验证过。
- `docs/audit/RUN_CHANGE_EVIDENCE.md`、`docs/audit/RAW_DISPATCH_AUDIT.md`、
  `docs/audit/MOD_SCANNER_ROUND4.md`、`docs/reviews/ROUND4_DISPOSITION.md`（新）。
- `skills/pawnmind-first-week/SKILL.md`：三态派发、`run_change_status` 两段式、
  晋升重试的 `initial_result` 语义、MCP 模式行为。

**未改动**：`manifest.xml`、`agent/policy/tool_policy.json`、`src/Core/PolicyCatalog.g.cs`、
`ModePolicy`、`PlayOps`、`play_allowlist`（仍 0）、任何游戏原生调用点的语义。

## 4. 测试（OFFLINE）

```text
cd PawnMind && ./test.sh
  passed=775 failed=0                     （基线 754，+21）
  contract gate tests: passed=53 failed=0
  adapter contract checks: 503

cd PawnMind && .venv/bin/python -B -m unittest discover -s agent/tests
  Ran 377 tests ... OK                    （基线 371，+6）

.venv/bin/python tools/gen_policy_catalog.py --check
  ok … sha256 e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc

PAWNMIND_BUILD_ONLY=1 ./build.sh
  Unity 编译门禁 PASS（0 warning / 0 error）

python3 -B scripts/security/scan_git_secrets.py
  secret-scan: clean (worktree + history)
```

### 4.1 Red → Green

| 项 | Red（旧语义下失败） | Green |
|---|---|---|
| F1b | 6 条断言失败（`run_change_status` 缺失 + 旧证据被提前驱逐） | 9 条通过 |
| F4b | 4 条（`silent failure` → failed；菜单 JSON 缺字段；异常 → not_dispatched） | 7 条通过 |
| F3b | 4 条（顶层残留 pending / 派发时 readback / 无 `initial_result`） | 5 条通过 |
| F5b | 4 条（不可达/未知仍 116 工具；`--mode test` 覆盖 bridge；无漂移守卫） | 6 条通过 |

Red 的采集方式与完整失败文本见 `ROUND4_DISPOSITION.md` §7 与各条目的 Red 表；
两处临时还原均已撤回，仓库内没有测试专用旁路。

## 5. CI 与第二台机器

`b98c05b` 上的两个工作流均通过：

| 工作流 | 结果 | 链接 |
|---|---|---|
| `python-tests` | success | https://github.com/ygmldr/probably-stolen-mods/actions/runs/44142867622 |
| `secret-scan` | success | https://github.com/ygmldr/probably-stolen-mods/actions/runs/44142867619 |

构建机 `CT101`（192.168.1.101，Linux + .NET 9.0.321 + 真实 `Assembly-CSharp.dll`，
`REQUIRE_GAME=1` 严格模式）在 `b98c05b` 上独立复跑：

```text
passed=775 failed=0
contract gate tests: passed=53 failed=0
contract checks: 503
adapter contract OK
```

命令（该机没有 Windows 路径，必须显式给出三处路径）：

```bash
cd /root/work/game_mods/PawnMind
DOTNET=/root/.dotnet/dotnet \
PAWNMIND_NEWTONSOFT_DLL=/root/work/game_managed/Newtonsoft.Json.dll \
GAME_ASSEMBLY=/root/work/game_managed/Assembly-CSharp.dll \
REQUIRE_GAME=1 ./test.sh
```

> 该机的仓库通过 `git bundle` 同步（它没有 GitHub 私钥），工作树干净，HEAD = `b98c05b`。

## 6. 风险与剩余工作

| # | 风险 / 缺口 | 级别 | 下一步 |
|---|---|---|---|
| 1 | **官方扫描已跑通**：`UNDECLARED: files`（今天拒绝激活）+ 14 条 `reflection-external`（12-10 起拒绝，其中 4 条结构性） | **发布阻塞** | 见 `MOD_SCANNER_ROUND4.md` §7 的三条阻塞与决策 |
| 2 | ~~已安装 DLL ≠ 本轮构建~~ | **已解决** | 2026-11-18 覆盖安装 `6adef8ac…`，与 `dist` 逐字节一致；旧版备份在 `_internal/install-backup-2026-11-18/` |
| 3 | `reflection-external` 14 条：10 条可用"调用点内联 `typeof()`"消除；**4 条结构性**（`NewGamePerksUI.SlotView` 私有嵌套类） | **高（12-10 硬阻塞）** | 向作者申请公开访问器，或放弃逐行 perk 卡片读取 |
| 4 | 跨局未确认期间的**普通写**未被阻止 | 中 | L3 前由 Skill 流程覆盖；如需硬保证要设计"切换窗口"锁 |
| 5 | `ActionResult.Failure` 的"派发前拒绝"仍是约定，无静态检查器 | 中 | 新写动作 review 对照 `RAW_DISPATCH_AUDIT.md` |
| 6 | `initial_result` / `candidate_actions` 尚未进 benchmark schema | 低 | 与实机 schema 冻结一起做 |
| 7 | C-2（B12 原生方向）、C-3（顾客实例切换）、`PP-READ-01` 运行时 `Refresh()` | 中 | 需要实机 |
| 8 | `dist` 与 HEAD 已对齐，并已安装到 `Mods/PawnMind/` | — | 已关闭 |
| 9 | `network` 声明使工坊版本从 2026-12-10 起需要"开发者已验证"（本地不受影响） | **产品级** | 决定是否上工坊；上则需申请验证 |

## 7. 下轮建议（按优先级）

1. **受控 L3 前置**：`dist` 已安装（R4-F7-04 PASS），只剩跑官方 `mod-scan`，把原始输出追加到
   `MOD_SCANNER_ROUND4.md` §5；在此之前不要宣称任何 LIVE 结论。
2. 依据扫描结果决定 `files` 路线（声明 / 关闭截图 / 阻塞发布）。
3. 把 `run_change_status` 的等待流程写进 benchmark 的"读档"用例。
4. 真机验证 `ReadRunId()` 在加载画面、主菜单、切档瞬间的返回值（`R4-F1-09` 的 LIVE 版）。
5. 之后才讨论 `play_allowlist`；本轮不为它填任何 `live_evidence`。
