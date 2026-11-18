# MOD_SCANNER_ROUND4 — 官方 ModScanner 证据（Round 4 / F7）

> **结论：`LIVE mod-scan = NOT_RUN`，`Workshop = BLOCKED`；但 `files` 已被
> 游戏加载器自己的审计行 LIVE 确认。**
> §1–§4 是静态（SRC）分析与采集步骤；§5 是 2026-11-18 实机采集到的
> 加载器审计行与 `/health` 指纹。**仍没有 `mod-scan` 的逐条 finding 输出**，
> 因此不把"预测"改写成"扫描确认"。

## 0. 为什么没有跑

按项目硬规则，Agent **不得**自行启动游戏、不得碰真实存档。官方 `mod-scan` 只能在
**运行中的游戏**（F8 控制台）里执行，因此本轮产出的是"扫描前必须成立的条件"和
"按官方规则预测会命中的项"。操作者执行 §4 后，把原始输出贴回来即可关闭 F7。

## 1. 指纹（已实测，可复现）

工具：`python3 scripts/audit/dll_fingerprint.py <dll>`（读取 PE/CLI 头的 `#GUID` 流，
输出 SHA256 与 MVID；MVID 与游戏侧 `Assembly.ManifestModule.ModuleVersionId` 同值）。

| 对象 | SHA256 | MVID |
|---|---|---|
| `PawnMind/dist/PawnMind/PawnMind.dll`（本轮构建，HEAD） | `6adef8ace8511e742053310b29be822905b42b5284f581af7f90ff1fdfceff78` | `4e3a88c5-1812-455d-9c7d-2d12c8d77dd0` |
| 已安装 `Mods/PawnMind/PawnMind.dll`（2026-11-18 覆盖为本轮构建） | `6adef8ace8511e742053310b29be822905b42b5284f581af7f90ff1fdfceff78` | `4e3a88c5-1812-455d-9c7d-2d12c8d77dd0` |
| ~~上一版已安装 DLL~~（备份在 `_internal/install-backup-2026-11-18/`） | `e9e1bd073d0ece5a05ecda6bfd51dfc285fc7c6a5b6626d38a820022c7084ca4` | `63388f67-299e-4f39-9fb8-7f33cb6a03d3` |
| 游戏 `Probably Stolen_Data/Managed/Assembly-CSharp.dll` | `725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05` | `911508a6-e0eb-4cc0-91e8-e14b16ea28d7` |
| `dist/PawnMind/manifest.xml` | `28a3d8b2bbeb79f28a8b7adc7a3af58b1e738090a544f4eb9de1b1ebe2ceb604` | — |

- 游戏程序集 MVID `911508a6-…` 与仓库内既有记录（`agent/fake_game.py` 的
  `FAKE_ASSEMBLY_MVID`）一致 → 本机安装的就是 **0.46D** 这一份，指纹可信。
- **`R4-F7-04` 已满足**：2026-11-18 由用户授权，Agent 在**游戏未运行**（`tasklist` 无
  `Probably Stolen.exe`）时把 `dist/PawnMind/{PawnMind.dll,manifest.xml}` 覆盖到
  `Mods/PawnMind/`，安装后 SHA256/MVID 与 `dist` 逐字节一致（见上表）。
  旧版 DLL 已备份到 `_internal/install-backup-2026-11-18/`（gitignored）。
  安装过程中**没有启动游戏**、没有读写任何存档。
- 构建方式：`cd PawnMind && ./build.sh`（只写 `dist/`，不安装、不碰游戏）。
- 策略指纹：`policy_sha256 = e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc`
  （`tools/gen_policy_catalog.py --check` 与 `tool_policy.json` 一致）。

## 2. 当前 manifest

```xml
<Capabilities>
  <Capability>network</Capability>
  <Capability>reflection-external</Capability>
</Capabilities>
```

