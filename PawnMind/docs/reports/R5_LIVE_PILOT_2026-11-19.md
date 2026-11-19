# R5 实机试点记录（L3 TEST_MODE_PILOT）· 2026-11-19

> 执行者：Agent 自主操作（用户 2026-11-19 19:19 授权"可以自己开游戏进行调试，走实机"）。
> 环境：Windows 10 + *Probably Stolen* 0.46D，`VERSION 100-Rev13` / MVID `911508a6-…`。
> 构建：`r5.1/fixes` @ 本文件提交前，`PawnMind.dll` sha256 `770f3faa…`，MVID `9ca1ed59-…`。
> 模式：`run_mode=test`（**测试模式**）。按 handoff §5 的定义，本次是 **D1a L3 TEST_MODE_PILOT**，
> 不是正式 Play benchmark；`play_ready=false`、`play_allowlist` 为空。
> 存档：槽 54（本轮专用新槽，等于当时的 next free）。Day 1 700 → Day 8 438。

---

## 1. 结论速览

| 项 | 结果 | 证据等级 |
|---|---|---|
| `stage_negotiation_item` A→B→A 切换 | **PASS** | LIVE |
| 同一顾客连续多笔成交 | **PASS** | LIVE |
| 顾客预算不足的预检拒绝 | **PASS** | LIVE |
| sleep 异步观察窗（不误判、不双推进） | **PASS** | LIVE |
| 完整 7 天 + 租金支付 | **PASS** | LIVE |
| typed 日终确认框（B2） | **NOT_EXERCISED** | — |
| 首次开局 prompt（A1） | **NOT_EXERCISED**（本轮开局没有弹 prompt） | — |
| 正式 Play 工具面 | 仍 `BLOCKED_BY_PLAY_POLICY` | — |

**新的实机缺陷：2 个**（见 §4），都不是 R5.1 修复引入的回归。

---

## 2. 关键证据

### 2.1 staging A→B→A（B1 头号验收）

同一位顾客（拾荒客，SELL）连续三次切换谈判桌商品，全部 `completed`：

```
stage 27: ok=True completed | staged 19→27 | nego=True client_same=True
stage 28: ok=True completed | staged 27→28 | nego=True client_same=True
stage 27: ok=True completed | staged 28→27 | nego=True client_same=True
```

这是 R4 的 `move_item(main→back)` 两步法**做不到**的：那时指针停在第一件商品上，
后续 `confirm_trade` 全部 `item_not_staged`，直接导致 Day 4–7 零成交。

### 2.2 同一顾客连续多笔

```
buy 27 → completed, 700→675, items_transferred=false, cash_moved=true
stage 31: 27→31
buy 31 → completed, 675→660
```

第二笔没有 `operation_pending`，说明 R5 的按笔证据与按调用收敛都生效。

### 2.3 预算预检（A3）

顾客买不起时返回**终态拒绝**，不再产生 `uncertain`：

```
sell 15: rejected client_budget_insufficient
sell 27: rejected client_budget_insufficient
...
```

R4 现场是同样的场景连出 5 个 `uncertain`，把 5 件商品锁死。现在一次拒绝就是一次干净的拒绝。

### 2.4 sleep 异步窗（A2）

每次 sleep 都是：`execution_status=running` → **约 1.1 秒后 day 增长** → `completed`。
5 次跨日全部如此，没有出现"三次零间隔轮询判失败"，也没有双推进。

### 2.5 整周与租金

```
Day 1 700 → Day 8 438（其间 Day 7 付租金 200）
last_payment: id=rent_1_200 amount=200 cash 643→443
              source=dialogue_choice paid_at_rev=445 detection_complete
rent_paid_total=200 weeks_paid=1 weeks_missed=0，下周 due=209
```

---

## 3. 没有验证到的

- **typed 日终确认框**：本轮 6 次 `end_day` 全部直接 `completed`（无客户在等时），
  `get_pending_prompt` 恒为 `null`，弹窗一次都没出现。B2 的实现因此**没有被实机覆盖**。
  R4 现场它出现在"有客户在谈"的关店流程里——下一轮应刻意构造该状态。
- **first-run prompt（A1）**：本轮开局没弹，走的是一路 `new_game_continue`。
- 关店快门确认（handoff 审阅 §2 提到的 `closeShutterConfirmationPanel`）。

---

## 4. 实机发现（新增）

### L3-1 `legal_actions` 漏报 `dismiss_current_client`（中）

顾客所有报价都被预算预检拒掉后，`legal_actions` 只有 `confirm_trade` 与 `sleep`，
看上去"无事可做"；但 `dismiss_current_client` 实际可用且 `completed`。
一个按 `legal_actions` 决策的 LLM 会在这里卡死。

**影响**：R5-C3 的决策接口把 `legal_actions` 当输入，这条会直接导致死循环。
**建议**：要么补齐派生动作，要么在 `get_state` 里显式给出 `can_dismiss_client`。

### L3-2 `stage_negotiation_item` 对部分商品 `uncertain` 后永久污染（高）

