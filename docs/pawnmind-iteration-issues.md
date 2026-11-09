# PawnMind 迭代问题记录

> 规则：每轮把新发现的问题写在这里；未解决的保留到下一轮，解决后移到“已解决”。
> 当前目标：完成 P1–P5（见 `docs/pawnmind-normal-gameplay-plan.zh-CN.md`）。

## 当前阶段：P1 收尾

### 未解决

| # | 问题 | 证据/现象 | 下一步 |
|---|---|---|---|
| 1 | ~~旋转只支持 90°~~ 已解决 | `turns=0..3`，真机 180/270 通过 | — |
| 2 | 重排成功路径未真机强制触发 | 失败路径已验：3x3 移入 2x2 暗格返回 `no_space`、物品留在 main；`RepackSolver` 单测覆盖碎片/满/阻挡/旋转 | 下一轮构造碎片容器验证成功重排 |
| 3 | 异形物品“右下角”按包围盒算，可能有空角 | `PlacementMath.BottomRight` 用 width/height | 形状感知锚点；或文档标注 |
| 4 | `get_layout.rows` 只保留每格最上层 id | 完整层在 `cells` | 文档已标注；如需 3D 再加 `layer_rows` |
| 5 | 机器内部容器（熔炉/水培/打印机等）不在 `list_containers` | 目前只列 8 个玩家容器 | P2/P3 之后按需补 |
| 6 | 补给站/博士/批发目录已接，购买/夜间未验 | `commissary` 真机 7 种；`jackson/wholesale` 门控正确；`commissary_buy` 店内 `cannot_buy` | P3 夜间到店后验购买/博士 |
| 7 | 砍价成功率/走人率已暴露；声望为估算 | `bargain_preview` 真机 50/20/30，`rep_per_1000=-2.48`；未计难度/Perk 最终修正 | 文档标注估算；如需精确再接预览 UI |
| 8 | `sell` 未真机验证 | 只验证过 buy；`sellable` 目录当前为空（该顾客 SELL-only） | P2 |
| 9 | 夜间三地点全部未实现 | 无 `list_locations/go_out/scavenge/...` | P3 |
| 10 | 阅读（书/物品详情）未实现 | 无 `list_books/read_book` | P4 |
| 11 | F7 / `debug_pulse` 日志行未出现在 `Player.log` | dump 文件正常，pulse 无日志 | 切 ModLog 或写文件确认 |
| 12 | 测试档槽位会累积（每次 `new_game` 新建） | 内存/存档列表变长 | 提供测试档清理或复用 |
| 13 | UI 快照只覆盖 `TabletButton` + 标准 `Button` | 开关/输入框/拖拽未纳入 | P2/P3 遇到再补 |
| 14 | 事件推送未做（仍轮询） | `wait_until` 是 polling | P5 后评估 |
| 15 | 启动物品全是矩形，异形 mask 未真机验证 | 起始背包 mask 都是全 1 | 遇到枪/工具时补样本 |

### 已解决

| # | 问题 | 解决 |
|---|---|---|
| A | `list_inventory` 的 x/y 全是 0 | 改用 `modifiedShape.minX/minY` |
| B | 布局只有摘要，没有二维图 | `get_layout` 加 `rows/display/symbols/cells` |
| C | 异形物品只给宽高 | 加 `mask`/`cells` |
| D | 同容器内不能重排 | `move_item` 指定坐标时允许同容器 |
| E | 指定格子/旋转未实现 | `move_item(x,y,turns=0..3)` + `GridShapeBuilder` + 回滚 |
| F | 后台线程会被新扫描器拒绝 | HTTP 改主线程轮询，无 Thread/Task/协程 |
| G | F7 全局按键打到浏览器 | 不再注入系统键鼠；调试走 HTTP |
| H | `TryAcceptAll` 非 null 不代表真的移动 | 3x3 移 2x2 暗格曾误报 ok；现在以 `item.parentInventory == target` 判定 |

## 模板（每轮新增）

```markdown
### YYYY-MM-DD / Pn
#### 新发现
- [ ] 问题：现象 / 证据 / 下一步
#### 已解决
- [x] 问题 -> 解决方式 / 测试
#### 留给下一轮
- ...
```
