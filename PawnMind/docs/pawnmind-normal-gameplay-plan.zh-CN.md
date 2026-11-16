# PawnMind 正常经营能力计划（v0.4 → v0.5）

> 目标：按用户确认的 7 项正常玩法，补齐“看得见、买得到、卖得出、搬得动、出得去、读得了”。
> 约束：不做视觉鉴定；TDD；所有动作走游戏正常逻辑；不读隐藏字段。

## 0. 总顺序

| 阶段 | 内容 | 产出 | 验收 |
|---|---|---|---|
| P0 | 游戏内调试快捷键 + 诊断 ✅ 2026-11-10 | `F7` 打一条状态脉冲；`Shift+F7` 把 `/state` 写到 ModData | 代码/单测完成，待真机按键验证 |
| P1 | 背包收尾：指定格子 + 旋转 + 排序兜底 | `move_item(item_id, target, x, y, rotate)`；自动摆放失败时调 CompactSort 重试 | 纯校验测试 + 真机移动/旋转/拒绝 |
| P2 | 买卖目录 + 声望预估 | `list_goods(scope)`、`buy/sell`、`bargain` 返回成功率/价格/声望增减 | 真机买一件、卖一件、一次砍价 |
| P3 | 夜间三地点 | `list_locations`、`go_out`、`scavenge`、`commissary_list/buy`、`return_home` | 真机跑拾荒场 + 补给站 + 博士 |
| P4 | 阅读 | `list_books`、`read_book`、`read_item` | 真机读一本已解锁书 + 一件物品详情 |
| P5 | 回归与交付 | 全量测试、真机清单、README/能力矩阵 | 3 个游戏日不崩、账目对得上 |

## P0 调试快捷键

- `F7`：输出一行诊断（rev/day/shop/cash/client/legal 数量/最近动作）
- `Shift+F7`：把完整 `/state` JSON 写入 `ModData/pawnmind/debug-state.json`，用 `ModHelper.WriteModDataFileText`（不需要 `files` 能力）
- 实现位置：`MainThreadPump.Update()` 轮询 `Input.GetKeyDown`，不新建线程/协程（兼容新扫描器）
- 纯逻辑：`HotkeyLatch`（边沿 + 冷却）单测
- 冲突：F8 是游戏控制台、F9 是开发菜单，避开

## P1 指定格子 + 旋转

- 研究 `GameInventory.TryInventorySlot` / `CheckShapePlacement` / `GridShape` 变换
- 纯逻辑：`PlacementValidator`（边界、重叠、锁定、容量、旋转后尺寸）
- 适配器：
  - `x/y` 给定：先校验，再 `UncheckedAccept` + 写回坐标；失败回滚
  - `rotate`：旋转 `modifiedShape` 后重新校验
  - 自动摆放失败：调用 CompactSort 的整理入口（按容器），再重试一次
- 验收：移动一件到指定空格；旋转一件放下；占用格/锁定物品被拒绝

## P2 买卖目录 + 声望

- `list_goods(scope=client|commissary|jackson|wholesale)`：统一 `{id,name,price,stock,owned,source}`
- `buy(item_id, qty)` / `sell(item_id)` / `sell_all`
- `bargain(direction, percent)` 返回：
  - 预计成交价（`GetPreBargainValue` + 档位）
  - 成功率/走人率（`GetBargainSuccessChance` / `GetBargainLeaveChance`）
  - 声望增减（`SetRepPreview` + tooltip，或按 wiki 公式：倍率 × 2.5 × 阵营系数）
- 约束：每方向一次；客户预算封顶；不自由定价

## P3 夜间三地点

- `AfterhourLocation.GetDiscovered()` → `list_locations`
- `MapUIManager.Travel(location)` / `Return()` → `go_out` / `return_home`
- `ScavHelper.CanScavenge/ScavengeDumpingGrounds/GetScavTimeLeft` → `scavenge`
- `CommissaryUIManager` → `commissary_list/buy`
- `DrJacksonLocation` → 复用 P2 的买卖目录（scope=jackson）
- 约束：地面有物品不能回店；受伤/空手；事件关闭地点

## P4 阅读

- `PaperBookRegistry` / `PaperBookData` → `list_books`（只列已拥有/已解锁）
- `read_book(book_id, page)` 返回结构化文字
- `read_item(item_id)` 返回名称/描述/flavor/已知标签
- 不做图片/视觉；不读隐藏鉴定答案

## P5 回归

- 全量 `./test.sh` + 适配器契约 + Python 8 项
- 真机清单：3 个游戏日、买/卖/搬/夜间/阅读各一次
- README 能力矩阵与限制
