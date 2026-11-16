# 给外部评审的技术简报：把游戏 AI 桥接做到"与玩家完全等价"

> 目的：我们正在给一个单机 Unity 游戏做"AI 能自己玩"的桥接层。最近的实机试跑暴露了一类系统性缺陷，
> 我们已经定位并修了一批，但**方法论层面还有几个判断题没有把握**。请重点批评第 5、6 节。

---

## 1. 背景

游戏：*Probably Stolen*（太空当铺模拟经营，Unity Mono，0.46D）。
目标：让一个 LLM（通过 MCP 工具）从新游戏菜单开始，真实经营一周、交上第一周租金，并录成视频。
形态：

```
Unity 游戏进程
  └─ C# Mod（PawnMind.dll）：监听 127.0.0.1:15528，暴露 ~100 个强类型 op
        └─ Python stdio MCP server：把 op 包成 MCP 工具
              └─ LLM 宿主（DSH）：模型只看到 mcp__pawnmind__* 工具
```

Mod 内部分层（硬约束）：
- `src/Core/`：纯逻辑，禁止引用 Unity/游戏类型。
- `src/Bridge/`：HTTP、鉴权、rev/幂等、动作路由、状态快照、策略。
- `src/Game/`：唯一允许反射/直连游戏类型的层，全部跑在 Unity 主线程（HTTP 线程只入队）。

关键不变量：
- 所有写动作必须带 `rev`（状态版本号），与最新不一致一律 `stale_rev` 拒绝；同时带 `action_id` 做幂等。
- 记忆/审计写入是 fail-closed：写不进历史，动作就不发。
- 只监听 loopback，除 `/health` 外都要 Bearer token。

## 2. 失败模式的来源

我们的 op 是直接调游戏的原生方法，例如：

```csharp
// 我们的 end_day
store.EndDay();

// 我们的 confirm_trade（旧版）
store.BuyItem(item);   // 只搬钱和物品
```

而**玩家实际触发**的是另一个方法，它包含前置校验与收尾：

```csharp
// 玩家点"关店"
public void OnClick() {
  if (lockShutterButtonFlag > 0 || isClientBeingArrested || isHandlingShootout) return;
  if (storeState == MORNING) { if (CanStartDay()) OpenShutter(); }
  else if (storeState == OPEN && (CanEndDay() || devMode)) CloseShutter();
}
public void CloseShutter() {
  if (clientStack.Count == 0) EndAndClose(); else ShowConfirmCloseUI();
}
// EndAndClose() 才做 8 件收尾事：清台面、关店对话、卷帘门动画、延迟送客、visitLeftTonight=1 …
```

于是出现了"桥能做成玩家做不出的事"。五个已确认的真实事故：

### 2.1 `sleep` 跳过了房东（存档级）

`PlayerStore.CanEndDay()` 的逻辑是：队列里任何 `dismissable == false` 的顾客（房东就是）就返回 false。
我们的 `end_day` 查了它，所以被拒：`cannot_end_day`。
但 `sleep` 没查——它走异步跨日，`EndNight` 里 `DismissClientStack()` 直接把整队清空。
模型学到的是"end_day 会被拒，那就 sleep"，连续 7 天把房东跳过，租金一次没收，第 8 天才发现。

### 2.2 成交后顾客不离店（流程死锁）

`confirm_trade` 直调 `BuyItem/SellItem`，没调 `CompleteBusiness()`、没启动收尾对话，
所以 `currentClientInstance` 永远非空 → `CanNextClient()` 恒 false → 客流卡死。
玩家点是 `NegociationUIManager.OnOfferAcceptClick()`，它做 `CompleteBusiness + StartDialogue(acceptDeal)`，
对话播完 `DialogUIManager` 才 `DismissCurrentClient`。

### 2.3 可以重复开门刷资源（存档污染）

`open_shop` 绕开 `OnClick()`，直接 `OnShutterOpened()`；后者每次都重建整队顾客并重发"当日晨间道具"
（报纸、导师笔记、福利兑换券、分红货物）。因为它没有把 `lockShutterButtonFlag` 加一，
2.75 秒动画期内可以再开一次 → **可重复刷钱刷物**。

### 2.4 "救命工具"又开了一个同样的口子

为了救 2.2 的死局，我们加了 `dismiss_current_client`（送客）。它没查 `IsClientDimissable()`，
于是**房东可以被这个新工具送走**——和 2.1 完全同型，只是换了个入口。

### 2.5 静默假成功

`advance_dialogue` 在"管理器存在但没有当前对话"时调用 `OnAdvancePressed()` 是空实现，
我们返回 `ok:true`，模型以为推进了。
同类：`confirm_trade` 在 `IsTradeLocked()`、意图未定、以物易物条件不满足等 7 个分支里，
原生都是静默 return，我们也报 ok。