三位顾客的商品（61/63/64 前后的几件）在暂存时得到
`outcome_unverified / staged_item_mismatch`，即"后态可读、但 requested item 没成为
staged"。因为 `uncertain` 是非终态，而那件商品的调用指纹已被占用，
**本进程内这件商品再也无法暂存**（换商品可以，换进程不行）。

**这与 R5-B 的 W-B1-2「hidden / trash 容器可达性」是同一类问题**：guard 判定这些
容器"玩家可达"，但原生拖放路径并不接受，于是拿到一个既不是成功也不是干净的失败的结果。

**建议**（下一轮）：
- 暂存前用原生的"是否可放上称重台"判断（如 `CanSellThisItem`/`CanBuyItemFromTable`）
  替代容器名推断；
- 或把"后态可读且 requested item 明确未被暂存"降级为终态 `failed`（需要先确认重试是否安全）。

---

## 5. 现场

- 游戏**仍在运行**（Day 8，MORNING，现金 438），槽 54 绑定中。
- 已安装 DLL `770f3faa…`；回滚副本在 `_internal/install-backup-2026-11-19-r51/`。
- `dev.txt` 保持删除状态（未使用 dev/scan）。

---

## 6. 与审阅 F 系列的关系

本次试点验证的是 **A2/A3、B1、B2b 之外的玩家等价路径**，不涉及审阅中的 F0/F1/F2/F3/F4
代码问题（那些已由 `69f8a72`、`1b1f70e` 修复并有单测）。
L3-2 与审阅 F3（`items_transferred` 语义）指向同一个根源：**"看起来放下了"不等于"确实放下了"**。


---

# 附录 A · 后续修复与再验证（2026-11-19 23:00–00:00）

用户在 23:00 授权继续。本轮针对上面 L3-1 / L3-2 做修复并再次实机验证（槽 55，新构建
`2e7caeb9…`，`run_mode=test`）。

## A.1 L3-2 根因（比原判断更准确）

不是"某些商品不可暂存"，而是 **staging 只能切换已打开的谈判，不能发起第一笔**。

`NegociationUIManager` 的存在性被当成"可读"的判据：

```csharp
input.NegotiationReadable = ui != null && ui.uiObject != null;   // 旧
```

但游戏是在**物品落到称重台**时才创建这个 UI（`PlayerStore.HandleAutoNegoUIDisplay`），
而"落上去"正是 staging 要做的事。于是每次访问的第一笔暂存必然
`negotiation_unreadable`。之前 L3-2 观察到的 `staged_item_mismatch` 是同一根因在
"UI 已被销毁但 negotiated_item 还在"时的另一种表现。

**修法**：manager 不存在 = 可读的"还没打开"；只有 manager 存在但读取抛异常才算不可读。
后置校验（`uiObject`/`IsOpen` 必须成立）保持不变，避免把"放了但看不到"当成功。

**再验证（LIVE）**：

```
before: negotiation_open: False
stage 27: ok=True completed | staged 19→27 | nego=True
after:  negotiation_open: True, negotiated 繁星果糊
confirm_trade(27) → completed 700→675；28、29 连续成交 650、635
```

## A.2 L3-1 修复与再验证

`GameFlags.CanDismissClient`（`PlayerStore.IsClientDimissable()`）→ 顾客可送走时
`legal_actions` 包含 `dismiss_current_client`。

**再验证（LIVE）**：顾客对话阶段的状态里直接出现
`legal=['advance_dialogue', 'dismiss_current_client', 'sleep']`。

## A.3 新发现 L3-3：B2 的 typed modal 在当前配置下不可达（重要）

反编译确认 `NegociationUIManager.OpenConfirmPanel()` 是**超顾客预算警告**
（`ui_nego_budget_warning`），由"出售"按钮在 `IsClientHaveEnoughToBuy()` 为假时弹出。

但 R5-A3 的**预算预检**会在调用原生出售之前就拒绝
（`client_budget_insufficient`，终态、不派发）。因此：

- 同一情形被两层覆盖：A3 前置拒绝（干净、终态）+ B2 弹窗处理（原本给原生点击兜底）；
- 在 A3 生效的前提下，**B2 的弹窗在 `confirm_trade` 路径上不会再出现**，
  L3 无法通过这条路径验证它。

原审阅（GPT 2026-11-19 §2）说"B2 修的不是关店弹窗"，这一点现在有了源码依据。
同时也说明 handoff 里"日终确认框"的措辞需要更正为**超预算确认框**。

**建议**：保留两层（防御纵深），但把 B2 的验证条件改写为"顾客预算在点击瞬间变化"
这类竞态；正式 Play 前需要一次专门构造（例如多件合并出售导致总价超过预算）。

## A.4 本轮未做

- F6（typed modal 身份绑定）：对应的就是 L3-3 里那条目前不可达的弹窗，优先级随之下调。
- F7（overlay 检测）：需要真实的 overlay 遮挡样本，本轮没构造出来。
- 完整 7 天：本轮只验证到 Day 1 的修复点，没有再跑整周（前一版构建已连跑两次）。
