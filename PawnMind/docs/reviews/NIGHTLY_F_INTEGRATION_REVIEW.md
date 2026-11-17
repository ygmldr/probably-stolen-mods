# NIGHTLY_F_INTEGRATION_REVIEW — 跨层集成审阅 + 全批次结论

Status: **APPROVE_WITH_BLOCKERS**
（离线批次本身通过：六个模块全部审阅完成且最终提交全绿（C# 750/0、门禁 53/0、契约 503、Python 365 OK、
policy sha 一致、Unity 编译 gate PASS）；"blockers" 指**下一阶段门槛**而非代码缺陷：L3 真机验收、
`play_allowlist=0` 不可解除、`dist`/已安装 DLL 不一致未修复、官方 ModScanner 未跑、发布前 capability/版本号
待决策。若按"能否进入实机验收"判定 = **可以**；按"能否进入 strict play / 录制 / 发布"判定 = **不可以**。）
Reviewing commit(s): **最终 HEAD `f7ae62c117995dffb43a56da70ddefd6254ccb5b`**（docs/nightly 收尾）；
代码/测试修复 `7548ddb23a8fb9a0ea10a08740e2c82352891b07`；模块本体
A `63ae0ef` / B `6ab3333` / D+C `f09314e` / E `299ecd4` / G `f789a92`；基线 `3369a31` + `4846189`。
审阅时工作树干净（`git status --porcelain` 仅剩本批 7 份审阅文件为 untracked，由 Lead 最后提交）。
Change scope（全批次）：`src/Core/`（Mode/RunMode/Endpoint 策略、PolicyCatalog.g.cs、SnapshotFieldMap、
ClientSnapshotProjection、LegalAction、ActionGate/OperationStatus/ShopOperationSemantics/TradeGuard 等）、
`src/Bridge/`（BridgeServer、ActionRouter、StateSnapshot、FirstWeekService、RecordingSaveService、TradeService）、
`src/Game/`（GameState、GameTradePort、RecordingSaveAdapter）、`agent/`（tool_policy/pawnmind_client/mcp_server/
trace_validator/fake_game 与 tests）、`test/PawnMind.Tests/`、`test/PawnMind.AdapterTests/Program.cs`、
`tools/gen_policy_catalog.py`、E 的 SKILL/BENCHMARK_SPEC、G 的 COMPAT_MATRIX/WORKSHOP_DRAFT、docs/reviews 7 份。

## Real evidence（本 Agent 亲自运行；全部在最终提交上）

```text
$ cd <repo>/PawnMind && set -a; . .env.server; set +a

$ ./test.sh                                         # 最终提交 f7ae62c
passed=750 failed=0
== contract gate tests ==
contract gate tests: passed=53 failed=0
contract checks: 503
adapter contract OK
EXIT=0                                              # 日志 /tmp/nightly_review_testsh_committed.log

$ .venv/bin/python -B -m unittest discover -s agent/tests
Ran 365 tests in 56.446s
OK
EXIT=0                                              # 日志 /tmp/nightly_review_pytests_committed.log

$ .venv/bin/python tools/gen_policy_catalog.py --check
ok: .../src/Core/PolicyCatalog.g.cs matches tool_policy.json
    (sha256 e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc)
EXIT=0

$ $DOTNET build src/PawnMind.csproj -c Release -p:GameManaged=<game-managed>
Build succeeded. 0 Warning(s) 0 Error(s)          # 增量
$ $DOTNET build src/PawnMind.csproj -c Release -t:Rebuild -p:GameManaged=<game-managed>
Build succeeded. 0 Warning(s) 0 Error(s)          # clean rebuild
src/bin/Release/netstandard2.1/PawnMind.dll = d29209a4...87eec（338,432 B）  # 两次构建同哈希 → 确定性

$ git -C <repo> log --oneline -3
f7ae62c docs(nightly): final reports, baseline, test matrix and schema sync
7548ddb fix(pawnmind): fail-closed policy fingerprint and review findings
f789a92 docs(audit): static mod compatibility matrix and workshop install draft (Nightly G)
$ git -C <repo> status --porcelain
?? PawnMind/docs/reviews/NIGHTLY_A_..._REVIEW.md ... （仅本批 7 份审阅文件；生产代码/文档干净）
```

**模块过滤证据（最终提交）**：

