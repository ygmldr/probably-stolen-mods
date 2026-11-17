# NEXT_LIVE_SESSION — 用户醒来后的实机验收清单（Nightly Batch A 之后）

> 目标：用**最少步骤**确认"当前仓库代码在真实游戏里是什么样"，而不是证明它一定对。
> 前置事实：`dist` 与已安装 DLL **仍不一致**，且本轮 `dist` 不是当前源码构建的 →
> 现在游戏里的任何现象**不能**归因于本批次代码。必须先完成 §1 再谈验收。
> 本批次没有启动游戏、没有写存档、没有安装 DLL、没有推送。

## 0. 先决条件（需要你确认）

| 项 | 需要你做的 | 为什么 |
|---|---|---|
| 游戏版本 | 确认仍是 0.46D / `VERSION 100-Rev13` / MVID `911508a6-e0eb-4cc0-91e8-e14b16ea28d7` | 变了要重跑契约与策略适用范围 |
| 录制槽 | 确认专用槽号且不是你在玩的档 | 任何写操作都要过 S4 策略，默认全锁 |
| 时间 | 游戏开着约 10–15 分钟 | 只做只读探测 + 少量受控写 |
| manifest 决定 | `files` capability 是否声明 / `reflection-external` 2026-12-10 retiring 如何应对 | 发布阻塞项，见 `NIGHTLY_MOD_COMPAT_MATRIX.md` Top3 |

## 1. 构建 + 安装（你在场时执行；未完成前不要看 §3 之后的结论）

```bash
# Windows/WSL 工作站
cd <repo>/PawnMind
./build.sh                        # 先跑全量测试，再打包 dist/PawnMind
sha256sum dist/PawnMind/PawnMind.dll
```

退出游戏后覆盖安装（**只在你确认要更新时**）：

```bash
cp dist/PawnMind/PawnMind.dll "/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Mods/PawnMind/"
sha256sum "/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Mods/PawnMind/PawnMind.dll"
# dist 与 installed 两行哈希必须相同，否则后续现象不可信
```

记录：`installed_dll_sha256`、`dist_dll_sha256`、安装时间。旧备份保留以便回滚。

## 2. 配置（只改这两行）

`%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\pawnmind\bridge.conf`：

```ini
run_mode=bootstrap
allow_test_mode=false
```

> 不写 `run_mode` 保持 `Locked`（除 `/health` 全 403，有意）。
> 主菜单用 `bootstrap`；进游戏后改 `play` 再重启。
> **`play` 允许集仍是 0**（没有任何 live evidence）——`play` 下什么都做不了是预期；
> 不要为了“跑通”改成 `test`，那会污染 live evidence 语义。

## 3. 只读验收（本批次新增项已包含）

```bash
curl -s http://127.0.0.1:15528/health | python -m json.tool
```

| 字段 | 期望 |
|---|---|
| `run_mode` / `run_mode_error` | `bootstrap` / `null` |
| `policy_schema` / `policy_sha256` | `2` / `e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc` |
| `play_ready` | `false` |
| `game_assembly_mvid` | 与 `bridge.conf` 的 `expected_assembly_mvid` 一致（不一致先记录实际值） |

门禁（应全部 403/401）：

```bash
T="Authorization: Bearer <token>"
curl -s -o /dev/null -w "state=%{http_code}\n"   -H "$T" http://127.0.0.1:15528/state
curl -s -o /dev/null -w "events=%{http_code}\n"  -H "$T" "http://127.0.0.1:15528/events?since=0&timeout_ms=100"
curl -s -o /dev/null -w "noauth=%{http_code}\n"  http://127.0.0.1:15528/operation?id=x
```

`GET /operation` 在 bootstrap 下**允许**（协议必要），但 Locked 下 403：

```bash
curl -s -H "$T" "http://127.0.0.1:15528/operation?id=never-issued" | python -m json.tool
# 期望：ok:false, known:false, error:"unknown_operation", next 提示不要盲重试
```

进入游戏后把 `run_mode` 改成 `test`（仅本机开发；看完即改回 bootstrap/删除）：
`GET /state` 应 200；连续开 10 次：

```bash
for i in $(seq 1 10); do curl -s -H "$T" http://127.0.0.1:15528/state > /dev/null; done
```

## 4. 需要插桩构建才能做的（当前代码做不到）

