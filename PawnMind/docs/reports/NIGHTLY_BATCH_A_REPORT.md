# NIGHTLY_BATCH_A_REPORT — Nightly Batch A 最终报告

> 日期：2026-11-17（服务器侧续跑，CT101）。
> 范围：`PawnMind/docs/PAWNMIND_OVERNIGHT_LARGE_BATCH_AGENT_HANDOFF_2026-11-17.md` 的 A–G 全部模块。
> 原则：离线 PASS 与实机 `NOT_RUN` 严格分开；`play_allowlist` 保持 0；未启动游戏、未读写存档、
> 未安装/覆盖 DLL、未发布、未推送远程。
>
> 关键事实：**没有一条 L3（真机）验证在本批次完成**。本报告的全部 PASS 都是 L0/L1/L2 离线证据，
> 不代表游戏内行为已经正确。

## 0. 提交与基线

| 项 | 值 |
|---|---|
| 起始 HEAD | `3369a31` |
| 本批次提交 | `4846189` chore → `6ab3333` B → `f09314e` D+C → `299ecd4` E → `f789a92` G → `7548ddb` 审阅修复 → `f7ae62c` 报告/文档 → 审阅文档提交（`git log -1`） |
| 最终代码/文档 HEAD | `f7ae62c`（含修复 `7548ddb`；审阅文档为最后一个提交） |
| A 模块（批次前） | `63ae0ef`（本次补做独立审阅） |
| 基线/矩阵 | `docs/audit/NIGHTLY_BASELINE.md`、`docs/audit/NIGHTLY_TEST_MATRIX.md` |
| 调用链审计 | `docs/audit/NIGHTLY_SNAPSHOT_CALLGRAPH.md` |
| 兼容矩阵 | `docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md` |
| 实机清单 | `docs/plans/NEXT_LIVE_SESSION.md` |
| 独立审阅 | `docs/reviews/NIGHTLY_{A,B,C,D,E,G}_*_REVIEW.md` + `NIGHTLY_F_INTEGRATION_REVIEW.md` |

环境（本批次从零补齐）：CT101 + .NET SDK **9.0.321** + `PawnMind/.env.server` + Python venv；
Unity 目标 Mod 可在 Linux 上直接编译（`dotnet build src/PawnMind.csproj -p:GameManaged=/root/work/game_managed`
→ Build succeeded）。这是本批次相对交接文档最大的环境收益：**C# 与 Unity 目标编译都有了真实 gate**。

## A — 模式配置到执行的强制策略：完成（离线，本批次补审阅）

