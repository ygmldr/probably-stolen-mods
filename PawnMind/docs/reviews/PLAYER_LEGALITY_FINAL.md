# PLAYER_LEGALITY_FINAL — AI 可授予写操作的逐项审计（Day-1 核心集）

> 基线：`r5.1/fixes`，本轮修复 `fed3690`（A1）、`18ebb79`（A3）、`3396d9d`（C 实证）。
> 范围：`--tools pilot` 面里 **11 个 Day-1 核心写操作**的八字段审计；其余 9 个是
> Day-0 新游戏菜单操作（侦察矩阵已标 `fixed`），本节只做处置说明，不重审。
> 判据：`player_entry` / `native_guards` / `pre_read` / `call_path` / `post_read` /
> `negative_test` / `L3_evidence` / `AI_enabled`，逐项写清**代码位置或 NOT_RUN**。

---

## 1. 结论表

| 操作 | 结论 | 一句话理由 |
|---|---|---|
| `open_shop` | **GRANTED** | 走原生 `StoreShutterButton.OnClick`，守卫镜像到 Core；**缺 post_read** |
| `call_next_customer` | **GRANTED** | **已改走原生铃铛 `OnClick→RingBell`**（§3.1 关闭）；仍缺 post_read |
| `advance_dialogue` | **GRANTED** | 前置检查充分；**已补 post_read**（对话 token 变化） |
| `choose_dialogue` | **GRANTED** | 前置 + 选中后回读对比 + 租金回执归因 |
| `stage_negotiation_item` | **GRANTED** | 前置 + 后态回读（staged id / 在台 / 谈判开 / 顾客身份）+ 实机 A→B→A |
| `confirm_trade` | **GRANTED** | 守卫 + 按笔证据（物品指针或现金，同一顾客）+ 实机多笔 |
| `dismiss_current_client` | **GRANTED** | 守卫完备（含不可送走的房东）；**已补 post_read** |
| `end_day` | **GRANTED** | 前置 + 关店前后回读对比 |
| `sleep` | **GRANTED** | 前置 + day 回读 + verifier 收敛 |
| `answer_prompt` | **GRANTED** | 前置身份/歧义检查 + 后态回读 + 派发状态如实 |
| `move_item` | **NOT_GRANTED（撤出 pilot）** | 2026-11-20 审阅指出：来源容器未校验、跨容器异常无回滚验证。修复已落地（R5.4），但**在后态与来源证据补全前不授予自主 pilot** |

`move_item` 已被移出 pilot 工具面（见 §5）。其余 10 项 GRANTED / GRANTED_WITH_GAP。

> **重要口径**：`GRANTED_WITH_GAP` **不等于无条件验收通过**。它表示"当前 pilot 可以用，
> 但有一个已记录的验证缺口"。未逐项审计的操作**不得因为被加进工具列表就视为合法**——
> 这正是 `dismiss_tutorial_hints` 被撤出的原因。

---

## 2. 逐项八字段

### 2.1 `open_shop`
| 字段 | 值 |
|---|---|
| player_entry | `StoreShutterButton.OnClick`（`GameActions.cs:58`） |
| native_guards | `lockShutterButtonFlag`、`isClientBeingArrested`、`isHandlingShootout`、`storeState==MORNING`、`CanStartDay()` |
| pre_read | `Core.OpenShopPolicy.Evaluate(...)` 镜像上述五项（`:38-49`），不满足返 `rejected` |
| call_path | **原生按钮 `button.OnClick()`**（`:58`），不是内部 `StartDay()` |
| post_read | **有**（R5.3/B）：`shop_state` MORNING→OPEN 才 completed（`Core/PostReadRules`） |
| negative_test | `P0-OPEN-01`（Core 策略单测，覆盖动画锁与重复开店） |
| L3_evidence | 多次真实开店（槽 54/55/56），`shop_state` 由 MORNING → OPEN |
| AI_enabled | 是（pilot） |

### 2.2 `call_next_customer`
| 字段 | 值 |
|---|---|
| player_entry | `StoreBell.RingBell`（矩阵）；实际代码走 `store.TryCallNextClient()`（`GameActions.cs:84`） |
| native_guards | `lockNextBellFlag`、`storeState==OPEN`、`CanNextClient()` |
| pre_read | `Core.ShopBellPolicy.Evaluate(...)`（`:76-81`） |
| call_path | `StoreBell.OnClick()` → `RingBell()`（原生铃铛；修复前是内部 `TryCallNextClient`） |
| post_read | **有**：顾客由 `none` → `client` |
| negative_test | `ShopBellPolicy` 单测 |
| L3_evidence | 真实叫号（多次），`client` 由 null → 名字 |
| AI_enabled | 是（pilot） |

**已修复（2026-11-20）**：改为 `FindObjectOfType<StoreBell>()` 后调用 `OnClick()`。
`RingBell()` 还会跑教程钩子、音频，以及"先送走可送走的顾客"分支——内部方法把
这些全跳过了。实机验证：`call_next_customer → completed`，导师正常到场。
适配器契约新增 `StoreBell` 类型与 `OnClick`（契约 526 → 528）。

