# PawnMind RFC-002：闭源 Unity 游戏 MCP 桥接的玩家语义等价、异步操作与可执行验证体系

> **文档状态**：外部技术评审稿（Proposal / Review Required），非已批准实现方案  
> **版本 / 日期**：v1.0 · 2026-11-17  
> **预期读者**：PawnMind C# Mod / Python MCP / DSH 维护 Agent；架构审查者  
> **适用版本**：用户报告的 *Probably Stolen* Unity Mono **0.46D**；实际游戏二进制 MVID、Mod 版本和源码函数签名必须由仓库再次核对  
> **首要任务**：保证 AI 能通过 MCP 完成“从新局菜单开始、连续正常经营、支付第一周租金、录像”的玩法，而不是通过漏洞越过游戏流程  
> **变更原则**：小步实施、以事实和测试闭环为准；不得据本 RFC 假定新 API 已存在；不得未经批准改动真实玩家存档。

---

## 0. 如何使用这份 RFC

### 0.1 证据分层（审阅者必须保持区分）

| 标记 | 含义 | 可否直接作为实现事实 |
|---|---|---|
| `FACT-USER` | 用户在本轮技术简报中报告的实机行为、反编译结果、已修状态 | 只能作为**待独立复核的项目输入**；仓库中的实际代码和运行测试仍是最终依据 |
| `FACT-AUDIT` | 较早的 PawnMind P0 审计及真实 MCP Schema 快照 | 属历史基线，不能自动套用于 0.46D 新版本 |
| `RECOMMENDATION` | 本文提出的架构/测试建议 | 需 Agent 审核后形成 ADR；不能视为已实现 |
| `HYPOTHESIS` | 需要通过 IL、反射、运行时事件或差分测试确认的推断 | **不得**被写入“已验证”的游戏机制知识库 |
| `OPEN` | 目前证据不足以决定的取舍 | 需要在评审报告中给出证据、选择与拒绝理由 |

本次**没有获得新版本的完整 PawnMind 源码或真实录像、IL、日志、游戏存档**，因此以下代码为协议草案或伪代码，不是假装已经读过实际实现。所有具体类型、方法、字段均需按照真实源码修改。

### 0.2 输入资料与当前基线

**本轮用户提供的技术简报**（此 RFC 的主要依据）：

- Unity Mono 游戏 0.46D；C# Mod → loopback HTTP → Python stdio MCP → DSH。
- `src/Core/` 纯逻辑，`src/Bridge/` 负责传输/授权/版本与动作，`src/Game/` 才能触及 Unity 类型；Unity 主线程执行游戏操作。
- 所有状态修改动作应使用 `rev` 和 `action_id`；写入审计 intent 失败时不向游戏派发动作。
- 实机事故：`sleep` 绕过房东、`confirm_trade` 丢失送客流程、`open_shop` 重复发奖励、救援 `dismiss_current_client` 绕过房东、静默成功、伪只读推进 rev/RNG。
- 已报告修复：`end_day`、`open_shop`、`sleep` 的一项限制、`dismiss_current_client`、`advance_dialogue`、`close_windows`；**并未提供这些修复的当前源码或验收记录**。
- 新报告的写 op=58；审计分类 A=22、B=43、C=15（可能交叉）；已修 B 类 P0 6、P1 4，称剩余 33。

**较早文件（只作历史上下文）**：`CAPABILITY_AUDIT.md`、`ADR-001-memory-architecture.md`、`mcp-tools-full.json`、`mcp-tools-recording.json`、`tool-matrix.md`、`P0_ARCH_REVIEW_PawnMind.md`。旧审计是工具 100/录制 94，BridgeClient 三入口 `/action /state /events`，日志和原生幂等缓存的持久性边界已被讨论。**版本升级后必须重采集 `tools/list`，不能拿旧计数冒充现况。**

### 0.3 需要输出的评审结果

请维护 Agent 在阅读 RFC 后**先审阅，后编码**：

1. `../reviews/PLAYER_PARITY_RFC_REVIEW.md`：按“接受 / 修改后接受 / 拒绝 / 待证实”逐章评估，所有反对意见给出具体证据。
2. `../audit/PLAYER_ENTRY_MATRIX.csv` 或 `.yaml`：当前**真实** op/原生入口/守卫/可观察效果/异步类别/测试场景矩阵。
3. `../ADR-PLAYER-PARITY.md`：架构取舍，尤其是 operation 所有权、journal 所有权、主线程执行规则、Hook 准入和运行模式。
4. `../plans/PLAYER_PARITY_IMPLEMENTATION.md`：分阶段任务与退出门槛，明确哪些工作阻塞下一次真实录像。
5. 对所有尚未验证的游戏函数，不编造结果；列 `UNKNOWN` 并说明需要的取证手段。

> **重要**：不要把本 RFC 误读为“立即重写 100 个工具”或“给所有游戏函数加 Hook”。它提供增量迁移的目标模型，而非推倒重来的强制重构计划。

---

## 1. 问题定义：玩家等价不是函数名等价

### 1.1 三个容易混淆的目标

1. **Action parity（操作权限等价）**：Bridge 不能让 Agent 触发玩家在同一游戏状态、相同解锁条件、相同 UI 交互约束下无法触发的业务动作。
2. **Outcome / trace parity（关键语义及行为轨迹等价）**：相同初始条件下，玩家路径与 Bridge 路径产生等价的合法性判定、领域事件、资源变化、进度与后续可操作性；异步事件可在允许的时间窗口内到达。
3. **Observation parity（信息等价）**：Agent 在严格模式下不能通过只读 MCP 看到玩家尚不能获得的隐藏信息、未来事件、尚未解锁的属性；其读取本身不能改变 RNG、任务或世界状态。

“与玩家**完全**等价”在闭源且可能含随机/非确定性游戏中不宜作为未经限定的可证明断言。工程目标应是：对定义清楚的可观察关键性质达到**有边界的可证伪等价**。在每份行动契约中列明哪些差异可容忍（例如 UI 动画帧数），哪些差异不可容忍（房东消失、凭空生成物资、重复扣款、推进 RNG）。

### 1.2 原先三条原则的评价与替代

原三条：

1. 调玩家那个方法；
2. 查玩家那个谓词；
3. 做完回读确认真的发生了。

上述是**有价值但不充分的实现启发式**：

- `OnClick` 可能绕过 Unity UI 的 `interactable`/焦点/面板前置条件；一个 OnClick 可能按游戏阶段执行不同动作。
- 单个 `CanXxx()` 可能只覆盖某一步，UI 交互、动画锁、所选物品、业务状态与串行化边界也属于玩家动作合法性。
- 单次回读“没变化”不能推出失败（协程尚未完成、等价幂等操作、现金不变的以物易物）；“有变化”也不能推出成功（游戏自发电费或随机事件也可能变化）。

**推荐以以下五条约束替代**：

| ID | 不变量 | 判定要求 |
|---|---|---|
| `INV-01 LEGALITY` | **不越权**：只有当前真实玩家有合法路径可达的业务动作才允许提交 | 每个 action 有可审计的玩家入口、完整守卫、游戏内时序限制；真正派发前复查 |
| `INV-02 SEMANTICS` | **不漏事**：调用后完整保留业务序列与关键副作用 | 交易收尾、顾客离店、租金/存档/奖励/任务进度等具名契约可验证 |
| `INV-03 OBSERVABILITY` | **不虚报**：区分提交请求、原生执行、领域效果、全流程完成和未知 | 不以 `OnClick` 返回/`rev` 增长/现金变化任一单信号断言成功 |
| `INV-04 RECOVERY` | **不危险重试**：超时和异常时不做不确定的重复写动作 | 同一 op 在代理层保持稳定 action_id、意图落盘、核对后再继续；无法判定时隔离 |
| `INV-05 READ_PURITY` | **观察不改变玩法**：严格只读不得改变任何会影响后续游戏的状态或 RNG | 对每种读工具提供纯度证明或标记为非纯并移出严格模式 |

辅助约束：`INV-06` 宿主通过同一游戏实例发起的相互冲突写动作必须串行；`INV-07` 故障/录像/记忆系统不得把 Bridge bug 当作正常游戏机制；`INV-08` `strict_player`、`assisted`、`test` 能力互相隔离。

### 1.3 非目标

- 不重建整个原版游戏状态机或复制所有游戏公式。
- 不为每一次只读请求建立冗长的人工按帧模拟。
- 不将 `postfix called` 直接等同于业务成功。
- 不实现“通用异常后回滚”，尤其禁止自行补金币、补物品以伪造原子性。
- 不要求 LLM 理解每个隐藏守卫并自行遵守；安全性不能建立在提示词可靠性上。
- 当前阶段不改长期记忆的 LLM 提炼、向量检索或 Planner，除非为避免错误经验污染必须加最小隔离标记。

