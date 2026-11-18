# R5_B_GAMEPLAY_REVIEW — R5-B（B1/B2/B2b）独立玩家等价审计

> 审计者：`reviewer-b`（fresh context，未参与 R5-B 任何实现；本轮实现者另有其人）。
> 审计时间：2026-11-18T20:47Z – 21:25Z（Linux CT101，**无 Windows、无游戏进程、无存档操作、无网络、无 DLL 安装、无 dev/scan、无 Workshop、未运行 build.sh/dist**）。
> 分支：`r5/overnight`。审计目标快照：**`49a9f566`**（B1 docs sync；B1=`704b5b88`、B2=`a0ac8848`、B2b=`643003c9`）。
> 说明：
> 1. 审计期间 `r5/overnight` 从 `49a9f566` 前移到 `6493a553`（中间为 C 阶段离线提交与本 review 文档本身；`git diff --name-only 49a9f566..6493a553` **不含 B1/B2/B2b 生产代码或测试**，含 `agent/fake_game.py`、C 报告/测试与 docs）。因此本审计对 B 代码的构建/测试结论对已提交分支尖端同样成立。所有测试都在独立 worktree `_internal/review-b-worktree@49a9f566` 上运行，不用主工作树。
> 2. 本审计唯一写入的文件就是本 review 文档；未修改生产代码/测试，未 `git add`/`commit`，未构建 `dist/`。审计结束时共享工作树存在其他 teammate 的**未提交**改动（C4 fake 场景、以及针对本 review WARN 的文档/代码修复等）；那些改动不在本审计目标提交内，本审计未运行/未验证它们。
> 3. 证据分级：**VERIFIED** = 本次实际运行或实际读源码/反编译并给出文件行号（行号均按 `49a9f566` 快照）；**REPORTED** = 提交信息/历史文档声明，本次未独立复现；**NOT_RUN** = 环境/授权不允许（Windows L3、真机、存档、扫描器）。

---

## 0. 结论摘要（Verdict）

| 项 | 结论 | 一句话依据 |
|---|---|---|
| **B1** 一等公民交易 staging | **PASS（L1/L2，离线）**，2 个 P2/P3 审计发现 | 只用原生 inventory 回调（`TryAcceptAll` / `Expel+UncheckedAccept`），全库无 `currentNegociatedItem`/归属/意图/价格直写；`completed` 严格要求 after==requested + 物品在称重台 + 谈判 UI 打开 + 同 client；静默/不匹配一律 `uncertain`；A→B→A、同顾客多笔、买卖两向、全部拒绝态、重复 id、同 item 不同 id 保护均有测试。发现见 W-B1-1..4（模态守卫启发式/异常 fail-open、容器白名单、client 身份 hash、矩阵措辞），**均不影响 staging 成功判据，属 L3 前应收紧项**。 |
| **B2** 类型化 negotiation-tablet 确认 | **PASS（L1，离线）** | 按 `kind`+`target` 身份路由（非按钮 index）；只认 GenericConfirmation 与 NegotiationTablet/Confirm；只调原生 public 回调（`ConfirmButtonPressed`/`CancelButtonPressed`/`OnConfirmPanelYes`/`OnConfirmPanelNo`）；路径内无 `press`；歧义/不可读/disabled/已变模态在派发前拒绝；回答后回读 `prompt_closed`+`shop_state`，未关闭即 mismatch 非 ok；generic confirmation 旧行为未回归（全量 864/0）。发现见 W-B2-1..4（答案歧义错误码、TOCTOU、面板来源注释不准、agent 文档/Play 措辞）。 |
| **B2b** typed overlay dismissal | **PASS（L1，离线）** | `close_windows` 先用 `WindowKinds.Overlays` 关两个无适配器覆盖层，调用其原生 `Button.onClick`（=`ApplyButton`/`CloseButton`/`CloseOverlayButton`），名字与 test-only `press` 白名单完全同源；`Clean` 纳入两覆盖层，关不掉即 `window_readback_mismatch` 非成功。发现见 W-B2b-1/2（路径检测 fail-open 风险、`Clean` 不含 CustomWindowOpen 的既有缺口）。 |
| **B3** 录制槽重启续跑 | **NOT_RUN / 未在本轮 B 提交中实现** | handoff §3-B3 标注“建议本轮后段，不阻塞”；`704b5b88`/`a0ac8848`/`643003c9` 均未新增 binding 持久化代码（grep `recording_binding` 无命中），`known-limitations` §10.1 仍记录“一个 Mod 进程=一局”。无夸大。 |
| **B 阶段退出门槛** | **离线满足；L3 NOT_RUN；Play BLOCKED_BY_PLAY_POLICY** | C# 864/0、gate 53/0、contract 526、Python 485/0、Release build 0/0、policy catalog `774fbfdd…` 一致；`play_allowlist == []`（实测），B1/B2/B2b 均未在真机验证，文档中也未宣称 L3。 |

**没有任何一项判 FAIL。** 本审计没有发现“silent/mismatched native 仍返回 ok:true”的路径，也没有发现直接改游戏指针/归属/价格/加钱的代码。

### 0.1 本次实际运行的门禁记录（VERIFIED）

worktree：`/root/work/game_mods/_internal/review-b-worktree`（`git rev-parse HEAD` = `49a9f566a09a712a3d6feac892d0fe6857f7b445`，clean）。

| 套件 | 命令 | 退出码 | 结果 | 原始日志（gitignored） |
|---|---|---|---|---|
| C# 纯逻辑 + 脚本 gate + 适配器契约 | 见下方命令 A | **0** | **passed=864 failed=0**；gate **passed=53 failed=0**；**contract checks=526 / adapter contract OK** | `_internal/logs/review_b_cs.log` |
| Python 全量 | 见下方命令 B | **0** | **Ran 485 tests — OK**（47.551s） | `_internal/logs/review_b_py.log` |
| validator 聚焦 | `-p 'test_trace_validator.py'` | **0** | **Ran 117 tests — OK** | 终端输出 |
| Skill 工具引用 | `-p 'test_skill_tool_refs.py'` | **0** | **Ran 16 tests — OK** | 终端输出 |
| trace fixture CLI | 逐文件 `python agent/trace_validator.py <fixture>` | 5×0 + 3×1 | **exit0**: `trade_partial_ok` / `pending_different_item_ok` / `prompt_confirm_then_real_start_ok` / `sleep_async_converges_ok` / `pending_stage_different_item_ok`；**exit1**: `trade_visit_complete_no_effect_invalid`→`TRADE_EVIDENCE_MISSING`、`pending_same_item_retry_conflict`→`CONFLICTING_WRITE_WHILE_PENDING`、`pending_stage_same_item_retry_conflict`→`CONFLICTING_WRITE_WHILE_PENDING`（relation=`same_item`，validator_version=1.3.0，schema=2） | 终端输出 |
| src Release build | `dotnet build src/PawnMind.csproj -c Release -p:GameManaged=/root/work/game_managed` | **0** | **0 Warning(s) / 0 Error(s)** | `_internal/logs/review_b_build.log` |
| policy catalog 一致性 | `python tools/gen_policy_catalog.py --check` | **0** | `ok: …PolicyCatalog.g.cs matches tool_policy.json (sha256 774fbfdd…)`；canonical policy sha 实测同为 `774fbfdd7b44ef5c6c7b616f885f1b624758b862b3e750497f8a39cc26763277` | 终端输出 |
| MCP 工具面（独立 stdio 会话，非仅靠单测） | 用 worktree `agent/tests/mode_fixture.FakeBridge` 起真实 MCP stdio，`list_tools` | — | **full=117、recording=107，`stage_negotiation_item` 两者都在**；full−recording 恰为 10 个高风险工具（`act/press/new_game/save_game/save_recording_game/dispatch_expedition/advance_expedition/get_ui_snapshot/wait_until/wine_add_water`） | 终端输出 |
| Play/test 面 | 同上脚本 `allowed_tools_for_mode` + `tool_policy.play_allowlist` | — | `play_allowlist=[]`、`allowed_tools_for_mode("play")=[]`；`stage_negotiation_item`/`close_windows` 的 `modes={"bootstrap":false,"play":false,"test":true}`；test 面为 `None`（不过滤） | 终端输出 |
| 工具清单 | `docs/audit/tools-inventory.json` | — | full=117、recording=107，均含 `stage_negotiation_item` | 终端输出 |
| 执行位（W-ENV-1 修复） | `git ls-files -s test.sh test/ContractGate.Tests/run.sh` | — | 两者均 **100755** | 终端输出 |

