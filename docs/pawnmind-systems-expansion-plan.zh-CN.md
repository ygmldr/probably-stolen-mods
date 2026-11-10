# PawnMind 系统扩展计划（P6+）

> 目标：在现有“背包 / 交易 / 夜间 / 阅读”之上，补齐彩票、电话、标签、分析仪/工具、机器与酿酒。
> 原则：优先做**语义接口**，不靠截图；能用游戏公开 hook 就不重复实现；TDD；真机统一用槽 17。
> 版本：0.46D / 100-REV6。

## 0. 现有基础（可直接复用）

- `get_item` / `list_inventory` / `get_layout`：物品、容器、二维布局、形状 mask。
- `move_item`：指定格/旋转/重排，能把物品放进机器槽。
- `GameItem` 的公开 hook：
  - `CanActivateSlotItem()` / `ActivateSlotItem()`
  - `CanToggleSlotItem()` / `ToggleSlotItem()`
  - `CanTarget(target)` / `Target(target)`
  - `onActivateSlotItemFunc` / `onToggleSlotItemFunc` / `onThisTargetItemFunc`
- 结论：**分析仪、工具、书、彩票、基因扫描仪、部分机器都可以先走一套通用交互层**，不必每个物品写死。

## 1. 通用物品交互层（P6 地基）

### 工具

| 工具 | 作用 |
|---|---|
| `list_interactions(item_id)` | 返回 `can_activate / can_toggle / can_target` 和可用目标提示 |
| `activate_item(item_id)` | 调用 `ActivateSlotItem()`（书、彩票、扫描仪等） |
| `toggle_item(item_id)` | 调用 `ToggleSlotItem()`（基因扫描仪、部分机器开关） |
| `use_tool(tool_id, target_id)` | `CanTarget` → `Target`（螺丝刀、焊枪、剪线钳、UV、菜刀等） |

### 约束

- 只对 `GeneralHelper.IsItemOwned` 的物品执行。
- `Target` 可能消耗工具/目标，执行前记录物品快照，失败/异常回滚不了的部分要明确返回 `partial`。
- 隐藏字段（鉴定真伪、基因隐藏值）不读；只返回游戏 UI 本来就展示的内容。

### TDD

- 纯逻辑：`InteractionCatalog`（选择 activate/toggle/target、优先级）。
- 适配器：`GameItem` 四个 hook 的契约检查。
- 真机：螺丝刀修 `system_module_ruined`、UV 照水、基因扫描仪开关。

## 2. 分析仪（P6）

| 分析仪 | 游戏入口 | 结构化结果 |
|---|---|---|
| 水质检测试纸 / AquaScan | `WaterScannerUIManager.Open(scanner, water)`；`GetTestedString` / `GetLabeledString` / `CanConfront` / `CanMatch` | 测试值、标签值、污染物、是否可对质/贴标 |
| 化学品扫描仪 | `ChemScannerUIManager.Open`；`CanConfrontChem` / `CanMatchChem`；TMP 字段 | 成分、毒性、欣快、总容量 |
| 植物分析仪 | `PlantAnalyserUIManager.OpenUI(seed)`；`Refresh`；TMP 字段 / `GetContaminantSentitivity` | 营养范围、耗水、污染敏感度、生长评分 |
| 基因扫描仪 | `gene_scanner` 的 `onToggleSlotItemFunc` → `PlayerStore.isGeneScannerScanning` | 开关状态；基因信息仍只给文本可见部分 |

### 工具

- `list_analyzers()`：拥有的分析仪 + 可用目标类型。
- `analyze_item(analyzer_id, target_id)`：统一返回 `{analyzer, target, fields, warnings}`。
- `read_analyzer(analyzer_id)`：读当前 UI 结果（UI 已打开时）。

### 实现策略

- 优先走 `use_tool` 打开游戏 UI，再读 `currentItem/currentWaterContainer/currentScannedItem` + 公开静态 getter。
- 没有静态 getter 的（植物分析仪）读 TMP 文本，解析成字段；解析失败返回原文，不猜。
- 不做视觉：不读 sprite、不读颜色、不读隐藏真伪。

### TDD

- `AnalyzerResult` 模型 + JSON 序列化测试。
- 文本解析器（植物分析仪）纯测试：正常/缺字段/本地化文本。
- 真机：水质试纸测一瓶水；植物分析仪测种子；化扫测药品。

## 3. 标签打印机 / 标记笔（P6）

### 现状

- 0.46D 里**没有找到独立“标记笔”物品**；标签编辑来自 `labeler` / `labeler_blue`（`LabelerUIManager`）。
- 可编辑的是 `ItemFeature` 的 `fakeCondition`，选项来自 `ItemConditionList.conditionLookup[category]`。
- `LabelerHelper.IsEditableLabelFeature`、`GetEditableLabelCount`、`CanEditLabel` 是公开判断。

### 工具

