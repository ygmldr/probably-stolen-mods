# PawnMind S5：首周经营、对话选择门禁与首次付租回执（离线证据）

**边界**：本轮没有启动游戏、没有打开菜单、没有调用真实 `/action`、没有创建/读取/写入/复制/删除任何真实存档，也没有改动 Steam、安装、`manifest.xml`、`dist/`、构建脚本或 Python。所有验证均为纯逻辑 / fake 端口 / 已安装 `Assembly-CSharp.dll` 的只读契约。**S5 的“代码完成”不等于真机已付首周租**；真实房东对话、禁用态、扣款与跨日自动保存时序仍留给 S8。

## 交付

| 层 | 内容 |
|---|---|
| Core 对话 | `DialogueChoicePolicy`：先判可读/有对话/有选项/索引/`Enabled`，再决定是否允许调用原生；`Changed` 只认身份、说话人、文本、结束态、选项文本/可用性的可观察变化。状态码：`no_dialogue`、`no_choice`、`bad_index`、`choice_disabled`、`choice_not_applied`、`dialogue_unavailable`、`outcome_unknown`。 |
| Core 租金 | `RentSnapshot`、`RentReceipt`、`RentReceiptTracker`：第一次可读快照只作基线；之后 `RunStats.rentPaid`/`rentWeeksPaid` 增加才生成一张回执；计数减少、`day` 回退或 `RunId` 改变视为读档/新局并重置，不生成回执；周数增加但金额不可读时不猜金额，返回 `receipt_unavailable`。 |
| Core 跨日 | `DayCycleRules`：回读 `day`、`rent_days_left`、`cash`；不可读返回 `readback_unavailable`，`day` 回退、租期越界（<0 或 >7）或现金为负返回 `readback_regression`。不要求 `day` 必须立即增加（原生 `sleep` 是异步黑屏序列）。 |
| Bridge 服务 | `FirstWeekService`：`choose_dialogue`（先门禁→一次原生 `SelectChoice`→回读→成功/失败/未知）、`end_day`（读前态→一次原生 `EndDay`→读后态→回读校验）、`RentState`（`/state` 与动作共用同一个回执 tracker，避免同一笔重复计数）。受控 `load_game`/`load_recording_game`/`confirm_new_game` 成功后调用 `ResetRentReceipts()`。 |
| Bridge JSON | `FirstWeekJson`：`dialogue`（含 `readable`/`error`、每项 `enabled`、可见文本/物品）、`rent`（`due`、`days_left`、`days_until_rent`、`can_pay`、`rent_paid_total`、`rent_weeks_paid`、`rent_weeks_missed`、`starting_rent`、`cash`、`day`、`last_payment`、`receipt_error`）、`readback`。不输出对话身份句柄、隐藏条件函数、鉴定答案或存档身份。 |
| Game 适配 | [GameState.cs](<../PawnMind/src/Game/GameState.cs>) 新增只读 `ReadDialogueView()`、`ReadRentSnapshot()`、`ReadShopDaySnapshot()` 与单次原生 `SelectDialogueChoice()`/`EndDay()`；[FirstWeekAdapter.cs](<../PawnMind/src/Game/FirstWeekAdapter.cs>) 只做端口转发。`RecordingSaveAdapter.Read()` 增加 `day`/`rentDaysLeft`/`cash`/`DayCycleReadable` 供 `sleep` 回读。 |
| State | `/state` 新增 `rent` 与 `dialogue.choices[].enabled`；`legal_actions` 不再为禁用选项生成 `choose_dialogue`。非游戏态 `rent=null`。 |
| 契约 | AdapterTests 新增 8 项：`DialogUIManager.Instance`、`SelectChoice`、`IsChoice`、`IsCurrentDialogEnd`、`IsLineStillDisplaying`、`Dialogue.title`、`Dialogue.IsChoiceEnabledAt`、`PlayerStore.CanEndDay`。既有 `RunStats.rentPaid/rentWeeksPaid/rentWeeksMissed`、`PlayerStore.CanPayRent/OnRentPaid/OnRentNotPaid/GetRentValue/GetRentDayCounter/RentIncrease/dayUntilRent/rentValue/temporaryRent/startingRent`、`StoreClientList.CreateLandlordClient` 复用。 |

## Rev13 关键结论（决定门禁与回执语义）

