# NIGHTLY_BATCH_A_REPORT — 跨夜批次阶段报告

> 状态：**批次中断于 A 模块完成、B/C/D/E/G 未开始**。
> 原因：用户要求把跨夜开发迁移到本地服务器（`CT101`）并在迁移后交接；服务器缺 .NET SDK，构建机上无法跑 C# 套件。
> 本报告不把未完成项写成已完成。全部实机项 `NOT_RUN`。

- 本批次提交：`63ae0ef`（`fix(pawnmind): fail-closed run mode and endpoint gate (Nightly A)`）
- 基线：`docs/audit/NIGHTLY_BASELINE.md`
- 回归状态：`docs/audit/NIGHTLY_TEST_MATRIX.md`
- 迁移：`docs/plans/SERVER_MIGRATION_2026-11-17.md`
- 实机清单：`docs/plans/NEXT_LIVE_SESSION.md`

## A — 模式配置到执行的强制策略：**完成（离线）**

| 要求 | 实现 | 证据 |
|---|---|---|
| A1.1 显式 `run_mode`，失败 fail closed | `bridge.conf` 新增 `run_mode` / `allow_test_mode`；`RunModeBinding.Evaluate` 对缺失、空白、未知值一律返回 `Locked` | `RunModeTests` 4 条；`BridgeConfig_Parses_And_Roundtrips_The_Mode` |
| A1.2 Test 必须显式且受控 | `run_mode=test` 之外还要 `allow_test_mode=true`，否则 `test_mode_not_authorized` | `Test_Mode_Requires_Explicit_Acknowledgement` |
| A1.3 无 MCP 切换通道 | 模式只在 `OnEnable` 绑定一次；没有任何 op 能改 `ModePolicy` | `grep ModePolicy src/` 仅启动处 + 路由委托 |
| A1.4 单一策略来源 | `src/Core/PolicyCatalog.g.cs` 由 `tools/gen_policy_catalog.py` 从 `agent/policy/tool_policy.json` 生成（schema + sha256 + 三档集合） | `test_generated_csharp_catalog_matches_the_policy`；`gen_policy_catalog.py --check` |
| A1.5 指纹不一致 → 拒绝危险操作 | 客户端读 `/health.policy_sha256` 比对，不一致时 `policy_mismatch` 且**不发送请求**；`/health` 报告 `run_mode`/`run_mode_error`/`policy_schema`/`policy_sha256`/`play_ready` | `PolicyFingerprintTests` 4 条；`Health_Stays_Open_And_Reports_The_Gate` |
| A1.6 candidate 不得转 play | `RunModeBinding` 的 Play 分支只读 `PolicyCatalog.PlayOps`（当前为空） | `Play_Mode_Stays_Closed_Without_Live_Evidence`、`Play_Does_Not_Inherit_The_Candidate_Allowlist` |
| A2 全入口授权且先于快照 | `EndpointPolicy` 作用于 `RouteKind.State/Events/Action`；`/state`、`/events` 在 provider 调用前 403 | `EndpointGateTests` 6 条，断言 provider/dispatch 计数 0 |
| A3 必测场景 | `PP-MODE-01a/b/c/d` PASS；`02a` PARTIAL；`02b` NOT_RUN（依赖 D）；`02c` PASS；`02d` PARTIAL | 见测试矩阵 |

已知边界（写进审阅文档）：

- C# 只能报告自己的策略哈希，**无法验证**外部 Python 侧；防护是单向的（Python 查 C#）。
- `Locked` 会连 `POST /action` 的 `ping` 一起拒绝；这是有意的"全锁"，`/health` 不受影响。
- 策略切换需要重启（无热重载），符合 A1.3 的"可信生命周期"要求，但当前没有配置文件监听器。

## B — 快照纯度与 LegalAction：**未开始**

- 已定位的真实缺陷：`StateSnapshot.DescribeClient` → `StoreClient.CanReport()` → `BargainUIManager.IsCurrentClientCoerced()` → `Refresh()`。
- `get_legal_actions` 仍会在谈价面板打开时列出 4 个 bargain 方向（候选语义未分离）。
- 需要：字段源穷举表、fake 计数装置、`availability` 语义、`PP-READ-01..07`。

## C — 交易守卫：**未开始**

无 `confirm_trade` 代码改动；`PP-TRADE-01..09` 全部 `NOT_RUN`。

## D — ActionGate 两轴与有界等待：**未开始**

`MarkResponseUndelivered` 仍会把已完成的响应改写为 `outcome_unknown`（`ActionRouter.cs`）；`get_operation` / `wait_operation` 不存在。

## E — 首周 Skill 与离线评测：**未开始**

未创建 `skills/pawnmind-first-week/SKILL.md`，未创建 `docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md`。

## G — 发布兼容静态预审：**未开始**

未做反射/后台线程/HTTP listener 的风险清单。

## F — 独立审阅与集成：**部分**

- A 模块独立审阅：`docs/reviews/NIGHTLY_A_MODE_POLICY_REVIEW.md`（由独立审阅 agent 产出）。
- F1 跨层集成、F2 全量重跑：A 之后已跑一次全量（见基线），但 B/C/D 未合入，集成审阅尚不成立。

## 总测试摘要

| 套件 | 结果 | 命令 |
|---|---|---|
| C# 纯逻辑 | **683 passed / 0 failed** | `cd PawnMind && ./test.sh` |
| 契约门禁 | **53 passed / 0 failed** | 同上（脚本自动追加） |
| 适配器契约 | **491 checks OK** | 同上 |
| Python | **253 OK** | `.venv/bin/python -B -m unittest discover -s agent/tests` |
| 策略生成物 | **ok（sha256 `48213300…`）** | `tools/gen_policy_catalog.py --check` |

## 能否进入下一阶段

- **可以**继续离线推进 B → D → C → E → G，前提是在有 .NET SDK 的主机上跑 C# 套件（当前 WSL 可用，服务器待装）。
- **不可以**进入实机/录制：`play_allowlist` 仍为 0，`dist` 与已安装 DLL 不一致，`PP-READ-01` 未插桩。

## 任务书 12 问（当前能回答的部分）

1. HEAD：`63ae0ef`；相对 `ROUND3_REMEDIATION.md` 的差异见基线第 1 节。
2. 是否移除"缺配置默认 Test"：**是**，默认 `Locked`，`/health` 报告原因。
3. 裸 HTTP 能否绕过读取限制：**否**，`/state`、`/events` 在 bootstrap/play/locked 下 403 且不构建快照。
4. C#/Python 是否同源：**是**（生成物 + 漂移测试）；不一致时 Python 侧拒绝写入。
5. 快照是否仍可能触发 `Refresh()`：**是**，`client.can_report` 仍在（B 模块待修）。
6. `get_legal_actions` 是否把候选当合法：**是**，未修。
7. B10–B16 是否为 0 派发测试：**无**。
8. `confirm_trade` 与对话是否分离：**未实现**。
9. `MarkResponseUndelivered` 是否仍改写真事实：**是**，未修。
10. `wait_operation` 是否阻塞主线程：**不存在**。
11. 首周最小工具集是否定义：**否**。
12. 剩余 `NOT_RUN`：游戏启动、MVID、Scanner、录制、交易成功路径、租金结算、`Refresh()` 计数。
