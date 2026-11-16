# PawnMind 下一阶段工程任务书（给 Coding Agent）

> **文件性质**：可直接交付的工程审阅与执行任务书（Agent Handoff / Execution Brief）  
> **版本**：2026-11-17 / Round 3 · Candidate 1  
> **项目**：*Probably Stolen* 0.46D / PawnMind / Unity Mono C# Mod + Python stdio MCP + DSH  
> **状态**：**评审建议，非已完成实现，也非已获准发布**  
> **首要目标**：实现**可验证的最小严格玩家经营闭环**，让 AI 从专用测试存档完成开店→交易→对话送客→关店→跨日，最终自主支付第一周租金；所有关键过程提供实机证据。  
> **产品定位**：可靠、可审计、可复现、可逐渐学习的自主游戏 Agent；**不是**“工具数量最多的 MCP”，也暂不争夺通用 Mod 调试接口市场。

## 0. 给 Agent 的执行契约（先读）

你是本仓库的技术审阅者与执行者。**先独立核对事实，再作设计决策，再以最小差异实现。** 本文引用的行号、版本、测试数量和竞品资料来自提交时的文件，不能代替对当前 HEAD 的检查。请严格区分：

- **F / 文件可证实**：此任务书已直接检查的上传附件或它们明确记录的值。
- **R / 仓库需验证**：文档称存在的 C# 调用链、测试通过、DLL 哈希、特定游戏行为；你必须到当前仓库复核并附路径与测试输出。
- **E / 实机需验证**：Unity 运行态、真实 UI 可达性、存档保存、反射/扫描器、DSH 真实交互；无游戏环境时标 `NOT_RUN`，**不能当 PASS**。
- **H / 外部假设**：竞品用户数、通关质量、市场窗口、安装摩擦。不要将作者自述提升为本项目事实。

**工作纪律**：
1. 不修改真实用户存档、不在无保护槽执行写入；不暴露 `debug_*`、任意反射、`act`/`press` 等非严格模式入口。
2. 所有写动作 **durable intent 先落盘，后派发**；动作开始后任何失败或超时，不能通过新 `action_id` 自动重试不可逆行为。`unknown/uncertain` 不等于 `failed`。
3. **不新建第二套 Agent 主循环**。DSH 仍为 Agent 宿主；复用 `ActionGate`、recorder、现有录制槽保护和旧测试。
4. 严格模式一律**默认拒绝**；缺少类别、证据、版本、前置条件或有开放的关键缺陷，就不能自动放行。Python MCP 和 C# 实际分发必须同策略；仅在 MCP 隐藏工具不构成授权。
5. 任何修改不能以“测试全部通过”替代真机语义验证。自动测试、实机测试和作者主张三者分别记录。
6. 当某项设计与当前代码不符时，提交 `REJECT / MODIFY / ACCEPT / NEEDS_EVIDENCE`，附精确文件位置、可复现命令和更小的替代方案；**不要为遵循本文而重写整个项目**。
7. 无法做的实机任务，提交完整验证脚本、操作者步骤和 `NOT_RUN` 记录；不要自行宣称录制就绪或发布就绪。

### 不在本轮范围

不要新增大型 Orchestrator、通用 Planner、向量数据库、复杂强化学习、多模型 Benchmark、内置 LLM UI、任意游戏反射执行、自动改钱/生成物品。不要为赶 Workshop 发布时间绕过扫描器、版本锁、用户权限或测试槽隔离。不要为了齐全而一次修复全部 114 个工具。

---

## 1. 输入资料、确认事实与主要冲突

### 1.1 必须阅读的仓库资料

| 资料 | 用途 |
|---|---|
| `ADR-PLAYER-PARITY-v2.md` | 最新 ADR：执行/交付双状态轴、严格模式、工作流、C# ModePolicy |
| `ROUND2_DISPOSITION.md` | 已审阅问题的处置与源码证据，包含尚未完成项 |
| `plans/PLAYER_PARITY_IMPLEMENTATION-v2.md` | D0、D0a、D1–D6 计划和测试 ID |
| `agent/tool_policy.py` | **当前有缺陷**的模式/纯度策略实现 |
| `audit/tools-inventory.json` | 当前 `tools/list` 清单；**不是**完整原生方法语义证据 |
| `audit/PLAYER_ENTRY_MATRIX.csv` | 守卫缺口、玩家 UI 入口、缺陷证据；需检查真实文件是否存在与最新 |
| `rfc/PawnMind_Player_Parity_RFC_v1.md` | 原始玩家语义等价 RFC |
| `reviews/PLAYER_PARITY_RFC_REVIEW.md` | Agent 的第一次技术反审 |
| `reviews/PLAYER_PARITY_ROUND2_EXTERNAL_REVIEW.md` | 第二轮独立审阅 |
| `pawnmind-competitive-landscape.zh-CN.md` | 竞品采样报告，作为研究线索而非已验证市场事实 |
| `PawnMind_Competitive_Strategy_Architecture_Review_2026-11-17.md` | 第三轮联合审查；**本任务书的直接背景** |

