# CP-SAT 最优性差距测量

测量 CompactSort 的装箱结果距离**理论最优**还有多少。结论见 `OBJECTIVE.md`。

## 结论（50 个基准实例）

| 指标 | 总 bboxGap | 平均 |
|---|---|---|
| 游戏内实际（Hybrid，固定种子 20261101） | 190 | 3.80 |
| 9 个算法逐一跑再取最好（oracle portfolio） | 185 | 3.70 |
| 有合法解见证的可达值 | 166 | 3.32 |
| 纯几何下界 | 163 | 3.26 |

- **50 个实例中 43 个，当前启发式在数学上就是最优的**：给定物品总面积 A 和容器
  W×H，不存在面积更小的包围盒窗口。这不是"大概最优"，是面积算术的硬结论，
  任何算法都改不动。
- 有 7 个实例存在更紧的窗口，其中 **6 个给出合法见证**（holes=0, components=1），
  合计可回收 **24 格**空隙，占总量 12.6%。
- 现有 9 个算法加起来只吃到其中 5 格——说明瓶颈不在搜索强度。

## 文件

| 文件 | 作用 |
|---|---|
| `OBJECTIVE.md` | 目标函数的实现口径（四套定义 + 精确公式） |
| `dumper/` | C# 导出器，编译 `src/Packing/*.cs`，导出 50 个真实实例与全部布局 |
| `instances.json` | 导出的实例 + 每个算法的指标与坐标（644 KB） |
| `evaluate.py` | 用 Python 独立复算指标并与 C# 对照（550 次运行 0 不一致） |
| `cpsat_bound.py` | CP-SAT 求最小包围盒窗口，给出 bboxGap 下界 |
| `cpsat_bounds.json` | 逐实例的下界结果 |
| `witness.py` | 对下界比现有解更好的实例，验证见证布局是否满足死洞/连通约束 |

## 复现

```bash
# 1) 导出实例（需要 Windows .NET SDK；WSL 里用完整路径调 dotnet.exe）
cd dumper
"/mnt/c/Program Files/dotnet/dotnet.exe" build -c Release -v q
"/mnt/c/Program Files/dotnet/dotnet.exe" bin/Release/net9.0/InstanceDump.dll ../instances.json

# 2) 校验指标
python3 evaluate.py

# 3) 求下界
python3 -m venv /tmp/cpsatenv && /tmp/cpsatenv/bin/pip install ortools
/tmp/cpsatenv/bin/python cpsat_bound.py --time 5

# 4) 验证见证
/tmp/cpsatenv/bin/python witness.py
```

## 为什么下界是可靠的

1. `bboxGap = bbox面积 − 已占面积`，已占面积是常数，所以最小化 bboxGap ⇔ 最小化 bbox 面积。
2. 容器无墙时平移不变，因此"存在 w×h 的包围盒"⇔"所有物品能装进某个 w×h 窗口且 w≤W, h≤H"。
3. 若 `[A, A+当前最好gap)` 区间内**不存在任何可行的 w·h 乘积**，则当前解的最优性是
   面积算术的直接推论，与具体摆放无关——`cands 0` 的那些行就是这种情况。
4. 反之若 CP-SAT 找到更小的可行窗口，它只是一个**下界**：可能违反 holes=0 或连通约束，
   所以必须再跑 `witness.py` 复验，通过才算真实可达。

## 局限

- 基准是合成实例（1×1–4×4 纯矩形、72%–95% 占用、无墙格）。真实容器有墙、
  锚定物品和 L 形物品，`bboxGap` 的算术结构会不一样，结论不能直接外推。
- CP-SAT 只建模几何，不建模多层堆叠（`CellLayer`）语义。
- 前两级（holes/components）在基准上全饱和，所以本测量**只覆盖了第 3 级字典序**。
