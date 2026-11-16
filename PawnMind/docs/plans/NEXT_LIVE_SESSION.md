# NEXT_LIVE_SESSION — 用户醒来后的实机验收清单

> 目标：用**最少步骤**确认"当前代码在真实游戏里是什么样"，而不是证明它一定对。
> 前置：`63ae0ef` 之后，`dist` 与已安装 DLL **仍然不一致**，所以现在游戏里的现象与仓库无关。

## 0. 先决条件（必须由你确认）

| 项 | 需要你做的 | 为什么 |
|---|---|---|
| 录制槽 | 确认专用槽号，并确认它不是你在玩的档 | 任何写操作都要过 S4 策略，默认全锁 |
| 游戏版本 | 确认仍是 0.46D / `VERSION 100-Rev13` | 版本变了要重跑契约 |
| 时间 | 需要游戏开着约 10 分钟 | 只做只读探测，不推进游戏 |

## 1. 构建与安装（你在场时执行）

```bash
cd /mnt/d/code/game_mods/PawnMind
./build.sh                       # 先跑全量测试，再打包 dist/PawnMind
sha256sum dist/PawnMind/PawnMind.dll
```

退出游戏后覆盖安装：

```bash
cp dist/PawnMind/PawnMind.dll "/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Mods/PawnMind/"
sha256sum "/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Mods/PawnMind/PawnMind.dll"
# 两行哈希必须相同，否则后面的现象不可信
```

## 2. 配置（只改这两行，其余不动）

`%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\pawnmind\bridge.conf`：

```ini
run_mode=bootstrap
allow_test_mode=false
```

> 不写 `run_mode` 会保持 `Locked`：除 `/health` 外全部 403。这是有意的。
> 主菜单阶段用 `bootstrap`；进入游戏后再改成 `play` 并重启游戏。
> `play` 目前允许集为 **0**（没有任何 live evidence），所以 `play` 下仍然什么都做不了——这是预期，不要在实机里改成 `test`。

## 3. 只读验收（不需要动游戏状态）

启动游戏，在主菜单：

```bash
curl -s http://127.0.0.1:15528/health | python -m json.tool
```

检查并记录：

| 字段 | 期望 |
|---|---|
| `run_mode` | `bootstrap` |
| `run_mode_error` | `null` |
| `policy_schema` / `policy_sha256` | `2` / `48213300…` |
| `game_assembly_mvid` | 与 `bridge.conf` 的 `expected_assembly_mvid` 一致（若未配置则先记录实际值） |
| `play_ready` | `false` |

再验证门禁（应全部 403）：

```bash
curl -s -o /dev/null -w "%{http_code}\n" -H "Authorization: Bearer <token>" http://127.0.0.1:15528/state
curl -s -o /dev/null -w "%{http_code}\n" -H "Authorization: Bearer <token>" "http://127.0.0.1:15528/events?since=0&timeout_ms=100"
```

## 4. 需要新构建才能做的两项（当前代码做不到）

| 项 | 缺什么 | 说明 |
|---|---|---|
| `PP-READ-01`（快照纯度实测） | `GameState` 里没有 `Refresh()` 调用计数 | 需要一次插桩构建：静态计数 + `/health` 暴露 `refresh_calls`，然后连开 10 次 `/state` 看计数是否变化 |
| `PP-MODE-01`（真实 MCP 全链路） | DSH 侧 E2E | 现在是 MCP 单测 + 裸 HTTP 测试，缺"模型真的调用工具"的记录 |

在这两项完成前，不要把 `play_allowlist` 填任何值。

## 5. 需要你在场才能决定的

1. 是否允许用专用槽开一局新游戏（`confirm_new_game`）——这是录制的前置，也会写存档目录。
2. 是否允许安装 Scanner 并跑一次（当前 `NOT_RUN`）。
3. 是否要录制；录制前必须先有 `PP-READ-01` 的计数证据。

## 6. 回滚

```bash
# 恢复旧 DLL（如果你备份过）
cp "<备份>/PawnMind.dll" "/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Mods/PawnMind/"
# 或者直接删除 Mods/PawnMind 并清空 bridge.conf 的 run_mode
```

代码侧回滚：`git revert 63ae0ef`（会同时撤掉模式门禁，不建议；只回滚文档/生成脚本可单独 `git revert --no-commit` 后挑选）。

## 7. 记录模板

把下面这段填好发回来，我就能把对应 `NOT_RUN` 改成 PASS/FAIL：

```
HEAD=63ae0ef
installed_dll_sha256=
dist_dll_sha256=
health.run_mode=
health.policy_sha256=
health.game_assembly_mvid=
expected_assembly_mvid=
GET /state (bootstrap) = <status>
GET /events (bootstrap) = <status>
Refresh counter (若已插桩) =
```
