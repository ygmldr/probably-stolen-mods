# NIGHTLY_SNAPSHOT_CALLGRAPH — 快照读路径调用链与纯度分类

> 模块：B（S1 / D0a 快照纯度）。日期：2026-11-17。
> 结论先行：**已知会触发 `BargainUIManager.Refresh()` 的 `client.can_report` 已从快照删除**；
> 快照源码不再出现任何禁用调用（`CanReport(` / `CanOffer*(` / `Refresh(` / `RNG.Roll` / `ClearTradeRepMultiplierOverrides(`）。
> 仍有 **UNKNOWN** 节点（未逐个证明纯），不得当成 pure；Unity 运行时 `Refresh()` 计数仍为 **NOT_RUN**。

## 0. 证据等级

| 标记 | 含义 | 本批证据 |
|---|---|---|
| `pure` | 读路径已核对到原生字段/纯计算，无已知副作用 | 源码 + `_decomp/` 行号 |
| `unknown` | 调用链未完整核对，或依赖游戏注入的谓词闭包 | 列出待插桩/待核对项 |
| `impure` | 已确认会改游戏/UI 状态 | 只允许出现在写路径，禁止出现在快照 |
| `static` | 由 `SnapshotPurityTests`（L0-static）强制 | 新增未登记调用即测试失败 |
| `fake` | 由 `SnapshotPurityTests`（L1-fake）强制 | `ClientSnapshotProjection` 十次构建 0 次 impure 探针 |
| `real` | 需要游戏进程 | **一律 NOT_RUN** |

`_decomp/` 是本地反编译参考（已 gitignore），行号对应 2026-11-09 的 dump。

## 1. 入口与调用位置

`StateSnapshot.BuildJson()`（`src/Bridge/StateSnapshot.cs:18`）是唯一的快照构建入口，调用位置：

| 调用点 | 触发 | 说明 |
|---|---|---|
| `GET /state` → `PawnMindMod` state provider | 裸 HTTP 读 | `EndpointPolicy`（Bootstrap/Play/Locked）在 provider 之前拒绝 |
| `GET /events` 长轮询 | rev 变化/超时 | 同一构建函数 |
| `ActionRouter.Handle` → `_refreshRevision`（`ActionRouter.cs:480`） | 每个写动作派发前/后 | `stale_rev` 也走这里（拒绝发生在派发前） |
| `ActionRouter.MarkResponseUndelivered` → `CurrentRevision()` | 传输失败 | 只读缓存计数器，不构建快照 |
| `Diagnostics.Pulse/DumpState`（F7） | 游戏内调试 | 同一构建函数 |
| MCP 六别名 | 见 §2 | 都走 `GET /state` |

## 2. 六个 MCP 快照别名（`PolicyCatalog.SnapshotReaders`）

`get_state`、`get_run_summary`、`get_legal_actions`、`wait_for_change`、`wait_for_events`、`wait_until`
——全部经 `client.get_state()` / 长轮询 `client.wait_events()`，与裸 HTTP `/state`、`/events` 共用
`StateSnapshot.BuildJson()`。`SnapshotPurityTests.PP_READ_04` 断言该名单与生成物
`PolicyCatalog.SnapshotReaders` 一致；`agent/tests/test_tool_policy.py` 另从 `mcp_server.py`
源码扫描断言没有未登记的别名。

## 3. BuildState / BuildJson 顶层字段

`BuildJson`：`state[rev]`（`RevisionTracker`，纯计数）、`state[timestamp]`（`DateTime.UtcNow`）。

`BuildState`（`StateSnapshot.cs:31`）逐项：

