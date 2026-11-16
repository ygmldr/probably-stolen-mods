# NIGHTLY_MOD_COMPAT_MATRIX — PawnMind 静态兼容/发布预审矩阵

> Nightly Batch A · 模块 G（发布/兼容静态预审）
> 状态：**静态预审完成（源码+本机反编译规则副本）**；官方 ModScanner 实机运行 = `NOT_RUN`；**未发布、未上架、未安装、未覆盖游戏目录**。
> 本文件只记录证据与建议，**不包含任何安装/覆盖命令的执行**。执行边界见 §0.3。

## 0. 元数据与边界

### 0.1 审计快照（可复现）

| 项 | 值 | 证据命令 |
|---|---|---|
| 快照时间（UTC） | 首扫 `2026-11-16T20:12:17Z` @ `4846189`；最终复核 `2026-11-16T20:21:06Z` @ `6ab3333` | `date -u` / `git log -1` |
| HEAD（最终复核） | `6ab33339f963e28a81d177546e7b44a3374ff4c9`（`git describe` = `v1.0.1-42-g6ab3333`；审计期间共享树新增 Nightly B 提交 `6ab3333`） | `git rev-parse HEAD` / `git describe --tags --always` |
| 工作树 | **脏**：20 项未提交（包含本文件与安装草稿 2 个新增；其余为模块 B/D/E 在共享树写入） | `git status --porcelain` |
| 扫描范围 | `PawnMind/src/**`、`PawnMind/agent/**`、`PawnMind/tools/**`、`PawnMind/build.sh`、`PawnMind/manifest.xml`、`PawnMind/dist/**`、`PawnMind/test.sh`（只读）、`/root/work/game_managed`（只读）、`/root/work/game_mods/_decomp`（只读） | — |
| 本文件统计口径 | 见 §12；全部计数可用 §14 命令重跑 | — |

> ⚠️ 共享工作树中的未提交项（最终复核时，**不含**本文件与安装草稿）：`src/Bridge/ActionRouter.cs`、`src/Core/ActionGate.cs`、`src/Core/EndpointPolicy.cs`、`src/Core/RouteTable.cs`、`test/PawnMind.Tests/ActionRouterTests.cs`、`test/PawnMind.Tests/PawnMind.Tests.csproj`（修改）；`agent/tests/test_operation_tools.py`、`agent/tests/test_skill_tool_refs.py`、`agent/tests/test_trace_validator.py`、`agent/trace_validator.py`、`docs/eval/`、`skills/`、`src/Core/OperationStatus.cs`、`src/Core/QueryValues.cs`、`src/Core/ShopOperationSemantics.cs`、`test/PawnMind.Tests/OperationEndpointTests.cs`、`test/PawnMind.Tests/OperationSemanticsTests.cs`、`test/PawnMind.Tests/OperationTests.cs`（新增）。
> 因此本矩阵是**时间点快照**：批次合并后必须用 §14 命令重跑；数字漂移不代表本文件当时造假。
> **移动树稳定性**：审计跨越 `4846189`→`6ab3333` 两次快照，§3–§8 的分类计数两次一致（反射 API 26 行 / `typeof` 68 / 线程 0 / 协程 2 行 / 监听 1 / Socket 23 行 / P-Invoke 0 / Core Unity 0），故结论对该区间内的并发写入稳定；`dist` 落后于源码与构建产物的结论也不受这些写入影响。

### 0.2 结论摘要（先看这里）

| # | 结论 | 等级 | 性质 |
|---|---|---|---|
| C1 | 预测存在 **未声明的 `files` capability**（`System.IO`/`UnityEngine.ScreenCapture`/`SaveFiles.IndexLoad` 命中），按本地反编译的启用链会**拒绝启用**；manifest 目前只声明 `network` + `reflection-external` | **高（发布阻塞-预测）** | 需实机 `mod-scan` 确认，`NOT_RUN` |
| C2 | `reflection-external` 是官方 **retiring/deprecated** 能力；本地游戏文案写明 **2026-12-10 更新起拒绝**。当前有 14 处 computed-`Type` 反射命中，manifest 仍声明该能力 → 该日期后发布即被拒 | **高（未来阻塞）** | 本地规则副本 + 游戏内文案；运行态 `NOT_RUN` |
| C3 | `dist` 与源码、`dist` 与已安装 DLL **三方不一致**：dist DLL 早于至少 19 个当前源码 `.cs` 文件；另有其他模块 20:16 的离线构建产物（`df8458…`/320,512 B）与 dist（`ad3359…`/296,960 B）不同，证明 dist 不是当前代码构建；已安装 DLL 本次不可访问（历史记录 `e9e1bd07…`/295,424 B）。**当前游戏现象不可归因于仓库代码** | **高（归因/验收）** | dist 实测 SHA256；安装侧 `NOT_RUN` |
| C4 | 版本号三处不一致：`manifest.xml` = `0.3.0`，最新 Mod tag = `v1.0.1`，README/AGENTS 仍写 `0.2.1` | 中（发布卫生） | 实测文件 + git tag |
| C5 | 后台线程 / `Task` / `ThreadPool` / `Timer` / `async` = **0**；网络监听仅 1 个 **loopback** `TcpListener`；P/Invoke / `DllImport` / `unsafe` = **0**；Core 无 `UnityEngine` = **0**；主线程约束静态未发现绕过 | 低 / PASS（静态） | 见 §3–§8 |
| C6 | 打包面干净：`dist/PawnMind/` 只有 `manifest.xml` + `PawnMind.dll`，**Python/脚本/外部进程不进 dist**；但"整仓拷进 Mods"会被扫描器按 payload/unlisted-DLL 拒绝 | 低（当前） / 高（误打包） | §5 + 安装草稿 |
| C7 | 仓库内**没有**官方 `APIChanges.md` / `ModdingGuide.md`；官方可执行 Scanner 未安装（不可运行），但 `_decomp/` 有**本机反编译规则副本**（仅分析用）。运行结果一律 `NOT_RUN` | 中（证据缺口） | §1 + §10 |

### 0.3 不可突破的边界（本矩阵遵守）

- 未启动/未操作游戏；未读用户存档；未安装/覆盖任何 DLL；**未对 `Mods/PawnMind` 或游戏目录写入**。
- 未运行 `./test.sh`、未运行 `./build.sh`、未构建、未提交 commit。
- 只读操作：`grep` / `read` / `sha256sum` / `ls` / `git status` / `stat`。
- 本批次新增文件仅：本文件 + `docs/plans/WORKSHOP_INSTALL_DRAFT.md`。`src/`、`test/`、`agent/`、`scripts`、`dist/`、`manifest.xml` 均未修改。
- `_decomp/` 是 gitignore 的本地反编译产物（AGENTS.md §5）：只用于互操作分析，**不进 dist、不随工坊包分发**；本文只引用类型名/方法名/结论，不复制大段反编译代码。

---

## 1. 规则来源：官方 ModScanner 与 APIChanges 的本地状态

### 1.1 找到了什么

| 对象 | 本地状态 | 位置/证据 | 能否作为运行结果 |
|---|---|---|---|
| 官方可执行 ModScanner | **不可运行**（本机 Linux 服务器无游戏安装、无 Windows `dotnet.exe` 目标；且本批次禁止运行游戏/构建） | 见 §10 `NR-01`/`NR-02` | `NOT_RUN` |
| 官方规则源码（反编译副本） | **存在**（本机从已安装游戏程序集反编译，`ModScanner.VERSION = 4`） | `_decomp/ModScanner.cs:1994`；`_decomp/ModCapability.cs`；`_decomp/ModScanResult.cs`；`_decomp/ModScanFinding.cs`；`_decomp/ModSecurityDebug.cs`；`_decomp/ModLoader.cs`（启用链） | 只作**规则推导**，不是扫描结果 |
| 游戏内官方命令 `mod-scan` | 源码存在（`[Command("mod-scan", …)]`，需游戏进程内执行） | `_decomp/ModSecurityDebug.cs:11-101` | `NOT_RUN` |
| 离线扫描 harness | 存在（CompactSort 的历史工具，硬编码 Windows 游戏路径） | `CompactSort/test/ScannerCheck/Program.cs:8-10`、`ScannerCheck.csproj` | `NOT_RUN` |
| `APIChanges.md` / `ModdingGuide.md` | **仓库内不存在**（`find` 0 命中） | §10 `NR-03` | `NOT_RUN` |
| 游戏程序集参考副本 | 存在（`/root/work/game_managed`，仅 Managed 目录，非安装目录） | `Assembly-CSharp.dll` SHA256 `725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05`，4,820,224 B，mtime 2026-11-09 | 参考用，MVID `NOT_RUN` |

