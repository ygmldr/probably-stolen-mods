# PawnMind 离线开发服务器迁移记录

日期：2026-11-17（跨夜批次 Nightly Batch A 期间）
目的：把跨夜离线开发从 WSL 工作站迁到常开服务器 `CT101`，让批次可以在主机休眠时继续。

## 0. 接手入口

按顺序读：

1. 本文件（环境、路径、同步方式、未完成项）
2. `docs/audit/NIGHTLY_BASELINE.md`（冻结基线：HEAD、测试数、策略指纹、DLL 一致性）
3. `docs/audit/NIGHTLY_TEST_MATRIX.md`（每个 `PP-*` 的真实状态：PASS / PARTIAL / NOT_RUN）
4. `docs/reports/NIGHTLY_BATCH_A_REPORT.md`（A 已完成、B/C/D/E/G 未开始的证据）
5. `docs/plans/NEXT_LIVE_SESSION.md`（需要真人在场才能做的部分）
6. 任务书：`docs/PAWNMIND_OVERNIGHT_LARGE_BATCH_AGENT_HANDOFF_2026-11-17.md`

**当前提交**：`63ae0ef`（A 模块）。工作树干净。

## 1. 目标主机

| 项 | 值 |
|---|---|
| 主机名 | `CT101` |
| 地址 | `192.168.1.101:22`（用户最初给的 `192.168.101.1` 不通：本机在 `192.168.1.0/24`，网段无路由） |
| 账号 | `root` |
| 认证 | **SSH 密钥**（`~/.ssh/id_ed25519`，注释 `<redacted-email>`，已可用） |
| 密码认证 | 不可用（`<redacted>` 被拒；不必再试，直接用密钥） |
| 规格 | 12 核 / 20 GB RAM / 根分区 17 GB 可用（78% 已用，注意空间） |
| 系统 | Linux x86_64，Python 3.12.3，已装 git / rsync / node / npm / curl |
| 已有 | DSH `0.2.1-rc.2`（全局 npm）、`/root/.dsh` 配置与历史会话 |

## 2. 迁移内容

| 内容 | 服务器路径 | 大小 | 说明 |
|---|---|---|---|
| 仓库工作区 | `/root/work/game_mods` | 115 MB | 含 `.git`（HEAD `70e9d0d`），可直接在服务器提交 |
| Python 虚拟环境 | `/root/work/game_mods/PawnMind/.venv` | — | 新建；`mcp==2.3.0` + `pydantic` |
| 反编译参考源 | `/root/work/game_mods/_decomp` | 9.8 MB | 已 gitignore；B 模块调用链审计需要 |
| 游戏托管 DLL | `/root/work/game_managed` | 36 MB | 供适配器契约测试加载，**不随仓库分发** |

同步时排除：`.venv/`、`*/bin/`、`*/obj/`、`__pycache__/`、`*.pyc`。

## 3. 已验证（真实命令与输出）

服务器上运行：

```bash
cd /root/work/game_mods/PawnMind
.venv/bin/python -B -m unittest discover -s agent/tests
# Ran 248 tests in 60.740s — OK
```

本地（WSL）同套件：`Ran 248 tests in 131.857s — OK`。服务器约 2.2 倍速。

本地基线（迁移前，供对照）：

```bash
cd PawnMind && ./test.sh       # C# 纯逻辑 666 passed / 0 failed + 契约 491 + 门禁 53
```

## 4. 未完成 / 阻塞

| 项 | 状态 | 原因与处理 |
|---|---|---|
| .NET 9 SDK | **NOT_INSTALLED** | 服务器到 `builds.dotnet.microsoft.com` 仅约 51 KB/s（200 MB ≈ 66 min），已按要求停止下载，留给服务器侧 agent 自行安装 |
| C# 纯逻辑套件（666） | **NOT_RUN（服务器）** | 依赖 .NET 9 SDK；本地已 PASS |
| 适配器契约（491） | **NOT_RUN（服务器）** | 同上；DLL 已就位，装好 SDK 即可跑 |
| 契约门禁（53） | **NOT_RUN（服务器）** | 同上 |

`PawnMind.Tests.csproj` 的 Newtonsoft 引用已改为可覆盖：