---

## 2. 现有事故的安全性质与修复证明

### 2.1 事故矩阵（用户报告事实，仍需当期回归验证）

| 事故 | 被绕过/遗漏的环节 | 风险 | 最小修复证明 |
|---|---|---|---|
| `INC-01 sleep` 7 天跳房东 | 睡觉路径未继承玩家真正的房东/强制顾客处理条件 | P0：正常进度失效、错误游戏知识 | 房东待处理状态时，所有实际可用的跨日入口都不能无故绕过；正常交租路径可达 |
| `INC-02 confirm_trade` 卡客 | `BuyItem/SellItem` 未触发 `CompleteBusiness`/接受对话/送客 | P0：业务死锁 | 真正成交后出现交易事件、对话/送客、下一顾客最终可接待 |
| `INC-03 open_shop` 双开刷资源 | 直接 `OnShutterOpened()` 绕过 UI 开门锁与动画保护 | P0：资源复制 | 动画期重复开门尝试不再产生第二次每日奖励或客队重建 |
| `INC-04 dismiss_current_client` 送走房东 | 救援工具漏 `IsClientDimissable()` | P0：越权修复 | 房东或不可送离对象一律拒绝；普通可送离对象按原生流程成功 |
| `INC-05 silent success` | 原生 `void` 方法静默 return | P1：模型状态机失真 | 所有关键 no-op 有明确拒绝/等待/已证实 noop 语义，不能泛报 `ok:true` |
| `INC-06 false read` | `Refresh()` 改 UI/rev；`RNG.Roll()` 改随机流 | P0/P1：决策污染、跨步 stale_rev | 连续只读不增加世界关键状态/消耗真实 RNG，或严格标为状态修改并禁止自动读取 |

### 2.2 已报告修复的复核清单

不要直接将“已经调用 `CloseShutter()`”认定为 `end_day` 玩家等价：实际玩家入口为 `OnClick()`，需要再次验证 UI enable、动画锁、枪战/逮捕、客队和二次点击等守卫是否等价；如果已通过其他守卫补齐，要有原生代码与测试对应关系。

`open_shop -> OnClick()` 是合理方向，但验证同样需要覆盖 UI 可达性、前置动画锁及重复开门；应确认无“失败时手拼调用”的 fallback。

`dismiss_current_client` 仅加 `IsClientDimissable()` 还不够；要验证玩家能否在同一 UI 上实际选择送客，以及业务收尾状态。

`advance_dialogue` 的 `no_dialogue` 需要明确对话刚结束、加载中、面板已关闭等边界，不得仅凭管理器存在认定有活跃对话。

**高危复核（必须修正）**：报告称 `sleep` 已添加 `outside == false`。这只解决“夜间外出丢地面物品”，**没有从文字证明房东不可绕过**。请优先检查真正的睡觉 UI 入口、顾客 dismissability、是否已处理强制事件；切勿把营业阶段的 `CanEndDay()` 无条件照搬到夜间。

**未证实修复**：简报中 `confirm_trade -> OnOfferAcceptClick()` 为“打算修改”，不得把它算成已完成。关键验收包括多种 clientIntent、贸易被锁、以物易物、顾客成功离店和游戏状态不可逆部分提交。

### 2.3 修复统计口径

- `A=22, B=43, C=15` 相加为 80，明显不是 58 个写 op 的互斥分组。需标明 A/B/C 是多标签，或者重新给出互斥统计。**不能用其和直接当作写 op 总数。**
- 若 B 缺口 43 个、修复 10 个且无重复计数，剩余 33 可以成立；需以 **唯一 defect_id** 计数，而不是“工具名 × 缺陷数”混计。
- 旧录制工具数量、写操作数量和本次 0.46D 数量不得未经核对混用。

## 3. 建议架构：保持三层，新增统一 Operation 执行与结果确认能力

### 3.1 推荐逻辑结构

```text
DSH（唯一 AI 决策循环）
 └─ PawnMind MCP Server（stdio, Python）
     ├─ tools/list, 向后兼容现有强类型工具
     ├─ Tool Metadata / Action Contract Registry（版本化）
     ├─ Recorder / Durable Intent Journal（SQLite，已有 Memory DB 可复用）
     ├─ Operation Client（短等待、状态查询、断连核对）
     └─ HTTP Bridge Client
          ├─ POST /action（原有入口，逐步复用统一 executor）
          ├─ GET /state /events（纯读取或声明观察性）
          ├─ GET /operations/{id}（建议新增）
          └─ GET /health
             ↓ localhost + auth
Unity Mod / Bridge（C#）
 ├─ ActionRouter（白名单/模式门禁）
 ├─ MainThreadDispatcher（所有游戏对象读取/调用在主线程）
 ├─ OperationTracker（一个实例的操作生命周期、冲突域锁）
 ├─ PlayerActionAdapters（业务领域）
 │   ├─ ShopLifecycle    开门、关门、睡觉
 │   ├─ TradeLifecycle   交易/谈判/以物易物/送客
 │   ├─ Dialogue         对话/关闭 UI
 │   └─ ...（按真实仓库逐步扩展）
 ├─ PassiveObservation / DomainEventTap（只读、最小侵入）
 └─ StateSnapshot（区分纯只读字段与可能有副作用的 getter）
             ↓
 原生游戏 UI、业务方法、协程、存档、RNG
```

**职责边界**：

- **Python** 是动作意图的耐久账本、MCP 兼容性与等待层。每次派发前把 `operation_id/action_id` 写入本地持久存储；超时不自动重试副作用性动作。
- **C#** 是真实 Unity 业务状态的观察与执行边界；最终合法性校验、原生调用、领域事件关联、主线程异步检查发生在这里。C# `OperationTracker` 可以先是进程内状态，不必一开始就引入 C# 持久数据库。
- **DSH** 仍唯一制定游戏策略，不承担基础安全职责。DSH 不必记住何时 `wait`、何时不能重试；这些限制由服务端实施。
- **Memory MCP** 消费确认事件及异常事件，但不参与决定一次 Unity 原生动作的执行成功与否。

**禁止**：Python 和 Unity Bridge 分别独立实现一套“交易成功判断”并互相矛盾；正常请求线程阻塞 Unity 主线程等待自己的协程；未知情况下创建新 `action_id` 进行静默重发。

### 3.2 谁是 Operation 的事实权威？

需要区分：

- `Intent authority`：Python durable journal（发出过什么请求）。
- `Native execution authority`：Unity 主线程上的执行观察、领域事件（原生真正做了什么）。
- `Reconciliation authority`：一套明确的、按动作类型版本化的 verifier（哪些信号足以支持哪个结论）。

**不存在跨 Python SQLite 和 Unity 游戏世界的自动原子事务**。因此只能保证：在调用通路被遵守、SQLite durable intent 成功的条件下才派发；不能保证任意崩溃点都有 exactly-once 的原生业务效果。对于“发出请求但无法证实执行与否”的窗口，应恢复为待核对/隔离，而不是声称实现了 exactly once。

### 3.3 最小增量改造选项

建议从方案 A 起步：

- `A（低侵入）`：保留现有 `/action`，在 C# 内为四个试点动作增加统一检查与 OperationTracker；Python 外面增加 durable journal、短等待和 `get_operation/wait_operation`；暂不改非试点工具响应结构。
- `B（后续可选）`：所有改状态 op 都走统一执行器，`/action` 使用同一个 OperationTracker，统一响应协议。
- `C（暂不推荐）`：完全重写 Unity Mod 以 Hook 代替方法调用、重新开发 LLM Orchestrator、为所有动作编排大状态机。

### 3.4 版本与兼容性

必须记录每个操作的：`game_version`, `game_assembly_mvid`, `mod_version`, `bridge_contract_version`, `action_contract_version`。版本不匹配时风险动作 fail-closed（保留只读诊断能力）。

旧工具可以保持名称不变，先在响应中增加可选 `operation_id/status`；对于原有调用者可能把 `ok:true` 当完成的情况，必须通过 schema 版本或能力协商避免破坏性静默行为改变。**不兼容变更应有测试，不得仅修改提示词。**

---

## 4. 统一 Operation Lifecycle（Q1 的直接方案）

### 4.1 不要把“已受理”和“已完成”混在一个 `ok`

推荐状态（名称可调整，语义必须保留）：