> **来源声明**：本矩阵的"扫描器预测"全部由 `_decomp/` 本地反编译副本的规则逐条推导，**没有引用任何网传兼容性口径**；外部 URL 只作为"获取方式"记录（§10 `NR-03`），其内容未在本机核实，不作为规则依据。
> **局限**：反编译副本对应 0.46D 的某个安装构建，但**未能证明**它与我方编译所用 `/root/work/game_managed` 是同一 MVID；规则会随更新变化，实机 `mod-scan` 结果永远优先于本矩阵。

### 1.2 本地规则要点（用于推导，逐条可查）

| 规则 | 证据（`_decomp/`） |
|---|---|
| 能力全集 9 种：`files`、`network`、`process`、`dynamic-code`、`native`、`reflection-external`、`steam`、`steam-auth`、`threads` | `ModCapability.cs:15-33` |
| Retiring（下版本拒绝）能力：`reflection-external`、`native`、`dynamic-code`、`threads` | `ModCapability.cs:37`；`IsDeprecated`/`IsRefused`：`:44-60` |
| 本构建中 `REFUSE_UNSCANNABLE_CAPABILITIES = false`（说明"现在允许、将来拒绝"） | `ModScanner.cs:1992`；`REFUSE_REFLECTION_EXTERNAL`：`:2015` |
| 游戏内文案：DEPRECATED 能力 **2026-12-10 更新起拒绝**；替代方案：`typeof(...)`/`ModReflect`、`ModHelper.StartCoroutine/RunInBackground`、删 `DllImport`/unsafe、然后从 manifest 移除能力 | `ModSecurityDebug.cs:54-58` |
| 2026-12-10 起，**Workshop** Mod 若声明 `files`/`network` 需开发者验证（本地文件夹不受影响，但当前开关 `WORKSHOP_VERIFIED_ONLY_CAPABILITIES = false`） | `ModSecurityDebug.cs:67-71`；`ModTrustList.cs:117-119`；`ModLoader.cs:694-699` |
| `files` 触发：`System.IO.File/FileStream/Directory/FileInfo/...`、`UnityEngine.ScreenCapture`、`Microsoft.Win32.Registry`、ES3 等 | `ModScanner.cs:50-56`（FileTypes）；调用判定 `:681-700`；`typeof` 判定 `:536-559` |
| `network` 触发：`System.Net.*`、`UnityEngine.Networking.UnityWebRequest`、Steam 网络类；例外 `System.Net.IPAddress/IPEndPoint/WebUtility` | `ModScanner.cs:73-77`、`:621-632`（`IsNetworkType`）；调用判定 `:724-728` |
| 反射外部命中：`System.Type.GetField/GetMethod/GetProperty/GetMembers…` 作用在**无法解析为字面量/游戏类型**的 receiver 上 → `reflection-external`；`typeof(游戏类型)` 字面量 **不算**（游戏程序集不是 external scope） | `ModScanner.cs:93-97`（TypeMemberLookups）、`:1284-1315`（`ClassifyReceiver`）、`:1526-1543`（`ClassifyTypeReference`）、`:1671-1696`（`IsExternalScope`：`Assembly-CSharp` 返回 false）、`:966-969`（`ReflectionExternal`） |
| `threads` 触发：`System.Threading.Thread/ThreadPool`、`Task.Run/Delay/…`、`TaskFactory`、`Parallel`、`Timer`；`MonoBehaviour.StartCoroutine` **只在其目标不是自身时**触发（自身 `ldarg.0` 调用预测不命中） | `ModScanner.cs:107-113`、`:1050-1079`（`IsThreadStart`）、`:659-679`（MonoBehaviour 分支：`Ldarg_0` 直接 return） |
| 硬旗（启用即拒）：`process-start`、`loader-target`（对 GuardTypes 反射/补丁）、`unlisted-reference`（引用非游戏程序集且未在 `<Assemblies>`/`<Prerequisites>`）、`bundled-framework`、`payload-file`/`opaque-resource`（Mod 文件夹里的 exe/dll/不明二进制）等 | `ModScanner.cs:125-147`、`:130`、`:138`；GuardTypes `:2000-2004`；文件夹扫描 `:2053-2142` |
| 启用链顺序：unlisted DLL → 信任黑名单 → 已拒绝能力 → 停止记录 → kill switch → Workshop 验证 → 缺失 DLL → shadowing 游戏程序集 → **scanner 硬旗** → **未声明能力（拒绝）** → 需确认能力 prompt | `ModLoader.cs:660-747`；未声明拒绝文案 `:739-743` |
| manifest 必填：`<ID>`（合法字符）→ `<Assemblies>`（相对 .dll，非空）→ `<Capabilities>` 元素（可为空）且能力名必须是已知 9 种 | `ModLoader.cs:1104-1188` |
| 发布友好替代 API：`ModReflect`（FindType/Types/Get/GetStatic/Call/CallStatic/Method<T>/StaticMethod<T>/New/Target） | `ModReflect.cs:19-276` |
| 发布友好协程/后台 API：`ModHelper.StartCoroutine(modId, routine)`、`Delay`、`RunInBackground`、`RunOnMainThread` | `ModHelper.cs:1056`、`:1082`、`:1092`、`:1127` |

---

## 2. 反射（Reflection）

### 2.1 逐条调用点（19 个成员查找 + 7 个 `MethodInfo.Invoke`；`typeof` 聚合见 §2.2）

> 说明："扫描器预测"由 §1.2 规则静态推导；**实际结果 `NOT_RUN`**。等级 = 兼容/发布风险，不是安全漏洞等级。

