# PawnMind 迭代问题记录

> 规则：每轮把新发现的问题写在这里；未解决的保留到下一轮，解决后移到“已解决”。
> 当前目标：完成 P1–P5（见 `docs/pawnmind-normal-gameplay-plan.zh-CN.md`）。

## 当前阶段：P1 收尾

### 未解决

| # | 问题 | 证据/现象 | 下一步 |
|---|---|---|---|
| 1 | ~~旋转只支持 90°~~ 已解决 | `turns=0..3`，真机 180/270 通过 | — |
| 2 | ~~重排成功路径未验证~~ 已解决 | 展示柜 7x5 精确摆成 6x4 满块 + 无 2x2 空洞，自动放入 2x2 触发 repack：5 件全部重排成功、无重叠 | — |
| 3 | 异形物品“右下角”按包围盒算，可能有空角 | `PlacementMath.BottomRight` 用 width/height | 形状感知锚点；或文档标注 |
| 4 | `get_layout.rows` 只保留每格最上层 id | 完整层在 `cells` | 文档已标注；如需 3D 再加 `layer_rows` |
| 5 | 机器内部容器（熔炉/水培/打印机等）不在 `list_containers` | 目前只列 8 个玩家容器 | P2/P3 之后按需补 |
| 6 | 补给站/博士/批发目录已接，购买/夜间未验 | `commissary` 真机 7 种；`jackson/wholesale` 门控正确；`commissary_buy` 店内 `cannot_buy` | P3 夜间到店后验购买/博士 |
| 7 | 砍价成功率/走人率已暴露；声望为估算 | `bargain_preview` 真机 50/20/30，`rep_per_1000=-2.48`；未计难度/Perk 最终修正 | 文档标注估算；如需精确再接预览 UI |
| 8 | ~~`sell` 未真机验证~~ 已解决 | 星际客 BUY 日：牙膏移到 back，`confirm_trade(sell)` 成功，cash 520→537、back 清空；`list_goods(sellable)` 已放宽为 owned+unlocked | — |
| 9 | 夜间三地点已通，细节待补 | 真机 go_out/scavenge/return/杰克逊列表；补给站 0 代币 | 代币获取、杰克逊买卖、拾荒伤口 |
| 10 | 阅读已通；多页手册索引硬编码 | 真机 water_guide 4 页；仅 8 本 vanilla 手册，Mod 书走 PaperBookRegistry | 新增手册需补映射 |
| 11 | F7 / `debug_pulse` 日志行未出现在 `Player.log` | dump 文件正常，pulse 无日志 | 切 ModLog 或写文件确认 |
| 12 | 测试档槽位会累积（每次 `new_game` 新建） | 内存/存档列表变长 | 提供测试档清理或复用 |
| 13 | UI 快照只覆盖 `TabletButton` + 标准 `Button` | 开关/输入框/拖拽未纳入 | P2/P3 遇到再补 |
| 14 | ~~事件推送未做~~ 已解决 | `/events` 长轮询 + `wait_for_events`；真机 change/timeout 均通过 | `wait_for_change` 条件仍轮询 |
| 15 | 启动物品全是矩形，异形 mask 未真机验证 | 起始背包 mask 都是全 1 | 遇到枪/工具时补样本 |

| 16 | `end_day` 在部分店态返回 `cannot_end_day` | day2 开店后直接 end_day 失败，sleep 仍可推进 | 补店态判断或文档说明 |
| 17 | 补给站购买缺代币来源 | `redeemed_credit=0` | 接回收/兑换或标注需玩家操作 |
| 18 | 批发成功路径未验 | 真机遇到 `wholesale_offer=true`、折扣 -17%，但该单没有未拥有货物 -> `no_items`；目录已过滤 owned | 遇到有货的批发单再验扣款 |

### P6 新发现

| # | 问题 | 证据 | 下一步 |
|---|---|---|---|
| 19 | `toggle_item` 后 UI 快照为空 | 储藏区 toggle 成功但 `get_ui_snapshot` 无元素 | 补 content window/容器快照 |
| 20 | 标签机目标判定严格 | `use_tool(labeler, 代糖果汁)` -> `cannot_target` | 用带可编辑标签的水/液体测 |
| 21 | 分析仪结果尚无结构化接口 | 只有 UI | P6 下一步 |

### 已解决

| # | 问题 | 解决 |
|---|---|---|
| A | `list_inventory` 的 x/y 全是 0 | 改用 `modifiedShape.minX/minY` |
| B | 布局只有摘要，没有二维图 | `get_layout` 加 `rows/display/symbols/cells` |
| C | 异形物品只给宽高 | 加 `mask`/`cells` |
| D | 同容器内不能重排 | `move_item` 指定坐标时允许同容器 |
| E | 指定格子/旋转未实现 | `move_item(x,y,turns=0..3)` + `GridShapeBuilder` + 回滚 |
| I | 自动摆放失败不会重排 | 内置 `RepackSolver` + 回滚；真机成功重排 5 件 |
| F | 后台线程会被新扫描器拒绝 | HTTP 改主线程轮询，无 Thread/Task/协程 |
| G | F7 全局按键打到浏览器 | 不再注入系统键鼠；调试走 HTTP |
| H | `TryAcceptAll` 非 null 不代表真的移动 | 3x3 移 2x2 暗格曾误报 ok；现在以 `item.parentInventory == target` 判定 |

## 模板（每轮新增）

```markdown
### YYYY-MM-DD / Pn
#### 新发现
- [ ] 问题：现象 / 证据 / 下一步
#| 16 | `end_day` 在部分店态返回 `cannot_end_day` | day2 开店后直接 end_day 失败，sleep 仍可推进 | 补店态判断或文档说明 |
| 17 | 补给站购买缺代币来源 | `redeemed_credit=0` | 接回收/兑换或标注需玩家操作 |
| 18 | 批发成功路径未验 | 真机遇到 `wholesale_offer=true`、折扣 -17%，但该单没有未拥有货物 -> `no_items`；目录已过滤 owned | 遇到有货的批发单再验扣款 |

### P6 新发现

| # | 问题 | 证据 | 下一步 |
|---|---|---|---|
| 19 | `toggle_item` 后 UI 快照为空 | 储藏区 toggle 成功但 `get_ui_snapshot` 无元素 | 补 content window/容器快照 |
| 20 | 标签机目标判定严格 | `use_tool(labeler, 代糖果汁)` -> `cannot_target` | 用带可编辑标签的水/液体测 |
| 21 | 分析仪结果尚无结构化接口 | 只有 UI | P6 下一步 |

### 已解决
- [x] 问题 -> 解决方式 / 测试
#### 留给下一轮
- ...
```
