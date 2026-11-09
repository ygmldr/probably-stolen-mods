# 竞品 head-to-head（离线）

对比对象：`SmartInventoryArrange.Mono.dll` 3.17.42（MIT，Steam Workshop 3829206666）反编译出的 `SmartInventoryArrange.Core.DeterministicSolver`。

> 只用于算法/行为对比，不复制竞品代码到 MOD。

## 方法

- 输入：`analysis/global_bench/real_snapshots` 的 72 份真实游戏快照；
- 只取**无锚定物、至少 2 件物品**的 90 个容器（大多为展示柜，主背包大多有锚定物被跳过）；
- 双方都在同一个空网格上从零排；
- 我方：`GridPacker.Hybrid`，CategoryFirst，350ms；
- 竞品：反编译 `DeterministicSolver`，BeamWidth=32，CandidatesPerState=24，350ms；
- 使用同一套 `GridPacker.ScoreLayout` 指标比较。

命令：

```bash
dotnet run --project analysis/competitor_bench/CompetitorBench.csproj -c Release -- \
  --snapshot "D:/code/game_mods/CompactSort/analysis/global_bench/real_snapshots" \
  --limit 200
```

## 结果（90 bins）

| 算法 | 平均 ms | holes | components | gap | reading | waste | clusters |
|---|---:|---:|---:|---:|---:|---:|---:|
| 我方 Hybrid | 49.8 | 0.00 | 1.00 | **2.10** | 44.23 | 10.83 | 7.22 |
| 我方 visual-first（88/90） | 0.0 | 0.28 | 1.56 | 13.00 | **8.18** | **3.34** | **6.59** |
| 竞品 deterministic | 25.9 | 0.29 | 1.67 | 13.83 | 36.03 | 16.51 | 7.71 |

- 按我们当前字典序：**我方 Hybrid 89 胜 / 竞品 0 胜 / 1 平**；
- visual-first 在 88/90 个容器成功；失败的两个容器回退 Hybrid；
- visual-first 的 reading/waste/clusters 都优于竞品，但 gap 明显高于 Hybrid；
- gap 单项：我方 81 胜，竞品 0 胜，9 平；
- 竞品 **reading 更好**的有 31/90，waste 更好 14/90。

## 解读

1. 竞品确实是 **visual-first**：reading 平均值明显更低；但它用 gap/components 换视觉。
2. 我们的 Hybrid 在 gap 上优势非常大，但这不代表“更好看”；
3. 这次对比**没有包含锚定物/固定物**，也主要是展示柜；主背包多数因锚定物被跳过；
4. 竞品这里用的是 `DeterministicSolver` 核心，不是工坊实际可能使用的 `Native34Solver` 增量搜索；
5. 所以这是一个**算法倾向**的对比，不是完整游戏体验结论。

## 下一步

- 把锚定物作为 `Fixed` 加入竞品问题，做更公平的 head-to-head；
- 用同一批 snapshot 比较 `visual-first` 候选（我们还没实现）与竞品；
- 游戏内同场景截图/人工验收仍然必要。
