# Global pool benchmark

用于比较**跨容器全局整理**策略的纯 C# benchmark。它不依赖 Unity 或游戏进程，直接编译 `src/Packing/`，用合成容器/物品跑多组固定 seed 的用例。

> 当前正式版仍禁用全局池（`SortFeatures.GlobalPoolEnabled = false`）。这个项目只用于实验和选型，不会自动改游戏行为。

## 1. 场景模型

每个 case 包含 3–5 个容器：

- `10x10`、`18x4`、`24x10`、`12x8`、`16x6`；
- 随机墙格；
- 可选锚定物品（1x1/2x2）；
- 40 件左右物品，总占用约 50%–65%；
- 纯矩形 + 少量 L 形；
- 5 个类别，类别内按 `identifier` 生成重复 key；
- 每件物品有随机原容器。

生成器固定 seed，所以结果可复现。

## 2. 当前比较的算法

| 名称 | 说明 |
|---|---|
| `greedy` | 当前生产版全局池策略的纯模型：按 identifier 分组、类别/同 key/原容器/空间排序、整组 first-fit，放不下再逐件 |
| `greedy+compact` | 同上，分配完后对每个容器做一次短预算 packer 重排 |
| `visual-first` | 单容器视觉候选：同 identifier 成行、竖放优先、行主序；见 `analysis/competitor_bench` |
| `regret` | 分组/单件都用 regret insertion，优先处理“第二选择很差”的组 |
| `regret+compact` | regret 分配 + 每容器重排 |

每容器重排使用：

- `PackAlgorithm.Greedy`
- 类别优先
- 同物品交换后处理
- 每容器 120ms 上限
- 类别优先失败时回退普通顺序

## 3. 指标

每个 case 记录：

- `success`：是否所有物品都分配成功；
- `ms`：算法耗时；
- `items` / `bins`：参与物品数 / 使用容器数；
- `holes`：死洞；
- `components`：空格连通块；
- `bboxGap`：包围盒空隙；
- `clusters`：同 identifier 的连通块数量；
- `sameItem` / `category`：同物品/同类别接触边；
- `score`：packer 的标量分；
- `error`：失败原因。

比较使用与游戏相同的字典序：

```text
holes → components → bboxGap → clusters → sameItem → category → maxRect → score
```

## 4. 运行

```bash
cd <repo>/CompactSort

# 自检（守恒 + 合法性）
analysis/global_bench/run.sh --selftest

# 100 个固定 seed case
analysis/global_bench/run.sh --cases 100 --seed 20261104 \
  --out analysis/global_bench/results-100.csv
```

或直接：

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run \
  --project "D:/code/game_mods/CompactSort/analysis/global_bench/GlobalBench.csproj" \
  -c Release -- --cases 20
