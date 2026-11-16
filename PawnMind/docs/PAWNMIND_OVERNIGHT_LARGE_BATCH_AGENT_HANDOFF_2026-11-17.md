# PawnMind — 无人值守大批次工程任务书（Nightly Batch A）

> **交给能够访问 PawnMind 完整本地仓库的 Coding Agent / Orchestrator。**
>
> 日期：2026-11-17。输入：`ROUND3_REMEDIATION.md`、`ROUND3_REPORT.md`、`ROUND3_DISPOSITION.md`、`BASELINE_CURRENT.md`、`ADR-PLAYER-PARITY-v2.md`、`PLAYER_PARITY_IMPLEMENTATION-v2.md`、`agent/policy/tool_policy.json` 等本地真实文件。
>
> **用户授权**：可以进行较大批次的**离线工程开发、单元测试、合同测试、集成测试（mock/fake）、静态审计、生成设计/审阅/报告文档、局部提交**；用户休息期间不需要每阶段停下来等待答复。
>
> **用户没有授权**：运行真实玩家存档上的自动操作、覆盖安装或上传 Mod、重启/操作游戏、启用真实录制、购买/支付、修改 Steam Workshop、推送远程、删除/清洗不属于本批次的工作文件。**一律不执行。**

## 0. 总任务与执行规范

本批次不是只做“下一项 D0a”，而是尽量一次性完成**所有可以安全离线推进的依赖工作**，包括：

1. 处理 `ROUND3_REMEDIATION.md` 中遗留的配置与契约问题；
2. S2：C# 与 Python 共源的真实模式门禁（默认安全、预快照拒绝、最终分发防绕过）；
3. S1 补强：完整快照读取路径、移除字段的消费者兼容、结果纯度测试；
4. S3：`confirm_trade` 的强类型守卫、结果证据、Operation 与 Customer Workflow 分离；
5. S4：在现有 `ActionGate` 上推进执行/响应双状态轴、`get_operation` / `wait_operation`、关店与睡眠异步语义；
6. 首周路径的**最小 Skill 与可复现离线场景评测装置**；
7. 静态兼容/发布预审，以及**每个模块独立审阅文档**、总集成审阅与下次真机验收手册。

**成功定义**：每个已修改模块经过真实运行的离线测试；新增测试针对正确/拒绝/异常/超时路径；文档把 `PASS`、`FAIL`、`NOT_RUN` 分开，**不把静态实现等同于实机等价**。不要求本批次交出可用的录制版，不以“测试数越多越好”为目标。

### 0.1 必须先核实的现状（不得照抄旧数字）

`ROUND3_REMEDIATION.md` 的作者报告：

- S1 / D0a 已静态实现：`GetBargainSnapshot()` 仅暴露 `open`，从快照移除 4 个 `can_*` 与 `can_report`；快照读取别名共 6 个：`get_state`、`get_run_summary`、`get_legal_actions`、`wait_for_change`、`wait_for_events`、`wait_until`。
- `ActionRouter.Handle` 已增加**预快照** `ModePolicy`，`PawnMindMod.DispatchAction` 有内部转发二次检查。
- **重大残余风险**：`ModePolicy` 尚未接 `bridge.conf`，运行默认 `Test`，即实际权限仍偏开放。
- Python policy schema 2：`candidate_allowlist` 报告为 73 项，`play_allowlist` = 0；`live_evidence` 均为空。
- `src/Core/SnapshotRules.cs` 在离线测试通过，但 `Refresh()` 真实调用次数与副作用仍未在 Unity 验证。
- 作者所报基线：C# 666、Python 248、contract 491，全离线；实机/Scanner **NOT_RUN**。
- 安装的游戏 DLL 与 `dist` DLL hash 不一致；**任何游戏进程中的现象不能归因于当前代码**。

**第一步请在当前 HEAD 下重新采集。** 若源码/状态已经变化，以当前 HEAD 为准并报告差异，不要覆盖用户已有工作。

### 0.2 并行与依赖

依赖链：