### 2.3 `advance_dialogue`
| 字段 | 值 |
|---|---|
| player_entry | `DialogUIManager.OnBubbleClicked` |
| native_guards | 有当前对话、非选项态 |
| pre_read | `ui.GetCurrentDialog() != null`、`!ui.IsChoice()`（`GameActions.cs:137-141`） |
| call_path | `ui.OnAdvancePressed()` |
| post_read | **有**：对话 token（说话人+文本）变化或对话结束 |
| negative_test | 拒绝路径：`no_dialogue` / `choice_required` |
| L3_evidence | 多次推进（导师对话、顾客对话） |
| AI_enabled | 是（pilot） |

### 2.4 `choose_dialogue`
| 字段 | 值 |
|---|---|
| player_entry | `DialogChoiceBubble.OnDialogChoiceClick` |
| native_guards | 选项索引合法、未被禁用 |
| pre_read | `DialogueChoicePolicy.Validate(before, index)`（`FirstWeekService.cs:75`） |
| call_path | `dialoguePort.SelectChoice(index)` |
| post_read | **有**：选中后回读对话视图对比；并对租金建立 baseline，只把"选中后增加"归因为本次支付（`:80-120`） |
| negative_test | 选项校验单测 + 租金归因测试 |
| L3_evidence | 第 7 天租金对话选"付钱"，回执 `rent_1_200` |
| AI_enabled | 是（pilot） |

### 2.5 `stage_negotiation_item`
| 字段 | 值 |
|---|---|
| player_entry | 原生放置（拖到称重台）→ `PlayerStore.HandleAutoNegoUIDisplay` |
| native_guards | 顾客在场且身份可读、意图明确、谈判 UI 可读、无遮挡、物品存在且玩家可达、方向与所有权匹配、原生接受谓词 |
| pre_read | `NegotiationStagingGuard.Evaluate(...)`（全部前置，`ModalState` 三态） |
| call_path | 原生 `Expel` / `UncheckedAccept` / `TryAcceptAll`（`NegotiationStagingAdapter`） |
| post_read | **有**：`staged_item_id_after == item_id`、`item_on_counter`、`negotiation_open`、顾客身份不变 |
| negative_test | `R5_STAGE_*`（38 项）+ `R5_1_STAGE_01`（Unknown 模态） |
| L3_evidence | 槽 54：19→27→28→27 三次切换；槽 56：首笔 19→27 |
| AI_enabled | 是（pilot） |

### 2.6 `confirm_trade`
| 字段 | 值 |
|---|---|
| player_entry | `NegociationUIManager.OnOfferAcceptClick` |
| native_guards | 顾客在场、物品已暂存、方向匹配、原生 `CanSellItem/CanBuyItemFromTable`、预算预检 |
| pre_read | `TradeGuardPolicy` + `client_budget_insufficient` |
| call_path | `AcceptNegotiationOffer()` |
| post_read | **有**：同顾客 + （物品指针移动 ∨ 现金变动）；`cash_readable` 参与判定 |
| negative_test | `R5_TRADE_01..07`、`R5_1_TRADE_01/02` |
| L3_evidence | 槽 55/56 多笔买入卖出；预算不足被拒且不产生 uncertain |
| AI_enabled | 是（pilot） |

### 2.7 `dismiss_current_client`
| 字段 | 值 |
|---|---|
| player_entry | `StoreDismissButton.OnClick` |
| native_guards | 有当前顾客、`IsClientDimissable()`、无进行中对话 |
| pre_read | 上述三条（`GameActions.cs:99-112`） |
| call_path | `store.TryPlayerDismissCurrentClient(...)` —— **玩家路径** |
| post_read | **有**：顾客由 `client` → `none` |
| negative_test | `client_not_dismissable` / `dialogue_active` 拒绝路径 |
| L3_evidence | 槽 55/56 多次送走顾客；`legal_actions` 现已列出该动作（L3-1 修复） |
| AI_enabled | 是（pilot） |

### 2.8 `end_day`
| 字段 | 值 |
|---|---|
| player_entry | `StoreShutterButton.OnClick`（关店） |
| native_guards | 快门不忙、`storeState==OPEN`、无顾客（或显式 dismissWaiting）、`CanEndDay()` |
| pre_read | `DayEndPolicy.EvaluateEndDay(...)` + 快门忙/状态检查（`FirstWeekService.cs:151-180`） |
| call_path | `shopPort.EndDay(dismissWaiting)` |
| post_read | **有**：关店前后 `CompareShopDay` 回读对比 |
| negative_test | `customers_waiting` / `client_still_present` / `shutter_busy` |
| L3_evidence | 多次真实关店；无客户时直接 completed |
| AI_enabled | 是（pilot） |

