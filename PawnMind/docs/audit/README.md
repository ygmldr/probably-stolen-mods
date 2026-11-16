# 玩家入口矩阵（PLAYER_ENTRY_MATRIX.csv）导读

- 数据源：`PawnMind/src/Bridge/*.cs`、`PawnMind/src/PawnMindMod.cs:249-773` 的 dispatch switch、`RecordingDispatchGuard.cs:82-118`、`../GUARD_AUDIT.md`（B1–B43 / C1–C15）、`_decomp/`（只核对类型名与方法名）。
- 快照：源码 `d0440ab`（HEAD `ab70004` 只加 docs）。GUARD_AUDIT 之后还有 `3d85963`（P0 修复）等提交，正文按**当前源码**写，冲突以源码为准。
- 读法：`guard_checks` 是**我们代码现在真的查的谓词**（`|` 分隔），不是审计建议；`implicit_deps` 写 `C<id>:<token>`；`defect_ids` 只放 B 编号（`read_impure` 行放 S 编号）。`status` 由 B 缺口与"桥缺一条显式门禁"的 C 规则共同决定；C10（笔窗口不同步）、C12（兑奖不可撤销）按审计判为可接受，不扣 status。

## 1. 互斥统计

| 口径 | 数量 |
|---|---|
| CSV 行数 | **62** = 58 `write` + 4 `read_impure` |
| 写 op 总数（dispatch 可见） | **58**（其中 `debug_grant`/`debug_fill_water`/`debug_unlock_expedition` 只在 HTTP，无 MCP 工具） |
| status（全表 62 行） | fixed **25** / open **33** / partial **2** / unverified **2** |
| status（58 写） | fixed **25** / open **30** / partial **2** / unverified **1** |
| `unverified=yes` | 5 行（见 §3） |

B 类缺口（43 条）按状态：

| 状态 | 数量 | 条目 |
|---|---|---|
| fixed | **10** | B1–B8、B20、B22 |
| open | **33** | B9–B19、B21、B23–B43 |
| partial | 0 | —— |

fixed 的 10 条 B 缺口全部落在提交 `3d85963 fix(pawnmind): close the P0 guard gaps found by the native audit`：

| defect_id | op | 当前证据 |
|---|---|---|
| B1 / B2 / B3 / B4 | open_shop | `GameActions.cs:40-68`（锁、逮捕、枪战、MORNING、CanStartDay，改走 `OnClick`） |
| B5 / B6 / B7 | end_day | `FirstWeekService.cs:164-182` + `GameState.cs:672-694`（`CloseShutter`/`EndAndClose`、OPEN、client_still_present） |
| B8 | sleep | `DayEndPolicy.EvaluateSleep` + `RecordingSaveService.cs:254`（not_in_store） |
| B20 / B22 | dismiss_current_client | `GameActions.cs:96-115`（`IsClientDimissable` + `TryPlayerDismissCurrentClient`） |

> `status=fixed` 是 **op 级**结论：25 个 fixed 里只有 2 个（open_shop、end_day）是"缺口被提交修掉"，另外 23 个是审计本来就判 A / 没给缺口、当前代码与原生一致。所以 op 级 fixed=25 ≠ B 缺口 fixed=10。

## 2. A / B / C 是多标签

审计共 80 条（A22 + B43 + C15），按 op 去重后：**A 命中 30 个 op、B 命中 23 个、C 命中 19 个**（B∪C 去重 33 个）。同一 op 可同时挂 A/B/C（例如 `confirm_trade` = A 入口 + B10–B16 + C2；`sleep` = A6 + B8/B9 + C4），所以 80 ≠ 58 是**标签数 vs op 数**的差异，不是漏了 22 个 op。

- **至少命中一个 B 缺口的 op 数 = 23**（占 58 个写 op 的 39.7%）。
- 只命中 C、没有 B 的 op 有 10 个：call_next_customer、bargain、close_windows、save_game、save_recording_game、set_pen_mode、redeem_ticket、arrest_wanted、dispatch_expedition、advance_expedition。
- B 和 C 都不命中的写 op 有 25 个：其中 22 个是审计判 A 的对齐项，另 3 个（activate_item / toggle_item / use_tool）是本次新发现的 `May*` 缺口（见 §4.5）。

## 3. `unverified=yes` 的 5 行：缺什么证据