```text
$ ./test.sh PP_READ      -> passed=14 failed=0     # B 快照纯度/LegalAction
$ ./test.sh PP_TRADE     -> passed=17 failed=0     # C 交易守卫/证据
$ ./test.sh PP_ASYNC     -> passed=12 failed=0     # D 两轴/operation
$ ./test.sh Operation    -> passed=38 failed=0     # D（37 + 新增 D-1 回归）
$ ./test.sh PP_MODE      -> passed=1  failed=0     # A（+RunModeTests/EndpointGateTests 等非前缀用例在全量内）
$ .venv/bin/python -B -m unittest discover -s agent/tests -p 'test_trace_validator.py'
Ran 77 tests in 0.006s OK
$ ... -p 'test_skill_tool_refs.py'  -> Ran 16 tests OK
```

**独立复现/探针（本审阅者自己跑，未改仓库）**：

- B 漂移灵敏度：`/tmp` 副本给 `StateSnapshot.cs` 注入未登记键 → `PP_READ_03_Emitted_State_Keys_Are_Registered`
  FAIL（`unregistered state keys: drift_test_unregistered_key`）；仓库未改。
- A-1（修复前）：连续 3 次 `client.action`，第 2/3 次 `ok:true` 且 2 个 `/action` POST → 指纹守卫被缓存绕过。
- A-1/A-2（修复后）：3/3 `policy_mismatch`、0 POST；无指纹桥 → `policy_unverifiable`、0 POST；匹配路径 1 POST。
- D-1（修复前）：`GET /operation` 对 guard 拒绝返回 `execution_status=rejected + dispatched=true`；
  修复后新增测试 `Rejected_Operation_Reports_Dispatched_False` PASS（本 Agent 单独跑）。

**跨层静态/运行一致性核对**：

```text
$ python tool_policy: tools=116, decisions allow=75/needs_evidence=30/deny=11
  deny-but-bootstrap-allowed=[]; bootstrap allowlist=50; play_allowlist=[]; live_evidence 非空=[]
$ python: docs/audit/tools-inventory.json -> full 116 / recording 106
$ grep PolicyCatalog.Sha256 == policy_sha256(e0da5a24...)   # gen --check 已锁死
$ git check-ignore -v PawnMind/.env.server PawnMind/dist/... # 均已 ignore（token 不落库、dist 不入库）
```

## Invariants checked（F1 七项跨层检查 + F2 收尾项）

| F1 跨层检查 | 结论 | 证据 / 说明 |
|---|---|---|
| C# Policy ↔ Python policy：同源、哈希一致、未知默认拒绝；不存在“Python 隐藏、HTTP 直接允许” | **PASS** | `PolicyCatalog.g.cs` 由 `tool_policy.json` 生成，`--check` ok（sha `e0da5a24…`）；C# `ModePolicy.Refusal` 对未知 op 默认 `mode_forbidden`（Test 显式除外）；Python `decision_for` 未知工具 = `deny`、`allowlist` 只取 allow；BootstrapOps=50 与 Python bootstrap allowlist=50 一一对应；**deny 工具无一是 bootstrap-allowed**（本 Agent 脚本核对）。裸 HTTP 侧：`/state`/`/events`/`/action` 全部走 `EndpointPolicy`，`/action` 内 `_authorize` 先于 refresh/cache/dispatch |
| Snapshot ↔ LegalAction ↔ Skill：字段删改有消费者兼容；未知不假装 legal | **PASS** | `client.can_report` 删除后 Python `_legal_action_payload`/debug_bundle/`test_snapshot_compat` 稳定降级；`candidate_actions` 与 `legal_actions` 分离，bargain 四向 `availability=unknown + requires=native_guard`；最终 `7548ddb` 让 legal 行也带 `availability="available"`，与 SKILL/benchmark schema 一致；`get_legal_actions` 不再宣称 bargain 合法 |
| Trade ↔ Operation ↔ Dialogue：交易完成 ≠ 离店；允许合法 continuation，不允许重复确认 | **PASS（L1）** | `TradeService` 的 Operation 在“交易提交”结束（business_complete + client_unchanged）；`advance_dialogue` 是独立 op，completed/uncertain 都不阻塞（`PP_TRADE_04` 两条）；同 `action_id` 重试不重复点击（AcceptCalls 恒 1）；新 id 重复同 op → `operation_pending`（`FindPendingFor`） |
| Gate ↔ Recorder：丢响应不丢业务已知事实；跨重启不盲重试 | **PASS（L1/L2 语义）** | `MarkResponseUndelivered` 只改 delivery 轴（L2 400 KiB 断连测试：execution 仍 completed、重试拿回原证据 + redelivered）；`CaptureOutcome`（截图证据）在 dispatch 返回时记录 op/ok/error/rev，**早于**响应投递，故响应丢失仍有业务事实记录；跨重启无 exactly-once 承诺：进程内 gate 清空 → `unknown_operation`，Python durable intent 不得盲放（trace validator 规则 1/2/15；真重启只能表现为 unknown，见 D-2） |
| Async ↔ Save：换天不等于落盘成功 | **PASS（语义）/ PARTIAL（save_completed 无证据来源）** | `ShopOperationSemantics.Sleep` 把 `day_changed` 与 `save_evidence` 完全分开；`RecordingSaveService` 生产路径把 save 轴固定 `save_unknown`（`last_save_time` 不算证据），`save_completed` 分支只在 L0 单测出现——正确方向，但“真实落盘证据”仍是 L3 `NOT_RUN`；`end_day` 只有回读 CLOSED 才 completed |
| Play gate ↔ Live evidence：离线全绿不能把 `play_allowlist` 变可用 | **PASS** | 最终提交 `play_allowlist == []`；116 工具 `live_evidence` 全空；`play_ready=false`；C# `PlayOps={}`；所有报告/矩阵/SKILL 一致声明“不得用 fake 填 live”。PS：`PolicyCatalog`/`play_allowlist` 有专门测试锁死，无自动转正路径 |
| Build ↔ Installed：不一致时没有真实游戏验收结论 | **PASS（记录）/ NOT_RUN（安装）** | `dist/PawnMind.dll` = `ad335987…`（296,960 B，mtime 17:58Z）；最终构建产物 = `d29209a4…`（338,432 B）→ 哈希不同、dist STALE；G 矩阵 D-04 记录 26 个 `.cs` 更新；安装目录本机不可访问（历史 `e9e1bd07…`）→ 任何游戏内现象不可归因；报告/矩阵/NEXT_LIVE_SESSION 均明确此边界 |

