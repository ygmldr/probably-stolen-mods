# ROUND3_REPORT — 第一批交付报告

按任务书 §7 第一批执行：**只做审阅 + S0/S2 静态原型**，未改业务逻辑，未启用录制。

---

## 1. 任务与决策

| 项 | 值 |
|---|---|
| Task ID | ROUND3-BATCH1 |
| Decision | `ACCEPT`（整体）/ 2 条 `REJECT` / 9 条 `NEEDS_EVIDENCE` |
| Status | 第一批完成；`strict_player` **未启用**（`live_ready = 0`） |
| HEAD before | `f68f60e` |
| HEAD after | `2bfdc30`（策略与三段文档）；本报告为其后一提交 |
| 工作树 | 提交后干净 |

---

## 2. 修改文件

| 文件 | 类型 | 说明 |
|---|---|---|
| `PawnMind/agent/policy/tool_policy.json` | 新增 | 114 条声明式策略（purity/status/decision/modes/evidence/reason） |
| `PawnMind/agent/tool_policy.py` | 重写 | 读取器 + `allowlist(mode)` + `live_ready` + `validate()`；未知默认 deny |
| `PawnMind/agent/tests/test_tool_policy.py` | 重写 | 12 条：结构/覆盖/未知/读分类/get_state/特权/集合一致性/证据 |
| `PawnMind/docs/audit/BASELINE_CURRENT.md` | 新增 | 基线冻结 |
| `PawnMind/docs/reviews/ROUND3_DISPOSITION.md` | 新增 | 逐项处置 |
| `PawnMind/docs/audit/STRICT_ALLOWLIST_DECISIONS.md` | 新增 | 114 个工具逐个 allow/deny/needs_evidence |
| `PawnMind/docs/reports/ROUND3_REPORT.md` | 新增 | 本报告 |

**未改**：`src/` 下任何 C# 文件、`mcp_server.py`、`play/`、`dist/`、安装目录、任何存档。

---

## 3. 源码证据（本轮核实，非转述）

| 断言 | 证据 |
|---|---|
| 写动作的 rev 门禁会构建快照 | `src/Bridge/ActionRouter.cs:198` → `:468 TryRefreshRevision` → `src/Bridge/StateSnapshot.cs:18 BuildJson` |
| 快照构建调用谈价 getter | `StateSnapshot.cs:88`、`:181`（`GetBargainSnapshot`）、`:302/:310`（`SafeCanReport`） |
| getter 首行即 `Refresh()` | `_decomp/BargainUIManager.cs:660-663`（`CanOfferDiscount`）、`:1103-1126`（`Refresh` 仅在 `trackedClient != client` 时重置） |
| 未送达响应会被改写成 unknown | `src/Bridge/ActionRouter.cs:244-287`（`_gate.Replace`） |
| `act` 会重新派发且无模式判定 | `src/PawnMindMod.cs:239-248` |
| 录制调度守卫只覆盖部分入口 | `src/Bridge/RecordingDispatchGuard.cs:20-110` |
| `sleep` 复用 `CanEndDay` | `src/Bridge/RecordingSaveService.cs:254` → `src/Core/DayEndPolicy.cs:31-41` |

---

## 4. 测试（原始命令与结果）

```
$ cd PawnMind && .venv/bin/python -B -m unittest discover -s agent/tests
Ran 246 tests in 134.084s
OK

$ cd PawnMind && ./test.sh
passed=656 failed=0
contract gate tests: passed=53 failed=0
adapter contract OK
contract checks: 491
```

| 用例组 | 结果 |
|---|---|
| `test_tool_policy.py`（12 条） | **PASS** |
| `PP-TOOLS-01/02` | **PASS**（集合一致性、未知默认拒绝） |
| `PP-TOOLS-03` | **PASS**（open 缺陷工具为 `needs_evidence`，不进 play） |
| `PP-MODE-01/02` | `NOT_RUN`（需 C# ModePolicy） |
| `PP-READ-01/02/03` | `NOT_RUN` |
| `PP-TRADE-01..04` | `NOT_RUN` |
| `PP-ASYNC-01/02`、`PP-SAVE-01` | `NOT_RUN` |
| `P0-LANDLORD-02`、`P0-OPEN-01` | `NOT_RUN` |
| `PP-RECORD-01`、`PP-MODSCAN-01` | `NOT_RUN` |
| `PP-PLAY-01` | `NOT_RUN` |

---

## 5. 游戏证据（缺失，如实标注）

| 项 | 值 |
|---|---|
| 游戏版本（期望） | `VERSION 100-Rev13` |
| 运行中 MVID | **`NOT_RUN`**（游戏未启动） |
| `dist` DLL | `ad335987…`，296,960 B |
| 已安装 DLL | `e9e1bd07…`，295,424 B |
| 一致性 | **不一致** → 任何真机结论都不可归因当前 commit |
| 存档槽 | 未触碰；录制未启动 |
| 视频/日志 | 无 |

---

## 6. 模式与安全效果（before → after）

| 项 | before | after |
|---|---|---|
| 策略来源 | Python 代码内联集合 | 声明式 `tool_policy.json`（sha256 `4e1ce849…`） |
| 读工具分类 | 54 个读被标成 `write`（`readOnlyHint` 全 null） | `pure` 52 / `write` 48 / `cached_impure` 3 / `mutating` 1 / `privileged` 10 |
| 未知工具 | 隐式按 `write` 放行 | **默认 deny**，未登记的工具进不了任何模式 |
| `get_state` | 静默从 strict 消失 | `d0a_pending` + `needs_evidence`，有明确重新准入条件 |
| 开放缺陷的写工具 | 仍在 strict（`bargain`/`buy_wholesale`/`move_item`） | `needs_evidence`，不进 `play` |
| play（strict_player） | 名义可用 | **不可启用**：`live_ready = 0` |
| C# 侧模式校验 | 部分（debug/存档） | **未改**（本轮只给设计与插入点） |

---

## 7. 剩余阻塞项

| # | 阻塞 | 缓解/下一步 |
|---|---|---|
| 1 | `get_state` 未过纯度（D0a） | S1：快照路径去掉 `CanOffer*`/`CanReport`；`PP-READ-01/02` |
| 2 | C# 无 `ModePolicy` | S2：`DispatchAction` 入口 + `act` 展开后二次校验（`PP-MODE-01`） |
| 3 | 二进制不一致 | S0：`./build.sh` → 安装 → `/health` 核对（`PP-RECORD-01`） |
| 4 | 33 个 open B 缺口 | S3/S4 按主路径清；非主路径禁用 |
| 5 | 无真机证据 | 所有 `PP-*`/`P0-*` 保持 `NOT_RUN`，不得宣称已通过 |
| 6 | Mod Scanner 未跑 | `PP-MODSCAN-01`；2026-12-10 规则变化需重读 |

---

## 8. 下一阶段唯一推荐起点

**S1（D0a）**：把 `CanOffer*`/`CanReport` 移出快照构建路径，用 Core 层可测的"快照源"抽象
断言 `Refresh()` 调用数为 0，然后接真机 `PP-READ-01/02`。
理由：它是 `play` 模式的硬前置，且**不依赖真机就能验证一半**；同时它解除 `get_state` 的准入阻塞，
让 S2 的 ModePolicy 有可放行的读入口。

在此之前不得启用录制、不得发布、不得宣称 strict 可用。