命令 A：

```bash
cd /root/work/game_mods/_internal/review-b-worktree/PawnMind
export DOTNET=/root/.dotnet/dotnet \
       PAWNMIND_NEWTONSOFT_DLL=/root/work/game_managed/Newtonsoft.Json.dll \
       GAME_ASSEMBLY=/root/work/game_managed/Assembly-CSharp.dll \
       REQUIRE_GAME=1
flock /root/work/game_mods/_internal/pawnmind-test.lock ./test.sh
```

命令 B：

```bash
cd /root/work/game_mods/_internal/review-b-worktree/PawnMind
PYTHONPATH=$PWD /root/work/game_mods/PawnMind/.venv/bin/python -m unittest discover -s agent/tests
```

**计数对账（防止“数字变大即完成”式误读）：**
- C#：A 审计基线 `c3196301`=825（已含 B2 的 8 个 + B2b 的 4 个）→ `3886b210` 加 `R5_RUN_07` = 826 → `704b5b88` 加 38 个 `R5_STAGE_*` = **864**，与本次实测一致。
- 契约：启动基线 506 → B2 +9 = 515 → B1 +11 = **526**，与本次实测一致。
- Python：A 审计 `5b7e2cff`=415（当时 B1 尚未合入）→ C2/C3 的 Python 测试 + B1 自身 +2（`test_mcp_tools_mode` 1 个新测试、`test_tool_policy` 1 个）合入后为 B1 提交信息报告的 **477** → `49a9f566` +2（validator 两个 call-scoped 测试）→ `6487048e` +6（`test_mcp_entrypoint`）→ **485**，与本次实测一致（`git show 6487048e -- …test_mcp_entrypoint.py | grep -c '^+    def test'` = 6；`git show 49a9f566 -- …test_trace_validator.py | grep -c '^+    def test'` = 2）。

### 0.2 环境（VERIFIED）

| 项 | 值 |
|---|---|
| 主机 | `Linux CT101 6.17.13-2-pve x86_64` |
| dotnet | `/root/.dotnet/dotnet` `9.0.321` |
| Python | `/root/work/game_mods/PawnMind/.venv/bin/python` `3.12.3` |
| 游戏程序集 | `/root/work/game_managed/Assembly-CSharp.dll` sha256 `725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05`（与 R5_BASELINE 一致） |
| Newtonsoft | sha256 `a56146202232958f46bd6a28b5a7da166aea123ee0d646735a46e5c341dfbf1f` |
| 游戏进程 / Windows / 存档写 | **无 / 不可用 / 未接触**；L3 一律 `NOT_RUN` |
| 反编译参考（只读，gitignored `_decomp/`） | 仅引用类型/方法/行号与结论，不复制大段代码，符合仓库 AGENTS §5 |

### 0.3 读了什么（本次实际阅读）

- 规则/交接：`PawnMind/AGENTS.md`、根 `AGENTS.md`、`R5_HANDOFF_2026-11-19.md` §3（B1/B2/B3）与 §6、`R5_A_PROTOCOL_REVIEW.md`（A 审计方法与判据基线）、`R5_BASELINE.md`、`docs/reports/R5_A_TEST_MATRIX.md`。
- 提交全文：`git show 704b5b88`（B1）、`git show a0ac8848`（B2）、`git show 643003c9`（B2b）、`git show 49a9f566`（协议/文档同步），含 `--numstat` 与逐文件 diff；`git log --oneline -12`。
- B1 源码：`src/Core/NegotiationStagingGuard.cs`（全文）、`src/Bridge/NegotiationStagingService.cs`（全文）、`src/Game/NegotiationStagingAdapter.cs`（全文）、`src/Bridge/GameActions.cs`（`StageNegotiationItem` + `Accept`/trade 相关）、`src/Game/GameState.cs`（`TryLocateItem`/`GetBackCounterInventory`/`IsStagingModalOpen`/`GetCurrentClientHandle`/`GetContainerIds` 等）、`src/PawnMindMod.cs`（dispatch）、`src/Core/RunChangePolicy.cs`（`PendingScope`）、`src/Bridge/ActionRouter.cs`（write 路由、fingerprint、`FindPendingFor`、`Classify`/`DecorateResponse`）、`src/Bridge/ActionResult.cs`（`SuccessRaw`/dispatch claim）、`src/Core/OperationStatus.cs`（`Classify`/`IsTerminal`）。
- B2/B2b 源码：`src/Core/PendingPrompt.cs`（全文）、`src/Bridge/PromptService.cs`（全文）、`src/Bridge/PromptJson.cs`（全文）、`src/Game/PromptAdapter.cs`（全文）、`src/Core/Windows.cs`（全文）、`src/Bridge/WindowService.cs`（全文）、`src/Game/WindowAdapter.cs`（全文）、`src/Core/UiPressPolicy.cs`、`src/Bridge/UiSnapshot.cs`（path 约定）。
- 工具面/政策：`agent/mcp_server.py`（新工具注册与描述）、`agent/policy/tool_policy.json`（`stage_negotiation_item`/`answer_prompt`/`close_windows`/`press` 条目）、`agent/tool_policy.py`（`allowlist`/`play_allowlist`）、`agent/trace_validator.py`（`VALIDATOR_VERSION`、`CALL_SCOPED_OPS`、`classify_pending_relation`）、`src/Core/PolicyCatalog.g.cs`、`src/Core/ModePolicy.cs`、`agent/tests/test_mcp_tools_mode.py` / `test_tool_policy.py` / `test_mcp_server.py` / `test_trace_validator.py`、`__fixtures__` 8 个 trace fixture、`server.py` 的 `--tools` 实现。
- 工具清单/矩阵：`docs/audit/tools-inventory.json`、`docs/audit/PLAYER_ENTRY_MATRIX.csv`（OP-063）、`docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md`、`skills/pawnmind-first-week/SKILL.md`、`docs/pawnmind-known-limitations.zh-CN.md`、`docs/audit/R5_NEXT_LIVE_SESSION.md`、`docs/eval/R5_LLM_PILOT_PROTOCOL.md`、`PawnMind/README.md` 与根 `README.md`（工具数）。
- 测试：`test/PawnMind.Tests/NegotiationStagingTests.cs`（全文 723 行）、`R5DayEndModalTests.cs`（全文）、`PromptServiceTests.cs`（既有 generic 断言）、`PromptTestFakes.cs`、`WindowRulesTests.cs` / `WindowServiceTests.cs`、`test/PawnMind.AdapterTests/Program.cs`（B1/B2 新增契约段）。
- 反编译只读核对（`_decomp/`）：`EmporiumEntry.cs`（backInvinvElement 回调 -> `PlayerStore.AddedItemToWeightedArea`/`RemovedItemToWeightedArea`）、`PlayerStore.cs`（两个回调实现、`SellItemFromTable`、`EndDay`）、`SlotMarker.cs`（`TryAcceptOnce` = `Expel`+`UncheckedAccept`）、`GameGridInventory.cs`（`UncheckedAccept`/`Expel`/`MayHaveValidInventorySlot`）、`GraphUtils.cs`（`TryAcceptAll`）、`ItemMouseDragHandler.cs`（`EndDrag` 的落点调用）、`NegociationUIManager.cs`（`OpenConfirmPanel`、`OnConfirmPanelYes/No`、窗口 id/命名）、`NegotiationTabletFrame.cs`、`TabletUI.cs`（`BigButton` -> `Button.onClick`）、`TabletButton.cs`、`GenericConfirmationUIManager.cs`、`CustomUIManager.cs`（`CreateWindow` 命名）、`CustomUIWindow.cs`、`EndOfDayUIManager.cs`、`BargainUIManager.cs`、`StoreShutterButton.cs`、`StoreUIManager.cs`。

---

## 1. B1 — 一等公民交易 staging（最高业务优先级）

### 1.1 逐条验证（对应 handoff §3-B1 与本次审计要求）