- `list_labels(item_id)`：返回可编辑类别、当前条件、可选条件（id + 显示值）。
- `set_label(item_id, category, condition_id)`：设置 `fakeCondition`，`SetExposed(false)`，调用 `WaterFeatureHelper.OnWaterLabelUpdated`。
- `repeat_label(item_id)`：复用 `LabelerUIManager.Repeat()` 的上一次标签。
- `list_labelers()`：拥有的标签机。

### 约束

- 只改“可编辑标签”，不碰真实成分。
- 改标签会影响交易/安检，动作要幂等并记录历史。
- 蓝标签机（`labeler_blue`）与普通标签机的可编辑范围可能不同，按 `IsEditableLabelFeature` 实际判断。

### TDD

- `LabelEdit` 纯校验：类别不存在/条件不存在/不可编辑 → 错误码。
- 真机：给一瓶水贴“纯净水”标签，读回 `list_labels`。

## 4. 电话（P7）

### 游戏入口

- `StorePhoneClient.InitPhoneClientDict()`：号码 → 联系人。
- `PhoneUIManager.AutoCall(number)` / `StartCalling()` / `HandleCall()`。
- `StorePhoneClient.WillAnswerCall(number)`、`ShownInPhoneBook()`、`currentCooldown`、`dialedBefore`。
- 电话对话走 `DialogUIManager`，状态可复用现有 `dialogue`。

### 工具

- `list_contacts()`：号码、名字、类型、是否显示、冷却、是否打过。
- `call(number)`：`AutoCall`；返回是否接通/无人接听。
- `phone_state()`：当前通话对象、状态、是否在对话。
- `end_call()`：关闭电话 UI/结束对话。

### 约束

- 不猜测隐藏号码；只列 `ShownInPhoneBook` 或已解锁的联系人。
- 电话服务可能扣钱/触发剧情，动作前返回预览（号码、对象、已知费用）。

### TDD

- `PhoneDirectory` 纯过滤：未解锁/冷却/已拨。
- 真机：打 `8376`（Wild）或已知联系人，读对话。

## 5. 彩票（P7）

### 游戏入口

- `LotteryHelper`：`IsLotteryUnlocked()`、`GetCurrentEdition()`、`GenerateLotteryTicket()`、`CountMatches`、`GetPrizeForMatches`、`CanRedeemPrize` / `TryRedeemPrize`。
- 彩票物品：`lottery_ticket`（`LOTTERY_TICKET_TAG`、`LOTTERY_NUMBER_TAG`、`LOTTERY_EDITION_TAG`、`LOTTERY_WINNING_NUMBER_TAG`、`LOTTERY_MATCH_COUNT_TAG`）。
- 刮刮卡：`ScratcherUIManager`，需要手动刮开。

### 工具

- `lottery_status()`：是否解锁、当前期号、开奖日、奖级表。
- `list_tickets()`：号码、期号、匹配数、奖金、是否可兑。
- `inspect_ticket(item_id)`：号码/期号/匹配/奖金。
- `redeem_ticket(item_id)`：`TryRedeemPrize`。
- `scratch_card(item_id)`：只读当前刮卡状态；自动刮开需要模拟 UI，先不做或单独开关。

### 约束

- 不预测开奖号；`DrawWinningNumberWithLuck` 依赖玩家彩票和幸运，不能用来“算号”。
- 兑奖前确认 `CanRedeemPrize`；兑奖会改现金/物品，记录历史。

### TDD

- `LotteryMath`：奖级表、匹配数、奖金、期号显示。
- 真机：买/捡一张票，读号码；到期兑奖（若测试档有）。

## 6. 机器（P8）

### 通用模型

- 枚举：`EmporiumEntry.GetAllOwnedItemsByTag("STANDARD_MACHINE_TAG")`。
- 状态：`MachineProgressHelper.GetMachineState(machine)`、`MACHINE_PROGRESS_CURRENT/TARGET`、`STATE_WORKING/READY`。
- 槽位：`MachineHelper.GetSlot(machine, i)`、`GetBodyStorage`、`GetBatterySlot`、`GetModuleInv`、`GetModules`。
- 电力：`MachineHelper.HasBattery/CanPower`。
- 启动：`MachineProgressHelper.StartProgressTypeMachine(machine, sourceItem)`；部分机器用按钮/`ActivateSlotItem`/`ToggleSlotItem`。

### 工具

- `list_machines()`：id、名称、类型、state、进度、电力、槽位摘要。
- `machine_status(machine_id)`：详细槽位/内容/模块/电池/产物。
- `machine_insert(machine_id, slot, item_id)`：把物品移入指定槽。
- `machine_start(machine_id)` / `machine_stop(machine_id)` / `machine_collect(machine_id)`。
- `machine_slots(machine_id)`：槽位 id/类型/容量。

### 分机器适配