| 编号 | 文件:行号 | 调用形态 | 扫描器预测（VERSION=4） | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| R-01 | `src/Game/GameVersionAdapter.cs:28` | `typeof(PlayerStore).GetField("VERSION_NUMBER", Public\|Static)` | 不触发（字面量 game 类型；`ldtoken` 解析为 Assembly-CSharp） | 低 | 只读公开常量；注释说明"避免 const 内联"是有意设计；字段改名会静默返回 null → 版本门锁死（安全侧） | 保持；契约测试覆盖 `PlayerStore.VERSION_NUMBER` 存在性（AdapterTests） |
| R-02 | `src/Game/GameVersionAdapter.cs:38,46` | `field.GetRawConstantValue()` / `field.GetValue(null)` | 不触发（receiver 是 `FieldInfo` 局部，非 computed `Type`） | 低 | 读 MVID/版本的辅助路径 | 保持 |
| R-03 | `src/Game/GameVersionAdapter.cs:59` | `typeof(PlayerStore).Assembly.ManifestModule.ModuleVersionId` | 不触发（`Assembly.get_ManifestModule`/`Module.ModuleVersionId` 不匹配 `reflection-external` 规则分支） | 低 | 读 MVID 做写入门禁；每次构建 MVID 变化 → 门禁自动锁（预期行为） | 保持；如未来 `Assembly.*` 被收紧，改用官方 `ModReflect` 未提供 MVID，需单独确认 |
| R-04 | `src/Game/GameState.cs:16-17` | `typeof(StoreClientInstance).GetField("storeClient", Instance\|NonPublic)` | 不触发（字面量 game 类型） | 中 | 私有字段；只用于取当前顾客实例（非隐藏鉴定数据）；字段改名→顾客相关功能退化但 fail-safe | 保持 + 契约测试；长期可用 `ModReflect.Get` 替代（可审计） |
| R-05 | `src/Game/GameState.cs:119-120`（读值 `:111`、`:226`） | `typeof(StoreClientInstance).GetField("gridInv", …)` + `GetValue` | 不触发 | 中 | 私有字段 `gridInv` 只读；mtime 改动后需重跑契约 | 同上 |
| R-06 | `src/Game/RecordingSaveAdapter.cs:402` | `type.GetField(name, StaticMembers)`（`type` 为方法参数） | **触发 `reflection-external`**（computed Type；`ModScanner.cs:1311-1314`） | 中（未来=高） | 泛型 helper `StaticField<T>(Type type, string name)`；能力已声明但属 retiring | 把 `typeof(...)` 下放到调用点（scanner 可解析），或改用 `ModReflect.GetStatic(Type, member)` |
| R-07 | `src/Game/RecordingSaveAdapter.cs:410,412` | `type.GetMethod(...)` + `method.Invoke(null,null)` | 触发 `reflection-external`（computed Type；`Invoke` 自身预测不命中） | 中（未来=高） | 调用 `NewGameDifficultyUI.OnStartClicked` 等原生静态回调；有派发次数/幂等门禁 | 同上；`Method<T>` 方式见 `ModReflect.cs:228-243` |
| R-08 | `src/Game/RecordingSaveAdapter.cs:256,266,285` | `typeof(NewGameDifficultyUI)` 传入 helper | 不触发（`typeof` 字面量） | 低 | 只是把字面量传给 R-06/R-07 helper；命中发生在 helper 体内 | 与 R-06/R-07 一起整改即可 |
| R-09 | `src/Game/NewGameMenuAdapter.cs:392` | `typeof(NewGamePerksUI).GetField("views", StaticMembers)` | 不触发（字面量 game 类型） | 中 | 读私有静态 `views`；UI 改版即失效（adapter 契约可发现） | 保持 + 契约测试；或用 `ModReflect.GetStatic` |
| R-10 | `src/Game/NewGameMenuAdapter.cs:400-408`（401、403、405、407 四个 `GetField`） | `Type type = view.GetType(); type.GetField("perk"/"inSelectedRow"/"hovered"/"frameImage", …)` | 触发 `reflection-external` ×4（computed Type） | 中（未来=高） | 读 perks UI 视图对象的私有字段；`view.GetType()` 使 receiver 不可解析 | 用字面量内部类型（若可引用）或 `ModReflect.Get(view, "perk")` |
| R-11 | `src/Game/NewGameMenuAdapter.cs:1280` | `type.GetField(name, StaticMembers)` | 触发 `reflection-external` | 中（未来=高） | helper `StaticField<T>` | 同 R-06 |
| R-12 | `src/Game/NewGameMenuAdapter.cs:1288` | `type.GetProperty(name, StaticMembers)` | 触发 `reflection-external` | 中（未来=高） | helper `StaticProperty<T>` | 同 R-06；`ModReflect.GetStatic` 可替代属性读取（其实现覆盖属性） |
| R-13 | `src/Game/NewGameMenuAdapter.cs:1290` | `property.GetGetMethod(true)` | 不触发（receiver 是 `PropertyInfo`） | 低 | 反射链的中间步骤 | 整改 R-12 时一并去掉 |
| R-14 | `src/Game/NewGameMenuAdapter.cs:1298,1306,1321,1331,1346` | `type.GetMethod(...)` ×5 | 触发 `reflection-external` ×5 | 中（未来=高） | helper `InvokeStaticInt/Bool/Void` 等 | 同 R-07 |
| R-15 | `src/Game/NewGameMenuAdapter.cs:1354` | `type.GetField(name, StaticMembers)` | 触发 `reflection-external` | 中（未来=高） | helper `ConstInt` | 同 R-06 |
| R-16 | `src/Game/NewGameMenuAdapter.cs:1292,1300,1316,1326,1341,1348` | `getter.Invoke` / `method.Invoke` ×6 | `MethodBase.Invoke` 预测不单独触发 | 低/中 | 反射执行本体；与 R-14/R-12 共命运 | 随 R-11~R-15 一起迁到 `ModReflect` |
| R-17 | `src/Game/RecordingSaveAdapter.cs:412` | `method.Invoke(null, null)` | 同上 | 低/中 | 见 R-07 | — |
| R-18 | `src/Bridge/UiSnapshot.cs:92,107` | `tablet.button.onClick.Invoke()` / `button.onClick.Invoke()` | 不触发（`UnityEvent.Invoke`，非反射/线程规则） | 低 | **不计入反射点**；属 `press` 动作直接触发 UI 回调的设计，需保持 rev/白名单门禁 | 保持；不要让 `press` 泛化为任意方法调用 |
| R-19 | `src/Game/GameState.cs:12` 注释 | 声称"reflecting on a game type is free for the capability scanner" | — | 低（文档） | 与本地反编译规则一致（字面量 game 类型不命中），但**不能替代实机扫描** | 注释保留，但发布前以 `mod-scan` 为准 |

**反射替换建议（唯一目标：在 2026-12-10 前消除 computed-Type 反射，然后从 manifest 移除 `reflection-external`）**

1. 方案 A（最小改动）：把 `StaticField<T>(Type,…)` / `StaticProperty<T>` / `InvokeStatic*` helper 的 `typeof(...)` 从"参数传入"改为"每个目标类型各写一个直调 helper"——callsite 即 `typeof(NewGamePerksUI).GetField(...)`，scanner 能解析为 game 类型，不触发；但 helper 数量膨胀。
2. 方案 B（推荐）：改用官方 `ModReflect`：`ModReflect.GetStatic<T>(typeof(NewGamePerksUI), "page")`、`ModReflect.CallStatic(typeof(NewGameDifficultyUI), "OnStartClicked")`。`ModReflect` 是游戏自带的受控反射门面（`_decomp/ModReflect.cs:19-276`），不在 scanner 命中列表；对 GuardTypes（`ModLoader`/`ModTrust*`/`ModScanner`/`ModReflect` 等）的访问会被 GuardTypes 规则拦（本项目不访问）。
3. 方案 C（不动代码的发布缓冲）：保持声明 `reflection-external`，把"消除 computed reflection"登记为 2026-12-10 前发布阻塞项；**不得**在未实机扫描的情况下宣称兼容。

### 2.2 `typeof(...)` 聚合（68 行，单独立表）

| 文件 | `typeof(` 行数 | 形态 | 扫描器预测 | 等级 |
|---|---|---|---|---|
| `src/Game/NewGameMenuAdapter.cs` | 61 | 绝大多数是 `typeof(游戏 UI 类型)` 字面量传参，另有 `typeof(string)`/`typeof(int)` 签名（`:1310`、`:1335`） | 字面量 game 类型不触发；`typeof(string/int)` 不在危险类型表 | 低 |
| `src/Game/RecordingSaveAdapter.cs` | 3 | `typeof(NewGameDifficultyUI)` 等 | 不触发 | 低 |
| `src/Game/GameVersionAdapter.cs` | 2 | `typeof(PlayerStore)` | 不触发 | 低 |
| `src/Game/GameState.cs` | 2 | `typeof(StoreClientInstance)` | 不触发 | 低 |

### 2.3 反射相关的"零命中"项（PASS，静态）

| 项 | 命中数 | 证据命令 |
|---|---|---|
| `Assembly.Load/LoadFrom/LoadFile/ReflectionOnlyLoad` | **0** | `grep -rn "Assembly\.Load" src` |
| `Activator.CreateInstance*` / `AppDomain` | **0** | `grep -rn "Activator\.\|AppDomain" src` |
| `Assembly.GetTypes()` / `GetExportedTypes`（生产代码） | **0** | `grep -rn "GetTypes(" src` |
| `Delegate.CreateDelegate` / `System.Reflection.Emit` / `Expression.Compile` | **0** | `grep -rn "CreateDelegate\|Reflection\.Emit\|Expression\." src` |
| `using System.Reflection` 的文件 | 4（`GameVersionAdapter.cs`、`RecordingSaveAdapter.cs`、`GameState.cs`、`NewGameMenuAdapter.cs`） | `grep -rln "System.Reflection" src` |
| GuardTypes 反射（`ModLoader`/`ModTrust*`/`ModScanner`/`ModCapability`/`ModWorkshop`/`SteamManager`/`PaperBookRegistry`/`ModReflect`） | **0** | `grep -rn "typeof(Mod\|GetField(\"modTrust" src`；`_decomp/ModScanner.cs:2000-2004` 定义 GuardTypes |

