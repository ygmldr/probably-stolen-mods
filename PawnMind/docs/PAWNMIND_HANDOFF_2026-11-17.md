# PawnMind 交接文档（2026-11-17 跨夜批次中断点）

> 这份文档是**唯一入口**：读完它就能接手，不需要回溯会话。
> 生成时间：2026-11-17 04:20 (Asia/Shanghai)。用户已去睡觉，批次由用户主动叫停并交接。
> 本文件只陈述已验证的事实；没有跑过的，一律写 `NOT_RUN`。

---

## 0. 一句话现状

**A 模块（fail-closed 运行模式 + 全入口门禁）已完成并通过全部离线测试；B/C/D/E/G/F 未开始；仓库已迁移到服务器 `CT101`，但服务器还缺 .NET SDK，所以服务器上目前只能跑 Python 套件。**

---

## 1. 状态速览

| 项 | 值 |
|---|---|
| HEAD | `c91db40`（文档）/ `63ae0ef`（A 模块代码） |
| 分支 / 远程 | `main` / **未推送** |
| 工作树 | 干净 |
| C# 纯逻辑 | **683 passed / 0 failed** |
| 契约门禁 | **53 passed / 0 failed** |
| 适配器契约 | **491 checks OK** |
| Python | **253 OK**（本地 134 s，服务器 60.7 s @ 迁移时点 248） |
| `play_allowlist` | **0**（没有任何 live evidence，禁止填） |
| `dist` vs 已安装 DLL | **不一致**（`ad335987…` vs `e9e1bd07…`） |
| 实机 / Scanner / 录制 | **全部 NOT_RUN** |
| 服务器构建机 | `CT101` 已就绪，但 **.NET SDK 未安装** |

---

## 2. 代码与提交

```
c91db40 docs: nightly baseline, test matrix and server handoff
63ae0ef fix(pawnmind): fail-closed run mode and endpoint gate (Nightly A)
70e9d0d fix(pawnmind): D0a snapshot purity, mode policy gate and R3 remediation   ← 批次起点
```

`63ae0ef` 改了什么（全部有测试）：

| 文件 | 改动 |
|---|---|
| `src/Core/ModePolicy.cs` | `RunMode` 增加 `Locked`（默认值）；新增 `Reset()` / `Error`；`Locked` 时拒绝一切 |
| `src/Core/RunModeBinding.cs` | 新增：解析 `run_mode`，**任何异常都返回 Locked**；`test` 需二次开关 |
| `src/Core/PolicyCatalog.g.cs` | 新增：由 Python 策略生成的 C# 镜像（schema、sha256、bootstrap/play/snapshot 集合） |
| `src/Core/EndpointPolicy.cs` | 新增：`/state`、`/events`、`/action` 的 HTTP 级门禁 |
| `src/Core/BridgeConfig.cs` | 新增 `run_mode` / `allow_test_mode`；**保留原始文本**，拼错不被静默改写 |
| `src/Bridge/BridgeServer.cs` | 三个端点在建快照/dispatch **之前**拒绝；`/health` 增加 `run_mode`、`run_mode_error`、`policy_schema`、`policy_sha256`、`play_ready` |
| `src/PawnMindMod.cs` | 启动时绑定模式，失败打 Error 日志说明怎么改 `bridge.conf` |
| `agent/tool_policy.py` | 新增 `canonical_json` / `policy_sha256`（与 C# 生成物同一实现） |
| `agent/pawnmind_client.py` | `/health` 指纹不一致时返回 `policy_mismatch`，**请求不发出** |
| `agent/mcp_server.py` | 启动时把本机策略哈希传给客户端 |
| `tools/gen_policy_catalog.py` | 生成器，支持 `--check` |
| `tools/setup_remote_host.sh` | 服务器一键装 SDK + venv + 跑两套测试 |
| 测试 | `RunModeTests`（11）、`EndpointGateTests`（6）、`test_client` 4 条、`test_tool_policy` 1 条漂移测试 |

**验证过的编译**：`PAWNMIND_BUILD_ONLY=1 ./build.sh` → `0 个警告 0 个错误`（包含 `PawnMindMod.cs`，即真实 Unity 目标）。

---

## 3. 服务器（`CT101`）