- `DialogUIManager.DisplayChoice()` 调用 `currentDialog.IsChoiceEnabledAt(i)` 设置按钮 `SetInteractable`；`SelectChoice()` 自己也会再次检查 `IsChoiceEnabledAt`，禁用/越界时静默忽略。S5 因此在调用前用同一 API 判 `choice_disabled`/`bad_index`，再回读对话是否真的变化，**不再把“调用过原生”当成成功**。
- `StoreClientList.CreateLandlordClient()` 的付租选项通过 `AddChoice(..., choiceEnabled: () => PlayerStore.Instance.CanPayRent())` 绑定 `isDisabledFunc`，所以 `IsChoiceEnabledAt` 与原生按钮的可用性一致；“资金不足”在 UI 上就是该选项 disabled。
- `PlayerStore.OnRentPaid()`：`dayUntilRent=7`、扣除 `rentValue+temporaryRent`、`RunStats.OnRentPaid(amount, cashAfter)`、`RentIncrease()`、`temporaryRent=0`。`OnRentNotPaid()`：`dayUntilRent=7`、累计 `temporaryRent`、`RunStats.OnRentMissed()`、`RentIncrease()`。`RunStats.OnRentPaid` 累加 `rentPaid` 并 `rentWeeksPaid++`；`OnRentMissed` 只累加 `rentWeeksMissed`。
- 付租回执的 `amount` 取 `RunStats.rentPaid` 的前后差，`cash_before/after` 取前后快照；`rent_days_left_before/after` 取 `PlayerStore.dayUntilRent`（与既有 `state.rent_days_left` 一致）。`state.rent.days_left` 另给原生 `GetRentDayCounter()` 值，两者在跨日/付租瞬间可能短暂不同，S8 需按游戏 UI 核对显示口径。
- `EndOfDayUIManager.OnEndDayButtonClick()` 启动 0.5s 黑屏协程后才 `BeginDay()`/`SaveGame()`；因此 `sleep` 的立即回读通常仍是旧 `day`。S5 只保证“可读且不回退”，不把“day 已增加”当成功条件；真实跨日完成与保存时序留给 S8。
- `PlayerStore.EndDay()` 只关店并更新服务费用，不推进 `day`；`end_day` 回读按“不回退”判定。`StoreClientManager` 的房东到访条件是 `GetRentDayCounter()==0`，不是 `dayUntilRent`。

## 强制错误码与语义

| 场景 | 结果 |
|---|---|
| 无对话 UI / 当前无对话 | `no_dialogue`，不调用原生 |
| 有对话但无选项 | `no_choice`，不调用原生 |
| index < 0 或 ≥ 选项数 | `bad_index`，不调用原生 |
| 选项原生 `enabled=false` | `choice_disabled`，不调用原生 |
| 原生调用后对话身份/文本/结束态/选项未变 | `choice_not_applied`，`ok:false`、`dispatched:true`，不报成功 |
| 原生调用抛异常或回读不可读 | `outcome_unknown`，`ok:false`、`unknown:true`、`dispatched:true`，不重试 |
| `end_day` 前态不可读 | `readback_unavailable`，不调用原生 |
| `end_day` 后态不可读/`day` 回退/租期越界/现金为负 | `outcome_unknown`，附 `readback` |
| `sleep` 前态 `day`/`rent_days_left`/`cash` 不可读 | `recording_readback_unavailable`，不调用原生 |
| `sleep` 后态不可读或回退 | `recording_outcome_unknown` 或录制策略的 `recording_readback_unavailable`，不报成功 |
| 租金不可读 | `rent_unavailable` + `receipt_error=receipt_unavailable`，不生成回执 |
| 周数增加但金额不可读 | `receipt_error=receipt_unavailable`，不生成金额 |
| 读档/新局/`RunId` 改变/计数减少 | 重置基线，不把旧付款当新回执 |

## 回执一次性规则

1. 每个 Mod 进程只共享一个 `RentReceiptTracker`（`FirstWeekService.Current`），`/state` 与 `choose_dialogue` 共用，避免同笔付款被状态轮询重复计数。
2. 第一次可读快照只建立基线；基线之前已存在的 `rentWeeksPaid` 不补发回执（无法证明付款时点）。
3. 只有 `rentPaid` 或 `rentWeeksPaid` 相对上次可读快照增加时才生成新回执；再次读取同一快照只返回 `last_payment`，不生成第二张。
4. 读档、新局、`RunId` 变化或任一计数减少会重置基线与 `last_payment`；受控读档/新局入口在成功后显式 `ResetRentReceipts()`。
5. 若一次观察到跨多周，回执标 `detection_complete=false` 并保留 `weeks_paid_delta`，不伪造逐周拆分。

## 测试证据