| 顶层 key | 调用链 | 分类 | 证据 |
|---|---|---|---|
| `in_game` | `GameState.IsInGame` → `PlayerStore.HasInstance` | pure | `_decomp/PlayerStore.cs` HasInstance 只判断单例存在；注释明确 `Instance` 会创建空 store，故不用 |
| `legal_actions` | `LegalActionBuilder.Build` → `Available()` | pure | Core 投影；**B2 后只含 `availability=available`** |
| `candidate_actions` | `LegalActionBuilder.Build` → `Candidates()` | pure | 新增；bargain 四向 `availability=unknown`、`requires=native_guard` |
| `prompt` | `PromptJson.ForState(PromptPolicy.ReadSafe(PromptAdapter.Instance))` | **unknown** | 适配器读原生弹窗字段；本批未逐字段核对 |
| `report` | `ReportService.State()` → `DayReport` 适配器 | **unknown** | `ReportAdapter` 读原生日报；未逐字段核对 |
| `calendar_next` | `CalendarService.StateNext()` → 日历适配器 | **unknown** | 同上 |
| `tutorial_hints` | `TutorialAdapter.ReadTutorial()` | **unknown** | 同上 |
| `day` | `GameState.GetDay` → `StoreStation.Instance.dayCounter` | pure | `_decomp/StoreStation.cs` 字段 |
| `shop_state` | `GetShopState` → `PlayerStore.storeState.ToString()` | pure | 枚举字段 |
| `outside` | `IsOutside` → `MapUIManager.Instance.IsOutside` → `Current != null` | pure | `_decomp/MapUIManager.cs:54` |
| `location` | `CurrentLocationId` → `MapUIManager.Instance.Current.Id` | pure | `_decomp/MapUIManager.cs:52-54` |
| `afterhour_count` | `EmporiumEntry.Instance.afterhourInventory.childItems.Count` | pure | 列表计数 |
| `cash` | `GetCash` → `PlayerStore.playerCash` | pure | `_decomp/PlayerStore.cs:2516` |
| `rent_days_left` | `PlayerStore.dayUntilRent` | pure | 字段 |
| `rent_value` | `PlayerStore.rentValue` | pure | 字段 |
| `starting_day` | `PlayerStore.startingDay` | pure | 字段 |
| `client_arrived` | `PlayerStore.isClientArrived` | pure | 字段 |
| `waiting_customers` | `storeClientManager.clientStack.Count` | pure | 列表计数 |
| `back_counter_count` / `back_counter` | `GetBackCounterItems` + `DescribeItems` | count pure / list **unknown** | `value` 走 `item.GetNegociatedValue()`，见 §5 |
| `front_counter_count` / `front_counter` | `GetFrontCounterItems` + `DescribeItems` | 同上 | 同上 |
| `client_item_count` / `client_items` | `GetClientItems`（反射 `StoreClientInstance.gridInv`，只读）+ `DescribeItems` | count pure / list **unknown** | 同上 |
| `negotiation_open` | `NegociationUIManager.Instance.IsOpen` → `uiObject.activeSelf` | pure | `_decomp/NegociationUIManager.cs:79-87` |
| `bargain` | `GetBargainSnapshot` → `BargainUIManager.Instance.IsOpen` → `uiObject.activeSelf` | pure | `_decomp/BargainUIManager.cs:165-173`；**只暴露 `open`** |
| `inventory_count` / `inventory` | `GetInventoryItems` + `DescribeItems` | count pure / list **unknown** | 同 item value 链 |
| `can_call_next` | `GetCanNextClient` → `PlayerStore.CanNextClient()` | pure | `_decomp/PlayerStore.cs:1347-1365`：list count + bool 字段 |
| `can_end_day` | `GetCanEndDay` → `PlayerStore.CanEndDay()` | pure | `_decomp/PlayerStore.cs:1816-1830`：dismissable 布尔 |
| `dialogue` | `FirstWeekJson.Dialogue(GameState.ReadDialogueView())` | **unknown** | `GetCurrentDialog/IsCurrentDialogEnd/IsChoice/IsLineStillDisplaying` 是字段/比较（pure），但 `Dialogue.IsChoiceEnabledAt` → `Dialogue.IsDisabled()` 执行游戏注入的 `isDisabledFunc/isHiddenFunc` 闭包（`_decomp/Dialogue.cs:263-274`），无法离线证明 |
| `rent` | `FirstWeekService.RentState` / `FirstWeekJson.Rent(ReadRentSnapshot())` | pure | `_decomp/PlayerStore.cs:1470`(CanPayRent 纯比较)、`:1509`(GetRentDayCounter)、`:1599`(GetRentValue)；runStats 字段 |
| `client` | `ClientSnapshotProjection.Describe(StoreClientSnapshot)` | pure | §4 |
| `negotiated_item` | `GetNegotiatedItem` → `PlayerStore.currentNegociatedItem` | pure | `_decomp/PlayerStore.cs:3151` 字段；`DescribeItem.value` 见 §5 |
| `run_summary` | `BuildRunSummary` 复制已投影字段 | pure | 无新读 |
| `rev` | `RevisionTracker.CurrentRevision` | pure | 进程内计数 |
| `timestamp` | `DateTime.UtcNow` | pure | 时钟 |

