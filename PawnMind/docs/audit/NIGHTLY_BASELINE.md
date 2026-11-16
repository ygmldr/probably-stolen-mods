# NIGHTLY_BASELINE — Nightly Batch A 基线冻结（服务器侧续跑版）

> 采集/收尾：2026-11-17 03:54–05:0x (Asia/Shanghai)，主机 `CT101`（`/root/work/game_mods`）。
> 未启动游戏，未读写存档，未覆盖安装，未推送远程。所有实机项 `NOT_RUN`。
>
> 上一版基线（WSL + A 模块）提交见 `docs/reports/NIGHTLY_BATCH_A_REPORT.md`；本文件记录本批次
> **最终** HEAD、测试计数与策略指纹，供下一次会话对照。

## 1. 仓库

| 项 | 值 |
|---|---|
| 起始 HEAD | `3369a31`（docs: server-side agent bootstrap and continuation guide） |
| 本批次提交（按时间） | `4846189` chore(.env.server) → `6ab3333` B → `f09314e` D+C → `299ecd4` E → `f789a92` G → 收尾 `docs/reports|audit|plans` 提交（`git log -1`） |
| A 模块提交 | `63ae0ef`（本批次之前已存在，本次补审阅） |
| 分支 / 远程 | `main` / 未推送（本批次不加 remote） |
| 收尾工作树 | 干净（审查文档提交后） |

## 2. 环境（已从零搭好）

| 项 | 值 |
|---|---|
| 系统 | Ubuntu 24.04 x86_64，12 核 / 20 GB |
| .NET SDK | **9.0.321**（`/root/.dotnet`，`/usr/local/bin/dotnet` 软链，本批次新装） |
| Python venv | `PawnMind/.venv`（mcp 2.3.0 / pydantic），`python -V` = 3.12.3 |
| 环境文件 | `PawnMind/.env.server`（DOTNET/GAME_MANAGED/PAWNMIND_NEWTONSOFT_DLL；已 gitignore） |
| 游戏 DLL | `/root/work/game_managed`（Assembly-CSharp + Newtonsoft + 161 DLL，只读引用） |
| 编译 gate | `dotnet build src/PawnMind.csproj -c Release -p:GameManaged=/root/work/game_managed` → **Build succeeded 0 warnings 0 errors**（服务器上可跑，见 G 矩阵 NR-06 更正） |

## 3. 测试基线（本批次末，CT101 实测）

| 套件 | 起始 | 本批次末 | 命令 |
|---|---|---|---|
| C# 纯逻辑 | 683 passed / 0 failed | **750 passed / 0 failed** | `cd PawnMind && ./test.sh` |
| 契约门禁（脚本级） | 53 / 0 | **53 / 0** | 同上（自动追加） |
| 适配器契约 | 491 checks | **503 checks**（+12 C 模块） | 同上（自动追加） |
| Python agent | 253 OK | **365 OK** | `.venv/bin/python -B -m unittest discover -s agent/tests` |
| 策略生成物 | ok（sha 48213300） | **ok（sha256 `e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc`）** | `.venv/bin/python tools/gen_policy_catalog.py --check` |
| Unity 目标编译 gate | NOT_RUN（本机无 SDK） | **PASS（Build succeeded）** | 见 §2 |

## 4. 策略与工具面

| 项 | 值 |
|---|---|
| `tool_policy.json` schema | `2` |
| canonical sha256 | `e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc` |
| `validate()` | `[]` |
| decisions | allow 75 / needs_evidence 30 / deny 11（共 116） |
| purity | pure 55 / write 48 / privileged 10 / cached_impure 2 / mutating 1 |
| bootstrap allowlist | **50**（+`get_operation`/`wait_operation`；与 `SNAPSHOT_READERS` 交集为空） |
| candidate allowlist（play） | **75** |
| **play allowlist（strict）** | **0**（无任何 live evidence；本批次没有任何东西改过它） |
| 工具数 | full **116** / recording **106**（+get_operation/wait_operation） |
| 新增工具 | `get_operation`、`wait_operation`（只读；`GET /operation`；不是 SNAPSHOT_READER） |
| 快照读取别名 | 6 个不变（`PolicyCatalog.SnapshotReaders`） |

## 5. 构建一致性（只记录，不安装）

| 项 | 值 |
|---|---|
| `dist/PawnMind/PawnMind.dll` | `ad3359875348ff672ff15f520ac9dd518f38221e1eb3ae85a287ea13930185f7`（296,960 B，2026-11-17 01:58，**旧**） |
| 已安装 `Mods/PawnMind/PawnMind.dll` | 历史安装 `e9e1bd07…`（295,424 B，见 `BASELINE_CURRENT.md:31`），本机不可访问 → `NOT_RUN` |
| 本批次离线构建产物 | `src/bin/Release/netstandard2.1/PawnMind.dll` = `d29209a44d2ede7e2e41010ff89abfa4409493155f12d357effe70b544c87eec`（338,432 B；编译 gate 产物，未打包进 dist） |
| 一致性 | **不一致**：dist ≠ 源码 ≠ 已安装。游戏内任何现象**不能**归因于本批次代码 |

版本三处不一致（G 模块发现）：`manifest.xml` 0.3.0 / git tag v1.0.1 / README+AGENTS 仍写 0.2.1；
本批次**未**改 manifest 与 tag（发布事项，需人工确认）。

## 6. 本批次改动边界

- 只改离线代码、测试与文档；新增 2 个只读 MCP 工具与 `GET /operation` 端点。
- 没有运行游戏、没有写玩家存档、没有安装/覆盖 DLL、没有发布、没有加 remote/push。
- `_decomp/`、`game_managed/`、`.venv/`、`bin/obj/`、`.env.server` 均未进 git。
- `dist/` 未重建。

## 7. NOT_RUN 清单（需要游戏/真机，全部未执行）

1. 游戏启动、`/health` 的 `game_assembly_mvid` 与 `expected_assembly_mvid` 核对。
2. `PP-READ-01-real`：Unity 运行时 `BargainUIManager.Refresh()` 调用计数（需插桩构建）。
3. 官方 ModScanner `mod-scan`；`APIChanges.md` / `ModdingGuide.md` 获取与核对。
4. 真实录制（专用槽）、`confirm_new_game`、租金支付/结算回执。
5. `confirm_trade` 真实点击成功路径、B10–B16 分支的真机现象。
6. `end_day` 快门动画 / `sleep` 黑屏 + 落盘的真实完成时序。
7. `PP-MODE-01` 真实 MCP/DSH 端到端；`PP-PLAY-01` play 模式。
8. `First Week benchmark`（L3）通关率/效率 = `NOT_MEASURED`。
9. dist 打包/安装一致性（`PAWNMIND_BUILD_ONLY=1` 之外的打包与安装）。
