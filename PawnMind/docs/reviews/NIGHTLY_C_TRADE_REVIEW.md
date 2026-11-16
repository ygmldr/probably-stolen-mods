# NIGHTLY_C_TRADE_REVIEW — C 模块（confirm_trade 守卫与交易结果协议）

Status: **APPROVE**
（首轮审阅发现的 C-1（拒绝后 `GET /operation.dispatched=true`，归因在 D 的 ActionGate）已由 Lead 在
`7548ddb` 修复并带回归测试；本文件按最终提交复核通过。C-2（B12 方向语义）保留为 L3 `NOT_RUN` 待确认项，
不是代码缺陷；C-3 为安全方向的潜在漏报，需真机观察。二者均不阻塞。）
Reviewing commit(s): C 本体 `f09314e3f5e53df43c6b8d27bd7796a8dc235844`；
C-1/D-1 修复 `7548ddb23a8fb9a0ea10a08740e2c82352891b07`；文档收尾 `f7ae62c117995dffb43a56da70ddefd6254ccb5b`；
**最终 HEAD `f7ae62c`**（审阅基准：最终提交，工作树除 `docs/reviews/` 外干净）。
Change scope: `src/Core/TradeGuard.cs`、`src/Bridge/TradeService.cs`、`src/Game/GameTradePort.cs`、
`src/Game/GameState.cs`（`GetCurrentClientHandle`/trade read helpers）、`src/Bridge/GameActions.cs`
（`ConfirmTrade` 重写）、`src/PawnMindMod.cs`（`confirm_trade` 路由）、
`test/PawnMind.Tests/TradeServiceTests.cs`、`test/PawnMind.AdapterTests/Program.cs`（+12 contracts）、
`agent/mcp_server.py`（`confirm_trade` 文档与错误分类）、`agent/policy/tool_policy.json`
（`confirm_trade` reason/live_evidence）、`docs/audit/PLAYER_ENTRY_MATRIX.csv`（B10–B16 引用）。

## Real evidence（本 Agent 亲自运行）

```text
$ ./test.sh PP_TRADE
passed=17 failed=0                # TradeServiceTests 全量（01..09 + B10..B16 + dialogue split）

$ ./test.sh                        # 最终提交 f7ae62c 全量回归
passed=750 failed=0
contract gate tests: passed=53 failed=0
contract checks: 503              # 比 B 之前多 12 条 = C 的 adapter 契约
adapter contract OK               # 日志 /tmp/nightly_review_testsh_committed.log

$ .venv/bin/python -B -m unittest discover -s agent/tests
Ran 365 tests in 56.446s
OK                                # 日志 /tmp/nightly_review_pytests_committed.log

$ grep -n "confirm_trade" src/PawnMindMod.cs src/Bridge/GameActions.cs
src/PawnMindMod.cs:790  case "confirm_trade": return GameActions.ConfirmTrade(item_id, side)
src/Bridge/GameActions.cs:462  return new TradeService(new GameTradePort()).Confirm(itemId, side, StateSnapshot.CurrentRevision)
```

读 diff/实现（不只看报告）：

```text
$ git show f09314e -- src/Bridge/TradeService.cs src/Core/TradeGuard.cs src/Game/GameTradePort.cs \
    src/Bridge/GameActions.cs test/PawnMind.Tests/TradeServiceTests.cs test/PawnMind.AdapterTests/Program.cs
# 核对结论：
# - Accept() 只调用 GameState.AcceptNegotiationOffer() → NegociationUIManager.Instance.OnOfferAcceptClick() 一次
# - 不再手拼 BuyItem/SellItem/AddCredit；没有 ModHelper.AddCredit 调用
# - 每个拒绝分支在 fake port 上断言 AcceptCalls == 0
$ grep -rn "AddCredit|BuyItem|SellItem" src/Bridge/TradeService.cs src/Game/GameTradePort.cs  # 0 命中
```

**本 Agent 复现的跨层缺陷（C-1，借用 /tmp 副本探针，仓库未改）**：在 `ActionRouter` 中注入一个
“返回 `{"ok":false,"error":"trade_locked","dispatched":false}` 的 dispatch”，写入后查询 `GetOperationJson`：