| F2 收尾项 | 结论 | 证据 |
|---|---|---|
| 最终全量 C#/Python 重跑并记录命令/计数/日志 | **PASS** | 750/0；gate 53/0；契约 503；Python 365；日志路径见 §Real evidence |
| clean build / 确定性 | **PASS** | `-t:Rebuild` Build succeeded 0/0；两次构建同哈希 `d29209a4…` |
| 每个新增测试标注证据等级（L0/L1/L2/L3=NOT_RUN） | **PASS** | 各模块审阅逐条标注；L3 全部 `NOT_RUN`；findings 未冒充 L3 |
| 没有删除/注释失败测试、没有放宽 guard 以过测试 | **PASS（git diff 复核）** | 修复提交 `7548ddb` 只加测试与 fail-closed 收紧；A-1/A-2/D-1 都是“先红后绿”；`test_bridge_without_a_fingerprint_*` 从 fail-open 改为 fail-closed（更严，非放宽） |
| 没有 fake 证据填 live evidence；`live_evidence` 全空 | **PASS** | 上述 tool_policy 核对；报告“全部 PASS 均为 L0/L1/L2，L3 没有一条完成” |
| 最终报告 12 问可被代码/测试支撑 | **PASS（抽查）** | 见 §12 问对照：HEAD/SHA、默认 Locked、裸 HTTP 门禁、同源策略、快照纯度静态结论、legal/candidate、B10–B16 0 次点击、trade/dialogue 分离、undelivered 只改投递轴、wait_operation 非阻塞、play_allowlist 0、L3 NOT_RUN |

### 任务书 12 问逐条核对（F 视角）

