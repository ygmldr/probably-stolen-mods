# RFC-002《玩家语义等价》审阅报告

> 审阅对象：`../rfc/PawnMind_Player_Parity_RFC_v1.md`（v1.0，2026-11-17，外部评审稿）
> 审阅基线：commit `d0440ab`，工作区干净；游戏 *Probably Stolen* 0.46D
> 审阅立场：**默认不采信**。本文每条结论都给了当前源码/测试/命令作为证据，未取证的写 `UNKNOWN` 或 `not_run`。
> 结论：**修改后接受**。方向正确，但有 3 条需要改写、1 条在当前实现下不可满足、2 条成本明显高于收益（详见 §7）。

---

## 1. 基线（实测，不是转述）

### 1.1 版本与构建

| 项 | 值 | 来源 |
|---|---|---|
| 游戏版本（录制门禁期望） | `VERSION 100-Rev13` | `ModData/pawnmind/bridge.conf` |
| 游戏程序集 MVID（门禁期望） | `911508a6-e0eb-4cc0-91e8-e14b16ea28d7` | 同上 |
| **当前运行中的实际 MVID** | **`not_run`**（游戏未启动，`/health` 不可达） | 无 |
| 已安装 Mod DLL | `e9e1bd07…`，295,424 B，2026-11-16 23:32 | 安装目录 |
| `dist/` 中的 DLL | `0a991b99…`，296,448 B，2026-11-17 00:12 | `PawnMind/dist/` |
| Mod manifest 版本 | `0.2.1`（与 git tag v1.0.1 不一致，历史遗留） | `manifest.xml` |

> **基线风险**：`dist/` 比**已安装**的 DLL 新 1,024 字节。也就是说"我们测的"和"游戏里跑的"不是同一个二进制。
> 在任何"真机验证"之前必须先 `./build.sh` + 重装 + 重启游戏，否则会把旧二进制的问题算到新代码上。**这是 D0 的阻塞项**。

### 1.2 测试基线（commit `d0440ab`，实跑）

| 层 | 命令 | 结果 |
|---|---|---|
| C# 纯逻辑 + 脚本门禁 | `cd PawnMind && ./test.sh` | `passed=651 failed=0`；`contract gate tests: passed=53`；`adapter contract OK` |
| 适配器契约 | 同上 | `contract checks: 490`（只断言成员存在，**不断言语义**） |
| Python | `.venv/bin/python -B -m unittest discover -s agent/tests` | `Ran 234 tests … OK` |
| 真机 UI 差分 | — | **`not_run`**（无 UI 驱动能力，见 §6-Q6） |

### 1.3 工具面（真实 `tools/list`，非文档抄写）

| 模式 | 工具数 | 快照 |
|---|---|---|
| `--tools recording` | **104** | `../audit/mcp-tools-recording.json` |
| `--tools full` | **114** | `../audit/mcp-tools-full.json` |

与 RFC §0.2 引用的"100 / 94"不同：这是加入了 4 个只读记忆工具后的现况。**RFC 的旧计数不能再用**。

---

## 2. 六类事故：当前真实状态

RFC §2.1 的事故矩阵基于我给它的简报（`FACT-USER`）。以下是逐条复核结果。