---

## 3. 后台线程 / 异步 / 协程

| 编号 | 文件:行号 | 调用形态 | 扫描器预测 | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| T-01 | — | `System.Threading.Thread`、`ThreadPool`、`Task.Run/Delay/ContinueWith`、`TaskFactory`、`Timer`、`Parallel`、`async void`、`await` | **0 命中** | PASS | 曾在 2026-11-09 因 `threads` retiring 清理过（`README.md:277-293`） | 保持；新增代码禁止引入 |
| T-02 | `src/Game/ScreenshotService.cs:62-66`（`StartCoroutine`）、`:85-109`（`IEnumerator CaptureLoop`、`WaitForEndOfFrame`） | 自身 MonoBehaviour 协程 | **自身调用预测不命中**（`ModScanner.cs:672-677` 对 `Ldarg_0` 直接 return；仅"另一个对象的 MonoBehaviour"才命中 `threads`） | 中 | 但 `README.md:285` 的历史扫描记录把 `StartCoroutine` 列为 `threads` 命中项（旧扫描器/旧代码的说法），且该文件于 2026-11-15 才加入（`8f64166`）——**与"已删除协程"的旧结论存在文档漂移**；需要实机扫描确认当前扫描器不再告警 | 发布前改用官方 `ModHelper.StartCoroutine("pawnmind", routine)`（`_decomp/ModHelper.cs:1056`），把协程生命周期交给 Mod 管理器；或在 Workshop 构建中用配置关闭截图特性 |
| T-03 | `src/Bridge/MainThreadPump.cs:6-9` | 注释明确"No threads, tasks or coroutines are created" | PASS | 低 | 与代码一致（`Update()` 驱动） | 保持 |
| T-04 | `src/Game/ScreenshotService.cs:79-83` | `OnDestroy` 置空 ledger、`_running=false`；`PawnMindMod.cs:216-219` 销毁 pump 对象 | PASS | 低 | 协程随对象销毁结束；无后台泄漏路径 | 保持 |
| T-05 | `agent/fake_game.py:47,225-228`、`agent/tests/*` | `ThreadingHTTPServer` + `threading.Thread`（仅测试替身/单测） | 不适用（不进 Mods、不进 dist） | 低 | 外部 Python 进程；与游戏扫描无关 | 不要打包进 Mod 文件夹（见 §5） |
| T-06 | `agent/mcp_server.py:283` | `threading.Lock`（stdio server 内部锁） | 不适用 | 低 | 外部 MCP 进程，不是 Mod | 保持隔离 |

---

## 4. 网络监听

| 编号 | 文件:行号 | 调用形态 | 扫描器预测 | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| N-01 | `src/Bridge/BridgeServer.cs:215` | `new TcpListener(IPAddress.Loopback, _config.Port)`；`:216` `Blocking=false`；`:217` `Start()` | 触发 `network`（`System.Net.*`）→ **manifest 已声明** | 低（PASS-预测） | 只绑定回环；无 `IPAddress.Any`/`0.0.0.0`（grep 0）；端口默认 15528，可在 `bridge.conf` 调整；启用时玩家会看到 network 能力提示 | 保持；在 manifest/README/Workshop 描述中明确"仅 127.0.0.1" |
| N-02 | `src/Bridge/BridgeServer.cs:145,216,232,340,352,365,404,417,431,642,656,897` 等（`grep -c "Socket"` = 23 行） | 非阻塞 `Socket.Poll/AcceptSocket/Receive/Send/Close` | 同上（`network`） | 低 | 单线程 Tick 驱动（见 §8）；无 TLS；Bearer token + loopback 是边界 | 保持；不要在公网/0.0.0.0 暴露 |
| N-03 | — | `HttpListener`、`TcpListener` 之外：`HttpWebRequest`、`WebClient`、`HttpClient`、`UnityWebRequest`、`UdpClient`、`NetworkStream` | **0 命中** | PASS | 无外联、无下载代码 | 保持 |
| N-04 | `agent/pawnmind_client.py:12,76-88` | `urllib.request` + 关闭代理（`ProxyHandler({})`）访问 `http://127.0.0.1:<port>` | 不适用（外部进程） | 低 | 只读/动作客户端；token 走 header；/health 免 token；有重定向防护类 | 保持；绝不把 token 打到日志 |
| N-05 | `tools/probe.py:16,46-47` | 同上的手工探测脚本 | 不适用 | 低 | 运维工具，不进 dist | 保持隔离 |
| N-06 | `agent/fake_game.py:225` | `ThreadingHTTPServer(("127.0.0.1", 0), …)`（测试替身，随机端口） | 不适用 | 低 | 仅用于离线测试/演示；**不是游戏** | 严禁拷入游戏目录 |

---

## 5. 打包 Python / 脚本 / 外部进程

| 编号 | 文件:行号 | 调用形态 | 风险 | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| P-01 | `dist/PawnMind/` 实际内容 | `manifest.xml`（1,138 B，SHA256 `fc14f3b7…`）+ `PawnMind.dll`（296,960 B，SHA256 `ad335987…`） | 无（当前产物干净） | 低（PASS） | `find dist -type f` 只有 2 个文件；无 `.py`、无脚本、无第三方 DLL | 发布打包只从 `dist/` 取 |
| P-02 | `build.sh:103-106` | `rm -rf dist` → `mkdir dist/PawnMind` → 只 `cp manifest.xml` + `cp src/bin/Release/netstandard2.1/PawnMind.dll` | 无 | 低 | 历史上曾加过 `assert_safe_dist`（`:55-101`）防止符号链接/误删；`PAWNMIND_BUILD_ONLY=1`（`:46-50`）可只编译不打包 | 保持；任何"顺手把 agent/ 复制进 dist"的改动都必须拒绝 |
| P-03 | `.gitignore:4`（`**/dist/`） | dist 不入 git | 中（可复现性） | 中 | dist 是本地构建产物；无法用 git 校验"dist 是否对应 HEAD"（见 §9 M-05） | 构建后记录 SHA256 + MVID 到审计文档；发布脚本改为可复现 |
| P-04 | `PawnMind/agent/**`（41 个 `.py` + `pyproject.toml`）、`tools/**`、`.venv/`、`_decomp/`、`test/` | 外部进程/开发物 | **误打包=高**：若用户把整个仓库拷进 `Mods/PawnMind`，扫描器文件夹规则会命中 `payload-file`（`.venv` 里的 `.exe/.dll`）→ 启用被拒；另有 unlisted DLL 拒绝链（`ModLoader.cs:666-671`） | 高（仅误操作场景） | 当前 dist 不受影响；对外分发/安装说明必须强调"只复制 dist/PawnMind 一个文件夹" | 安装草稿 §2/§5 明确写死；发布前做一次"干净机器解压→扫描"演练 |
| P-05 | `agent/mcp_server.py`（stdio MCP，114 工具；`--tools recording` 104 个） | 独立 Python 进程，经 `stdio` 与宿主通信；`threading.Lock` 仅同步 | 低 | 外部安装器与游戏 Mod **物理分离**（见草稿 §3）；不写入游戏目录 | 保持；不要在 Mod 内拉起 Python 子进程（当前没有 `Process.Start`） |
| P-06 | `agent/orchestrator.py`、`agent/demo_agent.py`、`tools/*.py/.sh` | 开发/演示工具 | 低 | 不随 Mod 分发；`tools/setup_remote_host.sh` 是主机侧脚本 | 保持；发布包白名单只允许 `manifest.xml` + `<Assemblies>` 里的 DLL |

---

## 6. 原生 DLL / P-Invoke / DllImport