```
H0 基线与工作区保护
  ├── A 模式配置/权限闸门 ──────────┐
  ├── B 快照纯度与 Schema 契约 ─────┤
  ├── C 交易守卫与提交回执（依赖 B 的读路径） ─┤
  ├── D ActionGate 状态机与有界等待（可与 B 并行） ├── F 跨模块集成审阅
  ├── E 首周 Skill + Fake 评测（可独立起草） ────┤
  └── G 发布兼容静态预审（可独立起草） ──────────┘
```

如仓库现有 OpenCode/DSH 多 Agent 环境允许并行：分别安排实现 Agent、测试 Agent、独立审阅 Agent；**同一文件必须单写者，审阅者不要共用实现者的工作草稿作为证据**。没有并行环境则按 A→B→D→C→E→G→F 串行完成。阻塞子任务后继续独立子任务，不应因为没有游戏进程就停止整批。

### 0.3 不可突破的约束

- 保留现有 Core / Bridge / Game / Python MCP / Recorder / Memory 分层；不新建第二个 Agent 主循环或第二套 OperationTracker。
- 禁止修改底层游戏的存档、资金、物品用于伪造验收；禁止绕开原生玩家守卫、插入 debug 路径来做 strict 演示。
- 新增真实游戏写操作时，必须有 `rev` 与 `action_id` 语义；但 `rev` 只是**序列化快照指纹计数**，不是隐藏状态/RNG 的完整版本号。
- 所有原生调用必须符合 Unity 主线程约束；不让同步处理主线程的 HTTP endpoint 等待协程或 sleep/poll。
- 未经真机证实的 hook、字段和原生方法只可标记 `HYPOTHESIS`；不得虚构存在的交易提交事件。
- `no_effect` 的举证责任在服务端：**守卫拒绝且未派发**可以确认；原生调用后看不见变化只能 `uncertain`/`running`，不能猜测无效。
- 旧 `ActionGate` 进程内幂等不是跨重启 exactly-once；落盘 intent 在 Python，不能盲重放未知状态动作。
- 没有游戏环境不影响绝大多数离线工作；实际 UI、MVID、DLL 安装、ModScanner 只记录 `NOT_RUN` 和精确复现步骤。

## H0：冻结基线与安全保护（先做）

**动作**

1. 记录 `git rev-parse HEAD`、`git status --porcelain`、分支、近期提交。若有未提交变更，**不要覆盖、reset、stash、clean、自动切分支**；在报告中说明如何保护并仅修改可隔离文件。若安全可行，可在本地创建独立工作分支；不向远程推送。
2. 记录 `bridge.conf` 中的模式/版本/MVID 字段（不得在文档中泄露 token）、policy schema/哈希、工具枚举数、`GameState` 和 `StateSnapshot` 相关入口。
3. 执行既有测试：`./test.sh`、`.venv/bin/python -B -m unittest discover -s agent/tests`。保存命令、退出码、测试摘要与失败日志文件路径；不要为了全绿删除失败测试。
4. 记录真实存档与安装目录**存在但绝不修改**。本批次允许生成新的 `dist` 文件用于离线编译验证，但不允许覆盖游戏安装目录。

**交付**：`docs/audit/NIGHTLY_BASELINE.md`。

---

## A：S2 配置到执行的强制模式策略（最高优先级）

### A1. 修复默认 `Test` 的 Fail-open

**问题**：当前 C# `ModePolicy` 默认为 `Test`，若服务启动时配置未接通或失效，就可能允许所有操作。务必消除“运行时默认 Test”作为普通启动路径。

**实施要求**

1. 为 `bridge.conf` 接入显式 `run_mode = bootstrap | play | test`（具体语法先核对现有 parser），在游戏进程初始化时加载并绑定；没有配置/非法值/策略缺失/版本不匹配时**fail closed**：禁止普通写操作、特权操作和有风险读取，允许必要的健康查询，并输出可定位的错误。
2. `test` 模式必须是**显式选择**，仅在受控开发配置中允许；不得因缺配置、解析异常、热重载回退到 `test`。
3. 模式切换必须受信任的本地配置控制，**不提供通过 MCP 动作切换到 test 的通道**；改变配置需重启或通过已有可信生命周期触发明确重新绑定。
4. Python policy 的唯一来源应转为**版本化生成物**给 C#（建议构建时生成 C# 常量/资源），或 C# 可靠读取同一 canonical policy JSON。避免两套手写 op 集合。包含 `policy_schema`、`policy_sha256`、`game_version/MVID` 适用范围、模式允许集合。
5. 若 Python 与 C# policy fingerprint 不一致，进入拒绝危险操作的安全状态；接口应可报告不含 secret 的版本/模式/策略摘要。
6. `candidate_allowlist` 只能用于报告与离线验证；**绝不自动转为运行时 `play_allowlist`**。严格模式所需主路径工具无真机证据时不得默默降级到 test，也不得伪造 `live_evidence`。