### 2.6 副作用伪装成只读

`bargain_preview` 内部调 `BargainUIManager.GetBargainSuccessChance()`，它第一行是 `Refresh()`——
会重建/打开谈判面板并推高 rev；于是模型"看一眼价格"之后，下一步写动作撞 `stale_rev`。
另一个：`get_clientele` 读 `GetNormalCustomerCount()`，它会 `RNG.Roll()`，**看状态会推进随机流**。

## 3. 已做的修复（证明方向可行）

- `end_day` → 走 `StoreShutterButton.CloseShutter()/EndAndClose()`，补 `shop_state==OPEN` /
  `shutter_busy` / `client_still_present`。
- `open_shop` → 走 `OnClick()`，补 5 个前置，删掉"手拼半序列"的 fallback。
- `sleep` → 补 `outside == false`（夜里出门直接睡会丢地面物品）。
- `dismiss_current_client` → 补 `IsClientDimissable()`，改走 `TryPlayerDismissCurrentClient()`。
- `advance_dialogue` → 先读 `GetCurrentDialog()`，为空返回 `no_dialogue`。
- 新增 `close_windows`：原生没有"关窗"统一入口，我们按 `canMove` 过滤后调各自的 `CloseUI()`，
  并用"回读是否真的关掉"来判定成功。

## 4. 一次系统性审计的结论

我们把 58 个写 op 逐个对照玩家 UI 入口，得到：

| 分类 | 数量 | 含义 |
|---|---|---|
| A 已对齐 | 22 | 调的就是玩家那个方法、判的就是同一个谓词 |
| B 有守卫但没检查 | **43** | 其中 P0（存档不可恢复/死锁/刷资源）7、P1 19、P2 17 |
| C 隐式规则 | 15 | 原生没有 if，靠"玩家只能这样操作"保证顺序 |

C 类典型：
- "上谈判台"= 物品在 `backInvinvElement`，玩家只能靠拖拽实现；桥必须显式要求先 `move_item` 到 back。
- 讨价还价面板必须先打开；机器必须先有电、模块仓没锁；标签机必须先持有。
- 彩票兑奖会 `Destroy()` 票，不可撤销。
- `arrest_wanted` 名不副实：`WantedUIManager.OnArrested(id)` 只是在海报上打个叉，不抓人。

## 5. 我现在的方法论（请批评）

我提出三条，缺一不可：

1. **调玩家那个方法**，不调它的内层步骤。
2. **查玩家那个谓词**，不自己重写判断（同一个 `CanXxx()`）。
3. **做完回读确认真的发生了**；没发生变化就返回 `no_effect`，绝不报 `ok`。

第 3 条是我最近才加的，原因是：前两条能保证"我们没做玩家做不出的事"，但不能保证"我们做的事真的成了"。
对 `confirm_trade` 我打算改成三段式：

```
前置：IsTradeLocked + clientIntent 分支各自的谓词  → 精确错误码
动作：调用一次 OnOfferAcceptClick()
回读：cash / 库存 / isBusinessComplete 是否变化    → 没变 = no_effect
```

## 6. 我没把握的问题

### Q1 第 1 条真的永远对吗？

有些 op 的"玩家方法"是异步协程（关门要 4 秒后送客、睡觉是黑屏序列、电话是 `AutoCall` 定时器）。
调用它们之后，状态在**我们返回之后**才变。此时"回读"必然读到旧状态：
- 若坚持回读，就会把成功误判成 `no_effect`；
- 若不回读，又回到"报 ok 但不知道成没成"。

我们现在用 `outcome_unknown`（结果未知）表示这类，要求模型停机核对。但这对"每天都要做的事"太重。
**有没有更好的模式？** 我看到两种：
(a) 返回 `pending` + 一个 `wait_op`（模型显式等某个条件成立，带超时）；
(b) op 内部阻塞到条件成立（主线程 pump 里轮询），但会占用一次工具调用时长。
我倾向 (a)，但担心模型不会正确使用。

### Q2 回读"什么"才算可靠？

可观测面：`cash`、五个容器的 `childItems` 数量与 id 集合、`day`/`shop_state`、`client`（当前顾客 id/intent）、
`isBusinessComplete`、`dialogue`（是否在播）、`rent` 数字。
我担心两类假信号：
- 交易成交但金额恰好相同（比如以物易物，现金不变）；
- 游戏自己在帧间改了同一字段（电费、随机事件），让我们误判"是我们改的"。
**有没有更结构化的判据？** 例如"原生方法的返回值 + 事件回调"？我在考虑给关键原生事件挂 hook
（`ModHook` 里有一批 `FireOnXxx`），用"事件是否触发"替代字段差分。这会不会太侵入？