| 事故 | 现状 | 证据 | 结论 |
|---|---|---|---|
| **INC-01 `sleep` 跳房东** | **已修** | `RecordingSaveService.Sleep` → `DayEndPolicy.EvaluateSleep` → `EvaluateEndDay` 里 `if (!canEndDay) return CannotEndDay`（`src/Core/DayEndPolicy.cs:31-41`、`src/Bridge/RecordingSaveService.cs:254-260`）；测试 `RecordingSaveServiceTests.cs:387` 断言 `AssertDenied(h.Service.Sleep(), "cannot_end_day")` | **RFC §2.2 的"高危复核"不成立**：`sleep` 现在与 `end_day` 共用同一个 `CanEndDay()` 判定，房东在队列时两者都拒。`outside` 只是**追加**的第五条限制，不是替代。 |
| **INC-02 `confirm_trade` 丢收尾** | **部分已修** | `GameActions.ConfirmTrade` 校验 `IsNegotiationOpen()` + `GetNegotiatedItem().GetUniqueID()==itemId` 后调 `GameState.AcceptNegotiationOffer()`（原生 `OnOfferAcceptClick`）。收尾对话由模型 `advance_dialogue` 推进。 | 主路径已走原生按钮；但**原生 7 个静默 return 分支仍未前置检查**（B10–B16），失败仍可能报 `ok`。见 §4。 |
| **INC-03 `open_shop` 双开刷资源** | **已修** | `GameActions.OpenShop` 改调 `StoreShutterButton.OnClick()`，前置 `lockShutterButtonFlag>0`/arrest/shootout/`storeState==MORNING`/`CanStartDay()`；手拼 fallback 已删（`src/Bridge/GameActions.cs` OpenShop）。契约：`AdapterTests/Program.cs:324-327`。 | 方向正确；**但"UI 是否真的可点"未验证**（按钮 `interactable` 与 Canvas 遮挡层没查）。 |
| **INC-04 `dismiss` 送走房东** | **已修** | 前置 `store.IsClientDimissable()` → `client_not_dismissable`；改调 `TryPlayerDismissCurrentClient(false)` 以保留 `tryInterceptDismiss`。 | 与 INC-01 同型入口已封。 |
| **INC-05 静默假成功** | **未修完** | 只修了 `advance_dialogue`（`no_dialogue`）。`confirm_trade` 的 7 个分支、`bargain`、`buy_wholesale`、`end_call` 仍可能"原生 return、桥报 ok"。 | 这是**当前最大的行为性风险**，比 RFC 估计的更集中。 |
| **INC-06 伪只读** | **未修** | `BargainService.PreviewJson` 调 `ui.GetBargainSuccessChance()`（内部 `Refresh()`）；`ClienteleService` 首次读取会 `RNG.Roll()`（按天缓存）。 | `strict_player` 下这两者**当前不合格**。 |

### 2.1 补充发现（RFC 未提）

`rev` 不是游戏计数器，而是**序列化快照的指纹变化计数**（`src/Bridge/StateSnapshot.cs:11-25`）。
推论：任何**不在我们序列化范围内**的状态变化（面板开合、RNG 推进、UI 动画阶段、未导出字段）都**不会**让 `rev` 变化。
因此"`rev` 未变"不能证明"世界没变"，`stale_rev` 的覆盖范围比直觉小。RFC §7 讨论读纯度时没有考虑这一点。

更严重的是：**`/state` 自己就不是纯读**。`StateSnapshot.cs:88` 与 `:181-188` 各 4 次调用 `CanOffer*`，
每次都会走到 `BargainUIManager.Refresh()`（审计 S1/S2），而 `client.can_report` 还会再多一次（S4）。
也就是说**每一个动作后的回读、每一次 `get_state`，都在重置谈价面板的状态位**（约 15 个，含
`discountOffered`/`markupOffered`…）以及 `ClearTradeRepMultiplierOverrides()`。
这比 `bargain_preview` 的影响面大得多，且直接命中"回读"这个我们最依赖的动作。

---

## 3. 五条不变量（逐条裁决）

| ID | 裁决 | 理由与修正 |
|---|---|---|
| **INV-01 LEGALITY**<br>不越权 | **修改后接受** | 方向对，但"玩家有合法路径可达"在闭源游戏里**不可判定**。必须给出可操作定义：*存在一条当前构建里可被真实 UI 输入触发的路径，且该路径的守卫集合已被记录并复核*。否则它会退化成"我们认为玩家能"。<br>另：`save_game` 这类**玩家没有入口、但我们确实需要**的能力，必须显式归入"特权通路"（见 ADR-PP-5 的 `assisted` 模式），不能塞进 `strict_player` 再用 INV-01 打补丁。 |
| **INV-02 SEMANTICS**<br>不漏事 | **接受** | 与现有 `Defect B18/B26` 完全对应。要求每个 op 有具名 `expected_effects`，否则无法验收。 |
| **INV-03 OBSERVABILITY**<br>不虚报 | **接受，但需补一条** | 原文已正确指出"单信号不足"。必须补：**`no_effect` 的举证责任在服务端**——只有"守卫拒绝且未调用原生入口"才可判 `no_effect`；"回读没变"只能判 `uncertain`。这一点 RFC §5.6 写了，但 INV-03 表格里没体现，容易被实现成"没变化=失败"。 |
| **INV-04 RECOVERY**<br>不危险重试 | **接受** | 与现有 `ActionGate`（进程内幂等）+ recorder durable intent（进程外）一致。明确拒绝"跨进程 exactly-once"的表述是正确的。 |
| **INV-05 READ_PURITY**<br>观察不改玩法 | **接受，但当前不可全满足** | `bargain_preview` 的 `Refresh()` 是**原生实现的耦合**，不改原生就只能"禁用或接受"。`get_clientele` 的 RNG 消耗无法在不复制游戏公式的前提下消除。<br>因此 INV-05 必须允许**分级**：`pure` / `cached_impure`（每日首次消耗 RNG，之后纯缓存）/ `mutating`（禁用）。RFC 只说了"提供纯度证明或标记为非纯"，我的修正是：**标记为非纯还不够，必须在协议层把该工具从 `strict_player` 的 allowlist 移除**，否则模型仍会调它。 |