### A2. 全入口授权，且优先于状态快照

需检查并覆盖：

- `/action` 直接写、`act` 展开再调用、所有其它间接调用 `DispatchAction` 的包装路径；
- **`GET /state`、`GET /events` 等裸 HTTP 读取入口**，特别是 bootstrap 时不能通过直连端口绕过 MCP 工具的 `SNAPSHOT_READERS` 禁令；
- `/health`、`get_operation` 等协议必要的低风险端点要有明确、最小权限，不泄露玩家不可见的隐私或隐藏游戏信息；
- 读操作在 C# 的 HTTP 接口粒度未必一一对应 MCP 工具名，若无法映射，必须以最严格的 endpoint 权限校验（无法可靠区分时拒绝）。

**重要时序要求**：拒绝在 `TryRefreshRevision()`、`StateSnapshot.BuildJson()`、`ActionGate` 意图登记和任何游戏原生调用**之前**发生。已知未送达的相同 `action_id` 重新查询时，要保留历史真实执行证据；授权模式切换不能导致缓存返回越权结果。

### A3. 必测场景（全部离线 fake，若环境允许再起无游戏进程的 loopback HTTP harness）

- `PP-MODE-01a`：缺 `run_mode`、无效值、解析异常不会进入 Test；无游戏侧修改。
- `PP-MODE-01b`：Bootstrap 拒绝 `get_state`、`get_run_summary`、`get_legal_actions`、`wait_for_change`、`wait_for_events`、`wait_until`，且 `/state`、`/events` 裸 HTTP 也拒绝。
- `PP-MODE-01c`：Play 拒绝 `save_game`、`new_game`、`bargain_preview`、`act`；**同一请求 `refreshRevision` 调用计数 0**。
- `PP-MODE-01d`：Test 显式配置可调用测试允许的 op，但未配置不等价于 Test。
- `PP-MODE-02a`：所有未知 op 默认拒绝；策略 C#/Python 哈希漂移时 fail closed。
- `PP-MODE-02b`：模式切换/策略重载后，旧 `action_id` 不被重新派发，已完成证据不被改写。
- `PP-MODE-02c`：HTTP 请求、RPC 包装、内部二次转发统一授权；无“先刷新再拒绝”侧效。
- `PP-MODE-02d`：只读/写权限不依赖 `readOnlyHint`（已知 114/114 该字段为 null）。

**交付/审阅**：`docs/reviews/NIGHTLY_A_MODE_POLICY_REVIEW.md`、测试日志和相关最小提交。

---

## B：S1 / D0a 快照纯度与对外契约补强（最高优先级）

### B1. 补完纯度的“源头”而非只是消除旧字段

已知上一轮从 `StateSnapshot` 移除了通过 `CanOffer*` / `CanReport` 调用 `Refresh()` 的 9 个字段，但这只是**已知热点的静态修复**。请：

1. 重新枚举 `BuildJson → BuildState / BuildFlags / GameState getters` 全部直接和间接调用路径，进行 `pure/unknown/impure` 分类；未知不能默认为 pure。
2. 检查 Snapshot 被调用的位置：正常 GET、Events 长轮询、写操作的 `TryRefreshRevision`、失败时回读、Operation verifier 快照。先建立清单，再做必要的最小修改。
3. 用真实源码对应的 fake/test-double 注入对 `Refresh`/`RNG.Roll`/UI setter 的调用计数，验证允许的读取路径从未调用已知 impure 入口；静态路径与动态 fake 测试互相补充，**不能声称 fake 能证明 Unity 运行时纯度**。
4. 检查 `SNAPSHOT_READERS` 的源码静态扫描是否漏掉间接调用、别名、多层 helper、装饰器变化；必要时改用 Python AST + 显式 endpoint 映射，或维护声明式元数据与调用链 smoke test。
5. 补 `PP-READ-04` 六种别名和 raw HTTP endpoint 的契约一致性。

