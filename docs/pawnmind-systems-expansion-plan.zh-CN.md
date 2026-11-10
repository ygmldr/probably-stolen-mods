# PawnMind 系统扩展计划（P6–P11）

> 目标：在现有“背包 / 交易 / 夜间 / 阅读”之上，补齐彩票、电话、标签、分析仪/工具、机器、酿酒，以及用户确认要做的补漏项。
> 原则：优先语义接口，不靠截图；能用公开 hook 就不重复实现；TDD；真机统一用**槽 17**，不再 `new_game`。
> 游戏版本：0.46D / 100-REV6。

## 0. 现有基础

- `get_item` / `list_inventory` / `get_layout`：物品、容器、二维布局、形状 mask。
- `move_item`：指定格/旋转/重排，可把物品放进机器槽。
- 通用物品 hook（P6 的核心）：
  - `CanActivateSlotItem()` / `ActivateSlotItem()`
  - `CanToggleSlotItem()` / `ToggleSlotItem()`
  - `CanTarget(target)` / `Target(target)`
- 动作历史、rev、长轮询 `/events` 已可用。

## 1. 通用物品交互层（P6 地基）

### 工具

| 工具 | 作用 |
|---|---|
| `list_interactions(item_id)` | `can_activate / can_toggle / can_target` + 可用目标提示 |
| `activate_item(item_id)` | 书、彩票、扫描仪等 |
| `toggle_item(item_id)` | 基因扫描仪、部分机器开关 |
| `use_tool(tool_id, target_id)` | 螺丝刀、焊枪、剪线钳、UV、菜刀、标记笔等 |

### 约束

- 只对 `GeneralHelper.IsItemOwned` 的物品执行。
- `Target` 可能消耗工具/目标；执行前快照，异常返回 `partial`。
- 不读隐藏字段（鉴定真伪、隐藏基因值）。
- 目标校验走 `CanTarget`，不自己猜规则。

## 2. 分析仪（P6）

| 分析仪 | 入口 | 结构化结果 |
|---|---|---|
| 水质试纸 / AquaScan | `WaterScannerUIManager.Open(scanner, water)`；`GetTestedString` / `GetLabeledString` / `CanConfront` / `CanMatch` | 测试值、标签值、污染物、可对质/可贴标 |
| 化学品扫描仪 | `ChemScannerUIManager.Open`；`CanConfrontChem` / `CanMatchChem`；TMP 字段 | 成分、毒性、欣快、总容量 |
| 植物分析仪 | `PlantAnalyserUIManager.OpenUI(seed)`；`Refresh`；`GetContaminantSentitivity` / TMP | 营养范围、耗水、污染敏感度、生长评分 |
| 基因扫描仪 | `gene_scanner` 的 `onToggleSlotItemFunc` → `PlayerStore.isGeneScannerScanning` | 开关状态；基因信息只给游戏可见文本 |

### 工具

- `list_analyzers()`
- `analyze_item(analyzer_id, target_id)` → `{analyzer, target, fields, warnings}`
- `read_analyzer(analyzer_id)`（UI 已打开时读当前结果）

### 策略

- 优先走 `use_tool` 打开游戏 UI，再读 `currentItem/currentWaterContainer/currentScannedItem` + 公开 getter。
- 植物分析仪没有静态 getter，读 TMP 并解析；解析失败返回原文。
- 不做视觉：不读 sprite/颜色/隐藏真伪。

## 3. 标签打印机 + 标记笔（P6）

### 3.1 标签打印机

- 物品：`labeler` / `labeler_blue`。
- `LabelerUIManager.OpenUI(item)`；可编辑 `ItemFeature.fakeCondition`。
- 选项来自 `ItemConditionList.conditionLookup[category]`。
- `LabelerHelper.IsEditableLabelFeature` / `GetEditableLabelCount` / `CanEditLabel`。

工具：

- `list_labels(item_id)`：类别、当前条件、可选条件 id/显示值。
- `set_label(item_id, category, condition_id)`：设置 `fakeCondition`、`SetExposed(false)`、`WaterFeatureHelper.OnWaterLabelUpdated`。
- `repeat_label(item_id)`：复用 `LabelerUIManager.Repeat()`。

约束：

- 只改可编辑标签，不碰真实成分。
- 改标签影响交易/安检，必须幂等 + 历史。
- 蓝标签机范围按 `IsEditableLabelFeature` 实际判断。

### 3.2 标记笔（给鱼/动物做标记）

