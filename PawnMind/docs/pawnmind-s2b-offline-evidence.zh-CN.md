# PawnMind S2B：运行时版本/程序集门禁与只读健康身份

**结论**：S2B 离线代码完成。仅在已安装的 `VERSION 100-Rev13`（观测 MVID `911508a6-e0eb-4cc0-91e8-e14b16ea28d7`）上验证，**不证明** Steam 正式稳定分支最新版；`latest` 始终为 `UNVERIFIED`。未启动游戏、未安装/覆盖 Mod、未读写真实存档、未运行 `build.sh`/写 `dist/`、未改 `manifest.xml`。S8 人工核对 Steam stable 并显式填写 expected 之前，所有写动作保持锁定。

## 交付内容

| 文件 | 内容 |
|---|---|
| [GameVersionGate.cs](<../src/Core/GameVersionGate.cs>) | 纯逻辑身份比较；缺失/非法 expected、运行时身份不可用、版本不符、MVID 变化均 fail-closed；MVID 归一化（大小写/连字符） |
| [BridgeHealth.cs](<../src/Core/BridgeHealth.cs>) | `/health` 只读 DTO；`Latest` 固定 `UNVERIFIED` |
| [BridgeConfig.cs](<../src/Core/BridgeConfig.cs>) | 新增 `expected_game_version` / `expected_assembly_mvid`；默认空；不自动填当前安装版 |
| [GameVersionAdapter.cs](<../src/Game/GameVersionAdapter.cs>) | Game 层读取 `PlayerStore.VERSION_NUMBER`（反射，避免 const 编译期内联）和 `typeof(PlayerStore).Assembly.ManifestModule.ModuleVersionId`；不读文件/存档/隐藏字段 |
| [ActionRouter.cs](<../src/Bridge/ActionRouter.cs>) | 所有非只读 op 在 dispatch 前过 version gate；纯读不受影响；重复/未知结果缓存优先 |
| [BridgeServer.cs](<../src/Bridge/BridgeServer.cs>) | `/health` 输出 mod/game identity、expected、gate state、`latest: UNVERIFIED`；token 回显脱敏 |
| [PawnMindMod.cs](<../src/PawnMindMod.cs>) | 在 `OnEnable`（Unity 主线程）捕获身份并计算唯一 gate；HTTP 只读缓存字符串 |
| [GameVersionGateTests.cs](<../test/PawnMind.Tests/GameVersionGateTests.cs>) + 既有 tests | 纯逻辑、config、router、health 正负例 |

## 门禁语义

- `expected_game_version` 与 `expected_assembly_mvid` 必须同时非空且匹配运行时身份，写门才为 `open`；任一缺失即 `locked`。
- 稳定错误码：
  - `version_gate_unconfigured`：expected 缺失（默认状态，不会自动批准当前安装版）
  - `version_gate_invalid`：expected MVID 格式非法
  - `version_identity_unavailable`：运行时 version/MVID 读取失败
  - `game_version_mismatch`：版本字符串不符
  - `game_assembly_mvid_mismatch`：MVID 变化
- 只读 op、`ping`、`get_action_history` 不受影响；写被拒时返回 `{ok:false,error:<code>,write_gate:"locked",dispatched:false,rev:<current>}`，不写缓存，因此开后可用同一 `action_id` 重试。
- 已执行写动作的重复请求、以及 `outcome_unknown` 缓存仍优先返回首次结果（保持 S1 行为），不会被后续锁门改写。

## `/health` 形状

```json
{
  "ok": true,
  "name": "pawnmind",
  "version": "0.2.1",
  "mod_version": "0.2.1",
  "game_version": "VERSION 100-Rev13",
  "game_assembly_mvid": "911508a6-e0eb-4cc0-91e8-e14b16ea28d7",
  "expected_game_version": null,
  "expected_assembly_mvid": null,
  "write_gate": "locked",
  "write_gate_reason": "version_gate_unconfigured",
  "latest": "UNVERIFIED",
  "latest_verified": false
}
```

`/health` 仍无需 token，但只含构建身份/门禁状态；不返回 token、存档、路径或隐藏字段，并对配置中意外回显的 token 做 `[redacted]`。

## S8 配置方式（人工）

在 `ModData/pawnmind/bridge.conf` 中，只有人工核对 Steam stable 与目标 DLL 后才填写：

```ini
expected_game_version=VERSION 100-Rev13
expected_assembly_mvid=911508a6-e0eb-4cc0-91e8-e14b16ea28d7
```

空值表示继续锁定；修改后需重启 Mod（gate 在 `OnEnable` 计算一次）。当前安装版只作暂定兼容，不是拍摄时最新版结论。

## Red / Green 证据

- **Red**：先写测试后运行 `./test.sh`，编译失败明确缺少 `WriteGateDecision`、`BridgeHealth`、`GameVersionGate` 等新契约；随后才实现生产代码。
- **Green**：`REQUIRE_GAME=1 ./test.sh` 退出 0；纯 C# **220** 项 `passed=220 failed=0`，脚本门禁 **53** 项，适配器 **351** 项 `adapter contract OK` 且 `strict mode: REQUIRE_GAME=1`、无 SKIP。
- Python：`Ran 12 tests ... OK`，0 skipped。
- `dotnet build src -c Release --no-incremental`：0 warning、0 error。
- 构建产物反查：不含编译期内联的 `VERSION 100-Rev13` 字符串（证明运行时走反射读取），包含 `version_gate_unconfigured` / `game_assembly_mvid_mismatch` / `latest_verified` 等门禁字符串。
- `git diff --check` 无错误；未改 S2A 的 `test.sh`/`build.sh`/适配器契约，未写 `dist/`。

## 剩余风险 / 待 S8

1. **Runtime Game adapter 未被纯逻辑测试执行**：纯测试不引用 Unity/游戏 DLL；已用 Release 编译和只读反射探针核对，但最终须在 S8 `/health` 对比所选 DLL 的 version/MVID。
2. **S2A 适配器契约仍钉精确 `VERSION_NUMBER` 值**：换新版 DLL 时 strict 适配器会先失败，这是强制重新调查的门禁，不是运行时锁；运行时门禁本身只读 config expected，不含 0.46D/Rev13 硬编码。换版时需按当时 DLL 重新基线化契约。
3. **gate 只在 `OnEnable` 计算一次**：修改 `bridge.conf` 后必须重启 Mod；运行中不会热加载。
4. **`latest` 永远 `UNVERIFIED`**：代码不访问 Steam，不能自动证明最新；只有 S8 人工核对并填写 expected 才能解锁写入。
5. **未真机验证**：主线程路径未改（仍由 `MainThreadPump.Update` 驱动 `BridgeServer.Tick`），但未测真实帧率/长时间连接。
6. **既有配置升级**：旧 `bridge.conf` 没有 expected 键时解析为空，写动作会锁定；这是预期的 fail-closed 行为。
7. **`/health` 为本地开放端点**：按需求输出构建身份/expected；不含 token/存档/隐藏字段，token 回显已脱敏，但仍应只监听 `127.0.0.1`（既有不变量）。