### B2. 修复 `LegalAction` 语义与 Snapshot JSON 消费者

**核心风险**：上一轮删除了 `can_*`、`can_report`，又让 `LegalAction` 在谈价面板打开时列出四个 bargain 方向，实际最终仍可能返回 `unavailable`。这样 `get_legal_actions` 可能宣称**并不合法**的行为合法；AI 与 Skill 会误学。

请全仓搜索：`can_offer*`、`can_report`、`bargain`、`LegalAction`、`get_legal_actions`、所有 JSON Path / prompt / playbook / test 消费者：

- 区分 `available`（已证实可做）与 `candidate / requires_check / unknown`（尚需原生谓词确认），**不能把“面板打开且可能可做”返回为 allowed/legally executable**。
- 若没有只读纯的合法性 getter，建议将优惠方向改成 `candidate_actions` 或 `availability:"unknown"`，实际写入时再检查原生 guard；不必复制原生公式。
- 若旧消费者必需字段，提供明确兼容策略：schema version/optional/missing reason/deprecation，不要静默填 `true` 或制造假值。
- `get_legal_actions` 不得借机暴露玩家看不到的隐藏信息。

### B3. 必测场景

- `PP-READ-01-fake`：连续 10 次快照构建，不触发已知 `Refresh` / `RNG.Roll` / UI mutation 入口。
- `PP-READ-02-fake`：stale `rev` 被拒后无世界副作用（mock 累计 0）。
- `PP-READ-03`：穷举快照字段源，附不可证明项列表。
- `PP-READ-04`：六个 MCP 读别名、GET `/state`、GET `/events` 与 Bridge 构建链保持一致；变更后漂移测试能失败。
- `PP-READ-05`：谈价面板打开但四个方向实际不可交易，`get_legal_actions` 不能报全部合法。
- `PP-READ-06`：旧字段缺失时 Python MCP / Skill / 报告能稳定降级，无 KeyError/错误建议。
- `PP-READ-07`：顾客切换情形（mock）不允许在纯读路径“帮忙”重置谈判状态；真实 Unity 仅列测试手册 `NOT_RUN`。

**交付/审阅**：`docs/audit/NIGHTLY_SNAPSHOT_CALLGRAPH.md`、`docs/reviews/NIGHTLY_B_SNAPSHOT_REVIEW.md`。

---

## C：S3 交易域的真实语义修复（可离线推进，不能宣称已实机通过）

### C1. 动作范围

仅围绕 `confirm_trade` 核心路径，追加 `advance_dialogue` / `call_next_customer` 的**必要最低限度**守卫/协议适配。不要顺手扩展到所有 58 个写动作。

必须保持：

1. 原生玩家入口调用 `OnOfferAcceptClick` 一次（或仓库真实封装 `AcceptNegotiationOffer()`），不手拼 `BuyItem`/`SellItem` 等 side effects。
2. 明确列出 B10–B16 七类已知静默拒绝分支，逐一用真实原生 predicate（或等价同源判定）预检查；若某分支无法无副作用地查到，则采取 fail-closed / uncertain，而非猜一个自制条件。
3. 提交结果判断：原生调用的返回/异常、物品 ID 归属变化、`IsClientBusinessDone` 等证据分层；**单个现金变化不足以证明交易成功**，以物易物可能零现金变化。
4. `confirm_trade` 的 Operation 到“交易提交”结束。后续顾客对话与离店属于**Customer Workflow**，应可调用 `advance_dialogue`，不得被交易 pending 锁死。
5. 原生异步提交尚未确认时，保持 `running`/`uncertain`，不要凭一个不变快照立即返回 `no_effect`。
6. 按 `client_id / item_id / scene/timeline_id` 关联证据，禁止把另一顾客的变化误记在当前交易下；如果没有可靠身份，要写明局限。
7. 补错误分类：`rejected`（派发前守卫拒绝）、`completed`（可信提交证据）、`uncertain`（已派发但证据不足）、`failed`（能证明没有业务完成的失败）。

