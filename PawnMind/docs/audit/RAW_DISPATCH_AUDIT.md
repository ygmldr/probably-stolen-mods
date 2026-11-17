# RAW_DISPATCH_AUDIT — 写动作的"是否真的调用了原生"矩阵（Round 4 / F4b）

> 基线：`4a5b8d1` → 本轮提交（见 `docs/reports/ROUND4_REPORT.md` 的 HEAD 行）
> 证据等级：**SRC**（逐分支阅读源码）+ **OFFLINE**（`./test.sh Round4`）
> 本文回答一个问题：**每个写动作的响应里，"原生调用到底有没有发生"是显式声明、还是被推断出来的？**

## 0. 结论先行

| 结论 | 状态 |
|---|---|
| `dispatched` 不再是"响应里有没有 JSON"的推断 | **FIXED** |
| 三态：`not_dispatched` / `dispatched` / `unknown` | **FIXED** |
| 缺失/非法声明默认 `unknown`（不当作"安全拒绝"，也不当作"已派发"） | **FIXED** |
| 未知派发 + `ok:false` → `execution_status:"uncertain"`（**不**再归为 `rejected`） | **FIXED** |
| 未知派发同样阻止"同 op 新 action_id"的盲重试 | **FIXED** |
| 全部 48 个 `purity=write` 工具的生产者声明派发事实 | **FIXED**（见 §2） |
| 新的写生产者若忘记声明 | 落到 `unknown`（fail-closed），并被 `docs/audit/RUN_CHANGE_EVIDENCE.md` 的复扫流程发现 |

## 1. 单一判定源

```
ActionResult.Dispatch (string, 三态)
        │  由生产者声明：Success() / Failure() / Failed() / UnknownDispatch()
        │  或由 RawJson 的 "dispatched": true|false 解析（缺失/非布尔 → unknown）
        ▼
ActionRouter.Classify(ok, pending, uncertain, dispatch)   ← 唯一分类入口
        ▼
OperationRecord.Dispatch ──► 写响应 / get_operation / 缓存重投递
        ▼
ExecutionStatus.Classify: ok→completed；!ok && not_dispatched→rejected；
                          !ok && dispatched→failed；!ok && unknown→uncertain
```

- `Classify` 不再读取 `result.RawJson != null`，也不再从 `ok` 反推（`src/Bridge/ActionRouter.cs` `Classify`）。
- 线格式：`dispatch_status`（字符串，恒存在）+ `dispatched`（`true`/`false`/`null`，兼容旧读者）。
  `null` 是 unknown 的线路表示，**绝不**序列化成 `false`。
- `ActionGate.Complete` 只接受 `dispatch` 字符串，并 `Normalize` 非法值为 `unknown`。
- `ActionGate.FindPendingFor` / `FindUnconfirmedRunChange` 用 `MayHaveRun`：
  `unknown` 与 `dispatched` 一样阻止第二次原生调用，只有 `not_dispatched` 放行。

## 2. 生产者矩阵（48 个写工具）

分类说明：**R** = 守卫拒绝（`not_dispatched`）；**D** = 原生已调用（`dispatched`）；
**U** = 无法确认（省略字段 → `unknown`）。