### Q3 半状态怎么处理？

现状：`rev` 在动作前校验，动作后不重试。但出现过"写返回 `stale_rev`，面板却打开了"的情况
（后来定位是 `bargain_preview` 的 `Refresh()` 副作用，不是写动作）。
即便如此，我仍然担心"原生调用抛异常但已经改了一半"。
现在的做法是抛异常就锁 `outcome_unknown`，要求人工核对。
**有没有更细的补偿/回滚策略**值得引入？（游戏没有事务，存档是唯一回滚点，而存档只在睡觉时写。）

### Q4 只读 op 的副作用要不要一刀切禁掉？

`bargain_preview`（推 rev）、`get_clientele`（推 RNG）、`GetNormalCustomerCount`（推 RNG）。
理想是"只读不改变任何东西"，但游戏没提供纯函数式接口。
我在考虑三种做法：
(a) 接受副作用，但在返回里显式告诉模型"这次读取改变了 X，rev 现在是 N"；
(b) 缓存（按天/按 rev），把副作用降到每次状态变化最多一次；
(c) 自己重算（复制游戏公式），彻底不碰原生——但会和版本漂移。
我目前是 (a)+(b) 混用。**哪种更可取？有没有第四种？**

### Q5 守卫要不要暴露给模型？

`/state` 现在有 `can_end_day`、`can_call_next`、`waiting_customers`、`negotiation_open`、`prompt` 等。
审计建议再补 12 个（`lockShutterButtonFlag`、`visitLeftTonight`、`isAfterhoursIntroduced`、
`isRequireVariety`、`serviceCostTotal`、`budge`…）。
支持：模型能预判，少撞错误码。
反对：状态体积膨胀（现在 `/state` 已经 6–10KB）；而且**暴露谓词 ≠ 暴露规则**，模型可能看到 `can_end_day=false`
却不知道原因。
**你会怎么设计？** 我倾向"只暴露能改变决策的聚合布尔 + 失败时返回 `blocked_by` 带原因码"。

### Q6 43 个缺口怎么防回归？

现在的测试是"纯逻辑单测 + 适配器契约测试（只断言类型/成员存在，不断言语义）"。
我们能测"我们调了 `OnClick`"，但测不了"原生 `OnClick` 的守卫和我们的前置一致"。
**有没有办法把"与原生等价"变成可执行的回归测试？** 我想到几条但都不满意：
- 反射读原生方法体/IL 比对（脆弱）；
- 真机跑一组"应当被拒"的操作（慢，且需要游戏在特定状态）；
- 只维护审计文档 + 人工复核（会腐烂）。

### Q7 什么时候可以"复制"而不是"调用"？

有些原生方法副作用太大或不可用，例如：
- `save_game`：游戏没有手动存档，我们只能调 `PlayerStore.SaveGame()`（玩家走不到这条路）。
- `get_clientele` 的预测：底层是 UI 的 `RefreshUI()` 逻辑。
- 开门动画：调用 `OnClick()` 会播 2.75 秒动画，录制时占画面时间。
**判据是什么？** 我现在用的是"玩家能不能走到"，但像"存档"这种玩家走不到、我们却需要的能力，就没有清晰答案。

### Q8 C 类隐式规则怎么落地？

15 条里有些没法用谓词表达，例如"必须先把货拖到 back 台才算上台"、"讨价还价面板必须先打开"。
现在只能写进给模型的剧本（`AGENTS.md`）。
**有没有更好的载体？** 例如把"当前可用动作"做成一个状态机（给出 op 的前置条件图），
让模型在调用前就能看到"要达成 X，先做 Y"。我担心这等于再写一套游戏逻辑。

## 7. 我想要的反馈

1. 第 5 节三条不变量的批评：有没有反例、有没有更本质的表述。
2. 第 6 节 8 个问题的直接回答（尤其 Q1、Q2、Q6）。
3. 如果你有类似"给闭源游戏做自动化驱动"的经验，请给一个**迁移路径**：
   在 33 个缺口、100 个 op 的规模下，怎么组织修复才不会变成打地鼠。
4. 任何你认为是**根本性错误**的地方，请直接说。

## 附：当前数字

- 录制用工具面 100 个 op（读 ~50 / 写 ~50）。
- C# 单测 651、适配器契约 490（只查存在性）、Python 197。
- 审计：A 22 / B 43（P0 7、P1 19、P2 17）/ C 15。
- 已修：P0 6 条、P1 4 条。
- 模型侧实测问题（一周试录制）：卡死 1 次（2.2）、跳过房东导致租金未付（2.1）、
  三个"静默假成功"（2.5）、一次 `stale_rev` 连锁（2.6）。