| # | 问题 | 核对结果 |
|---|---|---|
| 1 | 当前 HEAD 与 ROUND3 差异 | `f7ae62c`；`can_report` 确实移除、LegalAction 候选化、两轴 Operation、交易守卫落地（A/B/C/D 审阅证据） |
| 2 | 缺配置默认 Test 是否移除 | 是：`RunMode.Locked` 默认，缺/非法/未确认一律 Locked（A PASS） |
| 3 | 裸 HTTP 能否绕过读取限制 | 否：`/state`/`/events` 在 Bootstrap/Play/Locked 先于 provider 403（L2 `EndpointGateTests`） |
| 4 | C#/Python 是否同源、漂移如何拒绝 | 生成物 + `--check` + Python 指纹守卫；A-1 修复后 mismatch/unverifiable 在窗口内持续拒绝（A PASS，C# 自带哈希仅 PARTIAL） |
| 5 | 快照是否还可能触发 Refresh/RNG | 已知热点静态不可达（B）；prompt/report/calendar/tutorial/item/dialogue 链仍 UNKNOWN，`PP-READ-01-real` L3 NOT_RUN |
| 6 | legal 是否把候选当合法、旧消费者兼容 | 否；candidate 单列；Python 旧快照降级；`availability` 最终一致（B/E PASS） |
| 7 | B10–B16 每分支“派发 0 次”测试 | 是：`PP_TRADE_03a..g`，fake `AcceptCalls==0`（L1）；真实分支 L3 NOT_RUN |
| 8 | confirm_trade completed 与 dialogue 是否分离 | 是（`PP_TRADE_04`） |
| 9 | MarkResponseUndelivered 是否改写已完成业务事实 | 否（unit + L2 断连测试）；只改 delivery 轴 |
| 10 | wait_operation 是否阻塞主线程、超时是否假成功 | 否；Python 侧有界轮询（≤10s），超时 `ok:false+status:pending+operation_id+next`；DSH E2E L3 NOT_RUN |
| 11 | 首周最小工具集定义、哪些未实机 | SKILL §12 定义并由 AST lint 校验；`play_allowlist=0`，全部工具实机可用性 NOT_RUN |
| 12 | 剩余 NOT_RUN 与用户优先事项 | 见 `NEXT_LIVE_SESSION.md`；优先：安装同版本 DLL→只读 `/health`/门禁→插桩 Refresh 计数→交易/关店/睡觉真机→官方 ModScanner 决策；完成前禁止录制/填 play_allowlist |

## Critical findings（全批次汇总与处置）

**当前无未处置的 Critical/High 代码缺陷。** 首轮审阅提出、修复并复核的 Critical/High：

| 编号 | 模块 | 严重度 | 状态 | 一句话 |
|---|---|---|---|---|
| A-1 | A | **High** | **FIXED `7548ddb`，本会话独立复测** | Python 策略指纹守卫 30s 缓存绕过 → 第 2 次写被放行；修复为缓存“结论”，3 次连续拒绝 0 POST |
| A-2 | A | Medium | **FIXED（有意行为变更）** | 无 `policy_sha256` 的旧桥被兼容放行 → 改 fail-closed `policy_unverifiable`；旧 DLL 将不能经新 MCP 写（部署注意） |
| D-1 / C-1 | D/C | Medium | **FIXED `7548ddb`** | guard 拒绝的 `GET /operation` 记录 `dispatched=true` 与响应矛盾 → `Dispatched = (execution != rejected)` + 回归测试 |
| C-2 | C | Medium（风险） | **OPEN，L3 NOT_RUN** | B12 方向依赖 `IsNegociatingItemOwned()` 语义，需真机确认“玩家拥有 vs 顾客拥有”；方向反了是安全侧拒绝，不会假成交 |
| C-3 | C | Low | **OPEN，L3 NOT_RUN** | 若原生 accept 后立即换/移除顾客实例，已完成交易会被判 `client_changed`（安全但可能漏报成功） |
| B-2 | B | Low | 记录（非阻塞） | 源码正则漂移测试对跨行/间接调用有盲区；callgraph 已把 UNKNOWN 如实登记，建议后续 AST 化 |
| D-2 | D | Low | 记录（无需代码改） | `process_restarted` 只表示进程内缓存被清空/run 变化；真重启只能表现为 `unknown_operation` |
| E-2 | E | Low | 记录（建议文档注明） | trace validator 在 pending 期禁止任何其他写，比桥更严格（桥只阻止同一 op）；可能导致合法轨迹误报 error |
| E-3 | E | Low | 记录 | benchmark spec 的版本足迹停在 `f09314e`，最终 SHA 由 Lead 在 index 提交同步 |
| G-1 | G | Low | **待 Lead 收尾（文档）** | 版本号文档残留：`PawnMind/README.md:117` 与根 `README.md:121` 仍写 `manifest 0.2.1`（实际 0.3.0） |
| G-2 | G | Low | **待 Lead 收尾（文档）** | G 矩阵/安装草稿仍记 `749/0`，最终为 750/0（矩阵自带“合并后重跑”caveat，已由 report/test matrix 记录 750） |
| G-3 | G | Low | 记录 | 安装草稿未提示 A-2 行为变更：DLL 与 MCP 必须同版本安装，否则新 MCP 会拒绝旧 DLL 的写 |

## Potential regressions（跨模块）

1. **`client.can_report` 删除**是公开 schema 的破坏性变更：旧外部脚本访问 `state.client.can_report` 会 KeyError。
   仓库内 Python/MCP/skill 已兼容，但用户自写脚本需 release note（B 审阅已记）。