路径以实际仓库为准；找不到的文件要指出，并从附件/历史查证，不可凭空补造。

### 1.2 已独立执行的附件检查（F）

基于本次附件，用 Python 导入执行上传的 `agent/tool_policy.py` 并分析 `tools-inventory.json`，结果为：

```text
full = 114
recording = 104
strict_allowlist(full) = 100
strict - recording = []
recording - strict = [bargain_preview, barter_status, get_clientele, get_state]
full 中 readOnlyHint == null = 114
strict 含 bargain / barter_accept / buy_wholesale / commissary_buy / move_item
strict 不含 get_state
```

**严重缺陷 P0-ALLOW**：`purity(name, readOnlyHint)` 对缺省/`null` 提示默认为 `write`，导致大量读操作被误分为已允许写操作；同时 `strict_allowlist()` **没有**读取 `evidence_id`、测试结果、游戏版本、开放缺陷、玩家可达条件。ADR-PP-10 的“按证据生成”**尚未实现**。如果直接启用，严格模式失去核心 `get_state`，却仍允许未验收的交易动作。

**注意两个时间阶段**：在修复 D0a 前排除 `get_state` 是合理保守策略；错误不是“先禁用”，而是部署策略时未安排何时、凭何证据重新准入。不可通过把 `get_state` 强行标 `pure` 解决。

### 1.3 来自仓库审阅报告的待复核判断（R/E）

- `TryRefreshRevision → StateSnapshot.BuildJson → GetBargainSnapshot/CanOffer*、CanReport → Refresh()`；后者在 `trackedClient != client` 时可能清谈判状态位。**有条件副作用**，不是“每次读取必然改变状态”。
- `rev` 是序列化快照指纹变化计数，不包含全部 UI、RNG、隐藏游戏状态；`stale_rev` 拒绝也未必意味着零副作用。
- 当前 `ActionGate` 进程内有 `action_id` 缓存和未送达处理，但 `MarkResponseUndelivered` 可能把已完成业务覆盖为 `outcome_unknown`。
- `confirm_trade` 已改走原生 `OnOfferAcceptClick` 路径，仍有 B10–B16 等静默返回条件需要完整覆盖；对话结束和送客需模型执行后续操作。
- `sleep` 在当前源码中据称已经通过 `DayEndPolicy` 复用了 `CanEndDay()`，这是对早期事故的修复；真机负向仍需 PASS 才能作为正式录制证据。
- 当前 `ModePolicy` 只实现部分高风险/录制入口守卫；普通 op 直连 HTTP 及泛用 `act` 重派发路径需要最终权限检查。
- 历史文档记录构建 DLL 与已安装 DLL 哈希不一致；下一次真机验证之前必须重建、安装并核对 `/health`。
- 旧计划 D5 全量 UI 参考路径可后置，但已知 P0 操作必须保留针对性真实交互验证。

---

## 2. 本轮核心决策：两个产品轨道，只实施一个先手

### 主轨 R：研究型、可验证自主游戏 Agent（**现在执行**）

质量主张：

> “在特定游戏版本和声明的工具集下，AI 以正常玩家可触发的行动完成首周经营目标，且完整记录关键业务事件与故障恢复；下一阶段证明 Skill 和 Memory 带来的边际收益。”

首个可验收成果：**一局完整实机首周付租**，包括视频、MCP 日志、原生状态或事件证据、零人工介入声明（若确有人工介入必须如实标明）、模型配置与费用统计。

### 次轨 P：普通玩家可用的 Workshop Beta（**通过 R 的安全门槛后才启动**）

核心目标是安装/首次连接简单、权限说明清楚、不会破坏玩家真实存档。独立评估接入方式（stdio + 自动安装、游戏内 HTTP MCP、现有 Python桥），以当前官方 Mod 扫描规则和 Windows 用户实测决定，不因竞品“零依赖”宣传而立即重构。

**战略排除**：不同时争夺泛用 Mod 开发者工具、自动游戏商店经营、零依赖、长期记忆、全覆盖 114 工具五个方向。先证明一个真实价值点。

---

## 3. 实施阶段与依赖关系