| 状态 | 精确定义 | 是否终态 | 能否重复派发 |
|---|---|---|---|
| `prepared` | action_id 已生成，参数和身份已写入 durable intent，但尚未发送 | 否 | 由调度器决定；对恢复流程需先核查是否发过 |
| `dispatching` | 请求传输已开始，是否被游戏受理尚未明确 | 否 | **不**生成新 action_id 自动重试 |
| `rejected` | 已确认尚未产生业务副作用，因前置条件/版本/权限而拒绝 | 是 | Agent 调整参数或条件后可新发；旧 op 不重做 |
| `running` | 游戏主线程已接受/启动原生操作；可能处于跨帧步骤 | 否 | 不允许重复提交相同/冲突业务动作 |
| `completed` | 动作契约规定的关键效果已确认且达到完成门槛 | 是 | 不需要；同 op_id 再查只返回既有结果 |
| `failed` | 有充分证据表明没有成功完成，且已知安全状态（可含明确部分效果报告） | 是或需特殊恢复 | 不自动重做；依据契约决定是否能安全重新开始 |
| `uncertain` | 可能部分或全部执行，但证据不足以判定 | 保护性状态，可恢复后再结案 | **严禁自动重做** |
| `cancelled_before_dispatch` | 可证明未派发时取消 | 是 | 可以新建另一个操作，但不得复用旧操作 ID |

注意：对 `running` 已超过客户端等待窗口，只代表“等候超时”，**不意味着 `uncertain`**；只要 Bridge 明确知道协程仍在运行，就保持 `running`，返回 `pending` + 查询 ID。对于 Bridge 进程失联、操作关联证据丢失才进入 `uncertain`。

### 4.2 推荐时间线（关键：发出意图前/主线程原子检查）

```text
Agent: call confirm_trade(item_id=42, rev=182)
  Python: generate operation_id/action_id
  Python: validate tool metadata, mode, params
  Python: append durable intent + commit transaction
  Python: POST /action {same action_id, rev, ...}
  Bridge: enqueue work request to Unity main thread
  Unity main thread:
      read actual rev + mode/authority + full player guard
      if fail => reject with reason (no game mutation)
      acquire business conflict gate
      attach operation context, call native entry ONCE
      set running or directly completed if proven
  Python: short bounded wait, using non-Unity thread
       if terminal -> return result
       else -> return pending + operation_id
  Later: domain event / postcondition / timeout / crash reconciliation
  DSH: get_operation/wait_operation when necessary
```

**主线程要求**：`rev` 校验与前置条件及提交必须按 Unity 主线程的连续执行单元完成，避免 HTTP 线程“先查再排队”造成 TOCTOU。不要在 Unity `Update`、Coroutine、主线程 Dispatcher 中使用阻塞等待、同步 I/O 或睡眠等待自身游戏状态。

### 4.3 MCP 接口契约（示意，不假定 DSH 已支持特殊协议）

保持现有 `confirm_trade()`，新增统一信息结构：

```json
{
  "ok": true,
  "operation_id": "op-uuid",
  "action_id": "act-uuid",
  "status": "running",
  "phase": "post_trade_dialogue",
  "accepted_rev": 182,
  "latest_observed_rev": 183,
  "confirmed_effects": ["trade_committed"],
  "pending_effects": ["client_departed"],
  "next_poll_after_ms": 250,
  "can_retry": false
}
```

这里的 `ok:true` **只能表示 MCP 请求已被有效受理**，不是 `completed`。若旧客户端将其视作成功，应在正式切换前使用新字段并进行兼容测试，必要时提供版本化结果结构。

建议新增工具：

```python
get_operation(operation_id: str) -> OperationStatus
wait_operation(operation_id: str, timeout_ms: int = 3000) -> OperationStatus
```

`wait_operation` 应在 Python/HTTP 线程侧采用正常等待 / 条件变量 / 长轮询，不占 Unity 主线程。**等待超过 deadline 只返回当前状态**，不隐式取消游戏协程，更不重新派发操作。

操作的可见范围必须绑定 `instance_id`、授权 token、以及必要时 `session_id`，不能随便读取另一实例的操作结果。

### 4.4 自动等待：模型不需要参与正常短延迟

建议采用两段等待，但**以下数值仅为待实测初始配置，不是游戏定律**：

- 普通立即性动作：服务端短窗口核对（例如 100–500 ms）；
- 关店/睡觉/电话等跨帧动作：Python 端有界等待约 3–5 秒；
- 等待后仍在运行：返回 `running/pending`、`operation_id`、`phase` 与 `next_poll_after_ms`；
- 同一冲突域写动作被拒 `operation_in_progress`，并返回阻塞的 operation_id；
- 不等待或无法等待的 DSH 版本：立即返回 pending + 查询工具，靠服务端防重入兜底。

不要将数字写死为全局阈值；每个业务域可配置 `expected_duration`/`deadline_policy`。录像可能受机器性能与帧率影响，应测试慢帧和切后台。

### 4.5 冲突域与超时恢复

最小安全实现可以是**同一游戏实例只有一个 mutating operation in-flight**；读状态不阻塞。待打通后，可按 `shop`, `trade`, `dialogue`, `save/load`, `machine` 拆冲突域，必须证明没有隐式共享状态（UI、顾客对象、RNG、订单等）。

禁止存在“后续 `get_state` 导致先前操作自动标 completed”的隐式无约束行为；状态机与证据关联必须有契约。

必须覆盖的断连窗口：

1. intent 已 commit、HTTP 未发送；
2. HTTP 已发但 C# 未处理；
3. C# 已执行，响应丢失；
4. 协程已开始，Python/DSH 重启；
5. Unity/Mod 重启，C# 内存 OperationTracker 丢失；
6. 领域效果已产生、Python 写 outcome 失败。

这些场景中的恢复目标不是“一律自动完成”，而是**不重复执行、保留证据、允许安全继续或要求人工处理**。若没有跨 Unity 重启的 durable 事件确认，不得谎称能自动消除所有不确定性。

### 4.6 MCP Tasks 扩展的取舍

2026-07-28 MCP 将 Tasks 放在 `io.modelcontextprotocol/tasks` 扩展，而非默认核心能力。**不要假设 DSH、dsh-mcp-client、当前 Python SDK 已协商支持**。检查实际握手及 SDK；若无支持，保持普通 MCP 工具 `wait_operation/get_operation` 即可。原生 Tasks 可以作为未来适配，但内部 Operation 状态机不依赖它。

---

## 5. Outcome Verification（Q2）：证明哪个效果发生了，而非只比一次快照

### 5.1 三信号模型

每项 `ActionContract` 应声明：

1. `native_invocation`：原生入口是否被实际调用，调用前 guard 是否通过；**仅证明执行路径**。
2. `domain_event`：某个游戏业务效果是否确实提交，例如成交、交租、客户退场、存档完成；需确定该事件源的语义。
3. `postcondition`：关键世界状态是否符合结果要求（物品归属、交易记录、客户可继续流转、现金、日期等）。

验证器应为每个动作定义**必要信号组合**，不是给统一的 `if cash changed: success`。

### 5.2 四类信号可信度

| 级别 | 例子 | 合理结论 | 禁止的跳跃 |
|---|---|---|---|
| 调用证据 | `OnOfferAcceptClick()` 已被调用 | 调用了入口 | 一定成交 |
| 中间业务信号 | `CompleteBusiness()` 已执行且上下文匹配 | 顾客业务推进 | 一定离店 |
| 领域提交事件 | 原生库存转移/租金入账记录，带因果关联 | 该业务效果产生 | 其他收尾也一定完成 |
| 稳定后置条件 | 客户最终不在台前、可以再接下一位 | 流程完成 | 改变必定由最近 MCP 调用导致 |

### 5.3 交易的分阶段契约

`confirm_trade` 不应只输出一个 `succeeded`：

```text
validate guard / correct counter / clientIntent
      ↓
call OnOfferAcceptClick ONCE
      ↓
trade_commit observed? ── no ─→ rejected/uncertain by evidence
      ↓ yes
transaction_committed (item and value semantics)
      ↓
CompleteBusiness / accept dialogue / dismissal progress
      ↓
terminal_business_flow (client departed or contract-defined terminal)
      ↓
operation completed
```

例如以物易物 `cash` 可能恒定，因此检查**交易 ID、物品所属集合/所有权、特定业务事件与顾客状态**，不以现金差值作为充分或必要条件。库存数量相同也可能只是物品对换；必须尽量使用物品唯一 ID 和交易双方上下文。

若原生发生了交易提交而收尾流程失败，结果应保留 `confirmed_effects=[trade_committed]`、`pending/failed_phase=post_trade`，禁止再次提交交易。

