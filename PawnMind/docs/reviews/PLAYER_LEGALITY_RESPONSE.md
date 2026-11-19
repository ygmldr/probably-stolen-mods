# PLAYER_LEGALITY_RESPONSE — 对《玩家合法操作最终审查》的逐条核对

> 核对对象：`PAWNMIND_PLAYER_LEGALITY_AUDIT_2026-11-19.md`
> 被核对基线：`r5.1/fixes` @ `158d6a7`（与审计声明一致）
> 核对方式：**只读源码 + 现有测试 + 矩阵 CSV**，未修改任何代码、未启动游戏。
> 判定口径：**成立 / 部分成立 / 不成立 / 需实测**。凡我自己没能复现的，一律不判"成立"。

---

## 0. 总评

这份审计的**结构性判断是对的**：PawnMind 目前缺的不是更多工具，而是一条"AI 可执行的写操作必须逐项通过玩家等价验收"的闸门。
它点名的四类问题里，**F1、F2、F4 的核心指控成立**，F3 大部分成立，但有几处措辞与事实不符（见 §3）。

最重要的三条（我独立确认）：

1. `move_item` 的自动路径会 fallback 到 `TryRepack`，**一次调用可以重排整个柜台**；
2. `buy_wholesale` / `buy_upgrade` / `set_label` **直接改经济与物品状态**，不走原生 UI 业务路径；
3. `recording` 工具面仍然包含这些操作，且 `modes.test=true` —— `play_allowlist=[]` 只在 Play 模式生效，
   **test 模式的 pilot 并不受它保护**。

---

## 1. 逐条核对

### F1 `move_item` 缺少合法性闭环

| 子项 | 判定 | 证据 |
|---|---|---|
| 自动路径 fallback 到 `TryRepack`（多物品重排） | **成立** | `GameActions.cs:220-222` 调用 `TryRepack`；`:280-330` 证实它把**该网格上所有非固定商品**收集起来重新摆放（逐件 `Expel` + 重新定位），不是单件移动 |
| 半指定坐标被当成"自动放置" | **成立** | `GameActions.cs:213`：`bool specific = x >= 0 && y >= 0;` —— `x=-1,y=999` 走 auto 分支 |
| 带坐标路径**缺少**边界/占格校验 | **不成立** | `GameActions.cs:243-252` 有 `shape.minX/minY/maxX/maxY` 边界检查 + `grid.CheckShapePlacement(...)` |
| 跨容器用 `grid.UncheckedAccept` | **成立** | `GameActions.cs:263` |
| 成功后只查 `parentInventory` + `ValidateShapeState`，无独立几何回读 | **成立** | `GameActions.cs:266-267` |
| `GetContainer` 能取 `sold`/`afterhour`/`vending`/`fountain`/`hireling`，`MoveItem` 不验可达性 | **成立** | `GameState.cs` 容器映射；`MoveItem` 无可见性/窗口前置 |
| `RepackSolver` 只有 4 组几何测试 | **成立** | `test/PawnMind.Tests/RepackSolverTests.cs` 实测 4 个用例 |

**结论**：F1 的**两条致命点**是「auto→TryRepack 多物品副作用」和「半指定坐标被静默降级」。
至于"坐标路径没有校验"——**审计这一条写错了**，原生 `CheckShapePlacement` 确实被调用了。
真正的缺口是**事后没有独立回读**（不是事前没校验）。

### F2 业务动作直接写状态

| 操作 | 判定 | 证据 |
|---|---|---|
| `buy_wholesale` 直接 `store.playerCash -= price` + 内部 `OnItemBought`/`MoveDealItemToInventory` | **成立** | `GameActions.cs:164-172` |
| `buy_upgrade` 直接扣 `playerCash` + `upgrade.Unlock()` | **成立** | `UpgradeService.cs:62` |
| `set_label` 直接写 `fakeCondition` + `SetExposed(false)` | **成立** | `LabelService.cs:78-79` |
| `barter_accept` / `arrest_wanted` 与玩家入口不一致 | **无法确认** | `P9ActionService.cs` 中未匹配到审计描述的直接写法，需要按 op 名逐个定位 |

**结论**：前三条**成立且严重**——这三条是"AI 能做到玩家做不到的事"的直接证据（跳过 UI 的回执/对话/冷却）。

### F3 其他写路径与 `legal_actions`

| 子项 | 判定 | 证据 |
|---|---|---|
| `machine_insert/collect` 只用内部 slot `TryAcceptAll` | **未核实** | 本轮未展开到 `MachineService.cs`；不臆断 |
| `wine_add_water` 的 policy 已是 `denied` | **成立** | `tool_policy.json`：`wine_add_water` = `denied/deny`（但 `modes.test=true`，见 F4） |
| `legal_actions` 里的 `confirm_trade` 不经过 `TradeGuardPolicy` | **成立** | `StateSnapshot.CollectTradeItems`（`:203-213`）只按 `IsOwned` 分类，不查预算/谈判/守卫 |

`legal_actions` 这条**我在实机上已经遇到过同一类问题**并修了一半：
2026-11-19 的 L3 发现 `dismiss_current_client` 明明可用却不在 `legal_actions` 里
（`fd45c24` 之前的构建）。审计说的方向（"legal_actions 不能等同可安全执行"）是对的，
而且它指出的 `confirm_trade` 缺口**比已修的更严重**：实机里 `client_budget_insufficient`
的拒绝就是从这里来的——列表说能做，守卫说不能。

### F4 工具面没有强制约束