| 项 | 缺什么 | 怎么做 |
|---|---|---|
| `PP-READ-01-real`（快照纯度实测） | `GameState` 没有 `Refresh()` 调用计数 | 下一次插桩构建：`GameState` 静态计数器 + `/health` 暴露 `refresh_calls`；然后连开 10 次 `/state` 看计数是否变化（期望 0） |
| `PP-MODE-01`（真实 MCP 全链路） | DSH/host E2E | 用真实 MCP host 调一轮 `get_state` → `get_operation` → `wait_operation`，保存原始 transcript |
| `PP-ASYNC-real`（快门/黑屏/落盘） | 真实时序 | 见 §5 第 3–5 项 |

在这三项完成前，**不要**给 `play_allowlist` 填任何值、不要开录制。

## 5. 受控写验收（每步之间都要回读 state；失败立即停）

1. **交易拒绝路径**：在谈价面板打开时，用 `get_legal_actions` 看 bargain 只出现在
   `candidate_actions`（`availability=unknown`），不在 `legal_actions`；
   用 `bargain` 方向已用尽时调 `confirm_trade`，期望 `dispatched:false` + 稳定拒绝码。
2. **交易成功路径**：正常买/卖一次，记录响应里的 `business_complete`/`client_unchanged`/
   `items_transferred`/`cash_before`/`cash_after`；随后 `advance_dialogue` 直到顾客离店
   （交易 completed 不应阻塞对话）。
3. **end_day**：点关店后**立刻**读响应：若 `shop_closed_after=false` → 期望
   `ok:false + pending:true + execution_status:running`；稍后 `get_operation` 查询是否变 completed。
4. **sleep**（仅专用录制槽）：记录 `day_changed` 与 `save_evidence`；`last_save_time` 增加**不算**
   落盘成功，需要存档文件 mtime/size 等外部证据。
5. **run 切换**（new_game/load_game）：切换前后用 `get_operation` 查旧 id，期望
   `known:false + process_restarted:true`，且旧 id 不会被重放。
6. **barter/情报商/枪单**（有样本时）：验证 B13/B14/B15 真机现象与拒绝码。

## 6. 扫描器与发布

```bash
# 游戏内 F8 控制台（无进程则 NOT_RUN）
mod-scan pawnmind
mod-scan "<abs>/Mods/PawnMind"
# 离线 ScannerCheck（Windows）
cd CompactSort/test/ScannerCheck && dotnet run -c Release -- "D:\...\Mods\PawnMind"
```

记录命令、输出、Manifest 能力结论；**未安装扫描器就写 NOT_RUN**，不要用网传口径。

## 7. 需要你决策的

1. `manifest.xml` 是否声明 `files` capability（截图/存档索引功能需要），或改造/关闭那两处功能。
2. `reflection-external` 在 2026-12-10 起 retiring：14 处 computed-Type 反射是否改为字面量
   `typeof` 下放或官方 `ModReflect`。
3. 三处版本号统一（manifest 0.3.0 / tag v1.0.1 / 文档）与是否发版。
4. 是否允许安装官方 Scanner、是否允许专用槽开新局/录制。

## 8. 回滚

```bash
# 恢复旧 DLL（安装前备份）
cp "<备份>/PawnMind.dll" ".../Mods/PawnMind/"
# 或删除 Mods/PawnMind 并清空 bridge.conf 的 run_mode
```

代码侧：本批次模块提交可单独 `git revert`（`6ab3333` B / `f09314e` D+C / `299ecd4` E / `f789a92` G +
收尾 docs）；不建议整体回退 A 的 fail-closed 门禁。

## 9. 记录模板（填好发回即可把 NOT_RUN 改 PASS/FAIL）

```
HEAD=
installed_dll_sha256=
dist_dll_sha256=
health.run_mode=
health.policy_sha256=
health.game_assembly_mvid=
expected_assembly_mvid=
GET /state (bootstrap / play / test) =
GET /events (bootstrap) =
GET /operation?id=never-issued =
PP-READ-01 refresh counter (插桩后) =
confirm_trade: business_complete= client_unchanged= items_transferred= cash_before= cash_after=
end_day: shop_closed_after= execution_status= （稍后 get_operation =）
sleep: day_changed= save_evidence= 存档文件 mtime/size =
mod-scan 输出（或 NOT_RUN 理由） =
```