```text
PROBE response={"ok":false,"error":"trade_locked","dispatched":false,"rev":3,
                "operation_id":"rej-1","execution_status":"rejected","delivery_status":"delivered"}
PROBE execution=rejected dispatched=True ok=False
FAIL  ProbeReview.Probe_Rejected_Operation_Dispatched_Flag
      AssertionException: PROBE: rejected op must not claim dispatched (expected=False, actual=True)
```

即：HTTP 响应正确写了 `dispatched:false`（派发前拒绝），但 `GET /operation` 的缓存记录把它翻成
`dispatched:true`。交易拒绝（B10/B11/B12/B16 等）走的就是这条路径。

## Invariants checked（逐条 PASS/FAIL/PARTIAL）

| 不变量 | 结论 | 依据 |
|---|---|---|
| 只经原生玩家入口 `OnOfferAcceptClick` 提交，不手拼买卖/改钱 | **PASS（L0-static）** | `GameTradePort.Accept()` → `GameState.AcceptNegotiationOffer()` → `NegociationUIManager.Instance.OnOfferAcceptClick()`；`TradeService`/`GameTradePort` 中 0 处 `AddCredit`/`BuyItem`/`SellItem` |
| B10 `trade_locked` 预检查、0 次点击 | **PASS（L1-fake）** | `PP_TRADE_03a`：`AcceptCalls=0`、`dispatched=false`、`execution_status=rejected` |
| B11 `business_complete` / `intent_undefined`，0 次 | **PASS** | `PP_TRADE_03b`（两条断言） |
| B12 物品必须在桌上 + 方向匹配（用原生 staged ownership，不自写 IsOwned） | **PASS（L0/L1）** | `PP_TRADE_03c`：`item_not_staged` / `not_owned`；`TradeGuardPolicy` 用 `StagedItemOwned`；旧 `GameState.IsOwned` 判断已删除 |
| B13 barter 条件（offer 存在/可接受/已介绍）0 次 | **PASS** | `PP_TRADE_03d` 三条 |
| B14 情报商费用 `cannot_afford`/`not_introduced` 0 次 | **PASS** | `PP_TRADE_03e` |
| B15 枪单 `gun_order_not_accepted` 0 次 | **PASS** | `PP_TRADE_03f` |
| B16 原生买/卖 gate `cannot_buy_from_table` / `client_refuses_item` 0 次 | **PASS** | `PP_TRADE_03g` |
| 读失败 `trade_read_failed` → 0 次点击、不假成功 | **PASS** | `PP_TRADE_03_Read_Failure_Dispatches_Nothing` |
| 成功证据必须 `business_complete=true` 且同一顾客 `client_unchanged=true` | **PASS（L1-fake）** | `PP_TRADE_08`：`client_changed` → 不 ok；`TradeService.Confirm` 证据层顺序 |
| 静默 native return / 异常 / 未完成 → `uncertain`，绝不 `ok:true` | **PASS** | `PP_TRADE_06`、`PP_TRADE_07`、`PP_TRADE_09`；`Classify` pending/uncertain 优先于 ok |
| 以物易物零现金也能 completed（现金只是辅助证据） | **PASS** | `PP_TRADE_05`：`cash_changed=false` + `items_transferred=true` + completed |
| 同 `action_id` 重试不重复点击；新 id 重复成交必须拒绝 | **PASS** | `PP_TRADE_02`（AcceptCalls 恒 1、ReadCalls 不增加）；`FindPendingFor` 对非终态同 op 返回 `operation_pending` |
| 交易 completed 不阻塞对话 workflow；未提交不报 completed | **PASS** | `PP_TRADE_04_Dialogue_Is_A_Separate_Workflow`、`PP_TRADE_04_Uncommitted_Trade_Is_Not_Reported_Completed` |
| `ok:true` 仅在 execution_status=completed（HTTP 响应层） | **PASS** | `ActionRouter.DecorateResponse` 用 `ExecutionStatus.IsSuccess` 覆盖 ok；交易拒绝响应 ok=false |
| 拒绝后 `GET /operation` 的 `dispatched` 轴与 HTTP 响应一致 | **FAIL（C-1 = D-1）** | 探针复现：响应 `dispatched:false`，operation 记录 `dispatched:true` |
| 适配器契约覆盖新增原生成员 | **PASS** | `contract checks: 503`（+12）；`NegociationUIManager.OnOfferAcceptClick/IsTradeLocked/IsNegociatingItemOwned`、`PlayerStore.CanPay/CanBuyItemFromTable/CanSellThisItem`、`StoreClient.isIntroduced/serviceCost/barterOffer/gunOrderData/isBusinessComplete`、`BarterOffer.IsBarterAcceptable`、`GunOrderData.IsAccepted` 均在 PASS 行中实测出现 |
| 不把测试当 live evidence / 不宣称实机通过 | **PASS** | `tool_policy.json` 的 `confirm_trade` 仍 `needs_evidence`、`live_evidence=[]`、play 模式 false；报告与 SKILL 均标 L3 `NOT_RUN` |