---

## 4. 对 RFC 各章的裁决

| RFC 章节 | 裁决 | 说明 |
|---|---|---|
| §1 问题定义（三个 parity + 五不变量） | **接受**（含 §3 的修正） | "有边界的可证伪等价"是本轮最重要的一句话。 |
| §2.2 "sleep 高危未修" | **拒绝（事实错误）** | 见 §2：`sleep` 已检查 `CanEndDay()`，有测试。RFC 基于我简报里的"已加 outside"，误以为只加了这一条。 |
| §3 架构（保留三层 + OperationTracker） | **修改后接受** | 结构合理，但 **`OperationTracker` 不必是新的 C# 子系统**：`ActionGate` 已经有 action_id 生命周期、结果缓存、`MarkResponseUndelivered`；把它扩成状态机是增量而非新建（见 ADR-PP-1）。 |
| §4 Operation Lifecycle | **接受** | 状态表可直接落地；但 `ok:true` 表示"已受理"**与现有模型契约冲突**，见 ADR-PP-6：我选择 `ok` 只表示 `completed`，受理另用 `status=pending`。 |
| §5 Outcome Verification（三信号） | **接受，但信号源要按本仓库实况重写** | 见 §6-Q2：本游戏**没有交易领域事件**；`ModHook.FireOnXxx` 只有开关店/睡觉/夜间/摆货/读档等，没有 `trade_committed`。三信号模型成立，但"领域事件"一格在交易域必须换成"物品唯一 ID 的所有权集合 + `isBusinessComplete` + 顾客离店"。 |
| §6 半状态与恢复 | **接受** | 与现有"不重试 + 停机会话"一致。 |
| §7 只读纯度 | **接受**（补 §3 的分级） | |
| §8 affordance / 隐式规则 | **接受** | `allowed/blocked_by/resolution` 比"多暴露 12 个布尔"更可执行。 |
| §9 三模式 | **接受** | 但必须服务端强制（C# 侧拒绝），不能只靠 `tools/list` 隐藏。现状：`RecordingDispatchGuard` 已挡住 `act→debug`、录制期 `debug_*`、以及 `new_game/load/save/press/sleep`；**但普通业务 op 没有模式判定**，而 `act` 会重新派发到任意普通 op（`src/PawnMindMod.cs:239-248`）——直连 HTTP 即可调用被 MCP 隐藏的普通 op。 |
| §10 四层测试 | **接受，L3 成本被低估** | 本仓库**没有任何 UI 输入驱动**能力（见 Q6）。L3 不是"补测试"，而是"新建一套 Unity 内测试装置"。 |
| §11 按业务域迁移 | **接受** | |
| §12 录像门槛 | **接受** | |
| §13 记忆接口 | **接受，暂不实施** | 现有 P2 记忆与 Bridge 事故隔离只做到"分库"，还没做到"标注 Bridge bug 不是游戏规则"。 |
| §14 12 问 | **部分无法回答** | Q3/Q7/Q11 需要真机或 IL 取证，见 §8。 |

---

## 5. Q1/Q2/Q6 的最小方案（能否复用现有件）

### 5.1 Q1（异步回执）：可以复用，不需要新框架

现状已有三件可复用的东西：

