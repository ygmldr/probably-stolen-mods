# 服务器侧 Agent 任务书：从零搭环境 → 继续跨夜开发

> 读者：在 `CT101` 上接手的 Agent（DSH 会话或任意 coding agent）。
> 前提：你**没有**上一轮对话的上下文。这份文档是自足的。
> 目标：先把构建/测试环境从零跑到全绿，然后继续 Nightly Batch A 的 B/C/D/E/G/F。
> 你**没有**权限做的事写在 §7，先读那一节。

---

## 0. 一分钟理解

- 项目：`PawnMind`，Unity Mono C# Mod + Python stdio MCP，给游戏 *Probably Stolen 0.46D* 用。
- 仓库：`/root/work/game_mods`（已同步，HEAD 见 `git log -1`）。
- 已完成：A 模块（fail-closed 运行模式 + HTTP 门禁 + 策略单源）。
- 未完成：B（快照纯度）、D（状态机双轴）、C（交易守卫）、E（Skill/评测）、G（兼容预审）、F（审阅与集成）。
- 当前唯一环境缺口：**.NET 9 SDK 未安装**，所以 C# 套件在服务器上跑不了。
- 总任务书：`PawnMind/docs/PAWNMIND_OVERNIGHT_LARGE_BATCH_AGENT_HANDOFF_2026-11-17.md`。
- 交接总览：`PawnMind/docs/PAWNMIND_HANDOFF_2026-11-17.md`。

---

## 1. 三分钟自检

```bash
hostname                       # 期望 CT101
cd /root/work/game_mods && git log --oneline -1 && git status --porcelain | wc -l
ls PawnMind/agent/policy/tool_policy.json PawnMind/src/Core/PolicyCatalog.g.cs
ls /root/work/game_managed/Assembly-CSharp.dll /root/work/game_managed/Newtonsoft.Json.dll
```

任何一条不符，先停下核对，不要继续往下装。

---

## 2. 从零搭环境

### 2.1 系统事实（已核实，可直接对比）

| 项 | 值 |
|---|---|
| 系统 | Ubuntu 24.04.4 LTS，x86_64，12 核，20 GB RAM |
| 根分区可用 | 约 17 GB（**注意**：构建产物会占空间，见 2.6） |
| 已有 | `git` `rsync` `python3.12` `pip` `node` `npm` `curl` `apt-get` |
| 缺失 | `dotnet`（本节的唯一任务） |
| 已建 | `PawnMind/.venv`（`mcp==2.3.0`、`pydantic`） |
| 已备 | `/root/work/game_managed`（36 MB 游戏 DLL）、`PawnMind/_decomp`（反编译参考，已 gitignore） |

### 2.2 安装 .NET 9 SDK

测试工程 `TargetFramework=net9.0`，**必须 9.x**（8.x 会报错）。

**首选：一键脚本（推荐）**

```bash
cd /root/work/game_mods/PawnMind
./tools/setup_remote_host.sh
```

它会：装 SDK 到 `/root/.dotnet` → 建/复用 venv → 生成 `.env.server` → 跑两套测试。

**备用 A：手动装（脚本失败时）**

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
nohup bash /tmp/dotnet-install.sh --channel 9.0 --install-dir /root/.dotnet --no-path \
  > /tmp/dotnet-install.log 2>&1 &