```text
S0 / 当前基线审计 + 失败安全准入原型
      ├─ S1 / D0a 纯快照 & 读端点完整审查
      ├─ S2 / C# ModePolicy + MCP 同步白名单 + 构建/模组扫描
      └──────┬──────────────────┘
             ↓
S3 / D1 成交语义 + ActionGate 双轴 + DSH pending E2E
             ↓
S4 / D2 开关店/跨日 + 证据/故障恢复
             ↓
S5 / 最小 Skill + 一周独立实机录像
             ↓
S6 / 复现与分发体验 + 比较实验
             ↓
S7 / 记忆增益验证（先评测再增加复杂度）
```

**施工协调**：S1 和 S2 的静态/单元测试可以并行，但**不得在 `get_state` 尚不纯时，把完整 strict 工具集连接给真实 Agent 开局**。S2 的政策代码可先落地、输出有限的 `not_ready` 集合；要使用 `get_state` 进入实机试玩，必须先拿到 S1 的真实准入证据。

### S0：基线冻结 + 差异清单（先产物，后改代码）

- 记录 HEAD、worktree、游戏版本、Mod manifest、编译产物与安装版 SHA256、`/health` MVID、真实 `tools/list`。
- 记录 `agent/tool_policy.py` 当前全量分类、strict 集合、缺失元数据和开放缺陷；出一份 diff，不以旧报告的 114/104 作为当前事实。
- 读 `ActionRouter`、`ActionGate`、`StateSnapshot`、`GameActions.ConfirmTrade`、`RecordingSaveService.Sleep`、`RecordingDispatchGuard`、MCP server；验证本文 §1.3 的源码断言。
- 制作一份**一周主路径最小工具集草案**：按场景需求而不是“核心 40 个”拍脑袋确定。仅为候选，暂不一键启用。

**退出门槛**：`BASELINE.md` 有全部路径、哈希、命令、真实输出；缺失环境或未验证项清楚标为 `NOT_RUN/UNKNOWN`。

### S1：D0a / 快照与热读纯度（严格模式的关键前置）

1. 对 `/state`、`TryRefreshRevision`、`get_legal_actions`、`get_run_summary` 及主路径其它热读，逐个列出原生 getter、可能触发 `Refresh()`/UI/RNG/惰性生成的调用链。
2. 禁止仅为了构造状态快照而调用会重置谈判、推进 RNG 或改变游戏状态的方法。优先被动读取已有字段或经证实无副作用的缓存；暂不引入重复游戏规则的独立计算器。
3. 对 `get_state` 构造 fake 中的调用计数测试：快照构建应触发 **0 次**可变更业务状态的 `Refresh()`；不能仅检查 `rev` 不变。
4. 真机进行连续 10 次读取、首次进入顾客后读取、顾客切换时首次读取、动画中读取、旧 `rev` 被拒绝后的读取；对比 UI、谈判隐藏状态和 RNG（读取 RNG 的探针自身不能推进随机序列）。
5. 对 `get_clientele` 和 `bargain_preview` 继续 strict 禁用；如果未来得到真正纯且玩家可见的替代路径，再按证据准入。
6. 字段级信息可见性：玩家尚不可知的隐藏身份、未来事件或未解锁信息不可因 MCP 查询被暴露。

**退出门槛**：`PP-READ-01/02` 自动+真机 PASS；主路径只读函数的纯度/可见性来源可查；准入 `get_state` 之前须提供证据，不允许直接改默认值。

### S2：D0 / 真正的证据准入与跨入口 ModePolicy

**统一策略注册表**（推荐声明性 JSON/YAML + 生成器；也可保留纯 Python 作为源，但必须可序列化成同一确定性工件供 C# 消费）：

| 字段 | 意义 | 约束 |
|---|---|---|
| `name` | 规范化的真实 op 名称 | 与全部 `tools/list` 完全覆盖 |
| `kind` | `read` / `write` / `management` | 必填，不能由 `readOnlyHint=null` 推出 `write` |
| `purity` | `pure` / `cached_impure` / `mutating` / `n/a` | 读工具必填，未知即拒绝 strict |
| `visibility` | `player_visible` / `privileged` / `unknown` | strict 只能 player_visible |
| `risk` | `read`/`ui`/`low`/`medium`/`high` | 与“已验证程度”分开 |
| `player_reachable` | 原生玩家入口与守卫覆盖 | 写动作必填；不确定则拒绝 |
| `game_version` | 兼容版本 / MVID 约束 | strict 必须匹配当前运行版 |
| `open_defects` | 尚未关闭的阻塞问题 | 不许包含主路径未关闭 P0/P1 |
| `required_tests` | 具名自动/实机用例 | 与证据存储关联 |
| `evidence_status` | `pass`/`fail`/`not_run`/`stale` | 仅 `pass` 准入 |
| `lifecycle_phase` | `bootstrap` / `play` / `test` | 解决新游戏/读取专用记录槽的合法性 |
| `policy_revision` | 策略版本和 hash | C# 与 MCP 同源对照 |