### 5.4 事件 Hook：只在关键领域使用、分层验证

建议优先级：

1. 原游戏已有稳定的 `ModHook.FireOnXxx`，且其调用点确实位于业务完成后：优先订阅/读取。
2. 可从真实游戏对象**被动读取**的业务结果：优先读值，避免改变世界。
3. 仅在必要且经过验证时添加 Harmony Prefix/Postfix/Finalizer 的只读插桩；不使用 SkipOriginal、修改参数、吞异常、Transpiler 替代交易流程。

注意：Hook 被触发并不是业务成功证明——如果它位于可能提前 return 的入口，它只证明方法被进入或返回；需要检查真实 IL/源逻辑中的执行位置。

对 C# Mono 游戏，Coroutine 的后半段运行在后续帧中；一个静态 thread-local/调用栈里的 `operation_id` 不能自动跨帧传播。需要显式 `OperationTracker` 把 `client_id/item_id/domain/frame/sequence` 和可能匹配的原生事件关联。仅在“唯一匹配事件且时间窗/上下文成立”时宣称归因；否则记录 `unattributed`。

### 5.5 观测证据结构（样例）

```json
{
  "evidence_id": "ev-405",
  "event_type": "trade_committed",
  "game_instance_id": "instance-A",
  "operation_id": "op-0081",
  "client_id": "client-5",
  "item_id": "item-42",
  "frame": 135792,
  "sequence": 141,
  "observed_at": "2026-11-17T02:00:00+08:00",
  "source": "native_domain_event",
  "source_method": "to_be_verified",
  "confidence_basis": "exact_operation_context"
}
```

字段中的 `source_method` 需填已经查实的方法；不要把 Hook 名字当作事件语义的充分证明。`sequence/frame` 对异步先后顺序有用，但不能单靠相近帧号判定因果。

### 5.6 区分 done、no_effect 与 uncertain

- `completed`：契约列明的必要业务结果已证实；对预期没有状态变化的幂等动作，应定义有证据的 `already_in_desired_state`。
- `rejected/no_effect`：**必须有证据表明业务动作未发生**，例如主线程前置拒绝且未调用原生入口。
- `uncertain`：调用可能已开始，无法排除部分效果；不能因为一次回读不变就用 `no_effect`。
- `failed`：契约判定失败且能报告已发生的部分效果；无证据的假“失败”会诱发危险重试。

### 5.7 异步后置条件：从等待到稳定

对 `sleep/end_day` 等：

- `start_condition`：观察到正确的输入状态和原生触发。
- `progress_condition`：黑屏/卷帘门/对话等进入中的合法阶段；它可能不可读。
- `completion_condition`：游戏已发生跨日、结算、对应存档/客流处理，达到契约完成标准。
- `stability_condition`：连续观测达到稳定，或收到权威的 completion 事件；不要求所有字段跨帧完全相等（时间戳等会变化）。

如果无法证明“存档完成”，`sleep` 可以已跨日但保存仍 `unknown`；不要把两者合成一个未经验证的成功结论。

## 6. Q3：半状态、异常、回滚与幂等边界

### 6.1 不可承诺不存在的事务能力

游戏对象更新、UI/Coroutine、Python SQLite 日志三者**没有共享事务**。一个动作可能在调用途中抛异常，但已经修改金币、库存、任务或 UI 状态。主张“异常时自动回滚”必须先证明游戏提供原生、覆盖相关副作用且不会损坏存档的事务接口；目前证据不足。

**推荐的安全恢复顺序**：

1. **停止新写**：按业务冲突域锁住后续有风险操作。
2. **冻结与采集**：记录异常类型/堆栈、调用前后状态、最近领域事件、operation_id、当前版本、关键对象 ID；避免读副作用函数。
3. **对账**：以动作契约检查哪些效果已经发生、哪些未知、哪些可排除。
4. **决策**：安全完成则结案；已有业务提交但收尾失败则进入明确的半状态；只有已验证的原生补偿路径才允许自动恢复。
5. **人审/停机**：缺乏安全动作时停机并保留诊断，不能为了让试玩继续而偷偷调用 `debug_grant` 等管理入口。

### 6.2 Operation Journal 的最低字段

建议 SQLite 与现有 Memory/Event DB 配合，**迁移前先核查项目已有 Event Store，避免重复建平行账本**。如需新增表，最小示意如下：

```sql
-- 仅为 schema 草案；请依据现有 migration 机制适配。
CREATE TABLE IF NOT EXISTS game_operations (
    operation_id TEXT PRIMARY KEY,
    action_id TEXT NOT NULL UNIQUE,
    instance_id TEXT NOT NULL,
    session_id TEXT,
    timeline_id TEXT,
    game_version TEXT,
    game_mvid TEXT,
    tool_name TEXT NOT NULL,
    arguments_json TEXT NOT NULL,
    requested_rev INTEGER,
    status TEXT NOT NULL,
    phase TEXT,
    prepared_at TEXT NOT NULL,
    dispatched_at TEXT,
    accepted_at TEXT,
    completed_at TEXT,
    last_observed_at TEXT,
    terminal_reason TEXT,
    last_error TEXT
);

CREATE TABLE IF NOT EXISTS operation_evidence (
    evidence_id TEXT PRIMARY KEY,
    operation_id TEXT,
    evidence_type TEXT NOT NULL,
    evidence_json TEXT NOT NULL,
    observed_at TEXT NOT NULL,
    capture_quality TEXT NOT NULL,
    FOREIGN KEY (operation_id) REFERENCES game_operations(operation_id)
);

CREATE INDEX IF NOT EXISTS ix_operation_status
    ON game_operations(instance_id, status, prepared_at);
```

SQLite 建议 WAL、短事务、`busy_timeout`、versioned migrations；**绝不**保持事务打开去等待网络或 Unity 协程。敏感字段如 Bearer token 不得记录到 event 日志或录像字幕中。

“intent 先落盘”要求 SQLite commit 结束后才 dispatch。可按风险配置同步策略；对不可逆高风险动作至少使用经故障注入验证的 durable commit。恢复测试必须区分“durable commit”与仅写入 Python 缓冲区。

### 6.3 不能把 action_id 当成跨重启 exactly-once

历史审计中服务端幂等缓存只在进程内。若游戏进程重启，缓存丢失，**原 action_id 重发也未必安全**。即使 Python DB 对 action_id 唯一，也只能阻止本地重复发送；不能从无响应推断 Unity 没执行。

对于 `dispatching` 后断联：

- 有权威提交事件/后置条件可确认 → `completed`；
- 有权威守卫拒绝且没执行 → `rejected`；
- 仅看见不变的状态快照 → 仍可能 `uncertain`；
- 原生游戏提供可核对的唯一交易记录时 → 可按合同归因；
- 没有证据 → **不自动重发**。

这不是实现缺陷，而是当前缺少跨系统事务时必须诚实接受的恢复界限。

### 6.4 真实存档的防护

试录制必须使用专用 recording slot，保留原始玩家存档备份与只读 manifest。测试过程不得自动覆盖用户正常存档。`load_game`、`confirm_new_game`、`save_game` 等需被列为明确授权的高风险操作；如果是游戏玩家无法手动执行的能力，应只在 `assisted/test` 模式可用。对于“游戏只在睡觉时保存”的事实，请再次核实当前版本。

---

## 7. Q4：只读纯度与不推进 RNG 的 Observation 设计

### 7.1 工具层级必须重新标注

| `read_class` | 定义 | 允许在 strict_player 自动调用吗 |
|---|---|---|
| `pure_read` | 不改 World、UI、RNG、rev、任务、存档、可见状态 | 可以 |
| `passive_observation` | 读取游戏自然产生、已缓存的信息，不触发额外原生业务逻辑 | 可以，须验证缓存来源 |
| `effectful_query` | 查询时运行 `Refresh()`、抽 RNG、引发 UI/业务变化 | **不应**混在普通只读；显式改名并按写动作治理，严格模式默认不自动使用 |
| `test_only_inspection` | 读取玩家正常不可知信息或使用诊断捷径 | 只允许 test/管理侧 |

风险等级和只读纯度为不同维度。**rev 未变也不能证明纯读取**，因为游戏 RNG 状态、隐含缓存可能变化但不推动 rev。

### 7.2 四类实现策略的排序