| 编号 | 文件:行号 | 调用形态 | 扫描器预测 | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| N-01 | — | `DllImport` / `extern` / `Marshal.*` / `unsafe` / `calli` / `NativeLibrary` | **0 命中** | PASS | `grep -rn "DllImport\|extern \|Marshal\.\|unsafe" src` = 0；`System.Runtime.InteropServices` 也未导入 | 保持禁止 |
| N-02 | `dist/PawnMind/PawnMind.dll` | PE32 Mono/.NET assembly，3 sections（`file` 输出） | 无法从源码证明"无原生依赖"，需扫描器/unpack 校验 | 低 | 源码无 P/Invoke，但二进制引用清单未在 Linux 验证 | `NOT_RUN`：`NR-10`（需 `ilspycmd`/`mod-scan` 查看引用；**不要**仅凭 `file` 宣称兼容） |
| N-03 | `/root/work/game_managed/*.dll` | 游戏 Managed 参考副本存在（只读） | 不适用 | 低 | 仅用于编译/契约测试，不会进 dist（`<Private>false</Private>`，`src/PawnMind.csproj:24-61`） | 保持 `Private=false` |

---

## 7. Unity 调用是否在 Core / 分层

| 编号 | 文件:行号 | 形态 | 结果 | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| L-01 | `src/Core/**`（含新增 `ClientSnapshotProjection.cs`、`SnapshotFieldMap.cs`） | `using UnityEngine` / Unity 类型 / 游戏类型 | **0**（注释/字符串里出现 `PlayerStore`/`GameState.Get*` 只是文档性描述，不是代码依赖） | PASS | 满足 AGENTS "Core 纯逻辑"；新增文件只在注释与说明字符串中提及游戏成员 | 保持；CI 可加静态检查 `grep -rn "using UnityEngine\|PlayerStore\|GameItem" src/Core` 必须为空 |
| L-02 | `src/Bridge/**`：`AnalyzerService.cs:6`(TMPro)、`BookService.cs:4-5`、`Diagnostics.cs:3`、`GameActions.cs:5`、`MainThreadPump.cs:2`、`UiSnapshot.cs:4-6` | Bridge 层直接 `using UnityEngine` / `TMPro` / `UnityEngine.UI` | 与"Game 是唯一直连游戏类型层"的分层目标存在偏差 | 中 | 当前全部从 Unity 主线程 Tick 进入（§8），无运行时越界；但类型系统不阻止将来从非主线程调用 | 把 `BookService`/`GameActions`/`UiSnapshot`/`AnalyzerService` 的游戏/Unity 访问下沉到 `src/Game/` 适配器；或至少加"仅主线程断言" |
| L-03 | `src/Bridge/BookService.cs:20-27,56,97-101` | 直接使用游戏类型 `GameInventory`/`GameItem`/`PaperBookRegistry`/`StoreBookData` | 编译期分层偏差 | 中 | `PaperBookRegistry` 在扫描器 GuardTypes 里（`ModScanner.cs:2000-2004`）——仅当**反射**它才硬旗；本处是直连调用，预测不触发，但属于脆弱的游戏内部契约 | 下沉到 Game 适配层 + 契约测试；禁止对 GuardTypes 做反射 |
| L-04 | `src/Game/**`（`ScreenshotService`、`NewGameMenuAdapter`、`WindowAdapter`、`RecordingSaveAdapter`、`PromptAdapter`） | `using UnityEngine`/`UI`/`ScreenCapture` | 允许层 | PASS | Game 层是唯一允许反射/直连游戏类型的层 | 保持 |

---

## 8. 主线程约束（MainThreadPump）是否有绕过

| 编号 | 文件:行号 | 形态 | 结论 | 等级 | 说明 |
|---|---|---|---|---|---|
| MT-01 | `src/Bridge/MainThreadPump.cs:20-22` | `Update()` → `_server.Tick()` | 唯一驱动入口 | PASS | 注释 `:6-9` 明确无线程/任务/协程；F7/Shift+F7 也在此轮询（`:24-34`） |
| MT-02 | `src/PawnMindMod.cs:132-139,147-158,174-189` | OnEnable 建 pump GameObject → `AddComponent<MainThreadPump>()` → `_server.Start()`；`StateSnapshot.BuildJson` / `_router.Handle` 作为委托传入 BridgeServer | 委托只在 `Tick()` 内被调用 → 主线程 | PASS | `:152-154` 注释明确"Handle() runs inside BridgeServer.Tick from MainThreadPump.Update" |
| MT-03 | `src/Bridge/BridgeServer.cs:252-329` | `AcceptPending` → `TryRead` → `HandleRaw`（状态/动作/健康委托）→ `Flush`；`/events` 长轮询在 `TickEvents` 内 | 全部在 Tick 内同步执行，无 blocking sleep | PASS | 非阻塞 socket + 每 tick 预算（`MaxAcceptsPerTick`/`MaxReadsPerTickPerClient`/`MaxWriteBytesPerTick*`）；`grep Thread.Sleep/Sleep(` = 0 |
| MT-04 | 全仓生产代码 | `Thread`/`Task`/`async`/`await`/后台回调 | **0 命中**（协程见 T-02，仍在主线程） | PASS | 静态未发现"HTTP 线程直接触游戏对象"的路径 |
| MT-05 | 结构性风险 | 无编译期/运行期"仅主线程"断言 | 将来回归风险 | 中 | Bridge/Game 服务可被任意线程 new 出来调用（类型系统不阻止）；建议在 `GameState`/`GameActions` 等入口加 `MainThreadPump` 线程断言（仅 Debug 构建）或做单测审查 |
| MT-06 | `src/PawnMindMod.cs:204-241` | `OnDisable` → `_server.Stop()` → 销毁 pump | 主线程 | PASS | `Stop()` 关闭 socket；无后台线程/任务需要 join |

---

## 9. Manifest / 版本 / dist 一致性

### 9.1 Manifest 静态检查

| 编号 | 检查项 | 结果 | 等级 | 说明/证据 |
|---|---|---|---|---|
| M-01 | `<ID>` = `pawnmind` | 合法（字母/数字，非 `.` 开头，≤64） | PASS | `ModLoader.cs:1123-1132` 规则 |
| M-02 | `<Assemblies>` 非空、相对路径、`.dll` | `PawnMind.dll` ✓ | PASS | `ModLoader.cs:1133-1169` |
| M-03 | `<Capabilities>` 元素存在、能力名已知 | `network`、`reflection-external` ✓（均属 9 种已知） | PASS | `ModLoader.cs:1170-1187` |
| M-04 | **能力声明完整性** | 预测检测集合 = `{network, reflection-external, files}`；`files` **未声明** → 启用链会在 `ModLoader.cs:739-743` 拒绝 | **高（预测）** | 见 §11 F-01；`files` 检测点：`ScreenshotService.cs:157,197,201,207`、`RecordingSaveAdapter.cs:106,110,151` |
| M-05 | 降级/提示语义 | 声明 `reflection-external`（retiring）→ 启用时出现"下个版本将拒绝"的 DEPRECATED 提示；`network` 会显示能力确认 | 中 | `ModCapability.cs:37,44-51`；`ModSecurityDebug.cs:54-58`；`ModLoader.cs:745-746` |
| M-06 | Workshop 专属限制 | 2026-12-10 起 Workshop Mod 声明 `files`/`network` 需开发者验证（本构建开关仍为 false，但文案已在游戏内） | 高（发布计划） | `ModSecurityDebug.cs:67-71`；`ModTrustList.cs:117-119`；`ModLoader.cs:694-699` |
| M-07 | `<GameVersions>` = `0.46D` | 与 README/已知限制一致（0.46D）；内部构建号文档写 `100-REV6`/`100-Rev13`，两者不是同一字段，未核实 | 低 | `ModUIManager.cs:873,954` 仅在不匹配时给 issue 行，不是拒绝 |
| M-08 | 未使用 `<Prerequisites>` | 无 | PASS | 无需前置 Mod；`ModReflect`/`Prerequisites` 规则不涉及 |

### 9.2 版本号一致性（三处不一致）