## Critical findings

无“会造成错误成交/重复点击/伪造成功”的 Critical。C-1 已修复；以下保留修复记录与两条待真机确认的观察：

### C-1（Medium，**已在 `7548ddb` 修复**）拒绝交易的 `GET /operation.dispatched` 与 HTTP 响应矛盾

- 归因在 D 的 `ActionGate.Complete`（无条件 `Dispatched = true`），交易只是受影响最明显的路径。
- **复现**：见 §Real evidence 的 `PROBE`。等价交易路径：奏 `confirm_trade` 被守卫拒绝（如 `trade_locked`），
  HTTP 响应 `dispatched:false + execution_status:"rejected"`；随后 `get_operation(operation_id)` 返回
  `dispatched:true`。
- **影响**：AI/Skill 按协议文档把 `dispatched:true + rejected` 视为“可能已派发/结果未知”的异常组合，
  可能误判为 uncertain 而停机或要求人工核对；审计轨迹两处证据不一致。不产生错误成交。
- **修复（`7548ddb`）**：`ActionGate.Complete` 改为
  `entry.Operation.Dispatched = entry.Operation.Execution != ExecutionStatus.Rejected`；新增 C# 测试
  `OperationTests.Rejected_Operation_Reports_Dispatched_False`（`GET /operation` 对 guard 拒绝返回
  `dispatched=false`）。**复核**：本 Agent 单独跑该测试 `PASS`；全量 750/0 绿；修复前探针（见 §Real evidence）
  不再复现。

### C-2（Medium，L3 待确认）B12 方向判定依赖 `IsNegociatingItemOwned()` 的语义，尚未有游戏内证据

- **位置**：`GameTradePort.Read`：`input.StagedItemOwned = SafeNegociatingItemOwned()`；`TradeGuardPolicy`：
  `selling && !StagedItemOwned → not_owned`、`buying && StagedItemOwned → already_owned`。
- **依据**：`docs/audit/PLAYER_ENTRY_MATRIX.csv` OP-011 只写 “side buy/sell matches IsOwned”，未记录该谓词
  返回的是“玩家拥有”还是“顾客拥有”；`docs/audit/NIGHTLY_SNAPSHOT_CALLGRAPH.md` 也未展开该方法语义。
- **影响（若反了）**：合法 buy 会被拒成 `already_owned`、合法 sell 被拒成 `not_owned`——**安全侧**（不会
  错误成交），但交易成功路径整体不可用；也可能反过来漏掉某个原生拒绝分支。无游戏环境无法裁决。
- **需要**：`NEXT_LIVE_SESSION.md` 已列 confirm_trade 真机项；建议加一条“用玩家自己上架物品卖 vs 顾客物品买，
  各记录 `IsNegociatingItemOwned()` 原始值与 `confirm_trade` 结果”的步骤；或从 `_decomp` 再逐行核对谓词
  （若允许，仅本地分析、不提交产物）。

### C-3（Low）成功后若原生流程立刻移除顾客，会被判成 `client_changed`（安全但可能漏报成功）