```xml
<HintPath Condition="'$(PAWNMIND_NEWTONSOFT_DLL)' != ''">$(PAWNMIND_NEWTONSOFT_DLL)</HintPath>
```

未设置时仍回落到原来的 Windows 路径，因此本地行为不变。

## 5. 服务器端接力步骤

```bash
# 1) 装 SDK（任选其一）
./PawnMind/tools/setup_remote_host.sh          # 自动装 9.0 + 建 venv + 跑两套测试
# 或手动：curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --install-dir /root/.dotnet

# 2) 配置环境（setup 脚本会生成 .env.server）
set -a; . /root/work/game_mods/PawnMind/.env.server; set +a

# 3) 两套测试
cd /root/work/game_mods/PawnMind
./test.sh                                      # C# 纯逻辑 + 契约门禁 + 适配器契约
.venv/bin/python -B -m unittest discover -s agent/tests

# 4) 只跑匹配用例
./test.sh Bailout
```

若适配器契约因为缺少游戏 DLL 而 SKIP，检查 `$GAME_MANAGED/Assembly-CSharp.dll` 是否存在。

## 6. 同步方式

本地 → 服务器（服务器只作为执行镜像，源码以本地为准时可这样用）：

```bash
cd /mnt/d/code/game_mods
rsync -a --delete -e ssh \
  --exclude '.venv/' --exclude '*/bin/' --exclude '*/obj/' \
  --exclude '__pycache__/' --exclude '*.pyc' \
  ./ root@192.168.1.101:/root/work/game_mods/
```

服务器 → 本地（服务器上产生提交后）：

```bash
rsync -a -e ssh root@192.168.1.101:/root/work/game_mods/ /mnt/d/code/game_mods/
```

注意：

- `--delete` 不会删除被 exclude 的 `.venv`，但会删除服务器上未同步的新文件；在服务器上提交前先确认方向。
- 服务器仓库需要 `git config --global --add safe.directory /root/work/game_mods`（已设置）。
- 传输后属主会变成 root；`chown -R root:root /root/work/game_mods` 已执行。

## 7. 边界（与任务书一致，未变）

- 不启动游戏、不点 UI、不读写玩家存档、不覆盖安装 DLL、不启用录制、不发布、不推送远程。
- 服务器上没有任何游戏进程，因此所有实机项仍是 `NOT_RUN`。
- `_decomp` 与 `game_managed` 只用于本地互操作分析，不进 `dist/`、不进 Workshop 包。
- 服务器根分区只剩 17 GB，构建产物（`bin/`、`obj/`、`dist/`）要定期清理。

## 8. 当前工程状态（迁移时点）

| 项 | 状态 |
|---|---|
| HEAD | `63ae0ef`（A 模块） |
| 工作树 | 干净 |
| H0 基线 | 本地已完成（见第 3 节数字）；`docs/audit/NIGHTLY_BASELINE.md` 待写 |
| A 模块 | **已完成离线部分**：`run_mode` 配置、fail-closed、`PolicyCatalog.g.cs` 单源生成、`/state` `/events` HTTP 级门禁、`/health` 报告；C# 683 + 门禁 53 + 契约 491 全绿，Python 套件全绿 |
| B/C/D/E/G | 未开始 |

A 模块之后本地测试计数：`passed=683 failed=0`、`contract gate tests: passed=53 failed=0`、`contract checks: 491`、Python `253 OK`。

服务器侧 agent 接手时，从 A 模块的 Red 测试开始：先 `./test.sh RunMode` 确认失败原因，再实现 `Core/RunModeBinding.cs`、`Core/PolicyCatalog.g.cs`、`ModePolicy` 的 `Locked` 模式与 `EndpointPolicy`。

## 9. 未完成工作（按优先级，接手请照此推进）

### 9.1 B — 快照纯度（最高优先级，任务书 B1/B2）

已知**仍然存在**的缺陷（本轮只定位，未修）：

```
StateSnapshot.DescribeClient
  → StoreClient.CanReport()                     src/Bridge/StateSnapshot.cs
  → BargainUIManager.IsCurrentClientCoerced()   _decomp/BargainUIManager.cs:635
  → Refresh()                                   _decomp/BargainUIManager.cs:1103  ← 会重置谈判状态
```

做法：

