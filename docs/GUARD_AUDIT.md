# PawnMind 原生守卫（guard）审计

- 目标游戏：Probably Stolen **0.46D**（本地反编译快照 `_decomp/`，该目录已 gitignore）
- 仓库：PawnMind（桥接 Mod）
- 性质：**纯审计**。不改生产代码、不碰游戏/桥/`PawnMind/play/`/`dist/`、不 commit。
- 引用约定：只写类型名/方法名/文件名 + 行号，不复制反编译代码。
- 行号来自本地 `_decomp/` 快照；游戏小版本更新后行号可能漂移，**类型名+方法名**才是稳定锚点。

---

## 0. 结论速览

| 项 | 数字 |
|---|---|
| 写 op 总数（dispatch 可见） | **58**（其中 `new_game` / `press` 恒拒绝，实写 56） |
| 表 A 已对齐（入口/谓词与原生一致） | **22** 条 |
| 表 B 缺口（原生有守卫、我们没查） | **43** 条 |
| 表 C 原生无显式守卫但依赖 UI 隐式状态 | **15** 条 |
| P0（存档不可恢复 / 死锁） | **7** 条 |
| P1（半状态 / 跳流程 / 状态不一致） | **19** 条 |
| P2（少字段 / 表现层 / 计数） | **17** 条 |
| 建议补进 `/state` 的低成本字段 | **12** 个（见附录 C） |

最严重的三个新发现（都不在已知坑列表里）：

1. **`end_day` 走的是 `PlayerStore.EndDay()` 内层方法**，而玩家唯一入口是 `StoreShutterButton.EndAndClose()`。跳过了 8 个收尾副作用（`visitLeftTonight=1`、`RemoveNotOwnedItemFromTable`、关店对话、延迟送客、卷帘门动画、`HideFrontArea` 协程等）。
2. **`open_shop` 走的是 `StoreShutterButton.OpenShutter()`，绕开了 `OnClick()` 的 3 个守卫**。其中 `storeState` 与 `lockShutterButtonFlag` 缺失意味着**可重复开门**：每多开一次就再生成一整队顾客，并重发当日晨间道具（含 `HandleWelfareProgram` 的兑换券、`HandleDividends` 分红货物、报纸）——这是可重复刷钱/刷物的存档级污染。
3. **`dismiss_current_client` 不查 `IsClientDimissable()`，且绕过 `tryInterceptDismiss`**。房东/剧情顾客能被直接送走——和已经修过的 `sleep` 跳过房东是同一类坑，只是换了个 op。

---

## 1. 口径与读法

### 1.1 三种归类

- **A｜已对齐**：桥调用的就是原生玩家点击的那个方法，且前置判断用的是**同一个函数**（括号里注明 `同函数`）；少数是"同语义、自写判断"，标注 `同语义(自写)`，需要人工复核是否等价。
- **B｜有守卫但没检查**：原生 UI 在玩家点下去之前会判断，桥没判断。**B 表的每一行是一个"缺口"，不是"op"**；同一个 op 可以有多行。每行给：缺失谓词、触发条件、后果、建议错误码、该谓词能否从 `/state` 读到。
  - `/state` 读不到 = **模型无法预判**，只能靠失败码试错；这类要优先补字段或补判断。
- **C｜原生没有显式守卫，但依赖 UI 隐式状态**：原生靠"玩家只能这样操作"来保证顺序/状态，代码里没有 if。这类要把隐式规则写成显式文档（或显式检查）。

### 1.2 严重度

| 级别 | 定义 | 例子 |
|---|---|---|
| **P0** | 存档不可恢复 / 重复刷资源 / 流程死锁 | 重复开店刷道具、把房东送走、出门后睡觉丢地面物品 |
| **P1** | 半状态、跳流程、状态不一致、静默 no-op 报成功 | 关店不清台面、交易不 CompleteBusiness、电话挂断丢 endAction |
| **P2** | 少个字段、表现层不同步、统计/计数缺失 | UI 数字不刷新、价格用错字段、缺 analytics |

### 1.3 `/state` 可读性标记

`/state` 现有字段见 `PawnMind/src/Bridge/StateSnapshot.cs:31-106`（`shop_state`、`outside`、`location`、`can_call_next`、`can_end_day`、`waiting_customers`、`negotiation_open`、`bargain`、`client.*`、`dialogue.*`、`back_counter`、`front_counter`、`prompt`；健康不在 `/state`，在 `health_status`）。标记：

- ✅ 已在 `/state`（或对应 status op 的返回）里，模型可预判
- 部分 能间接推断，或只有另一个 op 才看得到
- ❌ 读不到，模型只能撞错误码

---

## 2. 写 op 全集（58）

说明：`get_*`/`list_*`/`*_status`/`get_ui_snapshot` 等**纯读**不计入。`debug_pulse`/`debug_dump`/`capture_screenshot` 只写日志/截图，不算游戏状态写，单独列出。全部入口都在 `PawnMind/src/PawnMindMod.cs:225-773` 的 `DispatchAction` switch，或在 switch 之前的 `RecordingDispatchGuard.TryHandle`（`RecordingDispatchGuard.cs:21-119`，保护 `sleep`/`save*`/`load*`/`new_game`/`confirm_new_game`/`press`）。

### 2.1 店铺日循环与顾客（6）

| # | op | 桥接实现 | 原生玩家路径 | 归类 |
|---|---|---|---|---|
| 1 | `open_shop` | `GameActions.cs:15-39` → `StoreShutterButton.OpenShutter()` | `StoreShutterButton.OnClick()`（`StoreShutterButton.cs:59`） | **B**(3+1) |
| 2 | `call_next_customer` | `GameActions.cs:41-49` → `CanNextClient()` + `TryCallNextClient()` | 同一按钮（`StoreBell` 走 `CanNextClient`） | A |
| 3 | `advance_dialogue` | `GameActions.cs:85-98` → `DialogUIManager.OnAdvancePressed()` | 对话气泡点击（`DialogUIManager.cs:152`） | A / B43 |
| 4 | `dismiss_current_client` | `GameActions.cs:56-83` → `PlayerStore.DismissCurrentClient()` | `StoreDismissButton.OnClick()`（`StoreDismissButton.cs:44`） | **B**(3) |
| 5 | `choose_dialogue` | `FirstWeekService.cs:70-147` → `DialogUIManager.SelectChoice()` | 选项气泡（`DialogUIManager.cs:513`） | A |
| 6 | `end_day` | `FirstWeekService.cs:149-197` → `PlayerStore.EndDay()` | `StoreShutterButton.OnClick()`→`CloseShutter`→`EndAndClose`（`StoreShutterButton.cs:79/214`） | **B**(3) |

### 2.2 夜间（4）

| # | op | 桥接实现 | 原生玩家路径 | 归类 |
|---|---|---|---|---|
| 7 | `go_out` | `NightService.cs:45-71` → `MapUIManager.OpenUI()` + `Travel()` | `StoreDoor.OnClick()`（`StoreDoor.cs:40`）→ `ResponseList.CreateAfterhoursResponse()`（`ResponseList.cs:23`）→ 选背包 → `MapUIManager.OpenUI()` | **B**(3) / C(3) |
| 8 | `scavenge` | `NightService.cs:73-120` → `ScavHelper.CanScavenge()` + `ScavengeDumpingGrounds()` | `DumpingGroundLocation.OnScavenge()`（`DumpingGroundLocation.cs:80`） | A |
| 9 | `return_home` | `NightReturnService.cs:27-88` → `MapUIManager.Return()` | `MapUIManager.Return()`（`MapUIManager.cs:213`）+ 确认面板 | A / C(1) |
| 10 | `sleep` | `RecordingSaveService.cs:231-312` → `EndOfDayUIManager.OnEndDayButtonClick()` | `StoreBedroll.OnClick()`（`StoreBedroll.cs:21`）/ `MapUIManager.Sleep()`（`MapUIManager.cs:124`）→ 平板"结束这一天" | A(入口) / **B**(2) |

### 2.3 交易（3）

| # | op | 桥接实现 | 原生玩家路径 | 归类 |
|---|---|---|---|---|
| 11 | `confirm_trade` | `GameActions.cs:416-461` → `GameState.AcceptNegotiationOffer()` → `NegociationUIManager.OnOfferAcceptClick()` | 同一按钮（`NegociationUIManager.cs:1309`） | A(入口) / **B**(7) |
| 12 | `buy_wholesale` | `GameActions.cs:100-134`（手写搬钱搬货） | `NegociationUIManager.OnWholesaleClick()`（`NegociationUIManager.cs:1258`） | **B**(3) |
| 13 | `bargain` | `GameActions.cs:520-555` → `CanOffer*()` + `Offer*()` | `BargainUIManager.OnBargainButton()`（`BargainUIManager.cs:538`） | A / C7 |

### 2.4 提示、面板、弹窗（5）

| # | op | 桥接实现 | 原生玩家路径 | 归类 |
|---|---|---|---|---|
| 14 | `dismiss_tutorial_hints` | `TutorialService.cs:25-65` → `tutorialEnable=false` + `CloseAllTutorial` | 设置里的教程开关（非"跳过教程"按钮） | A |
| 15 | `close_windows` | `WindowService.cs:29-94` → `PaperUIManager.Close*()` / `WindowsHandler.HideAllMovable()` / 各平板 `CloseUI()` | 各面板自己的关闭按钮 | A / C(1) |
| 16 | `answer_prompt` | `PromptService.cs:58-107` → `GenericConfirmationUIManager.ConfirmButtonPressed/CancelButtonPressed` | 确认面板按钮 | A |
| 17 | `focus_new_game_selected_perk` | `NewGameMenuService.cs:230` | 天赋列表点击 | A |
| 18 | `blur_new_game_option` | `NewGameMenuService.cs:359` | 天赋列表失焦 | A |

### 2.5 新游戏与存档（10）

