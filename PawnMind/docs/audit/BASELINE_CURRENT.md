# BASELINE_CURRENT — PawnMind 当前基线（Round 3 / 第一批）

> 采集时间：2026-11-17 02:40 (Asia/Shanghai)
> 采集方式：在本机仓库直接执行命令；未运行游戏，因此所有实机项标 `NOT_RUN`。
> 本文件是**冻结快照**，不是长期结论；提交后若 HEAD 前移需重新采集。

---

## 1. 仓库

| 项 | 值 | 命令 |
|---|---|---|
| HEAD（采集时） | `f68f60e` | `git rev-parse HEAD` |
| 分支 | `master` | `git branch --show-current` |
| 工作树 | 3 个改动（本轮策略文件，未提交） | `git status --porcelain` |
| 远程 | 未推送（提交只在本地） | — |

---

## 2. 游戏与构建

| 项 | 值 | 证据 |
|---|---|---|
| 游戏 | *Probably Stolen* 0.46D | `bridge.conf` |
| 期望版本字符串 | `VERSION 100-Rev13` | `ModData/pawnmind/bridge.conf` |
| 期望程序集 MVID | `911508a6-e0eb-4cc0-91e8-e14b16ea28d7` | 同上 |
| **运行中实际 MVID** | **`NOT_RUN`** | 游戏未启动，`/health` 不可达 |
| Mod manifest 版本 | `0.2.1` | `Mods/PawnMind/manifest.xml` |
| git tag | `v1.0.1`（与 manifest 不一致，历史遗留） | `git tag` |
| `dist/PawnMind/PawnMind.dll` | `ad335987…`，296,960 B，2026-11-17 01:58 | `sha256sum` / `stat` |
| **已安装** `Mods/PawnMind/PawnMind.dll` | `e9e1bd07…`，295,424 B，2026-11-16 23:32 | `sha256sum` / `stat` |
| 一致性 | **不一致**（差 1,536 B） | 上方两行 |

> **发布/录像前置**：重装前，任何真机现象都不能归因到当前 commit。

---

## 3. 工具面（真实 `tools/list`）

| 项 | 值 |
|---|---|
| full | **114** |
| recording | **104** |
| 差集 | 10 |
| 快照文件 | `PawnMind/docs/audit/tools-inventory.json`，sha256 `fa5d71bf…` |
| `get_operation` / `wait_operation` | **不存在**（计划新增，未计入） |
| `readOnlyHint == null` | **114 / 114**（因此不能作为分类依据） |

---

## 4. 策略基线（本轮新增）

| 项 | 值 |
|---|---|
| 声明式策略源 | `PawnMind/agent/policy/tool_policy.json`，sha256 `4e1ce849…` |
| 读取器/生成器 | `PawnMind/agent/tool_policy.py` |
| 条目数 | 114（与 full 一一对应） |
| 决策分布 | `allow` 72 / `needs_evidence` 31 / `deny` 11 |
| 纯度分布 | `pure` 52 / `write` 48 / `privileged` 10 / `cached_impure` 3 / `mutating` 1 |
| 模式可导出 | bootstrap **53** / play **72** / test **114** |
| **`live_ready`（有真机证据）** | **0** |
| `get_state` | `cached_impure` + `needs_evidence`，play = 禁止（D0a 待办） |
| `bargain_preview` | `mutating`，`deny` |
| `get_clientele` / `barter_status` | `cached_impure`，`needs_evidence` |
| 特权工具（`save_game`/`new_game`/`act`/`press`/…） | `privileged`，`deny` |

**结论：`play` 模式当前不可启用**——`live_ready` 为空，且 `get_state` 未过 D0a。

---

## 5. 测试基线（采集时实跑）

| 层 | 命令 | 结果 |
|---|---|---|
| C# 纯逻辑 + 脚本门禁 | `cd PawnMind && ./test.sh` | `passed=656 failed=0`；`contract gate tests: passed=53`；`adapter contract OK` |
| 适配器契约 | 同上 | `contract checks: 491`（只断言成员存在） |
| Python | `.venv/bin/python -B -m unittest discover -s agent/tests` | `Ran 246 tests … OK` |
| 真机 UI 差分 | — | `NOT_RUN`（无 UI 输入驱动） |
| Mod Scanner | — | `NOT_RUN`（本环境无官方扫描器） |

---

## 6. 守卫矩阵基线

| 项 | 值 |
|---|---|
| 文件 | `PawnMind/docs/audit/PLAYER_ENTRY_MATRIX.csv` |
| 行数 | 62（58 write + 4 `read_impure`） |
| status | `fixed` 25 / `open` 33 / `partial` 2 / `unverified` 2 |
| B 类 43 条 | `fixed` 10（B1–B8、B20、B22，提交 `3d85963`）/ `open` 33 |
| 命中 B 缺口的 op | 23 / 58 |

---

## 7. 未验证项（`NOT_RUN` / `UNKNOWN`）

1. 运行中游戏 MVID、版本、`/health`。
2. Mod Scanner（官方规则在 2026-12-10 可能收紧，需重新读 `APIChanges.md`）。
3. `get_state` 各 getter 的副作用全集（只核实了 `CanOffer*` 与 `CanReport`）。
4. DSH 对 `ok:false + status:pending` 的处理。
5. `dist` 与已安装 DLL 的差异来源（只知道哈希/大小不同）。
6. 任何真机负向用例（`P0-LANDLORD-02`、`P0-OPEN-01`、`PP-READ-*`、`PP-TRADE-*`）。
