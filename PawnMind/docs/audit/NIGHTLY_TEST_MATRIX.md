# NIGHTLY_TEST_MATRIX — Nightly Batch A 回归项状态

> 规则：空白不等于 PASS。没有真实运行记录的一律 `NOT_RUN`。
> 层级：`L0-static`（源码/纯逻辑）· `L1-fake`（假实现/假 HTTP）· `L2-integration-no-game`（真 loopback socket，无游戏）· `L3-real-game`。
> 本表在 2026-11-17 04:00 更新；只有 A 模块的条目已被真实运行覆盖。

## A — 模式策略（已完成离线部分）

| ID | 层级 | 覆盖用例 | 结果 | 证据 |
|---|---|---|---|---|
| `PP-MODE-01a` | L0 | `RunModeTests.Missing_Run_Mode_Fails_Closed` / `Invalid_Run_Mode_Fails_Closed` / `BridgeConfig_Parses_And_Roundtrips_The_Mode` | **PASS** | 缺失/空白/非法值 → `Locked` + 稳定错误码；拼写错误不被重新序列化覆盖 |
| `PP-MODE-01b` | L0+L2 | `RunModeTests.Bootstrap_Plan_Excludes_Snapshot_Readers`、`EndpointGateTests.Bootstrap_State_Is_Refused_Before_The_Provider_Runs` / `Bootstrap_Events_Is_Refused_Before_The_Provider_Runs`、`test_tool_policy.test_bootstrap_never_reads_the_snapshot` | **PASS** | 6 个 MCP 别名被 bootstrap allowlist 排除；裸 `GET /state`、`GET /events` 返回 403 且快照构建计数 0 |
| `PP-MODE-01c` | L0 | `ActionRouterTests.Mode_Refusal_*`（断言 `refreshRevision` 计数 0）、`RunModeTests.Play_Mode_Stays_Closed_Without_Live_Evidence` | **PASS** | Play 在无 live evidence 时允许集为空；`save_game`/`bargain_preview` 在刷新前被拒 |
| `PP-MODE-01d` | L0 | `RunModeTests.Test_Mode_Requires_Explicit_Acknowledgement` / `Acknowledged_Test_Mode_Is_Permissive` | **PASS** | 未配置 ≠ Test；`run_mode=test` 还需 `allow_test_mode=true` |
| `PP-MODE-02a` | L0+L1 | `ModePolicyTests.Play_Mode_Denies_Unknown_Operations`、`PolicyFingerprintTests.test_mismatched_policy_refuses_without_sending` | **PARTIAL** | 未知 op 拒绝成立；指纹漂移只在 Python 客户端侧被证伪，C# 侧只能报告自身哈希，**不能检测**外部漂移 |
| `PP-MODE-02b` | —— | 无 | **NOT_RUN** | 依赖 D 模块（两轴状态 + `get_operation`）；当前 `action_id` 缓存不变，但"模式切换后旧 ID 不重放"未实现/未测 |
| `PP-MODE-02c` | L2 | `EndpointGateTests.Locked_Refuses_State_And_Writes`、`Test_Mode_Keeps_The_Endpoints_Available` | **PASS** | 裸 HTTP 与 MCP 走同一 `ModePolicy`；拒绝发生在 provider 调用与 dispatch 之前 |
| `PP-MODE-02d` | L0 | `test_tool_policy`（纯度来自生成策略，不读 `readOnlyHint`） | **PARTIAL** | 现有测试证明策略文件不依赖 hint；没有一条"把 hint 改成 true 也不会放宽"的负向用例 |

## B — 快照纯度 / LegalAction（未开始）

| ID | 结果 | 说明 |
|---|---|---|
| `PP-READ-01` / `PP-READ-01-fake` | **NOT_RUN** | 需要假实现计数 `Refresh`/`RNG.Roll`/UI setter；Unity 侧 `Refresh()` 计数仍未插桩 |
| `PP-READ-02` | **NOT_RUN** | stale `rev` 无副作用：需要 fake 累计计数 |
| `PP-READ-03` | **NOT_RUN** | 快照字段源穷举表未产出 |
| `PP-READ-04` | **PASS（L0）** | `test_tool_policy` 的源码扫描断言 6 个别名与 `SNAPSHOT_READERS` 完全一致；新增别名会失败 |
| `PP-READ-05` | **NOT_RUN** | 谈价面板打开但四向不可交易时 `get_legal_actions` 的语义尚未改（当前仍列 4 个 bargain 方向） |
| `PP-READ-06` | **NOT_RUN** | 旧字段缺失时 Python/Skill 降级 |
| `PP-READ-07` | **NOT_RUN** | 顾客切换时纯读路径不得重置谈判状态（fake） |

> 已确认仍存在的缺陷：`StateSnapshot.DescribeClient` 调用 `StoreClient.CanReport()` →
> `BargainUIManager.IsCurrentClientCoerced()` → `Refresh()`。这是 `client.can_report` 字段的实时副作用，
> 属于 B1 的"源头"清理项，尚未修复。

## C / D / E / G — 未开始

| 组 | 条目 | 结果 |
|---|---|---|
| C 交易 | `PP-TRADE-01..09` | **NOT_RUN**（无实现变更） |
| D 状态机 | `PP-ASYNC-01..05`、`PP-SAVE-01`、`PP-RECOVERY-01` | **NOT_RUN** |
| D 守卫 fake | `P0-LANDLORD-02-fake`、`P0-OPEN-01-fake` | **NOT_RUN** |
| E Skill/评测 | `FIRST_WEEK_BENCHMARK_SPEC`、trace validator | **NOT_RUN**（未起草） |
| G 兼容 | Scanner / 反射清单 | **NOT_RUN**（未起草） |
| 实机 | `PP-MODE-01`、`PP-RECORD-01`、`PP-MODSCAN-01`、`PP-PLAY-01` | **NOT_RUN**（无游戏进程） |

## 复现命令

```bash
cd PawnMind
./test.sh RunMode          # A 的模式解析/目录一致性
./test.sh EndpointGate     # A 的 HTTP 级门禁
./test.sh                  # C# 683 + 门禁 53 + 契约 491
.venv/bin/python -B -m unittest discover -s agent/tests   # Python 253
.venv/bin/python tools/gen_policy_catalog.py --check      # 策略生成物漂移
```