1. **被动状态镜像（推荐默认）**：在原生游戏已有正常更新/界面显示时捕获生成的结果，再由 MCP 读静态快照。
2. **直接读取已存字段**：经反射/源码证明字段 getter 无副作用，且信息在玩家可见权限内。
3. **纯计算的复制/快照计算**：只用于完全确定性且有版本差分测试的公式；不得在原生 RNG 流上重新抽样。
4. **显式 effectful_query**：如果无法避免副作用，必须暴露“这是会变更游戏状态的动作”，执行守卫/审计/权限策略，且 strict_player 应默认禁用或按玩家实际能触发的等价动作建模。

“按 rev 缓存”可以减少多次副作用，但**并不能消除第一次读取产生的副作用**；若 rev 不覆盖 RNG/UI，缓存键甚至不能可靠决定值是否失效。

### 7.3 `bargain_preview` 示例

- 检查 `GetBargainSuccessChance()` 中 `Refresh()` 具体修改的对象、是否每次触发 UI 开启、是否消耗随机性。
- 找到 UI 已经正常显示的成功率字段时，优先读取其被动快照。
- 若需要计算，先验证是否只是纯概率公式；复制时做版本对照。
- 对外记录 `observed_rev` 与 `observation_source`，但“读后 rev 更新”不得被包装成纯读取成功。

### 7.4 `get_clientele` / `RNG.Roll()` 示例

- 不允许把会抽取真实游戏 RNG 的预测称作只读。
- 如果游戏正常刷新客户信息时会生成预测，Bridge 只读取已生成结果。
- 如果没有生成，返回 `unavailable` 或清楚标注为“离线估算”，而非通过真实 RNG.Roll 取样。
- 若希望重建随机结果，必须先证明存在独立、不会影响世界 RNG 的纯克隆计算通路；不建议第一版尝试。

### 7.5 读纯度回归测试

从相同已知初始状态 A/B 出发：

- A：执行 `read_tool` 0 次后进行一段确定性游戏流程；
- B：执行相同 `read_tool` N 次后执行同一流程；
- 比较场景变化、随机序列代理指标、顾客列表/奖励/事件轨迹。

如果缺少固定种子、精确 RNG 状态或可重复测试存档，则该测试只能提供部分保证；应如实记为 `read_purity_unproven`，不能仅凭连续两次 `rev` 相同宣称验证通过。可先通过 IL 数据流审计确认读取是否调用 RNG 或 `Refresh`，再决定需要多大范围的真机测试。

---

## 8. Q5 与 Q8：动作可执行性、隐式规则和模型接口

### 8.1 不暴露所有内部变量；暴露决策有用的 affordance

`/state` 现在已经 6–10 KB（用户报告）。不建议追加几十个内部字段就算完成。模型真正需要知道：

1. `allowed`：这个具体动作能否执行；
2. `blocked_by`：主要阻塞条件与当前可公开的证据；
3. `next_steps`：符合玩家操作路径的合法解阻动作；
4. `expected_effect`：完成后应该观察到什么；
5. `rev` / `observed_at`：该检查适用的快照边界。

建议**按需查询** `get_action_options(action, params)`，不要每帧返回 100 个动作的大矩阵。获取 affordance 不替代真正派发前在主线程重新校验。

### 8.2 输出契约示例

```json
{
  "action": "confirm_trade",
  "allowed": false,
  "observed_rev": 181,
  "blocked_by": [
    {
      "code": "item_not_on_back_counter",
      "target_id": "item-42",
      "explanation": "该物品尚未放到交易台",
      "resolutions": [
        { "tool": "move_item", "args_hint": {"target": "back"} }
      ]
    }
  ]
}
```

`resolutions` 是**建议而非自动执行命令**，不能越过游戏动作权限和前置条件。若真实游戏不允许观察具体守卫原因（例如隐藏剧情），严格模式下只能返回模糊的 `not_available`，不能泄露隐藏状态。

### 8.3 隐式规则的正确载体

对 C 类 15 项，建立：

- 一份版本化 **Action Contract Registry**（声明关系，不重写整个游戏状态机）；
- C# 端实现实际前置检查、按原生流程执行；
- MCP 的 `blocked_by`/`next_steps` 作为机器可读解释；
- `AGENTS.md` 作为模型的行为指导，而非唯一强制安全层；
- 为每条依赖生成至少一个“缺少前置时拒绝”的自动用例。

例如：`confirm_trade` 依赖 `item_on_back_counter`；讨价还价依赖 `negotiation_open`、正确意图；机器操作依赖供电/未锁/正确模块；彩票兑奖属于不可逆物品销毁，需要 higher risk。每条依赖**必须有可证明的原生字段/谓词/玩家交互来源**。

### 8.4 不要变成第二套游戏逻辑

以下做法应该拒绝：

- 在 Python 重写 `CanEndDay`、租金结算、NPC 送客和库存规则并把它作为真相来源；
- 只靠 LLM 提示词安排“先做 A 后做 B”，C# 却允许直接越过 B；
- 将静态脚本状态机当作原生游戏状态的唯一来源。

Action Contract 是**行为边界和证据计划**，不是替代游戏的业务执行器。

---

## 9. Q7：复制、调用与特权操作的准入标准

### 9.1 三个模式（由服务端强制，不仅靠 MCP tools/list）

| 模式 | 权限范围 | 目标 |
|---|---|---|
| `strict_player` | 玩家合法路径可达的业务操作 + 玩家可获得的信息；录制/日志作为外部观察手段 | 正式 AI 试玩、玩家等价评测 |
| `assisted` | 经过明确授权的非玩家辅助能力，例如额外保存检查点、调试观察 | 联调、故障复现 |
| `test` | 专用测试槽、固定条件、假数据、可能有特权的状态构造 | 自动化契约与差分测试 |

必须在 C# Bridge 授权层执行模式限制，不能因为模型看不到工具就推断它不能调用；泛化 `act` 和 `debug_*` 的间接路由仍需独立检查。有关录制模式下 `act` 的历史风险审计仍然适用，但新版本必须重新验证。

### 9.2 复制/绕过原生步骤的判据

只有同时满足以下条件，才考虑复制公式或直接调用更底层接口：

1. 没有合适且可控的原生等价入口，或为了纯读取必须避免原生危险副作用；
2. 要复制的逻辑是有界、确定性、可从已可见状态重算；
3. 不产生真实游戏业务状态修改、额外 RNG 抽样或隐藏信息泄露；
4. 已有同版本原生实现的对照测试以及版本变更时的失效检测；
5. 文档明确标记该能力 `strict_player` 可否使用。

绝对不建议复制 `confirm_trade` 整段交易/送客流程；对于 `save_game` 这类玩家无法手动到达的能力，应该放在 `assisted/test` 并明确授权，而不是硬说等价。

### 9.3 快速动画与玩家等价

为了录像节约 2.75 秒而跳过开门动画，可能改变锁的持续时间、调用次序、资源发放/客户到访时序，因此不能默认算“只优化表现”。正式 `strict_player` 应走原生流程。测试模式可做时间加速，但必须对业务轨迹做差分验证，并在录像/评测中注明模式，不把加速结果当作正式玩家行为证据。

---

## 10. Q6：可执行的“玩家等价”回归体系

### 10.1 总体结构：四层而不是一条测试

| 层 | 运行环境 | 能证明什么 | 不能证明什么 |
|---|---|---|---|
| L0 静态/契约 | 纯 C# / Python / IL 解析 | API 存在、契约字段齐全、原生引用未漂移、危险入口封锁 | 行为与玩家等价 |
| L1 Fake / Model Based | fake game + 单元/属性/状态机测试 | Recorder 与执行器协议、异步时序、安全拒绝、恢复行为 | 原版游戏真实隐藏副作用 |
| L2 Unity 实机负向/正向 | 专用测试存档、Mod/Player 测试驱动 | 真实守卫、正向进度、异常行为、对原生变化的准确捕获 | 未覆盖路径的完备性 |
| L3 UI vs MCP 差分 | 相同可克隆初态，玩家真实 UI 路径 vs MCP | 在测试范围内的业务可观察等价 | 无法保证所有随机序列、未覆盖剧情或任意版本 |

**一个测试不通过不允许通过补写提示词将其标绿**。必须修 C# 合法性/执行/观察层或明确禁用该工具。

### 10.2 每个写动作的一页契约

推荐 `../contracts/actions/*.yaml`，并由注册表生成风险矩阵与测试清单。示意：