1. `ActionGate`：`action_id` 幂等 + 结果缓存 + `MarkResponseUndelivered`（`src/Core/ActionGate.cs:19-90`）。
2. recorder 的 durable intent（Python，fail-closed，`agent/memory/recorder.py`）。
3. `wait_for_events` 长轮询（`agent/pawnmind_client.py:98`，桥侧 `/events`）。

**最小方案**：把三者接起来，而不是引入 MCP Tasks：

```
POST /action → 返回 {status: completed|pending|rejected|uncertain, operation_id=action_id}
GET  /operations/{id} → C# ActionGate 的 Entry（加 status/phase/deadline）
MCP  wait_operation(id, timeout_ms) → Python 侧有界等待（不是主线程）
```

**关键增益**：异步动作（关门/睡觉）不再靠"回读一次没变就报 no_effect"。
本游戏还有一类**免费的领域事件**可用：`ModHook.FireOnShutterClosedEarly/Late`、`FireOnGoingSleepEarly/Late`、`FireOnHandlingNightlyServices*`。订阅它们是只读的，且比轮询状态更接近"真的发生了"。
**成本**：约 1 个 C# 类（状态机）+ 1 张 Python 表 + 2 个 MCP 工具；不需要改 104 个工具的签名（只加字段）。

### 5.2 Q2（回执证据）：交易域没有原生事件，必须用"可区分后置条件"

`ModHook.FireOn*` 全集（`_decomp/` 实测，去重后 **44** 个）里**没有**任何交易事件：
`ShutterOpened/Closed`、`GoingSleep`、`HandlingNightlyServices`、`GenerateCustomer`、`PlaceInventorInventoryItem`、`Afterhour*`、`GameLoaded*`、`Leaving/ReturningStore`、`LootRolled`、`NetworkUpgradeUnlocked`……

所以交易的回执只能靠：

1. **物品唯一 ID 的所有权集合**（不是数量、不是现金）——`back`/`front`/`main` 三个容器的 id 集合差分；
2. **`StoreClientInstance.IsClientBusinessDone()` / `isBusinessComplete`**；
3. **顾客离店**（`currentClientInstance == null`）作为流程终态；
4. 现金差值**只作辅助**（以物易物恒为 0）。

这不是"字段差分拍脑袋"，而是把 RFC 的单快照比较换成**带身份的信号集合**。
若三步都不可靠，才考虑对 `CompleteBusiness` 加只读 Harmony postfix —— 但这属于 ADR-PP-4 的受限准入。

### 5.3 Q6（独立 UI Oracle）：目前没有，但有可行路径

现状：`not_run`，仓库里没有任何输入注入。

可行路径（**都需要真机验证，当前标 `needs_real_game_test`**）：

- **推荐**：用 `PointerInputModule.inputOverride` 注入一个 `BaseInput` 子类，伪造鼠标位置/按键，让 EventSystem 走真实的 `GraphicRaycaster` → `Button.onClick`。这才是"玩家路径"，因为 `interactable=false`、遮挡层、射线目标都会生效。
- 备选：`ExecuteEvents.Execute(button.gameObject, pointerData, ExecuteEvents.pointerClickHandler)` —— 仍会绕过 `interactable` 与射线，**不能单独作为 Oracle**，只能作为"UI 存在性"探测。
- 反向验证：对每个被测 op，先构造"按钮不可点"的状态（灰化/被遮挡/动画锁），断言 Bridge 也拒绝。这类**负向**用例比正向更容易做，且正是当前事故的形态。

---

## 6. 必答六问

### Q1 `sleep` 还能绕过房东吗？

**不能（在当前代码上），但只在 `sleep` 与 `end_day` 两条路径上成立。**

证据链：
- `RecordingSaveService.Sleep` → `DayEndPolicy.EvaluateSleep(true, game.CanEndDay, game.WaitingCustomers, dismissWaiting, game.ShopState, game.Outside)`（`src/Bridge/RecordingSaveService.cs:254-256`）。
- `EvaluateSleep` 第一行就调 `EvaluateEndDay`，后者 `if (!canEndDay) return CannotEndDay`（`src/Core/DayEndPolicy.cs:31-41, 50-53`）。
- `RecordingSaveServiceTests.cs:387`：`AssertDenied(h.Service.Sleep(), "cannot_end_day")`。
- 跨日入口枚举：`end_day`、`sleep`、`load_game`（读档不跨日）、`new_game`（录制模式拒绝）。**没有第三条跨日路径**。