## 4. client 段（B 的核心修复）

**修复前**（`StateSnapshot.cs` 旧 300/308 行）：

```
DescribeClient(client)
  → client.CanReport()                              src/Bridge/StateSnapshot.cs:310
    → BargainUIManager.Instance.IsCurrentClientCoerced()   _decomp/StoreClient.cs:1270
      → Refresh()                                   _decomp/BargainUIManager.cs:635-637
        → trackedClient != client 时重置 ~15 个谈判位 + ClearTradeRepMultiplierOverrides()
                                                    _decomp/BargainUIManager.cs:1103-1126
```

**修复后**：`DescribeClient` 只构造 `StoreClientSnapshot`（实现 `IClientSnapshotSource`），
由 `ClientSnapshotProjection.Describe`（Core）投影 11 个纯字段：

| key | 原生读取 | 分类 |
|---|---|---|
| `name` | `StoreClient.displayName` 字段 | pure |
| `intent` | `StoreClient.clientIntent.ToString()` | pure |
| `uses_budget` | `StoreClient.useClientBudget` | pure |
| `impatience` | `StoreClient.clientImpatience` | pure |
| `is_wanted` | `StoreClient.isWanted` | pure |
| `report_reward` | `StoreClient.reportReward` | pure |
| `reported` | `StoreClient.reported` | pure |
| `wanted_poster_id` | `StoreClient.wantedPosterId` | pure |
| `bargain_available` | `StoreClient.isBargainAvailable` | pure |
| `faction` | `StoreClient.clientFaction` | pure |
| `budget` | `StoreClient.GetBudget()` → `return clientBudget;`（`_decomp/StoreClient.cs:1017`） | pure |

`can_report` **已删除**：判断"能否举报"必须走写动作 `report_client` 的原生 guard，
读路径不再代劳。`reported` / `is_wanted` 保留为纯字段。

禁止列表（`SnapshotFieldMap.ForbiddenTokens`，注释行先被剥离，避免注释误报）：
`CanReport(`、`CanOfferDiscount(`、`CanOfferMarkup(`、`CanOfferBuyingDiscount(`、
`CanOfferBuyingMarkup(`、`Refresh(`、`RNG.Roll`、`ClearTradeRepMultiplierOverrides(`、
`GetBargainSuccessChance(`、`GetBargainLeaveChance(`。

## 5. item 投影（UNKNOWN 项）

`DescribeItems`（`StateSnapshot.cs:267`）逐项读：

| key | 调用链 | 分类 | 说明 |
|---|---|---|---|
| `id` | `GameItem.GetUniqueID` | pure | id 字段 |
| `name` | `GameItem.name` | pure | 字段 |
| `value` | `GetNegociatedValue()` → `GetComplexeItemValue` / `AccumulateFeatureStages` / `GetItemMiscValue` / `WaterHelper.GetContainerPrice` | **unknown** | `_decomp/GameItem.cs:1740-1754`：**当 `DebugPanel.priceDebugPanel.activeSelf` 为真时会写 debug UI 文本**（条件性 UI 副作用，非游戏状态；无 RNG）。正常游玩 debug 面板关闭，但不能离线证明"永远不写"，故记 unknown |
| `owned` | `GameState.IsOwned` → `GeneralHelper.IsItemOwned` → `IsTag("IS_OWNED_TAG")` | pure | `_decomp/GeneralHelper.cs:200` |
| `sellable` | `GameState.CanSellItem` → `PlayerStore.CanSellThisItem(item, true)` | **unknown** | `_decomp/PlayerStore.cs:2610+` 调用 client blueprint 谓词，内部有 `Debug.Log`；未发现 `Refresh()`/`RNG.Roll`，但未穷尽 |

`DescribeItem`（协商物品）：`name` / `identifier` / `count` pure，`value` →
`GetCurrentValue`（读 `StoreStation.storeEventManager.GetActiveEvents()` + 计算）
分类 **unknown**（同 debug 面板条件写路径）。

> 结论：这些 UNKNOWN 不是"已证实的 bug"，但按任务书要求**不得默认 pure**；
> 若真机插桩发现副作用，先修 `GetNegociatedValue` 的 debug 写入或改为缓存值。

## 6. 快照构建之外的读取（不在本快照里，但共享 GameState 层）