| 要求 | 结论 | 证据 |
|---|---|---|
| 只使用玩家可达的原生 inventory 放置，不直接改 `currentNegociatedItem`/归属/意图/价格 | **VERIFIED** | 唯一写路径：`NegotiationStagingAdapter.cs:171–218`（在称重台上：`back.Expel(item)` + `back.UncheckedAccept(item)`；不在台上：`back.TryAcceptAll(item)`；异常/静默失败时用原生 accept 还原，`183–188/199–217`）。全仓 `grep -rn "currentNegociatedItem\|SetCurrentlyNegociated\|AddCredit\|GetCorrectBaseValue" src/` 仅命中注释与既有只读快照字段（`SnapshotFieldMap.cs`），无任何赋值/加钱/鉴定读取。`GameActions.StageNegotiationItem`（`GameActions.cs:480`）只 new service + port，无额外写。 |
| 与反编译 wiring 一致（`EmporiumEntry.backInvinvElement` 回调 -> `PlayerStore.AddedItemToWeightedArea`/`RemovedItemToWeightedArea`） | **VERIFIED** | `_decomp/EmporiumEntry.cs:318–327`：`backInvinvElement.onSlotAddItemEndFunc` -> `AddedItemToWeightedArea`；`onSlotRemoveItemEndFunc` -> `RemovedItemToWeightedArea`。`_decomp/PlayerStore.cs:2873–2890`（add：设 `currentNegociatedItem`、`OnAddedToWeightedArea`、`OnWeightedAreaChanged`、`TutorialUIManager.BuyItemDrag`、`HandleAutoNegoUIDisplay`——后者按 intent/ownership 打开谈判 UI）与 `2909–2920`（remove：重新指向台上最后一件或 null）。`_decomp/SlotMarker.cs:64–114`：玩家拖放落点执行 `Expel`+`UncheckedAccept`；`_decomp/ItemMouseDragHandler.cs:577–637` 的 `EndDrag` 对普通 inventory 正是 `lastSlot.TryAcceptOnce()`，对 machinery tab 用 `GraphUtils.TryAcceptAll`。因此“同一条原生 inventory 回调链”成立；差别只是桥自动挑选合法槽位（任何合法槽位玩家也都能拖到）。 |
| `completed` 必须是派发后回读：`after==requested` + 物品在称重台 + 谈判打开 + 同一 client | **VERIFIED** | `NegotiationStagingService.cs:108–168` 顺序检查：`Called`、`Error`、`AfterReadable`、client 可读/未变、`StagedItemIdAfter == itemId`、`ItemOnCounterAfter`、`NegotiationReadableAfter`、`NegotiationOpenAfter`；任一不满足 → `uncertain`。before/after 的 staged id 由 adapter 在原生调用前后各读一次（`NegotiationStagingAdapter.cs:156–165/243–276`），client handle 也是先采样再比较（`156/268`）。**没有任何一条路径在缺证据时返回 ok:true。** |
| 静默/不匹配的原生返回永不 ok | **VERIFIED** | 静默 `TryAcceptAll`：adapter 用 `item.parentInventory` 复核（`NegotiationStagingAdapter.cs:199–217`），不落台/无 error 也会被 `ReadAfter` 的 `ItemOnCounterAfter`/staged 指针挡成 uncertain；`Called=false`/native 异常/readback 不可读/指针不匹配/UI 不开/客户端变，全部 `uncertain`（`NegotiationStagingService.cs:108–168, 222–273`）。对应测试 `R5_STAGE_23/24/25/26/27/28/29`。 |
| A→B→A、同一顾客多笔、买/卖两向覆盖 | **VERIFIED（L1 fake）** | `R5_STAGE_21`（A→B→A，`Transitions=="0->56,56->57,57->56"`，`NegotiationStagingTests.cs:377–399`）、`R5_STAGE_22`（同顾客连续 3 笔，`:402–418`）、`R5_STAGE_36`（SELL 方向=玩家买入，`:548–564`）、`R5_STAGE_12/13`（BUY 需 owned / SELL 拒绝 owned，`:282–293`）。**真机原生行为 L3 NOT_RUN。** |
| 失败关闭：不可读 client/intent/item/counter/negotiation、模态、sold/unreachable/owned-wrong、back counter full | **VERIFIED（除模态可读性的边界，见 W-B1-1）** | `NegotiationStagingGuard.cs:101–138`：`not_in_game`/`no_client`/`client_unreadable`/`intent_undefined`/`negotiation_unreadable`/`modal_open`/`item_not_found`/`item_sold`/`item_unreadable`/`item_unreachable`/`not_owned`/`already_owned`/`staging_read_failed`/`back_counter_unreadable`/`back_counter_full`；`StagingContainers.IsPlayerReachable` 仅 main/front/back/showcase/hidden/trash（`159–185`）。测试 `R5_STAGE_02..17` 逐码断言。 |
| PendingScope：同 item+args 阻塞、不同 item 放行 | **VERIFIED** | `src/Core/RunChangePolicy.cs:117–124` 把 `stage_negotiation_item` 与 `confirm_trade` 同为 call-scoped；`ActionRouter.cs:250–252` 对 call-scoped op 用参数指纹 `FindPendingFor`；指纹覆盖除 op/rev/action_id 外的全部参数（`ActionRouter.cs:1101–1116`，即 item_id）。测试 `R5_STAGE_35`（同一 router：同 item 新 id → `operation_pending`、native 只 1 次；换 item → 成功且 native 2 次），`R5_STAGE_19`（`PendingScope` 判定）。validator 侧同口径：`agent/trace_validator.py:243` `CALL_SCOPED_OPS = {confirm_trade, stage_negotiation_item}`，fixture 实测 same-item→`CONFLICTING_WRITE_WHILE_PENDING(relation=same_item)`、不同 item→exit 0。 |
| 不读隐藏字段、不加钱、不执行任意代码 | **VERIFIED** | 新文件无 `System.Reflection`/`Activator`/`AddCredit`/`AppraisalHelper`/`GetCorrectBaseValue`；`GeneralHelper.IsItemOwned`、`client.clientIntent`、`PlayerStore.GetCurrentlyNegociatedItem` 都是玩家可见语义；`PromptAdapter` 只读 TMP 文本/按钮 interactable（`PromptAdapter.cs:99–124, 169–187`）。 |
| 工具面/政策/文档同步完整 | **PASS（有文档 WARN，见 §4）** | `agent/mcp_server.py:163`（recording 集合）、`1217–1219`（工具定义）、“full 117 / recording 107”帮助文案（`1287`）；policy 条目 `tool_policy.json:1742`（`purity=write`、`player_reachable=true`、`status=open`、`decision=needs_evidence`、`modes.play/bootstrap=false`、`modes.test=true`、`live_evidence=[]`）；`PolicyCatalog.g.cs:11` Sha256=`774fbfdd…`；`tools-inventory.json` full/recording 各含新工具；`PLAYER_ENTRY_MATRIX.csv` 新增 OP-063；adapter contract +11（`PawnMind.AdapterTests/Program.cs:177–196`）；`49a9f566` 同步 validator 1.3.0/Skill/benchmark/AGENTS。 |

### 1.2 审计发现（WARN，均非“假成功”路径）

**W-B1-1（P2，守卫完整性）：模态遮挡的“失败关闭”只在“读到且确实打开”时成立；模态状态不可读/形态未知时是 fail-open。**
- `GameState.IsStagingModalOpen`（`GameState.cs:564–617`）只认两类：`GenericConfirmationUIManager.panel.activeInHierarchy`（`568–573`）与 CustomUI 层根下名字以 `custom_window_` 开头的活动子对象（`611`），并显式排除 `negotiation_tablet`/`bargain_panel`（`578–579, 606`）。异常分支 `catch { return false; }`（`612–616`）——**读不到模态状态被当成“没有模态”**，而服务只认 `ModalOpen=true` 才拒绝（`NegotiationStagingGuard.cs:113`），没有第三态。
- 反编译核对：`CustomUIManager.CreateWindow` 默认把 root 命名 `custom_window_<id>`（`_decomp/CustomUIManager.cs:423,440`），所以 wild-network（`WildNetworkUI.cs:88`，未改名）能被检测到；但 **`EndOfDayUIManager` 把 root 改名为 `EndOfDayTablet`**（`_decomp/EndOfDayUIManager.cs:85,95`），`NegociationUIManager` 改名为 `NegotiationTablet`（`154,165`），`TransitUI` 改名为 `TransitSystem`（`76`）——改名后的 CustomUIWindow 不会被 `custom_window_` 前缀命中；另有 **`StoreUIManager.closeShutterConfirmationPanel`** 这种非 CustomUI 的普通面板（`_decomp/StoreShutterButton.cs:255–258`）。这些模态在“玩家无法拖拽”的意义上可能应阻止 staging，但当前守卫不会检测到。
- 影响面：staging 的成功判据本身仍要求“物品真的在台上 + staged 指针 + 谈判 UI 打开 + 同 client”，所以这不是假成功；但“Guard fails closed on … modal occlusion”（提交信息/B1 交付声明）在**不可读或改名模态**下不成立。建议：把模态读取改成三态（readable/open + error），异常/未知一律拒绝；按 window id（`EndOfDayTablet`/`closeShutterConfirmationPanel` 等）显式登记“阻挡类”模态而不是名字前缀；L3 用 wild-network、日终 tablet、关店确认面板逐一做正负例。

