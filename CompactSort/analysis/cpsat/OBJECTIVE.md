# CompactSort 目标函数（实现口径）

本文档按**代码实际行为**描述目标函数，不是设计意图。所有行号对应 `src/Packing/`。
CP-SAT 与其他实验都以本文档为准。

## 0. 结论先行

代码里有 **四套互不一致的目标定义**，它们用的系数完全不同：

| # | 名称 | 位置 | 作用 | 系数 |
|---|---|---|---|---|
| 1 | `Evaluate` | GridPacker.cs:1132 | 构造时给**单个落点**打分 | `1500·holes − 7·contact − 4·same + 5·growth + 0.08·dist + 3·rotSteps` |
| 2 | `Score` | GridPacker.cs:1295 | 全局**标量总分** | 见 §2 |
| 3 | `IsTighterThan` | PackModels.cs:230 | **权威比较器**，决定最终提交哪套布局 | 8 级字典序，见 §1 |
| 4 | `AcceptSa` | AdvancedPacker.cs:102 | 模拟退火**接受准则** | `20000·Δholes + 2000·Δcomps + 100·Δgap + 0.5·Δscore` |

另外还有一个更粗的 `PackResult.IsTighter`（PackModels.cs:264），只比较
`holes → components → gap → score`，在搜索循环里当快速判据用。

**只有 #3 决定玩家最终看到什么。** #1 和 #4 是搜索过程中的启发式，不保证与 #3 一致——
这是当前代码里一个真实的隐患（见 §5）。

## 1. 权威目标：`IsTighterThan`（字典序，8 级）

```csharp
holes <          // 死洞：四邻全实心的空格，越少越好
components <     // 空格 4-连通块数量，越少越好
bboxGap <        // 包围盒空隙 = bbox 面积 − 已占格数
ItemClusters <   // 同一 identifier 被拆成几块，越少越好
SameItemContacts >   // 同物品相邻边，越多越好
CategoryContacts >   // 同类别相邻边，越多越好
MaxEmptyRect >       // 最大空矩形面积，越大越好
Score <              // 最后才用标量分
```

前面平了才看后面。`PackResult.PreferMaxRectFirst = true` 时第 3、4 级互换。
**这是字典序，不是加权求和**——也就是说第 1 级差 1 个洞，比后 7 级全部加起来还重要。

## 2. 标量分 `Score`

```
Score = 500·components + 2000·isolated + 6·bboxGap
        − 2·contact + 0.02·distance
        − 20·maxEmptyRect − 6·categoryContacts
        − 10·sameItemContacts + 8·itemClusters
```

- `components` 的系数 500 与 `isolated` 的 2000，已经在前两级字典序里独占决定性地位，
  所以这个标量分实际上被前两项"掏空"了——真正靠它决胜负的只有后面几项。
- `MaxRectWeight`（默认 20）只在这条式子里出现，而 `IsTighterThan` 里 `MaxEmptyRect`
  排在第 7 级。这解释了 `analysis/maxrect_weight_sweep.csv` 为什么 24 行全部一模一样：
  **这个参数在实战中几乎是惰性的**。

## 3. 各指标的精确定义

网格 `W×H`，`Wall[]` 为容器外/不可用格，`Layers[]` 为字节位掩码占用（支持多层堆叠）。

- **`isolated`（死洞）**：空格 `(x,y)` 且上下左右四邻**都是**「墙 或 已占」。
  注意 `IsSolid` 用 `IsWall`，**越界也算实心**，所以贴边的空格只要另两侧被占也会被计成死洞。
- **`components`（空连通块）**：空格在 4-邻接下的连通分量个数。墙不参与，但墙会切断连通。
- **`bboxGap`（包围盒空隙）**：`(maxX−minX+1)·(maxY−minY+1) − 已占格数`。
  bbox 只框已占格，**不含墙**；锚定物品（owner=−2）算已占。
- **`MaxEmptyRect`**：最大的全空轴对齐矩形面积，直方图栈 O(W·H)。
- **`contact`**：已占格与「墙/已占」的相邻边数。实现上右、下两侧算「墙或已占」，
  左、上两侧**只算墙和越界**——正好做到内部邻接只计一次、边界邻接四面都计。
- **`ItemClusters`**：每个 `identifier` 各自的 4-连通块数量再求和。
- **`SameItemContacts` / `CategoryContacts`**：相邻两格 owner 的 key / category 相同的边数，
  只统计 owner≥0（可移动物品）；锚定物品不计。

## 4. 关键事实：前两级已经在所有用例上饱和

把 50 个基准实例的真实布局导出后用 Python 独立复算（`evaluate.py`，
550 次运行、0 处不一致）：

```
holes 总和 = 0     （每个实例都是 0）
components 总和 = 50 （每个实例都是 1）
```

即字典序前两级在**全部 50 个用例上都已经取到理论最小值**。
所以真正的优化空间只剩第 3 级 `bboxGap`，其余是审美性的 tiebreak。

这直接决定了 CP-SAT 怎么建模：前两级可以当硬约束钉死，只对 bbox 面积求极小。

## 5. 不一致之处（待修）

1. `Evaluate` 用 `1500·holes` 但**完全不看 `components`**；构造阶段可以为了接触面
   而切碎空区，到了全局评分才发现连通块变多。
2. `AcceptSa` 的权重（20000/2000/100/0.5）与 `Score`（2000/500/6/…) 比例不同，
   退火"认为的改进"和最终"提交的判据"不是一回事。
3. `Rotate` 惩罚只在 `Evaluate` 里出现（3 分/步），`Score` 里没有——
   朝向统一这个卖点在最终比较里其实没被计分。

## 6. 对应到 CP-SAT

因为 §4，优化问题退化为：

```
minimize  bboxW · bboxH
s.t.      每个物品放一个合法位置（0° 或 90°）
          物品之间不重叠、不出容器、不压墙
          holes = 0，components = 1（已可达成，故最优解必然满足）
```

容器无墙时平移不变，窗口可固定锚在 (0,0)，于是只需枚举 `w·h ≥ 面积` 的窗口。
`cpsat_bound.py` 就是干这个的。