| 工具 | 生产者（文件:行） | R（拒绝） | D（已派发） | 备注 |
|---|---|---|---|---|
| `open_shop` | `GameActions.OpenShop` | ✅ 守卫 | ✅ `Success` / `Failed` | 失败改 `Failed`（点击后异常） |
| `call_next_customer` | `GameActions.CallNextCustomer` | ✅ | ✅ | |
| `dismiss_current_client` | `GameActions.DismissClient` | ✅ | ✅ `Failed` | 原 `Failure` 会误报"未派发" |
| `advance_dialogue` | `GameActions.AdvanceDialogue` | ✅ | ✅ | |
| `buy_wholesale` | `GameActions.BuyWholesale` | ✅ | ✅ `Failed` | 部分扣款后异常 |
| `commissary_buy` | `GameActions.BuyCommissary` | ✅ | ✅ | |
| `move_item` | `GameActions.MoveItem` | ✅ | ✅ `Failed`（`no_space`/`detach_failed`） | `TryAcceptAll`/`Expel` 已调用 |
| `confirm_trade` | `TradeService.Confirm` | ✅ `Refusal` | ✅ `Success` / `Uncertain` | 原有显式声明 |
| `bargain` | `GameActions.Bargain` | ✅ | ✅ | |
| `activate_item` / `toggle_item` / `use_tool` | `InteractionService` | ✅ | ✅ `Failed` | `*_failed` 在原生调用之后 |
| `choose_dialogue` | `FirstWeekService.ChooseDialogue` | ✅ 变量 `dispatched` | ✅ | 原有 |
| `end_day` | `FirstWeekService.EndDay` | ✅ | ✅ | 原有 |
| `answer_prompt` | `PromptService.AnswerPrompt` | ✅ | ✅ | 原有 |
| `close_windows` | `WindowService.Close` | ✅ | ✅ | 原有 |
| `dismiss_tutorial_hints` | `TutorialService.Dismiss` | ✅ | ✅ | 原有 |
| `go_out` / `scavenge` / `return_home` | `NightService` | ✅ | ✅ | **新增声明** |
| `return_home`（旧别名路径） | `NightReturnService` | ✅ | ✅ | 成功行原缺 `dispatched`，**已补** |
| `confirm_new_game` / `load_game` / `load_recording_game` / `sleep` | `RecordingSaveService` | ✅ | ✅ | 原有 |
| `new_game` / `new_game_continue` / `new_game_next_page` / `select_new_game_option` / `focus_new_game_selected_perk` / `blur_new_game_option` / `open_new_game_menu` | `NewGameMenuService` + `NewGameMenuSession` | ✅ | ✅ | **新增**：`Dispatched` 字段在调用端口前置位，端口 `Called=false` 时回退 |
| `buy_upgrade` | `UpgradeService.BuyJson` | ✅ | ✅ | **新增**（`upgrade_failed` 属 D） |
| `machine_insert` / `machine_collect` | `MachineService` | ✅ | ✅ | **新增** |
| `start_fermentation` / `wine_add_ingredient` / `wine_add_water`(debug) | `WineService` | ✅ | ✅ | **新增** |
| `redeem_ticket` | `LotteryService.RedeemJson` | ✅ | ✅ | **新增** |
| `set_label` | `LabelService.SetJson` | ✅ | ✅ | **新增**（成功后回读体加 `dispatched`） |
| `set_pen_mode` | `MarkingPenService.SetModeJson` | ✅ | ✅ | **新增** |
| `call` / `phone_advance` / `phone_choose` / `end_call` | `PhoneService` | ✅ | ✅ | **新增**（advance/choose 回读体加标记） |
| `toggle_service` | `ServiceStatusService` | ✅ | ✅ | **新增** |
| `analyze_item` | `AnalyzerService.AnalyzeJson` | ✅ | ✅ | **新增**；gene 分支是"无原生调用"，显式 `false` |
| `deliver_request` / `report_client` / `barter_accept` / `arrest_wanted` | `P9ActionService` | ✅ | ✅ | **新增**；`arrest_wanted` 守卫移出 `try`，使失败分支判定唯一 |

**U（无法确认）分支数量：0。** 所有写生产者都能区分"调用前拒绝"与"调用后失败"。
唯一保留 `unknown` 的入口是**外部输入不可信**的情况：RawJson 不是合法 JSON、
`dispatched` 非布尔、或 `Dispatcher` 抛出异常（见 §3）。

## 3. 异常与丢失路径

| 路径 | 旧行为 | 新行为 |
|---|---|---|
| 调度器抛异常（`ActionRouter.Dispatch` catch） | `ActionResult.Failure` → `rejected` / `dispatched:false` | `ActionResult.UnknownDispatch` → `uncertain` / `dispatch_status:"unknown"` / `dispatched:null` |
| RawJson 缺 `dispatched` | 推断为 `true` → `failed` | `unknown` → `uncertain` |
| `dispatched` 是字符串/数字 | 推断为 `true` | `unknown` |
| RawJson 非法 JSON | `SuccessRaw` 已置 `bad_response`，但派发仍推断 `true` | `unknown` |
| 结果不可观测（`TryRefreshRevision` 失败） | `execution_status:"uncertain"` | 不变，另带记录级 `dispatch_status` |
| 响应丢失后重投递 | 可能改写派发声明 | 记录级唯一来源，三视图一致（`R4-F4-07`） |

`uncertain` 写响应现在自带 `next`：*"the outcome is unknown: do not retry with a new
action id; read the state and decide"*。

## 4. 仍属 OPEN 的部分

1. **裸 `ActionResult.Failure` 的语义仍是约定**："调用方在派发前拒绝"。
   本轮把已知的调用后失败全部改为 `Failed`（`GameActions` 6 处、`InteractionService` 3 处），
   但**没有**静态检查器阻止未来的作者在调用后写 `Failure`。
   缓解：新增写动作必须同时给出 Red 测试（`R4-F4-05` 形状），并在 review 时对照本表。
2. **`act` 包装**：`RunChangeOps.Resolve` 已能识别内层 run-change op；
   但 `act` 不出现在 `purity=write` 策略里（full 模式才注册），本轮未为它单独补矩阵行。
3. **`capture_screenshot` / `list_screenshots`**：读/诊断面，不在 48 个写工具内；
   `ScreenshotService` 的副作用留待 F7 的 scanner 结论。

## 5. 复现命令

```bash
cd PawnMind
./test.sh Round4            # R4-F4-01 … R4-F4-07
rg -n 'SuccessRaw\(|ActionResult\.(Failure|Failed|UnknownDispatch|Success)\(' src
rg -n 'dispatched|DispatchStatus' src/Core/OperationStatus.cs src/Bridge/ActionResult.cs
```