- 熔炉（`MachineFurnace`）：垃圾处理、模块。
- 水培（`MachineHydroponic`）：水槽/种子/产出、`GetGrowStatus`。
- 打印机（`MachinePrinter`）：塑料/图纸。
- 净化器（`MachinePurifier`）：水罐/滤芯。
- 酿酒架（`MachineWineRack`）：陈酿、`GetAgingDays`。
- 搅拌机/化学（`MachineBlender`）：配方/损失。
- DNA 操纵器/解序器：后期。

### TDD

- `MachineState` 模型 + 槽位解析测试。
- 真机：水培放种子+水，读 `GetGrowStatus`；熔炉放垃圾，启动，收产物。

## 7. 酿酒（P8，独立小系统）

### 游戏入口

- `WineHelper.TryStartFermentation(item)` / `StartFermentation` / `FinishFermentation`。
- `WineHelper.IsFermentingOrFinished` / `IsFinishedWine` / `GetWineQualityTier` / `GetWaterQualityScore` / `GetWineExtractPerMille` / `GetWineContaminantPerMille`。
- `WineHelper.OnAgeWine`（酒架每日陈酿）。
- `MachineWineRack.CanAgeWine` / `GetAgingDays`。

### 工具

- `list_wine()`：瓶子、发酵状态、品质、提取率、污染物、陈酿天数。
- `start_fermentation(item_id)` / `wine_status(item_id)` / `age_wine(item_id)`。
- `pour_wine(source_id, target_id)`。

### 约束

- 发酵/陈酿会跨天；工具只触发，结果靠状态读回。
- 不暴露隐藏配方；只显示游戏 UI 可见数值。

## 8. 你列表之外的遗漏（建议排期）

| 系统 | 入口 | 价值 | 建议 |
|---|---|---|---|
| 以物易物 | `BarterOffer` / `ExchangeBarter` / `BarterHelper` | 高 | P9 |
| 顾客需求/悬赏 | `StoreRequestList` / `WantedUIManager` | 高 | P9 |
| 声望/派系/Perk | `StoreReputation` | 高 | P9 |
| 违禁品/安检/突袭 | `ContrabandHelper` / `InspectionUIManager` / `RaidManager` | 高 | P9 |
| 店铺升级/网络 | `NetworkUpgradeList` | 中 | P9 |
| 健康/伤口/成瘾 | `HealthData` | 中 | P9 |
| 远征/雇佣兵 | `ExpeditionHelper` / `ExpeditionLocationList` | 中 | P10 |
| 水培/养殖/宠物 | `HydroponicHelper` / `AquacultureHelper` / `HusbandryHelper` | 中 | P10 |
| 营销/招牌 | `sign_*` 物品 | 低 | P10 |
| 保险/贷款/赎回 | `Repo` / foreclosure | 低 | P10 |
| 事件/天气/节日 | `StoreEventManager` | 低 | P10 |
| 机器模块/维修 | `MachineHelper` / `MachineBrokenHelper` | 中 | P8 顺带 |

## 9. 分期与验收

### P6：通用交互 + 分析仪 + 标签（优先）

- `list_interactions` / `activate_item` / `toggle_item` / `use_tool`
- `list_analyzers` / `analyze_item`
- `list_labels` / `set_label` / `repeat_label`
- 验收：真机修一个坏模块、测一瓶水、贴一次标签

### P7：电话 + 彩票

- `list_contacts` / `call` / `phone_state`
- `lottery_status` / `list_tickets` / `inspect_ticket` / `redeem_ticket`
- 验收：打一次电话、读一张票、兑一次奖（若到期）

### P8：机器 + 酿酒

- `list_machines` / `machine_status` / `machine_insert/start/collect`
- `list_wine` / `start_fermentation` / `wine_status`
- 验收：水培种一株、熔炉处理一次、酒瓶发酵/陈酿

### P9：经济/社交/风险

- barter、wanted、reputation、contraband/inspection、upgrades

### P10：世界/农场/远征

- expedition、hydroponic/aquaculture、health、marketing

## 10. TDD 与真机约定

- 每个阶段先写失败测试：纯模型（无 Unity）+ 适配器契约。
- 真机统一 **槽 17**，不 `new_game`。
- 每个动作返回 `{ok, data, warnings, partial}`；失败给明确错误码。
- 所有动作写入 `ActionHistory`，便于回滚/复盘。
- 扫描器约束：不加 Thread/Task/协程；UI 读取只在主线程。

## 11. 风险

1. 分析仪结果多为本地化 UI 文本，解析可能随语言/版本变；需要原文兜底。
2. 标签编辑会改变交易/安检结果，必须幂等 + 历史。
3. 机器启动/收取可能有动画/延迟，动作后要轮询状态，不假设立即完成。
4. 彩票/电话有随机性和剧情分支，不能承诺固定收益。
5. `GameItem.Target` 可能消耗物品，失败回滚不完整；需要逐工具验收。