1. 把 `client.can_report` 从快照里删掉，或换成不需要 `Refresh()` 的纯字段（例如直接用 `client.reported` / `client.isWanted`）。
2. 建立 `docs/audit/NIGHTLY_SNAPSHOT_CALLGRAPH.md`：`BuildJson → BuildState/BuildFlags → GameState.* → 原生 getter` 的**完整**树，每个节点标 `pure | unknown | impure` + 证据（`_decomp/` 行号）。未知不得默认 pure。
3. 加 `Core/SnapshotFieldMap.cs`（字段 → 来源 → 纯度）与**漂移测试**：解析 `StateSnapshot.cs` 的调用，出现未登记成员即失败。
4. fake 计数装置：`PP-READ-01-fake` 连续 10 次构建，`Refresh`/`RNG.Roll`/UI setter 计数必须为 0。
5. `PP-READ-05`：`get_legal_actions` 把 bargain 四向标成 `candidate`/`availability:"unknown"`，不要伪装成合法；同步改 `agent/mcp_server.py` 的说明与 Skill。
6. 不要声称 fake 能证明 Unity 运行时纯度；`PP-READ-01` 实机仍是 `NOT_RUN`，需要插桩构建。

### 9.2 D — 执行/投递双轴 + `get_operation`（其次，C 依赖它）

1. `ActionGate` 增加 `execution_status`（prepared/running/completed/rejected/failed/uncertain）与 `delivery_status`（pending/delivered/undelivered）。
2. `MarkResponseUndelivered` 只改投递轴，**不得**把已完成结果覆盖成 `outcome_unknown`。
3. `BridgeServer` 增 `GET /operation?id=`；`mcp_server` 增 `get_operation` / `wait_operation`（Python 侧有界轮询，不阻塞主线程）。
4. 新增工具必须同步：`agent/policy/tool_policy.json` 条目、`tools/gen_policy_catalog.py` 重跑、`tools-inventory`/工具数测试、`PP-MODE-02d`。
5. 测试：`PP-ASYNC-01..05`、`PP-SAVE-01`、`PP-RECOVERY-01`。

### 9.3 C — 交易守卫（依赖 9.1 的读路径与 9.2 的状态轴）

- 只改 `confirm_trade` 核心路径 + `advance_dialogue` / `call_next_customer` 的最低适配。
- B10–B16 七个静默拒绝分支逐个预检查，**派发 0 次**的失败测试；删掉任何手拼 `BuyItem`/`SellItem`。
- 结果证据分层（返回值/异常、物品归属、`IsClientBusinessDone`），现金变化不能单独作证。
- 交易 Operation 与顾客对话 Workflow 分离。

### 9.4 E / G / F

- E：`skills/pawnmind-first-week/SKILL.md`、`docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md`、trace validator（禁止新 `action_id` 重试 uncertain）。
- G：`docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md`（反射、后台线程、HTTP listener、打包脚本），Scanner 仍 `NOT_RUN`。
- F：每模块独立审阅 + `NIGHTLY_F_INTEGRATION_REVIEW.md`；A 的审阅已产出（`docs/reviews/NIGHTLY_A_MODE_POLICY_REVIEW.md`）。

### 9.5 A 模块遗留

| 项 | 状态 | 说明 |
|---|---|---|
| `PP-MODE-02b` | NOT_RUN | 依赖 9.2：模式切换/重载后旧 `action_id` 不得重放 |
| `PP-MODE-02d` 负向用例 | PARTIAL | 需要"把 `readOnlyHint` 改成 true 也不会放宽"的显式测试 |
| C# 校验 Python 策略 | 不可能 | C# 只报告自己的哈希；方向是 Python 查 C# |
| `.env.server` | 未生成 | 服务器装好 SDK 后由 `tools/setup_remote_host.sh` 生成 |

### 9.6 服务器侧必须先做的事

```bash
cd /root/work/game_mods/PawnMind
./tools/setup_remote_host.sh     # 安装 .NET 9 SDK + 建 venv + 跑两套测试
# 装完后：
set -a; . .env.server; set +a
./test.sh                        # 期望 683 / 53 / 491
.venv/bin/python -B -m unittest discover -s agent/tests   # 期望 253
```

服务器下载 SDK 约 51 KB/s（200 MB ≈ 66 分钟），可以后台跑，或换成 apt/内网镜像。