### 2.9 `sleep`
| 字段 | 值 |
|---|---|
| player_entry | `StoreBedroll.OnClick` / `MapUIManager.Sleep` |
| native_guards | 在店内、已关店、无等待顾客、日终面板可点 |
| pre_read | `DayEndPolicy.EvaluateSleep(...)` |
| call_path | `EndOfDayUIManager.OnEndDayButtonClick` |
| post_read | **有**：day 回读 + `TryResolveSleep`（增长→completed，否则三读后 failed） |
| negative_test | `R5_SLEEP_01..06`、`Sleep_Verifier_*` |
| L3_evidence | 十几次跨日，全部 running → ~1.1s → completed，无双推进 |
| AI_enabled | 是（pilot） |

### 2.10 `answer_prompt`
| 字段 | 值 |
|---|---|
| player_entry | `GenericConfirmationUIManager.ConfirmButton` / 协商平板确认 |
| native_guards | 模态存在、可读、选项 enabled |
| pre_read | `PromptPolicy.ReadSafe` + `ValidateAnswer`（含歧义拒绝） |
| call_path | 按身份路由到对应原生回调（`promptPort.Answer(before.Prompt, choice)`） |
| post_read | **有**：`outcome.Dispatched` 如实反映是否点了；派发前后都回读 |
| negative_test | `R5_MODAL_01..08` |
| L3_evidence | **NOT_RUN**：该模态在 A3 生效后不再由 `confirm_trade` 触发（见 R5_LIVE_PILOT 附录 A.3） |
| AI_enabled | 是（pilot），但实机路径未覆盖 |

### 2.11 `move_item`
| 字段 | 值 |
|---|---|
| player_entry | `ItemMouseDragHandler.EndDrag` / 右键菜单 |
| native_guards | 形状合法、边界、`CheckShapePlacement`、锁定/固定 |
| pre_read | `Core.MoveItemPolicy`（本轮新增）+ 容器可达性 |
| call_path | 自动：`TryAcceptAll`；坐标：`Expel` + `UncheckedAccept` |
| post_read | **弱**：`item.parentInventory == target` + `ValidateShapeState()`，不逐格回读 |
| negative_test | `MoveItemPolicyTests`（8 项）+ 实机 6 个负例（`PLAYER_LEGALITY_L3.md`） |
| L3_evidence | 槽 56：6 个负例全部 `dispatched=false`；`no_space` 不重排 |
| AI_enabled | 是（pilot），带 §3 的缺口 |

---

## 3. 审计中发现、需要后续补的（按严重度）

1. ~~`call_next_customer` 走内部方法而非原生铃铛点击~~ —— **已修**（见 §2.2）。

2. ~~四处缺 post_read~~ —— **已补**（2026-11-20）。
   `Core/PostReadRules` + `Bridge/PostReadCheck`：派发前取 token，派发后回读。
   变化符合预期 → `completed`；可读但还没变 → 返 `running` 并挂 8 秒窗口，
   由 `VerifyOperation` 收敛（窗口内变了 → completed；窗口后仍没变 → failed
   `no_observable_change`；状态不可读 → uncertain `state_unreadable`）。
   **实测**：`open_shop`、`call_next_customer`、`advance_dialogue` 的回读都是
   **即时**命中（原生是同步的），`running` 窗口是安全网而不是常规路径。

3. **`move_item` 后态弱**（P1）
   只查 `parentInventory` 与 `ValidateShapeState()`，没有逐格回读坐标与占用。

4. **`answer_prompt` 的实机路径已不可达**（P2，已在 R5_LIVE_PILOT 附录 A.3 记录）
   A3 预算预检在原生点击前拒绝，超预算弹窗不再出现。

---

## 5. 2026-11-20 复审后的调整

| 操作 | 调整 | 依据 |
|---|---|---|
| `move_item` | **撤出 pilot** | 来源容器未校验（本次已修）；跨容器异常回滚不可证（本次已修）；后态不逐格（未修） |
| `dismiss_tutorial_hints` | **撤出 pilot** | 矩阵 `player_entry = NONE(no in-game toggle)`，没有玩家入口 |
| `close_windows` | **保留** | 走原生 `PaperUIManager.CloseBook/CloseUI/CloseContract/CloseDeed` 与各平板 `CloseUI`，与关闭按钮同一批原生方法；不触碰提示框/确认框 |

## 4. 拒绝集（不进 pilot 面）

`--tools recording` 面共 49 个写操作；pilot 取其中 20 个，其余 **29 个**一律不授予：

```
activate_item analyze_item arrest_wanted bargain bargain_preview
barter_accept barter_status call commissary_buy deliver_request
end_call get_clientele go_out load_game load_recording_game
machine_collect machine_insert phone_advance phone_choose
redeem_ticket report_client return_home scavenge set_pen_mode
start_fermentation toggle_item toggle_service use_tool wine_add_ingredient
```

（`load_game` / `load_recording_game` 仍在录制面，但不在 pilot 面；它们受 S4 槽绑定策略管辖。）

其中 `buy_wholesale` / `buy_upgrade` / `set_label` 已直接移出**录制面**（107→104），
因为它们直接写 `playerCash` / `fakeCondition`（`fed3690`）。

**根本不在录制面（只存在于 full 面，人工/调试用）**：`press`、`act`、`new_game`、
`save_game`、`save_recording_game`、所有 `debug_*`。