**W-B1-2（P3，L3 待验）：容器白名单把 `hidden`/`trash` 当作“店内可达”，但没有证明其窗口在此刻打开。**
`StagingContainers.IsPlayerReachable`（`NegotiationStagingGuard.cs:169–184`）允许 `main/front/back/showcase/hidden/trash`。对 `main/front/back/showcase` 与商店 UI 同屏的假设成立；`hidden`（暗格）与 `trash`（垃圾桶）是否在谈判时同屏可见未在 L1 证明。若窗口未开，桥能通过原生 accept 把物品放上称重台，而“玩家此刻做不到同样拖拽”。原生路径本身仍是 inventory 回调，故不是作弊；属 player-equivalence 的 L3 待验证项，建议 L3 记录这两类容器的可见性。

**W-B1-3（P3，理论）：client 身份用 `RuntimeHelpers.GetHashCode` 比较，而非引用相等/唯一 id。**
`GameState.GetCurrentClientHandle`（`GameState.cs:925–936`）返回 `RuntimeHelpers.GetHashCode(client)`；`NegotiationStagingService.cs:188–193` 用 before==after 判“同一 client”。identity hash 碰撞概率极低，但严格来说不等价于 `ReferenceEquals`/`StoreClient.GetIdentifier()`。建议改用对象引用相等或客户端唯一 id，并把“before 不可读 = 0 → uncertain”的现有规则保留。

**W-B1-4（P3，文档精度）：`PLAYER_ENTRY_MATRIX` OP-063 的 player_entry 写 `ItemMouseDragHandler.EndDrag`，桥实际直接调 `GraphUtils.TryAcceptAll`/`Expel`+`UncheckedAccept`。**
反编译确认这些正是 `EndDrag`/`SlotMarker.TryAcceptOnce` 使用的同批原生调用（见 1.1），主张成立；但矩阵该列写得更精确的应是“EndDrag -> SlotMarker.TryAcceptOnce（Expel+UncheckedAccept）/ GraphUtils.TryAcceptAll”，并注明“由桥自动选择合法槽位”。不影响判据，建议随 L3 文档收口。

### 1.3 测试覆盖（本次实跑全绿）

- 38 个 `R5_STAGE_*`（`NegotiationStagingTests.cs`）全部 PASS：01/01b/02–37（含 01b、36、37）。覆盖：拒绝码逐项、modal/sold/unreachable/owner 方向/back counter、完成回读、A→B→A、同客多笔、买卖、stale/missing rev、重复 id 重放、同 item 不同 id 保护、uncertain 后 read/get_state 指引。
- 拒绝测试不只断言 `ok:false`：`Refused()` 统一断言 `error` 精确码 + `dispatched:false` + `execution_status:"rejected"` + `StageCalls==0`（`NegotiationStagingTests.cs:163–175`）。
- 缺失（L1 无法覆盖，L3 必做）：真实 adapter 的 native 静默失败/`Expel` 失败/还原路径、真实 A→B→A 与多笔成交、预算拒绝与真实 `AddedItemToWeightedArea` 副作用、模态遮挡。

### 1.4 B1 残留风险 / L3 NOT_RUN

- 真机 `TryAcceptAll`/`Expel`+`UncheckedAccept` 是否真的触发 `EmporiumEntry.backInvinvElement` 的 `onSlotAddItemEndFunc`/`onSlotRemoveItemEndFunc` 并打开谈判 UI —— **L3 NOT_RUN**（反编译 wiring 已核对，行为未在进程内观测）。
- 真实 `HandleAutoNegoUIDisplay` 的打开条件（`EmporiumOption.AutoOpenNegotiationUI`、`isClientArrived`、intent/ownership/`IsClientBuyingThisItem`）与 `negotiation_open` 回读 —— **L3 NOT_RUN**。
- 真实游戏下 “A→B→A”“同顾客多笔” 的完整成交证据（staging + confirm_trade + 现金/物品回读） —— **L3 NOT_RUN**。
- 模态守卫的改名/不可读边界（W-B1-1）、hidden/trash 可达性（W-B1-2） —— **L3 NOT_RUN**。

证据等级：L1 service/guard/router 行为 **VERIFIED**（实跑 + 源码）；native wiring **VERIFIED（只读反编译）**；真实游戏 staging **NOT_RUN**。

---

## 2. B2 — 类型化 negotiation-tablet 确认

### 2.1 逐条验证

| 要求 | 结论 | 证据 |
|---|---|---|
| 按模态身份路由，而不是按钮 index | **VERIFIED** | 身份：`PendingPrompt.Kind/Target`（`PendingPrompt.cs:93–104`），target=`negotiation_tablet_confirm`/`generic_confirmation`（`56–66`）。读取：`PromptAdapter.ReadNegotiationConfirm`（`133–193`）用 `NegociationUIManager.uiObject.GetComponent<NegotiationTabletFrame>()` 的 `confirmRoot` 是否 active 判定打开、`confirmText` 读正文、`confirmButtonHost` 里恰好 2 个 `TabletButton`（`200–212`），并用**原生锚点 x** 区分左右（`214–233`）。`PromptAdapter.Answer`（`243–268`）按 Kind 分派，Target 不符即 `NotDispatched(prompt_changed)`。 |
| 只允许 GenericConfirmation 与 NegotiationTablet/Confirm two families | **VERIFIED** | 未知 Kind → `NotDispatched(PromptChanged)`（`266–267`）；读取只尝试这两族（`ReadGenericConfirmation`/`ReadNegotiationConfirm`）；两族同时打开 → `PromptErrors.PromptAmbiguous`，Readable=false（`52–62`）。 |
| 只调 public 原生回调，路径无 `press` | **VERIFIED** | `ui.ConfirmButtonPressed()`/`ui.CancelButtonPressed()`（`PromptAdapter.cs:294,304`；反编译 `_decomp/GenericConfirmationUIManager.cs:76–88` public），`ui.OnConfirmPanelYes()/OnConfirmPanelNo()`（`339,349`；反编译 `_decomp/NegociationUIManager.cs:1529–1557` public）。`grep -rn "press" src/Game/PromptAdapter.cs src/Bridge/PromptService.cs` 无命中；`close_windows` 同理走 `onClick.Invoke()`。 |
| 歧义/不可读/disabled/changed 模态在派发前拒绝 | **VERIFIED** | 歧义：adapter `52–62`；不可读：各 read 返回 `Unreadable(ReadFailed)`，`PromptPolicy.ValidateAnswer`（`PendingPrompt.cs:197–218`）→ 拒绝；disabled：`PromptPolicy` 先查 `ConfirmEnabled`，adapter 在点击前用实时 read 再查（`AnswerNegotiationConfirm` `320–351`）；changed：`AnswerNegotiationConfirm` 重新 `ReadNegotiationConfirm`，不 readable/open → `NotDispatched(PromptChanged)`（`320–326`）；generic 同样重读（`279–286`）。测试：`R5_MODAL_04/05/07`。 |
| 回答后回读 `prompt_closed` + `shop_state`；mismatch 永不 ok | **VERIFIED** | `PromptService.AnswerPrompt`（`PromptService.cs:64–148`）：`!outcome.Dispatched` → `dispatched:false` + 错误码（`104–114`）；`outcome.Error` → `dispatched:true` + 错误（`116–124`）；之后 `ReadSafe` 必须 `IsClosed`，否则 `prompt_readback_mismatch` + `readback`（`126–136`）；成功行 `ok=true, prompt_closed=true, state=<day/cash/outside/location/shop_state>`（`138–147`），`PromptJson.StateSummary`（`PromptJson.cs:56–76`）。测试 `R5_MODAL_02/06`、既有 `Answer_Panel_Still_Open_Is_Readback_Mismatch`/`Answer_Unreadable_After_Click_Is_Readback_Mismatch`。 |
| 旧 generic confirmation 行为未回归 | **VERIFIED** | 全量 864/0 含既有 `PromptServiceTests` 全部用例；diff 只加 target/enabled 字段与“点击前实时校验”，未删除旧断言（`a0ac8848 --numstat`：PromptService 43/2、PendingPrompt 121/8、PromptAdapter 327/18、PromptServiceTests 6/2、PromptTestFakes 45/4）。`Answer_State_Read_Failure_Still_Reports_Verified_Close` 等旧语义保留。 |
| 原生面板来源与 yes/no 语义 | **VERIFIED（只读反编译）** | `_decomp/NegociationUIManager.cs:1510–1527`：`OpenConfirmPanel` 清空 `confirmButtonHost` 后 `BigButton(x=57,"yes",OnConfirmPanelYes)`、`BigButton(x=57+200+16,"no",OnConfirmPanelNo)` → **是/否 = 左/右**，与 adapter 的 x 排序一致；`TabletUI.cs:365–398` 的 `BigButton` 把 `onClick` 挂到 `Button.onClick`，即玩家点击触发同一回调。`PromptTargets` 的窗口身份来自 `NegotiationTabletFrame.confirmRoot`（`_decomp/NegotiationTabletFrame.cs:33–37`）。契约测试钉住这些成员（`PawnMind.AdapterTests/Program.cs:684,691–698`；`TabletButton.Interactable/label` 见 `339–341`，`NegociationUIManager.IsOpen` 见 `731`）。 |
| 面板语义核对（相对 handoff 的“日终确认框”） | **VERIFIED（反编译），见 W-B2-3** | `OpenConfirmPanel` 全仓只有两个调用点：`NegociationUIManager.OnSellAllClick`（客户端预算不够买完台上的货，`_decomp/NegociationUIManager.cs:821–838`）与 `PlayerStore.SellItemFromTable`（`CanSellItemFromTable()==false` 但 `ignoreBudget` 版为真，`_decomp/PlayerStore.cs:2793–2819`）——即**“超预算仍要卖”的确认框**，不是 `end_day` 直接弹出的框；`PlayerStore.EndDay`（`1682–1687`）不打开它。真正的“有顾客排队时关店”确认是 `StoreUIManager.closeShutterConfirmationPanel`（`_decomp/StoreShutterButton.cs:83–88,255–258`），那一个未被 B2 类型化；但桥的 `end_day` 路径不可达它：`dismiss_waiting=true` 直接走 `EndAndClose`（`GameState.cs:841–856`），`dismiss_waiting=false` 在 `client_still_present`/`customers_waiting` 前置即被拒（`FirstWeekService.cs:149–183`）。因此 B2 类型化的正是首周实机阻塞流程里被 `press` 点的那个面板，但“end_day 会 raise 它”的说法不准确（见 W-B2-3）。 |