**可能的性能优化**：统一 TradeGuard，但不要重写游戏商业规则。若需要大量反射，仅使用本项目现有可验证 API，不能新增受官方 Mod Scanner 拒绝的依赖。

### C2. 测试

- `PP-TRADE-01`：正常成交，随后对话推进并离店；提交完成不依赖离店完成。
- `PP-TRADE-02`：同一 `action_id` 重试不重复提交；新的 `action_id` 试图重复成交必须拒绝。
- `PP-TRADE-03a..g`：B10–B16 七个原生拒绝分支逐项覆盖，派发次数 0。
- `PP-TRADE-04`：成交后有对话、Operation 已 completed 时可 `advance_dialogue`；未提交时不得虚报 completed。
- `PP-TRADE-05`：以物易物现金不变但商品转移，可正确判断。
- `PP-TRADE-06`：原生静默 return，桥不能回 `ok:true`。
- `PP-TRADE-07`：快照不变/后置字段不可取 → uncertain，不得假 no_effect。
- `PP-TRADE-08`：其他顾客/其他物品发生变化不能为本次交易背书。
- `PP-TRADE-09`：旧客户端 `ok` 只在 completed 才为 true，pending/uncertain 不被当作成功。

**交付/审阅**：`docs/reviews/NIGHTLY_C_TRADE_REVIEW.md`；可读的操作证据契约和差异测试输出。不能假装测试时真正点击过 Unity UI。

---

## D：S4 ActionGate / Operation / 异步与存档回执（离线主干，可与 C 分别提交）

### D1. 两轴状态与原有缓存兼容

在**现有 `ActionGate`** 上迭代，不新建第二套 tracker：

```
execution_status ∈ prepared | running | completed | rejected | failed | uncertain
 delivery_status ∈ pending_delivery | delivered | undelivered
```

- `MarkResponseUndelivered` 只改变 delivery 轴，**不得**将已确认的执行结果覆盖成 `outcome_unknown`。
- 不提供跨进程 exactly-once 承诺；Python durable intent 负责记录曾发出过，C# 响应内存负责当次进程事实；重启后的未完成一律禁止自动重放。
- 已完成动作丢 HTTP 响应，重复相同 ID 返回原完成证据及 `undelivered` 注记；不能再执行。
- 执行中出现 timeout 或 HTTP 失败，不能自动改用新的 `action_id` 盲重试。
- 保留现有 `rev`/幂等冲突诊断；回收缓存、过期、场景切换和新的 timeline 要有明确策略。

### D2. Operation 查询/等待

- 增加 `get_operation(operation_id)` 和 `wait_operation(operation_id, timeout_ms)`：查询只读，不修改游戏；等待在 Python/HTTP 客户端有界轮询，不阻塞 Unity 主线程。
- 给出清晰的 unknown operation / expired / wrong timeline / process restarted 返回格式。
- `wait_operation` 如果当前 DSH/MCP 语义可能把 `ok:false + status:pending` 解释成异常，提供不会误报 completed 的兼容层；**没有真实 DSH E2E 时只能记 `NOT_RUN`**。
- 两轴具体响应要保留现有工具的兼容性：`ok:true` 仅代表 `completed`；`pending` 必须有 `operation_id` 与下一步行动建议。
- 只读 Operation 查询不能被错误标为 privileged；但要避免泄露其他 run 的 action 记录。

### D3. 关店/睡觉/开店异步语义

- 在已有 `end_day`/`sleep`/`open_shop` 封装上加完成验证，不绕过按钮 `OnClick` 和必要原生守卫。
- `FireOnShutterClosed*`、`FireOnGoingSleep*` 等只能是过程证据。若 hook 尚未验证，不要写成强成功证明。
- `end_day`：区分请求派发与 shop 真正 CLOSED；`sleep`：独立标 `day_changed` 与 `save_completed`/`save_unknown`；不能把 `last_save_time` 增加无条件视为落盘成功。
- 原生流程仍在异步进行时返回 running，不要一次回读未变就 no_effect。
- 不允许为模拟成功而 force day/settle rent/write save。