```yaml
schema_version: 1
contract_id: shop.confirm_trade.v1
tool: confirm_trade
game_version_scope: "0.46D"      # 实际须匹配 MVID
mode: strict_player
native_reference:
  component: NegociationUIManager
  entry: OnOfferAcceptClick
  player_reachable_path: "real-ui-click"
  proof: "NEEDS_VERIFICATION"
semantics:
  stage: customer_at_counter
  guard_sources:
    - IsTradeLocked
    - clientIntent
    - item_on_back_counter
  implicit_dependencies:
    - negotiation_context_ready
  conflicting_domains: [trade, client, ui]
  is_async: true
  irreversible_effects: [item_ownership, cash, customer_business]
expected:
  commit_evidence: [trade_committed]
  completion_evidence: [client_business_terminal]
  counter_evidence: [trade_rejected]
  fields_for_reconcile: [item_owner, client_id, business_complete]
  may_have_zero_cash_delta: true
retry:
  safe_automatic: false
  on_transport_timeout: query_operation_then_reconcile
  on_native_exception: quarantine
verification:
  reference_cases:
    - V-TRADE-001
    - V-TRADE-002
    - V-TRADE-003
  status: unverified
```

不能要求 contract registry 自动“调用”虚构的 `trade_committed` 原生事件；真实证据源要由下一步审计填写。对于不同 `clientIntent`，需要多条子契约/测试场景，而非一个笼统的交易成功判断。

### 10.3 实机差分测试协议

**准备阶段**：

1. 创建备份的专用测试存档，不接触玩家正常槽。
2. 记录游戏版本/MVID、Mod hash、配置、场景、资源、时间与可观察 RNG 控制方式。
3. 定义共同初始状态：顾客、物品、台面、租金、UI、锁、动画阶段；不能只以 `day` 相同作为同态。
4. 定义本场景必须相等、允许不同以及不可观察的字段；一份测试对应明确的 Oracle 和比较谓词。

**Reference 分支**：由真实可点击 UI、输入映射、拖拽完成路径触发玩家操作，不允许通过反射直接调用被测 `OnClick` 作为唯一参考，否则测试双方可能一起绕过 UI 可达性限制。

**MCP 分支**：在相同受控初态调用工具，记录桥入口、领域事件、状态快照、实际视觉/交互后果。

**比较阶段**：

- 动作是否都允许/拒绝；
- 是否发生同一类业务提交、客户/物品/租金/现金等关键结果；
- 所有预期异步阶段是否最终完成；
- 不允许出现额外奖励、重复客户、额外 RNG 前移、非法 UI 操作；
- 不能将 `rev`、时间戳、Unity 实例内存地址等不稳定字段逐字比较；
- 对随机结果，优先固定条件，否则比较不变量及统计，而不是武断判定相同 ID。

### 10.4 必须固定的回归用例（最小集合）

| ID | 场景 | 预期结果 | 环境 |
|---|---|---|---|
| `P0-LANDLORD-01` | 不可送离的房东排队时 `end_day` | 拒绝或进入与真实 UI 一致的阻塞 | Unity L2/L3 |
| `P0-LANDLORD-02` | 房东待处理状态尝试 `sleep` | 不越过强制交互；异常路径不能跳过 | Unity L2/L3 |
| `P0-LANDLORD-03` | `dismiss_current_client` 对房东 | 拒绝且对象仍在、进度未被跳过 | Unity L2/L3 |
| `P0-OPEN-01` | 2.75 秒开门动画期间重复开店 | 无额外晨间道具/客户重建 | Unity L2/L3 |
| `P0-OPEN-02` | 不应允许开店的阶段调用 `open_shop` | 不出现开门副作用 | Unity L2/L3 |
| `P0-TRADE-01` | 正常成交与对话/送客 | 全流程最终可接待下一顾客 | Unity L2/L3 |
| `P0-TRADE-02` | `IsTradeLocked` / intent 不合法 | 原生没有提交；准确拒绝 | Unity L2/L3 |
| `P0-TRADE-03` | 以物易物现金未变化 | 根据物品/交易事件判定完成 | Unity L2/L3 |
| `P0-TRADE-04` | 成交已提交但对话尚未结束 | `running`，不得再次提交 | Fake + Unity |
| `P0-DIALOG-01` | 没有当前对话时 advance | `no_dialogue`，不能 `ok completed` | Fake + Unity |
| `P0-READ-01` | 连续调用 bargain_preview | 不改变 UI/rev/下一动作合法性（strict） | Unity |
| `P0-READ-02` | 反复读取 clientele | 不推进真实 RNG 流（需要可观测的证明方式） | Unity |
| `P0-REV-01` | stale rev 在 Unity 主线程派发前 | 拒绝，零世界业务副作用 | Fake + Unity |
| `P0-EXCEPTION-01` | 原生提交一半后抛异常 | 保留部分效果，隔离冲突写，不二次执行 | Fake；Unity 有安全可构造场景才测 |
| `P0-TIMEOUT-01` | 交易执行后 MCP 响应丢失 | 可用 operation ID 核对；无自动重复 | Fake + 故障注入 |
| `P0-ASYNC-01` | 关店协程跨帧 | 正常返回 pending/完成，不把延迟误报 no_effect | Fake + Unity |
| `P0-ASYNC-02` | 睡觉已换天但未确认保存 | 区分 day_changed / save_unknown | Fake + Unity |
| `P0-SEC-01` | 调试 `debug_*` 经泛化 op | strict 模式不可达，状态不变 | Fake + 静态路径检查 |
| `P0-SAVE-01` | 非测试权限直接调用手动保存 | strict 模式拒绝（若玩家无该能力） | Fake |
| `P0-UI-01` | UI button disabled 但 OnClick 可直接反射 | Bridge 必须拒绝 | Unity L2/L3 |
| `P1-OBS-01` | 与 MCP 操作无关的随机事件同时改现金 | 不会错误归因交易成功 | Fake + Unity 可构造 |
| `P1-DB-01` | SQLite 写 intent 失败 | 没有 dispatch；可审计失败 | Fake + 故障注入 |
| `P1-RECOVER-01` | Python 退出/重启、Unity 不重启 | 重新获取上次 operation 结果或保持 uncertain | Fake + 集成 |
| `P1-RECOVER-02` | Unity/Mod 重启、幂等缓存丢失 | 禁止盲目重发，按证据 reconciliation | Fake + 集成 |

要求每个测试报告包括：`test_id, code_commit, game_mvid, initial_state_fingerprint, reference_trace, mcp_trace, diff, oracle, evidence_link, result`。

### 10.5 负向测试优先于“成功路径演示”

当前事故的共同特征是 **Bridge 可以在玩家不合法的状态下绕过约束**。因此每个高风险动作至少有：

- 一条正向合法场景；
- 一条显式 guard 拒绝；
- 一条邻接边界状态（UI 动画/对话/加载/异步中）；
- 一条超时或异常恢复；
- 如涉及隐藏约束，再补最不直观的一条越权尝试。

让高优先级负向 case 先通过，再讨论 LLM 是否会顺利“玩一周”。

### 10.6 IL/反编译守卫审计的正确作用

IL/方法体 Hash **只能提醒已审计的原生入口或守卫在版本升级时可能变化**，不能证明动作语义等价。建议维护 `player_entry`, `guard_methods`, `hook_sites` 的成员身份和方法体 hash；MVID 或相关 IL hash 变化时自动把依赖契约标记 `needs_revalidation`，重新跑受影响的真机测试。Hook 会受内联、运行环境与第三方 Mod 影响，版本差异下只能作为需要重新验证的风险信号。

### 10.7 测试优先级与有限预算

- 一开始不要求 58 个写 op 全部做 UI 差分场景；第一组做 `confirm_trade`, `sleep`, `end_day`, `open_shop` 四个典型操作，加 `dismiss_current_client` 的房东拒绝。
- 之后按交易、顾客、商店生命周期、跨日/保存、机器/电话、彩票等**业务域**扩展，而不是工具名的字母顺序。
- 每修复一个底层通用 guard/OperationTracker 组件，就重跑引用它的整个业务域测试，而不是只重跑曾经报错的工具。
- 对未验证工具，Recording/strict_player 使用显式 allowlist；不能因为“纯逻辑测试通过”就允许高风险真机执行。

## 11. 33 个缺口的迁移策略：按“根因与业务域”而不是逐工具打地鼠

### 11.1 先清楚定义缺陷，而不是先估计工时

建议在 `../audit/PLAYER_PARITY_GAPS.yaml` 统一记录：

```yaml
- defect_id: PARITY-001
  tools: [sleep]
  domain: shop_day_transition
  defect_class: missing_guard
  risk: critical
  current_version: "0.46D"
  evidence: "user_report_2026-11-17"
  root_cause: "bypasses mandatory landlord interaction"
  proposed_fix: "align with native sleep UI reachability"
  safety_mode: strict_player
  state: needs_verification
  tests: [P0-LANDLORD-02]
  native_methods: ["NEEDS_CODE_AUDIT"]
  code_owner: "TBD"
  closure_evidence: []
```