| # | op | 桥接实现 | 原生玩家路径 | 归类 |
|---|---|---|---|---|
| 19 | `open_new_game_menu` | `NewGameMenuService.cs:56` | 主菜单"新游戏" | A |
| 20 | `select_new_game_option` | `NewGameMenuService.cs`（`Select`） | 选项按钮 | A |
| 21 | `new_game_next_page` | `NewGameMenuService.cs:117` | 分页箭头 | A |
| 22 | `new_game_continue` | `NewGameMenuService.cs:434` | 继续按钮 | A |
| 23 | `confirm_new_game` | `RecordingSaveService.cs:116-200` | 难度页"开始" | A |
| 24 | `new_game` | `RecordingSaveService.cs:314-322`（恒拒绝） | —— | A(策略) |
| 25 | `press` | `RecordingSaveService.cs:324-332`（恒拒绝） | —— | A(策略) |
| 26 | `save_game` | `RecordingSaveService.cs:398-445` → `PlayerStore.SaveGame()` | 无可见按钮，游戏只在睡眠黑屏里自动存（`EndOfDayUIManager.cs:277`） | A(入口) / C(2) |
| 27 | `save_recording_game` | 同上（录制槽） | 同上 | 同上 |
| 28 | `load_game` / `load_recording_game` | `RecordingSaveService.cs:447-504` → `MainMenuUIController.StartNewGameClick()` | 主菜单"载入" | A |

### 2.6 商品、容器、机器、服务（9）

| # | op | 桥接实现 | 原生玩家路径 | 归类 |
|---|---|---|---|---|
| 29 | `move_item` | `GameActions.cs:152-224` | `ItemMouseDragHandler.EndDrag()`（`ItemMouseDragHandler.cs:580`） | **B**(3) |
| 30 | `commissary_buy` | `GameActions.cs:136-150` → `commissaryData.BuyItem()` | `CommissaryElement.OnBuyClick()`（`CommissaryElement.cs:26`）→ `CommissaryUIManager.CanBuy()`（`CommissaryUIManager.cs:68`） | A(同语义) |
| 31 | `toggle_service` | `ServiceStatusService.cs:41-65` → `isUsing=on` | `EndOfDayUIManager.OnToggled()`（`EndOfDayUIManager.cs:233`） | A(同语义) |
| 32 | `machine_insert` | `MachineService.cs:130-145` → `slot.Inventory.TryAcceptAll()` | 拖入机器仓储窗口（`ItemMouseDragHandler.EndDrag`） | **B**(2) |
| 33 | `machine_collect` | `MachineService.cs:147-180` → 逐个 `main.TryAcceptAll()` | 机器收取按钮 | **B**(1) |
| 34 | `buy_upgrade` | `UpgradeService.cs:45-76` → `playerCash-=cost` + `Unlock()` | `WildNetworkUI.OnUseClicked()`（`WildNetworkUI.cs:560`） | **B**(2) |
| 35 | `redeem_ticket` | `LotteryService.cs:75-90` → `CanRedeemPrize()` + `TryRedeemPrize()` | 彩票机/物品交互 | A |
| 36 | `deliver_request` | `P9ActionService.cs:11-40` → `CanFillRequest()` + `FillRequest()` | `StoreRequestElement.OnClick()`（`StoreRequestElement.cs:64`） | A |
| 37 | `analyze_item` | `AnalyzerService.cs:49-102` → `CanTarget()` + `Target()` + 读扫描 UI 文本 | 扫描仪窗口拖入 | B(1, UNVERIFIED) / C(1) |

### 2.7 水、酒、标签、笔、交互（8）

