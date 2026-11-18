# R5_BASELINE — Round 5 启动基线（真实运行记录）

> 采集时间：2026-11-18T19:16Z（本地 2026-11-19 03:16 +08:00）
> 采集者：Coding Agent（Lead），Linux 服务器 CT101，**无 Windows、无游戏进程、无用户在场**。
> 分支：`r5/overnight`（禁止触碰 `main`）。
> 本文件只写**本机实际执行**得到的数字；开发者报告中无法复核的内容标 `REPORTED`。

## 0. 环境（实测）

| 项 | 值 | 证据等级 |
|---|---|---|
| 主机 | `Linux CT101 6.17.13-2-pve #1 SMP PREEMPT_DYNAMIC` x86_64 | VERIFIED |
| dotnet | `/root/.dotnet/dotnet` `9.0.321` | VERIFIED |
| Python | `PawnMind/.venv/bin/python` `3.12.3` | VERIFIED |
| 游戏程序集 | `/root/work/game_managed/Assembly-CSharp.dll` sha256 `725cb999d019dff1…`（4,820,224 bytes，2026-11-09） | VERIFIED（文件存在且契约测试通过） |
| Newtonsoft | `/root/work/game_managed/Newtonsoft.Json.dll` sha256 `a56146202232958f…` | VERIFIED |
| 游戏进程 | 无（`ps aux` 无 Probably Stolen / Unity 进程） | VERIFIED |
| Windows 真机可用性 | 不可用：本轮所有 L3 验收一律 `NOT_RUN` | VERIFIED（环境事实） |

测试命令（与 BRIEF §2 一致）：

```bash
cd /root/work/game_mods/PawnMind
DOTNET=/root/.dotnet/dotnet \
PAWNMIND_NEWTONSOFT_DLL=/root/work/game_managed/Newtonsoft.Json.dll \
GAME_ASSEMBLY=/root/work/game_managed/Assembly-CSharp.dll \
REQUIRE_GAME=1 ./test.sh
.venv/bin/python -m unittest discover -s agent/tests
```

## 1. 代码基线

| 项 | 值 | 证据等级 |
|---|---|---|
| `git rev-parse HEAD` | `a043449a45f2e015ea4373b4f4ad7abfc128655b` | VERIFIED |
| 分支 | `r5/overnight` | VERIFIED |
| `git status --porcelain` | 空（启动时工作树干净） | VERIFIED |
| HEAD 与 HANDOFF 期望 | HANDOFF 期望 `211309d` + 1 doc commit；实际 `a043449a` 的 parent 即 `211309dd`（`docs(r5): add the Round 5 handoff spec`），**一致** | VERIFIED |
| 未提交/未跟踪改动 | 无 | VERIFIED |

`git log -10 --oneline`（实测）：

```
a043449a docs(r5): add the Round 5 handoff spec to the branch
211309dd docs(report): first-week session report (2026-11-18 → 11-19)
1721cb47 feat(pawnmind): close CustomUIManager windows so a modal cannot stall the day
e833176b Revert "refuse a no-op sleep" — the signal was wrong
3917f07e fix(pawnmind): refuse a no-op sleep and keep stuck operations reachable
f87bc3a7 docs(live): record the two remaining blockers from the week attempt
ac856c54 fix(pawnmind): scope the pending block per call and let sleep resolve
3fa0ff5b fix(pawnmind): count a purchase as a trade, and dispatch press
3dd6e08a docs: record the second install (ea2cb3ce, slot 44)
dc724e20 feat(pawnmind): let test mode dismiss the two adapter-less overlays
```

## 2. 测试基线（全部真实运行）

| 套件 | 命令 | 结果 | 耗时 | 证据等级 |
|---|---|---|---|---|
| C# 纯逻辑 | `./test.sh`（含门禁） | **passed=794 failed=0** | 6s（整脚本） | VERIFIED |
| 脚本门禁（contract gate） | `./test.sh` 内 `test/ContractGate.Tests/run.sh` | **passed=53 failed=0** | 同上 | VERIFIED |
| 适配器契约 | `./test.sh` 内 `PawnMind.AdapterTests`（`REQUIRE_GAME=1`） | **contract checks=506，adapter contract OK** | 同上 | VERIFIED |
| Python | `.venv/bin/python -m unittest discover -s agent/tests` | **Ran 377 tests，OK** | 44.8s | VERIFIED |
| 策略目录生成一致性 | `.venv/bin/python tools/gen_policy_catalog.py --check` | `ok: …PolicyCatalog.g.cs matches tool_policy.json (sha256 e0da5a24…)`，exit 0 | <1s | VERIFIED |

与 BRIEF §3.5 启动者基线对比：`C# 794/0`、`gate 53/0`、`contract 506` **完全一致**；
Python 377 与 `SESSION_2026-11-18_FIRST_WEEK.md` 的 “Python 377 OK” 一致。
→ 基线无漂移，可以继续。

