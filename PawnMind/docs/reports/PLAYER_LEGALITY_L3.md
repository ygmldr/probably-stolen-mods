# PLAYER_LEGALITY_L3 — move_item 玩家等价负例实测（2026-11-20）

> 目的：证明 R5.3/A1 的闸门在**真实游戏**里确实挡住了非法移动，且被拒时没有副作用。
> 构建：`c7c8eaf6…`（含 A1 修复）；安装 SHA 与 `dist/` 逐字节一致。
> 环境：Windows + *Probably Stolen* 0.46D，`VERSION 100-Rev13`，MVID `911508a6-…`，
> policy `774fbfdd…`，`run_mode=test`。
> 存档：槽 56（专用新槽）。物品：`13 合成肉 2×2`、`15 代糖果汁`、`16 牙膏` 等。
> 授权：用户 2026-11-19 授权"修完之后可以自己开游戏进行调试，走实机"。

---

## 1. 结论

| 用例 | 期望 | 实测 | 结论 |
|---|---|---|---|
| 半指定坐标 `x=0`（无 y） | 拒绝、不派发 | `rejected / partial_coordinates / dispatched=false` | **PASS** |
| 半指定坐标 `y=2`（无 x） | 拒绝、不派发 | 同上 | **PASS** |
| 目标 `sold` | 拒绝、不派发 | `rejected / container_not_reachable / dispatched=false` | **PASS** |
| 目标 `afterhour` | 拒绝、不派发 | 同上 | **PASS** |
| 同容器且无坐标 | 拒绝、不派发 | `rejected / same_container_needs_coordinates / dispatched=false` | **PASS** |
| 坏旋转 `turns=9` | 拒绝、不派发 | `rejected / bad_rotation / dispatched=false` | **PASS** |
| **容器放不下（旧代码会触发 TryRepack）** | `no_space`，**其他物品一格不动** | `failed / no_space`；`hidden` 里原有的 13 仍在 `(0,0)` 2×2 | **PASS** |
| 正例对照：`16 → back` | 成功 | `completed` | **PASS** |

**全部 6 个负例都是 `dispatched=false`**，即原生调用根本没发生——这是"拒绝必须没有副作用"的最强形式。

---

## 2. 关键用例：repack 不再偷偷发生

`hidden` 容器是 2×2。先把 2×2 的「合成肉(13)」放进去占满，再尝试放「代糖果汁(15)」：

```
move 13 -> hidden: completed
move 15 -> hidden: failed no_space
hidden 布局: {'13': 'A'}  items: [(13, x=0, y=0, 2x2)]
item 15 位置: inventory（未移动）
```

旧实现里这一步会 fallback 到 `TryRepack`：它把网格上所有非固定商品收集起来，
逐件 `Expel` + 重新定位，再决定能不能塞进去。也就是说**一次 `move_item`
可能重排整个柜台**——一个玩家动作做不到的事。现在它只返回 `no_space`，
原有物品的容器与坐标完全不变。

---

## 3. 没有覆盖到的（明确记录）

| 审计要求的负例 | 状态 | 原因 |
|---|---|---|
| 超左/右/上/下边界 | **NOT_RUN** | 需要精确构造一个"算出来越界"的坐标请求；本轮容器都很空，人工构造成本高 |
| 旋转后越界 | **NOT_RUN** | 同上 |
| 与已有物品重叠 | **NOT_RUN** | 同上（`CheckShapePlacement` 已在代码路径上，但没有实机反证） |
| 多层占格冲突 | **NOT_RUN** | 需要特定叠放物品 |
| 锁定/固定物品 | **NOT_RUN** | 本轮存档里没有 pinned 物品 |
| 来源在 `sold`/机器内部且窗口不可访问 | **部分**：`sold`/`afterhour` 作为**目标**已被拒；作为**来源**未测 | 需要构造那种状态 |
| 目标网格满 | **PASS**（第 2 节） | — |

审计里提到的"用户所报物品部分越界仍放置成功"：**本轮未能复现**。
坐标路径有 `shape.min/max` 边界检查 + 原生 `CheckShapePlacement`，
自动路径走原生 `TryAcceptAll`。要么是 `TryRepack` 的 2D 求解器与原生 shape
不一致（该路径已删除），要么该现场没有留下可复现的输入。**建议保留为 OPEN**，
等有具体复现步骤再查。

---

## 4. 代码与测试对应

| 修复 | 单测 | 实机 |
|---|---|---|
| 坐标必须成对 | `MoveItemPolicyTests.Half_Given_Coordinates_Are_Refused` | 第 1 节前两行 |
| 容器可达性 | `Unreachable_Container_Is_Refused` / `Player_Visible_Containers_Are_Reachable` | `sold` / `afterhour` |
| 同容器需坐标 | `Same_Container_Without_Coordinates_Is_Refused` | 同名字典 |
| 去 TryRepack | 代码删除 + `RepackSolver` 仅存于 Core | 第 2 节 |
| 同容器回滚 | 代码（try/catch restore） | **NOT_RUN**（该分支需要"改形状后校验抛异常"，离线无法构造） |
