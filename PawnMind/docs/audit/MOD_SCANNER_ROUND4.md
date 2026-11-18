# MOD_SCANNER_ROUND4 — 官方 ModScanner 证据（Round 4 / F7）

> **结论：官方 `mod-scan` 已跑通（2026-11-18 23:28）。**
> `declared: network, reflection-external` / `detected: files, network, reflection-external`
> → **`UNDECLARED (enable would be refused): files`**。
> 41 条 finding：`files` 5、`network` 22、`reflection-external` 14；
> 另有 `from December 10, 2026 (3)` 与一条 `DEPRECATED: reflection-external`。
> **Workshop = BLOCKED**（今日因 `files` 未声明，2026-12-10 起因 `reflection-external`）。
> 原始输出逐行转录见 §6；预测与实际对照见 §3。

## 0. 运行记录

按项目硬规则，Agent **不得**自行启动游戏、不得碰真实存档。官方 `mod-scan` 只能在
**运行中的游戏**（F8 控制台）里执行，所以流程是：

1. 2026-11-18 23:19 用户自行启动游戏；Agent 只读采集 `Player.log` / `mod-audit.log` / `/health`。
2. 首次 F8 无效：`Player.log` 记录 `Developer tools disabled: no dev.txt`。
   Agent 补建空 `dev.txt`（游戏根目录），用户重启游戏。
3. 23:28 用户在 F8 执行 `mod-scan pawnmind`，控制台输出由操作者截图为证，
   Agent 逐行转录为 §6。**Agent 全程没有启动、操作或读取游戏存档。**

与 §3 静态预测的差异见 §3.3：预测命中了 `files`，但**漏掉了 10 条 `reflection-external`**
（原因见 §3.3 的自我更正）。

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

## 3. 预测 vs 实测（逐项对照）

| # | capability | 触发点（SRC） | 官方规则 | 预测结果 | disposition |
|---|---|---|---|---|---|
| C1 | `files` | `src/Game/ScreenshotService.cs:157` `ScreenCapture.CaptureScreenshotAsTexture()` | 表中 `ScreenCapture` 明确列为 `files` 触发项 | **未声明 → 激活被拒**（`detected ⊄ declared`） | 必须处理，见 3.1 |
| C2 | `files` | `src/Game/ScreenshotService.cs:196,197,201,207` `System.IO.Directory.Exists/GetFiles`、`new System.IO.FileInfo` | `Directory`/`FileInfo` 作为调用计数 | 同上（同一能力，不额外加价） | 可用 `ModHelper.ListModDataFiles` 消除，见 3.1 |
| C3 | `reflection-external` | `src/Game/NewGameMenuAdapter.cs:400` `Type type = view.GetType(); type.GetField(...)` ×4 | "computed type" → `reflection-external` | **命中 4 条**（`ReadPerkSlots`） | 见 3.2；**结构性，无法用 `typeof()` 消除** |
| C3b | `reflection-external` | **预测漏项**：把 `typeof(X)` 作为参数传进泛型辅助方法后，辅助方法内部再 `Type.GetField/GetMethod` | "值经过方法调用/参数 → 算 computed" | **命中 10 条**（`StaticField`/`StaticProperty`/`InvokeStaticInt`/`InvokeVoid`/`InvokeStaticVoid`/`InvokeStaticVoidInt`/`InvokeStaticBool`/`ConstInt` + `RecordingSaveAdapter.StaticField`/`InvokeStaticVoid`） | 可用"调用点内联 `typeof(...)`"消除，见 3.2 |
| C4 | `reflection-external` | `typeof(X).GetField("literal")` 直接写在调用点（`GameState.cs:17,120`、`GameVersionAdapter.cs:28`） | 字面类型 + 字面成员 → 免费 | **确认免费**（未出现在 findings 中） | 通过 |
| C5 | `network` | `src/Bridge/BridgeServer.cs:218` `new TcpListener(IPAddress.Loopback, port)` | `System.Net.*` → `network` | **已声明** | 通过 |
| C6 | `threads` | — | `new Thread`/`Task.Run`/`ThreadPool`/`System.Threading.Timer`/非自有 MonoBehaviour 的 `StartCoroutine` | 静态扫描未发现（HTTP 全部在主线程 `Tick` 里 poll；`ScreenshotService` 是自有 `MonoBehaviour`，其 `StartCoroutine` 免费） | 预测不命中；若扫描器命中须重新评估 |
| C7 | `native` / `dynamic-code` | — | `DllImport`/`unsafe`/`Marshal`/`Assembly.Load`/`TypeNameHandling` | 静态扫描未发现 | 预测不命中 |
| C8 | 硬拒绝项 | — | `Process`、`UnpatchAll(null)`、改 loader/trust、Steam 写 | 静态扫描未发现（`src/` 无 `System.Diagnostics.Process`，无 `SteamUGC` 写） | 预测不命中 |
| C9 | `files`（2026-12-10 起） | `RecordingSaveAdapter.Inspect` ×2、`ReadLastSaveTime` → `SaveFiles.IndexLoad` | `[ModRestricted]`：2026-12-10 起按 `files` 计 | **预测错误**：确实直接调用了 `SaveFiles.IndexLoad`（扫描器列为 `from December 10, 2026 (3)`） | 见 3.1 |