`state` 应区分 `reported`, `reproduced`, `root_cause_confirmed`, `fixed_unverified`, `verified`, `reopened`；仅修改代码不能直接标 `verified`。一份缺陷可能关联多个工具，一份工具可能关联多个缺陷。A/B/C 要作为独立标签列，不可混用总数。

### 11.2 根因分类建议

| 根因族 | 典型事故 | 复用修复设施 |
|---|---|---|
| `missing_player_guard` | 房东被 sleep/dismiss 跳过、UI 不可点击 | PlayerReachability + main-thread guards |
| `bypassed_business_lifecycle` | 交易后不送客、关店不清台 | PlayerActionAdapter + domain lifecycle contract |
| `reentrant_side_effect` | 重复开门刷资源 | conflict gate + idempotency + animation-state guard |
| `silent_noop` | dialogue/trade `ok:true` | OutcomeVerifier + explicit rejection causes |
| `async_completion` | sleep/end_day/phone timer | OperationTracker + bounded wait |
| `effectful_read` | `Refresh()`/RNG.Roll | PassiveSnapshot + read-purity gate |
| `implicit_ui_dependency` | 物品必须上 back 台、谈判 UI 打开 | Affordance/BlockedBy + native dependency guards |
| `privileged_operation` | 手动保存/debug/特权工具 | server-side mode permissions |
| `version_drift` | 游戏升级后守卫/Hook 变了 | game MVID + method-body change alert + targeted retest |

根因分类要求可叠加；例如一个交易 op 可能同时有隐式 UI 依赖、静默 noop、异步完成三个问题。

### 11.3 推荐交付阶段（允许根据审阅证据调整）

| 阶段 | 工作内容 | 交付 | 必须满足的阶段出口 |
|---|---|---|---|
| **D0 基线确认** | 重新枚举 0.46D 的 MCP 工具；真实调用链/原生入口、旧修复 diff；现有测试结果和 recording 安全状态 | 当期 `tools/list`、版本清单、事故-代码证据矩阵 | 已知 6 个事故对应的函数/守卫有明确来源，统计口径和缺口 ID 可解释 |
| **D1 P0 禁止越权** | 先解决 sleep 房东、交易收尾、重复开门、不可送客、debug 泛化入口、隐藏只读 RNG 污染 | 功能修复 + 负向验收 | 已知可致死锁/刷资源/跳付租流程在 strict 入口不能复现；未修的高风险工具显式禁用 |
| **D2 Operation 垂直切片** | `operation_id`, durable intent、Unity 主线程 guard、pending/get/wait、reconcile、冲突域 | 先做 trade/end_day/sleep/open_shop 四个 op | 假游戏断连/超时/崩溃用例通过；真实异步动作不会虚报 no_effect 或自动重做 |
| **D3 Outcome 领域观察** | 最小必要领域事件 Hook、后置条件、非归因事件 | Trade/Customer/Shop/Day 事件证据 | 能区分“成交已发生”与“顾客已离店”、跨日与存档；同时发生的无关事件不误归因 |
| **D4 Action Registry + Diff Testing** | 可执行契约、UI 参考驱动、场景构造、版本漂移检查 | 试点 4+1 操作的真机差分绿灯 | 存档/随机控制方案可复现，关键轨迹差异有解释，原生路径不使用同源绕过 oracle |
| **D5 批量治理** | 按业务域关闭其余 P1/P2 缺口和 C 类隐式依赖 | 每个写工具的契约 + 状态/测试证据 | Recording/strict_player 的所有暴露写工具都有风险等级、合法性/完成判据；无法验证的按策略隐藏 |
| **D6 真实一周录像** | 新局菜单、经营、交租、录像、审计一致性核对 | 录像、MCP trace、领域事件 trace、状态快照、回放报告 | 正常交租、无已知 P0 越权、无未解释重复副作用；未知结果保持可追踪而非遮掩 |

**阶段依赖**：D0/D1 是 D2 的输入，但 D1 修复完已知 P0 后可开始小规模试点集成；D3 只 Hook 真正需要的领域。D4 的测试 harness 可以与 D2 并行开发；D5 不能用“工具都已编译”代替验收。D6 前应先通过关键 L2/L3 用例。

### 11.4 最小改动比新框架优先

若现有 `ActionGate`、`BridgeClient`, fake_game、事件 recorder 已满足某些要求，**复用**。本 RFC 不要求另外创建一套同名 Operation Executor。实现前先做 gap map：

```text
Requirement → Current owner → Existing code path → Missing behavior → Proposed patch → Tests
```

有可靠现成机制则标 `reuse`，没有才新增。对相同职责避免“两套执行循环/两套来源真相”。

---

## 12. 发布与录像前的安全门槛

### 12.1 Strict Player Recording 门槛

下一次正式实机周目录制，至少应具备：

- [ ] 确认游戏版本/MVID/Mod 版本与审计一致；toolset 和 recording allowlist 固化。
- [ ] `sleep/end_day/dismiss_current_client` 不能在原生玩家不可越过的房东/强制顾客状态下跳过。
- [ ] `open_shop` 重入测试无双倍每日资源、无多一批顾客。
- [ ] `confirm_trade` 已从内部资源转移改为完整原生业务路径，包含被锁/意图/以物易物分支与送客流程的真实测试。
- [ ] 非纯读不会在 strict 下被作为读工具自动调用，尤其确认 `bargain_preview` / `get_clientele`。
- [ ] 关键写操作有 action_id，至少有已知冲突防重入和未知结果停机/对账逻辑。
- [ ] 提供完整 `game_run_id/timeline_id/session_id`（若已有记忆事件系统），防止跨周目归因错乱。
- [ ] recording 存档独立、可备份、被覆盖时有显式授权；调试特权路径在 recording 下不可达。
- [ ] 对 5 类已知事故有实机可复现的回归测试；不能仅是 mock 绿灯。
- [ ] 实际录像、MCP trace、关键业务事件能够按时间线对齐。

严格模式第一阶段可以降低暴露工具数量：**宁可只开放已验证的经营主路径，也不要将没有足够守卫的所有写工具默认开放给模型。**

### 12.2 可量化项目指标（建议，不伪装成当前已达成）

- `HighRiskIllegalActionAccepted = 0`：在构造的全部高危非法状态中，不允许成功提交业务动作。
- `VerifiedTradeToClientTerminalRate = 100%`：在真实游戏被定义为应终止的正常交易场景中，最终进入合法顾客终态；需注明案例数量与超时窗口。
- `DuplicateDailyRewardOnReentrantOpen = 0`。
- `FalseCompletedOnTimeout = 0`：超时未证实完成不得返回 completed。
- `UnknownAutoRedispatch = 0`：未知结果不产生未经许可的新写请求。
- `StrictReadKnownWorldMutation = 0`：已知世界/RNG 副作用的读取不能在 strict 读面出现。
- `HighRiskCoverage`：每个暴露高风险工具至少有一正一负真机场景；需报告分子/分母，不可只报百分比。
- `ParityDefectsReopened`：已修缺陷因后续重构复发的个数，用于判断是否仍在打地鼠。

这些指标是**门槛建议**，不是对未知路径的形式证明。测试覆盖只代表已覆盖状态中的证据，而非整个游戏的数学完备性。

### 12.3 发布回退

- 统一 Operation 机制必须支持 feature flag：先在 fake 和测试存档打开；发现异常可回退到旧实现**但不允许重新启用已知越权的旧路径**。
- 对未完成的 operation，禁用冲突写，不能在版本回退时自动丢弃 pending/uncertain 记录。
- 新 Hook 可以单独关闭，关闭后结果核对应降级为 `uncertain`/状态观察，不能继续声称“事件已确认”。
- 录像失败时保留失败证据而不是覆盖旧日志；不得为追求录像完成调用作弊修复。

---

## 13. 与长期记忆系统的接口

### 13.1 防止把 Bridge Bug 学成游戏规则

已见典型：LLM 从 `sleep` 绕过房东学到“可以不交租连续睡七天”。这必须归类为 `bridge_failure`，而不是 `verified_game_mechanic`。建议最终事件同时记录：

```json
{
  "event_type": "game_action_outcome",
  "operation_id": "op-0081",
  "game_run_id": "native-run-id",
  "timeline_id": "timeline-id",
  "outcome_status": "completed",
  "capture_quality": "domain_event_plus_postcondition",
  "parity_status": "verified_for_tested_contract",
  "contract_id": "shop.confirm_trade.v1",
  "bridge_defect_ids": [],
  "eligible_for_memory_learning": true
}
```