- `GameTradePort.Accept()` 用 `RuntimeHelpers.GetHashCode(StoreClient)` 前后比对；若某些意图在点击后
  立即把当前顾客换成下一位/置空，`ClientUnchanged=false` → `uncertain`。方向是安全（绝不假成功），
  但可能把已完成交易标成待查。需要 L3 观察 Native `OnOfferAcceptClick` 后 `GetCurrentClient()` 是否仍为同一实例；
  `PLAYER_ENTRY_MATRIX.csv` 写“closing dialogue starts|client leaves after dialogue”，倾向于仍为同一实例。

## Potential regressions

1. 旧调用方（如外部脚本）若依赖 `confirm_trade` 旧响应形状（`ok:true` 即成功、无 `execution_status`），
   现在 `ok:true` 只对应 completed（更严格），且拒绝时 `ok:false` 是**正常业务拒绝**而非异常；MCP 客户端
   需按 `dispatched`/`execution_status` 分支。SKILL/benchmark 已按新 schema 写。
2. `confirm_trade` 现在对“不在谈判桌上的 item_id”直接 `item_not_staged`；旧实现也校验 staged item，但
   旧 `findItem` 语义不同（旧代码先 `FindItem` 再比 staged），若玩家用 back-counter 物品 id 而非当前
   negotiated id 会更容易被拒。属有意收紧，需在 skill 里提示正确 item_id 来源（SKILL §5 已提示）。
3. `TradeService.Confirm` 现在在每次 action 内 new 一个 service/port；真实 port 每次重新读状态，
   无跨请求缓存，无状态泄漏风险。
4. 交易完成后服务 re-read 的是 `StateSnapshot.CurrentRevision`（旧值，不刷新）；`rev` 只作为响应标注。
   不构成回滚风险，但若有消费者把 `rev` 当作 result_rev 使用需注意（`get_operation.result_rev` 由 router 写）。
5. B10–B16 预检查是“同源谓词”的静态映射；若游戏更新改变 `OnOfferAcceptClick` 内部顺序/新增分支，预检查
   可能漏掉新分支 → 走 `uncertain` 兜底（不假成功）。需要在版本升级时重跑 `PLAYER_ENTRY_MATRIX` 对照。

## NOT_RUN / UNKNOWN

- **L3-real-game 全部 NOT_RUN**：真实 `OnOfferAcceptClick` 点击、B10–B16 真实现象、以物易物真实现金/物品
  转移、枪单/情报商成功路径、顾客离场时序、`IsNegociatingItemOwned()` 语义。需要：游戏 + 授权存档 +
  新构建 DLL；不得触碰用户存档。
- **UNKNOWN**：`StoreClient.clientIntent` 的枚举全集与 `BARTER/INFORMATION_DEALER/GUNSMITH` 之外意图的
  分支（例如 RENT/故事顾客）；未知意图当前不额外拦（只做通用 B16 gate），失败会落 `uncertain`。
- **UNKNOWN**：`items_transferred` 的判定（staged id 变化）在“买/卖同一 id 换手”场景是否可靠；真机验证。
- **NOT_RUN**：DSH/宿主真实 MCP 调用链（`confirm_trade` 在真实 prompt/模型下）不在本批次。

## Reviewer independence

独立审阅 Agent `nightly-reviewer`（fresh spawn 会话，未参与实现；C 实现者为 Lead）。
本会话未写任何 C 代码/测试/文档；仅读 diff、跑测试，并在 `/tmp` 副本用一次性探针复现 C-1（仓库未改）。
所有数字均来自本会话亲自运行（`./test.sh`、`./test.sh PP_TRADE`、`./test.sh PP_READ` 等，日志路径见上）。
审阅期间工作树被他方并发修改（E/G 字段同步、Lead 收尾与 `7548ddb` 修复），最终按 **HEAD `f7ae62c`**
复核；C 相关代码在 `7548ddb` 中仅经 ActionGate/测试修复间接变化（C-1 复核通过），其余不变。与实现者、E/G
作者为不同 agent/会话，非 self-review。