### D4. 测试

- `PP-ASYNC-01`：pending → progress → completed；主线程分发无阻塞等待。
- `PP-ASYNC-02`：completed + response undelivered，保留 completed，重复 ID 不重执行。
- `PP-ASYNC-03`：restart 后 Python durable intent 标 unknown 并禁止重放（fake）。
- `PP-ASYNC-04`：双请求冲突与允许的后续对话不会死锁。
- `PP-ASYNC-05`：`wait_operation` 超时返回 pending/uncertain，有 operation_id，不返回假 success。
- `PP-SAVE-01`：day_changed 与 saved 分开；无文件落盘证据时 `save_unknown`。
- `P0-LANDLORD-02-fake`：房东待接待时 sleep/end_day 均拒。
- `P0-OPEN-01-fake`：动画锁/非 MORNING 期间 open_shop 重复调用被拒。
- `PP-RECOVERY-01`：回滚存档、timeline 改变，旧 op 证据不能错归属。

**交付/审阅**：`docs/reviews/NIGHTLY_D_OPERATION_REVIEW.md`、状态机图、兼容性矩阵。若这些更改与 C 的操作状态冲突，以协议一致性优先，拆开独立提交并在 F 中集成。

---

## E：面向首周任务的最小 Skill 与可复现离线评测（不改 Agent 主循环）

**目的**：竞品 Jerod 等项目已经提供面向模型的经营 Skill；PawnMind 不应只堆工具。但 Skill 必须基于已验证接口，不能把 Bridge bug 当游戏规则。

1. 写 `skills/pawnmind-first-week/SKILL.md`（或项目现有 Skill 目录），包含启动/进入游戏、状态观察、普通交易、对话结束、关店、睡觉、租金目标、错误分类、停止条件；每种动作引用真实工具名与更新后的响应 Schema。
2. 将 `candidate_actions` 与 `allowed_actions` 区别写清楚；任何 `unknown/uncertain` 应停止冲突写入并查询 operation 状态，不盲重试。
3. 加 `docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md`：测试初始存档/seed（如果可控）、游戏版本、模型/提示词版本、工具集合版本、成功判据、失败码、成本、回合时长、人工介入次数；**不要把 benchmark 自己跑出来**。
4. 做一个 fake replay / trace validator：检查不重复使用新的 action_id 重试 uncertain 操作；pending 时不会错误调用冲突动作；首周租金需要外部可验证的结算事件/状态，不是模型自述成功。
5. 可做 `ToolMode` 最小模式的离线 lint，不增加泛化 `act` 或绕过当前强类型操作。
6. 对记忆只做读取边界与错误标注，不实施 P3 自动学习/提炼，避免一夜间开新项目。

**交付/审阅**：`docs/reviews/NIGHTLY_E_SKILL_EVAL_REVIEW.md`。所有“通关率”“效率提升”标 `NOT_MEASURED`，不编造演示结果。

---

## G：发布/兼容静态预审（不做实际发布）

1. 阅读仓库内现有官方 ModScanner / `APIChanges.md` / manifest 规范。可使用已有静态扫描工具，但若官方 Scanner 未安装，则记录命令与 `NOT_RUN`；不要用网传兼容性口径替代本地证据。
2. 标记使用反射、动态代码、原生 DLL、后台线程、HTTP listener、打包的 Python/脚本等风险点；整理成文件路径 + 调用 + 替代建议，**不为适配未知规则盲目重写全仓**。
3. `dist` 与已安装 DLL 不一致：只记录，允许构建新离线 `dist` 及输出 SHA256，但**不要同步安装**。
4. 写适合未来 Workshop Beta 的非破坏性安装说明草稿，区分游戏内 Mod 与外部 MCP 安装器；不推送、不上架。

**交付/审阅**：`docs/reviews/NIGHTLY_G_MOD_COMPAT_REVIEW.md`、`docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md`。

---

## F：独立审阅、总集成与停止条件（必须做，不能跳过）