```

参数：

```text
--cases N
--seed N
--bins N
--items N
--algorithms greedy,greedy+compact,regret,regret+compact
--out path.csv
--selftest
```

## 5. 当前结果（100 cases，seed 20261104）

| 算法 | 成功 | 平均 ms | holes | components | gap | totalGap | reading | waste | clusters |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| greedy | 100/100 | 0.5 | 0.02 | 3.53 | 41.17 | 4117 | 227.09 | 89.13 | 19.87 |
| **greedy+compact** | **100/100** | **15.0** | **0.01** | **2.64** | **22.65** | **2265** | 202.85 | 214.60 | 23.68 |
| regret | 100/100 | 30.9 | 0.04 | 3.98 | 46.45 | 4645 | 180.46 | 81.50 | 19.59 |
| regret+compact | 100/100 | 44.1 | 0.01 | 2.99 | 22.88 | 2288 | 163.23 | 166.58 | 23.22 |

对 greedy 的字典序胜负：

```text
greedy+compact   win=97 loss=2 tie=1
regret           win=34 loss=66 tie=0
regret+compact   win=80 loss=20 tie=0
```

### 结论

1. **最大收益仍然是“分配后每容器重排”**；
   - `greedy+compact` 的 gap 从 41.17 降到 22.65，耗时约 15ms。
2. **row-major 距离和 neatening 改变了视觉顺序，但还不是免费午餐**；
   - reading 从 227 降到 202；
   - waste 从 89 升到 214，说明当前 compaction 的主要目标仍是 gap，对同物品矩形度的改善有限。
3. **regret+compact 的 reading/waste 更好，但 gap/components 略差**；
   - 如果视觉整齐比 0.2 gap 更重要，它值得作为 neatness 候选；
   - 当前字典序仍让 greedy+compact 赢。
4. **同 identifier 被拆散仍是主要问题**；
   - 需要更强的 group-aware compaction（不仅平均分，还要组内矩形/朝向/行基线）。
5. **不要现在上 RL**；
   - 简单 compaction 已经带来 18 点 gap 改善；
   - 真实 snapshot 还没有积累，先做数据。

## 6. 真实游戏快照结果（72 份）

使用 `SnapshotDumper` 在游戏内睡眠后导出的 72 份快照（已修复 1.8.8 的 JSON 逗号问题）：

| 算法 | 成功 | 平均 ms | holes | components | gap | totalGap | reading | waste | clusters |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| greedy | 72/72 | 0.4 | 0.01 | 2.47 | 24.24 | 1745 | 261.86 | 73.38 | 24.74 |
| **greedy+compact** | **72/72** | **11.8** | **0.00** | **1.88** | **12.07** | **869** | 265.21 | 64.43 | 25.03 |
| regret | 72/72 | 47.6 | 0.10 | 2.58 | 28.57 | 2057 | 288.60 | 73.71 | 24.81 |
| regret+compact | 72/72 | 58.3 | 0.00 | 1.90 | 10.08 | 726 | 284.92 | 76.46 | 25.15 |

对 greedy 的字典序：

```text
greedy+compact   win=69 loss=1 tie=2
regret           win=33 loss=39 tie=0
regret+compact   win=65 loss=7 tie=0
```

真实数据的关键变化：

- `greedy+compact` 同时改善 gap（24.24 → 12.07）和 same-item bbox waste（73.38 → 64.43）；
- reading 和 clusters 略差，但幅度不大（261.86 → 265.21，24.74 → 25.03）；
- `regret+compact` gap 更低（10.08），但 reading/waste 更差，且耗时 58ms；
- 所以“分配后重排”在真实场景同样成立，下一步可以做 visual-first 版本与它对比。

运行：

```bash
python3 test/ingame/check_snapshots.py
analysis/global_bench/run.sh --snapshot analysis/global_bench/real_snapshots \
  --out analysis/global_bench/real-results.csv
```

## 7. 局限

- 合成数据，不是游戏真实容器；
- 没有建模“容器只能主储藏区 / 机器只能主储藏区或机器区”；
- 没有模拟 Unity 的 `CheckShapePlacement`、所有权、可取出限制；
- 每容器重排是短预算 Greedy，不是完整 Hybrid；
- `regret` 的评分没有把重排后的质量作为反馈；
- 墙格和锚定物比例较简单。

## 8. 真实游戏场景对接

`--snapshot` 已经支持读取从游戏导出的 JSON（文件或目录）：

```json
{
  "version": 1,
  "bins": [
    {
      "id": 0,
      "kind": "main_storage",
      "width": 10,
      "height": 10,
      "wall": "0000...",
      "anchors": [
        { "id": 1, "key": "locked_rifle", "category": "Weapons", "width": 2, "height": 2, "x": 0, "y": 0 }
      ]
    }
  ],
  "items": [
    {
      "id": 10,
      "key": "medkit",
      "category": "Meds",
      "width": 2,
      "height": 2,
      "mask": "1111",
      "originalBin": 0,
      "allowedKinds": ["main_storage", "machine_area"]
    }
  ]
}
```

游戏端由 `SnapshotDumper` 在每次 `StoreStation.StartDay` 后自动写一份到：

```text
%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\CompactSortSnapshots\
```

一次 sleep 一份，不会修改存档。再用：

```bash
analysis/global_bench/run.sh --snapshot "C:/Users/<user>/AppData/LocalLow/Questing Goose Studio/Probably Stolen/CompactSortSnapshots" --cases 0
```

就可以对真实布局跑同一套 allocator。

## 9. 后续优先级

1. 用 `SnapshotDumper` 的真实存档数据扩充 case；
2. 加容器类型/物品类型约束（主储藏区/机器区）；
3. 做 cluster-aware compaction，减少同 identifier 被拆散；
4. 再考虑跨容器 LNS / regret 与 compaction 的联合优化；
5. RL 放到最后：先有稳定 benchmark 和真实数据，再做 contextual bandit/learning-to-rank 更合适。