原始日志（gitignored，本机保留）：`_internal/logs/r5_baseline_cs.log`、`_internal/logs/r5_baseline_py.log`。

## 3. 协议 / 工具面 / 文档版本

| 项 | 值 | 证据等级 |
|---|---|---|
| `agent/policy/tool_policy.json` schema | `2` | VERIFIED |
| policy canonical sha256 | `e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc` | VERIFIED（`tool_policy.policy_sha256`） |
| 工具总数（full） | **116** | VERIFIED（policy JSON `tools` 计数；`mcp_server.py --help` 自述 116） |
| 工具面（recording） | **106**（`--tools recording`） | REPORTED（help 文本；未逐个注册清单核对 → 见缺口） |
| `play_allowlist` | **空**（无 live evidence；Play 写操作仍全拒） | VERIFIED |
| `docs/audit/tools-inventory.json` schema | `2` | VERIFIED |
| `src/Core/PolicyCatalog.g.cs` | 与 policy 同步（见上 --check） | VERIFIED |
| `agent/trace_validator.py` 版本 | `VALIDATOR_VERSION = "1.1.0"` | VERIFIED |
| `skills/pawnmind-first-week/SKILL.md` 状态 | 顶部 `OFFLINE_DRAFT`；声明适用 policy sha `e0da5a24…`、full=116/recording=106 | VERIFIED（文本） |
| `docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md` | 状态 `OFFLINE_SPEC`；`spec_version: 2026-11-17-final`；文中引用 HEAD `f7ae62c`（**与当前 HEAD 不同 → 漂移，R5-A4 修**） | VERIFIED（文本） |

文件 sha256（本轮启动时；前 16 位用于后续 diff 对比，全量值见 `OVERNIGHT_LOG.md`）：

| 文件 | sha256（前 16） |
|---|---|
| `agent/policy/tool_policy.json` | `2d4c5c27379eee50` |
| `docs/audit/tools-inventory.json` | `f0fcc07e40268846` |
| `agent/trace_validator.py` | `2ebbdc274b5c9acc` |
| `agent/mcp_server.py` | `d7c4d7531fe12534` |
| `skills/pawnmind-first-week/SKILL.md` | `d3a80ccafd69c8a0` |
| `docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md` | `de4b0ec74aded878` |
| `src/Core/PolicyCatalog.g.cs` | `db70c5428933aa9c` |

## 4. 证据差（本轮启动时必须记录的缺口）

| # | 项 | 结论 | 处理 |
|---|---|---|---|
| E1 | 成交笔数：`SESSION_2026-11-18_FIRST_WEEK.md` 写 **32 笔**（逐日 8/16/8/0/0/0/0）；`LIVE_SESSION_2026-11-18.md` 写“**约 45 笔**” | **CONFLICT（口径差异）**。两份文档对同一局（槽 53）给出不同数字；后者称“买/卖各半”但未给逐日拆分 | **不猜测、不调和**。R5 文档引用时并列标注两个数字与来源；需要原始轨迹才能定案 |
| E2 | 原始轨迹（append-only JSONL / 导出的 validator trace） | **NOT_RUN / 缺失**：仓库内与 `_internal/` 下均未找到槽 53 的原始轨迹文件。报告中的 32/45 笔均无法由原始请求/回执复算 | 本轮 C2 建可信记录器；历史 run 不追认 |
| E3 | `_internal/week.py` | **缺失**：仓库 `.gitignore` 忽略 `_internal/`，本机 `_internal/` 中也没有该脚本（只有 BRIEF/HANDOFF/日志）。首周脚本无法复核 | 记录为证据缺口；本轮不重建、不包装成 AI（C1） |
| E4 | 794/377/506/53 等数字 | 本项目**本轮已真实复跑**且一致（见 §2）；此前报告中的数字从今日起可视为 VERIFIED | — |
| E5 | `dist/PawnMind/PawnMind.dll` 时间戳早于当前 HEAD | **stale artifact**：不是本轮产物，不能作为当前源码证据；本轮不发布、不安装 | 仅在构建验证时重建，不提交 dist（gitignored） |
| E6 | 真机证据（槽 43–53、MVID、DLL 安装 SHA、`rent_1_200` 回执） | 只有文档描述，**无本机可复核原始文件** | 一律 `REPORTED`，不是本轮的 VERIFIED |

## 5. R4→R5 已知基线的源码级核对（不做真机复核）

BRIEF §4 列出的“已知正确基线”，本轮只做**源码/测试存在性**核对（live 行为 `NOT_RUN`）：