| 项 | 值 |
|---|---|
| 地址 | `192.168.1.101:22`（最初给的 `192.168.101.1` 不通；本机在 `192.168.1.0/24`） |
| 账号 / 认证 | `root` / **SSH 密钥**（`~/.ssh/id_ed25519`）。密码 `<redacted>` **被服务端拒绝**，别再试 |
| 规格 | 12 核 / 20 GB / 根分区 17 GB 可用 |
| 仓库 | `/root/work/game_mods`（115 MB，含 `.git`，HEAD 与本地一致） |
| Python venv | `/root/work/game_mods/PawnMind/.venv`（`mcp==2.3.0`、`pydantic`） |
| 反编译参考 | `/root/work/game_mods/_decomp`（已 gitignore） |
| 游戏 DLL | `/root/work/game_managed`（36 MB，供适配器契约测试） |
| 已装 | git、rsync、python3.12、node、npm、curl |
| **缺** | **.NET 9 SDK**（服务器到 Microsoft CDN 仅 ~51 KB/s，200 MB ≈ 66 分钟，已按要求停手） |

服务器上已实测：

```bash
cd /root/work/game_mods/PawnMind
.venv/bin/python -B -m unittest discover -s agent/tests   # 248 OK / 60.7 s（迁移时点）
```

### 服务器下一步（一条命令）

```bash
cd /root/work/game_mods/PawnMind && ./tools/setup_remote_host.sh
```

它会装 SDK 9.0、建 venv（已存在则复用）、生成 `.env.server`、然后跑两套测试。
装完后的期望值：`683` / `53` / `491`、Python `253`。

### 同步（本地 ↔ 服务器）

```bash
# 本地 → 服务器
cd /mnt/d/code/game_mods
rsync -a --delete -e ssh --exclude '.venv/' --exclude '*/bin/' --exclude '*/obj/' \
  --exclude '__pycache__/' --exclude '*.pyc' ./ root@192.168.1.101:/root/work/game_mods/

# 服务器 → 本地（服务器上提交后）
rsync -a -e ssh root@192.168.1.101:/root/work/game_mods/ /mnt/d/code/game_mods/
```

注意：`--delete` 会删掉服务器上未同步的新文件；传输后属主变 root，需要 `chown -R root:root`（已配 `safe.directory`）。

---

## 4. 已完成的工作

### H0 基线（`docs/audit/NIGHTLY_BASELINE.md`）

冻结了 HEAD、测试数、策略指纹、工具数、DLL 一致性，以及"相对 `ROUND3_REMEDIATION.md` 的差异"。

### A 模块（`docs/reports/NIGHTLY_BATCH_A_REPORT.md` 第 A 节）

| 要求 | 结果 |
|---|---|
| 缺配置/非法值不得进入 `Test` | ✅ 一律 `Locked`，`/health` 报 `run_mode_error` |
| `test` 必须显式 + 二次开关 | ✅ `run_mode=test` + `allow_test_mode=true` |
| 无 MCP 切模式通道 | ✅ 只在 `OnEnable` 绑定一次 |
| C#/Python 单一策略来源 | ✅ 生成物 + 漂移测试 |
| 指纹不一致 → 拒绝危险操作 | ✅ 客户端侧 `policy_mismatch`，请求不发 |
| candidate ≠ play | ✅ `PlayOps` 为空，`play_ready=false` |
| 裸 HTTP 不能绕过读取限制 | ✅ `/state`、`/events` 在 provider 之前 403，计数实测 0 |
| 拒绝先于快照/派发 | ✅ `EndpointGateTests` + `ActionRouterTests`（`refreshRevision` 计数 0） |

审阅：**`NOT_REVIEWED`**。独立审阅 agent 已启动但被用户叫停，`docs/reviews/NIGHTLY_A_MODE_POLICY_REVIEW.md` 未产出；接手者应重跑一次独立审阅，**不要**用实现者自述代替。

---

## 5. 未完成的工作（按建议顺序）

### 5.1 B — 快照纯度与 LegalAction（最高优先级）

**已知仍然存在的真实缺陷（本轮只定位，未修）**：

```
StateSnapshot.cs:300   description["can_report"] = SafeCanReport(client);
StateSnapshot.cs:308   → StoreClient.CanReport()
_decomp/BargainUIManager.cs:635  → IsCurrentClientCoerced() → Refresh()
_decomp/BargainUIManager.cs:1103 → 重置 trackedClient/报价状态  ← 读快照会改游戏状态
```

要做：

1. 删除或替换 `client.can_report`（`reported` / `isWanted` 已是纯字段）。
2. 产出 `docs/audit/NIGHTLY_SNAPSHOT_CALLGRAPH.md`：`BuildJson → BuildState/BuildFlags → GameState.* → 原生 getter`，每个节点标 `pure/unknown/impure` + `_decomp` 行号；未知不得默认 pure。
3. 新增 `Core/SnapshotFieldMap.cs` + 漂移测试（`StateSnapshot.cs` 出现未登记调用即失败）。
4. `PP-READ-01-fake`：fake 里连续 10 次构建，`Refresh`/`RNG.Roll`/UI setter 计数为 0。
5. `PP-READ-05`：`LegalAction.cs:111` 的 4 个 bargain 方向改为 `candidate` / `availability:"unknown"`，不要伪装合法；同步 `mcp_server` 文案。
6. `PP-READ-06`：旧字段缺失时 Python/Skill 的降级。
7. `PP-READ-07`：顾客切换时纯读路径不得重置谈判状态（fake）。