**示意配置，`not_ready` 不等于已授权**：

```yaml
get_state:
  kind: read
  purity: unknown
  visibility: player_visible
  lifecycle_phase: play
  evidence_status: not_run
  required_tests: [PP-READ-01, PP-READ-02]
  gate: not_ready
confirm_trade:
  kind: write
  purity: n/a
  visibility: player_visible
  player_reachable: true  # 需现版源码复核
  lifecycle_phase: play
  open_defects: [B10, B11, B12, B13, B14, B15, B16]
  evidence_status: not_run
  required_tests: [PP-TRADE-01, PP-TRADE-02, PP-TRADE-03]
  gate: not_ready
```

**生成条件**（示意伪代码）：

```python
for op in actual_full_tools:
    metadata = registry.get(op.name)
    if not metadata or not metadata.complete: DENY(op, "unknown_tool_or_metadata")
    elif metadata.version != current_game_version: DENY(op, "version_mismatch")
    elif metadata.open_blocking_defects: DENY(op, "open_defect")
    elif metadata.evidence_status != "pass": DENY(op, "evidence_missing")
    elif metadata.kind == "read" and (metadata.purity != "pure" or metadata.visibility != "player_visible"):
        DENY(op, "not_pure_or_not_visible")
    elif metadata.kind == "write" and not metadata.player_reachable: DENY(op, "not_player_reachable")
    elif not lifecycle_phase_allows(metadata): DENY(op, "wrong_phase")
    else: ALLOW(op)
```

**C# 最终权限控制**：
- 最终分发入口必须检查模式与规范化后 op；若存在 `act` 包装或内部重派发，在**展开后**再执行同一策略；不允许 `debug_*` 进入 strict。
- `/action` 之外的 `/state`、`/events`、其它诊断/图片/管理入口也纳入逐端点可见性、纯度、阶段判定。观察工具同样有权限问题。
- MCP `tools/list` 与 C# 策略均从同一 policy artifact + hash 获取，策略 hash 不一致时拒绝进入 strict，而非偷偷采用较宽松集合。
- Bootstrapping 新局与加载专用录制槽的特殊权限必须是**有条件、有限次数、绑定实际游戏状态的例外**，不能让模型拿到 `save_game` 或任意 load/overwrite 权限。
- 保留现有 token、loopback、MVID 与 `action_id` 门禁，拒绝操作在快照刷新和业务副作用之前完成。

**必须跑的测试**：
- `unknown_tool`/空 metadata、`readOnlyHint=null`、失效证据、旧版本证据、开放缺陷、泛用 `act` 以及 HTTP 直连绕过全部拒绝。
- 通过验证的主路径动作可放行；不能只测“坏工具被剔除”。
- `MCP recording set == C# strict policy set == generated manifest set`；启动期加入 hash mismatch 负向测试。
- 对 `bootstrap/play` 的生命周期切换做拒绝与允许测试。

**退出门槛**：没有未知默认放行；Python/C# 全入口一致；主路径必要的只读接口在 S1 真机通过后正确准入。

### S3：D1 / `confirm_trade` 语义 + Operation 恢复

**必须保留的设计**：`ActionGate` 为 C# 当前执行权威，Python recorder 为 durable intent 权威。不承诺跨进程 exactly-once。

**修正状态结构**：

```text
execution_phase: prepared → dispatched → running → settled
outcome: unknown → succeeded | rejected | failed
transport_delivery: not_sent | response_sent | write_failed
```

`uncertain` 是“当前无法确认结果”的**认识状态**，可在核对后解析为 succeeded/failed；不能把已被原生证据证实的完成事实，因 HTTP 响应未送达就覆盖成未知。`response_sent` 也不等于模型已收到或正确理解。