### 3.1 `files`（C1/C2 + C9）——实测后的选项

**实测**（§6）：

```
detected: files, network, reflection-external
UNDECLARED (enable would be refused): files
  files: ScreenshotService.CaptureJpeg -> ScreenCapture.CaptureScreenshotAsTexture
  files: ScreenshotService.ReadUsage  -> Directory.Exists / Directory.GetFiles / FileInfo..ctor / FileInfo.get_Length
  from December 10, 2026 (3):
    files: RecordingSaveAdapter.Inspect -> SaveFiles.IndexLoad   ×2
    files: RecordingSaveAdapter.ReadLastSaveTime -> SaveFiles.IndexLoad
```

注意 C9：`RecordingSaveAdapter` **确实**调用了 `[ModRestricted]` 的 `SaveFiles.IndexLoad`
（上一轮"只走玩家路径"的静态判断是错的）。它今天不计费，2026-12-10 起按 `files` 计。

因此：

- **只删 `ScreenCapture` + `System.IO` 并不能得到 0 capability**：12-10 之后 `SaveFiles.IndexLoad`
  仍会要求 `files`。要真正归零，还得去掉存档索引读取。
- 最小、可当天完成的解：**在 `manifest.xml` 声明 `files`**（同时覆盖今天的 5 条与 12-10 的 3 条）。

### 3.1a 旧的静态分析与选项（保留备查）

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

### 3.2 `reflection-external`（C3 + C3b）——已知会在 2026-12-10 被拒

**实测 14 条**，分两类：

| 类别 | 条数 | 站点 | 能否用 `typeof()` 消除 |
|---|---|---|---|
| 辅助方法把 `Type` 当参数 | **10** | `NewGameMenuAdapter.{StaticField,StaticProperty,InvokeStaticInt,InvokeVoid,InvokeStaticVoid,InvokeStaticVoidInt,InvokeStaticBool,ConstInt}`、`RecordingSaveAdapter.{StaticField,InvokeStaticVoid}` | **能**：把 `typeof(X)` 与成员名字面量内联到 67 个调用点（`NewGameMenuAdapter` 62 + `RecordingSaveAdapter` 5） |
| 真正的 computed type | **4** | `NewGameMenuAdapter.ReadPerkSlots`：`view.GetType()` 后取 `perk`/`inSelectedRow`/`hovered`/`frameImage` | **不能**：`SlotView` 是 `NewGamePerksUI` 的**私有嵌套类**（`_decomp/NewGamePerksUI.cs:7`），外部拿不到 `typeof(SlotView)` |

**这意味着**：即使把 10 条辅助方法全部内联，仍有 4 条 → 2026-12-10 起**整个 Mod 会被拒绝激活**。
唯一出路是（a）游戏作者提供公开访问器，或（b）放弃"逐行 perk 卡"读取。
所以内联工作本身**不足以**解锁 12-10，属于"该做但不能单独解决问题"。

`NewGamePerksUI.SlotView` 是**私有嵌套类**（`_decomp/NewGamePerksUI.cs:7`），
外部代码拿不到 `typeof(SlotView)`，只能 `view.GetType()` → 官方规则判为 computed
→ `reflection-external`。这意味着 **bootstrap 的 `get_new_game_options` 选项行读取**
在 2026-12-10 之后会被拒绝激活（不是运行时失败，是整个 Mod 不能被激活）。

可选项：
1. 向作者申请一个公开访问器（最干净）；
2. 放弃"逐行 perk 选择"读取，只保留场景/难度（若它们不依赖 `SlotView`）；
3. 接受 **Workshop BLOCKED**，仅本地开发者模式使用。

本轮**不**改动反射实现：没有扫描器输出前重构，等于用猜测替换证据（brief §6.1）。

## 4. 采集步骤（已完成；保留供复扫）

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

### 4.1 采集前的 Gate C（历史，已被 §7 取代）