2. **`legal_actions` 不再包含 bargain 四项**：旧消费者若依赖“panel open → 四个 bargain 在 legal_actions”会少看到；
   必须改读 `candidate_actions`。属于有意语义修正，但对外部协作者是破坏性变更。
3. **A-2 行为变更**：新 MCP 对无指纹旧桥 fail-closed；升级外层 MCP 而未升级游戏内 DLL 的用户会看到写全拒。
   需要在 release note/安装草稿写明（G-3）。
4. **幂等/幂等缓存边界**：`FindPendingFor` 把 `uncertain` 也算“非终态”，同 op 新 `action_id` 在进程/run 存续期间
   一直被拒，没有人工 ack/清除 API；对交易是任务书要求（防重复成交），但需在 Skill/运维文档说明“uncertain 后
   只能查证 + 重启/读档恢复”。
5. **`wait_operation` 超时统一报 `status:"pending"`**，即使 `execution_status=uncertain`；调用方若只看 `status`
   会误以为“还在跑”。SKILL 已要求看两轴，仍属低风险误读点。
6. **Play 模式仍是全关**（`play_allowlist=0`）：任何“按旧习惯用 play 跑起来”的用户会看到全部写被拒；这是设计
   而非 bug，但 UX 上需靠文档/`/health.play_ready` 解释。
7. **dist/安装不一致**：任何游戏内现象不可归因于最终提交；若用户跳过重建直接验收，会把旧 DLL 行为当新代码
   行为（G 矩阵/NEXT_LIVE_SESSION 已强调）。
8. **G 矩阵的发布阻塞项未解决**：manifest 缺 `files` capability（预测启用拒绝）、`reflection-external` 于
   2026-12-10 retiring、版本号未统一——这些是发布计划阻塞，不是本批次代码回归。

## NOT_RUN / UNKNOWN

- **L3 真机（一律 NOT_RUN，本批次无游戏进程）**：真实 `/health` MVID/版本/模式门禁；`PP-READ-01-real`
  （Unity 运行时 `Refresh()` 计数，需插桩构建）；快照 UNKNOWN 链（prompt/report/calendar/tutorial/item value/
  dialogue/CanSellThisItem）；`confirm_trade`/B10–B16 真机现象与 `IsNegociatingItemOwned()` 语义；
  `end_day`/`sleep` 真实异步与落盘证据；`GET /operation` 真机主线程表现；真实存档回滚后的 op 归属。
- **NOT_RUN（环境/授权）**：官方 ModScanner `mod-scan`、`APIChanges.md`/`ModdingGuide.md`（仓库不存在）、
  `dist` 打包/安装、已安装 DLL 哈希/`/health` 实机校验、Workshop 上传/验证。
- **NOT_RUN（宿主）**：DSH/OpenCode 真实 MCP E2E（含 `wait_operation` 语义、policy_mismatch 呈现）。
- **NOT_MEASURED**：首周通关率、Skill 效率、平均成本/回合时长、人工介入、seed 可复现性、竞品对比
  （benchmark spec §9；本审阅确认没有被填数字）。
- **UNKNOWN**：`_decomp` ModScanner 规则副本（VERSION=4）与 `<game-managed>` 是否同一 MVID；
  规则随游戏更新变化，实机扫描永远优先。
- **UNKNOWN**：`dist`/安装 DLL 与最终 HEAD 的对应关系；这是“游戏内验收不可归因”的根因，需用户重建并重装后消除。

## Reviewer independence

本文件作者为 Team 中的独立审阅 Agent `nightly-reviewer`：**fresh spawn 会话**（`provider: spawn`,
`context: fresh`），在实现完成后由 Lead 启动；未参与任何模块实现/测试/文档编写，未继承实现会话上下文。
实现者为 Lead（A/B/C/D）与 teammate `eval-scribe`（E）、`compat-scribe`（G）；本会话与它们均为不同 agent/会话。
审阅期间只读生产代码/测试/文档，唯一写入为 `docs/reviews/` 下 7 个审阅文件（本批 untracked，由 Lead 最后提交）；
未 commit/push、未启动游戏、未读写存档、未安装/覆盖 DLL、未运行 `build.sh` 打包（只跑编译 gate 与测试）。
所有数字均来自本会话亲自运行（命令与日志路径见 §Real evidence）；首轮发现的 High/Medium 缺陷（A-1/A-2/D-1）
在本会话独立复现后由 Lead 修复，本会话再独立复测并引用修复后证据。基准：**最终 HEAD `f7ae62c`**。