| op | 为什么要证 | 需要什么 |
|---|---|---|
| OP-014 `dismiss_tutorial_hints`（status=unverified） | 0.46D 没有游戏内教程开关：`tutorialEnable=false` 全仓只有 `TutorialUIManager.OnSleep` 与 `PlayerStore.HandleSkipIntro` 两处赋值，玩家侧只有新游戏页的 skipIntro 勾选（`MainMenuUIController.OnSkipIntroToggled`） | 真机确认设置/主菜单有没有等价的"关提示"入口；没有就要在文档里写"本 op 无玩家对等入口" |
| OP-034 `machine_collect`（open） | 审计写的"机器收取按钮"不存在，实际是从机器仓储窗口拖出 | 逐机器确认哪些槽能拖出、`MachineProgressHelper.GetMachineState` 是否有独立"可收取"态 |
| OP-038 `analyze_item`（open） | B42 本身 UNVERIFIED：`Target()` 是否同步刷新扫描窗口未知 | 真机连扫两件不同物品，比对返回读数与 UI 文本 |
| OP-042 `set_label`（open） | 标签机窗口"应用"没有公开调用点（`fakeCondition` 只在 `LabelerUIManager.Repeat` 里赋值） | 真机贴一次标签，记录实际回调链与 labeler 持有判定 |
| OP-062 `barter_status`（unverified） | S5：3 个 `getBarterResult` delegate 现在纯读，但不保证将来不掷骰 | 读 `StoreClientListBarter` 里全部 delegate，确认无 RNG/写状态 |

## 4. 与 `../GUARD_AUDIT.md` 的差异（审计过时/说错的地方）

1. **§0/§2.1–§2.2/§9 的"当前行为"已过时**：文档把 `open_shop` 写成手拼 `OnShutterOpened()+OpenShutter()`、`end_day` 写成直调 `PlayerStore.EndDay()`、`sleep` 没有 outside 判定、`dismiss_current_client` 不查 `IsClientDimissable()` —— 这四条在同一次提交 `3d85963` 里已改，正文没回写，§9.1 前 10 名仍把它们当未修。另外 §2 全表的行号是修复前快照（`open_shop` 15-39 → 现 22-71，`move_item` 152-224 → 现 192-264，`confirm_trade` 416-461 → 现 456-501）；本表用当前行号。
2. **§2.6 `machine_collect` 的玩家路径写错**：0.46D 没有通用"机器收取按钮"；收取是机器仓储窗口拖出（`ItemMouseDragHandler.EndDrag`），个别机器另带按钮（如 `MachineChemFinisher` 的 output）。§2.3 的 `B33` 仍成立。
3. **§2.1 `call_next_customer` 的入口描述不准**：`StoreBell.RingBell`（`StoreBell.cs:125`）**不调用** `CanNextClient()`，它自己判 `lockNextBellFlag`/`storeState`/`IsClientPresent`，有人时改走 `TryPlayerDismissCurrentClient`；`CanNextClient` 在 `_decomp` 里没有外部调用点（只有 `PlayerStore` 内部）。所以 C1 的门禁应写 RingBell 的 `lockNextBellFlag` + `storeState`；B21 的 `bell_locked` 是同一字段，但只挂在 `dismiss_current_client` 上，`call_next_customer` 没有对应 B 行。
4. **§2.4 `dismiss_tutorial_hints` 的"设置里的教程开关"不存在**（见 §3 OP-014）。该 op 的效果与玩家侧 `HandleSkipIntro` 不等价（后者还会解锁服务/改剧情标志），A14 的"同语义"需要改成"无玩家对等入口"。
5. **§2.7 的 `B(1，共用 #29 规则)` 没有对应 B 行**：`activate_item`/`toggle_item`/`use_tool` 实际差的是 `May*` —— 原生 `ItemContextHandler:228/232` 查 `May && Can`，桥只查 `Can`（+ `IsOwned`）；`MayActivateSlotItem` 含 `forceDisableActivate`、`MayTarget` 含 `forceDisableUse`/`canUseOutsideCombat`。本表这 3 行按事实标 `open` 且 `defect_ids=-`（建议补 B44–B46）。

> 已排除：58 个写 op 里有 3 个 debug 写只在 HTTP dispatch 可见（没有 MCP 工具）。审计 §0 的 58 这个总数是对的，但"模型可调用"的写面只有 55 个。