- 物品：`marking_pen`；`MARKING_PEN_TAG`。
- 模式：`MARKING_PEN_MODE_INT`，0–4 = 红/紫/绿/蓝/橙，5 = 橡皮擦（`AnimalMarkHelper.ERASER_MODE`）。
- 可标记：`AnimalMarkHelper.IsMarkable(item)`（`ANIMAL_ITEM_TAG` 或 `AquacultureHelper.IsFish`）。
- 标记：`SetMark(item, colorIndex)`；擦除：`ClearMark(item)`；读取：`GetColorIndex(item)` / `IsMarked`。

工具：

- `list_marking_pens()`：笔、当前颜色、模式。
- `set_pen_mode(pen_id, mode)`：0–5。
- `mark_item(pen_id, target_id)`：按当前模式标记/擦除（走 `use_tool` 的 `Target` 即可）。
- `list_marks()`：已标记的鱼/动物 + 颜色。

验收：真机标记一条鱼，读回颜色；再擦除。

## 4. 电话（P7）

### 联系人（`StorePhoneClient.InitPhoneClientDict`）

| 号码 | 联系人 | 类型 | 剧情/服务 |
|---|---|---|---|
| 8376 | Joe Wild 助手 | Wild | 选择浏览 `WildNetworkUI` 或挂断 |
| 8815 | Red Imp 代表 | Supplier | 初次送免费快递客户；之后订货/冷却 |
| 56371 | 矿业 | Supplier | 初次免费快递；之后矿物订单 |
| 4615 | Dale（制药） | Supplier | 初次免费快递；之后药品订单 |
| 3319 | Ashish（化学） | Supplier | 初次免费快递；订单分档，`amountSpent >= 400` 解锁大单 |
| 51189 | 革命商人 | Supplier | 稀有货订单 |

### 状态/分支

- `PhoneState`：`NotResponding / Responding / Introduction / Regular / Cooldown`。
- `dialedBefore`、`currentCooldown`、`cooldownDuration`、`amountSpent`、`stockType`。
- 初次通话 `Introduction` → 结束送 `courrierXFree`，`UseService()` 进入冷却。
- 常规 `Regular` → 选择订货档位 → `QueueFuturClient("courrierX", n)` → 冷却。
- `Cooldown` → 只报剩余天数。
- Wild 分支：`WildNetworkUI.Open()`，关闭电话。
- 联系人 alias 命中当前在店顾客时，`IsPhoneClientAtStore` 为真（不能在店打）。
- 电话对话走 `DialogUIManager`，现有 `dialogue`/`choose_dialogue` 可复用。

### 工具

- `list_contacts()`：号码、名字、状态、冷却、是否拨过、消费额、alias。
- `call(number)`：`AutoCall`；返回接通/无人接听。
- `phone_state()`：当前联系人/状态/是否在对话。
- `choose_dialogue(choice)`：现有工具即可选订单/挂断。
- `order_service(number, tier)`：语义化“订货”，内部走电话对话选择。
- `wild_network_list()` / `wild_network_buy(item_id)`：Wild 网络浏览/购买（P7 后段）。
- `end_call()`。

### 分支结论

电话没有多结局剧情；分支是**状态机 + 消费阈值 + 免费首单 + 送货客户**。Wild 是独立网络 UI，供应商是订单/冷却循环。

## 5. 彩票（P7）

### 范围

用户确认：**只读 + 兑奖，不做自动刮卡**。

### 入口

- `LotteryHelper.IsLotteryUnlocked()`（第 14 天解锁）、`GetCurrentEdition()`。
- 票：`lottery_ticket`，tag `LOTTERY_TICKET_TAG` / `LOTTERY_NUMBER_TAG` / `LOTTERY_EDITION_TAG` / `LOTTERY_WINNING_NUMBER_TAG` / `LOTTERY_MATCH_COUNT_TAG`。
- `CountMatches(ticket, winning)`、`GetPrizeForMatches`、`CanRedeemPrize` / `TryRedeemPrize`。
- 刮刮卡：`ScratcherUIManager` 只读状态，不自动刮。

### 工具

- `lottery_status()`：解锁、期号、开奖日、奖级表。
- `list_tickets()`：号码、期号、匹配、奖金、可兑。
- `inspect_ticket(item_id)`。
- `redeem_ticket(item_id)`。
- `scratch_card_status(item_id)`：只读，不刮。

约束：不预测开奖号；兑奖前 `CanRedeemPrize`；记录历史。

## 6. 机器（P8）

### 通用

- 枚举：`EmporiumEntry.GetAllOwnedItemsByTag("STANDARD_MACHINE_TAG")`。
- 状态：`MachineProgressHelper.GetMachineState`、`MACHINE_PROGRESS_CURRENT/TARGET`、`STATE_WORKING/READY`。
- 槽位：`MachineHelper.GetSlot` / `GetBodyStorage` / `GetBatterySlot` / `GetModuleInv` / `GetModules`。
- 电力：`HasBattery` / `CanPower`。

