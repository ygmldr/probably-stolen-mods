# 紧凑整理 / Compact Inventory Sort

针对《深空当铺：可能是偷的》(Probably Stolen) 的整理优化 MOD。
核心目标只有一个：**减少碎缝，让空格尽量连成整块**。

## 它解决什么

原版整理（`InventorySortHelper`）只做两件事：

1. 按“旋转后的高度”排序；
2. 从上往下开货架，能塞进货架就塞，塞不下就开新层。

它只保证“物品放得下”，不保证：

- 同类/同物品聚在一起；
- 朝向统一；
- 短物品上方的空隙被利用；
- 空格不被切成一条条碎缝。

本 MOD 在右键整理菜单里新增独立的“紧凑整理”入口，原版四个按钮保持原样：

- **最大接触**：优先贴墙、贴已有物品；
- **最小包围盒增长**：尽量不把整体轮廓撑大；
- **空洞/凹槽惩罚**：放置时预判会不会制造 1 格死洞；
- **重力收拢**：放完后反复把物品往上/往左挤，直到挤不动；
- **多策略取最优**：大件优先、最大边优先、同物品聚簇、随机扰动等多套顺序，按“死洞 → 空格连通块 → 包围盒空隙 → 接触/距离”的字典序取最好的一套；
- **LNS 毁坏-重建**：盯着空洞/窄缝，拆掉附近 2–4 个物品，强制其中一件覆盖目标空格后再贪心补回；接受更紧的布局，允许少量平移跳出局部最优；
- **单件定向移动**：先尝试把附近一件物品直接移到空洞上，保留其余布局不动；
- **不破坏性重排**：如果新布局在“死洞/连通块/包围盒空隙”上不比当前好，就保留当前布局；
- **可回退**：算完后仍会逐个调用游戏自己的 `CheckShapePlacement` 做最终校验；提交失败会回滚。

## 安全边界

- 只移动非锁定、非残骸、可正常取出的物品；
- 锁定物品、残骸、固定家具保持原位；
- 不销毁、不复制、不修改数量/品质/耐久；
- 结果写回前会验证每个物品的最终位置；
- 写入失败会尝试恢复原布局；
- 会注册原版撤销快照，整理后重新打开右键菜单，撤销按钮可用。

## 安装

游戏版本：正式版 `0.46D`（主菜单 VERSION 100，原生 ModLoader）。