| 来源 | 值 | 证据 |
|---|---|---|
| `manifest.xml:6`（src 与 dist 相同 SHA256） | `ModVersion = 0.3.0` | `sha256sum`：`fc14f3b7e65313c1cd9afef17480b1a4733422c3355cccf2ba2f439314ce124c`（src 与 dist 相同） |
| git tag（PawnMind 最新） | `v1.0.1`（`6b01756`）；最终复核时 HEAD = `6ab3333`，`git describe` = `v1.0.1-42-g6ab3333` | `git tag --list`、`git log -1 v1.0.1`、`git describe --tags --always` |
| README / AGENTS | `v1.0.1`（`README.md:6`）但"已知限制"写 **`manifest 0.2.1`**（`README.md:116`、`AGENTS.md:52`） | 文档漂移 |
| `/health` 的 `version` / `mod_version` | 取自 manifest（`PawnMindMod.cs:97,179`；`BridgeServer.cs:701-702`） | 因此实际报告 `0.3.0` |
| 其他 tag | `v1.9.4` 属于 **CompactSort**（`git log -1 v1.9.4` = `feat(compact-sort): 1.9.4 …`），不要误当 PawnMind | — |

> 结论：**发布前必须统一版本号**（manifest / tag / README / 工坊条目），否则工坊"更新版本"与信任记录（`trust=Updated` 需玩家重新确认）会混乱。等级：中。

### 9.3 dist 产物身份与新鲜度

| 编号 | 检查项 | 结果 | 等级 | 说明 |
|---|---|---|---|---|
| D-01 | dist 文件清单 | 只有 `manifest.xml` + `PawnMind.dll` | PASS | `find dist -type f -printf '%p %s\n'` |
| D-02 | dist DLL SHA256 / 大小 | `ad3359875348ff672ff15f520ac9dd518f38221e1eb3ae85a287ea13930185f7` / 296,960 B | 记录 | `sha256sum`；与历史文档 `docs/audit/BASELINE_CURRENT.md:30` 一致（`ad335987…`） |
| D-03 | dist manifest SHA256 | `fc14f3b7e65313c1cd9afef17480b1a4733422c3355cccf2ba2f439314ce124c`，与 `src` manifest 相同 | PASS | `sha256sum manifest.xml dist/PawnMind/manifest.xml` |
| D-04 | **dist 是否对应当前源码** | **不对应（STALE）**：dist DLL mtime `2026-11-16 17:58:22Z`；最终复核时 **20 个源码 `.cs` 文件更新**（列表见 §14 命令）。其他模块在 20:16 留下的离线构建产物 `src/bin/Release/netstandard2.1/PawnMind.dll`（SHA256 `df84589a…`，320,512 B，mtime 20:16:09Z）与 dist（`ad335987…`，296,960 B）**哈希不同** → dist 确定不是当前代码构建；该构建产物自身也有 7 个 `.cs` 比它新，且其构建溯源未被本审计独立验证 | **高** | `find src -name '*.cs' -newer dist/PawnMind/PawnMind.dll`；`sha256sum src/bin/Release/netstandard2.1/PawnMind.dll dist/PawnMind/PawnMind.dll`；`build.sh:106` 的输入是 `src/bin/Release/netstandard2.1/PawnMind.dll` |
| D-05 | dist 是否可被 git 校验 | 不可：`.gitignore:4` `**/dist/` | 中 | `git check-ignore -v PawnMind/dist/PawnMind/PawnMind.dll` → `.gitignore:4:**/dist/` |
| D-06 | build 产物 / MVID | 离线构建产物**存在**（`src/bin/Release/netstandard2.1/PawnMind.dll`，`df84589a…`，320,512 B，20:16，由其他模块生成，非本审计产物）；但 DLL MVID/程序集版本未读取、构建溯源未核实 | **NOT_RUN** | §10 `NR-06`/`NR-10` |
| D-07 | 与已安装 DLL 比较 | 已安装目录本机不可访问（无 `/mnt`、无 Probably Stolen 安装；`/root/work/game_managed` 只是 Managed 参考目录，不是 `Mods/PawnMind`） | **NOT_RUN** | §10 `NR-04`；历史记录：已安装 `e9e1bd07…`、295,424 B（`docs/audit/BASELINE_CURRENT.md:31`、`docs/plans/PLAYER_PARITY_IMPLEMENTATION-v2.md:29`）→ 与 dist `ad335987…`、296,960 B **不一致** |
| D-08 | 是否"只记录不覆盖" | **是**：本批次未复制、未安装、未覆盖任何游戏目录 | PASS | 仅 `sha256sum`/`stat`/`ls` |

> **验收含义**：`dist` ≠ 当前源码，且 `dist` ≠ 已安装 DLL（历史记录）。因此**任何当前游戏内现象都不能归因于仓库 HEAD 代码**；也不能用旧安装证明新代码可用。等级：**高（验收归因）**。

---

## 10. 官方 ModScanner / APIChanges：`NOT_RUN` 与获取方式

> 纪律：没有本地运行结果，就**不写"0 硬旗""兼容"结论**。以下全部为 `NOT_RUN`，并给出精确复现命令。
> 本地反编译规则副本（`_decomp/`）只能支撑"预测"，且已明确版本/MVID 未核实。

| NR | 项 | 本机状态 | 精确命令/获取方式 | 期望记录 |
|---|---|---|---|---|
| `NR-01` | 官方扫描命令 `mod-scan`（游戏内） | `NOT_RUN`（无游戏进程） | 启动 0.46D → 加载 PawnMind → 打开开发者控制台（F8）→ 执行：`mod-scan pawnmind`；未加载时：`mod-scan "D:\Program Files\steam\steamapps\common\Probably Stolen\Mods\PawnMind"` | 完整输出：`declared` / `detected` / `UNDECLARED (enable would be refused)` / `DEPRECATED` / `VERIFICATION` / `HARD FLAGS` / `findings` / `fingerprint` |
| `NR-02` | 离线 ScannerCheck harness | `NOT_RUN`（需 Windows 构建机；构建会写 `bin/obj`，本批次禁止污染共享树/构建） | `cd CompactSort/test/ScannerCheck && dotnet run -c Release -- "D:\Program Files\steam\steamapps\common\Probably Stolen\Mods\PawnMind"`（`Program.cs:8-10` 硬编码 Managed 路径；需要游戏 0.46D 默认安装位置） | `error=`、`refused=`、`caps=`、`methods=`、`hard=`、`findings=`；注意它**不打印** declared/undeclared/verification |
| `NR-03` | `APIChanges.md` / `ModdingGuide.md` | 仓库内**不存在**（`find` 0 命中）；内容未在本地核实 | `cd /root/work/game_mods && find . -path ./.git -prune -o \( -iname 'APIChanges*' -o -iname 'ModdingGuide*' \) -print`；仓库内文档引用的获取地址（**仅获取方式，未核实内容，禁止当作本地证据**）：`https://github.com/dragonpan2/probably-stolen-modding/blob/main/APIChanges.md`、`.../ModdingGuide.md`（见 `docs/PAWNMIND_AGENT_BRIEF_NEXT_PHASE_2026-11-17.md:329-332`） | 下载后记录 commit/hash 与读取日期；与 `_decomp/ModScanner.cs` 规则对比 |
| `NR-04` | dist vs 已安装 DLL / manifest | 安装目录不可访问 | Windows/WSL 构建机：`sha256sum "/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Mods/PawnMind/PawnMind.dll"` + `stat -c '%s %y' …`；`sha256sum …/manifest.xml`；`cat …/manifest.xml` | 与 dist `ad335987…`/296,960 B 对比；历史安装 `e9e1bd07…`/295,424 B；**只记录，不覆盖** |
| `NR-05` | 运行中版本/MVID/`/health` | `NOT_RUN`（无游戏） | 游戏运行后：`curl -s http://127.0.0.1:15528/health`（`/health` 免 token）；记录 `ok`、`version`、`game_version`、`game_assembly_mvid`、`expected_*`、`write_gate`、`latest`；或游戏内 `mod-trust` | 与 `bridge.conf` 的 `expected_*` 对比；`latest=UNVERIFIED` 是预期 |
| `NR-06` | 只编译不打包的构建验证 | `NOT_RUN`（本批次禁止跑 `test.sh`/`build.sh`；共享树有他人写入；Linux 无 Windows dotnet 目标） | 构建机：`cd PawnMind && PAWNMIND_BUILD_ONLY=1 ./build.sh`（Windows Git Bash）→ 记录退出码 + `sha256sum src/bin/Release/netstandard2.1/PawnMind.dll` | 与 dist 对比；确认可复现 |
| `NR-07` | Workshop 上传/验证/预览素材 | `NOT_RUN`（未发布，按边界不执行） | 由 Steam 客户端/游戏内工坊 UI 完成；上传前需 `mod-scan` 0 硬旗 +（若声明 files/network）开发者验证；本仓库未找到离线上传工具 | 记录工坊 item id、版本、验证状态；**本批次不上架** |
| `NR-08` | 真实启用/拒绝/能力提示行为 | `NOT_RUN`（无游戏） | 见 `NR-01` 前：把 dist 装到**只读验证环境**后启用，记录 `ModEnableCheck` 的 refusal/prompt 文案 | 预测：`UNDECLARED files` 拒绝（见 §11 F-01）；`reflection-external` DEPRECATED 提示 |
| `NR-09` | 真实存档/ModData 路径与方法 | `NOT_RUN`（本机无 Windows 用户环境） | 游戏运行后查看 `%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\pawnmind\bridge.conf`（只读）；存档目录只做定位，**不读写** | 路径仅用于安装草稿的"禁止触碰"清单 |
| `NR-10` | dist DLL 的引用/IL 级检查（unlisted-reference/native 的二进制证据） | `NOT_RUN`（无 ilspycmd，且本批次不允许运行工具/构建） | 构建机：`ilspycmd -l c dist/PawnMind/PawnMind.dll`（或 `mod-scan`）；检查 AssemblyReferences 是否只有游戏程序集（Assembly-CSharp/UnityEngine.*/Unity.TextMeshPro/Newtonsoft.Json）与 P/Invoke/unsafe | 若出现非游戏引用 → `unlisted-reference` 硬旗风险 |