### 2.2 审计发现（WARN）

**W-B2-1（P3，错误码一致性/测试精度）：`answer_prompt` 在“歧义模态”下返回 `no_prompt`，而 `get_pending_prompt` 返回 `prompt_ambiguous`。**
`PromptService.AnswerPrompt` 先 `PromptPolicy.ValidateAnswer`（`PromptService.cs:66–68`），而 `ValidateAnswer` 对 `Readable=false` 的 snapshot 一律 `NoPrompt`（`PendingPrompt.cs:197–199`）；`get_pending_prompt` 则把 `snapshot.Error` 原样传回（`PromptService.cs:36–47`），所以读路径能看到 `prompt_ambiguous`。这是既有“unreadable→no_prompt”设计（`PromptPolicy` 注释 + `Answer_Unreadable_Prompt_Returns_NoPrompt_And_Zero_Calls` 钉死），但两条读路径对同一歧义状态给出不同错误码；且 `R5_MODAL_04` 对 answer 只断言 `ok:false` 与 `AnswerCalls==0`（`R5DayEndModalTests.cs:120–128`），未钉错误码。安全性不受影响（拒绝发生在派发前，零点击）；建议统一口径或在测试里显式钉住 `no_prompt`/新增 `prompt_ambiguous` 分支。

**W-B2-2（P2/L3，TOCTOU）：“模态已变”的检测只证明“同一族面板仍打开且选项 enabled”，未比对具体是哪一次确认。**
`AnswerNegotiationConfirm` 重读只检查 `current.Readable && current.Open`，随后点击 `OnConfirmPanelYes/No`（`PromptAdapter.cs:320–351`）；没有比较 body/标签/按钮文本与首次读取是否一致，也没有把“意图/客户”绑定进身份。若在同族面板仍打开但内容已被替换（例如另一位顾客/另一笔超预算确认）时回答，可能把 yes 点到一个已变化的面板上。发生窗口很窄（主线程同步读取/点击），但属真实 TOCTOU 风险；建议把 `body+confirm_text+cancel_text`（或面板 owner 对象引用）作为答案前置指纹，不一致即 `prompt_changed`。**真机验证 L3 NOT_RUN。**