**不要**声称 fake 证明了 Unity 运行时纯度；`PP-READ-01` 实机仍是 `NOT_RUN`。

### 5.2 D — 执行/投递双轴 + `get_operation`（C 依赖它）

1. `ActionGate` 加 `execution_status`（prepared/running/completed/rejected/failed/uncertain）与 `delivery_status`（pending/delivered/undelivered）。
2. `ActionRouter.cs:262 MarkResponseUndelivered` **只改投递轴**，不得把已完成结果覆盖成 `outcome_unknown`。
3. 新增 `GET /operation?id=`；MCP 增 `get_operation` / `wait_operation`（Python 侧有界轮询，不阻塞 Unity 主线程）。
4. 新工具必须同步四处：`agent/policy/tool_policy.json` 条目、`tools/gen_policy_catalog.py` 重跑、工具数/清单测试、`PP-MODE-02d` 负向用例。
5. 测试：`PP-ASYNC-01..05`、`PP-SAVE-01`、`PP-RECOVERY-01`、`PP-MODE-02b`。

### 5.3 C — `confirm_trade` 守卫

- 只改核心路径 + `advance_dialogue`/`call_next_customer` 最低适配，不扩散到 58 个写动作。
- B10–B16 七个静默拒绝分支逐个预检查，测试断言**派发 0 次**。
- 证据分层（返回值/异常、物品归属、`IsClientBusinessDone`）；现金变化不能单独作证；以物易物要能判定。
- 交易 Operation 与顾客对话 Workflow 分离；未提交时不得虚报 `completed`。
- 测试：`PP-TRADE-01..09`。

### 5.4 E — 首周 Skill 与离线评测

- `skills/pawnmind-first-week/SKILL.md`：启动、观察、交易、对话、关店、睡觉、租金、错误分类、停止条件；引用真实工具名与响应 schema。
- `docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md`：存档/seed、版本、模型、工具集版本、成功判据、失败码、成本、回合时长、人工介入次数。**不要把 benchmark 自己跑出来**。
- trace validator：禁止用新 `action_id` 重试 `uncertain`；pending 时不得调用冲突动作；租金需外部可验证事件。
- 通关率/效率一律 `NOT_MEASURED`。

### 5.5 G — 发布兼容静态预审

`docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md`：反射、后台线程、HTTP listener、打包 Python、`dist` 与已安装不一致；Scanner 未安装则 `NOT_RUN`。不做实际发布。

### 5.6 F — 独立审阅与集成

- 每模块一份 `docs/reviews/NIGHTLY_<X>_REVIEW.md`，按任务书固定结构写，**必须披露独立性**。
- `NIGHTLY_F_INTEGRATION_REVIEW.md`：C#↔Python 策略、Snapshot↔LegalAction↔Skill、Trade↔Operation↔Dialogue、Gate↔Recorder、Async↔Save、Play gate↔live evidence、Build↔Installed。
- A 的审阅 **未完成**（`NOT_REVIEWED`）：独立 agent 被叫停，需重跑。

### 5.7 A 的遗留项

| 项 | 状态 |
|---|---|
| `PP-MODE-02b`（模式切换后旧 `action_id` 不重放） | `NOT_RUN`，依赖 5.2 |
| `PP-MODE-02d` 负向用例（`readOnlyHint` 改成 true 也不能放宽） | `PARTIAL` |
| C# 验证 Python 策略 | 不可能（C# 只报告自身哈希；方向是 Python 查 C#） |

---

## 6. 已知缺陷清单（供接手者核对）

| # | 位置 | 问题 | 状态 |
|---|---|---|---|
| 1 | `StateSnapshot.cs:300` | `client.can_report` 触发 `Refresh()`，纯读路径改游戏状态 | **未修** |
| 2 | `LegalAction.cs:111` | 谈价面板打开即列出 4 个 bargain 方向，可能实际不可交易 | **未修** |
| 3 | `ActionRouter.cs:262` | `MarkResponseUndelivered` 把已完成结果改写为 `outcome_unknown` | **未修** |
| 4 | `dist` vs 已安装 | `ad335987…` ≠ `e9e1bd07…`，游戏内现象不可归因 | 未处理（本批次未重装） |
| 5 | `policy/tool_policy.json` | `play_allowlist=0`，所有工具缺 live evidence | 设计如此 |
| 6 | `ModePolicy` | 无热重载，改模式需重启游戏 | 设计如此（A1.3） |
| 7 | `EndpointPolicy` | `Locked` 下连 `POST /action` 的 `ping` 也拒绝 | 有意，`/health` 不受影响 |