未声明 `files`。以下按官方
[ModdingGuide — Capabilities and the Scanner](https://github.com/dragonpan2/probably-stolen-modding/blob/main/ModdingGuide.md#capabilities-and-the-scanner)
的触发表逐项对照（**SRC + 官方文档**，仍是预测）。

## 3. 预测的 finding 与 disposition

| # | capability | 触发点（SRC） | 官方规则 | 预测结果 | disposition |
|---|---|---|---|---|---|
| C1 | `files` | `src/Game/ScreenshotService.cs:157` `ScreenCapture.CaptureScreenshotAsTexture()` | 表中 `ScreenCapture` 明确列为 `files` 触发项 | **未声明 → 激活被拒**（`detected ⊄ declared`） | 必须处理，见 3.1 |
| C2 | `files` | `src/Game/ScreenshotService.cs:196,197,201,207` `System.IO.Directory.Exists/GetFiles`、`new System.IO.FileInfo` | `Directory`/`FileInfo` 作为调用计数 | 同上（同一能力，不额外加价） | 可用 `ModHelper.ListModDataFiles` 消除，见 3.1 |
| C3 | `reflection-external` | `src/Game/NewGameMenuAdapter.cs:400` `Type type = view.GetType(); type.GetField("perk"\|"inSelectedRow"\|"hovered"\|"frameImage")` | "computed type"（`someObject.GetType()`）→ `reflection-external` | **已声明**，今日通过，**DEPRECATED → 2026-12-10 起拒绝** | 见 3.2 |
| C4 | `reflection-external` | `typeof(X).GetField("literal")` 形式（`GameState.cs:17,120`、`GameVersionAdapter.cs:28`、`NewGameMenuAdapter.cs` 多处） | 字面类型 + 字面成员 → 与直接调用同价（免费） | 预测**不**计入 | 等扫描器确认 |
| C5 | `network` | `src/Bridge/BridgeServer.cs:218` `new TcpListener(IPAddress.Loopback, port)` | `System.Net.*` → `network` | **已声明** | 通过 |
| C6 | `threads` | — | `new Thread`/`Task.Run`/`ThreadPool`/`System.Threading.Timer`/非自有 MonoBehaviour 的 `StartCoroutine` | 静态扫描未发现（HTTP 全部在主线程 `Tick` 里 poll；`ScreenshotService` 是自有 `MonoBehaviour`，其 `StartCoroutine` 免费） | 预测不命中；若扫描器命中须重新评估 |
| C7 | `native` / `dynamic-code` | — | `DllImport`/`unsafe`/`Marshal`/`Assembly.Load`/`TypeNameHandling` | 静态扫描未发现 | 预测不命中 |
| C8 | 硬拒绝项 | — | `Process`、`UnpatchAll(null)`、改 loader/trust、Steam 写 | 静态扫描未发现（`src/` 无 `System.Diagnostics.Process`，无 `SteamUGC` 写） | 预测不命中 |
| C9 | `[ModRestricted]` 游戏方法 | — | `SaveFiles` 等（2026-12-10 起） | `src/Game` 只走玩家路径（`RecordingSaveAdapter` 调存档 UI/接口），未直接调用 `SaveFiles` | 需扫描器确认 |

### 3.1 `files`（C1/C2）——本轮不改代码，理由与选项

- **不能只换 `Directory`/`FileInfo`**：即使把它们改成 `ModHelper.ListModDataFiles`
  （官方免费替代），`ScreenCapture` 仍然触发 `files`。因此"删 manifest 字段"
  或"只改一半"都不成立。
- 三个选项（需要操作者/产品决定，本轮**不擅自选择**）：
  1. **声明 `files`** 并在 Workshop 描述里披露（截图功能确实会写自己的 ModData 文件夹）；
  2. **去掉 `ScreenCapture`**：截图功能整体关闭或改为用户手动截图，`files` 只剩 C2，
     再用 `ModHelper.ListModDataFiles` 消除，最终 **0 capability**；
  3. **保留现状 + 阻塞发布**。
- 现状：`capture_screenshot` 属于诊断面（不在 48 个写工具、不在 recording 106 工具里，
  见 `RECORDING_TOOL_NAMES` 之外的定义），关掉它对玩法闭环无影响。

### 3.2 `reflection-external`（C3）——已知会在 2026-12-10 被拒

`NewGamePerksUI.SlotView` 是**私有嵌套类**（`_decomp/NewGamePerksUI.cs:7`），
外部代码拿不到 `typeof(SlotView)`，只能 `view.GetType()` → 官方规则判为 computed
→ `reflection-external`。这意味着 **bootstrap 的 `get_new_game_options` 选项行读取**
在 2026-12-10 之后会被拒绝激活（不是运行时失败，是整个 Mod 不能被激活）。

可选项：
1. 向作者申请一个公开访问器（最干净）；
2. 放弃"逐行 perk 选择"读取，只保留场景/难度（若它们不依赖 `SlotView`）；
3. 接受 **Workshop BLOCKED**，仅本地开发者模式使用。

本轮**不**改动反射实现：没有扫描器输出前重构，等于用猜测替换证据（brief §6.1）。

## 4. 操作者采集步骤（拿到原始输出即可关闭 F7）

前置（`R4-F7-04` 已于 2026-11-18 完成，可跳过第 1 步）：
```bash
cd PawnMind && ./build.sh          # dist 必须等于 HEAD
python3 ../scripts/audit/dll_fingerprint.py dist/PawnMind/PawnMind.dll
sha256sum dist/PawnMind/manifest.xml
git rev-parse HEAD
```
1. 退出游戏，把 `dist/PawnMind/PawnMind.dll` 与 `dist/PawnMind/manifest.xml`
   复制到 `.../Probably Stolen/Mods/PawnMind/`（覆盖）。
2. 确认 `dev.txt` 存在于游戏根目录（F8 控制台需要），启动游戏。
3. 打开 F8，依次执行并**完整保存原始输出**：
   - `mod-scan pawnmind`（或 `mod-scan <Mods/PawnMind 的绝对路径>`）
   - `mod-trust`（记录该 mod 的 approval 状态与 capabilities）
4. 把输出贴回仓库（`docs/audit/MOD_SCANNER_ROUND4.md` 追加"§5 原始输出"），并记录：
   - 游戏版本字符串、`Assembly-CSharp.dll` 的 MVID；
   - `declared` / `detected` / `DEPRECATED` / `from December 10, 2026` / `ERROR:` / 硬 flag / `fingerprint:`；
   - 每条 finding 的 `capability: Type.Method -> 引用`。
5. 复扫：按 §3 的结论改代码/清单后，重复 3–4，确认 `detected ⊆ declared` 且无硬 flag。

**不做**的事：不改 scanner、不开 developer mode 绕过、不改 trust 层、不删 manifest 字段了事。

## 5. Gate C 判定

| 门槛 | 状态 |
|---|---|
| `R4-F7-01` 原始 scanner 日志 + 指纹 | **NOT_RUN**（指纹部分已就绪，见 §1） |
| `R4-F7-02` 每项 finding 有定位与 disposition | **PARTIAL**（静态预测已完成，扫描未确认） |
| `R4-F7-03` 复扫无硬拒绝、`declared ⊇ detected` | **BLOCKED**（C1 预测即拒绝） |
| `R4-F7-04` 已安装 DLL 与本轮构建匹配 | **PASS**（§1：`6adef8ac…` / `4e3a88c5…` 一致） |
| `R4-F7-05` 区分"当前可启动"与"2026-12-10 后可启动" | **DONE**（§3.2） |

> **Gate C = BLOCKED。** 不得用静态分析或单元测试替代 LIVE 扫描结论。

---

## 5. LIVE 证据（2026-11-18 23:19–23:23，用户启动游戏）

游戏侧文件全部是**只读采集**：用户自己启动游戏；Agent 未启动、未操作游戏，
未读写任何存档。

### 5.1 加载器审计行（决定性）

`%LOCALAPPDATA%Low\Questing Goose Studio\Probably Stolen\mod-audit.log`
（2026-11-18T15:21:41Z ≈ 本地 23:21）：

```
[ModLoader] audit: pawnmind v0.3.0 source=Local workshop=0 owner=0 running=True
  trust=DeveloperMode caps=files, network, reflection-external hash=cb148a74…
[ModLoader] Loading: PawnMind.dll
[PawnMind] loaded PawnMind · AI Bridge v0.3.0
```

对照 `manifest.xml`（`<Capability>network</Capability>` + `reflection-external`）：

| | 集合 |
|---|---|
| `declared` | `network`, `reflection-external` |
| 加载器报告的 `caps` | **`files`**, `network`, `reflection-external` |
| 差异 | **`files` 被检测到但未声明** |

**这直接证实了 §3 的预测 C1/C2。** 该 Mod 当前之所以能运行，是因为
`trust=DeveloperMode`（开发者模式跳过"未声明的能力"检查，见官方指南
*Developer mode*）。在一台普通玩家机器上，`detected ⊄ declared` 会**拒绝激活**。

> 因此 F7 的发布结论不需要再等 `mod-scan` 才能定性：**`files` 必须处理**。
> 仍需要 `mod-scan` 的是：逐条 finding 的 `Type.Method -> 引用`、
> `DEPRECATED` 行、`from December 10, 2026` 列表与 `fingerprint:`。

### 5.2 `/health` 指纹（LIVE）

```json
{"ok":true,"name":"pawnmind","version":"0.3.0","mod_version":"0.3.0",
 "game_version":"VERSION 100-Rev13",
 "game_assembly_mvid":"911508a6-e0eb-4cc0-91e8-e14b16ea28d7",
 "expected_game_version":"VERSION 100-Rev13",
 "expected_assembly_mvid":"911508a6-e0eb-4cc0-91e8-e14b16ea28d7",
 "write_gate":"open","write_gate_reason":null,
 "latest":"UNVERIFIED","latest_verified":false,
 "run_mode":"locked","run_mode_error":"run_mode_missing",
 "policy_schema":"2",
 "policy_sha256":"e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc",
 "play_ready":false}
```

- 运行中的 MVID = 仓库记录的 0.46D MVID = 我用 `dll_fingerprint.py` 从
  `Assembly-CSharp.dll` 读出的 MVID → **三方一致**；
- `policy_sha256` 与仓库 `tool_policy.json` / `PolicyCatalog.g.cs` 一致
  → 已安装 DLL 确实由本仓库策略构建（与 §1 的 SHA 一致互为佐证）；
- `run_mode:"locked"` + `run_mode_error:"run_mode_missing"`：
  `ModData/pawnmind/bridge.conf` 里没有 `run_mode`，因此除 `/health` 外全部拒绝。
  **这是设计内的 fail-closed**，不是故障；也意味着此刻 MCP 会解析成 `locked`
  （F5b 的行为在实机上一致）。
- `play_ready:false`、`latest:"UNVERIFIED"`：未做任何玩法验证。

### 5.3 为什么还没有 `mod-scan`

用户按了 F8，但 `Player.log` 记录：

```
[DevTools] Developer tools disabled: no dev.txt next to the executable
  (D:\Program Files\steam\steamapps\common\Probably Stolen\dev.txt).
```

官方指南要求游戏根目录存在 `dev.txt`，且**只在启动时检查一次**。
2026-11-18 23:23 已创建空 `dev.txt`；需要**重启游戏**后 F8 才会响应。
采集完成后请删除 `dev.txt`（它会同时打开 F9 开发菜单，那个菜单能直接改存档）。

### 5.4 更新后的 Gate C

| 门槛 | 状态 |
|---|---|
| `R4-F7-01` 原始 scanner 日志 + 指纹 | 指纹 **LIVE 完成**（§5.2）；`mod-scan` 原始输出 **NOT_RUN** |
| `R4-F7-02` 每项 finding 有定位与 disposition | `files` **LIVE 确认**（§5.1）；逐条定位仍待 `mod-scan` |
| `R4-F7-03` 复扫无硬拒绝 | **BLOCKED**（`files` 未声明 → 普通玩家激活会被拒） |
| `R4-F7-04` 已安装 DLL 与本轮构建匹配 | **PASS**（§1） |
| `R4-F7-05` 区分"当前可启动"与"2026-12-10 后" | **DONE**；注意 §5.1 表明"当前可启动"依赖开发者模式 |