**交易执行**：
1. 在真正派发前使用同一个主线程同步步骤检查 `rev`、ModePolicy、当前交易 UI、顾客身份、物品 ID/所有权、`IsTradeLocked`、`clientIntent`、`isBusinessComplete`，及 BARTER/INFO/GUNSMITH/BUY/SELL 对应原生判定。
2. 只调用一次原生 `OnOfferAcceptClick()`，不得用 `BuyItem/SellItem` 手拼后置效果。
3. 成交证据采用物品唯一 ID 所有权差异 + `isBusinessComplete` / 明确原生事件（若存在）；现金变化仅辅助。没有证据不可宣称成功，状态差分也不能单独证明归因。
4. **操作成功 != 顾客工作流结束**：`trade_committed` 证明充分后 `confirm_trade` 可以 `completed`，返回 `workflow.phase=post_trade_dialogue` 与允许继续的 `advance_dialogue`；不得以 pending 全局锁阻止送客。若原生成交仍在运行，按资源冲突域只禁止再次交易，不阻止必要 continuation。
5. `get_operation` / `wait_operation` 在 Python 侧可查询/等待有限时长；**禁止**阻塞 Unity 主线程；测试 DSH 对 `ok:false,status:pending` 的行为。不要把 pending 误当工具系统错误，也不应反过来把 pending 伪装为 ok。
6. `MarkResponseUndelivered` 只更新传输证据，不覆盖已成功的 `execution/outcome`；断连后根据已有 `action_id` 读取/核对，不能用新 ID 再执行不可逆动作。

**退出门槛**：B10–B16 的每个拒绝分支有独立测试；真实成交→模型对话→送客→再接客 PASS；重复提交无重复所有权/资金转移；DSH pending E2E PASS；响应丢失不抹除已确认业务事实。

### S4：D2 / 关店、睡觉与保存完成证据

- `end_day` 必须保留原生卷帘门/顾客收尾序列，`open_shop` 不可跳过动画锁重刷资源。
- `sleep` 的房东限制以当前玩家合法路径为准，复核源码 `DayEndPolicy` 的修复并做真机负向；不能从“旧单测 PASS”直接推断最新游戏版本正确。
- 在现有游戏 `ModHook` 中使用只读 hook 辅助阶段观察；Hook 触发证明某步骤发生，**不是**无条件证明业务终态或保存落盘。
- 显式区分 `day_changed`、`save_requested`、`save_observed`、`save_durable_verified`（仅真实复启验证时可以声明最强证据）。`last_save_time` 前进不自动等同持久落盘。
- 统一 `running/settled/uncertain` 的等待语义；超时给出操作 ID 和可继续核对的条件。

**退出门槛**：`P0-LANDLORD-02`、`P0-OPEN-01`、`PP-ASYNC-01/02` 实机 PASS，未运行不算通过。对尚未证实保存的动作不返回 `save_completed:true`。

### S5：最小 Skill、真实一周录像与失败恢复

**不要照抄竞品 Skill**，自行建立 PawnMind 的可验证操作教程；Skill 只引用已通过 strict 准入的工具，不得依赖被禁用的 `bargain_preview`、`get_clientele`、`act`、`press`。

建议目录：

```text
play/skills/pawnmind-core/SKILL.md
play/skills/pawnmind-core/references/opening-week.md
play/skills/pawnmind-core/references/customer-trade.md
play/skills/pawnmind-core/references/dialogue-and-prompt.md
play/skills/pawnmind-core/references/day-end-and-rent.md
play/skills/pawnmind-core/references/errors-and-recovery.md
```

Skill 必须有：可用工具发现、每类操作的最小合法前置、`rev` 冲突后读取新状态再判断、`pending` 等待和核对、交易成功后的对话 continuation、房东无法送走的处理、弹窗读取、绝不自动对 `uncertain` 重发、没有可靠证据时停止；**经营策略和工具事实分开**，引用本版本经过验证的来源。

**真实录像标准**：
- 游戏版本、MVID、构建/安装 SHA、strict policy hash、DSH/模型/参数、初始记录槽条件、时间戳可追踪。
- 完整主流程，从新局/合法 bootstrap 到一周租金支付。评测任务由**实际租金状态和原生事件**证明，不以模型文字宣称完成。
- 提供操作/观察日志、关键状态快照、视频文件索引、每次错误及恢复、人工介入明细、token/成本（若提供方可得）。
- 对 `memory_*`：可以仅作读取/审计，不允许将 `bridge_failure`、`uncertain` 或已知不合法操作提炼成已验证游戏机制。
- 如发生一次人工协助，标为 `supervised`；没有完整成功记录，就标实验未通过，不做“已全自动通关”的公开宣称。

**退出门槛**：在受保护槽真实游戏中完成至少 1 次完整首周付租且关键测试全绿；证据可以让第三方从日志查到成功和任何异常。

### S6：简化安装与可复现实验（达到 S5 后）