1. 关闭游戏；
2. 把 `CompactInventorySort` 文件夹整个放进：

   `...\steamapps\common\Probably Stolen\Mods\`

   正确层级：

   ```
   Mods\
     CompactInventorySort\
       manifest.xml
       CompactInventorySort.dll
   ```

3. 启动游戏 → 主菜单“模组” → 找到“紧凑整理” → 启用；
4. 首次启用会弹一次代码注入授权，点同意；
5. 如果提示需要重启，按提示重启。

## 使用

右键容器空白处打开整理菜单。菜单里现在有：

```
大件优先（从前往后）
小件优先（从前往后）
大件优先（从后往前）
小件优先（从后往前）
紧凑整理          ← 我们的
撤销
```

- 原版四个按钮**保持原版算法**，不再被替换；
- 点“紧凑整理”才走 Hybrid + 分类聚拢；
- 整理后重新打开菜单，原版“撤销”按钮可用。

## 快捷键

**1.8.7 起全局池和快捷键整理已从游戏内移除。** 当前只保留：

| 快捷键 | 功能 |
|---|---|
| `Ctrl+Q` | 悬停物品：锁定/解锁排序 |
| `F8` | 只读自检，输出 `[CompactSort][TEST]` 日志和屏幕提示 |

设置 → **紧凑整理锁定键** 只保留：

- **快捷键修饰键**：Ctrl / Alt / 无；
- **锁定排序按键**：A–Z、0–9、F1–F12。

`Ctrl+R`、`Ctrl+Shift+R` 不再绑定任何功能。修改后点设置界面的“应用”生效，存 `PlayerPrefs`。

## 锁定排序

- 右键物品菜单里的 Pin 已改名为“锁定排序 / 解锁排序”，和 `Ctrl+Q` 是同一个功能；
- 使用游戏原生 `PINNED_TAG`，原版排序、紧凑整理、拖拽补位都不会移动它；
- Pin 自带的多选/卸载限制也保留；
- 锁定物品在拖拽补位时作为所在容器的固定锚点。

## 全局池整理（已禁用）

**1.8.7 起游戏内不再提供全局池整理入口：**

- `Ctrl+R` 不再触发全局池；
- 设置里不再显示“全局池整理按键”；
- 全局池代码和测试保留，便于以后重新启用，但当前没有任何游戏内调用路径；
- 唯一整理入口是右键“紧凑整理”（单容器）。

历史说明（仅作为代码参考）：全局池曾按 `identifier` 分组，把玩家存储容器放进一个逻辑池重新分配；现在不再在游戏内执行。

## 展示柜 / 垃圾桶 / 桌面

- **拖拽自动补位：全部容器可用**，包括展示柜、垃圾桶、前台/后台/内部柜台；
- 仍然只在“拖拽结束后原版没有完全放入”时触发，不是进入容器就自动整理；
- `Ctrl+R` / `Ctrl+Shift+R`：仍禁用，不会触发全池/逐容器整理；
- 全局池/自动收集：仍然排除展示柜、垃圾桶、三个柜台；
- 暗格不排除，作为普通存储参与右键“紧凑整理”和拖拽补位；暗格里的物品仍然必须守恒；
- 手动右键“紧凑整理”：仍然可用。
- 购买溢出/种植溢出：无父容器的 overflow item 不触发重排；部分堆叠数量保持不变；插入锁定/不可移除物品直接跳过。

## 耗时滑块

```
设置 → 游戏性 → 紧凑整理耗时上限    [====|----] 350 ms
```

- 范围 80–1000ms，默认 350ms，存 `PlayerPrefs`；
- 影响右键“紧凑整理”和拖拽补位的时间预算；
- 调高更容易找紧布局，但整理卡顿更久。

## 为什么有时排序“不动”

1. 容器有不可取出/锁定物品 → `skip: container has unremovable items or no movable items.`
2. 可移动物品 ≤1 → `skip: only N movable item(s).`
3. 分类优先顺序放不下 → 自动回退普通顺序重试：`category-first layout failed; retrying generic order.`
4. 新布局在死洞/连通块/bboxGap 上不更好 → `current layout is already as tight (...)`。
5. 同物品分组改进：新增“直接交换两件物品”的后处理；同物品相邻边增加、洞/连通块不变、gap 最多 +1 时接受。
6. 日志会打印 `same-item` / `category` 两边数值，能直接看出是不是分组目标没提升。

## 1.1.0 的优化效果

在 50 个随机合成容器（10×10 到 14×13，72%–95% 占用）上做 A/B：

- LNS 版：**8 胜、0 负、42 平**；
- 平均分：70.0 → 68.8；
- 总包围盒空隙：228 → 215；
- 最好的一例：空隙 10 → 1，分数 160 → 115；
- 三个固定展示用例（混合/密集/不规则）本身已接近局部最优，LNS 没有进一步改进，但也没有变差。

这些是合成用例，实际游戏仓库/背包的效果还要以游戏内为准。

## 竞品对比

参照对象：[创意工坊 3829206666「智能仓库排列」](https://steamcommunity.com/sharedfiles/filedetails/?id=3829206666)，也是 N 网 691 作者本人维护的版本。

| 能力 | 竞品 智能仓库排列 | 本 MOD 紧凑整理 |
|---|---|---|
| 右键菜单位置 | 新增“智能排列” | 四个原版选项下面新增“紧凑整理” |
| 相同物品成排 | 是 | 是 |
| 同类别聚拢（医疗/食品等） | 主要按相同物品聚拢 | 按目录类别 + identifier 两层聚拢 |
| 原版四按钮 | 保留 | 保留，不替换 |
| 一键整理快捷键 | 有（F6 设置，可选范围） | 已移除（1.8.7） |
| 设置菜单 | 有（F6） | 游戏性设置里的耗时滑块 |
| 保留储物容器/能量阵列 | 有 | 锁定/残骸固定，但不专门识别储物窗口 |
| 拖拽放不下时自动补位 | 未看到 | 有（多选/单件，失败后整容器重排） |
| 最大空格矩形 | 未看到作为指标 | 有（评分项 + 同级 tiebreak） |
| 算法 | 作者自研 | Hybrid：贪心+LNS / Beam+LNS；全局池代码保留但游戏内禁用 |
| 通知/菜单置顶/朝向选项 | 有 | 暂无 |

结论：竞品的**易用性和设置完整度明显更强**；我们的差异点是**分类聚拢、拖拽补位、最大矩形指标和开源算法**。两者都加菜单项，建议二选一，避免菜单重复和整理行为互相干扰。

## 1.9.4 拖拽补位覆盖全部容器

- 展示柜、垃圾桶、前台/后台/内部柜台现在也允许**拖拽补位**；
- 全局池/自动收集仍然排除这些手动区，且 `Ctrl+R`/`Ctrl+Shift+R` 仍禁用；
- 增加溢出安全边界：
  - 无父容器的购买/种植溢出物品不会触发目标容器重排；
  - 只在目标容器内、没有外部 incoming 时不重排；
  - 部分堆叠的 `unitCount` 在补位后保持不变；
  - 插入锁定、不可移除、非法槽位仍然直接跳过；
- 新增适配器回归：`OrphanOverflowItem`、`AlreadyInsideItem`、`PartialStack`。

## 1.9.3 移除将退休的 capability

- 移除 manifest 里的 `dynamic-code` 和 `reflection-external`，只保留快照需要的 `files`；
- 撤销快照改为模组自己保存（`ModUndo`），不再反射 `InventorySortHelper.undoSnapshots`；
- 拖拽/锁定/菜单补丁改用 Harmony 字段注入（`___field`），不再 `AccessTools.Field` / `Method.Invoke`；
- 这样不会再触发 ModLoader 的“下次游戏更新将拒绝此 capability”警告；
- 原版撤销和原版排序不受影响；模组自己的紧凑整理仍可撤销一次。

## 1.9.2 visual-first 候选

- 新增 `PackAlgorithm.VisualFirst`：同 identifier 整组排成一行/一块，默认竖放，从左上按行优先放置；
- 手动“紧凑整理”在低占用时自动比较 gap-first 与 visual-first，gap 最多增加一个随容器大小缩放的小容差；
- 高占用/visual 失败时回退现有 Hybrid，不改变安全边界；
- 真实 90 个容器离线对比：visual-first 的 reading 8.18、waste 3.34，明显优于竞品（36.03 / 16.51），但 gap 13.00 高于我们的 Hybrid 2.10；
- 所以它作为“视觉候选”，不会无条件替代 gap-first。

## 1.9.1 真实快照 benchmark + JSON 修复

- 修复 `SnapshotDumper` 在“前一个容器为空、后一个容器有物品”时写出 `"items":[,` 的逗号 bug；
- 新增 `test/ingame/check_snapshots.py` 校验快照 JSON；
- 已用 72 份真实游戏快照跑 benchmark：`greedy+compact` 对 greedy 为 **69 胜 / 1 负 / 2 平**，平均 gap 24.24 → 12.07，waste 73.38 → 64.43，耗时约 11.8ms；
- 真实数据说明“分配后每容器重排”同时改善 gap 和同物品矩形度，但仍会让 reading/clusters 略变差。

## 1.9.0 阅读顺序与同物品 neatening

- 放置距离从 `x + y` 改为 **row-major** `y * (W + 1) + x`，优先从上到下、再从左到右；
- 新增 `ReadingOrderDisorder`：期望顺序（类别 → identifier → id）与实际行主序的反转数；
- 新增 `SameItemBBoxWaste`：同 identifier 的包围盒内浪费格；
- 新增同 identifier 小组的局体重排：把一组物品在其它物品留下的空间里重新 pack，目标是更接近矩形；
- 这些指标排在 gap 和 cluster 之后，不会为了整齐明显牺牲碎缝/同物品聚拢；
- benchmark 显示 gap 明显改善，但 bbox waste 仍然偏高；下一步需要真实 snapshot 做针对性 neatening。

## 1.8.8 自动快照

- 每次游戏内睡眠/进入新一天时，`StoreStation.StartDay` 之后自动 dump 一份只读 JSON；
- 目录：`%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\CompactSortSnapshots\`；
- 包含仓库/主背包/前后柜台/展示柜/垃圾桶/暗格/雇佣兵等的格子、墙体、物品 key/类别/形状/位置/锁定状态；
- 不修改存档，不影响整理；
- benchmark 已支持 `--snapshot <文件或目录>`，可以直接用这批真实数据分析算法。

## 1.8.7 禁用全局池和快捷键整理

- `Ctrl+R`、`Ctrl+Shift+R` 从游戏中移除，不再绑定任何整理行为；
- 设置 → 紧凑整理锁定键 只保留锁定键和修饰键，不再显示全局池/逐容器按键；
- 全局池和逐容器整理的代码保留（`SortFeatures.GlobalPoolEnabled = false`、`PerContainerSortEnabled = false`），但游戏内没有调用入口；
- 右键“紧凑整理”仍然是唯一排序入口；
- F8 自检新增 `global_pool_disabled`、`per_container_disabled` 检查；
- 暗格参与右键/拖拽整理；展示柜、垃圾桶、三个柜台桌面当时排除（1.9.4 已恢复拖拽补位，全局池仍排除）。

## 1.8.6 暗格重新参与自动整理

- 暗格（`hiddenElement`）不再被 `IsAutoSortBlocked` 排除；
- 暗格会作为普通存储容器加入 `CollectAutoContainers`，参与全局池和逐容器整理；
- 暗格的外层垃圾桶仍然排除，不会把整个垃圾桶卷进来；
- F8 自检改为检查 `hidden_allowed`，有物品时还检查 `hidden_collected`；
- 暗格物品仍然要求守恒、无重叠、可回滚。

## 1.8.5 F8 自检可见反馈

- F8 自检结束后除了写 `Player.log`，还会调用 `StoreUIManager.Notify` 在游戏里弹出“紧凑整理自检 PASS/FAIL N/N”；
- 日志格式不变，`test/ingame/check_selftest.py` 仍可直接解析；
- 只读，不移动物品。

## 1.8.4 全池排除桌面 + 游戏内自检

- 全局池/自动整理现在额外排除 **前台柜台、后台柜台、内部柜台**（`frontInvinvElement`、`backInvinvElement`、`backInvinvElementCounter`），以及垃圾桶、展示柜；**暗格不再排除**；
- 柜台上的物品不会被全局池搬走，也不会被搬上柜台；柜台只能手动“紧凑整理”；
- 新增 **F8 只读自检**：检查真实场景里的容器绑定、排除列表、重叠、越界，输出 `[CompactSort][TEST] ...` 和 `SUMMARY pass=N fail=N`；
- 新增 `test/ingame/check_selftest.py` 和 `test/ingame/press_f8.ps1`，把“进游戏看一眼”变成可判定的日志测试。

## 1.8.3 测试与安全修复

- **LNS 不再丢件**：`TryRepair` 在“强制覆盖空洞”时会把当前顺序位重复跳过，导致拆出的 2–4 件物品少放回 1 件；现在强制件只替换它自己在顺序中的位置，其余拆出件全部放回，回归用例 `Regression_Lns_ReinsertsEveryTornOutPiece`。
- **不完整布局直接失败**：`GridPacker` 在返回前检查“每件物品恰好出现一次”；数量不足/重复时返回失败，而不是把部分布局交给提交层。
- **提交失败返回 false**：排序、拖拽补位、全局池的提交函数现在返回成功/失败；回滚后不再打印“已放置/已整理”的成功日志。
- **包围盒空隙不再把墙体算成空隙**：`BBoxGap` 只统计包围盒内真正可用的空格，非矩形容器的指标更准确。
- **校验器回退**：如果游戏 `CheckShapePlacement` 拒绝了启发式 Top-16 的所有位置，会在合法范围内重新扫描，避免“其实放得下却报无解”。
- 新增纯算法 TDD 套件与 fake inventory 适配器测试；`build.sh` 在打包前强制跑测试。详见 `AGENTS.md` 和 `test/IN_GAME_CHECKLIST.md`。

## 1.7.3 全局池按“同物品组”分配

全局池不再只是一件一件往“同类最多的容器”塞，而是：

1. 先把所有可移动物品按 `identifier` 分组；
2. 同一组（比如四根粉针）优先整组放进一个容器；
3. 容器候选顺序：同类物品最多 → 同 identifier 最多 → 原容器 → 剩余空间最大；
4. 整组放不下，才退化为逐件分配，尽量少拆组；
5. 仍然保持锁定物品原地锚定、展示柜/垃圾桶排除、事务提交/回滚。

日志会显示 `... N identifier groups kept together ...`。

## 1.7.2 拖拽提交重写

- 拖拽补位不再全量 Expel/Accept 目标容器物品，改为原地改形状；仅跨容器的那件物品做父容器转移；
- 提交后做“物品未销毁 + 父容器正确”的守恒检查，失败回滚；
- 撤销快照 `ValueTuple` 泛型修复；
- 全局池校验忽略目标容器原物品，并逐件重试容器；
- 同物品 `ItemClusters` + 直接交换后处理。

## 1.7.1 紧急修复

- **拖拽自动补位默认关闭**，避免继续丢物品；
- 修复撤销快照 `ValueTuple` 泛型未闭合导致 `could not register undo snapshot` 的问题；
- 修复全局池校验：忽略“仍在目标容器里、但马上要搬走”的原物品，避免 `placement invalid for nutrient_tablet`；
- 全局池改为逐件尝试容器，某个容器拒绝该物品时换下一个，而不是整池直接 abort；
- 新增 `ItemClusters`：同一 identifier 被拆成几块；四根粉针拆成 2+2 会算 2 个 cluster，排成 4 根算 1 个；
- 同物品交换/智能入口接受条件里加入 cluster 降低，优先把同类拼成一整块。

### 数据丢失处理

先不要再拖拽自动整理。用游戏内的存档历史/备份加载到丢失前的存档；或者把 `AppData\LocalLow\Questing Goose Studio\Probably Stolen` 整个目录复制一份再操作。

## 1.3.0：最大空格矩形、拖拽补位、分支定界

### 最大空格矩形

用直方图栈算法 `LargestEmptyRectangle` 求当前空格中最大的轴对齐矩形面积（O(W×H)）：

- 作为独立指标 `MaxEmptyRect` 记录；
- 在总分里按 `-20 × 面积` 计入，面积越大分数越低；
- 最终比较在“死洞、连通块、包围盒空隙”都相同的情况下，优先最大矩形更大的布局。

这样不会为了一个漂亮的大矩形把布局重新弄散；如果你希望“大矩形优先于包围盒空隙”，可以再切成另一个模式。

### 拖拽补位（1.7.2 改为原地提交）

1.7.1 曾默认关闭；1.7.2 改了提交方式后重新开启：

- **目标容器里原有的物品不再被批量 Expel / UncheckedAccept**；
- 它们只改 `modifiedShape`，父容器不变，不触发增减回调；
- 只有真正从外面拖进来的物品才做一次“从源容器 Expel → 目标容器 Accept”；
- 提交后逐个检查：物品未被销毁、父容器 == 目标容器，否则回滚；
- 理论上消除了“几十件物品全量搬家”导致的丢失路径。

仍然建议第一次用时备份存档，先拿一个小容器试；如果还丢，立刻关游戏并发日志，我再按物品级事务继续修。

拖拽结束后，如果普通放置只放进了一部分，后置钩子会：

1. 取出目标容器里所有可移动物品；
2. 加上还没放进去的拖拽物品；
3. 用 Hybrid 重新打包整个容器；
4. 全部合法且验证通过才提交；失败则保持原样。

覆盖：

- 多选拖拽 `ItemMultiSelectHandler.EndGroupDrag`；
- 单件拖拽 `ItemMouseDragHandler.EndDrag`；
- 只处理 `GameGridInventory`（普通格子容器），暂不处理 `alwaysStack` 滚动容器；
- 单件拖拽如果只拖了部分堆叠（`currentRemove < unitCount`）会跳过，避免拆堆错误；
- 拖拽补位不注册撤销快照，并会清掉该容器旧快照，避免跨容器撤销把物品搬错；
- **1.7.1 默认关闭**，等修好并验证后再考虑开启。

### 分支定界

`PackAlgorithm.BranchAndBound` 是限时 DFS：

- 用贪心结果做 incumbent；
- 物品按面积从大到小；
- 每层展开前几个候选；
- 用“剩余物品总面积 ≤ 剩余空格”做可行性剪枝；
- 用当前分数超过 incumbent + 600 做启发式截断。

50 个随机容器上的结果：**3 胜 / 7 负 / 40 平，平均 351ms**，不如 Hybrid，所以只保留为实验分支，不做默认。

## 1.2.0 多算法对比

在 50 个随机合成容器（10×10–14×13，72%–95% 占用）上，用 1.1 的贪心+LNS 作为基线：

| 算法 | 胜/负/平 | 平均分 | 平均 bboxGap | 平均耗时 |
|---|---|---|---|---|
| Greedy | 0 / 9 / 41 | 70.0 | 4.6 | 4.7ms |
| Greedy+LNS（1.1） | 0 / 0 / 50 | 68.8 | 4.3 | 19.8ms |
| Beam | 8 / 16 / 26 | 68.6 | 4.2 | 18.4ms |
| Beam+LNS | 11 / 10 / 29 | 66.7 | 3.9 | 32.8ms |
| **Hybrid（1.2 默认）** | **11 / 0 / 39** | **64.0** | **3.2** | **51.5ms** |
| Hybrid+SA | 12 / 0 / 38 | 64.0 | 3.2 | 228.9ms |
| SA | 6 / 0 / 44 | 67.7 | 4.1 | 198.1ms |
| GA | 4 / 4 / 42 | 68.6 | 4.3 | 25.7ms |
| 分支定界 | 3 / 7 / 40 | — | — | 351ms |

- **Hybrid** = 先跑贪心+LNS，再跑 Beam+LNS，取更紧的一套；不会比 1.1 差。
- **Beam / GA** 单独用会输给 1.1，所以只作为候选分支保留。
- **Hybrid+SA** 只比 Hybrid 多赢 1 例，但耗时 4 倍多，不做默认。
- 大容器（30×30、100 件）上 Hybrid 会先拿贪心结果保底，再让 Beam 用剩余预算，避免搜索超时导致失败。

## 全局池 benchmark

`analysis/global_bench/` 是一个不依赖 Unity 的跨容器分配 benchmark，用来比较 greedy、regret、每容器重排等策略。

```bash
analysis/global_bench/run.sh --selftest
analysis/global_bench/run.sh --cases 100 --seed 20261104
```

100 个合成 case 的结果：`greedy+compact` 对当前 greedy 为 **97 胜 / 2 负 / 1 平**，平均 bboxGap 41.17 → 22.65，耗时约 15ms。

72 份真实游戏快照的结果：`greedy+compact` 为 **69 胜 / 1 负 / 2 平**，bboxGap 24.24 → 12.07，same-item bbox waste 73.38 → 64.43。详细口径和 CSV 见 `analysis/global_bench/README.md`。

## 兼容性

- 和“智能仓库排列 / Smart Inventory Arrange (Nexus 691)”这类同样在整理菜单里加菜单项的 MOD **二选一**，避免菜单重复和整理行为互相干扰；
- 不依赖 MelonLoader；
- 只处理当前打开/右键的容器，不做跨容器一键整理。

## 限制

- 容器接近全满（>95%）时，可调整空间有限，可能判定“当前布局已经足够紧”而保持不变；
- 单次整理有时间预算（约 350ms），超大容器可能提前停止并回退原版；
- 只尝试“当前朝向”和“转 90°”两种朝向，避免物品上下颠倒或触发状态校验失败；
- 不会按品类/价值/品质排序——这一版只优化空间碎缝；
- LNS 只做 2–4 件物品的局部拆补，不做全局回溯/精确求解；已经接近最优的容器可能没有变化。

## 从源码构建

需要 Windows 上的 .NET SDK（Roslyn csc）和游戏本体的 `Managed` 目录。

```bash
./build.sh
```

输出：

```
dist/CompactInventorySort/
  CompactInventorySort.dll
  manifest.xml
dist/CompactSort-v1.8.3.zip
```

## 测试与 TDD

项目规则见 [`AGENTS.md`](AGENTS.md)：任何行为改动先写失败测试，再实现。测试命令：

```bash
./test.sh                 # 纯算法 + fake inventory 适配器测试
./test.sh BBoxGap         # 只跑名字匹配的用例
```

- `test/CompactSort.Tests/`：不依赖游戏的纯算法测试（放置合法性、守恒、严格占用、墙体、类别/同物品分组、LNS 回归、指标）；
- `test/CompactSort.AdapterTests/`：用 fake `GameGridInventory`/`GameItem` 驱动真实 `CompactSorter`，覆盖整理守恒、锁定、拖拽补位、提交失败回滚；
- `test/PackerTest.csproj`：旧的压力/基准测试，保留用于算法对比，不属于 TDD 门禁；
- `test/IN_GAME_CHECKLIST.md`：Unity 运行时路径的人工验收清单；
- `test/ingame/`：F8 只读自检 + 日志解析器（`check_selftest.py`）/窗口激活脚本（`press_f8.ps1`）。

`build.sh` 会先运行 `./test.sh`，测试失败不会生成安装包。纯算法测试仍不能替代游戏内验收；拖拽、暗格/展示柜/桌面等行为按清单验证。

游戏内自检：

```bash
# 进入店内后按 F8，或：
powershell -ExecutionPolicy Bypass -File test/ingame/press_f8.ps1
python3 test/ingame/check_selftest.py
```