**W-B2-3（P3，文档精度 + L3 待验）：注释/文档把该面板说成“end_day 触发”，与反编译不符；yes/no 与 confirm/cancel 的映射来自布局推导，无运行时语义复核。**
`PendingPrompt.cs:43–48` 写“end_day and a sale the client cannot afford are the two native flows that raise it”；`PromptAdapter.cs:17–25` 写“(raised by end_day / sales over the client's budget)”。反编译实测只有两个“超预算销售确认”调用点（见 2.1 表），`end_day` 不 raise 它。首周工作流中它确实阻塞关店（因此 handoff 把它叫“日终确认框”），但描述来源应改成“超预算卖出确认（会阻塞首周关店流程）”；真正的关店排队确认面板（`closeShutterConfirmationPanel`）仍未类型化，只是当前 `end_day` 工具路径不可达（L3 需复核）。另外 confirm/cancel 映射依赖“原生 yes 在左”的布局约定（反编译当前成立），没有运行时按标签语义/回调身份复核；若未来 prefab 布局翻转，桥会把 confirm 点到 no 而不自知（响应会给出 confirm_text/cancel_text，但代码不自洽）。建议 L3 正例逐项验证是/否文本与行为。

**W-B2-4（P2，文档/协议一致性与 Play 措辞）：B2/B2b 的协议增量没有写进 agent 面文档；`UiPressPolicy` 注释已过时；`known-limitations` 里“正式 Play 不再需要 press”在当前政策下是超前表述。**
- `skills/pawnmind-first-week/SKILL.md`、`docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md` 全文检索无 `negotiation_confirm`/`negotiation_tablet_confirm`/overlay typed dismissal 的说明（`SKILL.md` 最新只到 B1 的 staging；`known-limitations.zh-CN.md:171–173` 与 `R5_NEXT_LIVE_SESSION.md:92–111` 有提及）。MCP 工具描述也未更新：`get_pending_prompt` 仍只列 `{body, confirm_text, cancel_text, single_button}`（`mcp_server.py:629–631`），`close_windows` 仍只写 notes/books/floating windows（`634–639`），未说明覆盖层与 `kind/target` 新字段。
- `src/Core/UiPressPolicy.cs:15,34–35,39` 仍写“end_day 会 raise 它”“nothing else answers it: answer_prompt does not recognise this modal”“R5/B2 is migrating”——B2 之后这些句子已不成立（`answer_prompt` 已识别并会走 typed callback；第四路径已迁移完成）。handoff §6 明确要求同步该注释。
- `docs/pawnmind-known-limitations.zh-CN.md:173` 写“正式 Play 不再需要 `press` 完成这两类阻塞”。实测 `tool_policy.play_allowlist=[]`（`agent/tool_policy.py:75–78` 要求 `live_evidence`，全部为空）、`allowed_tools_for_mode("play")=[]`，且 `close_windows` 的 `modes.play=false`——**当前 Play 面根本不存在**，typed 路径只是 test 面可用并为将来 promotion 做准备。措辞应改为“typed 路径已实现（L1），但 Play 仍 BLOCKED_BY_PLAY_POLICY，需 L3 证据 + policy promotion”，否则读者会误以为 Play 已可用。
- 影响：代码安全性不因此改变（`play_allowlist` 为空即 Play 全拒），但这是 handoff B 阶段“API/tool policy 全面一致”的文档缺口，且会误导后续 D 阶段操作者。

### 2.3 测试覆盖（本次实跑全绿）

- `R5_MODAL_01..08`（`R5DayEndModalTests.cs`）：身份/文本/enabled/target 读取、confirm/cancel 各一次点击 + `prompt_closed`+`shop_state`、歧义不答、disabled 拒绝、面板仍开非成功、changed 模态未派发、generic 旧 identity/行为不回归。全部 PASS。
- 既有 `PromptServiceTests`（generic 关闭/单按钮/不可读/state 形状/状态读失败仍报 verified close 等）全部 PASS。
- 缺失（L1 → L3）：真实 `NegotiationTabletFrame`/`TabletButton` 的读取、yes/no 左右映射、点击后 native 动作是否发生（例如超预算卖出是否真的执行）、两种模态同开的真实场景、以及 W-B2-2 的 TOCTOU。

### 2.4 B2 残留风险 / L3 NOT_RUN

- 真机 `get_pending_prompt` 能否读到 `NegotiationTablet/Confirm` 的文本/按钮、enabled 状态 —— **L3 NOT_RUN**。
- 真机 `answer_prompt` 点击 yes/no 后：面板关闭、`shop_state` 变化、超预算卖出是否真正发生的区分 —— **L3 NOT_RUN**。
- 真机中英文标签、同名按钮、双模态同开、模态在 read/answer 之间变化 —— **L3 NOT_RUN**。
- 权限模式（locked/bootstrap/test/play）：policy 层实测 `play_allowlist=[]`、`answer_prompt` 仅为 `modes.play=true` 候选但无 live evidence；`press` 仍 test-only。**真机模式切换 L3 NOT_RUN。**

证据等级：L1 service/adapter 逻辑 **VERIFIED（实跑）**；native 面板/回调 wiring **VERIFIED（只读反编译）**；真机模态 **NOT_RUN**。

---

## 3. B2b — 两个 adapter-less 覆盖层的 typed dismissal

### 3.1 逐条验证

| 要求 | 结论 | 证据 |
|---|---|---|
| `close_windows` 用原生按钮（同玩家点击）关掉 tutorial 覆盖层与 `_DialogueOptionUI` | **VERIFIED（代码/路径）** | `WindowAdapter.cs:22–27` 三条路径常量；`CloseOverlays`（`161–174`）先 `InvokeControl(TutorialOverlayButton)`，再 `ApplyButton`，fallback `CloseButton`；`InvokeControl` 只做 `button.onClick.Invoke()`（`176–182`）——即玩家点击触发的同一个 UnityEvent。`WindowKinds.Overlays`（`Windows.cs:27–34`）先在计划里执行（`117–135`），`WindowService.cs:72–77` 分派。 |
| 检测覆盖层并纳入 readback（关不掉 = mismatch 非成功） | **VERIFIED** | 读取：`ReadWindows` 设 `TutorialOverlayOpen`/`DialogueSettingsOpen`（`WindowAdapter.cs:57–62`）；`WindowSnapshot.Clean` 纳入两者（`Windows.cs:62–83`）；`WindowRules.IsClean` 要求 readable+Clean（`138–141`）；`WindowService.Close` 在原生调用后重读，`!IsClean` → `window_readback_mismatch`（`WindowService.cs:90–103`）。测试 `R5_WIN_01..04`：计划顺序、Clean 语义、typed port 调用、仍开→mismatch；全部 PASS。 |
| 无 `press` 依赖 | **VERIFIED（代码）** | `close_windows` 路径不含 `press`；`UiSnapshot.Press` 只被 op `press` 使用（`UiSnapshot.cs:66–120`）。 |
| 路径来源可信度 | **VERIFIED（来源）/ L3 待验证** | 三条路径字符串与 test-only `press` 白名单完全相同（`UiPressPolicy.cs:30–32`），而 `press` 在该路径上的使用来自首周实机记录（`LIVE_SESSION_2026-11-18.md` §2.2b、`SESSION_2026-11-18_FIRST_WEEK.md`，**REPORTED**）；`WindowAdapter.PathOf`（`199–210`）与 `UiSnapshot.Path`（`UiSnapshot.cs:144–154`）是同一“最多上溯 3 层”的约定，因此匹配成功的前提与 press 当时相同。**typed close_windows 本身真机 L3 NOT_RUN。** |

### 3.2 审计发现（WARN）

**W-B2b-1（P2/L3，fail-open 风险）：overlay 检测/点击完全依赖硬编码路径匹配；匹配失败会被读成“屏幕上没有覆盖层”，于是 `close_windows` 返回 `ok:true, dispatched:false, changed:false`（看起来干净）而覆盖层仍在。**
`ReadWindows` 用 `FindControl(path) != null` 判定（`WindowAdapter.cs:58–61`），`FindControl` 要求 `PathOf(button.transform)` 与常量**完全相等**（`184–196`），且不同于 `UiPressPolicy.StripDuplicateSuffix`，这里**不忽略 Unity 的 ` (1)` 重名后缀**；若路径长度/命名变化（例如 `CanvasScreenLayer2` 不再是根、或按钮是 `CloseOverlayButton (1)`），检测为 false → 计划里没有 `overlays` → 直接 no-op 成功。代码在“检测到但关不掉”时是 fail-closed（mismatch），但“检测不到”时是 fail-open。建议：把 `FindControl` 的最长前缀/后缀归一化与 press 保持一致；或在读取里增加“同一 Canvas 层下可见覆盖层结构”的独立证据；L3 正负例必测。
（注：现有三条路径与 press 同源，首周实机 press 成功关闭这两个覆盖层，因此当前 build 下命中概率高——这是 REPORTED 证据，不是 typed 路径的 L3 证据。）

**W-B2b-2（P3，既有缺口，B2b 未引入）：`WindowSnapshot.Clean` 仍未包含 `CustomWindowOpen`，`Describe` 也未输出覆盖层/custom 字段。**
`Windows.cs:75–83` 的 `Clean` 只含 Book/Paper/Contract/Deed/Tablet/Movable/两个覆盖层，**不含 `CustomWindowOpen`**；而 `PlanClosures` 会关 Custom（`133`）。因此 custom window 未被关掉时 readback 不会报 mismatch（这是 R4 既有行为，B2b 未改变）。另外 `WindowService.Describe`（`131–147`）不输出 `tutorial/dialogue/custom`，证据可读性差。建议顺手把 `CustomWindowOpen` 纳入 `Clean` 并补 `Describe` 字段（会改变 `close_windows` 的失败面，需回归 + L3）。

### 3.3 测试覆盖（本次实跑全绿）

- `R5_WIN_01`（计划先 overlays）、`R5_WIN_02`（Clean 要求覆盖层关闭）、`R5_WIN_03`（typed port 一次调用、readback ok）、`R5_WIN_04`（仍开→mismatch）全部 PASS；既有 windows 测试未删改（`643003c9 --numstat` 仅 +38/+55 测试行、`Windows.cs` 33/1）。
- 缺失（L1 → L3）：真实 `CanvasScreenLayer2` 层级与 Button 路径、点击 Apply/Close 后游戏状态（尤其是 Apply 可能提交设置）、tutorial overlay 的关闭副作用。

### 3.4 B2b 残留风险 / L3 NOT_RUN

- 真实场景中两个覆盖层的检测、点击、关闭后回读 —— **L3 NOT_RUN**。
- `Apply` vs `Close` 的选择：代码优先 Apply（`WindowAdapter.cs:167–168`），它是玩家“确认”路径但可能提交面板里现存的设置；需 L3 验证关掉的是预期窗口且不产生意外设置写入 —— **L3 NOT_RUN**。
- 路径失配时的 fail-open（W-B2b-1）、custom window readback 缺口（W-B2b-2） —— 需 L3。

证据等级：L1 service/adapter 代码路径 **VERIFIED**（实跑 + 源码）；路径常量 **VERIFIED（与 press 同源）/ 真机 typed 行为 REPORTED**；typed dismissal **L3 NOT_RUN**。

---

## 4. 工具面 / 政策 / 协议同步核查（VERIFIED）

| 项 | 结果 |
|---|---|
| MCP 全量/录制面 | 独立 stdio 会话实测 **full=117 / recording=107**；`stage_negotiation_item` 两面均注册；full−recording 恰为 10 个高风险工具；`--tools` 帮助文案与实际一致（`mcp_server.py:1287`）。 |
| policy 条目 | `stage_negotiation_item`：`purity=write`、`player_reachable=true`、`status=open`、`decision=needs_evidence`、`modes={bootstrap:false, play:false, test:true}`、`live_evidence=[]`——与“test-only、无真机证据”一致；`test_tool_policy.py` 的 `test_stage_negotiation_item_has_a_reviewed_write_entry` 全绿。 |
| 生成文件一致 | `gen_policy_catalog.py --check` exit 0；canonical policy sha = `PolicyCatalog.g.cs:11` = **`774fbfdd7b44ef5c6c7b616f885f1b624758b862b3e750497f8a39cc26763277`**；policy JSON 文件字节 sha 为 `adf80533…`（canonical JSON 哈希才是协议哈希，二者不同属正常）。 |
| tools-inventory | full 117 / recording 107，新工具同时在两个集合；与新提交的 `mcp_server` 实测一致。 |
| PLAYER_ENTRY_MATRIX | 新增 OP-063 `stage_negotiation_item`（write；player_entry/guard/business_effect/evidence_source/min_test 均填写；`_decomp/PlayerStore.cs:2873-2920` 与 `_decomp/EmporiumEntry.cs:318-327` 引用正确）。措辞精度见 W-B1-4。 |
| validator / fixtures | `VALIDATOR_VERSION=1.3.0`（`trace_validator.py:169`）、schema 2；`CALL_SCOPED_OPS` 与 Core.PendingScope 一致（`:243`）；8 个 fixture 实测 5×exit0 + 3×exit1，code/relation 与 `EXPECTED_CODES`（`test_trace_validator.py:1617–1628`）一致；scoped 117 OK。 |
| Skill / benchmark | B1 部分完整：工具数 117/107、policy sha 基线、staging 工作流、completed/uncertain 判据、稳定拒绝码（`SKILL.md:10,29,110,228–259,437–445,554,558–559`；`FIRST_WEEK_BENCHMARK_SPEC.md:10–11,78,112–113,141`）。**B2/B2b 部分缺失**，见 W-B2-4。 |
| adapter contract | B1 +11 checks 覆盖 `PlayerStore.AddedItemToWeightedArea/RemovedItemToWeightedArea`、`GameGridInventory.Expel`、`GameInventory.MayHaveValidInventorySlot`、`NegociationUIManager.uiObject`、`CustomUIManager.GetWindow`、`CustomUIWindow.root/IsAlive`、`GenericConfirmationUIManager.panel`；B2 +9 checks 覆盖 `NegotiationTabletFrame.confirmRoot/confirmText/confirmButtonHost`、`NegociationUIManager.OnConfirmPanelYes/No/Instance`、`GenericConfirmationUIManager.confirmButton`。adapter 用到的 `TabletButton.Interactable/label`、`NegociationUIManager.IsOpen`、`EmporiumEntry.backInvinvElement`、`PlayerStore.GetCurrentlyNegociatedItem`、`GeneralHelper.IsItemOwned`、`CustomUIManager.GetLayerRoot` 等均有既有/新增 check（`Program.cs:96,138,140,151,166,170,177–196,339–341,684,691–698,731`）。整体 contract 526 OK。 |
| Play 面 | `play_allowlist=[]`（`tool_policy.play_allowlist` 要求 live evidence，全为空），`allowed_tools_for_mode("play")=[]`；B1/B2/B2b 均未进入 Play。**Play 仍 BLOCKED_BY_PLAY_POLICY（不是 PASS 也不是 FAIL）**。 |
| 仓库卫生 | `git ls-files -s`：`test.sh`、`test/ContractGate.Tests/run.sh` 均 **100755**（`b07186e1` 修复生效），本次 C# 门禁因此实际执行了 53 项 gate（此前干净 worktree 会静默跳过）。 |

**文档漂移（不阻塞 B 判据，建议收口）：**
- `PawnMind/README.md:7,16` 与根 `README.md:101,135` 仍写“116 个工具 / recording 106”，B1 后应为 117/107；这两个文件不在 B1 声明的同步集（AGENTS/Skill/benchmark/validator/inventory/matrix/catalog）内，但属面向用户的过时事实（既有漂移，B1 使其多偏 1）。
- `docs/audit/R5_NEXT_LIVE_SESSION.md:33` 仍写 “trace validator v1.2.0”，而 `49a9f566` 已升 1.3.0（该文件是 B 交付物，应随之更新；截至 `6493a553` 也未修）。
- `docs/audit/mcp-tools-{full,recording}.json` 是历史快照（114/104），并非当前清单；RFC 已标注其历史定位，不作同步要求。
- `docs/pawnmind-known-limitations.zh-CN.md:11.5` 把“typed 日终弹窗”列在“仍未覆盖/未验证”中，而 §11.4 说“已实现并合入、L3 NOT_RUN”；语义应为“L3 未验证”，建议措辞统一（`5fd83cc4` 已改为“typed 日终弹窗真机”，歧义基本消除；49a9f566 快照的该行仍为旧措辞）。

---

## 5. “是否被夸大 / 是否藏错误”专项检查

1. **是否有私有 setter 调用伪装成玩家动作？** 无。B1 唯一写路径是原生 inventory accept/expel 回调（§1.1 第 1/2 条）；`grep -rn "currentNegociatedItem\|CurrentNegociatedItem\|SetCurrentlyNegociated"` 在 `src/` 只命中注释与既有只读快照字段。B2/B2b 只调用 public 原生回调/`Button.onClick`，未直接 SetActive/改 panel 状态（`PromptAdapter.cs:243–354`、`WindowAdapter.cs:161–210`）。
2. **是否有测试只断言 `dispatched:false` 而不验错误码？** B1 的每个拒绝测试通过 `Refused()` 统一断言精确 error + dispatched + execution_status + 零派发（`NegotiationStagingTests.cs:163–175`）；B2 的 `R5_MODAL_05/07` 断言精确码，`R5_MODAL_04` 的 answer 分支**未钉错误码**（W-B2-1）；B2b 的 `R5_WIN_04` 断言 `window_readback_mismatch`。整体达标。
3. **是否有“永远返回成功”的 fake port？** `FakeStagingPort` 可被配置为 throw/Called=false/任意 evidence（`NegotiationStagingTests.cs:21–46`），`FakePromptPort` 有 `Outcome/ThrowOnAnswer`（`PromptTestFakes.cs`），`FakeWindowPort` 有 `AfterOverride/ThrowOnClose`；反例测试确实走到了失败分支。L1 无法证明真实 adapter 的失败路径（已标 L3）。
4. **是否有“completed 可被 fake 伪造”的假证据？** 对 staging：completed 需 4 个独立 readback 事实同时成立（as above），测试却用可控 fake 证明；但生产 adapter 才构造 `StagingEvidence`，且 `Called=true` 只在拿到 item 后设置（`NegotiationStagingAdapter.cs:159–168`），无 error 才继续；未发现可伪造路径。
5. **文档是否宣称 L3？** B1/B2/B2b 提交信息、Skill、benchmark、`R5_NEXT_LIVE_SESSION`、known-limitations 均显式 L3 `NOT_RUN`；**唯一措辞过强**的是 `known-limitations.zh-CN.md:173`“正式 Play 不再需要 press 完成这两类阻塞”（当前 Play 面为空、`close_windows` 甚至不是 play candidate）——见 W-B2-4；应改为“typed 路径已就绪，Play 仍 BLOCKED_BY_PLAY_POLICY，待 L3 + promotion”。
6. **是否有失败被吞成成功？** B1：adapter 的 native 异常/静默失败都写入 `evidence.Error` 或被 readback 挡成 uncertain（`NegotiationStagingAdapter.cs:169–236`；service `:108–168`）。B2：`IPromptPort.Answer` 三态（Called/CalledWithError/NotDispatched）把“拒绝”与“可能已执行”分开（`PendingPrompt.cs:259–298`、`PromptService.cs:92–124`）。B2b：`CloseOverlays` 找不到控件时 throw → `WindowService` 捕获为 mismatch（`WindowAdapter.cs:170–173`、`WindowService.cs:85–88`）。
7. **测试是否只测 happy path？** 否：B1 覆盖 15+ 拒绝码/回读不匹配/静默/异常/A→B→A/多笔/买卖/盲重试；B2 覆盖歧义/disabled/panel 仍开/changed/旧 generic 回归；B2b 覆盖计划顺序/Clean/typed 调用/mismatch。缺口主要是 L3（真实 native）与 §4/WARN 所列文档/边界。

---

## 6. 证据分级汇总

### 6.1 VERIFIED（本次实际运行或实际读源码+行号）

- 门禁：49a9f566 worktree 上 C# `passed=864 failed=0`、gate `53/0`、contract `526`；Python `Ran 485 OK`；validator scoped `117 OK`、skill-refs `16 OK`；8 个 fixture CLI `5×exit0 + 3×exit1` 且 code/relation 精确；Release build `0 warning / 0 error`；`gen_policy_catalog.py --check` exit 0 且 sha=`774fbfdd…`；`git ls-files -s` 脚本 100755。
- B1：源码/守卫/回读判据、PendingScope+fingerprint、router 分类、工具面/policy/inventory/matrix/catalog 同步、adapter contract 526；反编译核对 `EmporiumEntry`/`PlayerStore`/`SlotMarker`/`GameGridInventory`/`GraphUtils`/`ItemMouseDragHandler` 的玩家路径与回调。
- B2：身份路由、双模态族、public 回调、无 press、拒绝分支、回读、generic 不回归；反编译核对 `OpenConfirmPanel`（两处“超预算卖出”调用点、yes/no 位置）、`TabletUI.BigButton`、`GenericConfirmationUIManager` 回调、`UiPressPolicy` 第四路径。
- B2b：overlays 计划/Clean/readback/typed onClick；路径与 press 白名单同源；反编译核对 `CustomUIManager` 命名约定、`EndOfDayUIManager`/`NegociationUIManager` 改名、`StoreShutterButton` 关店确认面板。
- 环境：主机/dotnet/Python/DLL sha。

### 6.2 REPORTED（提交信息/历史文档声明，本次未独立复现）

- B2/B2b 提交信息中的历史门禁数字（820/824、gate 53、contract 515）：A 审计已独立复跑过同族提交；本次在 B head 全量复跑通过，历史数字未被重新逐提交重放。
- 首周实机 `press` 关闭两个覆盖层（`SESSION_2026-11-18_FIRST_WEEK.md`、`LIVE_SESSION_2026-11-18.md`）与实机日终阻塞流程；本机无 Windows，无法复核原始轨迹（R5_BASELINE E2/E3 证据缺口仍在）。
- B1 的 RED 证据（实现前失败序列）：本次只在最终快照上验证绿灯，未在历史态重放红。
- 各提交信息中的 L3“NOT_RUN”声明：与本次环境事实一致，但真机未运行。

### 6.3 NOT_RUN（环境/授权不允许，全部 L3）

- B1：真实游戏 staging（`TryAcceptAll`/`Expel+UncheckedAccept` -> `AddedItemToWeightedArea`/`RemovedItemToWeightedArea`）、谈判面板真实打开、A→B→A 与同顾客多笔真实成交、模态遮挡（wild-network/日终 tablet/关店确认）、hidden/trash 容器可达性、client 身份真实变化。
- B2：真机 `get_pending_prompt`/`answer_prompt` 的 negotiation_confirm 读取与点击、yes/no 语义、超预算卖出真实执行、双语/同名/双模态/模态变化、权限模式 locked/bootstrap/test/play 的真机行为。
- B2b：真机覆盖层检测/点击/关闭回读、Apply vs Close 副作用。
- 所有 `run_mode=play` 行为：`play_allowlist` 为空 → **BLOCKED_BY_PLAY_POLICY，NOT_RUN**。
- Workshop / 官方 scanner / 存档 / 游戏进程 / dev-scan：未接触（本审计职责外）。

---

## 7. 审计判定

- **B1 PASS（L1/L2）**：玩家等价 staging 主路径成立；完成判据与失败关闭（除 W-B1-1 的模态可读性边界）满足 handoff；工具面/政策/契约/文档（B1 部分）一致。WARN：W-B1-1（P2，模态守卫 fail-open/改名窗口漏检）、W-B1-2（P3，hidden/trash L3）、W-B1-3（P3，身份 hash）、W-B1-4（P3，矩阵措辞）。
- **B2 PASS（L1）**：类型化确认按身份路由、只调 public 回调、无 press、拒绝与回读语义正确、generic 无回归。WARN：W-B2-1（P3，answer 歧义码）、W-B2-2（P2/L3，TOCTOU）、W-B2-3（P3，面板来源注释/yes-no 布局依赖）、W-B2-4（P2，agent 文档/UiPressPolicy 注释/Play 措辞）。
- **B2b PASS（L1）**：typed dismissal 走玩家同款 `onClick`、readback 覆盖两覆盖层。WARN：W-B2b-1（P2/L3，路径失配时 fail-open、未忽略 Unity 重名后缀）、W-B2b-2（P3，既有 `Clean` 不含 CustomWindowOpen）。
- **B3 NOT_RUN**：本轮 B 提交未实现；handoff 允许后置，不构成本轮 PASS/FAIL。
- **B 阶段退出门槛**：Linux L1/L2 单测/集成/契约/policy 全部通过（本次独立复跑）；工具面/政策/生成文件/矩阵与 B1 文档同步一致；**Windows 真机全部 L3 NOT_RUN，未发现任何把离线证据写成 LIVE 的表述**（仅 W-B2-4 的 Play 措辞需收紧）。→ **离线门禁通过；L3 NOT_RUN；Play BLOCKED_BY_PLAY_POLICY**。

## 8. 残留风险与建议（汇总）

| ID | 级别 | 说明 | 建议 |
|---|---|---|---|
| W-B1-1 | P2 | 模态检测只认 GenericConfirmation + `custom_window_` 前缀，改名的 CustomUIWindow（EndOfDayTablet/TransitSystem）与普通面板（closeShutterConfirmationPanel）漏检；读取异常 catch→false（fail-open） | 模态三态（readable/open/error），未知一律拒绝；按 window id 登记阻挡类模态；L3 正负例 |
| W-B1-2 | P3/L3 | `hidden`/`trash` 被当作同屏可达，但未证明窗口可见 | L3 记录容器可见性；不可见时拒绝 |
| W-B1-3 | P3 | client 身份用 `RuntimeHelpers.GetHashCode` 比较 | 改引用相等/客户端唯一 id |
| W-B1-4 | P3 | matrix player_entry 写成 EndDrag，实际是 EndDrag 内部的 SlotMarker/GraphUtils 原语 | 改为“EndDrag -> SlotMarker.TryAcceptOnce / GraphUtils.TryAcceptAll（桥自动选槽）” |
| W-B2-1 | P3 | answer 对歧义模态返回 no_prompt，read 返回 prompt_ambiguous；R5_MODAL_04 未钉 answer 错误码 | 统一错误码并在测试中钉住 |
| W-B2-2 | P2/L3 | changed-modal 只验同族仍开，不比对具体确认内容（TOCTOU） | 增加 body/标签/owner 指纹，不一致即 prompt_changed |
| W-B2-3 | P3 | 注释称 end_day 会 raise negotiation confirm；实际是“超预算卖出确认”（两处调用点）；真正的关店排队确认未类型化（当前工具路径不可达） | 更正注释；L3 复核关店确认是否需要 typed 路径；yes/no 语义逐项 L3 |
| W-B2-4 | P2 | SKILL/benchmark/MCP 描述未写入 negotiation_confirm/overlay typed；`UiPressPolicy` 注释过时；known-limitations “正式 Play 不再需要 press” 超前（play_allowlist=[]，close_windows 非 play candidate） | 同步 agent 面文档；修正 Play 措辞为“实现完成、Play 仍 BLOCKED 待 L3+promotion” |
| W-B2b-1 | P2/L3 | overlay 路径匹配失败时 close_windows no-op ok（fail-open）；未忽略 Unity “ (1)” 后缀 | 归一化后缀/加独立检测证据；L3 正负例 |
| W-B2b-2 | P3 | `Clean` 不含 CustomWindowOpen（既有），`Describe` 缺新字段 | 纳入 Clean + 补 Describe；回归 |
| W-DOC-1 | P3 | 根/PawMind README 仍 116/106；`R5_NEXT_LIVE_SESSION` 仍写 validator 1.2.0；known-limitations §11.5 wording | 随文档 pass 收口（不阻塞 B） |

> 审计声明：本文件为 `reviewer-b` 本次唯一写入；未修改生产代码/测试，未 `git add`/`commit`，未构建 `dist/`、未安装 DLL、未启动游戏、未接触存档/网络/Workshop/dev-scan。所有 VERIFIED 结论可在本次会话命令与 `_internal/logs/review_b_{cs,py,build}.log` 中复核；REPORTED/NOT_RUN 已逐项显式区分，未把开发者/历史报告的数字当作本次自己执行的结果。