`eligible_for_memory_learning` 必须由**确定性门槛与审计状态**计算，不能由模型自行声明。已知越权/执行未确认/跨版本数据默认只能供调试和问题分析；跨时间线的“未来库存”等必须继续隔离。

### 13.2 证据与知识的保守分级

- `confirmed_native_event`：游戏确实发生了某个领域提交，但不代表对其因果解释成立。
- `bridge_incident`：桥的越权/假成功/半状态；不可晋升为正常游戏机制。
- `partial_observation`：只知道快照改变，不能归因。
- `gameplay_hypothesis`：AI 根据正常游戏观察提出的待验证规则。
- `verified_rule`：满足额外游戏机制来源或受控实验标准的规则；两次相似观察不自动晋升。

修复桥 bug 后，应根据 `bridge_defect_ids` 找到受影响的历史经验并撤销/降级，保留原始不可变事件以便复盘。Operation 结果、领域事件与 Memory claim 之间保持单向可追溯引用，禁止反向由记忆宣称动作成功。

---

## 14. 给审阅 Agent 的强制提问（请逐条回答并给证据）

**A. 设计审查（必须回答）**

1. 当前版本是否真的存在统一 `action_id` 和 `rev` 门禁？它们在 **HTTP 线程**、**Unity 主线程**哪个时点生效？是否有 TOCTOU？
2. Python durable journal 目前到底放在哪个模块？是否已经满足“写入 commit 后才向游戏发动作”？重复建立账本是否会制造两份不一致状态？
3. 当前 `sleep` 的玩家 UI 入口有哪些守卫？房东、不可送离顾客、夜间外出、强制事件、动画锁分别如何验证？`outside=false` 能否阻止真实事故？
4. 当前 `end_day` 是否只调用 `CloseShutter()`？未触及 `OnClick()` 的 UI 锁、动画与分支如何对齐？
5. `confirm_trade` 目前实际调用的是 `BuyItem/SellItem`，还是 `OnOfferAcceptClick()`，或者其他适配器？交易提交、送客完成与资源变化从何确认？
6. `get_clientele`、`bargain_preview` 是否仍有副作用？`rev` 与游戏 RNG 是否存在漏记？
7. `ModHook.FireOnXxx` 实际有哪些事件？哪几个能证实**业务提交**而不只是“方法被调用”？Hook 在 Mono/Harmony 当前组合是否可靠？
8. 当前录制模式通过哪些服务端检查阻止 `act -> debug_*` / 其他特权入口？只隐藏工具是否足够？
9. 读操作可能暴露隐藏游戏信息吗？如果信息等价不可验证，是否该在 strict 模式下隐藏该字段？
10. 是否存在一个能真实触发 Unity UI 交互的测试入口？如果没有，如何建立独立 Oracle，避免把桥调用与“真实 UI”混成一个测试？
11. 如果录制存档被读回五天前，Operation 与 Memory timeline 如何隔离？本次任务是否已覆盖？
12. 0.46D 的工具真实计数、A/B/C 标签关系、43 个 B 缺口修复 10 后剩 33 的计算是否有唯一 defect_id 证据？

**B. 对本 RFC 的反驳审查（允许提出更优设计）**

13. OperationTracker 是否一定要加在 C#？能否由现有执行框架实现同样语义而无需新增复杂模块？用调用链说明。
14. 统一 `get_operation/wait_operation` 与当前 `wait_for_change/wait_for_events` 如何复用？是否会有冗余或死锁？
15. 交易领域事件最好直接读取原生回调还是加 Harmony Hook？相应的可观测覆盖、调用位置、异常与版本漂移风险是什么？
16. `strict_player` 的能力边界是否过严，以致无法完成新游戏菜单/录像？哪些特权必须通过外部管理通路而不是游戏动作通路实现？
17. 目标是“一周交租”而非覆盖全游戏。有什么更小的垂直切片仍能在安全条件下完成录像？请提出最小 allowlist。
18. 哪些指标无法在当前 Unity 游戏里可靠测得？请如实删掉或降级，不能为了交付强行发明观测能力。

**C. 产出格式**

对每条问题使用：`结论 / 证据（文件+方法+行号、IL/运行日志）/ 置信程度 / 已知反例 / 建议改动 / 测试用例 / 是否阻塞 D1/D2`。不要给纯意见而无证据的结论。评审时允许标记 `needs_instrumentation`、`needs_real_game_test`。

---

## 15. 建议下一次交付的最小实现包

不建议本轮一次性推进 D0–D6。下一轮 Agent 交付限定为：

1. **审计与决策**：核对 0.46D 基线，填充 6 个事故的当前真实证据；确定第 5 节新不变量是否接受；给出 `OperationTracker` 最小落点 ADR。
2. **危险路径关闭**：优先验证/修复 `sleep` 房东绕过与 `confirm_trade` 送客，并确认 `open_shop`/`dismiss_current_client` 无其它同型路径；修复后让实机测试变绿。
3. **单个端到端示范**：选 `confirm_trade`，实现 `prepared → running → completed/uncertain`、领域证据与完整结果审计；如果现有代码有足够基础，额外覆盖 `end_day`。
4. **可执行测试**：至少运行 `P0-TRADE-01/02/03/04`, `P0-LANDLORD-02`, `P0-TIMEOUT-01`, `P0-RECOVER-01` 的可运行部分；未跑的明示阻塞条件。
5. **边界说明**：哪些动作仍只支持旧语义/还未具有 Operation 状态机；这些工具在 strict_player 下是否应禁用。

**交付标准**：可审阅 commit/diff、真实命令及原始测试结果、被测版本、回归用例 ID、从未执行的真实危险操作清单。未经用户明确授权，不应在真人存档验证房东、`debug_*`、覆盖存档等危险行为。

---

## 16. 参考资料及版本注意事项

以下为用于支撑**通用工程建议**的公开资料，不是 PawnMind 已实现的证据：

- Unity Manual — Coroutines（协程跨帧、并非独立线程）：https://docs.unity3d.com/2022.3/Documentation/Manual/Coroutines.html
- Unity Scripting API — `MonoBehaviour.StartCoroutine`（启动返回不等于流程结束）：https://docs.unity3d.com/2023.2/Documentation/ScriptReference/MonoBehaviour.StartCoroutine.html
- Harmony — Patching / Prefix / Postfix / Finalizer（Hook 的执行语义和限制）：https://harmony.pardeike.net/v2/articles/patching.html
- Harmony 3 文档（作为版本差异参考；不能假设项目已经使用 Harmony 3）：https://harmony.pardeike.net/v3/articles/patching.html
- MCP 2026-07-28 规范发布与 Tasks 扩展：https://blog.modelcontextprotocol.io/posts/2026-07-28/

**风险警告**：对闭源 Unity Mono 游戏的反射与 Patch 有版本、内联、生命周期和插件冲突风险，任何 Hook 必须在受控环境验证，并保留禁用路径。不要将规范的未来能力（如 Tasks 扩展）直接当作当前 DSH 已支持的接口。

---

## 17. 一页版决策摘要

1. **保留原三层架构**（Unity Mod / Python MCP / DSH），不用重新造独立 Agent Loop。
2. **玩家入口优先但非充分**：合法性包括 UI 可达性、完整守卫、时序和信息边界。
3. **结果不由单次回读判定**：采用受理/提交/业务完成的分阶段证据协议。
4. **异步首选**：Bridge OperationTracker + Python 有界等待 + `get_operation/wait_operation`；Unity 主线程不阻塞。
5. **失败绝不猜测重试**：先 durable intent，未知执行结果进入核对/隔离；不承诺跨世界 exactly-once。
6. **确认关键领域事件**：以最少只读 Hook 和稳定后置条件组合验证，尤其交易、顾客、跨日与存档。
7. **只读必须真的只读**：RNG/Refresh 副作用优先改为被动快照，不能只靠缓存或 rev 掩盖。
8. **模型可执行性按需暴露**：`allowed/blocked_by/resolution`，真实守卫由 C# 强制执行。
9. **测试以真实 UI 为 Oracle**：契约测试、fake 故障测试、真实 Unity 用例、UI 与 MCP 差分的组合。
10. **按业务域修复缺口**：先关闭 P0 权限/交易死锁/重复资源，再做统一执行器和其余治理；录像前严格门禁。

> **最终目标不是“58 个写工具全部返回 ok”**，而是“在验证过的游戏状态里，AI 只能做玩家能做的事；做了以后，系统有证据说明究竟完成到哪一步；失败不会因自动重试造成第二次损害；这些结论可被回归测试重新证伪”。