每个模块完成后，指派**独立审阅 Agent（最好全新会话）**审查其实际 diff 和测试；不能只让实现者给自己的代码打分。必须各写一份独立文档：

1. `docs/reviews/NIGHTLY_A_MODE_POLICY_REVIEW.md`
2. `docs/reviews/NIGHTLY_B_SNAPSHOT_REVIEW.md`
3. `docs/reviews/NIGHTLY_C_TRADE_REVIEW.md`
4. `docs/reviews/NIGHTLY_D_OPERATION_REVIEW.md`
5. `docs/reviews/NIGHTLY_E_SKILL_EVAL_REVIEW.md`
6. `docs/reviews/NIGHTLY_G_MOD_COMPAT_REVIEW.md`
7. `docs/reviews/NIGHTLY_F_INTEGRATION_REVIEW.md`

每份按以下固定结构写：

```
Status: APPROVE / APPROVE_WITH_BLOCKERS / REJECT / NOT_REVIEWED
Reviewing commit(s): <sha>
Change scope: <paths>
Real evidence: <commands / code references / test output>
Invariants checked: <names>
Critical findings: <severity, reproduction, impact, fix plan>
Potential regressions: <cases>
NOT_RUN / UNKNOWN: <reasons, needed environment>
Reviewer independence: <different agent? separate session? if not, disclose>
```

### F1. 交叉集成检查

- C# Policy ↔ Python policy：同源、版本/哈希一致、未知默认拒绝；不能出现“Python 隐藏、HTTP 直接允许”。
- Snapshot ↔ LegalAction ↔ Skill：字段删改有消费者兼容，未知不假装 legal。
- Trade ↔ Operation ↔ Dialogue：交易结果完成不等于离店完成；允许合法 continuation，不允许重复确认。
- Gate ↔ Recorder：丢响应不丢业务已知事实；跨重启不盲重试。
- Async ↔ Save：换天不等于落盘成功。
- Play gate ↔ Live evidence：即使离线测试全部 PASS，`play_allowlist` 也不能因为本批次的假证据变成可用。
- Build ↔ Installed：不一致时没有真实游戏验收结论。

### F2. 最终测试与失败处理

1. 合并/串行集成后再跑全量 C# 与 Python 套件，记录确切计数、命令、返回码和日志路径。
2. 做一次 clean build（如果项目环境支持），对政策生成 artifact 做确定性校验（同源同输入输出一致）。
3. 每个新增测试都写明等级：`L0-static` / `L1-fake` / `L2-integration-no-game` / `L3-real-game NOT_RUN`。
4. 若某模块无法通过，**不得偷偷注释测试或放宽 guard**。保留可复现失败信息；若风险较大回退该模块的本批次变更（不动他人/既有提交），其余已通过模块继续交付。
5. 禁止用记录在 JSON 的 `live_evidence=[]` 或用 fake 测试填补 live evidence。

### F3. 人不在场时允许与不允许自动进行的事项

**允许自主继续**：离线代码修改、mock/contract 测试、静态审计、Skill 草案、审阅、普通本地提交、小范围回滚本批次未通过的更改、按记录修复新测试问题。

**必须停在边界并登记**：实机点击、真实录制、租金支付、对原始存档读写或覆盖、安装覆盖 DLL、重启用户的游戏/服务、修改或公开 Workshop、执行不可逆脚本、第三方账户变更、未审核的插件安装、下载未知程序并运行。

**不要求用户醒来后补答大量问题。** 对缺游戏环境的问题直接 `NOT_RUN`，写清下次可执行命令和必要步骤；其他独立任务照常做完。

## 交付包（最终缺一不可）

请生成并提交：