---

## 7. 硬边界（违反即返工）

- **不启动/操作真实游戏**、不点 UI、不读写玩家存档、不在真实存档上跑自动操作。
- **不覆盖安装 DLL**、不发布、不推远程、不改 Workshop。
- **不伪造 live evidence**：没有实机记录就不能把 `play_allowlist` 从 0 改成非 0，也不能把 `NOT_RUN` 写成 `PASS`。
- **不删除/放宽失败测试**；测试红了就修代码或记录阻塞。
- 不新建第二套 Agent 主循环 / 第二套 OperationTracker。
- `_decomp` 与 `game_managed` 只用于本地分析，不进 `dist/`、不进工坊包。

---

## 8. 实机验收（需要真人在场）

详见 `docs/plans/NEXT_LIVE_SESSION.md`，摘要：

1. `./build.sh` → 校验 `dist` 哈希 → 退出游戏后覆盖安装 → 再比对哈希。
2. `bridge.conf` 设 `run_mode=bootstrap`、`allow_test_mode=false`。
3. 启动游戏 → `curl /health` 核对 `run_mode`、`policy_sha256`、MVID。
4. 裸 `/state`、`/events` 应 403。
5. `PP-READ-01` 需要**插桩构建**（`Refresh()` 计数 + `/health` 暴露），当前代码做不到。

**在 `PP-READ-01` 有计数证据之前，不要让任何东西进入 Play。**

---

## 9. 常用命令

```bash
cd /mnt/d/code/game_mods/PawnMind

./test.sh                                   # C# 683 + 门禁 53 + 契约 491
./test.sh RunMode                           # 模式解析
./test.sh EndpointGate                      # HTTP 级门禁
PAWNMIND_BUILD_ONLY=1 ./build.sh            # 只编译 Mod（含 Unity 目标），不打包
.venv/bin/python -B -m unittest discover -s agent/tests      # Python 253
.venv/bin/python tools/gen_policy_catalog.py --check          # 策略生成物漂移检查
.venv/bin/python tools/gen_policy_catalog.py                  # 策略改了之后重跑
```

新增/修改策略时：改 `agent/policy/tool_policy.json` → 重跑生成器 → 跑 Python 测试（漂移测试会失败直到生成物同步）→ 跑 C#。

---

## 10. 接手第一天 checklist

- [ ] 在服务器装 .NET SDK 并跑 `tools/setup_remote_host.sh`，确认 683 / 53 / 491 / 253。
- [ ] 读 `docs/reviews/NIGHTLY_A_MODE_POLICY_REVIEW.md`，把 blocker 变成待办。
- [ ] 修 `StateSnapshot.cs:300` 的 `can_report`（B1 的第一步），先写 fake 计数测试。
- [ ] 产出 `NIGHTLY_SNAPSHOT_CALLGRAPH.md`，把 `unknown` 列出来而不是猜。
- [ ] 再动 D（`ActionGate` 双轴），因为 C 和 `PP-MODE-02b` 都依赖它。

---

## 11. 文档索引

| 文档 | 内容 |
|---|---|
| `docs/PAWNMIND_OVERNIGHT_LARGE_BATCH_AGENT_HANDOFF_2026-11-17.md` | 任务书（A–G、交付要求、终止规则） |
| `docs/audit/NIGHTLY_BASELINE.md` | 冻结基线 |
| `docs/audit/NIGHTLY_TEST_MATRIX.md` | 每个 `PP-*` 的真实状态 |
| `docs/reports/NIGHTLY_BATCH_A_REPORT.md` | 批次阶段报告（含 12 问回答） |
| `docs/plans/SERVER_MIGRATION_2026-11-17.md` | 服务器细节与同步命令 |
| `docs/plans/NEXT_LIVE_SESSION.md` | 实机验收步骤与记录模板 |
| `docs/reviews/NIGHTLY_A_MODE_POLICY_REVIEW.md` | A 模块独立审阅（**尚未产出**） |
| `docs/audit/BASELINE_CURRENT.md` | 上一轮基线（R3） |
| `docs/reviews/ROUND3_REMEDIATION.md` | 上一轮整改说明 |
| `PawnMind/AGENTS.md`、根 `AGENTS.md` | 工程规则（TDD、分层、安全不变量） |