| 要求 | 实现 | 证据 |
|---|---|---|
| 显式 `run_mode`，失败 fail closed | `RunModeBinding`；缺失/空白/未知 → `Locked` | `RunModeTests`、`PP-MODE-01a` PASS |
| Test 必须显式 | `run_mode=test` + `allow_test_mode=true` | `PP-MODE-01d` PASS |
| 无 MCP 切换通道 | 只在 `OnEnable` 绑定 | 静态检查 |
| 单一策略来源 | `PolicyCatalog.g.cs` 由 `agent/policy/tool_policy.json` 生成 | 漂移测试 + `gen_policy_catalog.py --check` |
| 指纹不一致拒绝 | Python 客户端比对 `/health.policy_sha256` 后拒绝 | `PP-MODE-02a` PARTIAL（C# 无法验证外部） |
| candidate 不转 play | `PlayOps` 只读 live evidence，仍为空 | `play_allowlist=0` |
| 全入口先于快照 | `EndpointPolicy`（State/Events/Action/**Operation**） | `EndpointGateTests` + `OperationEndpointTests` |
| 模式切换旧 ID 不重放 | D 模块落地后可测：模式收紧 → 旧 `action_id` 拒绝、dispatch 不增加、证据不被改写 | `PP-MODE-02b` **PASS** |

已知边界（详见 A 审阅）：C# 只能报告自身哈希，无法检测外部 Python 漂移；Locked 连 `POST /action`
的 `ping` 一起拒（有意）；策略切换需重启（无热重载）。

## B — 快照纯度与 LegalAction：完成（离线）

**修复的真实缺陷**：`StateSnapshot.DescribeClient` 曾调用 `StoreClient.CanReport()` →
`BargainUIManager.IsCurrentClientCoerced()` → `Refresh()`，纯读快照会重置当前顾客的谈价状态位。

- 删除 `client.can_report`，`client` 段改由 Core `ClientSnapshotProjection` 投影 11 个纯字段
  （`displayName`/`clientIntent`/`useClientBudget`/`clientImpatience`/`isWanted`/`reportReward`/
  `reported`/`wantedPosterId`/`isBargainAvailable`/`clientFaction`/`GetBudget()`）。
- 新增 `SnapshotFieldMap`：声明式登记快照每个键/每个 `GameState.*` 调用/每个服务调用的纯度
  （pure/unknown/impure）；`SnapshotPurityTests` 9 条 L0-static 漂移测试（禁用 token、未登记调用、
  未登记键一律失败）+ 4 条 L1-fake。禁止列表含 `CanReport(`、`CanOffer*(`、`Refresh(`、`RNG.Roll`、
  `ClearTradeRepMultiplierOverrides(` 等。
- `PP-READ-05`：`legal_actions` 只保留 `availability=available`；新增 `candidate_actions`，
  bargain 四向为 `availability=unknown` + `requires=native_guard`（不再把“面板开着”说成合法）。
- `PP-READ-06`：Python `get_legal_actions` 返回两段；旧快照缺字段稳定降级（8 条测试，无 KeyError）。
- 未证明项全部登记为 UNKNOWN 而非 pure：prompt/report/calendar/tutorial 适配器、`GetNegociatedValue`
  的 debug 面板条件写、`Dialogue.IsDisabled` 闭包、`CanSellThisItem` 全链。`Refresh()` 计数真机
  `NOT_RUN`（需插桩构建）。

## C — 交易守卫与提交回执：完成（离线）

- 新增 Core `TradeGuardPolicy`：B10–B16 七个静默拒绝分支逐个预检查（trade_locked / intent_undefined /
  business_complete / item_not_staged / not_owned / already_owned / barter_not_acceptable / not_introduced /
  cannot_afford / gun_order_not_accepted / cannot_buy_from_table / client_refuses_item）。**每次拒绝
  native accept 点击 = 0 次**（fake port 计数断言）。
- 方向判定改用原生 `IsNegociatingItemOwned()`（B12 同源），不再自写 `IsOwned` 当方向。
- 证据分层：`business_complete` + 同一顾客身份（`RuntimeHelpers.GetHashCode`）+ 物品 id；
  静默 return/异常 → `uncertain`（绝不 `ok:true`）；现金变化只是辅助证据（**barter 零现金也能完成**）；
  `items_transferred` 单独记录。
- 交易 Operation 与顾客对话 Workflow 分离：completed/uncertain 都不阻塞 `advance_dialogue`。
- 新增 src/Game `GameTradePort`（只读适配）+ 适配器契约 +12。`PP-TRADE-01..09` 全绿（17 条）。
- 真机点击/以物易物/枪单/情报商 = `NOT_RUN`。

## D — ActionGate 两轴 / Operation / 异步与存档：完成（离线主干）

- 在**现有 `ActionGate`** 上扩展 `OperationRecord`：execution ∈ prepared/running/completed/rejected/
  failed/uncertain，delivery ∈ pending_delivery/delivered/undelivered；不新建第二套 tracker。
- `MarkResponseUndelivered` **只改投递轴**：保留 completed 证据与原始响应体；重复 ID 返回原证据 +
  `delivery_status:undelivered` + `redelivered:true`；旧行为（改写成 `outcome_unknown`）已删除并
  更新测试。已完成的超大响应无法重投时，改由 `GET /operation` 查询证据。
- 新增 `GET /operation?id=`（RouteTable/EndpointPolicy/BridgeServer）：需 Bearer；Bootstrap 可查、
  Locked 403；**不构建快照、不派发**。未知 id → `unknown_operation` + `known:false`；run 变化
  （new_game/load_game）→ 清空 + `process_restarted:true`。
- MCP 新增 `get_operation`/`wait_operation`（工具数 114→116，recording 104→106，policy/inventory/
  测试同步）。`wait_operation` 在 Python 侧有界轮询，超时返回 `ok:false + timed_out + status=pending +
  operation_id + next`，**不阻塞 Unity 主线程、不返回假成功**。
- 同 op 非终态时新 `action_id` → `operation_pending`（不重复派发）；无关写不被死锁。
- D3 异步语义：`end_day` 只有回读 CLOSED 才 completed，仍 OPEN 返回 running/pending；
  `sleep` 分离 `day_changed` 与 `save_evidence`（`last_save_time` 不算落盘证据 → `save_unknown`）；
  `open_shop` 守卫抽到 Core `OpenShopPolicy`（动画锁/非 MORNING/P0-OPEN-01）。
- 真机快门动画/黑屏/落盘时序 = `NOT_RUN`。

## E — 首周 Skill 与离线评测：完成（草稿级）

- `skills/pawnmind-first-week/SKILL.md`：启动/观察/交易/对话/关店/睡觉/租金/错误分类/停止条件；
  legal vs candidate、pending/uncertain 只查 operation、租金只认外部回执。
- `docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md`：可复现规范；通关率/效率 = `NOT_MEASURED`。
- `agent/trace_validator.py` + 77 条测试（18 类违规码）；`test_skill_tool_refs.py` 16 条（AST 校验
  SKILL 工具名真实存在）。
- L3 benchmark = `NOT_RUN`。

## G — 发布/兼容静态预审：完成（静态）

- `docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md`：反射 26 行 / 后台线程 0 / loopback 监听器 1 /
  P-Invoke 0 / Core 中 UnityEngine 0 / 主线程绕过 0（静态）；dist 与已安装 DLL 不一致；
  manifest 0.3.0 vs tag v1.0.1 vs 文档 0.2.1 三处不一致。
- 三个高风险：①manifest 缺 `files` capability（预测被扫描器拒绝）；②`reflection-external` 2026-12-10
  起 retiring，14 处 computed-Type 反射；③dist/源码/已安装不一致使游戏内现象不可归因。
- **更正**：编译 gate 在 CT101 可跑（实测 PASS），不是 `NOT_RUN`；官方 Scanner/APIChanges 仍 `NOT_RUN`。
- Workshop 安装草稿：未上架、未推送。

## F — 独立审阅与集成

独立 reviewer（fresh session、未参与实现；不同 agent）逐提交读 diff 并自己重跑测试后给出：

| 文档 | Status |
|---|---|
| `docs/reviews/NIGHTLY_A_MODE_POLICY_REVIEW.md` | **APPROVE**（首轮发现 A-1/A-2，修复后复审通过） |
| `docs/reviews/NIGHTLY_B_SNAPSHOT_REVIEW.md` | **APPROVE** |
| `docs/reviews/NIGHTLY_C_TRADE_REVIEW.md` | **APPROVE** |
| `docs/reviews/NIGHTLY_D_OPERATION_REVIEW.md` | **APPROVE** |
| `docs/reviews/NIGHTLY_E_SKILL_EVAL_REVIEW.md` | **APPROVE** |
| `docs/reviews/NIGHTLY_G_MOD_COMPAT_REVIEW.md` | **APPROVE** |
| `docs/reviews/NIGHTLY_F_INTEGRATION_REVIEW.md` | **APPROVE_WITH_BLOCKERS**（blockers 均为 L3 真机 / strict play / 发布门槛，非代码缺陷） |

A 的旧 `NOT_REVIEWED` 已补做。审阅发现的问题与处置：

| # | 严重度 | 状态 | 说明 |
|---|---|---|---|
| A-1 | High | **已修复（`7548ddb`）** | Python 策略指纹 30s 缓存只存时间不存结论 → 窗口内第二次写被放行。改为缓存结论；新增连续 3 次拒绝回归测试（reviewer 独立复现 + 复测 3/3 拒绝、0 POST） |
| A-2 | Medium | **已修复（有意的行为变更）** | 桥不报 `policy_sha256` 原被当兼容放行 → 现 fail-closed `policy_unverifiable`；旧 DLL 不能经新 MCP 写。测试夹具/fake_game 统一补报 canonical 指纹；安装草稿已注明“DLL 与 MCP 必须成对安装” |
| D-1/C-1 | Medium | **已修复** | guard 拒绝的 operation 记录曾报 `dispatched=true`；改为按 execution 判定 + 回归测试 |
| E Low | Low | **已修复** | legal_actions 行现在也输出 `availability=available`；fake_game 与 Python 测试钉死 schema |
| C-2 | Medium | **OPEN / L3 NOT_RUN** | B12 方向依赖 `IsNegociatingItemOwned()` 原生语义，需真机确认；方向反了是安全侧拒绝，不会假成交 |
| C-3 | Low | **OPEN / L3 NOT_RUN** | 原生 accept 后立即换/移除顾客实例会被判 `client_changed`（安全但可能漏报成功） |
| B-2 | Low | 记录 | 快照纯度测试是源码正则，跨行/间接调用有盲区；callgraph 已把未知项标 UNKNOWN |
| D-2 | Low | 记录 | `process_restarted` 只表示进程内缓存被清空/run 变化；真重启只能 unknown_operation（无需改代码） |
| E-2 | Low | 记录 | trace validator 在 pending 期禁止任何其他写，比桥更严格（有意保守，建议文档注明） |

## 总测试摘要（CT101 实测，2026-11-17 05:0x）

| 套件 | 结果 | 命令 |
|---|---|---|
| C# 纯逻辑 | **750 passed / 0 failed** | `cd PawnMind && ./test.sh` |
| 契约门禁 | **53 passed / 0 failed** | 同上 |
| 适配器契约 | **503 checks OK** | 同上 |
| Python agent | **365 OK** | `.venv/bin/python -B -m unittest discover -s agent/tests` |
| 策略生成物 | ok（sha256 `e0da5a24…`） | `tools/gen_policy_catalog.py --check` |
| Unity 目标编译 | **Build succeeded** | `dotnet build src/PawnMind.csproj -c Release -p:GameManaged=/root/work/game_managed` |

失败数 0；无删除/注释失败测试；无放宽 guard；`play_allowlist` 仍为 **0**。

## Public schema 变化

1. 快照：`client.can_report` **删除**（用 `reported`/`is_wanted`）；新增顶层 `candidate_actions`；
   `legal_actions` 只含 `availability=available`。
2. 写响应：新增 `operation_id`、`execution_status`、`delivery_status`；`ok:true` 仅当 completed；
   失败/待查含 `dispatched`/`uncertain`/`pending`/`next`。
3. 新端点：`GET /operation?id=`；新工具：`get_operation`、`wait_operation`（116/106）。
4. `confirm_trade`：新证据字段 `business_complete`/`client_unchanged`/`items_transferred`/
   `cash_before`/`cash_after`/`cash_changed`/`staged_item_id`/`client_intent`/`evidence.*`。
5. `end_day`/`sleep`：`shop_closed_after`、`pending`、`day_changed`、`save_evidence`/`save_completed`/
   `save_unknown`。
6. 策略 sha256：`e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc`。

## 能否进入下一阶段

- **可以**进入实机验收（`docs/plans/NEXT_LIVE_SESSION.md`）；离线部分已按停止规则收口，
  独立审阅结论为 A/B/C/D/E/G **APPROVE**、F 集成 **APPROVE_WITH_BLOCKERS**（blockers = L3/发布门槛）。
- **不可以**进入 strict play / 录制 / 发布：
  `play_allowlist=0`、`dist` 未重建且与已安装 DLL 不一致、`PP-READ-01-real` 未插桩、
  官方 ModScanner 未跑、manifest capability 与 reflection-external 到期风险未解决。

## 任务书 12 问

1. **HEAD**：`3369a31` 起步，模块提交 `6ab3333`(B) / `f09314e`(D+C) / `299ecd4`(E) / `f789a92`(G)
   + 收尾 docs 提交（`git log -1`）。相对 `ROUND3_REMEDIATION.md`：`can_report` 确已移除、
   LegalAction 候选化、两轴 Operation、交易守卫均已落地。
2. **是否移除“缺配置默认 Test”**：是，默认 `Locked`，`/health` 报告 `run_mode`/`run_mode_error`。
3. **裸 HTTP 能否绕过读取限制**：否。`/state`、`/events` 在 Bootstrap/Play/Locked 403 且先于
   provider；`/operation` 同样过 `EndpointPolicy`。
4. **C#/Python 是否同源**：是，生成物 + 漂移测试；不一致时 Python 拒绝写入；C# 侧只能自报哈希。
5. **快照是否还可能触发 Refresh/RNG**：已知热点（`CanReport`/`CanOffer*`）**已静态不可达**；
   UNKNOWN 项见 `NIGHTLY_SNAPSHOT_CALLGRAPH.md` §8（4 类适配器 + 2 个 item/dialogue 链），
   真机 `Refresh()` 计数 `NOT_RUN`。
6. **get_legal_actions 是否把候选当合法**：否。candidate 单独列出且 `availability=unknown`；
   旧 JSON 消费者由 Python 兼容层稳定降级。
7. **B10–B16 是否每个分支都有“派发 0 次”测试**：是，`PP-TRADE-03a..g` 7 条 fake 断言
   `AcceptCalls==0`；真机现象 `NOT_RUN`。
8. **confirm_trade completed 与 dialogue workflow 是否分离**：是（`PP-TRADE-04` 两条）。
9. **MarkResponseUndelivered 是否仍改写业务事实**：否，只改投递轴（`PP-ASYNC-02` 三条 + L2 运输测试）。
10. **wait_operation 是否阻塞主线程**：否，Python 侧有界轮询；超时= pending 不假成功
    （`PP-ASYNC-05`、`test_operation_tools.py`）。真实 DSH E2E `NOT_RUN`。
11. **首周最小工具集是否定义**：SKILL 已定义并 lint 校验工具名；`play_allowlist=0`，尚无一个工具有
    live evidence，因此所有工具的“实机可用”仍是 `NOT_RUN`。
12. **剩余 NOT_RUN / 用户醒来后优先做什么**：见 `NEXT_LIVE_SESSION.md`：优先 ①安装本批次新构建并核对
    SHA/MVID ②`/health` + 模式门禁 ③插桩构建测 `Refresh()` 计数 ④交易/end_day/sleep 真机路径
    ⑤官方 ModScanner 与 manifest capability 决策。**不要**在完成 ①②③ 前开录制或填 `play_allowlist`。

## 阻塞项汇总（需要人/真机，代码侧已尽力）

| # | 项 | 状态 | 需要的条件 |
|---|---|---|---|
| 1 | `PP-READ-01-real`（`Refresh()` 运行时计数） | NOT_RUN | 插桩构建 + 游戏进程 |
| 2 | 快照 UNKNOWN 链（prompt/report/calendar/tutorial/item value/dialogue/CanSellThisItem） | UNKNOWN | 真机日志或进一步反编译核对 |
| 3 | `confirm_trade` 真机成功路径与 B10–B16 真实现象 | NOT_RUN | 游戏 + 存档 |
| 4 | `end_day`/`sleep` 真实异步完成时序、落盘证据 | NOT_RUN | 游戏 + 存档文件访问 |
| 5 | `PP-RECOVERY-01` 真实存档回滚后的 op 归属 | NOT_RUN | 游戏 + 存档 |
| 6 | 官方 ModScanner / APIChanges | NOT_RUN | 游戏进程或安装扫描器 |
| 7 | manifest `files` capability 声明 / `reflection-external` 2026-12-10 retiring | 待决策 | 用户决定（声明 capability vs 改造） |
| 8 | dist 与已安装 DLL 不一致 / 版本三处不一致 | NOT_RUN | 用户在场重新构建 + 安装 + 版本号统一 |
| 9 | `PP-ASYNC-05`/E2E 的 DSH 真实链路 | NOT_RUN | 真实 MCP host E2E |
| 10 | First-week benchmark 通关率/效率 | NOT_MEASURED | 真机跑分（禁止编造） |
| 11 | A 的 `PP-MODE-02a/d` PARTIAL（C# 无法验证外部策略；无 hint 负向用例） | PARTIAL | 可后续补强，不阻塞 |
