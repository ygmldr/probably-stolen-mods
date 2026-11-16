# NIGHTLY_BASELINE — Nightly Batch A 基线冻结

> 采集：2026-11-17 03:19–03:55 (Asia/Shanghai)，本地 WSL 仓库 + `CT101` 构建机。
> 未启动游戏，未读写存档，未覆盖安装。所有实机项为 `NOT_RUN`。

## 1. 仓库

| 项 | 采集前 | 本批次提交后 |
|---|---|---|
| HEAD | `70e9d0d` | `63ae0ef` |
| 分支 | `main` | `main` |
| 工作树 | 干净（仅未跟踪的任务书） | 干净 |
| 远程 | 未推送 | 未推送 |

`70e9d0d` 是 `ROUND3_REMEDIATION.md` 描述的状态。相对该文档的差异：

- 文档称"快照已移除 `can_report`"，实际 `StateSnapshot.DescribeClient` 仍在调用 `StoreClient.CanReport()`
  → 该方法经 `BargainUIManager.IsCurrentClientCoerced()` 调用 `Refresh()`，是**仍然存在的纯度缺陷**（B 模块待修）。
- 文档称 `ModePolicy` 默认 `Test`，已确认；本批次改为默认 `Locked`。
- 其余（`GetBargainSnapshot` 只留 `open`、6 个快照读取别名、`candidate_allowlist` 73 / `play_allowlist` 0）与文档一致。

## 2. 环境

| 主机 | 用途 | 关键事实 |
|---|---|---|
| WSL 工作站 | 编辑 + 全量测试（dotnet.exe） | `192.168.1.5`，Windows SDK 跑 `net9.0` 测试 |
| `CT101` `192.168.1.101` | 迁移目标 / 构建镜像 | 12 核 / 20 GB / 17 GB 空闲；Python 3.12.3；**无 .NET SDK**（按用户要求留给服务器侧 agent 安装） |

服务器细节见 `docs/plans/SERVER_MIGRATION_2026-11-17.md`。

## 3. 测试基线

| 套件 | 本批次前 | 本批次后（本地） | 服务器 |
|---|---|---|---|
| C# 纯逻辑 | 666 passed / 0 failed | **683 passed / 0 failed** | NOT_RUN（缺 SDK） |
| 契约门禁 | 53 | **53** | NOT_RUN |
| 适配器契约 | 491 | **491** | NOT_RUN（DLL 已就位） |
| Python | 248 OK / 131.9 s | **253 OK / 134.1 s** | 248 OK / 60.7 s（迁移时点，早于新增 5 个用例） |

命令：

```bash
cd PawnMind && ./test.sh
cd PawnMind && .venv/bin/python -B -m unittest discover -s agent/tests
```

## 4. 策略与工具面

| 项 | 值 |
|---|---|
| `tool_policy.json` schema | `2` |
| canonical sha256 | `48213300cc4544c1f082a1e683aaa1a1430ada07bbafa7214566cecb731d3ea4` |
| `validate()` | `[]` |
| decisions | allow 73 / needs_evidence 30 / deny 11 |
| purity | pure 53 / write 48 / privileged 10 / cached_impure 2 / mutating 1 |
| bootstrap allowlist | 48（与 `SNAPSHOT_READERS` 交集为空） |
| candidate allowlist | 73 |
| **play allowlist** | **0**（无任何 live evidence） |
| 工具数 | full 114 / recording 104 |

`src/Core/PolicyCatalog.g.cs` 由 `tools/gen_policy_catalog.py` 从同一 JSON 生成，Python 测试
`test_generated_csharp_catalog_matches_the_policy` 在漂移时失败。

## 5. 构建一致性

| 项 | 值 |
|---|---|
| `dist/PawnMind/PawnMind.dll` | `ad335987…`，296,960 B，2026-11-17 01:58 |
| 已安装 `Mods/PawnMind/PawnMind.dll` | `e9e1bd07…`，295,424 B，2026-11-16 23:32 |
| 一致性 | **不一致**；本批次未重装，因此游戏内现象不能归因于 `63ae0ef` |

## 6. 本批次改动边界

- 只改离线代码与文档；没有运行游戏、没有写存档、没有安装 DLL、没有推送。
- `dist/` 未重建（`build.sh` 会打包，本次不需要）。
- 迁移：仓库 + venv + `_decomp` + 游戏 Managed DLL 已复制到 `CT101`，见迁移文档。

## 7. NOT_RUN 清单

游戏启动、`/health` MVID 核对、`Refresh()` 运行时计数、Scanner、录制、租金支付、交易成功路径、房东/开店守卫的实机验证，全部 `NOT_RUN`。