**残余风险**：`CanEndDay()` 本身是原生函数，它返回 true 时我们不再复核"房东是否真的不在"。若游戏中存在 `dismissable=true` 但本应强制招待的剧情客户，我们无法识别。**置信度：中高；需要一次真机负向用例（RFC `P0-LANDLORD-02`）才能升为高。**

### Q2 `confirm_trade` 真的走完"成交 → 收尾 → 送客"了吗？哪些分支仍可能静默 no-op？

**成交：是。收尾：不是由它完成。送客：不由它完成。**

- `ConfirmTrade` 只调 `AcceptNegotiationOffer()`（原生 `OnOfferAcceptClick`），随后**立即返回**（`src/Bridge/GameActions.cs` ConfirmTrade）。
- 收尾对话由模型调 `advance_dialogue` 推进；送客由 `DialogUIManager` 在对话结束后执行。也就是说：**如果模型不推进对话，顾客就不会走** —— 这是已知的、写进剧本的流程，不是 bug，但它意味着"`confirm_trade` 成功 ≠ 顾客离店"。
- **仍可能静默 no-op 的分支**（审计 B10–B16，均未修）：
  `IsTradeLocked()`（逮捕/枪战/射击）、`clientIntent == UNDEFINED`、`isBusinessComplete` 已置位、BARTER 条件不满足、INFO_DEALER 付不起服务费、GUNSMITH 枪单未接受、`CanBuyItemFromTable`/`CanSellThisItem` 拒绝（钱不够/顾客不买/超预算/品类重复）。
- 这些分支下原生 `return`，我们返回 `ok:true`。**这是当前最需要修的行为性缺陷**。

### Q3 `rev` 校验与原生写操作是否在主线程同一连续步骤？

**是。** 证据：

- HTTP 请求在 `BridgeServer.Tick` 里处理，而 `Tick` 由 `MainThreadPump.Update` 调用（`src/PawnMindMod.cs:135` 注释 + `src/Bridge/MainThreadPump.cs:22`）。
- `ActionRouter.HandleWrite`：action_id 校验 → 缓存探测 → 版本门禁 → `TryRefreshRevision()`（重建快照、刷新 `rev`）→ `_gate.Check(rev, currentRevision)` → **紧接** `Dispatch(op, request)`（`src/Bridge/ActionRouter.cs:165-210`）。中间没有 `yield`、没有 I/O、没有线程切换。
- 因此**不存在"HTTP 线程先查、主线程后写"的 TOCTOU**。

**但有两个真实限制**：
1. `rev` 是**快照指纹计数**，只覆盖我们序列化的字段（`src/Bridge/StateSnapshot.cs:11-25`）。UI 面板、隐藏字段、RNG 状态变化不改变 `rev`。
2. `TryRefreshRevision` 会重建整个快照（含若干 getter），这本身有开销；如果某个 getter 有副作用（见 Q5），刷新就会推进世界。

### Q4 超时/断连/半效果后，能否保证不自动重放不可逆操作？

**在当前实现下：同一进程内可以，跨重启不可以（且我们不声称可以）。**

- 同进程：`ActionGate` 以 `action_id` 为键缓存首次结果；重复请求返回缓存（`Duplicate`）；响应未送达时 `MarkResponseUndelivered` 把缓存改写成 `outcome_unknown`（`src/Bridge/ActionRouter.cs:213-227, 244+`；`src/Core/ActionGate.cs:19-22` 明确写"cache is bounded and in-process only"）。
- 跨进程：缓存丢失。**Python 侧**的 recorder 在启动时把挂起的 intent 标成 `unknown` 并拒绝重放（P1 已有测试）。**C# 侧没有任何持久化。**
- 因此"不会自动重放"依赖：模型遵守 `outcome_unknown → 停机`（提示词）+ recorder 的 fail-closed。**这是策略保证，不是机制保证**——一旦宿主绕过 MCP 直接打 HTTP，就没有第二道闸。

### Q5 哪些"只读"会改 UI/rev/RNG？strict 下怎么避免？