- `docs/reports/NIGHTLY_BATCH_A_REPORT.md`：按 A/B/C/D/E/G/F 分章，完成项、未完成项、提交 SHA、改变的公共 Schema、总测试摘要、当前失败数、能否进入下一阶段。
- `docs/audit/NIGHTLY_TEST_MATRIX.md`：每个 `PP-*` 的层级、执行命令、结果 `PASS/FAIL/NOT_RUN`、证据链接；禁止空白当 PASS。
- `docs/audit/NIGHTLY_BASELINE.md`：本批次前后 HEAD 和工作树情况、版本/策略/工具数。
- `docs/reviews/NIGHTLY_F_INTEGRATION_REVIEW.md`：所有模块的跨层互斥与回归风险。
- `docs/plans/NEXT_LIVE_SESSION.md`：用户醒来后一次性执行的实机验收清单，尽量让用户只需操作最少步骤。重点检查新版 DLL 安装一致性、`/health` MVID、`PP-READ-01/02`、`PP-MODE-01`、交易拒绝、房东、动画开店和 save evidence。
- 上述 A/B/C/D/E/G 各自独立审阅文档。没有独立 reviewer 时明确写“self-review”，不能冒充独立。

### 推荐提交颗粒度（仅本地，不 push）

```
nightly: baseline and policy-source review
fix: fail-closed runtime mode configuration and endpoint gate
fix: pure snapshot contract and legal-action semantics
feat: trade guard and outcome evidence protocol
feat: actiongate execution-delivery state and bounded wait
feat: shop/day operation states (offline semantics)
docs: first-week skill and offline benchmark
review: independent per-module reviews and nightly report
```

### 最终报告必须回答的 12 个问题

1. 当前 HEAD 是什么？相比 `ROUND3_REMEDIATION.md` 有哪些差异？
2. 是否移除了运行时“缺配置默认 Test”的危险回退？配置失败怎样降级？
3. HTTP 的 `/state` / `/events` 能否绕过 Bootstrap/Play 的读取限制？
4. C# 与 Python 的 allowlist 是否确实来自同一个版本化策略？不一致时如何拒绝？
5. Snapshot 构建是否还可能直接/间接调用 `Refresh()`、`RNG.Roll()` 或其它 getter side effects？哪些还 UNKNOWN？
6. `get_legal_actions` 有没有把“候选”误标为“合法”？旧 JSON 消费者是否兼容？
7. B10–B16 的每个分支，是否有 `派发0次` 的失败测试？
8. `confirm_trade` 的 completed 和 dialogue workflow 是不是分离的？
9. `MarkResponseUndelivered` 是否仍会改写已完成业务事实？
10. `wait_operation` 是否会阻塞 Unity 主线程？模拟超时/重启是否安全？
11. 当前最小首周工具集合定义了吗？哪几个尚未实机验证？
12. 哪些真实游戏/Scanner 项目仍是 NOT_RUN？用户醒来后应优先做什么？

## 终止规则与产出标准

**达到以下条件即可结束本批次并交付，不需要继续扩展：**

- 能安全离线实现的 A/B/C/D 已尽量完成；对于缺少原生明确证据的局部实现，明确 `BLOCKED` 而非硬造；
- E/G 已形成实用草稿/审阅；
- 测试全量重跑且结果可复现；
- 每模块至少一份审阅文档 + 综合审阅；
- 当前 `play_allowlist` 不因任何离线测试被自动标为正式 live-ready；
- 未执行真实游戏、修改存档、安装覆盖 DLL、发布或远程推送。

**不允许做的“捷径”**：减少测试/删除失败用例、硬编码所有工具为合法、复制原生业务流程以绕过 hook 缺失、把 pending 当 completed、把 fake 证据写成 live、把按钮方法被调用误认作业务完成、用提示词替代 C# 门禁。

---

## 给主 Agent 的最短执行指令（可与全文一起粘贴）

> 阅读 `PAWNMIND_OVERNIGHT_LARGE_BATCH_AGENT_HANDOFF_2026-11-17.md` 和所列当前仓库文件。用户现在离线，授权你在**不触碰真实游戏/存档/安装/发布/远程**的边界内完成最大化安全的离线开发批次。不要每项都等待批准；按依赖推进 A/B/C/D，并行完成 E/G。每项必须有可运行测试、实际 diff、**独立审阅文档**，最后全套测试与跨模块审阅。任何真机要求记 `NOT_RUN` 并继续其它任务。不要声称 strict_player 就绪，不要把 fake 证据填为 live。最终只提交 `NIGHTLY_BATCH_A_REPORT.md`、审阅文件索引、测试矩阵、commit 清单和下次实机验证步骤。