| 入口 | 调用 | 分类 | 备注 |
|---|---|---|---|
| `bargain_preview` | `BargainUIManager.GetBargainSuccessChance()` → `Refresh()` | **impure（已知）** | `_decomp/BargainUIManager.cs:1258`；策略里 `bargain_preview` 为 deny（只读工具但会重置谈判） |
| `get_state` 旧字段 `can_*` / `can_report` | `CanOffer*` / `CanReport` → `Refresh()` | **impure（已从快照移除）** | 本模块修复 |
| `report_client` 写路径 | `StoreClient.CanReport()` | impure（允许） | 写动作 guard，`_decomp/StoreClient.cs:1270` |
| `get_clientele` 首次读取 | `RNG.Roll`（按天缓存） | **cached_impure** | 策略 `needs_evidence`，不在快照路径 |

## 7. 静态/动态测试映射

| 测试 | 等级 | 断言 |
|---|---|---|
| `SnapshotPurityTests.PP_READ_03_Forbidden_Calls_Absent_From_Snapshot_Build` | L0-static | `StateSnapshot.cs` / `GameState.cs` 无禁用调用 token |
| `PP_READ_03_Every_GameState_Call_Is_Registered` | L0-static | 源内 `GameState.*` 调用全部登记且非 impure |
| `PP_READ_03_Emitted_State_Keys_Are_Registered` | L0-static | `state["…"] =` 键全部登记 |
| `PP_READ_03_Client_Section_Is_Registered_And_Pure` | L0-static | client 段键全部登记为 pure，且无 `can_report` |
| `PP_READ_03_Client_Adapter_Only_Reads_Registered_Members` | L0-static | 适配器只读登记的 `StoreClient` 成员 |
| `PP_READ_03_RunSummary_And_LegalAction_Keys_Are_Registered` | L0-static | run_summary / legal_action 行键登记 |
| `PP_READ_03_Item_Keys_Are_Registered` | L0-static | item 投影键登记 |
| `PP_READ_03_Service_Calls_Are_Registered` | L0-static | Bridge/适配器调用登记 |
| `PP_READ_03_Snapshot_Emits_No_Impure_Key` | L0-static | 不得 emit `SnapshotRules.ImpureKeys` 中任何键（含 `can_report`） |
| `PP_READ_04_Snapshot_Reader_Aliases_Match_The_Policy_Catalog` | L0-static | 六别名与生成物一致 |
| `PP_READ_01_Fake_Ten_Client_Projections_Never_Probe_Impure` | L1-fake | 10 次投影 0 次 impure 探针 |
| `PP_READ_01_Fake_Projection_Omits_Can_Report_And_Reports_Pure_Fields` | L1-fake | 无 `can_report`，有 `reported`/`is_wanted` |
| `PP_READ_07_Fake_Client_Switch_Does_Not_Reset_Bargain_State` | L1-fake | 切换顾客 10 次读投影不触发 impure 探针 |
| `PP_READ_02_Stale_Rev_Does_Not_Dispatch_Or_Mutate` | L1-fake | stale_rev 被拒：dispatch=0、world mutation=0、快照构建=1 |
| `PP-READ-06`（Python） | L1-fake | 旧快照缺 `candidate_actions`/`can_report` 时 MCP 与 debug bundle 稳定降级 |

## 8. 仍为 NOT_RUN / UNKNOWN（需要真机或插桩）

1. **PP-READ-01-real**：Unity 运行时 `BargainUIManager.Refresh()` 真实调用计数。需要一次插桩构建
   （静态计数器 + `/health` 暴露 `refresh_calls`），连开 10 次 `/state` 看计数是否不变。
   当前代码只能证明"静态不可达"，不能证明运行时 0 次。
2. `prompt` / `report` / `calendar_next` / `tutorial_hints` 四个适配器读路径未逐字段核对（UNKNOWN）。
3. `GetNegociatedValue()` 在 debug 价格面板打开时的条件性 UI 写（UNKNOWN）。
4. `dialogue` 段 `Dialogue.IsDisabled()` 的游戏注入谓词闭包（UNKNOWN）。
5. `CanSellThisItem` 的完整谓词链（UNKNOWN；只确认有 `Debug.Log`，未见 Refresh/RNG）。
6. 顾客实例身份：`GetClient()` 在会话内是否恒返回同一 `StoreClient` 实例（影响 `Refresh()` 是否真重置），
   需真机日志 `trackedClient` 变化。

用户醒来后的最小验证步骤见 `docs/plans/NEXT_LIVE_SESSION.md`。