| 门槛 | 当时状态 |
|---|---|
| `R4-F7-01` 原始 scanner 日志 + 指纹 | NOT_RUN（指纹部分已就绪） |
| `R4-F7-02` 每项 finding 有定位与 disposition | PARTIAL（只有静态预测） |
| `R4-F7-03` 复扫无硬拒绝、`declared ⊇ detected` | BLOCKED（C1 预测即拒绝） |
| `R4-F7-04` 已安装 DLL 与本轮构建匹配 | PASS |
| `R4-F7-05` 区分"当前可启动"与"2026-12-10 后可启动" | DONE |

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

### 5.3 `mod-scan` 的执行条件（已完成）

用户按了 F8，但 `Player.log` 记录：

```
[DevTools] Developer tools disabled: no dev.txt next to the executable
  (D:\Program Files\steam\steamapps\common\Probably Stolen\dev.txt).
```

官方指南要求游戏根目录存在 `dev.txt`，且**只在启动时检查一次**。
2026-11-18 23:23 补建空 `dev.txt` → 重启游戏 → `[DevTools] Developer tools enabled`
→ 23:28 `mod-scan pawnmind` 成功（输出见 §6）。
**采集完成后应删除 `dev.txt`**（它会同时打开 F9 开发菜单，那个菜单能直接改存档）。

---

## 6. 官方 `mod-scan pawnmind` 原始输出（2026-11-18 23:28）

> 来源：用户 F8 控制台截图（5 张），以下为**逐行转录**。行首缩进与换行按控制台宽度，
> 内容未改写。`…` 表示截图之间的重叠行已合并、未丢失内容。

### 6.1 汇总

```
> mod-scan pawnmind
PawnMind · AI Bridge (D:/Program Files/steam/steamapps/common/Probably
Stolen/Probably Stolen_Data\..\Mods\PawnMind)
  author: "ygmldr"
  assemblies: PawnMind.dll
  methods scanned: 1342
  declared: network, reflection-external
  detected: files, network, reflection-external
  UNDECLARED (enable would be refused): files
  DEPRECATED: reflection-external refused by the game update of December
10, 2026. Replace computed reflection with typeof(...) or ModReflect,
compiled expression trees with ModReflect.Method<T>, reach other mods
through <Prerequisites>, threads and timers with ModHelper.StartCoroutine /
RunInBackground, drop DllImport and unsafe code, then remove the
capabilities from the manifest. The findings below name each site.
  from December 10, 2026 (3, not counted until then):
    files: PawnMind.Game.RecordingSaveAdapter.Inspect ->
SaveFiles.IndexLoad (reads, moves or copies files in the save folder)
    files: PawnMind.Game.RecordingSaveAdapter.Inspect ->
SaveFiles.IndexLoad (reads, moves or copies files in the save folder)
    files: PawnMind.Game.RecordingSaveAdapter.ReadLastSaveTime ->
SaveFiles.IndexLoad (reads, moves or copies files in the save folder)
  VERIFICATION: declares network. From December 10, 2026 a Workshop mod
with these loads only when the developers have verified it (trust list
`verified`, by version hash or "*"); local folders are not affected. Ask
for verification before publishing.
  findings (41):
```

### 6.2 `findings (41)` 全文

`reflection-external`（14）：

```
    reflection-external: PawnMind.Game.NewGameMenuAdapter.ReadPerkSlots ->
Type.GetField on a computed type          ×4
    reflection-external: PawnMind.Game.NewGameMenuAdapter.StaticField ->
Type.GetField on a computed type
    reflection-external: PawnMind.Game.NewGameMenuAdapter.StaticProperty ->
Type.GetProperty on a computed type
    reflection-external: PawnMind.Game.NewGameMenuAdapter.InvokeStaticInt ->
Type.GetMethod on a computed type
    reflection-external: PawnMind.Game.NewGameMenuAdapter.InvokeVoid ->
Type.GetMethod on a computed type
    reflection-external: PawnMind.Game.NewGameMenuAdapter.InvokeStaticVoid ->
Type.GetMethod on a computed type
    reflection-external: PawnMind.Game.NewGameMenuAdapter.InvokeStaticVoidInt ->
Type.GetMethod on a computed type
    reflection-external: PawnMind.Game.NewGameMenuAdapter.InvokeStaticBool ->
Type.GetMethod on a computed type
    reflection-external: PawnMind.Game.NewGameMenuAdapter.ConstInt ->
Type.GetField on a computed type
    reflection-external: PawnMind.Game.RecordingSaveAdapter.StaticField ->
Type.GetField on a computed type
    reflection-external: PawnMind.Game.RecordingSaveAdapter.InvokeStaticVoid ->
Type.GetMethod on a computed type
```

`files`（5）：