1. 先测用户最短连接流程，再决定是否引入官方 SDK 的游戏内 HTTP MCP。**禁止**在 Mod 文件夹内随意放 `.exe`/`.bat`/`.ps1` 或未过官方 scanner 的运行时。
2. 做 Windows 干净环境连接验证：安装、MCP 配置、token 轮换、游戏重启、DSH/其它客户端连接、首次合法 `get_state`、失败诊断。
3. 导出 `capabilities` 类工具/文档（只汇报本版本严格允许的能力及拒绝原因），而不是把 114 个工具同时塞给玩家模型。
4. README 显著展示能力边界、未验证功能、存档保护与安全模式；公开代码/安装哈希与实际测试日志。
5. 对照实验先在 PawnMind 内部进行：Skill on/off，Memory on/off，最小集 vs Full（Full 仅用于受控测试）；固定/匹配起点、模型、提示词预算、游戏版本和记录条件。

**退出门槛**：首次连接路径有真实实测数据，安全门禁持续通过；任何 beta 明确写“实验性/已知限制”，不依赖未经验证的市场宣传。

### S7：长期记忆效果验证（独立小项目，推迟重型研发）

- 对录像和事件作来源分级：`game_observation` / `bridge_failure` / `unverified_outcome` / `confirmed_mechanic`。游戏机制的记忆晋升必须有可靠的、适用版本和时间线的证据。
- 建立 `memory-on` vs `memory-off` 基线，比较首周租金完成率、净资产、非法调用次数、重复失败数、工具调用数、模型成本。一次成功录像**只能证明可行，不能证明记忆有收益**。
- 如果数据没有稳定显示增益，不做向量库、复杂 Planner 或 RL 扩张。

---

## 4. 必须建立的可执行回归矩阵

> 每条记录必须附 `test_id / type(fake|contract|live|DSH) / game_version / commit / expected / observed / evidence_ref / verdict`。`NOT_RUN` 与 `UNKNOWN` **不是** `PASS`。

| ID | 级别 | 场景 | 验收 |
|---|---|---|---|
| `PP-TOOLS-01` | CI | full/recording/strict/C# 清单比较 | 真实版本下集合与 policy hash 一致 |
| `PP-TOOLS-02` | CI | `readOnlyHint=null`、未知工具、新增工具 | 默认为禁止，新增工具未登记使 CI 失败 |
| `PP-TOOLS-03` | CI | P0/P1 open 的写工具及 expired 证据 | `buy_wholesale` 等不得提前 strict 放行 |
| `PP-MODE-01` | fake/HTTP | `act` 重派发、debug、直连普通 op、读端点 | 策略外操作全部拒绝、零副作用 |
| `PP-MODE-02` | fake | bootstrap/play/test 生命周期切换 | 只在合法菜单/专用槽条件下放开必要动作 |
| `PP-READ-01` | fake/live | 连读十次状态 + 顾客切换首读 | UI/谈判/RNG 无非玩家副作用 |
| `PP-READ-02` | fake/live | 旧 rev 被拒 | 拒绝前后游戏业务状态完全不变 |
| `PP-READ-03` | audit/live | 主路径 getter 逐成员审查 | 纯度与玩家可见性证据齐全 |
| `PP-TRADE-01` | fake/live | 成交→对话→送客→再次接客 | 交易/工作流边界明确，模型不死锁 |
| `PP-TRADE-02` | fake/live | 同一个 action_id 重试 / 不同 ID 重复成交 | 无重复交易、状态可核对 |
| `PP-TRADE-03` | fake/live | B10–B16 的 7 个静默分支 | 逐条正确拒绝，零假成功 |
| `PP-TRADE-04` | DSH E2E | `ok:false,status:pending` | 模型能够等待/查证，不把它当终态失败或成功 |
| `PP-ASYNC-01` | fake/live | end_day pending 到完成 | 不阻塞主线程、终态证据准确 |
| `PP-ASYNC-02` | fake | completed 后响应未送达 | outcome 仍 succeeded，delivery 单独变化 |
| `P0-LANDLORD-02` | live | 房东尚未合法处理时 end_day/sleep | 两入口均拒绝，无绕过 |
| `P0-OPEN-01` | live | 开门动画期重复开店 | 不重复生成晨间奖励/顾客 |
| `PP-SAVE-01` | live | 跨日、重启、存档可读性 | 区分时间戳与可恢复存档证据 |
| `PP-RECORD-01` | build/live | DLL SHA/version/MVID/录制槽 | 一致且不会覆盖真实用户存档 |
| `PP-MODSCAN-01` | build | 官方 Mod Scanner | 禁用/拒绝项 0 或被明确标为发布阻塞 |
| `PP-PLAY-01` | DSH/live | 完整首周租金 | 真实游戏状态证实已付，零未披露人工介入 |