| # | 基线 | 源码核对结果 | 证据等级 |
|---|---|---|---|
| K1 | 交易证据 =（物品指针移动 **或** 现金变动）且同一顾客；`business_complete=false` 的真交易必须 completed | `src/Bridge/TradeService.cs`：`tradeProven = (ItemsTransferred \|\| cashMoved) && ClientUnchanged`；`!BusinessComplete && !tradeProven → uncertain`，即 `BusinessComplete \|\| tradeProven` 判成功。**注意：`BusinessComplete=true` 且无单笔证据时仍会判成功 → A3 待验风险确实存在** | VERIFIED（源码） |
| K2 | pending 阻塞按 `op + 参数指纹`，仅 `confirm_trade` 走；`end_day`/`sleep` 按 op 名 | `PendingScope.IsCallScoped` 仅 `confirm_trade`；`ActionRouter.HandleWrite` 用 `FindPendingFor(op, fingerprint\|null)` | VERIFIED（源码） |
| K3 | `get_operation("*")` 列出非终态操作（只读） | `ActionRouter.ListOperationsJson` + `ActionGate.ListNonTerminal(20)`。**无 `truncated` 字段 → A2 要求补** | VERIFIED（源码） |
| K4 | `press` 仅 `run_mode=test` + `UiPressPolicy` 白名单 | `UiPressPolicy.Allowed` 4 条路径（含日终确认）；`RecordingDispatchGuard` 有 test-mode 守卫 | VERIFIED（源码） |
| K5 | 正式 Play 不得依赖 `press` | `play_allowlist` 为空；`press` 不在 Play 面 | VERIFIED（policy JSON） |
| K6 | `EndOfDayUIManager.IsOpen` / `CustomUIManager.HasEscapeTarget()` 已被真机证明不可靠，禁止重新引入为否决前置 | `src/` 内已无这两者的否决用法（`WindowAdapter.cs` 注释明确不采用 `HasEscapeTarget`） | VERIFIED（源码）；真机结论 `REPORTED` |
| K7 | `confirm_new_game` 的 run-change 语义（A1 风险） | `ActionRouter.HandleWrite` 对 `RunChangeOps.IsRunChangeOp("confirm_new_game")` 一律登记 `RunChangeRequested=true`；`RunChangePolicy.Classify(NoRun, NoRun) = Requested`；`FindUnconfirmedRunChange` 会挡住下一次真正 start → **A1 待复现风险成立（源码级）** | VERIFIED（源码） |
| K8 | sleep verifier 三次零间隔读 → `failed("day_not_changed")` | `RecordingSaveService.TryResolveSleep`：`sleepPolls >= SleepVerifierPolls(3)` 且 day 未增 → `failed`；**无时间下限 → A2 风险成立（源码级）** | VERIFIED（源码） |

## 6. KNOWN / VERIFIED / NOT_RUN / CONFLICT 汇总

**VERIFIED（本轮真实执行）**

- 基线 HEAD/分支/干净工作树（§1）。
- C# 794/0、gate 53/0、contract 506、Python 377/0、policy catalog 一致（§2）。
- policy schema/hash、116 工具、空 play_allowlist（§3）。
- 上表 K1–K8 的**源码形态**（不是 live 行为）。

**REPORTED（开发者报告，本轮未复核）**

- 首周真机完成、槽 53、租金回执 `rent_1_200`、逐日成交 8/16/8/0/0/0/0。
- 真机 MVID / 安装 DLL SHA / `dev.txt` 已删除等现场状态。
- “约 45 笔”成交。

**NOT_RUN（环境或授权不允许）**

- 所有 Windows L3 验收（真实游戏、扫描器、存档、`run_mode=test/play` 真机行为）。
- Workshop 发布、`dev/scan`。
- 全新开局 / 读档 / 写存档。
- LLM 真机 pilot（R5-D）。

**CONFLICT**

- 成交笔数 32 vs 约 45（同局不同口径，未调和；E1）。
- `FIRST_WEEK_BENCHMARK_SPEC.md` 引用的 HEAD `f7ae62c` 与当前 `a043449a` 不一致（R5-A4 修）。
- `SKILL.md` §5.2 把 `business_complete:true` 写成成交必要条件，与 R4 已实现/已真机验证的 partial trade 冲突（R5-A4 修）。
- `trace_validator.py` `TRADE_EVIDENCE_MISSING` 规则同旧口径（R5-A4 修）。

## 7. 本轮硬边界（继承 BRIEF §0，执行期内自查）

1. 只在 `r5/overnight` 提交；不 push（本机无 GitHub 私钥，也不允许）。
2. 不启动游戏、不装 DLL、不读写存档、不跑 dev/scan、不发布 Workshop。
3. 任何需要 Windows 真机的验收 → `L3 NOT_RUN`，不伪造数据。
4. 生产代码改动一律 Red → Green → Refactor，`./test.sh` 全量回归后才提交。
5. 报告只写实际运行结果；`REPORTED` ≠ `VERIFIED`。
6. 无法安全判断 → 停下并写 `BLOCKED`。
