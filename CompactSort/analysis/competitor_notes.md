# Smart Inventory Arrange 反编译笔记

对象：Steam Workshop `3829206666`，`SmartInventoryArrange.Mono.dll`，版本 `3.17.42-mono.1`。
许可证：MIT（随包 `LICENSE`）。仅用于行为/API 对比，不直接复制代码。

## 1. 结论

它不是 RL，也不是单一贪心；核心是**视觉优先的多候选启发式**：

- 20 级字典序评分；
- 按容器占用率切换 `aesthetic / balanced / dense` 三套排序；
- 同 identifier 组成 **atomic block**，整块放置；
- 同类别/同形状组成 shelf cohort，整行排；
- 默认竖放，空间不足才横放；
- Beam / Shelf / FirstFit / MaxRects / 原布局多个候选取最优；
- 全部不合法时回退原布局。

它和我们的核心差别：

```text
竞品：type runs / row alignment / orientation consistency 在前
我们：holes / components / bboxGap 在前
```

这解释了为什么我们的可乐虽然 cluster 在一起，但不够“整齐”。

## 2. 评分顺序

`Core/LayoutScore.cs` 是 20 个字段的 `IComparable`：

```text
placedCount
placedArea
negativeTypeRuns          // 同类型区域越少越好
sameTypeRowAlignment      // 同类型同一行
sameTypeHorizontalContact // 同类型水平接触
negativeCohortRuns        // 同 cohort 区域越少越好
cohortRowAlignment
cohortHorizontalContact
orientationConsistency
verticalPreference
priorityOrderAlignment
categoryOrderAlignment
largeFirstAlignment
sameTypeContact
sameCategoryContact
topLeftCompactness
rowRegularity
negativeFragments
negativeInterleaving
negativeRotations
stableTie
```

注意：**没有 bboxGap 这一项**。它主要优化“看起来整齐”，而不是“包围盒最小”。

## 3. Density mode

`DeterministicSolver.DensityMode`：

- occupancy < 0.80：`aesthetic`
- 0.80–0.92：`balanced`
- >= 0.92：`dense`

`aesthetic` 更重视 type/cohort rows 和视觉顺序；`dense` 才把 fragments/edges 提前。

我们在 50%–65% 占用时仍然 gap-first，方向就反了。

## 4. AtomicSkylineBlocks

同 identifier 先组成一个 `AtomicBlock`：

- 组内物品按 portrait 方向排列；
- 整块尝试放在 skyline 的候选位置；
- 候选按 category distance、bottom、holes、edge 排序；
- beam 保留 24 个 block 状态；
- aesthetic 模式下如果 `NegativeTypeRuns == 0`，直接采用该完整布局。

这直接解决“四罐可乐被逐件摆成阶梯”的问题。

## 5. Shelf / portrait

- `PreferredOrientations` 先按 `height > width`（竖放），再按高度差、是否旋转排序；
- `FindWholeRowPlan` 先尝试把整个 cohort 排成一行；
- 失败后退化为逐件 shelf placement；
- `portraitOnly=true` 时只允许竖放，`allowHorizontal` 才接受横放。

竞品即便允许旋转，也把**竖放**作为强偏好。

## 6. 多候选

`DeterministicSolver.Solve` 至少尝试：

- atomic skyline（portrait / any）
- original layout
- greedy shelf groups（portrait / horizontal）
- greedy first fit（左/右）
- greedy max rects × 5 heuristics × 3 种物品顺序
- improve complete layout（如果有原布局）

然后按 `CompareForProblem` 选最优；没有完整候选时回退原布局。

## 7. 对新版 Native34 的观察

`Native34Solver` 进一步做成增量搜索：

- 种子：`shelf / compact / unit-tail` × 8 种顺序；
- 保留最多 256 个候选；
- 支持 session/batch 继续搜索；
- 仍然用 `LayoutScore` 做最终排序。

所以它比我们当前 350ms 单轮 Hybrid 更重，但视觉优先级非常明确。

## 8. 我们可以借鉴的部分

1. **视觉优先候选**
   - 低占用（<80%）不要用 gap-first；
   - 增加 `aesthetic` 评分顺序。

2. **同 identifier atomic block**
   - 先把一组同物品排成整块/整行，再放置；
   - 放不下才拆。

3. **竖放优先**
   - 同 identifier 选主朝向；
   - 旋转只在空间不足时使用；
   - 增加 `OrientationConsistency`。

4. **type/cohort row alignment**
   - 同类型/同形状的 cohort 尽量整行；
   - 增加 `NegativeTypeRuns`、`RowStagger`。

5. **density-aware**
   - 低占用：视觉优先；
   - 高占用：碎缝/空间优先。

6. **多候选 + 原布局回退**
   - 至少保留“当前布局”候选；
   - 任何新候选不优时不动。

## 9. 不能直接照搬

- 竞品代码量远大于我们，包含撤销、设置、插入、迁移、序列化等；
- `Native34Solver` 有增量搜索/候选池，不适合直接塞进 350ms；
- 我们先做“visual-first 单容器候选”，不要直接抄整个 solver。