| 子项 | 判定 | 证据 |
|---|---|---|
| `recording` 面包含 `move_item`/`buy_wholesale`/`machine_insert`/`use_tool` | **成立** | 我从 `mcp_server.py` 独立数出：107 项，逐个命中 |
| `move_item` 等 `status=open, decision=needs_evidence, modes.test=true` | **成立** | `tool_policy.json` 实测 |
| `play_allowlist=[]` 不能保证 test-mode pilot 安全 | **成立** | 策略里 test 模式对所有 `modes.test=true` 的操作放行 |

**结论**：这是四条里**最有行动价值**的一条。它不要求重写任何业务逻辑，只要求一条**在桥入口强制的 AI 写操作 allowlist**。

### 矩阵统计

审计的数字**与 CSV 完全一致**（我独立统计）：

```
63 行 = 59 write + 4 read_impure
write:   fixed 25 / open 31 / partial 2 / unverified 1
read:    open 3 / unverified 1
```

---

## 2. 审计没提、但我在核对中发现的

1. **`legal_actions` 与守卫不一致是系统性的**，不止 `confirm_trade`：`StateSnapshot.BuildFlags`
   只用"便宜"的原生字段，而真正的写守卫（`TradeGuardPolicy`、staging guard）在 Bridge 层。
   两份判断在架构上就是分开的 → 会持续产生"列表说能做、执行被拒"。
2. **`move_item` 的 `sameContainer` 分支没有回滚路径**：`item.modifiedShape = ...` 之后直接
   `ValidateShapeState()` 并返回成功，全段没有 try/catch；一旦校验抛异常，形状已被改写且不恢复。
   跨容器分支有回滚，同一容器没有。
3. **`stage_negotiation_item` 现在也有 `modes.test=true`**，但它今天刚在实机上拿到两三笔
   LIVE 证据；按审计的标准它仍属 `open`，不该因为"实机能用"就升 Play。

---

## 3. 我不同意或需要补证的地方

| 审计表述 | 我的核对 |
|---|---|
| "带坐标路径没有以最终真实位置/所有占格做独立成功判据 → 调用内部捷径" | **事前校验存在**（边界 + 原生 `CheckShapePlacement`）。准确说法应是"**事后**缺独立回读" |
| "用户所报『物品部分越界仍放置成功』" | 我在源码里**找不到**能产生该结果的路径：坐标路径有边界检查，auto 路径走原生 `TryAcceptAll`。**要么是 `TryRepack` 求解器与原生 shape 不一致，要么该现场没有留下证据。**这一条必须先复现再改，不能凭描述改代码 |
| "`barter_accept`、`arrest_wanted` 直接写状态" | 未在 `P9ActionService.cs` 匹配到对应写法，**需按 op 名定位** |

---

## 3.5 执行状态（2026-11-20 01:30）

用户批准"第一批修复"后已完成：

| 阶段 | 状态 | 提交 |
|---|---|---|
| A1 `move_item` 玩家等价闸门（坐标成对 / 去 TryRepack / 同容器回滚 / 容器可达） | **已完成** | `fed3690` |
| `buy_wholesale` / `buy_upgrade` / `set_label` 移出 AI 面 | **已完成**（recording 107→104） | `fed3690` |
| A3 自主 pilot 的显式写操作 allowlist | **已完成**（`--tools pilot`，75 = 55 读 + 20 写） | 本次提交 |
| A2 容器白名单 | **已随 A1 完成** | `fed3690` |
| B Day-1 核心集逐项八字段审计 | **未做** | — |
| C 实机负例 | **未做**（需一次专门的真机时段） | — |

**A3 的做法说明**：AI 的唯一入口是 MCP（模型没有裸 HTTP 通道），所以把闸门放在 MCP 面
并用 `--tools pilot` 显式收窄，而不是在桥里再加一套并行协议——审计原文也要求
"不要新增并行一套协议或新的审计系统"。`pilot` 面之外的工具对模型完全不存在。

## 4. 如果接受这份审计：最小修复顺序与工作量

按"堵真实漏洞优先、不扩张基础设施"的原则，我建议：

| 阶段 | 内容 | 粗估 |
|---|---|---|
| A1 | `move_item`：坐标必须同时给/同时省（否则拒绝）；去掉 auto 路径的 `TryRepack`；`sameContainer` 失败也要回滚 | 0.5 天，可单测 |
| A2 | `move_item`：目标容器白名单（只允许玩家可触及的容器），`sold`/`afterhour`/机器内部一律拒绝 | 0.5 天 |
| A3 | 桥内强制 **AI 写操作 allowlist**（默认只开 Day1 核心集），与 `play_allowlist` 分离；test 模式仍可在显式调试开关下放开 | 1 天 |
| B | Day1 核心集 11 项逐个按八字段审计（`PLAYER_ENTRY_MATRIX.csv` 已有五列底子） | 2–3 天 |
| C | 实机负例（越界/重叠/锁定/满格/不可达容器），每项要求 `dispatched=false` 且状态不变 | 0.5 天 + 你的授权 |

**不建议**按审计原文一次性审完 59 项——那会拖成一周，而且大部分操作在 Day1 pilot 里根本用不到。

---

## 5. 本报告未做的事

- 没有修改任何代码（用户要求只出核对报告）。
- 没有跑实机负例。
- `machine_insert/collect`、`barter_accept`、`wine_*` 等只做了抽查，未逐行确认。
- 59 项 × 八字段的完整审计未做。

**一句话回应审计的验收标准**：
"模型不能通过 PawnMind 完成任何真人在当前 UI 下不能完成的动作" —— 我同意，
而且在现有代码里**至少有三条已经违反**（`buy_wholesale`、`buy_upgrade`、`set_label` 直接改状态）。
这三条今天就该在 AI 工具面关掉，不需要等 59 项审完。