| 工具 | 副作用 | 证据 | strict 处置建议 |
|---|---|---|---|
| `get_state`（**最热**） | 每次重建快照时 `CanOffer*` → `BargainUIManager.Refresh()`（8 次）+ `can_report` → `Refresh()`（1 次），重置谈价状态位并 `ClearTradeRepMultiplierOverrides()` | 审计 S1/S2/S4；`src/Bridge/StateSnapshot.cs:88,181-188,312` | **必须修**：把 `CanOffer*` 从快照里摘掉或缓存（`needs_instrumentation`）。在修好前，所有"回读"都可能改变谈判结果 |
| `bargain_preview` | `GetBargainSuccessChance` → `Refresh()`，会重建/打开谈价面板并推 `rev` | `src/Bridge/BargainService.cs:37-38`；审计 S1 | **禁用**（allowlist 移除），或改为纯计算（`needs_instrumentation`） |
| `get_clientele` | 首次调用 `GetNormalCustomerCount()` → `RNG.Roll(100)`×2；之后按天缓存 | 审计 S3；`src/Bridge/ClienteleService.cs`（`cached` 字段） | 保留但标 `cached_impure`，首次返回 `rng_consumed: true` |
| `get_state` | 重建快照，可能触碰有副作用的 getter | `StateSnapshot.BuildJson` | `needs_instrumentation`：逐个 getter 分类 |
| `get_day_report` / `get_calendar` | 未发现副作用 | — | 保留 |

### Q6 如何用真实 UI 交互做独立 Oracle？

见 §5.3。一句话：**当前没有；推荐用 `PointerInputModule.inputOverride` + `BaseInput` 伪造输入，让 EventSystem 走真实射线与 `interactable`。**
`ExecuteEvents.Execute` 只能作为辅助探测，不能当作 Oracle（它绕过可交互性与遮挡）。
该能力本身需要先写一个 Unity 测试装置，属于 D5，**不阻塞 D1/D2**。

---

## 7. 与 RFC 的三处实质分歧

1. **`ok` 的语义**：RFC §4.3 建议 `ok:true` 表示"已受理"。我不同意：现有 104 个工具、剧本、记忆系统全都把 `ok:true` 当"完成"，改成"受理"会让模型误判（它不会去读 `status`）。**我的方案：`ok:true` 只在 `status=completed` 时出现；`pending` 用 `ok:false, pending:true`。** 兼容性由新增字段承担，而不是改变旧字段的语义。
2. **OperationTracker 的位置**：RFC 建议新增 C# 组件。我主张**扩展现有 `ActionGate`**（它已经是 action_id 生命周期 + 结果缓存 + 未送达改写），避免两套状态机并存。
3. **L3 UI 差分的优先级**：RFC 把它列为 Q6 的答案。我认为它**成本最高、收益最晚**，应排在 D5；先用 L0/L1 + 真机负向用例覆盖 P0，再谈 UI 差分。

---

## 8. 无法回答 / 需要取证（`UNKNOWN`）

| 项 | 原因 | 取证手段 |
|---|---|---|
| 运行中游戏的真实 MVID | 游戏未启动 | 启动后读 `/health` |
| `StoreShutterButton` 的 UI `interactable` 与遮挡关系 | 未读 `Button.interactable` 的运行时值 | 真机 + `PointerInputModule` 探针 |
| `TryPlayerDismissCurrentClient` 的拦截器行为 | 只读了反编译，未跑剧情客户 | 真机 |
| `GetBargainSuccessChance` 是否确定（不含 RNG） | 未读完整实现 | IL / 反编译 |
| `StateSnapshot` 各 getter 是否有副作用 | 未逐个审 | `needs_instrumentation` |
| `sleep` 保存是否完成 | 游戏异步写盘，我们只看到 `last_save_time` | 真机 |

---

## 9. 结论与阻塞项

- RFC 的**方向与五条不变量整体成立**，但 §2.2 对 `sleep` 的判断是事实错误，§4.3 的 `ok` 语义与现有契约冲突，§10 的 L3 成本被低估。
- **阻塞下一次录像的项**（D0）：`dist`/已安装 DLL 不一致；`confirm_trade` 的 7 个静默分支；`bargain_preview` 仍在 strict 工具面上；**`get_state` 的 `CanOffer*` 副作用**（它会让"回读"改变谈判状态）。
- 不阻塞：OperationTracker 的完整实现、L3 差分、Hook。