### 工具

- `list_machines()`：id、名称、类型、state、进度、电力、槽位摘要。
- `machine_status(machine_id)`：槽位/内容/模块/电池/产物。
- `machine_insert(machine_id, slot, item_id)`。
- `machine_start(machine_id)` / `machine_stop` / `machine_collect`。
- `machine_slots(machine_id)`。

### 适配

- 熔炉、水培（`GetGrowStatus`）、打印机、净化器、酒架、搅拌机、DNA 操纵器/解序器。
- 机器模块/维修：`MachineHelper` / `MachineBrokenHelper`（P9 顺带）。

## 7. 酿酒（P8）

- `WineHelper.TryStartFermentation` / `StartFermentation` / `FinishFermentation`。
- `IsFermentingOrFinished` / `IsFinishedWine` / `GetWineQualityTier` / `GetWaterQualityScore` / `GetWineExtractPerMille` / `GetWineContaminantPerMille`。
- `WineHelper.OnAgeWine`、`MachineWineRack.CanAgeWine` / `GetAgingDays`。

工具：

- `list_wine()`：瓶子、发酵状态、品质、提取率、污染物、陈酿天数。
- `start_fermentation(item_id)` / `wine_status(item_id)` / `age_wine(item_id)`。
- `pour_wine(source_id, target_id)`。

约束：发酵/陈酿跨天；只触发，状态读回。

## 8. 补漏项（全部要做）

| 系统 | 入口 | 计划 |
|---|---|---|
| 以物易物 | `BarterOffer` / `ExchangeBarter` / `BarterHelper` | P9：报价/条件/接受判定/交换 |
| 顾客需求/悬赏 | `StoreRequestList` / `WantedUIManager` | P9：列表/接取/交付 |
| 声望/派系/Perk | `StoreReputation` | P9：快照/事件增量/Perk |
| 违禁品/安检/突袭 | `ContrabandHelper` / `InspectionUIManager` / `RaidManager` | P9：风险扫描/安检/突击只读+应对 |
| 店铺升级/网络 | `NetworkUpgradeList` | P9：可购升级/价格/效果 |
| 健康/伤口/成瘾 | `HealthData` | P9：状态/治疗/风险 |
| 机器模块/维修 | `MachineHelper` / `MachineBrokenHelper` | P9：模块/损坏/修理 |
| 远征/雇佣兵 | `ExpeditionHelper` / `ExpeditionLocationList` | P10：地点/装备/派遣/结算 |
| 水培/养殖/宠物 | `HydroponicHelper` / `AquacultureHelper` / `HusbandryHelper` | P10：种植/喂食/收获/鱼群 |
| 营销/招牌 | `sign_*` 物品 | P10：招牌/营销位 |
| 保险/贷款/赎回 | `Repo` / foreclosure | P10：债务/赎回 |
| 事件/天气/节日 | `StoreEventManager` | P10：事件列表/影响 |

## 9. 分期与验收

| 阶段 | 内容 | 验收 |
|---|---|---|
| P6 | 通用交互 + 分析仪 + 标签 + 标记笔 | 修坏模块、测水、贴标、标记鱼 |
| P7 | 电话 + 彩票（只读/兑奖） | 打一次电话订货、读票、兑奖 |
| P8 | 机器 + 酿酒 | 水培种一株、熔炉处理、酒瓶发酵/陈酿 |
| P9 | barter/wanted/声望/安检/升级/健康/机器模块 | 各一次真机 |
| P10 | 远征/农场/营销/保险/事件 | 各一次真机 |
| P11 | 回归、文档、tag | 全量测试 + 3 天回归 |

## 10. TDD 与真机约定

- 先失败测试：纯模型（无 Unity）+ 适配器契约。
- 真机统一槽 17；不 `new_game`。
- 动作返回 `{ok, data, warnings, partial}`；失败给错误码。
- 所有动作进 `ActionHistory`；扫描器约束：无 Thread/Task/协程。

## 11. 风险

1. 分析仪/植物结果多为本地化 UI 文本，解析需原文兜底。
2. 标签会改变交易/安检，必须幂等 + 历史。
3. 机器启动/收取有动画/延迟，动作后轮询状态。
4. 彩票/电话有随机与冷却，不能承诺固定收益。
5. `Target` 可能消耗物品，失败回滚不完整。
6. 补漏项（barter/raid/expedition）状态复杂，先做只读再做动作。