---

## 11. 逐条风险命中（预测，按类别汇总）

> 所有"预测命中"需 `NR-01`/`NR-08` 实机确认；**不把预测写成 PASS/FAIL**。

### 11.1 `files` capability（最高优先级）

| 编号 | 文件:行号 | 调用形态 | 预测 | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| F-01 | `src/Game/ScreenshotService.cs:157` | `ScreenCapture.CaptureScreenshotAsTexture()` | `files`（`UnityEngine.ScreenCapture` 在 FileTypes） | 高（未声明→拒绝） | 截图证据特性（2026-11-15 新增） | 二选一：①manifest 声明 `files` 并接受 2026-12-10 起 Workshop 需验证；②发布构建关闭截图特性（需配置/条件编译支持，当前未实现） |
| F-02 | `src/Game/ScreenshotService.cs:197` | `System.IO.Directory.Exists(folder)` | `files` | 高 | 读取 ModData 截图目录用量 | 改用 `ModHelper` 文件 API（如提供列举能力）或接受 `files` |
| F-03 | `src/Game/ScreenshotService.cs:201` | `System.IO.Directory.GetFiles(...)` | `files` | 高 | 同上 | 同上 |
| F-04 | `src/Game/ScreenshotService.cs:207` | `new System.IO.FileInfo(...).Length` | `files` | 高 | 同上 | 同上 |
| F-05 | `src/Game/RecordingSaveAdapter.cs:106` | `SaveFiles.IndexLoad<List<int>>(...)` | `files`（`SaveFiles` 类带 `[ModRestricted("files", …)]`，`_decomp/SaveFiles.cs:12`） | 高 | 读存档索引（只读，但扫描器按能力分类） | 无法用 `ModHelper` 替代（游戏未提供公开存档索引 API）；建议声明 `files` + 申请 Workshop 验证，并把"读索引"限制在只读/录制策略门禁后 |
| F-06 | `src/Game/RecordingSaveAdapter.cs:110` | `SaveFiles.IndexLoad<int>("slotIdGenerator", 0)` | `files` | 高 | 同上 | 同上 |
| F-07 | `src/Game/RecordingSaveAdapter.cs:151` | `SaveFiles.IndexLoad<SavePreviewData>(...)` | `files` | 高 | 读绑定槽的预览时间戳（不读预览对象本体） | 同上 |
| F-08 | `src/Game/ScreenshotService.cs:196` | `System.IO.Path.Combine(...)` | 不触发（`Path` 不在 FileTypes） | 低 | 与 F-02/F-03 成组出现 | 随 F-02/F-03 一起改 |
| F-09 | `src/PawnMindMod.cs:949,969`；`src/Bridge/Diagnostics.cs:46`；`src/Game/ScreenshotService.cs:135` | `ModHelper.ReadModDataFileText / WriteModDataFileText / WriteModDataFileBytes` | 不触发（游戏许可 API） | PASS | 受路径穿越防护（`RequireInside`） | 保持 |
| F-10 | `src/Bridge/BridgeServer.cs:3,116,387` | `System.IO.MemoryStream` | 不触发（不属于 FileTypes） | PASS | 仅内存缓冲 | 保持 |

> **F 组结论**：当前 manifest **缺 `files`**。按本地启用链（`ModLoader.cs:739-743`），哪怕其余都干净，启用也会以"uses capabilities its manifest does not declare"被拒。修复方式不是"猜"，而是先跑 `NR-01` 确认命中集合，再决定"声明 files + 申请验证"还是"改造/关闭该特性"。

### 11.2 `reflection-external`（未来阻塞）

预测命中 14 处 computed-`Type` 反射：`RecordingSaveAdapter.cs:402,410`；`NewGameMenuAdapter.cs:401,403,405,407,1280,1288,1298,1306,1321,1331,1346,1354`。manifest 已声明该能力（当前允许，但 retiring），**2026-12-10 更新起拒绝**。整改路径见 §2.1 方案 A/B/C。等级：高（未来发布阻塞）。

### 11.3 `network`（已声明、形态合规）

`BridgeServer.cs` 的回环 `TcpListener` + `Socket` 预测为 `network`；manifest 已声明；无公网绑定/外联。等级：低。注意：能力提示会被玩家看到（"Access the internet"），安装草稿必须解释"仅 127.0.0.1"。

### 11.4 其他零命中

`process` / `dynamic-code` / `native` / `steam` / `steam-auth` / `threads` 预测 **0 命中**（依据：§3/§6 的 0 结果 + §1.2 规则）。仍以 `NR-01` 为准。

---

## 12. 统计汇总

> 口径见 §14 命令；快照 = §0.1。共享树仍在被其他模块修改，数字可能漂移。

| 指标 | 数量 | 备注 |
|---|---|---|
| **反射 API 调用点** | **26 行**：成员查找 19（`GetField` 11 + `GetMethod` 7 + `GetProperty` 1）+ `MethodInfo.Invoke` 7 | 另有 `UnityEvent.Invoke` 2 行**不计**反射；`using System.Reflection` 文件 4 个 |
| `typeof(...)` | **68 行** / 4 文件（NewGameMenuAdapter 61、RecordingSaveAdapter 3、GameVersionAdapter 2、GameState 2） | 字面量 game 类型预测不触发 |
| 预测反射 `reflection-external` 命中 | **14 处**（computed Type） | manifest 已声明，但 retiring |
| **后台线程** | **0**（Thread/ThreadPool/Task/Timer/Parallel/async/await 全 0） | — |
| **协程** | **1 处**（2 行：`StartCoroutine` + `IEnumerator`） | `ScreenshotService`；自身 MonoBehaviour 预测不命中 `threads` |
| **网络监听器** | **1**（`TcpListener`，`127.0.0.1`）+ `Socket` API **23 行** | `HttpListener`/WebClient/HttpClient/UnityWebRequest = 0 |
| **P/Invoke / DllImport / unsafe** | **0** | — |
| 预测 `files` 命中 | **8 处**（F-01~F-07，其中 F-08 不触发） | manifest 未声明 → 预测拒绝启用 |
| **Python/脚本进 dist** | **0**（dist 只有 2 个文件） | dist SHA256 已记录 |
| **Core 中的 UnityEngine 调用** | **0** | Core 纯逻辑保持 |
| **主线程约束绕过** | **0（静态）** | 仅协程在 Unity 主线程；无后台线程 |
| dist 文件数 | 2（DLL 296,960 B + manifest 1,138 B） | `ad335987…` / `fc14f3b7…` |
| 比 dist 新的源码文件 | **20**（最终复核时 `.cs`；首扫时 14，树仍在写入） | dist STALE |
| 离线构建产物 vs dist | `df84589a…`（320,512 B）≠ `ad335987…`（296,960 B） | 构建产物由其他模块生成，溯源未核实 |
| Manifest 版本 vs tag | `0.3.0` vs `v1.0.1` | 三处不一致（§9.2） |