# 每 60s 看一次：du -sh /root/.dotnet ; tail -3 /tmp/dotnet-install.log
```

**备用 B：如果 CDN 太慢（实测约 51 KB/s）**

```bash
# 1) 先看 apt 源里有没有 9.0
apt-get update -qq && apt-cache search dotnet-sdk | head
# 2) 没有就加 Microsoft 源（Ubuntu 24.04）
wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/ms.deb
dpkg -i /tmp/ms.deb && apt-get update -qq && apt-get install -y dotnet-sdk-9.0
```

**验证**

```bash
export DOTNET_ROOT=/root/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet --version          # 期望 9.x
dotnet --list-sdks        # 必须包含 9.0.x
```

> 下载慢就**后台装**，同时先做 §4 里只涉及 Python 的 B/E 工作，不要空等。

### 2.3 Python venv

已存在 `/root/work/game_mods/PawnMind/.venv`。校验：

```bash
cd /root/work/game_mods/PawnMind
.venv/bin/python -c "import mcp, pydantic; print('ok')"
```

缺失时重建：

```bash
python3 -m venv .venv && .venv/bin/pip -q install "mcp==2.3.0" pydantic
```

### 2.4 环境变量

`setup_remote_host.sh` 会写 `.env.server`；手动装 SDK 的话自己建：

```bash
cat > /root/work/game_mods/PawnMind/.env.server <<'EOF'
export DOTNET=/root/.dotnet/dotnet
export GAME_MANAGED=/root/work/game_managed
export PAWNMIND_NEWTONSOFT_DLL=/root/work/game_managed/Newtonsoft.Json.dll
EOF
set -a; . /root/work/game_mods/PawnMind/.env.server; set +a
```

三个变量各自的作用：

| 变量 | 作用 | 不设的后果 |
|---|---|---|
| `DOTNET` | `test.sh`/`build.sh` 用的 dotnet 可执行 | 默认指向 Windows 路径，直接失败 |
| `GAME_MANAGED` | 适配器契约测试加载游戏 DLL 的目录 | 适配器测试 SKIP（491 项少跑） |
| `PAWNMIND_NEWTONSOFT_DLL` | 测试工程引用的 Newtonsoft 路径 | 编译失败（找不到引用） |

### 2.5 跑测试，核对期望数字

```bash
cd /root/work/game_mods/PawnMind
set -a; . .env.server; set +a

./test.sh
# 期望：passed=683 failed=0
#       contract gate tests: passed=53 failed=0
#       contract checks: 491
#       adapter contract OK

.venv/bin/python -B -m unittest discover -s agent/tests
# 期望：Ran 253 tests ... OK   （约 60–135 s，慢是正常的）