- **Red A**：先加 `FirstWeekCoreTests` / `FirstWeekServiceTests` / `FirstWeekJsonTests` / `FirstWeekSleepTests` 与 `LegalActionTests.Disabled_Choices_Are_Not_Offered`，`REQUIRE_GAME=1 ./test.sh` 编译失败，确认缺少 `IDialoguePort`、`DialogueChoiceView`、`RentSnapshot`、`FirstWeekService` 等 S5 类型。
- **Red B（实现过程中）**：Core/Bridge 初次实现后 `423 pass / 5 fail`；失败为测试辅助数据 speaker 取值、JToken 空值断言，以及 `sleep` 后态不可读时录制策略返回 `recording_readback_unavailable` 而非测试预设的 `recording_outcome_unknown`。修正断言口径后转绿；未放宽生产门禁。
- **Red C（真实回归）**：加入 `RunId` 重置逻辑时，首次观察即解引用空基线导致 `NullReferenceException`；`Run_Identity_Change_Resets_Without_Receipt` / `Reset_Rent_Receipts_Forgets_The_Last_Receipt` 先红后修 `last != null` 转绿。
- **Green（主代理可复跑）**：
  - `REQUIRE_GAME=1 ./test.sh`：C# **435**、脚本门禁 **53**、适配器契约 **392**，`adapter contract OK`，`strict mode: REQUIRE_GAME=1`，无 SKIP。
  - `dotnet build src/PawnMind.csproj -c Release --no-incremental`：**0 警告 / 0 错误**。
  - Python 未改动；`agent/tests` 现为 **29** 项通过（不是 S5 交付内容）。

覆盖：无对话/无选项/负索引/越界/禁用不调用原生；合法但对话未变→失败；合法成功回读；租金支付回执字段与只记一次；状态轮询检测回执不重复；资金不足 `can_pay=false` 与禁用选项；读档/`RunId` 变更重置；`sleep` 前后 `day`/`rent_days_left`/`cash` 与回退/不可读；`end_day` 前后回读与回退；`confirm_trade`/`bargain` 失败经路由器仍 `ok:false`；JSON 不含对话身份、隐藏条件、鉴定答案、存档身份。

## S8 必须真机确认（离线无法证明）

1. **真实房东对话**：`CreateLandlordClient` 的付租选项在 Rev13 实际 UI 中的 index、文本与 `IsChoiceEnabledAt` 是否与状态快照完全一致；本地化文本变化不影响 index，但 S8 要以画面为准。
2. **禁用态**：现金不足时游戏原生按钮确为 disabled；桥必须返回 `choice_disabled` 且不产生任何扣款/对话推进。
3. **扣款与回执金额**：`runStats.rentPaid` 增量、现金前后、`dayUntilRent` 7 重置与游戏画面扣款一致；`due` 为付款前的 `rentValue+temporaryRent`，付款后 `RentIncrease` 会立即抬高下一期租金，回执只应记录本次金额。
4. **跨日自动保存时序**：`sleep` 的 0.5s 黑屏协程完成后 `day` 才增加、`SaveGame` 才写盘；S8 必须在真实 UI 上确认最终 `day`/`cash`/`rent_days_left` 与存档槽一致。当前 `sleep` 响应可能显示 `day_advanced=false`（请求已受理但尚未跨日），不应被误判为失败，也不能当作已跨日证据。
5. **`days_left` 口径**：`state.rent.days_left`（`GetRentDayCounter` 日历值）与 `state.rent.days_until_rent`/顶层 `rent_days_left`（`dayUntilRent`）在付款瞬间可能不同；S8 需对照日历/账单 UI 确认 agent 应使用哪一个作为“还剩几天”的主口径。
6. **手动读档**：只有通过桥的受控 `load_game`/`load_recording_game` 才会显式重置回执基线；若用户在游戏 UI 手动读档，代码只能靠 `RunId`/计数回退识别。S8 不应在录制中手动读档；若必须，先停机并由人确认。
7. **隐藏字段**：本轮从 `state.client` 移除了 `stealth_wanted`（计划 D.5 明确不应暴露）。S8 需确认现有 MCP/宿主没有依赖该字段；`is_wanted`/`wanted_poster_id` 等仍按既有可见信息保留。
8. **真实 `Dialogue` 身份回读**：适配器用运行时对象哈希 + 文本/选项快照判变化；若某版本产生“同一文本重入”的对话，S8 需确认桥不会把真实成功误判为 `choice_not_applied`（或反之）。
9. **失败重试**：`outcome_unknown` 后不得用新 `action_id` 重试扣款/存档；S8 需在真实 HTTP 客户端上验证调用层遵守。

## 未触碰

未启动游戏、未安装/覆盖 Mod、未写 `dist/`、未改 manifest/构建脚本/Python/Steam/云同步、未读写任何真实存档、未生成 `_decomp/` 产物。