---

## 13. Top 风险与发布建议（不修改源码，仅建议）

### Top 3 最大风险

1. **【高·发布阻塞-预测】manifest 未声明 `files` capability → 启用被拒**
   - 证据：检测点 `ScreenshotService.cs:157,197,201,207`、`RecordingSaveAdapter.cs:106,110,151`；拒绝链 `_decomp/ModLoader.cs:739-743`；规则 `_decomp/ModScanner.cs:50-56,681-700`。
   - 复现：`NR-01`（游戏内 `mod-scan pawnmind`）→ 看 `UNDECLARED (enable would be refused)`。
   - 决策树（需实机确认后选）：①声明 `files` → 接受"2026-12-10 起 Workshop 需开发者验证"（`ModSecurityDebug.cs:67-71`）并准备验证材料；②改造：截图用量统计改走 `ModHelper`/去掉；存档索引读出无官方公开替代（HYPOTHESIS），若无法消除则必须走①。
2. **【高·未来阻塞】`reflection-external` 2026-12-10 起被拒，当前 14 处 computed-`Type` 反射 + manifest 仍声明**
   - 证据：`_decomp/ModSecurityDebug.cs:54-58`（游戏内文案）、`_decomp/ModCapability.cs:37,44-60`；命中点见 §11.2。
   - 建议：2026-12-10 前按 §2.1 方案 B（`ModReflect`）/A（字面量下放），然后把 `<Capability>reflection-external</Capability>` 从 manifest 移除；**在完成前不要把工坊发布为"长期可用"**。
3. **【高·验收归因】dist STALE + dist ≠ 已安装 + 版本三处不一致**
   - 证据：dist DLL mtime 17:58:22Z 早于至少 19 个源码 `.cs` 文件（§9.3 D-04）；20:16 的离线构建产物 `df8458…`/320,512 B 与 dist `ad3359…`/296,960 B 哈希不同；历史安装哈希 `e9e1bd07…`/295,424 B（`docs/audit/BASELINE_CURRENT.md:30-31`）；manifest `0.3.0` vs tag `v1.0.1` vs 文档 `0.2.1`。
   - 建议：在构建机跑 `NR-06` 重建并在**用户在场**时按 `docs/plans/NEXT_LIVE_SESSION.md` 安装；记录三份 SHA256 + MVID + `/health`；在此之前所有游戏内结论标注 `NOT_ATTRIBUTABLE`。

### 发布前建议（只记录，不在本批次执行）

1. 先跑 `NR-01`/`NR-08`：取得真实 `mod-scan` 输出，替换本矩阵所有"预测"。
2. 按结果修 manifest（`files` 是否需要）与反射（2026-12-10 前）。
3. 统一版本号（manifest/tag/README/工坊），重建 dist 并记录 SHA256/MVID。
4. 打包只用 `dist/PawnMind/`；安装/回滚按 `docs/plans/WORKSHOP_INSTALL_DRAFT.md` 草稿执行；**不覆盖用户 DLL、不碰存档**。
5. 取得 `APIChanges.md`/`ModdingGuide.md` 最新版并记录 commit；与本地反编译规则差异记入下轮审计。

---

## 14. 附录：证据命令（只读；可复现 §12）

```bash
cd /root/work/game_mods/PawnMind

# 0) 快照
date -u; git -C .. rev-parse HEAD; git -C .. status --porcelain | head -30

# 1) 反射
grep -rn "GetField("  src --include='*.cs' | wc -l        # 11
grep -rn "GetMethod(" src --include='*.cs' | wc -l        # 7
grep -rn "GetProperty(" src --include='*.cs' | wc -l      # 1
grep -rn "\.Invoke("  src --include='*.cs' | wc -l        # 9（含 2 个 UnityEvent）
grep -rn "typeof("    src --include='*.cs' | wc -l        # 68
grep -rn "Assembly\.Load\|Activator\.\|CreateDelegate\|Reflection\.Emit\|GetTypes(\|AppDomain" src  # 0
grep -rln "System.Reflection" src                          # 4 个文件

# 2) 线程/协程
grep -rn "System.Threading\|ThreadPool\|\bTask\b\|new Thread\|Timer\|await \|async " src   # 0
grep -rn "StartCoroutine\|IEnumerator" src                 # 2 行
grep -rn "Thread\.Sleep" src                               # 0

# 3) 网络
grep -rn "TcpListener" src                                 # 2 行（1 个实例）
grep -rn "Socket" src | wc -l                              # 23
grep -rn "HttpListener\|WebClient\|HttpWebRequest\|HttpClient\|UnityWebRequest\|UdpClient" src  # 0
grep -rn "IPAddress\.Any\|0\.0\.0\.0" src                  # 0

# 4) 原生
grep -rn "DllImport\|extern \|Marshal\.\|unsafe\|System.Runtime.InteropServices" src  # 0

# 5) Core 纯度
grep -rn "using UnityEngine" src/Core | wc -l              # 0
grep -rn "using UnityEngine" src/Bridge                     # 6 行（分层偏差，见 §7）
grep -rn "using UnityEngine" src/Game                       # 7 行（允许层）

# 6) 文件能力（预测 files）
grep -rn "System\.IO\.\|ScreenCapture\." src               # 5 行
grep -rn "SaveFiles\." src                                 # 3 行
grep -rn "ModHelper\." src                                 # 6 行（授权 API）

# 7) dist
find dist -type f -printf '%p %s bytes\n'
sha256sum dist/PawnMind/PawnMind.dll dist/PawnMind/manifest.xml manifest.xml
find src -name '*.cs' -newer dist/PawnMind/PawnMind.dll | wc -l   # 20（2026-11-16T20:21Z；随后仍在变）
sha256sum src/bin/Release/netstandard2.1/PawnMind.dll          # df84589a…（20:16，其他模块产物）vs dist ad335987…
git -C .. check-ignore -v PawnMind/dist/PawnMind/PawnMind.dll

# 8) 官方规则副本定位（仅为推导，不是运行结果）
ls /root/work/game_mods/_decomp/ModScanner.cs /root/work/game_mods/_decomp/ModSecurityDebug.cs
grep -n "VERSION = 4\|REFUSE_UNSCANNABLE_CAPABILITIES" /root/work/game_mods/_decomp/ModScanner.cs
```

---

## 15. 审阅者须知

- 本文件是 **static audit only**：`PASS`/`FAIL` 只用于静态项；所有需要游戏/Scanner 的项均为 `NOT_RUN`（§10）。
- 预测来自本地反编译规则副本（`_decomp/ModScanner.cs` VERSION=4），**不是**官方运行结果；其与已安装构建的 MVID 对应关系未核实（`UNKNOWN`）。
- 未引用任何网传兼容性口径；外部链接仅作获取方式记录。
- **共享树是移动的**：本文件跨越 `4846189`（首扫）→ `6ab3333`（Nightly B 提交，最终复核）两个快照；最终复核后又出现模块 D/E 的未提交写入与 20:16 的离线构建产物。分类计数在两次快照间一致（§0.1 说明），但**合并/提交后必须用 §14 重跑**并据此复核本矩阵。
- 与本文件配套阅读：`docs/plans/WORKSHOP_INSTALL_DRAFT.md`（安装/回滚草稿，未上架）。