```
    files: PawnMind.Game.ScreenshotService.CaptureJpeg ->
ScreenCapture.CaptureScreenshotAsTexture
    files: PawnMind.Game.ScreenshotService.ReadUsage -> Directory.Exists
    files: PawnMind.Game.ScreenshotService.ReadUsage -> Directory.GetFiles
    files: PawnMind.Game.ScreenshotService.ReadUsage -> FileInfo..ctor
    files: PawnMind.Game.ScreenshotService.ReadUsage -> FileInfo.get_Length
```

`network`（22，全部 `PawnMind.Bridge.BridgeServer`）：

```
    network: BridgeServer.Start -> TcpListener..ctor / TcpListener.get_Server /
Socket.set_Blocking / TcpListener.Start                     （4）
    network: BridgeServer.Stop -> Socket.Close / TcpListener.Stop        （2）
    network: BridgeServer.AcceptPending -> TcpListener.get_Server / Socket.Poll /
TcpListener.AcceptSocket / Socket.set_Blocking / Socket.Close ×2     （6）
    network: BridgeServer.TryRead -> Socket.Poll / Socket.get_Available ×2 /
Socket.Receive / SocketException.get_SocketErrorCode                 （5）
    network: BridgeServer.Flush -> Socket.Poll / Socket.Send /
SocketException.get_SocketErrorCode ×2                               （4）
    network: BridgeServer.Close -> Socket.Close                          （1）
```

### 6.3 尾部

```
  fingerprint:
cb148a742723952eec115b972489659c9638307003f4e2fbb87edc6726f9f91b
    PawnMind.dll:
6adef8ace8511e742053310b29be822905b42b5284f581af7f90ff1fdfceff78
  trust list: None
```

### 6.4 交叉验证

| 项 | 值 | 验证 |
|---|---|---|
| findings 总数 | 41 | 本文计数 14 + 5 + 22 = **41** ✓ |
| `fingerprint` | `cb148a74…` | 与 `mod-audit.log` 的 `hash=cb148a74…` 一致 ✓ |
| `PawnMind.dll` | `6adef8ac…` | 与 `dist` / 已安装 / §1 的 SHA256 一致 ✓（R4-F7-04） |
| `methods scanned` | 1342 | 扫描的是完整 DLL，不是空壳 ✓ |

### 6.5 扫描带来的两条新结论（此前无法得知）

1. **`network` 会让 Workshop 版本从 2026-12-10 起需要"开发者已验证"**：
   `VERIFICATION: declares network. From December 10, 2026 a Workshop mod with these
   loads only when the developers have verified it (…); local folders are not affected.`
   → 本地 `Mods/` 安装不受影响；**上工坊则需要作者验证**。这是产品级约束，不是代码问题。
2. **`SaveFiles.IndexLoad` 是 `[ModRestricted]`**：`RecordingSaveAdapter` 的三个调用点
   从 12-10 起按 `files` 计费，推翻上一轮"只走玩家路径、未直接调用 `SaveFiles`"的判断。

---

## 7. 最终 Gate C 判定

| 门槛 | 状态 | 依据 |
|---|---|---|
| `R4-F7-01` 原始 scanner 日志 + 指纹 | **PASS** | §6 全文；`fingerprint: cb148a74…`；`PawnMind.dll: 6adef8ac…` |
| `R4-F7-02` 每项 finding 有定位与 disposition | **PASS** | §3 表 + §6.2 全部 41 条；§6.4 交叉验证计数一致 |
| `R4-F7-03` 复扫无硬拒绝、`declared ⊇ detected` | **BLOCKED** | `UNDECLARED (enable would be refused): files`；另有 14 条 `reflection-external`（12-10 起拒绝） |
| `R4-F7-04` 已安装 DLL 与本轮构建匹配 | **PASS** | 扫描器自报的 `PawnMind.dll` 哈希 = `dist` = 已安装 |
| `R4-F7-05` 区分"当前可启动"与"2026-12-10 后" | **PASS（结论更严）** | 今天：本地安装靠 `DeveloperMode` 才能跑，普通玩家会被拒；12-10 起：即便声明 `files`，`reflection-external` 仍会拒绝整个 Mod；工坊：`network` 还需要作者验证 |

**Gate C = BLOCKED。** 三条独立阻塞：

1. **今天**：`files` 未声明 → 普通玩家激活被拒（当前仅靠开发者模式运行）。
2. **2026-12-10 起**：14 条 `reflection-external`，其中 **4 条结构性不可消除**
   （`NewGamePerksUI.SlotView` 是私有嵌套类）。
3. **工坊**：`network` 触发"开发者已验证"要求（`local folders are not affected`）。

代码/清单之外**没有**任何"扫描器豁免""开发模式绕过""改 trust 层"的做法被采用或计划采用。