应把旧事故场景纳入固定测试：房东绕过、成交卡顾客、重复开门、非法送客、静默假成功、伪只读。所有 P0 门禁的 live 证据必须可查；无法跑只能禁用受影响工具，不能豁免。

---

## 5. 发布兼容性（竞争背景下仍是安全门槛）

**现有资料中引用的官方 Mod 指南**计划在 **2026-12-10** 起收紧 `reflection-external`、`dynamic-code`、`native`、`threads` 能力。这个日期是文档中的计划，不是本任务书独立实测的未来部署结果；开始动工时应重新读取最新 `APIChanges.md` 和 `ModdingGuide.md`。参考：

- https://github.com/dragonpan2/probably-stolen-modding/blob/main/APIChanges.md
- https://github.com/dragonpan2/probably-stolen-modding/blob/main/ModdingGuide.md

需要提交**当前产物**的 `mod-scan` 完整输出，检查 `System.Threading.*`、`Task.Run`、Timer、动态类型反射和外部程序集访问；以官方规则判断是否需改用 `ModReflect`、`ModHelper.RunInBackground/Delay/RunOnMainThread` 等；不要仅靠静态 grep 宣称兼容。若扫描有弃用/拒绝项，记录具体文件/IL 证据、替代方案和是否阻塞发布。外部 Python/脚本应与 Workshop Mod 文件夹分离，除非最新官方规则已明确允许相应内容。

**不要仅因为竞争对手能订阅即用，就把未经 Scanner 验证的原生客户端包强行塞入游戏 Mod 目录。**

---

## 6. 竞品观察对产品路线的具体要求

本节是**方向参考**，不要求你去修改或直接复制第三方项目代码。

| 参考项目/类型 | 可学的产品特征 | 本项目可检验任务 | 本轮不应下的结论 |
|---|---|---|---|
| Jerod / Skill | 分主题的经营知识、错误恢复指引 | 用最小 PawnMind Skill 做同模型开局对照 | 作者声称“6 小时通关”不是独立复现 |
| mcxufly / LIVE 验证 | 实机成功与脚本测试分开记录 | 每个被放行操作关联 live 测试与版本哈希 | 不能以工具数量推断胜负 |
| 星际掌柜 / 低摩擦接入 | 能力发现、HTTP 接入、权限说明 | 记录第一次成功合法操作的步骤与失败率 | 不能仅以“无需 Python”推断用户已零配置 |
| detyse / 安装流程 | 自动依赖安装、连接引导 | 在干净 Windows 环境比较安装流程 | 不假设每个客户端都受支持 |
| DsW-4 / 开发桥 | 开发调试效率与热加载 | 吸收调试日志文化，保留 strict 目标 | 与正常玩家 AI 不完全同赛道 |
| 小肥鱼 / 可见的 AI | 可视反馈、视频故事 | 录像叠加实际状态与决策解释（后续） | 不为了展示而增加作弊/绕过入口 |

**不要采信的数字推论**：15 个相关项目 ≠ 15 个直接竞争者；工坊订阅合计不是独立用户数；订阅数/销量不是有效渗透率；“2–4 周黄金窗口”“Python 劝退 90% 玩家”“领先 3–9 倍”等均不应写入对外结论，除非有独立测量。

**优先产品指标**：首周目标完成率、关键事故回归通过率、非法副作用数、错误及人工接管次数、首次连接成功率与所需步骤、工具调用数和 token/成本、可复现证据完整度。长期再测记忆 A/B 效益；不要用工具总数作为成功指标。

---

## 7. 给 Agent 的分批交付包与停止点

### 第一批：仅审阅与 S0/S2 静态原型（**默认第一轮执行范围**）

1. `reviews/ROUND3_DISPOSITION.md`：逐项 `ACCEPT/MODIFY/REJECT/NEEDS_EVIDENCE`，给出源码位置、测试命令和风险。
2. `audit/BASELINE_CURRENT.md`：HEAD、工作树、DLL SHA、MVID、tools/list、Mod 扫描状态（不能实跑标 NOT_RUN）。
3. 修正后的 `agent/tool_policy.py` **或**新的声明式策略源及生成器：未知默认拒绝，证据驱动，明确 bootstrap/play/test；严禁误分类读工具。
4. 新/扩展 `agent/tests/test_tool_policy.py`：正向、负向、版本失效、缺失证据、陌生工具、跨入口集合一致性。
5. `audit/STRICT_ALLOWLIST_DECISIONS.md`：列出每个候选工具的 `allow/deny/needs_evidence` 和直接原因；**不许仅给数量**。
6. 若当前环境无法修改 C#，给出 `ModePolicy` 最小接口/具体插入点/测试脚本；能改也先保持最小独立提交。
7. `reports/ROUND3_REPORT.md`：真实执行命令、测试结果、未解决问题、下一阶段唯一推荐起点。

