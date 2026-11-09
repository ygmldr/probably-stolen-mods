# PawnMind 已知限制（2026-11-10）

> 这份清单随迭代更新；每条都对应代码里的实际行为，不是“以后可能”。
> 当前游戏版本 0.46D / 100-REV6，其他版本未验证。

## 1. 布局 / 形状

- `list_inventory` / `get_item` 的 `x/y/width/height` 是**包围盒**；真实占格看 `mask` 和 `cells`。
- `get_layout` 的 `rows` 是每格**最上层**的 item id；同一格不同 layer 的完整占用在 `cells`（含 layer）里。
- `get_layout` 只支持格子容器（main/front/back/showcase/hidden/afterhour）；垃圾桶/已售这类槽位容器返回 `not_a_grid_container`。
- `move_item` 的“右下角”按包围盒算；异形物品可能视觉上有空角。
- 旋转支持 0/90/180/270（`turns=0..3`）；异形旋转后的包围盒/锚点仍按 bbox。
- 指定位置放置失败会回滚；但“自动摆放失败 → 调 CompactSort 重排再试”还没接。
- 机器内部容器（熔炉/水培/打印机等）没有纳入 `list_containers`。

## 2. 买卖

- 只验证过“顾客卖 → 玩家买”；`sell` 代码存在但未真机验证。
- 商品目录只覆盖顾客前台/后台物品；补给站（Commissary）、博士（Dr. Jackson）、批发（wholesale）还没接。
- 卖价只能走砍价面板 10/25/50/75，不支持自由定价。
- 砍价接口返回成功/失败，但**成功率、走人率、声望增减**还没暴露。
- 批量买/卖（buy_all/sell_all）未实现。

## 3. 夜间

- `list_locations` / `go_out` / `scavenge` / `commissary_*` / `return_home` 全部未实现。
- 拾荒的受伤/空手/时间限制、事件关闭地点都未接。

## 4. 阅读

- `list_books` / `read_book` / `read_item` 未实现。
- 只计划支持已拥有/已解锁的书；不读隐藏鉴定答案。

## 5. 视觉 / 鉴定

- 明确不做截图/多模态；不调用 `AppraisalHelper.GetCorrectBaseValue`。
- 鉴定相关只保留文本级物品详情。

## 6. 调试 / 兼容

- F7 只在游戏窗口聚焦时生效；`debug_pulse` 的日志行这次没出现在 `Player.log`（`debug_dump` 文件正常）。
- 不注入系统键鼠；所有自动化走 HTTP。
- 全主线程非阻塞 HTTP，无 Thread/ThreadPool/Task/协程；已避开新扫描器的 `threads` 退役项。
- `load_game` 只支持菜单读档；不会主动读玩家存档列表。
- 测试档会新建存档槽；旧档不会被修改。

## 7. 测试覆盖

- C# 纯逻辑 + 适配器契约 + Python 8 项；真机只覆盖了“新档第 1 天：开店/叫客/对话/买一件/睡觉/移动/旋转/布局”。
- 未覆盖：多顾客、砍价、卖出、夜间、阅读、跨多日、异常回滚、Mod 组合。