.venv/bin/python tools/gen_policy_catalog.py --check
# 期望：ok: ... matches tool_policy.json (sha256 48213300cc...)
```

数字对不上就**不要**开始改代码：先判定是环境问题还是代码漂移，并记录到报告里。

### 2.6 失败对照表

| 现象 | 原因 | 处理 |
|---|---|---|
| `error MSB3245: 未能解析引用 Newtonsoft.Json` | 没设 `PAWNMIND_NEWTONSOFT_DLL` | `set -a; . .env.server; set +a` |
| `SKIP adapter tests: game Assembly-CSharp.dll not found` | 没设 `GAME_MANAGED` 或路径错 | 检查 `/root/work/game_managed` |
| `dotnet: command not found` | PATH 没加 | `export PATH=/root/.dotnet:$PATH` |
| `NETSDK1045: 当前 .NET SDK 不支持 net9.0` | 装了 8.x | 装 9.x，别改 TargetFramework |
| `error NU1301` / 还原超时 | NuGet 网络 | 重试；测试工程不需要外部包，通常只是 SDK 首次还原 |
| `dubious ownership in repository` | rsync 导致属主变化 | `git config --global --add safe.directory /root/work/game_mods` |
| 根分区写满 | `bin/obj` 累积 | `find . -name obj -o -name bin \| xargs rm -rf`，保留源码 |

---

## 3. 仓库与提交规则

- 只在 `/root/work/game_mods` 工作；`_decomp/`、`game_managed/`、`.venv/`、`bin/`、`obj/` 都不进 git。
- **不加 remote、不 push**。提交只留在本地。
- 提交信息：Conventional Commits + 中文正文，例如：
  ```
  fix(pawnmind): drop Refresh()-backed can_report from the snapshot

  - 快照不再调用 StoreClient.CanReport()
  - 新增 fake 计数测试 PP-READ-01-fake
  - C# 690 / Python 256
  ```
- 每个模块一次提交，不要混。文档提交单独一次。
- 提交前必须跑完整套件（不是只跑单个用例）。

---

## 4. 当前工程状态

### 4.1 已完成（不要重做）

**A 模块**（提交见 `git log`，文件：`ModePolicy.cs`、`RunModeBinding.cs`、`EndpointPolicy.cs`、`PolicyCatalog.g.cs`、`BridgeConfig.cs`、`BridgeServer.cs`、`PawnMindMod.cs`、`tool_policy.py`、`pawnmind_client.py`、`mcp_server.py`、`tools/gen_policy_catalog.py`）。

它保证：

1. `run_mode` 缺失/非法/未确认 → `Locked`，除 `/health` 外全部 403。
2. `test` 必须 `run_mode=test` + `allow_test_mode=true`。
3. `/state`、`/events`、`/action` 在构建快照与 dispatch **之前**被同一 `ModePolicy` 拒绝。
4. `src/Core/PolicyCatalog.g.cs` 由 `agent/policy/tool_policy.json` 生成；改策略必须重跑生成器，否则 Python 测试失败。
5. `/health` 报告 `run_mode`、`run_mode_error`、`policy_schema`、`policy_sha256`、`play_ready`。
6. `play_allowlist` = **0**；`candidate_allowlist` = 73，**不得**转成 play。

### 4.2 未完成的真实缺陷（按严重度）

| # | 位置 | 问题 |
|---|---|---|
| 1 | `src/Bridge/StateSnapshot.cs:300` | `client.can_report` → `StoreClient.CanReport()` → `BargainUIManager.IsCurrentClientCoerced()` → `Refresh()`；纯读快照会重置谈判状态 |
| 2 | `src/Core/LegalAction.cs:111` | 谈价面板一开就列 4 个 bargain 方向，可能实际不可交易 → AI 误学 |
| 3 | `src/Bridge/ActionRouter.cs:262` | `MarkResponseUndelivered` 把已完成结果改写成 `outcome_unknown` |
| 4 | `dist` vs 已安装 DLL | 哈希不一致；**没有**重新安装，任何游戏内现象不可归因 |
| 5 | `play_allowlist` | 0；所有工具缺 live evidence |

### 4.3 状态数据

| 项 | 值 |
|---|---|
| 工具数 | full 114 / recording 104 |
| 策略 schema | 2 |
| 策略 sha256 | `48213300cc4544c1f082a1e683aaa1a1430ada07bbafa7214566cecb731d3ea4` |
| decisions | allow 73 / needs_evidence 30 / deny 11 |

---

## 5. 继续开发（严格按顺序）

### 通用规则（每个模块都适用）

1. **TDD**：先写失败测试 → `./test.sh <名字>` 看失败原因符合预期 → 最小实现 → 全绿 → 再重构。
2. 禁止改生产代码后补测试；禁止放宽断言；禁止只测 happy path。
3. 每模块至少覆盖：正常、拒绝、异常、超时。
4. 离线 PASS 与实机 `NOT_RUN` 必须分开写，不能混。
5. `src/Core/` 不得 `using UnityEngine`；游戏类型只能出现在 `src/Game/`；HTTP 线程不得直接碰游戏对象。
6. 新增游戏成员 → 先加 `test/PawnMind.AdapterTests/Program.cs` 契约。
7. 改策略 JSON → 重跑 `tools/gen_policy_catalog.py` → 跑 Python 测试（漂移测试会红直到生成物同步）。

### 5.1 B — 快照纯度（先做这个）

**目标**：让"读状态"在任何路径都不改游戏状态。

1. 写失败测试：`test/PawnMind.Tests/SnapshotPurityTests.cs`
   - fake 的 `BargainUIManager` 统计 `Refresh()` 次数；
   - 连续构建 10 次快照，断言 `Refresh` 计数 = 0；
   - 先让它红（当前 `can_report` 会触发）。
2. 修 `StateSnapshot.DescribeClient`：删掉 `can_report`，或换成 `reported` / `isWanted` 等纯字段（`_decomp/StoreClient.cs` 可查）。
3. 产出 `docs/audit/NIGHTLY_SNAPSHOT_CALLGRAPH.md`：`BuildJson → BuildState/BuildFlags → GameState.* → 原生 getter` 全树，每节点标 `pure | unknown | impure` + `_decomp` 行号。未知**不得**默认 pure。
4. 加 `src/Core/SnapshotFieldMap.cs` + 漂移测试：解析 `StateSnapshot.cs` 的调用，出现未登记成员即失败。
5. `PP-READ-05`：`LegalAction` 的 bargain 四向改成 `candidate` / `availability:"unknown"`；同步 `agent/mcp_server.py` 文案与测试。
6. `PP-READ-06`：旧字段缺失时 Python 侧稳定降级（无 KeyError）。

**验收**：`./test.sh` 全绿；新增测试在 `git revert` 掉修复后会红（自己验证一次）。

### 5.2 D — 执行/投递双轴 + `get_operation`

1. `ActionGate` 增加两轴：
   - `execution_status ∈ prepared | running | completed | rejected | failed | uncertain`
   - `delivery_status ∈ pending_delivery | delivered | undelivered`
2. `MarkResponseUndelivered` **只改投递轴**，保留 completed 证据。
3. 新增 `GET /operation?id=`（只读、不碰游戏）与 MCP 工具 `get_operation` / `wait_operation`（Python 有界轮询，不阻塞主线程）。
4. 新工具四处同步：`tool_policy.json` 条目 → 生成器 → 工具数/清单测试 → `RouteTable`/`EndpointPolicy`（若新增端点）。
5. 测试：`PP-ASYNC-01..05`、`PP-SAVE-01`、`PP-RECOVERY-01`、`PP-MODE-02b`。

**注意**：`wait_operation` 超时必须返回 `pending` + `operation_id`，不能返回假成功。

### 5.3 C — `confirm_trade` 守卫

1. 只改 `confirm_trade` 主路径 + `advance_dialogue` / `call_next_customer` 的最低适配。
2. B10–B16 七个静默拒绝分支逐个预检查，测试断言**派发 0 次**。
3. 证据分层：返回值/异常、物品 ID 归属、`IsClientBusinessDone`；**现金不变也能成交**（以物易物）。
4. 交易 Operation 与顾客对话 Workflow 分离；未提交不得报 `completed`。
5. 测试：`PP-TRADE-01..09`。

### 5.4 E — 首周 Skill 与离线评测

1. `skills/pawnmind-first-week/SKILL.md`：启动、观察、交易、对话、关店、睡觉、租金、错误分类、停止条件；引用真实工具名。
2. `docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md`：存档/seed、版本、模型、工具集版本、成功判据、失败码、成本、回合时长、人工介入次数。
3. trace validator：禁止用新 `action_id` 重试 `uncertain`；pending 时不得调冲突动作。
4. 通关率/效率一律 `NOT_MEASURED`。

### 5.5 G — 兼容静态预审

`docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md`：反射、后台线程、HTTP listener、打包 Python、`dist` 与安装不一致；Scanner 未安装就写 `NOT_RUN`。

### 5.6 F — 审阅与集成

- 每模块一份 `docs/reviews/NIGHTLY_<X>_REVIEW.md`，固定结构（Status / Reviewing commit / Scope / Real evidence / Invariants / Critical findings / Regressions / NOT_RUN / Reviewer independence）。
- **A 的审阅目前是 `NOT_REVIEWED`（上一个审阅 agent 被叫停）**，重跑一次。
- `docs/reviews/NIGHTLY_F_INTEGRATION_REVIEW.md`：C#↔Python 策略、Snapshot↔LegalAction↔Skill、Trade↔Operation↔Dialogue、Gate↔Recorder、Async↔Save、Play gate↔live evidence、Build↔Installed。

---

## 6. 测试分级与证据规则

| 层级 | 含义 | 例子 |
|---|---|---|
| `L0-static` | 纯逻辑/源码扫描 | `RunModeTests`、`tools/gen_policy_catalog.py --check` |
| `L1-fake` | 假实现/假 HTTP | `PolicyFingerprintTests` |
| `L2-integration-no-game` | 真 loopback socket，无游戏 | `EndpointGateTests`、`BridgeTransportTests` |
| `L3-real-game` | 需要游戏进程 | **一律 `NOT_RUN`** |

写报告时：空白 ≠ PASS；`PARTIAL` 要说明缺哪一半；不得用 fake 证据填 live evidence。

---

## 7. 硬边界（违反即返工）

- **不启动、不操作游戏**；不点 UI；不读写任何玩家存档。
- **不覆盖安装 DLL**；不发布；不推到远程；不碰 Workshop。
- **不伪造 live evidence**：`play_allowlist` 必须保持 0，不能因为离线测试全绿就填值。
- 不删除/注释失败测试；不为了让测试过而放宽 guard。
- 不新建第二套 Agent 主循环或第二套 OperationTracker。
- `_decomp/` 与 `game_managed/` 只用于分析，不进 `dist/`。

---

## 8. 交付物

| 文件 | 内容 |
|---|---|
| `docs/reports/NIGHTLY_BATCH_A_REPORT.md` | 更新为完整版（A–G 分章、提交 SHA、测试摘要、能否进下一阶段） |
| `docs/audit/NIGHTLY_TEST_MATRIX.md` | 每个 `PP-*` 的层级 / 命令 / PASS·FAIL·NOT_RUN / 证据 |
| `docs/audit/NIGHTLY_BASELINE.md` | 更新本批次结束时的 HEAD 与计数 |
| `docs/audit/NIGHTLY_SNAPSHOT_CALLGRAPH.md` | B 的调用链与纯度分类 |
| `docs/reviews/NIGHTLY_<A..G>_REVIEW.md` | 每模块独立审阅（含 A 的重跑） |
| `docs/reviews/NIGHTLY_F_INTEGRATION_REVIEW.md` | 跨层集成审阅 |
| `docs/plans/NEXT_LIVE_SESSION.md` | 实机验收清单（已存在，按新代码更新） |

---

## 9. 完成定义与停止规则

达到以下条件即可收工：

- B/C/D 能安全离线实现的部分已完成；缺原生证据的局部明确写 `BLOCKED`，不硬造。
- E/G 有可用的草稿与审阅。
- 全量测试重跑且数字可复现（C# / 门禁 / 契约 / Python 四项）。
- 每模块一份审阅 + 一份集成审阅。
- `play_allowlist` 仍为 0；未启动游戏；未装 DLL；未推送。

**遇到必须真机才能推进的项**：写 `NOT_RUN` + 精确复现步骤，然后继续下一个独立任务，不要停下来等人。

---

## 10. 回传到本地工作站

```bash
# 在本地 WSL 执行（不是服务器）
rsync -a -e ssh root@192.168.1.101:/root/work/game_mods/ /mnt/d/code/game_mods/
```

反向同步（本地 → 服务器）会 `--delete` 掉服务器上的新提交，**不要在服务器有未回传提交时执行**。

---

## 附：命令速查

```bash
cd /root/work/game_mods/PawnMind
set -a; . .env.server; set +a

./test.sh                     # C# 683 + 门禁 53 + 契约 491
./test.sh RunMode             # 只跑名字匹配
PAWNMIND_BUILD_ONLY=1 ./build.sh   # 只编译 Mod（Unity 目标），不打包

.venv/bin/python -B -m unittest discover -s agent/tests
.venv/bin/python tools/gen_policy_catalog.py --check
.venv/bin/python tools/gen_policy_catalog.py

git log --oneline -5
git status --porcelain
```