**硬停止点**：若策略来源不一致、`get_state` 未通过纯度测试、运行版本无法验证，不能宣称 strict 可用。可以提交静态代码和回归测试，但不要启用真人录制或发布。

### 第二批：S1 → S4（在第一批验收后，经既定计划继续）

- D0a 读路径纯度；D1 交易和 Operation；D2 跨日；每项独立 commit + 具名用例 + 操作前后证据。
- 实机环境缺失即 `NOT_RUN`；准备脚本和复核步骤，不要触碰非测试存档。
- 如果发现改造超出原先最小范围，在报告中提供“保守禁用受影响工具”的退化方案，禁止扩大权限以绕过问题。

### 第三批：S5 → S7（有实机闭环再执行）

- Skill + 录像证据包；公开 Beta 的接入质量；记忆增益对照实验。
- 不经用户明确要求，不自动发布到 Steam、GitHub 或覆盖现有外部连接配置。

### 最终报告模板（每项必须可核查）

```text
Task ID / Decision / Status
HEAD before / HEAD after / worktree clean?
Modified files and diff summary
Source code evidence: file:line and observed call chain
Tests: exact commands + PASS/FAIL/NOT_RUN + raw excerpts
Game evidence: game version, MVID, DLL SHA, save slot, video/log references
Mode/security effect: before vs after tool sets and C# allow policy hash
Remaining blockers + mitigation / next stage prerequisites
```

---

## 8. 一段可直接启动下一轮 Agent 的指令

> 请把本文件作为本轮唯一执行任务书，先阅读 `ADR-PLAYER-PARITY-v2.md`、`ROUND2_DISPOSITION.md`、`PLAYER_PARITY_IMPLEMENTATION-v2.md`、`agent/tool_policy.py`、`audit/tools-inventory.json`、第三轮联合审查和竞品采样报告，并与**当前 HEAD** 核对，不要假设历史审查仍然准确。第一轮**只执行 §7 第一批**：基线审计、逐条反审、修正 fail-closed 的证据准入策略及 CI、输出 C# ModePolicy 接入方案。当前上传附件中的 `strict_allowlist()` 会排除 `get_state` 却放行未验收交易工具，这必须先解决；不能靠放宽 `STRICT_DENY` 或 `readOnlyHint` 默认判断绕过。请给我 `ROUND3_DISPOSITION.md`、`BASELINE_CURRENT.md`、`STRICT_ALLOWLIST_DECISIONS.md`、`ROUND3_REPORT.md`，报告精确命令和真实测试结果。D0a、交易和跨日实现及任何真实游戏写操作必须以明确通过的安全/环境前置为条件。不要重写整套 MCP、增加不必要新组件、开始 Steam 上架或把竞品宣传当独立证据。

---

## 9. 引用与证据等级说明

**直接依据的上传材料**：
- `PawnMind_Competitive_Strategy_Architecture_Review_2026-11-17.md`：第三轮架构+竞品联合审查与实际 `tool_policy` 交叉校验。
- `ADR-PLAYER-PARITY-v2.md`：两轴、ModePolicy、Strict、operation/workflow 语义。
- `ROUND2_DISPOSITION.md`：调用链核查、`get_state` 条件性副作用、已完成/待完成与测试状态。
- `PLAYER_PARITY_IMPLEMENTATION-v2.md`：D0/D0a/D1–D6 的硬门槛。
- `tool_policy.py`、`tools-inventory.json`：本地实际执行与数据核对。
- `pawnmind-competitive-landscape.zh-CN.md`：相关项目索引与发布/产品推断，**其订阅/销量/领先叙事不直接作为事实结论**。

**外部链接**（作为后续复核入口，不代表本文当前已独立验收）：
- 官方 Mod 文档：<https://github.com/dragonpan2/probably-stolen-modding>
- Jerod：<https://github.com/cloud-1104/probably-stolen-player>
- mcxufly：<https://github.com/mcxufly/ProbablyStolenMcp>
- 星际掌柜：<https://github.com/Arom-corgi/probably-stolen-mcp>
- detyse：<https://github.com/detyse/probably-stolen-mcp>

**最终提醒**：让 Agent 用证据挑战这份计划的每一项假设。**验证游戏能正常玩，优先于宣传系统很先进；验证它记忆有收益，优先于展示记忆很多。**