| # | op | 桥接实现 | 原生玩家路径 | 归类 |
|---|---|---|---|---|
| 38 | `wine_add_water` | `WineService.cs:31-47` → `WaterHelper.AddPureWater()` | 水龙头 target（`WaterHelper.cs:148-158`） | **B**(2) / C(1) |
| 39 | `wine_add_ingredient` | `WineService.cs:49-64` → `grid.TryAcceptAll()` | 拖拽到瓶内网格 | B(并入 #29/#32 规则) |
| 40 | `start_fermentation` | `WineService.cs:66-79` → `WineHelper.TryStartFermentation()` | 酿酒台按钮 | A(入口) / **B**(1) |
| 41 | `set_label` | `LabelService.cs:34-80` → 直接写 `feature.fakeCondition` | `LabelerUIManager`（标签机） | **B**(1) / C(1) |
| 42 | `set_pen_mode` | `MarkingPenService.cs:11-30` → 写 `MARKING_PEN_MODE_INT` | 记号笔窗口单选（`AnimalMarkHelper.cs:152-160`） | A(同语义) / C(1) |
| 43 | `activate_item` | `InteractionService.cs:45-61` → `item.ActivateSlotItem()` | 物品上下文菜单 | B(1，共用 #29 规则) |
| 44 | `toggle_item` | `InteractionService.cs:63-79` → `item.ToggleSlotItem()` | 物品上下文菜单 | 同上 |
| 45 | `use_tool` | `InteractionService.cs:81-100` → `CanTarget()` + `Target()` | 把工具拖到目标物品（`ItemMouseDragHandler.cs:639-646`） | 同上 |

### 2.8 电话（4）

| # | op | 桥接实现 | 原生玩家路径 | 归类 |
|---|---|---|---|---|
| 46 | `call` | `PhoneService.cs:68-94` → `OpenUI()` + `AutoCall()` | 键盘拨号 + 呼叫（`PhoneUIManager.cs:140/303`） | A(入口) / **B**(1) |
| 47 | `phone_advance` | `PhoneService.cs:115-124` → `DialoguePhoneUIManager.OnAdvancePressed()` | 电话气泡点击（`DialoguePhoneUIManager.cs:129`） | **B**(1) |
| 48 | `phone_choose` | `PhoneService.cs:130-145` → `SelectChoice()` | 电话选项（`DialoguePhoneUIManager.cs:390`） | **B**(1) |
| 49 | `end_call` | `PhoneService.cs:171-182` → `OnPhoneDialogEnd()` + `CloseUI()` | 挂断（`PhoneUIManager.cs:218/255`） | **B**(1) |

### 2.9 P9 世界（5）

| # | op | 桥接实现 | 原生玩家路径 | 归类 |
|---|---|---|---|---|
| 50 | `report_client` | `P9ActionService.cs:42-75` → `CanReport()` + `OnCustomerReported()` | `StoreSecButton.OnClick()`（`StoreSecButton.cs:68`） | A / B(1) |
| 51 | `barter_accept` | `P9ActionService.cs:77-96` → `offer.OnBarterAccepted()` | `NegociationUIManager.OnOfferAcceptClick()` BARTER 分支 → `PlayerStore.AcceptBarter()`（`PlayerStore.cs:3156`） | **B**(1) |
| 52 | `arrest_wanted` | `P9ActionService.cs:98-112` → `WantedUIManager.OnArrested(id)` | 无（该方法是纯表现，见 C15） | **C**(假写) |
| 53 | `dispatch_expedition` | `ExpeditionService.cs:26-67` | **无玩家路径**（debug-only） | C |
| 54 | `advance_expedition` | `ExpeditionService.cs:69-102` | 无玩家路径 | C |

### 2.10 调试 / 证据（3 写，计入 58）

| # | op | 桥接实现 | 原生玩家路径 | 归类 |
|---|---|---|---|---|
| 55 | `debug_grant` | `DebugService.cs:11-40` → `GeneralHelper.SetItemOwned` + `GraphUtils.EmporiumTryAdd` | 无（调试面板） | A(策略门禁) |
| 56 | `debug_fill_water` | `DebugService.cs:44-52` → `WaterHelper.FillWithPureWater` | 无（调试面板） | A(策略门禁) / 同 B34 的水质问题 |
| 57 | `debug_unlock_expedition` | `ExpeditionService.cs:11-24` | 无（调试面板） | A(策略门禁) |

> 编号到 57 而 op 数是 58：`#28` 一行含 `load_game` 与 `load_recording_game` 两个 op。
> 三个 debug 写都需 `allow_test_items`；录制中由 `RecordingDispatchGuard` 拒绝（`RecordingDispatchGuard.cs:121-134`），**造物/造水**，不进正常回归。
> 另有 `debug_pulse` / `debug_dump`（只写日志）与 `capture_screenshot`（只写截图账本），不是游戏状态写，未计入。

> 备注：`NightService.ReturnHomeJson()`（`NightService.cs:122-143`）是**死代码**——switch 用的是 `NightReturnService`。它比正式实现少"读确认面板"这一步，建议删掉，避免以后有人接错。

---

## 3. 表 A：已对齐（22 条）

| # | op | 原生方法（同一函数） | 对齐方式 | 备注 |
|---|---|---|---|---|
| A1 | `call_next_customer` | `PlayerStore.CanNextClient()`（`:1347`）+ `TryCallNextClient()`（`:1368`，内部重复校验） | 同函数 | 见 C1：不校验 `storeState` |
| A2 | `choose_dialogue` | `DialogUIManager.SelectChoice()`（`:513`）+ `IsChoiceEnabledAt()` | 同函数 | 有选择后 readback（`DialogueChoicePolicy.Changed`） |
| A3 | `answer_prompt` | `GenericConfirmationUIManager.ConfirmButtonPressed/CancelButtonPressed` | 同函数 | 单按钮面板禁 `cancel`（`PendingPrompt.cs:145-158`）+ 关闭 readback |
| A4 | `close_windows` | `PaperUIManager.CloseUI/CloseBook/CloseContract/CloseDeed`、`WindowsHandler.HideAllMovable`、四个平板 `CloseUI` | 同函数 | 有 `IsClean` readback；不关谈判/平板/地图/电话（C3） |
| A5 | `return_home` | `MapUIManager.Return()`（`:213`） | 同函数 | 会读 `GenericConfirmationUIManager` 并返回 `confirmation_required`（这一条是本仓库正面样例） |
| A6 | `sleep`（入口） | `EndOfDayUIManager.OnEndDayButtonClick()`（`:241`） | 同函数 | 入口正确、守卫不全（B8/B9） |
| A7 | `save_game` / `save_recording_game` | `PlayerStore.SaveGame()`（`:875`） | 同函数 | 见 C5：存档时机无守卫 |
| A8 | `load_game` / `load_recording_game` | `MainMenuUIController.StartNewGameClick()` | 同函数 | 先查 `!InGame` + 槽策略 |
| A9 | `confirm_new_game` | 菜单控件 + 难度页 | 同函数 | phase + `StartingGame` + readback 齐全 |
| A10 | `open_new_game_menu` / `select_new_game_option` / `new_game_next_page` / `new_game_continue` | 菜单原生控件 | 同函数 | 每条都有 phase 校验和 after-phase readback |
| A11 | `redeem_ticket` | `LotteryHelper.CanRedeemPrize()`（`:494`）+ `TryRedeemPrize()`（`:503`，内部再校验） | 同函数 | 双重守卫，安全 |
| A12 | `deliver_request` | `StoreRequest.CanFillRequest()`（`:213`）+ `FillRequest()`（`:248`） | 同函数 | `FillRequest` 自身不校验，靠 `CanFillRequest` 挡；注意 `StoreRequestElement.OnClick` 还会 `PCUIManager.OnChange/RefreshState`（跳过，低危） |
| A13 | `scavenge` | `ScavHelper.CanScavenge()`（`:243`）+ `ScavengeDumpingGrounds()`（`:70`，内部再校验） | 同函数 | `CanScavenge` 已含 `isWoundedFresh` 与剩余次数 |
| A14 | `dismiss_tutorial_hints` | `tutorialEnable=false` + `TutorialUIManager.CloseAllTutorial()` | 同语义(自写) | 刻意不走 `HandleSkipIntro`（会解锁服务/改剧情标志），见 `TutorialAdapter.cs:5-9` 注释 |
| A15 | `toggle_service` | `StoreService.isUsing` + `RefreshUI()` | 同语义(自写) | 已查 `unlocked`/`isMandatory`/`currentCooldown>0`，与 `EndOfDayUIManager.BuildServiceRow`（`:195-231`）等价 |
| A16 | `commissary_buy` | `CommissaryUIManager.CanBuy()`（`:68`）+ `CommissaryData.BuyItem()`（`:190`） | 同语义(自写) | 建议直接调 `CommissaryUIManager.CanBuy(id)` 以免日后漂移 |
| A17 | `set_pen_mode` | 写 `MARKING_PEN_MODE_INT`（`AnimalMarkHelper.cs:154-160` 同一 tag） | 同语义(自写) | 笔窗口单选不会即时同步（C10） |
| A18 | `bargain` | `BargainUIManager.CanOfferDiscount/Markup/BuyingMarkup/DemandBuyingDiscount`（`:660/678/714/732`）+ `Offer*`（内部再校验） | 同函数 | 见 C7：不查谈判面板/讨价还价面板是否打开 |
| A19 | `report_client` | `StoreClient.CanReport()`（`:1270`）+ `PlayerStore.OnCustomerReported()`（`:3451`，内部再校验） | 同函数 | 见 B41：`SEC_ALARM_IGNORED` 时原生按钮无效 |
| A20 | `start_fermentation` | `WineHelper.TryStartFermentation()`（`:427`，自守卫，返回 bool） | 同函数 | 见 B36：失败会弹模态 |
| A21 | `new_game` / `press` | —— | 策略拒绝 | 恒拒绝，不触达原生 |
| A22 | `debug_*`（3 个写） | —— | 策略门禁 | `allow_test_items` + 录制策略双门禁 |

> A 表只代表"入口/谓词对齐"。**A 里仍可能有缺口**（例如 A1 缺 `storeState`、A6 缺两个守卫），缺口统一列在表 B。

---

## 4. 表 B：有守卫但没检查（43 条缺口）

列说明：`/state` 列 = 该谓词能否从 `/state`（或对应 status op）读到；❌ 表示**模型无法预判**。

### 4.1 店铺日循环

| # | op | 缺失的谓词（原生出处） | 触发条件 | 可能后果 | 建议错误码 | /state |
|---|---|---|---|---|---|---|
| B1 | `open_shop` | `storeState == StoreState.MORNING`（`StoreShutterButton.cs:66`） | 已 OPEN 或已 CLOSED 时再调 `open_shop` | `OnShutterOpened()`（`PlayerStore.cs:1289`）无条件重建整队顾客；CLOSED 的一天被"复活" | `shop_not_morning` | ✅ `shop_state` |
| B2 | `open_shop` | `lockShutterButtonFlag > 0`（`StoreShutterButton.cs:62`） | 开门动画 2.75s 内（`OpenShutter` 里 `lockShutterButtonFlag++`，`StoreShutterButton.cs:96`）再开一次 | **重复发当日晨间道具**：报纸、`dayCounter` 2/4/6/9/11/13/25 的导师笔记与指南、`HandleWelfareProgram` 兑换券（`StartingPerkContent.cs:377-397`，每次调用都造券）、`HandleDividends()` 分红货（`ShareholderHelper.cs:76-105`）→ 可重复刷钱/刷物 | `shutter_busy` | ❌ |
| B3 | `open_shop` | `isClientBeingArrested` / `isHandlingShootout`（`StoreShutterButton.cs:62`） | 逮捕或枪战演出期间 | 打断演出协程，顾客/警方状态机错位；`StoreUIManager.isShot` 与 `storeState` 不一致 | `busy_arrest` / `busy_shootout` | ❌ |
| B4 | `open_shop` | 原生整段 `OpenShutter()`（`StoreShutterButton.cs:91-106`），不是 `PlayerStore.OnShutterOpened()+OpenShutter()` 手拼 | `StoreShutterButton.Instance` 为 null 或 `OpenShutter()` 抛异常（`GameActions.cs:29-38` 的 fallback） | 半序列：不跑 `OnShutterDoorOpenMorning`/`ShowFrontArea`/`OnLateShutterOpen`/`Validate`，`lockShutterButtonFlag` 恒 0 → 之后可无限重复开门；**却回报 `ok:true`** | `open_shop_failed`（不要吞异常） | ❌ |
| B5 | `end_day` | 玩家入口 `StoreShutterButton.EndAndClose()`（`:214-253`），不是 `PlayerStore.EndDay()`（`:1682`） | 任何 `end_day` | 跳过 8 个副作用：`visitLeftTonight=1`、`RemoveNotOwnedItemFromTable()`、`healthData.wentToAfterhoursLastNight=false`、`InterruptDialog`+`HideChoiceBubble`、关店收尾对话、`ToggleShutterDoor`、`HideFrontArea` 协程、4s 后 `DismissCurrentClient`、`lockShutterButtonFlag++` | `use_close_shutter` | 部分 |
| B6 | `end_day` | `storeState == OPEN`（`StoreShutterButton.cs:73`） | MORNING/CLOSED 时调 `end_day` | `EndDay()` 里 `storeClientManager.DismissClientStack()`（`StoreClientManager.cs:1545-1548` = `clientStack.Clear()`）把整队顾客**静默清空**，当天直接报废 | `shop_not_open` | ✅ `shop_state` |
| B7 | `end_day` | 关店后必须等当前顾客离开（`StoreShutterButton.cs:238-247` 的延迟 `DismissCurrentClient`） | `end_day` 后立刻 `call_next_customer` | `currentClientInstance != null` → `CanNextClient()` 恒 false；若该顾客 non-dismissable（房东），`dismiss_current_client` 又过不了 → **流程死锁** | `client_still_present` | ✅ `client_arrived` |
| B8 | `sleep` | `MapUIManager.Current == null`（即 `outside == false`；对照 `MapUIManager.Sleep()` `:124-134`） | 夜间出门（`go_out`）后直接 `sleep` | 跳过 `CloseUI()`+`CloseAllPaperUIForSleep()`+`OnArriveStore()`+`RestoreStoreUIAfterReturn()`；`afterhourInventory` 不 `TransferAfterhourToInv`（`MapUIManager.cs:290-296`）→ **地面物品永久丢失**，且店内 UI 停留在"已离店"状态 | `not_in_store` | ✅ `outside` |
| B9 | `sleep` | 当晚服务费 ≤ 现金（`EndOfDayUIManager.RefreshUI` `:108-131` 里 `endDayButton.SetInteractable(flag)`） | 现金不足以付已勾选服务 | 原生按钮是灰的、点不动；桥仍执行 → 现金变负 / `UpdateServiceCost` 结算异常 | `cannot_pay_services` | 部分（`cash`✅，服务费合计❌） |

### 4.2 交易

| # | op | 缺失的谓词（原生出处） | 触发条件 | 可能后果 | 建议错误码 | /state |
|---|---|---|---|---|---|---|
| B10 | `confirm_trade` | `NegociationUIManager.IsTradeLocked()`（`:463`，= `isClientBeingArrested` / `isHandlingShootout` / `StoreUIManager.isShot`） | 逮捕/枪战/射击状态 | `OnOfferAcceptClick()` 在 `:1311` 直接 return；桥报 `ok:true`，模型以为成交 | `trade_locked` | ❌ |
| B11 | `confirm_trade` | `client.clientIntent != UNDEFINED && !client.isBusinessComplete`（`NegociationUIManager.cs:1317-1320`） | 同一顾客重复确认 / 意图未定 | 静默 no-op 报成功；`isBusinessComplete` 之后再点可能重复触发收尾对话 | `business_complete` / `intent_undefined` | 部分（`client.intent`✅，`isBusinessComplete`❌） |
| B12 | `confirm_trade` | `NegociationUIManager.IsNegociatingItemOwned()`（`:270`），而不是自写 `GameState.IsOwned(item)`（`GameActions.cs:436`） | 物品已离开 `backInvinvElement` 但仍是 `currentNegociatedItem` | 原生要求 `backInvinvElement.Count != 0` 才算"上台"；自写判断与之不同源，方向（买/卖）可能判反 | `item_not_staged` | ✅ `back_counter` / `negotiated_item` |
| B13 | `confirm_trade` | BARTER 分支：`barterOffer != null && IsBarterAcceptable() && isIntroduced`（`NegociationUIManager.cs:1346`） | 以物易物顾客条件未满足 | 原生 return；桥报成功，顾客不成交也不离开 | `barter_not_acceptable` | 部分（`barter_status.acceptable`✅，`isIntroduced`❌） |
| B14 | `confirm_trade` | INFO_DEALER 分支：`CanPay(serviceCost) && isIntroduced`（`NegociationUIManager.cs:1352`） | 情报商服务费付不起 | 静默 no-op 报成功 | `cannot_afford` | 部分（`cash`✅，`serviceCost`❌） |
| B15 | `confirm_trade` | GUNSMITH 分支：`gunOrderData != null && IsAccepted()`（`NegociationUIManager.cs:1365`） | 枪单未接受 | 静默 no-op 报成功 | `gun_order_not_accepted` | ❌ |
| B16 | `confirm_trade` | BUY：`PlayerStore.CanBuyItemFromTable()`（`:2592`：`isRegularSellDisable`、back 非空、`playerCash >= 价值`）；SELL：`CanSellThisItem()`（`:2610`：`IsClientBuyingThisItem`、`CanClientExposeAnyFeatureDeep`、预算、`isRequireVariety`） | 钱不够 / 顾客不买这件 / 超预算 / 重复品类 | 原生按钮点不动，桥报成功；或超预算成交（经济/口碑数值异常） | `cannot_buy_from_table` / `client_refuses_item` | 部分（`sellable`✅、`budget`✅，`isRegularSellDisable`/`isRequireVariety`❌） |
| B17 | `buy_wholesale` | `IsTradeLocked()`（`NegociationUIManager.cs:1260`） | 逮捕/枪战 | 同上，整单批发在原生里被拦，桥照做 | `trade_locked` | ❌ |
| B18 | `buy_wholesale` | 收尾：`storeClient.CompleteBusiness()` + `StartDialogue(acceptDealDialogue/allDoneDialogue)`（`NegociationUIManager.cs:1278-1305` 的 SELL/SELLNBUY 分支） | 批发客户（SELL/SELLNBUY 意图） | `isBusinessComplete` 没置位、收尾对话没开 → **顾客不离店**，与旧版 `confirm_trade` 完全同型的 bug | `business_not_completed` | ❌ |
| B19 | `buy_wholesale` | `EmporiumEntry.SwapInv()`（`:1105`，前后各一次，`NegociationUIManager.cs:1271/1306`） | 任何批发 | 原生靠它把整批货摆上 back 台（"上台"）；桥直接按 `GetAllNonOwnedItem()` 搬走 → front 台残留，后续 `IsNegociatingItemOwned` 判定与原生不一致 | `wholesale_not_staged`（P2） | ✅ `front/back_counter_count` |

### 4.3 顾客与夜间门禁

| # | op | 缺失的谓词（原生出处） | 触发条件 | 可能后果 | 建议错误码 | /state |
|---|---|---|---|---|---|---|
| B20 | `dismiss_current_client` | `PlayerStore.IsClientDimissable()`（`:1386`；`StoreDismissButton.cs:49`） | 对房东/剧情顾客送客 | 跳过租金/剧情（和已修的 `sleep` 坑同型）；租金天数不推进，房东消失 | `client_not_dismissable` | ❌（`client` 里没有 `dismissable`） |
| B21 | `dismiss_current_client` | `PlayerStore.lockNextBellFlag <= 0`（`StoreDismissButton.cs:45`） | 切客/送客演出期间 | 重复 `DismissCurrentClient` → 二次 `OnClientStartLeaving`、动画冲突 | `bell_locked` | ❌ |
| B22 | `dismiss_current_client` | `PlayerStore.TryPlayerDismissCurrentClient()`（`:1609-1625`，含一次性 `tryInterceptDismiss`） | 带剧情拦截器的顾客 | 原生会让拦截器拒绝离开；桥直接 `DismissCurrentClient` → 剧情/委托链断 | `dismiss_intercepted` | ❌ |
| B23 | `go_out` | `StoreDoor.OnClick()`（`StoreDoor.cs:40-48`）的 6 个门禁：`!HealthData.IsSeriouslyWounded()`、`!IsEventActive("crackdown")`、`!StartingPerkContent.IsAgoraphobiaResting()`、`PlayerStore.isAfterhoursIntroduced`、`storeState == CLOSED`、`visitLeftTonight > 0` | 白天开店中出门 / 剧情未解锁 / 当晚已出门 / 重伤 / 戒严 | `MapUIManager.OpenUI()`（`:94`）会 `EmporiumEntry.OnLeaveStore()` 隐藏所有柜台 → **半开店铺**；同晚可无限次出门（`visitLeftTonight` 不复位，`OnArriveStore` 会 `visitLeftTonight--` → 变负）；跳过剧情门禁 | `shop_not_closed` / `not_introduced` / `no_visit_left` / `wounded` / `crackdown` | 部分（`shop_state`✅、`health.seriously_wounded`✅、`visitLeftTonight`❌、`isAfterhoursIntroduced`❌、`crackdown`❌） |
| B24 | `go_out` | `ResponseList.CreateAfterhoursResponse()`（`ResponseList.cs:23-72`）里的背包选择 → `EmporiumEntry.selectedBackpack`（唯一消费者 `TransferBackpackToInv`，`EmporiumEntry.cs:1876-1884`） | 任何出门 | 永远空手出门，口袋/背包位全空；`IsButcherHunting()` 时原生强制"背包里必须有枪"，桥直接放行 → 送死 | `backpack_required` | ❌ |
| B25 | `go_out` | `MapUIManager.IsShowingGoOutsideConfirm()`（`:146`，back 台有非己有物时先弹确认） | 台面还摆着顾客的货 | 直接把别人的货留在台上去过夜，回店后 `RemoveNotOwnedItemFromTable` 已错过 | `items_on_counter` | ✅ `back_counter_count` |

### 4.4 以物易物、升级、物品搬运

| # | op | 缺失的谓词（原生出处） | 触发条件 | 可能后果 | 建议错误码 | /state |
|---|---|---|---|---|---|---|
| B26 | `barter_accept` | 入口本身：`PlayerStore.AcceptBarter()`（`:3156-3175`）而不是 `BarterOffer.OnBarterAccepted()`（`:174`） | 任何以物易物 | 少了 `CompleteBusiness()`、`TransferOwnershipFrontInv/BackInv()`、`SwapInv()`、`MoveDealItemToInventory()`、`RemoveFeatureRelatedToEvent` → **所有权不转移、顾客不离店**（半状态，可重复交易） | `barter_not_completed` | 部分（`barter_status.acceptable`✅） |
| B27 | `buy_upgrade` | `NetworkUpgrade.GetCost()`（`:937`，受 `Difficulty.Rules.upgradePriceMultiplier`、`LLPILLAR`、`WildFavorite` 等系数影响）而不是裸 `upgrade.cost`（`UpgradeService.cs:55/61`） | 任何购买 | 扣款金额与玩家在 UI 看到的价格不一致（可多扣/少扣） | `price_mismatch` | ❌ |
| B28 | `buy_upgrade` | `NetworkUpgrade.IsReady()`（`:844`：前置 + `cooldownCurrent==0` + `state==1`）+ `wildFavor` 抵扣（`WildNetworkUI.cs:565-578`） | 冷却期升级 / 有 wild favor | 冷却期也能买（`Unlock()` 会把 `state` 再压回 1，但 `cooldownCurrent` 被重置）；有 favor 时原生先花 favor，桥全扣现金 | `upgrade_on_cooldown` | ❌（`wildFavor`/`cooldown` 未暴露） |
| B29 | `move_item` | `GameItem.MayRemove()`（`:1181-1205`：`parentInventory.IsRemoveLocked()`、`GeneralHelper.IsInsideNonOwnedContainer`、`mayInventoryRemoveItemFunc`） | 从非己有容器/锁定残骸里取物 | `GameInventory.Expel()`（`GameGridInventory.cs:744`）**不调用** `MayRemove` → 可以把顾客包里的货、锁定残骸取出来（偷取/复制） | `item_cannot_be_removed` | ❌ |
| B30 | `move_item` | `GeneralHelper.MayPlayerInsertInto(inventory)`（`:298-305`，非己有容器只允许 `GunHelper.IsServiceGun`） | 往非己有容器塞东西 | 可把物品写进顾客容器/未拥有的机器 → 物品"消失"或跨所有权转移 | `container_not_insertable` | ❌ |
| B31 | `move_item` | 拖拽允许集合里的 `MODULE_STUCK_TAG`、`HydroponicHelper.IsGrowingSeed()`（同款条件见 `GameItem.cs:1507`） | 取运行中的种子/卡住的模块 | 机器生产状态机错乱（进度 tag 在机器上，槽却空了） | `item_stuck` / `plant_growing` | ❌ |
| B32 | `machine_insert` | 同 B30 + 模块仓锁（`MachineHelper.LockModuleInv/UnlockModuleInv` `:179/185`、`GetLockedModuleBayMachine` `:199`） | 往锁定的模块仓插模块 | 机器行为异常/模块丢失 | `module_bay_locked` | ❌ |
| B33 | `machine_collect` | 机器"可收取"状态（`MachineProgressHelper.GetMachineState`；`machine_status` 已能读） | 生产中途收取 | 原料拿走、进度 tag 残留 → 产出丢失或卡住 | `machine_not_ready` | 部分（`machine_status.state`✅，但 `collect` 自己不读） |

### 4.5 水、酒、标签、电话、报告

| # | op | 缺失的谓词（原生出处） | 触发条件 | 可能后果 | 建议错误码 | /state |
|---|---|---|---|---|---|---|
| B34 | `wine_add_water` | 水龙头 target 的等级与条件（`WaterHelper.cs:148-158`：`Grade 1` + `StoreStation.todayRustwaterPurity/todayGutterflowPurity`，且需 `GUTTERFLOW_TAP` 或 `RUSTWATER_TAP`） | 任何加水 | 桥用 `AddPureWater`（`:1194` → Grade 0），等于免费造**最高纯度水** → 鉴定/陈酿/水质标签全被污染 | `wrong_water_grade` | ❌ |
| B35 | `wine_add_water` | `AddWater` 满瓶静默 return（`WaterHelper.cs:976-980`：`num3 <= 0` 直接 return） | 瓶已满 | 报 `ok:true` 但没加水（应前后比对容量） | `not_fillable` | ✅ `wine_status.ml` |
| B36 | `start_fermentation` | 失败路径会弹模态：`GenericConfirmationUIManager.OpenPanelSingleButton`（`WineHelper.cs:445-449`、`453-457`） | 无原料 / 水不足 | 桥返回 `ok:false` 但屏幕留下一个单按钮模态；模型不知道要 `answer_prompt`，后续动作全被挡 | `fermentation_blocked`（响应里要带 prompt） | ✅ `prompt` |
| B37 | `set_label` | 标签机工具链（`LabelerHelper.IsEditableLabelFeature` 之外，玩家还必须持有 labeler 并与物品交互；`LabelerUIManager`） | 任何改标签 | 可给任意物品贴任意可编辑标签（赝品/水质/成分），绕过工具与次数限制 | `labeler_not_available` | ❌ |
| B38 | `phone_advance` / `phone_choose` | `DialoguePhoneUIManager.OnAdvancePressed()`（`:129-150`）的 `currentDialog != null`、`!IsChoice()`、typing/pending 分支；`SelectChoice()`（`:390-407`）的 index 范围 + `IsChoiceEnabledAt` | 无电话对话 / 正在打字 / 选项禁用 / 索引越界 | 全部静默 no-op，桥随后返回 `ok:true` + 当前 state（模型只能靠读 dialogue 自己发现没动） | `no_phone_dialogue` / `choice_required` / `bad_index` / `choice_disabled` | 部分（`phone_state.dialogue`✅，`/state` 里没有电话块） |
| B39 | `end_call` | 挂断必须先让对话自然结束（`DialoguePhoneUIManager.InterruptDialog()` `:501-518` 只清 `currentDialog`，**不执行 `endAction`**） | 对话未播完就挂断 | 当前行的 `endAction`（成交/委托/联系人状态推进）被丢弃 → 电话剧情断链 | `dialogue_not_finished` | 部分（`phone_state.dialogue.ended`） |
| B40 | `call` | `PhoneUIManager.WillAnswerCall()`（`:323-347`）的 4 个拒绝条件（`NotResponding`、`None`、`IsPhoneClientAtStore`、供应商 + `LLAVOIDED`） | 打给不会接的人 | `AutoCall()` 先 `StopCall()`（会中断当前通话）再进入"无人接听"；桥仍报 `ok:true`（虽然回了 `will_answer:false`） | `call_will_not_answer` | ✅ `phone_state.in_call`（需额外调用） |
| B41 | `report_client` | `StoreReputation.IsPerkUnlocked("SEC_ALARM_IGNORED")`（`StoreSecButton.cs:71`） | 解锁了"警报被无视" perk | 原生按钮点了什么都不做；桥照常报警并拿赏金 → 绕过一个玩家可见的限制 | `alarm_ignored` | ❌ |
| B42 | `analyze_item` | 扫描结果来自 UI 文本框（`AnalyzerService.cs:104-139` 读 `WaterScannerUIManager/ChemScannerUIManager/PlantAnalyserUIManager` 的 TMP），不是 `Target()` 的返回值 | `Target()` 未同步刷新窗口 | 可能返回**上一件物品**的读数（错误情报） | `scan_readback_stale` | ❌ |

> **UNVERIFIED(B42)**：`item.Target(target)` → `ItemBehaviourManager.Target` 是否同步打开/刷新扫描窗口，未逐行确认。修之前先用真机验证一次"连续扫两件不同物品"。

### 4.6 对话推进（补）

| # | op | 缺失的谓词（原生出处） | 触发条件 | 可能后果 | 建议错误码 | /state |
|---|---|---|---|---|---|---|
| B43 | `advance_dialogue` | `DialogUIManager.OnAdvancePressed()`（`:152-163`）的三个前置：`IsAnyBubbleTyping()`、`isEndPending`、`isWaitingForManualAdvance`/`isAutoAdvancePending`。桥只查了 `currentDialog != null` 与 `!IsChoice()`（`GameActions.cs:92-94`） | 上一行还在打字机动画里 / 当前行是自动推进且未到点 | 一次 `advance_dialogue` 可能**只跳过打字机**，对话没前进；桥报 `ok:true` → 模型重复调用或误判剧情推进 | `line_still_typing` / `advance_not_ready` | ✅ `dialogue.typing` 已有，但 op 不查 |

> 这一条是已知坑（"没有当前对话时静默空操作"）的**剩余部分**：`currentDialog == null` 已被挡住，但"有对话、点了也没前进"的两种分支还在。

### 4.7 B 表统计

| 严重度 | 条目 |
|---|---|
| P0（7） | B2、B5、B6、B7、B8、B20、B26 |
| P1（19） | B1、B3、B4、B9、B10、B11、B13、B14、B15、B16、B17、B18、B22、B23、B24、B29、B30、B39、B43 |
| P2（17） | B12、B19、B21、B25、B27、B28、B31、B32、B33、B34、B35、B36、B37、B38、B40、B41、B42 |

---

## 5. 表 C：原生没有守卫，但依赖 UI 隐式状态（15 条）

这些不是"少写了一个 if"，而是"玩家必须按某个顺序操作，代码里没写"。桥要么把规则显式化，要么把状态暴露给模型。

| # | 隐式规则 | 依据 | 桥当前行为 | 建议 |
|---|---|---|---|---|
| C1 | **必须先 `open_shop` 才能 `call_next_customer`**：`CanNextClient()`（`:1347`）只看 `clientStack`、`isShot`、`isHandlingShootout`、`isClientBeingArrested`、`currentClientInstance`，**不看 `storeState`** | `PlayerStore.cs:1347-1366` | `call_next_customer` 同样不查 `storeState`；开门前 `clientStack` 为空，所以"碰巧"安全；但 `end_day` 直调 `EndDay` 后可以再 `open_shop` 刷队列 | 加 `shop_state == OPEN`；`legal_actions` 已按 OPEN 过滤（`LegalAction.cs:122`） |
| C2 | **"上台"= 物品在 `backInvinvElement`**：`UpdateUI(gameItem)`（`:476-483`）只在 `OnBackCounter(gameItem)` 时设 `currentNegociatedItem`；`RefreshNow`（`:504-514`）发现不在 back 就清空 | `NegociationUIManager.cs:270/303/476/504` | `confirm_trade` 已要求 `GetNegotiatedItem()==item_id`（`GameActions.cs:429-434`），但方向判断用自写 `IsOwned` | 改用 `IsNegociatingItemOwned()`；文档里把"必须先拖到 back 台"写死 |
| C3 | **`close_windows` 只关"纸/平板/浮动窗"**：不关 `EndOfDayUIManager.UI`、`NegociationUIManager.uiObject`、`MapUIManager`、`PhoneUIManager.phonePanel`、`GenericConfirmationUIManager.panel` | `WindowAdapter.cs:56-126`；对照 `StoreUIManager.cs:417-441` 的"谁在挡柜台" | `close_windows` 报告 `remaining` 只含 book/paper/contract/deed/movable | 在返回里显式列出 `negotiation_open`/`end_of_day_open`/`phone_open`/`map_open`/`prompt`，并说明各自该用哪个 op 关 |
| C4 | **`sleep` 只能在 CLOSED 且店内**：床垫 `StoreBedroll.OnClick()`（`:21-29`）先判 `storeState==CLOSED`；`MapUIManager.Sleep()`（`:124-134`）负责从地图回店 | 见左 | 只判 CLOSED（`DayEndPolicy.EvaluateSleep`），漏 `outside` | 见 B8 |
| C5 | **存档时机**：游戏只在睡眠黑屏序列里自动存（`EndOfDayUIManager.cs:277`），没有玩家可见的"保存"按钮 | `PlayerStore.SaveGame()`（`:875`）无任何前置校验 | `save_game` 可在对话中/拖拽中/面板打开时调 | 至少要求 `shop_state==CLOSED` 或 `outside==false` 且 `prompt==null`；否则写进文档"只在日终存" |
| C6 | **电话是异步三段**：`OpenUI` → `AutoCall`（`AutoCall` 里 `clickTimer=2.25f` 或 `sequenceTimer=1.5f`）→ `StartPhoneDialog` → `OnAdvancePressed` | `PhoneUIManager.cs:140/150/194/303-321` | `call` 返回 `state`，`phone_advance` 立刻可用 | 返回 `pending_seconds`/`in_call`，或让 `phone_advance` 在 `currentDialog==null` 时返回 `not_ready` |
| C7 | **讨价还价面板必须先打开**：唯一入口 `OnBargainClick()`（`:255-259`）→ `BargainUIManager.ToggleUI()`；面板停靠在谈判平板上 | `NegociationUIManager.cs:255`、`BargainUIManager.cs:1621-1657` | `bargain` 不查 `NegociationUIManager.IsOpen` / `BargainUIManager.IsOpen` | 补 `bargain_not_open`；`/state.bargain.open` 已有，但 op 不查 |
| C8 | **机器必须先拥有 + 供电 + 模块仓解锁**：`MachineHelper.CanPower`（`:99/109`）、`LockModuleInv/UnlockModuleInv`（`:179/185`）、`IsModuleBay`（`:169`） | 见左 | `machine_insert` 直接 `slot.Inventory.TryAcceptAll()` | 见 B32；并把 `can_power`/`module_bay_locked` 放 `machine_status` |
| C9 | **机器插入只搬物品，不跑机器逻辑**：UI 拖拽会触发仓储窗口的 `onChanged`/`MachineryIntake`/`MachineProgressHelper` 回调 | `ItemMouseDragHandler.cs:580-660`、`MachineHelper.cs:317-350` | `machine_insert` 只做 `TryAcceptAll` | 若机器需要"启动"才处理，补一个显式 `machine_start` 或文档写明"插入 ≠ 开始" |
| C10 | **记号笔模式窗口不同步**：改 tag 后，已打开的笔窗口单选仍显示旧值，直到 `onLoaded` 重开（`AnimalMarkHelper.cs:162-180`） | 见左 | `set_pen_mode` 写 tag 后不刷新 | 低危；要么不管，要么在返回里提示"重开窗口才可见" |
| C11 | **标签机是工具**：`LabelerHelper.IsEditableLabelFeature` 只是"这个 feature 可编辑"，不代表玩家手上/附近有 labeler | `LabelerHelper.cs`（`IsEditableLabelFeature`）、`LabelService.cs:48` | 只判 feature 可编辑 | 见 B37 |
| C12 | **彩票兑奖会销毁票**：`TryRedeemPrize`（`:503-515`）`gameItem.Destroy()` + `ModCash`，无确认面板 | 见左 | 已用 `CanRedeemPrize` 双重守卫 | 保持；文档里写明"不可撤销" |
| C13 | **以物易物的条件分支会"按条件匹配物品"再卖**：`BarterOffer.OnBarterAccepted()`（`:174-215`）按 `conditions` 挑 `GetOwnedItemsBack()`，非条件分支卖整个 back 台 | 见左 | 桥用错入口（见 B26） | 见 B26 |
| C14 | **`arrest_wanted` 名不副实**：`WantedUIManager.OnArrested(id)`（`:40-48`）只是给海报元素排一个 1.5s 后打叉的协程（`WantedElement.cs:34-50`），**不抓人、不改 `wantedStatus`、不发赏金** | 见左 | 桥返回 `ok:true` | 要么删掉这个 op，要么改成"报备/上报"语义；真正的抓捕走 `report_client`（wanted 分支）或 `OnWantedClientShot` |
| C15 | **探险是 debug-only**：`Expedition.StartExpedition/ContinueExpedition` 在 0.46D 没有玩家 UI 调用点（全仓仅 debug panel 触达） | `Expedition.cs:52/58/183` | `dispatch_expedition`/`advance_expedition` 直接调；`advance` 在 `CanContinue()==false` 时仍返回 `ok:true`（静默 no-op），异常时还 `EndExpedition()`（`ExpeditionService.cs:99`） | 标 debug-only；`advance` 加 `cannot_continue`；异常路径不要顺手 `EndExpedition` |

---

## 6. 附录 A：每个 op 对应的 `CanXxx()` / `IsXxx()` 全集（天然可暴露状态）

> 这些是游戏自己写的布尔函数。**优先直接调用它们**，不要重写判断（重写会随版本漂移）。

### 6.1 `PlayerStore`（`:1177-1816`）

| 函数 | 行 | 对应 op |
|---|---|---|
| `CanStartDay()` | 1177 | `open_shop`（恒 true，0.46D 里是空守卫） |
| `CanNextClient()` | 1347 | `call_next_customer` |
| `CanEndDay()` | 1816 | `end_day` / `sleep` |
| `IsClientPresent()` | 1377 | `dismiss_current_client`、`report_client` |
| `IsClientDimissable()` | 1386 | `dismiss_current_client` |
| `CanDismissCurrentClient()` | 1604 | `dismiss_current_client`（同一字段，未判空，调用方要先判 present） |
| `CanPay(int)` | 2487 | `confirm_trade`（情报商）、`buy_wholesale` |
| `CanBuyItemFromTable()` | 2592 | `confirm_trade`(buy) |
| `CanSellThisItem()` / `CanSellItemFromTable()` | 2610 / 2641 | `confirm_trade`(sell)、`/state.*.sellable` |
| `CanPayRent()` | 1470 | 日终/租金（未暴露） |
| `IsPlayerOwnThisItem(string)` | 1804 | 物品所有权（按 identifier） |

### 6.2 `NegociationUIManager`

| 函数 | 行 | 对应 op |
|---|---|---|
| `IsOpen`（属性） | 79 | `confirm_trade`、`/state.negotiation_open` |
| `IsNegociatingItemOwned()` | 270 | `confirm_trade`（方向判定） |
| `IsClientHaveEnoughToBuy()` | 449 | `confirm_trade`(sell 批量) |
| `IsTradeLocked()`（static） | 463 | `confirm_trade`、`buy_wholesale` |

### 6.3 `BargainUIManager`

| 函数 | 行 | 对应 op |
|---|---|---|
| `IsOpen` | 165 | `bargain` |
| `CanOfferDiscount` / `CanOfferMarkup` / `CanOfferBuyingMarkup` / `CanDemandBuyingDiscount` | 660 / 678 / 714 / 732 | `bargain`、`/state.bargain.can_*` |
| `IsCurrentClientCoerced()` | 635 | `report_client`（经 `CanReport`） |
| `IsCriminal()` / `IsEventCustomer()` | 645 / 650 | 讨价还价概率/展示 |
| `DiscountOffered/MarkupOffered/BuyingMarkupOffered/BuyingDiscountOffered` | 177-256 | `/state.bargain.*_used` |

### 6.4 其他类型

| 类型.函数 | 行 | 对应 op |
|---|---|---|
| `DialogUIManager.IsChoice()` | 473 | `advance_dialogue`、`choose_dialogue` |
| `DialogUIManager.IsCurrentDialogEnd()` | 405 | `advance_dialogue` |
| `DialogUIManager.IsLineStillDisplaying(Dialogue)` | 396 | `advance_dialogue`（打字中） |
| `DialoguePhoneUIManager.IsChoice()` | 354 | `phone_choose`（**注意：`currentDialog==null` 会 NRE**） |
| `MapUIManager.IsOutside` | 54 | `go_out`/`return_home`/`sleep` |
| `MapUIManager.IsInTransit` | 61 | `go_out`/`return_home`（在用标志） |
| `MapUIManager.IsShowingGoOutsideConfirm()` | 146 | `go_out` |
| `StoreClient.CanReport()` | 1270 | `report_client`（已含 `reported`/`isShot`/coerced/`arrestable`/`dismissable`） |
| `StoreClient.IsSellingContraband()` / `IsSellingStolen()` | 1205 / 1217 | `report_client` 分支 |
| `StoreClient.IsClientBuying()` / `IsClientSelling()` | 1229 / 见 6.3 | `bargain` 方向 |
| `BarterOffer.IsBarterAcceptable()` | 161 | `barter_accept`、`confirm_trade`(BARTER) |
| `CommissaryUIManager.CanBuy(string)` | 68 | `commissary_buy` |
| `LotteryHelper.CanRedeemPrize(GameItem)` | 494 | `redeem_ticket` |
| `WineHelper.CanModifyLiquid(GameItem)` | 114 | `wine_add_water` |
| `ScavHelper.CanScavenge()` | 243 | `scavenge` |
| `StoreRequest.CanFillRequest()` | 213 | `deliver_request` |
| `NetworkUpgrade.IsReady()` / `IsUnlocked()` / `IsHavePrerequisite()` | 844 / 873 / 900 | `buy_upgrade` |
| `StoreService.IsOnCooldown()` | 326 | `toggle_service` |
| `PlayerStore.HasInstance` | 675 | 所有 op 的"在不在游戏里" |
| `GameInventory.IsInsertLocked()` / `IsRemoveLocked()` | 226 / 235 | `move_item` |
| `GameItem.MayRemove()` | 1181 | `move_item`（**未接**） |
| `GeneralHelper.MayPlayerInsertInto(inv)` | 298 | `move_item`/`machine_insert`（**未接**） |
| `GeneralHelper.IsItemLocked(GameItem)` | 120 | `move_item`（`LOCKED_TAG`，未接） |
| `MachineHelper.CanPower(GameItem)` | 109 | `machine_insert`/`machine_collect` |
| `HealthData.IsSeriouslyWounded()` | —— | `go_out`（`health_status.seriously_wounded` 已暴露） |
| `StartingPerkContent.IsAgoraphobiaResting()` | —— | `go_out`（未暴露） |

---

## 7. 附录 B：副作用清单（含"看起来只读其实会动状态"）

### 7.1 读动作会改状态（必须在文档里标出来）

| # | 读 op / 读路径 | 会调到的原生方法 | 副作用 | 影响 |
|---|---|---|---|---|
| S1 | `bargain_preview`（`BargainService.cs:37-38`） | `BargainUIManager.GetBargainSuccessChance()`（`:1258`）→ `Refresh()`（`:1103`）；`GetBargainLeaveChance()`（`:1299`）同样 | `Refresh()` 在 `trackedClient != client` 时重置一整批状态位（`discountOffered`/`markupOffered`/`buyingDiscountApplied`… 约 15 个）并 `client.ClearTradeRepMultiplierOverrides()` | 讨价还价的"已用/已生效"内存态被清；若发生在接受报价之后，交易口碑倍率可能回退到基准值。**已知的坑，确认存在** |
| S2 | **每一次 `/state`**（`StateSnapshot.cs:88` 和 `:181-188`，各 4 次 `CanOffer*`） | 同上 `Refresh()` | 每份 `/state` 触发 **8 次** `Refresh()` | 见 S1；`/state` 是热路径（每个动作后都读一次），副作用频率很高 |
| S3 | `get_clientele`（`ClienteleAdapter.cs:145/178`） | `StoreClientManager.GetNormalCustomerCount()`（`:1390-1400`）、`ShowcaseHelper.GetDisplayCasePrediction()` | `GetNormalCustomerCount` 里 `RNG.Roll(100)` ×2 → 推进 `UnityEngine.Random` 状态；`GetDisplayCasePrediction` 缓存一次随机供应商挑选 | 后续 `GenerateClient()`/`scavenge`/`RNG` 结果改变 → **同一份存档不可复现**。已在 `ClienteleAdapter.cs:14-27` 注释说明，但没写进 `/state`/文档 |
| S4 | `/state.client.can_report`（`StateSnapshot.cs:312`） | `StoreClient.CanReport()` → `BargainUIManager.IsCurrentClientCoerced()` → `Refresh()` | 同 S1 | 每份 `/state` 再多 1 次 `Refresh()` |
| S5 | `barter_status`（`PlayerStatusService.cs:185`） | `BarterOffer.IsBarterAcceptable()`（`:161`）→ `getBarterResult()`（`StoreClientListBarter.cs:107/201/271` 等） | 现有 3 个 delegate 都是纯读（查 back 台有无某物）；**但这是每个 offer 自己注入的委托，不保证未来不掷骰** | 标 UNVERIFIED（当前实现纯读） |
| S6 | `list_goods`（`GoodsService.cs:114`） | `PlayerStore.GetWholesalePrice()`（`:3185`） | 纯算术（`RNG.CalculatePercentageOff` 只是 `value*percentage/100+value`，见 `RNG.cs:187-195`），**不消耗随机数** | 无副作用（澄清常见误解） |
| S7 | `get_ui_snapshot` / `list_labels` / `wine_status` | 只读 tag/TMP | 无 | —— |

### 7.2 写动作的副作用（会改 state / 弹 UI / 起协程）

| op | 副作用 |
|---|---|
| `open_shop` | 生成整队顾客（`PlayerStore.cs:1289`）；每晨道具（报纸/导师笔记/指南/许可/兑换券/分红货）；`lockShutterButtonFlag++` + 2.75s 协程；`EmporiumEntry.ShowFrontArea()`；`Validate()`。**幂等性：无**（重复调用重复发） |
| `call_next_customer` | 从 `clientStack` 取一个、`OnCustomerArrived`、`currentNegociatedItem=null`、`QueueClientArriveAnimation`（异步） |
| `advance_dialogue` | 可能只"跳过打字机"（`DialogUIManager.cs:152-163`）；可能 `FinishConversationNow` 执行 `endAction`；可能什么都不做 |
| `choose_dialogue` | 执行选项 `Item3()` 回调、切换 `currentDialog`、0.5s 后 `ContinueConversation` |
| `dismiss_current_client` | `RemoveNotOwnedItemFromTable`、`OnClientStartLeaving`、`callNextWhenPossible`、`InterruptDialog`、`OnDismiss` 回调。**绕过 `tryInterceptDismiss`**（B22） |
| `end_day` | `storeState=CLOSED`、`clientStack.Clear()`、`UpdateServiceCost()`。**不含**关店动画/对话/延迟送客（B5） |
| `sleep` | 黑屏序列（异步 0.5s+0.5s）、`EndNight`、`storeEventManager.OnDayEnd`、`BeginDay`、`SaveGame`（自动存档）、`StartOfDayUIManager.OpenUI` |
| `go_out` | `OnLeaveStore`（隐藏柜台/电话/平板）、`OnGoingOutside`（设置 `bagShown`、`TransferBackpackToInv`、`wentToAfterhoursLastNight=true`、`lastAfterhourOutingDay`）、`Travel` 里 `visits++`/`lastVisitDay`/`location.Arrive()` |
| `return_home` | 可能弹 `GenericConfirmationUIManager`（`MapUIManager.cs:490-506`）；`OnReturning` → `OnArriveStore`、`TransferAfterhourToInv`、`RestoreStoreUIAfterReturn` |
| `confirm_trade` | 一次点击里可能：`CompleteBusiness`、`InterruptDialog`、`StartDialogue`、`RefreshUI`、`Despawn`/`Destroy` 枪单物品、`OnItemBought`/`OnItemsSold`（口碑/统计） |
| `buy_wholesale` | 扣钱、逐件 `OnItemBought`、`MoveDealItemToInventory`、`isOfferWholesale=false`（**缺** CompleteBusiness/收尾对话/RefreshUI） |
| `bargain` | 改 `currentNegociatedItem` 的价值 modifier、`RecomputeTradeRepMultiplier`、可能让顾客**直接走人**（`BargainCanLeave`） |
| `report_client` | `FlushScheduledEndActions`、`InterruptDialog`、`reported=true`、`StartArrestSequence`（2.5s 协程）、`StartDialogue`、`GrantWantedCaptureReward`（加钱/加物品/改 `wantedStatus`）、延迟 `onSecAlarmLeftAction` |
| `barter_accept` | 只跑 `OnBarterAccepted` → `OnItemsSold`（3 个受控物品/整台）；**不 CompleteBusiness、不转移所有权**（B26） |
| `deliver_request` | 销毁交付物、发奖励、从 `storeRequests` 移除 |
| `commissary_buy` | 扣供货点、减库存、`TryAddToAfterhourInv`（直接进夜间库存） |
| `toggle_service` | 改 `isUsing`；不影响当日已发生的费用，但影响下一次 `UpdateServiceCost` |
| `buy_upgrade` | 扣现金、`Unlock()`（可能触发 `NetworkUpgradeList.UnlockActions`，例如解锁水龙头/展示柜/枪证 → **连锁改世界状态**）、`FireOnNetworkUpgradeUnlocked` |
| `redeem_ticket` | 销毁票、`ModCash` |
| `start_fermentation` | 改酒瓶状态 tag；失败时**弹模态**（B36） |
| `wine_add_water` / `wine_add_ingredient` | 改液体 tag / 移动物品（不可撤销） |
| `machine_insert` / `machine_collect` | 改机器仓储内容；不触发机器逻辑回调（C9） |
| `move_item` | `Expel`/`UncheckedAccept` 会触发 `onSlotRemoveItemFunc`/`onSlotInsertItemFunc` 与 `ValidateShapeState`；失败时**已实现回滚**（`GameActions.cs:275-333`），但只覆盖 repack 路径 |
| `set_label` | 改 `feature.fakeCondition` + `SetExposed(false)` + `OnWaterLabelUpdated` |
| `set_pen_mode` | 改 tag；窗口单选不同步（C10） |
| `activate_item` / `toggle_item` / `use_tool` | 走物品自身的 `ActivateSlotItem`/`ToggleSlotItem`/`Target` 回调（可能开窗口/起协程/改机器状态） |
| `analyze_item` | `Target()` + 读扫描 UI（可能开/刷新扫描窗） |
| `call` / `end_call` | 电话开面板、`StopCall` 中断、`InterruptDialog` 丢 `endAction`（B39） |
| `close_windows` | 逐个 `SetActive(false)`；**不关**谈判面板/结束这一天平板/地图/电话/模态（C3） |
| `answer_prompt` | 执行确认面板的闭包（可能是"丢地面物品"、"放弃交易"等不可撤销操作） |
| `save_game` | 序列化全部库存到存档（无守卫，C5） |
| `load_game` / `confirm_new_game` | 重建整个 run（策略 + 版本门禁保护） |

---

## 8. 附录 C：建议补进 `/state` 的低成本字段（按"能挡住多少种错误"排序）

低成本 = 一次原生属性/字段读取，不需要遍历、不需要 UI。

| 排序 | 建议字段 | 数据来源 | 能挡住的缺口 | 类型 |
|---|---|---|---|---|
| 1 | `shutter_locked` | `PlayerStore.lockShutterButtonFlag > 0` | B2（重复开门刷道具）、关店连点 | bool |
| 2 | `trade_locked` | `NegociationUIManager.IsTradeLocked()` | B3、B10、B17（3 条） | bool |
| 3 | `client.dismissable` / `client.introduced` / `client.business_complete` | `StoreClient.dismissable`、`isIntroduced`、`isBusinessComplete` | B11、B13、B14、B20（4 条） | 3 bool |
| 4 | `visit_left_tonight` + `afterhours_introduced` | `PlayerStore.visitLeftTonight`、`isAfterhoursIntroduced` | B23（含"同晚无限出门"）、B5 的 `visitLeftTonight` 副作用 | int + bool |
| 5 | `service_cost_total` | 遍历 `storeService` 里 `unlocked && isUsing` 求和（`EndOfDayUIManager.RefreshUI:108-121` 同款） | B9（付不起服务费仍睡） | int |
| 6 | `in_transit` | `MapUIManager.IsInTransit` | `go_out`/`return_home` 连点、异步未完成就下一步 | bool |
| 7 | `business_complete`（顶层） | `GameState.IsCurrentClientBusinessComplete()`（已存在，`GameState.cs:744`） | B18（批发）、B26（barter）、B11 | bool |
| 8 | `bargain_panel_open` / `end_of_day_open` / `phone_open` / `map_open` | 各 manager 的 `IsOpen`/`panel.activeInHierarchy` | C3（close_windows 关不掉什么）、C7（bargain 前提） | 4 bool |
| 9 | `wild_favor` + `upgrade_ready:<id>` 或 `upgrade.cooldown` | `PlayerStore.wildFavor`、`NetworkUpgrade.IsReady()`/`cooldownCurrent` | B28（冷却期买、favor 抵扣） | int + bool |
| 10 | `phone` 块（`in_call`、`dialogue.ended`、`will_answer`） | `PhoneUIManager.currentPhoneClient`、`DialoguePhoneUIManager` | B38、B39、B40（3 条） | object |
| 11 | `machine:<id>.ready` / `module_bay_locked` | `MachineProgressHelper.GetMachineState`、`MachineHelper.IsModuleBay`/锁 | B32、B33 | bool |
| 12 | `backpack_selected` | `EmporiumEntry.selectedBackpack != null` | B24（空手出门） | bool |

> 说明：`health.seriously_wounded` 已有（`health_status`），但不在 `/state`；建议直接塞进 `/state`，`go_out` 才能预判。

---

## 9. 优先级与前 10 个最该补的守卫

### 9.1 前 10（按"先修哪个"排序）

| 名次 | 缺口 | 级别 | 一句话后果 | 修法 |
|---|---|---|---|---|
| 1 | **B5 + B7**：`end_day` 走内层 `PlayerStore.EndDay()` | **P0** | 台面非己有物品跨天、`visitLeftTonight` 错乱、当前顾客残留导致 `call_next` 死锁 | 改调 `StoreShutterButton.CloseShutter()`/`EndAndClose()`（走玩家路径）；确实需要回落 `EndDay` 时必须**显式报 partial**（`dispatched:true, partial:true`），不能报完整成功；补 `shop_state == OPEN` 与"关店后顾客为空" |
| 2 | **B2 + B1 + B4**：`open_shop` 绕 `OnClick` 且可重复 | **P0** | 可重复刷整队顾客 + 晨间道具/兑换券/分红 → 存档污染 | 改调 `StoreShutterButton.OnClick()`（或显式判 `MORNING` + `lockShutterButtonFlag==0` + 非逮捕/非枪战）；**删掉半序列 fallback**，异常直接失败 |
| 3 | **B8**：`sleep` 在 `outside==true` 时执行 | **P0** | 地面物品不转移 = 永久丢失；店内 UI 停在"已离店" | 加 `outside == false`（+ `IsInTransit == false`），错误码 `not_in_store` |
| 4 | **B20 + B22**：`dismiss_current_client` 不查 `dismissable`、绕过 `tryInterceptDismiss` | **P0** | 房东/剧情顾客被送走，租金与剧情断链 | 改调 `PlayerStore.TryPlayerDismissCurrentClient(false)` 并前置 `IsClientDimissable()`；错误码 `client_not_dismissable` |
| 5 | **B6**：`end_day` 不查 `storeState==OPEN` | **P0** | 白天调用直接清空整队顾客、当天报废 | 加 `shop_state == OPEN`，`shop_not_open` |
| 6 | **B26**：`barter_accept` 用错入口 | **P0** | 所有权不转移、顾客不离店（可重复交易） | 改调 `PlayerStore.AcceptBarter()`（原生 BARTER 分支同款顺序） |
| 7 | **B23**：`go_out` 缺 6 个门禁 | **P1** | 半开店铺、同晚无限出门、跳过剧情门禁 | 至少补 `shop_state==CLOSED` + `!outside` + `visitLeftTonight>0`；其余写成文档 |
| 8 | **B18**：`buy_wholesale` 不收尾 | **P1** | 顾客不离店（旧 confirm_trade 同款） | 改调 `NegociationUIManager.OnWholesaleClick()`（并补 `IsTradeLocked`） |
| 9 | **B10 + B11**：`confirm_trade` 静默 no-op | **P1** | 模型以为成交，实际什么都没发生 | 补 `IsTradeLocked()`、`intent`/`isBusinessComplete`；错误码 `trade_locked` / `business_complete` |
| 10 | **B29 + B30**：`move_item` 缺 `MayRemove`/`MayPlayerInsertInto` | **P1** | 可偷取非己有容器内物品 / 把物品塞进顾客容器 | 两行判断即可；错误码 `item_cannot_be_removed` / `container_not_insertable` |

### 9.2 全量分级

**P0（7 条）**：B2、B5、B6、B7、B8、B20、B26
**P1（19 条）**：B1、B3、B4、B9、B10、B11、B13、B14、B15、B16、B17、B18、B22、B23、B24、B29、B30、B39、B43
**P2（17 条）**：B12、B19、B21、B25、B27、B28、B31、B32、B33、B34、B35、B36、B37、B38、B40、B41、B42

### 9.3 建议下一步先修哪 3 个

1. **`end_day` → 改走 `StoreShutterButton` 的关店/收尾路径**（B5/B6/B7）。
   - 理由：这是玩家每天必走的一步，影响面最大；且 `PlayerStore.EndDay()` 在 0.46D 里**只有 `EndAndClose` 一个调用者**，等于我们独创了一条游戏从没走过的路径。
   - 顺带把 `legal_actions` 里的 `sleep` 从 `OPEN` 分支挪走（`LegalAction.cs:122-123` 现在在 OPEN 下同时推荐 `end_day` 和 `sleep`，而 `sleep` 的守卫要求 CLOSED —— 自相矛盾，会诱导模型失败）。

2. **`open_shop` → 改走 `StoreShutterButton.OnClick()` 的守卫**（B1/B2/B3/B4）。
   - 理由：重复开门是**可重复刷资源**，属于存档污染；而且 fallback 半序列会把"失败"报成成功，是最难排查的一类。
   - 建议彻底删掉 `GameActions.cs:33-38` 的 `store.OnShutterOpened(); store.OpenShutter();` 兜底。

3. **`sleep` → 加 `outside == false`**（B8），顺手补 `service_cost_total <= cash`（B9）。
   - 理由：一行判断挡住"地面物品永久丢失"；B9 只是把原生灰按钮的语义补上。

第 4 个（半天工作量、立刻见效）：`dismiss_current_client` → `TryPlayerDismissCurrentClient` + `IsClientDimissable()`（B20/B22）。这是"房东被跳过"的第二个入口，和团队已经踩过的坑同型。

---

## 10. UNVERIFIED 清单

以下结论没有逐行确证，修之前请先真机/源码确认：

1. **B42**：`item.Target(target)` 是否同步打开并刷新扫描仪 UI（决定 `analyze_item` 返回的读数是否可能滞后）。
2. **S2/S1 的危害程度**：`Refresh()` 的 `ClearTradeRepMultiplierOverrides()` 是否真会在"同一次谈判中途"被触发。理论上 `GetClient()` 返回同一 `StoreClient` 实例时不会重置，但没有验证 `GetClientBlueprint()` 是否每次返回同一对象。
3. **B24**：`selectedBackpack == null` 出门后，`UsesAfterhourBag` 为 true 的地点仍会 `TransferAfterhourToInv`（`MapUIManager.cs:290-296`），所以地面战利品**不丢**；但口袋/背包位为空、`IsButcherHunting` 的"必须带枪"限制被绕过。若某些地点 `UsesAfterhourBag == false`，地面物品才会丢——需要逐地点确认。
4. **B28**：`Unlock()` 在 `cooldownDuration != 0` 且非 `heldUntilUsed` 时会把 `state` 压回 1（`NetworkUpgrade.cs:882-899`），所以"冷却期购买"的实际后果可能只是重置冷却，而不是永久解锁 —— 具体语义待确认。
5. **B9**：`UpdateServiceCost()` 在现金不足时是否会产生负现金/欠款状态，未验证；原生 UI 只是把按钮置灰。
6. **C9**：机器 `TryAcceptAll` 之后是否真的需要额外触发 `onChanged`/`MachineProgressHelper` 才会开始处理，未逐机器确认。
7. **`storeState` 的 `MORNING` 是否在 `BeginDay()` 之外还会被别处设置**（`StartOfDayUIManager.cs:66`、`EndOfDayUIManager.cs:254/261`、`PlayerStore.BeginDay()`），未穷举。
8. **`BeginDrag` 允许集合**只核对了 `GameItem.cs:1507`（`TryUnloadAll`）与 `InventorySortHelper.cs:35`，`ItemMouseDragHandler` 的 `BeginDrag` 具体 allow-list 未逐行读。

---

## 11. 复现方式（给别人看）

```bash
# 1) 确认原生入口存在（不需要游戏运行）
rg -n "public void OnClick|void EndAndClose|void OpenShutter" _decomp/StoreShutterButton.cs
rg -n "OnOfferAcceptClick|IsTradeLocked|IsNegociatingItemOwned" _decomp/NegociationUIManager.cs
rg -n "EndDay\(\)|CanEndDay|DismissClientStack" _decomp/PlayerStore.cs _decomp/StoreClientManager.cs

# 2) 确认桥的调用点
rg -n "OpenShutter|EndDay\(|OnOfferAcceptClick|DismissCurrentClient" PawnMind/src

# 3) 确认 /state 暴露面
rg -n "state\[" PawnMind/src/Bridge/StateSnapshot.cs
```

真机验证建议（每条缺口一个最小复现）：

| 缺口 | 最小复现 |
|---|---|
| B2 | 连续两次 `open_shop`（第二次在 3 秒内）→ 数 `waiting_customers` 与台面报纸/兑换券数量 |
| B5 | `end_day` 后立刻 `call_next_customer` → 应返回 `client_still_present`；再 `sleep` → 看次日台面非己有物品是否还在 |
| B8 | `go_out`（dumping_ground）→ `scavenge` 拿到东西 → 不回店直接 `sleep` → 看回店后地面物品是否消失 |
| B20 | 房东到店（`can_end_day == false`）→ `dismiss_current_client` → 看房东是否消失、租金是否推进 |
| B26 | 以物易物客户 → `barter_accept` → 看 `business_complete` 与所有权是否变化、顾客是否离开 |
| B18 | 批发客户 → `buy_wholesale` → 看 `client` 是否还在、是否出现收尾对话 |

---

*文档结束。下一步：把 9.3 的三条按 TDD 落成适配器契约测试（`PawnMind/test/PawnMind.AdapterTests`）+ Core 策略测试，再改生产代码。*
