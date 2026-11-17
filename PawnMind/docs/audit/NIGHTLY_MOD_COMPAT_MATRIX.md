# NIGHTLY_MOD_COMPAT_MATRIX — PawnMind 静态兼容/发布预审矩阵

> Nightly Batch A · 模块 G（发布/兼容静态预审）
> 状态：**静态预审 + 编译 gate 实测完成**；官方 ModScanner 实机运行 = `NOT_RUN`；`dist` 打包/安装 = `NOT_RUN`；**未发布、未上架、未安装、未覆盖游戏目录**。
> 本文件只记录证据与建议，**不包含任何安装/覆盖命令的执行**。执行边界见 §0.3。
> **2026-11-16 复核（task-4）**：修正了上一版 `NR-06` 的错误结论（"Linux 不可编译"不成立）；C/D/E 合并后按 HEAD `f789a92` 重扫，并实测 `PAWNMIND_BUILD_ONLY=1 ./build.sh` 通过。

## 0. 元数据与边界

### 0.1 审计快照（可复现）

| 项 | 值 | 证据命令 |
|---|---|---|
| 快照时间（UTC） | 首扫 `2026-11-16T20:12:17Z` @ `4846189` → 复扫 `20:21:06Z` @ `6ab3333` → **C/D/E 合并后复核 + 编译 gate `20:40–20:41Z` @ `f789a92`** | `date -u` / `git log -1` |
| HEAD（最终） | `f789a92f7b347ab605ab3230f08512684629b25e`（`git describe` = `v1.0.1-45-gf789a92`）；审计期间新增 C/D `f09314e`、E `299ecd4`、G 文档 `f789a92` | `git rev-parse HEAD` / `git describe --tags --always` |
| 工作树 | 审计开始时 clean；复核/写文档期间其他模块在更新 `AGENTS.md`、`README.md`、`PawnMind/AGENTS.md`、`PawnMind/README.md`、`docs/audit/NIGHTLY_BASELINE.md`、`docs/audit/NIGHTLY_TEST_MATRIX.md`、`docs/reports/NIGHTLY_BATCH_A_REPORT.md`、`agent/tests/test_skill_tool_refs.py`（均为文档/测试元数据，非本模块修改） | `git status --porcelain` |
| 扫描范围 | `PawnMind/src/**`（含 C/D 新增 `TradeService`/`GameTradePort`/`Operation*`/`TradeGuard`）、`PawnMind/agent/**`、`PawnMind/tools/**`、`PawnMind/build.sh`、`PawnMind/manifest.xml`、`PawnMind/dist/**`、`PawnMind/test.sh`（只读）、`<game-managed>`（只读）、`<repo>/_decomp`（只读） | — |
| 本文件统计口径 | 见 §12；全部计数可用 §14 命令重跑 | — |

> ⚠️ 本矩阵是**时间点快照**：批次合并后必须用 §14 命令重跑；数字漂移不代表本文件当时造假。
> **跨快照稳定性**：`4846189` → `6ab3333` → `f789a92` 三次快照中，§3–§8 的分类计数一致（反射 API 26 行 / `typeof` 68 / 线程 0 / 协程 2 行 / 监听 1 / Socket 23 行 / P-Invoke 0 / Core Unity 0 / 预测 `files` 8 处）；C/D 合并**未新增**任何反射、线程、监听器或文件能力命中。

### 0.2 结论摘要（先看这里）

| # | 结论 | 等级 | 性质 |
|---|---|---|---|
| C1 | 预测存在 **未声明的 `files` capability**（`System.IO`/`UnityEngine.ScreenCapture`/`SaveFiles.IndexLoad` 命中），按本地反编译的启用链会**拒绝启用**；manifest 目前只声明 `network` + `reflection-external` | **高（发布阻塞-预测）** | 需实机 `mod-scan` 确认，`NOT_RUN` |
| C2 | `reflection-external` 是官方 **retiring/deprecated** 能力；本地游戏文案写明 **2026-12-10 更新起拒绝**。当前有 14 处 computed-`Type` 反射命中，manifest 仍声明该能力 → 该日期后发布即被拒 | **高（未来阻塞）** | 本地规则副本 + 游戏内文案；运行态 `NOT_RUN` |
| C3 | `dist` 与源码、`dist` 与已安装 DLL **三方不一致**：dist DLL 早于 26 个当前源码 `.cs` 文件；本机编译 gate 产出的新 DLL（`d29209a4…`/338,432 B）与 dist（`ad335987…`/296,960 B）不同；已安装 DLL 本次不可访问（历史记录 `e9e1bd07…`/295,424 B）。**当前游戏现象不可归因于仓库代码** | **高（归因/验收）** | dist/产物实测 SHA256；安装侧 `NOT_RUN` |
| C4 | 版本号不一致：`manifest.xml` = `0.3.0`，最新 Mod tag = `v1.0.1`；README/AGENTS 文档已在本轮被其他模块更新（旧版曾写 `0.2.1`，复核时为 `0.3.0` 但三处仍未统一到同一发布号） | 中（发布卫生） | 实测文件 + git tag |
| C5 | 后台线程 / `Task` / `ThreadPool` / `Timer` / `async` = **0**；网络监听仅 1 个 **loopback** `TcpListener`；P/Invoke / `DllImport` / `unsafe` = **0**；Core 无 `UnityEngine` = **0**；主线程约束静态未发现绕过（含新 `/operation` 端点） | 低 / PASS（静态） | 见 §3–§8 |
| C6 | 打包面干净：`dist/PawnMind/` 只有 `manifest.xml` + `PawnMind.dll`，**Python/脚本/外部进程不进 dist**；但"整仓拷进 Mods"会被扫描器按 payload/unlisted-DLL 拒绝 | 低（当前） / 高（误打包） | §5 + 安装草稿 |
| C7 | 仓库内**没有**官方 `APIChanges.md` / `ModdingGuide.md`；官方可执行 Scanner 仍不可运行（无游戏进程），但 `_decomp/` 有本机反编译规则副本（仅分析用）。扫描结果一律 `NOT_RUN` | 中（证据缺口） | §1 + §10 |
| C8 | **编译 gate 实测 PASS**（本机 Linux，.NET 9 SDK）：C# 749/0（审阅修复后 750/0）、脚本门禁 53/0、适配器契约 503 OK、`Build succeeded 0 Warning/0 Error`、退出码 0；`PAWNMIND_BUILD_ONLY=1` 在 dist 打包前退出。**dist 打包/安装仍 `NOT_RUN`** | PASS（编译） / NOT_RUN（打包·安装） | §10.1 `NR-06` |

### 0.3 不可突破的边界（本矩阵遵守）

- 未启动/未操作游戏；未读用户存档；未安装/覆盖任何 DLL；**未对 `Mods/PawnMind` 或游戏目录写入**；未提交 commit、未推送。
- 本任务在 lead 授权下运行 **仅一次** `PAWNMIND_BUILD_ONLY=1 ./build.sh`（内部会先跑 `./test.sh`；在 dist 打包前退出，**未改写 dist**）；未运行完整 `./build.sh`、未运行游戏。
- 允许的只读操作：`grep` / `read` / `sha256sum` / `ls` / `stat` / `git status` / `git log` / 只读 `gen_policy_catalog.py --check`。
- 本批次修改文件仅：本文件 + `docs/plans/WORKSHOP_INSTALL_DRAFT.md`。`src/`、`test/`、`agent/`、`scripts`、`dist/`、`manifest.xml` 均未修改（构建产生的 `src/bin`、`src/obj` 为 gitignore 产物）。
- `_decomp/` 是 gitignore 的本地反编译产物（AGENTS.md §5）：只用于互操作分析，**不进 dist、不随工坊包分发**；本文只引用类型名/方法名/结论，不复制大段反编译代码。

---

## 1. 规则来源：官方 ModScanner 与 APIChanges 的本地状态

### 1.1 找到了什么

| 对象 | 本地状态 | 位置/证据 | 能否作为运行结果 |
|---|---|---|---|
| 官方可执行 ModScanner | **仍不可运行**（本机无游戏进程；离线 harness 需游戏程序集且会写构建产物，本任务未授权运行） | 见 §10.2 `NR-01`/`NR-02` | `NOT_RUN` |
| 编译链路（供"能否构建"判断） | **可用且已实测**：.NET 9 SDK；`.env.server` 提供 `DOTNET` / `GAME_MANAGED` / `PAWNMIND_NEWTONSOFT_DLL`；`PAWNMIND_BUILD_ONLY=1 ./build.sh` 通过 | `<dotnet>/dotnet`（9.0.321）；`<game-managed>`；证据 §10.1 `NR-06` | PASS（编译 gate） |
| 官方规则源码（反编译副本） | **存在**（本机从已安装游戏程序集反编译，`ModScanner.VERSION = 4`） | `_decomp/ModScanner.cs:1994`；`_decomp/ModCapability.cs`；`_decomp/ModScanResult.cs`；`_decomp/ModScanFinding.cs`；`_decomp/ModSecurityDebug.cs`；`_decomp/ModLoader.cs`（启用链） | 只作**规则推导**，不是扫描结果 |
| 游戏内官方命令 `mod-scan` | 源码存在（`[Command("mod-scan", …)]`，需游戏进程内执行） | `_decomp/ModSecurityDebug.cs:11-101` | `NOT_RUN` |
| 离线扫描 harness | 存在（CompactSort 的历史工具，硬编码 Windows 游戏安装路径，会写 `bin/obj`） | `CompactSort/test/ScannerCheck/Program.cs:8-10`、`ScannerCheck.csproj` | `NOT_RUN` |
| `APIChanges.md` / `ModdingGuide.md` | **仓库内不存在**（`find` 0 命中） | §10.2 `NR-03` | `NOT_RUN` |
| 游戏程序集参考副本 | 存在（`<game-managed>`，仅 Managed 目录，非安装目录） | `Assembly-CSharp.dll` SHA256 `725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05`，4,820,224 B，mtime 2026-11-09 | 参考用，MVID `NOT_RUN` |

> **来源声明**：本矩阵的"扫描器预测"全部由 `_decomp/` 本地反编译副本的规则逐条推导，**没有引用任何网传兼容性口径**；外部 URL 只作为"获取方式"记录（§10.2 `NR-03`），其内容未在本机核实，不作为规则依据。
> **局限**：反编译副本对应 0.46D 的某个安装构建，但**未能证明**它与我方编译所用 `<game-managed>` 是同一 MVID；规则会随更新变化，实机 `mod-scan` 结果永远优先于本矩阵。

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
> C/D 合并后复核：以下行号仍然有效（`GameState` 仅追加新方法，原反射行未移动；`GameVersionAdapter`/`RecordingSaveAdapter`/`NewGameMenuAdapter` 未改动）。

| 编号 | 文件:行号 | 调用形态 | 扫描器预测（VERSION=4） | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| R-01 | `src/Game/GameVersionAdapter.cs:28` | `typeof(PlayerStore).GetField("VERSION_NUMBER", Public\|Static)` | 不触发（字面量 game 类型；`ldtoken` 解析为 Assembly-CSharp） | 低 | 只读公开常量；注释说明"避免 const 内联"是有意设计；字段改名会静默返回 null → 版本门锁死（安全侧） | 保持；契约测试覆盖 `PlayerStore.VERSION_NUMBER` 存在性（AdapterTests） |
| R-02 | `src/Game/GameVersionAdapter.cs:38,46` | `field.GetRawConstantValue()` / `field.GetValue(null)` | 不触发（receiver 是 `FieldInfo` 局部，非 computed `Type`） | 低 | 读 MVID/版本的辅助路径 | 保持 |
| R-03 | `src/Game/GameVersionAdapter.cs:59` | `typeof(PlayerStore).Assembly.ManifestModule.ModuleVersionId` | 不触发（`Assembly.get_ManifestModule`/`Module.ModuleVersionId` 不匹配 `reflection-external` 规则分支） | 低 | 读 MVID 做写入门禁；每次构建 MVID 变化 → 门禁自动锁（预期行为） | 保持；如未来 `Assembly.*` 被收紧，改用官方 `ModReflect` 未提供 MVID，需单独确认 |
| R-04 | `src/Game/GameState.cs:16-17` | `typeof(StoreClientInstance).GetField("storeClient", Instance\|NonPublic)` | 不触发（字面量 game 类型） | 中 | 私有字段；只用于取当前顾客实例（非隐藏鉴定数据）；字段改名→顾客相关功能退化但 fail-safe | 保持 + 契约测试；长期可用 `ModReflect.Get` 替代（可审计） |
| R-05 | `src/Game/GameState.cs:119-120`（读值 `:111`、`:226`） | `typeof(StoreClientInstance).GetField("gridInv", …)` + `GetValue` | 不触发 | 中 | 私有字段 `gridInv` 只读；契约测试已覆盖 | 同上 |
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
| `using System.Reflection` 的生产文件 | 4（`GameVersionAdapter.cs`、`RecordingSaveAdapter.cs`、`GameState.cs`、`NewGameMenuAdapter.cs`；`src/obj` 生成文件不计） | `grep -rln "System.Reflection" src --include='*.cs' \| grep -v /obj/` |
| GuardTypes 反射（`ModLoader`/`ModTrust*`/`ModScanner`/`ModCapability`/`ModWorkshop`/`SteamManager`/`PaperBookRegistry`/`ModReflect`） | **0** | `grep -rn "typeof(Mod" src`；`_decomp/ModScanner.cs:2000-2004` 定义 GuardTypes |

### 2.4 C/D 新增文件复核（无新增反射/线程/网络/P-Invoke；`/operation` 端点见 §8/§11.5）

| 新增/修改文件 | 反射 | 线程/协程 | 网络 | P/Invoke | 文件能力 | 结论 |
|---|---|---|---|---|---|---|
| `src/Bridge/TradeService.cs`（C/D） | 0 | 0 | 0 | 0 | 0（只用 Newtonsoft） | 纯桥编排；无 scan 风险新增 |
| `src/Game/GameTradePort.cs`（C） | 0 | 0 | 0 | 0 | 0 | Game 层直连原生成员（见 L-05）；`RuntimeHelpers.GetHashCode` 不在扫描规则内 |
| `src/Core/{OperationStatus,ShopOperationSemantics,QueryValues,TradeGuard}.cs` | 0 | 0 | 0 | 0 | 0 | 纯逻辑；0 `using UnityEngine` / 游戏类型 |
| `src/Bridge/{ActionRouter,BridgeServer,PawnMindMod,GameActions,FirstWeekService,RecordingSaveService}.cs`（D 修改） | 0 新增 | 0 新增 | 0 新增（监听器/Socket 计数不变） | 0 | 0 新增 | 新 `/operation` 只读端点；仍由 `Tick()` 主线程驱动 |
| `src/Game/GameState.cs`（D 追加 `GetCurrentClientHandle`，`:755-772`） | 0 新增（原 2 处字面量反射未变） | 0 | 0 | 0 | 0 | `RuntimeHelpers.GetHashCode` 用作进程内顾客身份，不序列化 |

---

## 3. 后台线程 / 异步 / 协程

| 编号 | 文件:行号 | 调用形态 | 扫描器预测 | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| T-01 | — | `System.Threading.Thread`、`ThreadPool`、`Task.Run/Delay/ContinueWith`、`TaskFactory`、`Timer`、`Parallel`、`async void`、`await` | **0 命中**（C/D 合并后复核仍为 0） | PASS | 曾在 2026-11-09 因 `threads` retiring 清理过（`README.md:277-293`） | 保持；新增代码禁止引入 |
| T-02 | `src/Game/ScreenshotService.cs:62-66`（`StartCoroutine`）、`:85-109`（`IEnumerator CaptureLoop`、`WaitForEndOfFrame`） | 自身 MonoBehaviour 协程 | **自身调用预测不命中**（`ModScanner.cs:672-677` 对 `Ldarg_0` 直接 return；仅"另一个对象的 MonoBehaviour"才命中 `threads`） | 中 | 但 `README.md:285` 的历史扫描记录把 `StartCoroutine` 列为 `threads` 命中项（旧扫描器/旧代码的说法），且该文件于 2026-11-15 才加入（`8f64166`）——**与"已删除协程"的旧结论存在文档漂移**；需要实机扫描确认当前扫描器不再告警 | 发布前改用官方 `ModHelper.StartCoroutine("pawnmind", routine)`（`_decomp/ModHelper.cs:1056`），把协程生命周期交给 Mod 管理器；或在 Workshop 构建中用配置关闭截图特性 |
| T-03 | `src/Bridge/MainThreadPump.cs:6-9` | 注释明确"No threads, tasks or coroutines are created" | PASS | 低 | 与代码一致（`Update()` 驱动） | 保持 |
| T-04 | `src/Game/ScreenshotService.cs:79-83` | `OnDestroy` 置空 ledger、`_running=false`；`PawnMindMod.cs:215-235` 销毁 pump 对象 | PASS | 低 | 协程随对象销毁结束；无后台泄漏路径 | 保持 |
| T-05 | `agent/fake_game.py:47,225-228`、`agent/tests/*` | `ThreadingHTTPServer` + `threading.Thread`（仅测试替身/单测） | 不适用（不进 Mods、不进 dist） | 低 | 外部 Python 进程；与游戏扫描无关 | 不要打包进 Mod 文件夹（见 §5） |
| T-06 | `agent/mcp_server.py:283` | `threading.Lock`（stdio server 内部锁） | 不适用 | 低 | 外部 MCP 进程，不是 Mod | 保持隔离 |

---

## 4. 网络监听

| 编号 | 文件:行号 | 调用形态 | 扫描器预测 | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| N-01 | `src/Bridge/BridgeServer.cs:218` | `new TcpListener(IPAddress.Loopback, _config.Port)`；`:219` `Blocking=false`；`:220` `Start()`（字段声明 `:146`） | 触发 `network`（`System.Net.*`）→ **manifest 已声明** | 低（PASS-预测） | 只绑定回环；无 `IPAddress.Any`/`0.0.0.0`（grep 0）；端口默认 15528，可在 `bridge.conf` 调整；启用时玩家会看到 network 能力提示 | 保持；在 manifest/README/Workshop 描述中明确"仅 127.0.0.1" |
| N-02 | `src/Bridge/BridgeServer.cs:115,235,352,355,389,402,407,420,434,436,438,692,694,708,710,712,949` 等（`grep -c "Socket"` = 23 行） | 非阻塞 `Socket.Poll/AcceptSocket/Receive/Send/Close` | 同上（`network`） | 低 | 单线程 Tick 驱动（见 §8）；无 TLS；Bearer token + loopback 是边界 | 保持；不要在公网/0.0.0.0 暴露 |
| N-03 | — | `HttpListener`、`TcpListener` 之外：`HttpWebRequest`、`WebClient`、`HttpClient`、`UnityWebRequest`、`UdpClient`、`NetworkStream` | **0 命中** | PASS | 无外联、无下载代码 | 保持 |
| N-04 | `agent/pawnmind_client.py:12,76-88` | `urllib.request` + 关闭代理（`ProxyHandler({})`）访问 `http://127.0.0.1:<port>` | 不适用（外部进程） | 低 | 只读/动作客户端；token 走 header；/health 免 token；有重定向防护类 | 保持；绝不把 token 打到日志 |
| N-05 | `tools/probe.py:16,46-47` | 同上的手工探测脚本 | 不适用 | 低 | 运维工具，不进 dist | 保持隔离 |
| N-06 | `agent/fake_game.py:225` | `ThreadingHTTPServer(("127.0.0.1", 0), …)`（测试替身，随机端口） | 不适用 | 低 | 仅用于离线测试/演示；**不是游戏** | 严禁拷入游戏目录 |
| N-07 | `src/Bridge/BridgeServer.cs:566-613`（`case RouteKind.Operation`）；`src/Core/RouteTable.cs:37`；`src/Core/EndpointPolicy.cs:15,36-41` | `GET /operation?id=` 只读查询（复用同一 loopback 监听器；Bearer 必需，401；`EndpointPolicy` 映射到 `get_operation`；bootstrap 可查、Locked 拒绝；不建快照、不派发） | 不新增监听器/能力（同一 `TcpListener`，仍为 `network`） | 低 | 源码证据：`BridgeServer.cs:566-613`（auth/mode gate/`QueryValues.Get`/`_operationProvider` 只读）；`EndpointPolicy.cs:15,36-41`；`RouteTable.cs:37`；测试 `test/PawnMind.Tests/OperationEndpointTests.cs:14-217`（L2 无游戏） | 保持只读语义；不要让该端点演变成写/派发入口 |

---

## 5. 打包 Python / 脚本 / 外部进程

| 编号 | 文件:行号 | 调用形态 | 风险 | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| P-01 | `dist/PawnMind/` 实际内容 | `manifest.xml`（1,138 B，SHA256 `fc14f3b7…`）+ `PawnMind.dll`（296,960 B，SHA256 `ad335987…`） | 无（当前产物干净） | 低（PASS） | `find dist -type f` 只有 2 个文件；无 `.py`、无脚本、无第三方 DLL；**本任务未改写 dist** | 发布打包只从 `dist/` 取 |
| P-02 | `build.sh:103-106` | `rm -rf dist` → `mkdir dist/PawnMind` → 只 `cp manifest.xml` + `cp src/bin/Release/netstandard2.1/PawnMind.dll` | 无 | 低 | `assert_safe_dist`（`:55-101`）防符号链接/误删；`PAWNMIND_BUILD_ONLY=1`（`:46-50`）只编译不打包（本任务实测：在打包前退出，dist 未被改写） | 保持；任何"顺手把 agent/ 复制进 dist"的改动都必须拒绝 |
| P-03 | `.gitignore:4`（`**/dist/`） | dist 不入 git | 中（可复现性） | 中 | dist 是本地构建产物；无法用 git 校验"dist 是否对应 HEAD"（见 §9.3 D-05） | 构建后记录 SHA256 + MVID 到审计文档；发布脚本改为可复现 |
| P-04 | `PawnMind/agent/**`（含 `pyproject.toml`）、`tools/**`、`.venv/`、`_decomp/`、`test/` | 外部进程/开发物 | **误打包=高**：若用户把整个仓库拷进 `Mods/PawnMind`，扫描器文件夹规则会命中 `payload-file`（`.venv` 里的 `.exe/.dll`）→ 启用被拒；另有 unlisted DLL 拒绝链（`ModLoader.cs:666-671`） | 高（仅误操作场景） | 当前 dist 不受影响；对外分发/安装说明必须强调"只复制 dist/PawnMind 一个文件夹" | 安装草稿 §2/§5 明确写死；发布前做一次"干净机器解压→扫描"演练 |
| P-05 | `agent/mcp_server.py`（stdio MCP，**116 工具**；`--tools recording` **106** 个；与 `docs/audit/tools-inventory.json` full=116/recording=106、`AGENTS.md` 一致） | 独立 Python 进程，经 `stdio` 与宿主通信；`threading.Lock` 仅同步 | 低 | 外部安装器与游戏 Mod **物理分离**（见草稿 §3）；不写入游戏目录 | 保持；不要在 Mod 内拉起 Python 子进程（当前没有 `Process.Start`） |
| P-06 | `agent/orchestrator.py`、`agent/demo_agent.py`、`tools/*.py/.sh` | 开发/演示工具 | 低 | 不随 Mod 分发；`tools/setup_remote_host.sh` 是主机侧脚本 | 保持；发布包白名单只允许 `manifest.xml` + `<Assemblies>` 里的 DLL |
| P-07 | 编译/打包链路 | 本机编译 gate 可跑；打包/安装仍需 Windows/游戏环境/用户在场 | 低（编译） / NOT_RUN（打包·安装） | `PAWNMIND_BUILD_ONLY=1 ./build.sh` 已验证通过（§10.1 `NR-06`）；完整 `./build.sh`（重写 dist）与安装未执行（§10.2 `NR-11`） | 发布前由用户在构建机执行完整构建 + 安装 + `/health` 校验 |

---

## 6. 原生 DLL / P-Invoke / DllImport

| 编号 | 文件:行号 | 调用形态 | 扫描器预测 | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| N-01 | — | `DllImport` / `extern` / `Marshal.*` / `unsafe` / `calli` / `NativeLibrary` | **0 命中**（C/D 合并后复核仍为 0） | PASS | `grep -rn "DllImport\|extern \|Marshal\.\|unsafe" src` = 0；`System.Runtime.InteropServices` 也未导入 | 保持禁止 |
| N-02 | `dist/PawnMind/PawnMind.dll` 与 `src/bin/Release/netstandard2.1/PawnMind.dll` | PE32 Mono/.NET assembly | 无法从源码证明"无原生依赖"，需扫描器/引用清单校验 | 低 | 编译 gate 已 PASS（§10.1），但**编译成功不等于无原生依赖**；二进制引用清单/IL 检查仍 `NOT_RUN` | `NOT_RUN`：`NR-10`（需 `ilspycmd`/`mod-scan` 查看引用） |
| N-03 | `<game-managed>/*.dll` | 游戏 Managed 参考副本存在（只读） | 不适用 | 低 | 仅用于编译/契约测试，不会进 dist（`<Private>false</Private>`，`src/PawnMind.csproj:24-61`） | 保持 `Private=false` |

---

## 7. Unity 调用是否在 Core / 分层

| 编号 | 文件:行号 | 形态 | 结果 | 等级 | 说明 | 替代建议 |
|---|---|---|---|---|---|---|
| L-01 | `src/Core/**`（含 B 的 `ClientSnapshotProjection.cs`、`SnapshotFieldMap.cs`、D 的 `OperationStatus.cs`、`ShopOperationSemantics.cs`、`QueryValues.cs`、`TradeGuard.cs`） | `using UnityEngine` / Unity 类型 / 游戏类型 | **0**（注释/字符串里出现 `PlayerStore`/`GameState.Get*` 只是文档性描述，不是代码依赖；C/D 新 Core 文件复核为 0） | PASS | 满足 AGENTS "Core 纯逻辑" | 保持；CI 可加静态检查 `grep -rn "using UnityEngine\|PlayerStore\|GameItem" src/Core` 必须为空 |
| L-02 | `src/Bridge/**`：`AnalyzerService.cs:6`(TMPro)、`BookService.cs:4-5`、`Diagnostics.cs:3`、`GameActions.cs:5`、`MainThreadPump.cs:2`、`UiSnapshot.cs:4-6` | Bridge 层直接 `using UnityEngine` / `TMPro` / `UnityEngine.UI` | 与"Game 是唯一直连游戏类型层"的分层目标存在偏差 | 中 | 当前全部从 Unity 主线程 Tick 进入（§8），无运行时越界；但类型系统不阻止将来从非主线程调用 | 把 `BookService`/`GameActions`/`UiSnapshot`/`AnalyzerService` 的游戏/Unity 访问下沉到 `src/Game/` 适配器；或至少加"仅主线程断言" |
| L-03 | `src/Bridge/BookService.cs:20-27,56,97-101` | 直接使用游戏类型 `GameInventory`/`GameItem`/`PaperBookRegistry`/`StoreBookData` | 编译期分层偏差 | 中 | `PaperBookRegistry` 在扫描器 GuardTypes 里（`ModScanner.cs:2000-2004`）——仅当**反射**它才硬旗；本处是直连调用，预测不触发，但属于脆弱的游戏内部契约 | 下沉到 Game 适配层 + 契约测试；禁止对 GuardTypes 做反射 |
| L-04 | `src/Game/**`（`ScreenshotService`、`NewGameMenuAdapter`、`WindowAdapter`、`RecordingSaveAdapter`、`PromptAdapter`、`GameTradePort`） | `using UnityEngine`/`UI`/`ScreenCapture` + 直连游戏类型 | 允许层 | PASS | Game 层是唯一允许反射/直连游戏类型的层 | 保持 |
| L-05 | `src/Game/GameTradePort.cs:16-49,80-154` | 直读原生成员：`StoreClient.clientIntent/isIntroduced/barterOffer/serviceCost/gunOrderData/isBusinessComplete`、`PlayerStore.CanPay/CanBuyItemFromTable`、`NegociationUIManager.IsTradeLocked/IsNegociatingItemOwned`；`:51-78` 单次原生 `OnOfferAcceptClick`（经 `GameState.AcceptNegotiationOffer`，`GameState.cs:744-753`） | 允许（Game 层），但属**原生契约脆弱点** | 中 | 只读守卫 + 唯一原生点击；失败 fail-closed；适配器契约已扩到 503 checks（构建 gate PASS）且覆盖 B10-B16 成员（`test/PawnMind.AdapterTests/Program.cs`）；**未读隐藏字段**（无 `AppraisalHelper.GetCorrectBaseValue`/隐藏身份/水质）；无扫描器能力新增 | 保持契约测试先行；游戏更新后若字段改名，契约测试应第一时间失败 |
| L-06 | `src/Game/GameTradePort.cs:3` | `using PawnMind.Bridge;`（`ITradePort`/`TradeGuardInput`/`TradeEvidence` 定义在 Bridge，`TradeService.cs:9-42`） | Game→Bridge 反向依赖（单程序集内可编译） | 低 | 不影响扫描器/运行时；若将来拆分程序集会成为循环分层 | 把 port 接口/数据契约下沉到 `src/Core/` 或独立的 Game 接口层 |

---

## 8. 主线程约束（MainThreadPump）是否有绕过

| 编号 | 文件:行号 | 形态 | 结论 | 等级 | 说明 |
|---|---|---|---|---|---|
| MT-01 | `src/Bridge/MainThreadPump.cs:20-22` | `Update()` → `_server.Tick()` | 唯一驱动入口 | PASS | 注释 `:6-9` 明确无线程/任务/协程；F7/Shift+F7 也在此轮询（`:24-34`） |
| MT-02 | `src/PawnMindMod.cs:133-139`（pump GameObject + ScreenshotService）、`:148-162`（ActionRouter + `/operation` verifier 注释）、`:178-200`（BridgeServer + MainThreadPump + Start） | OnEnable 建 pump → `AddComponent<MainThreadPump>()` → `_server.Start()`；`StateSnapshot.BuildJson` / `_router.Handle` / `VerifyOperation` 作为委托传入 BridgeServer | 委托只在 `Tick()` 内被调用 → 主线程 | PASS | `:153-155` 注释明确"Handle() runs inside BridgeServer.Tick from MainThreadPump.Update"；`:160-162` 表明 `/operation` 回读只读 |
| MT-03 | `src/Bridge/BridgeServer.cs:255-329` | `AcceptPending` → `TryRead` → `HandleRaw`（状态/动作/健康/`/operation`）→ `Flush`；`/events` 长轮询在 `TickEvents` 内 | 全部在 Tick 内同步执行，无 blocking sleep | PASS | 非阻塞 socket + 每 tick 预算；`grep Thread.Sleep/Sleep(` = 0 |
| MT-04 | 全仓生产代码 | `Thread`/`Task`/`async`/`await`/后台回调 | **0 命中**（协程见 T-02，仍在主线程） | PASS | 静态未发现"HTTP 线程直接触游戏对象"的路径；C/D 合并后复核仍为 0 |
| MT-05 | 结构性风险 | 无编译期/运行期"仅主线程"断言 | 将来回归风险 | 中 | Bridge/Game 服务可被任意线程 new 出来调用（类型系统不阻止）；建议在 `GameState`/`GameActions` 等入口加 `MainThreadPump` 线程断言（仅 Debug 构建）或做单测审查 |
| MT-06 | `src/PawnMindMod.cs:215-241` | `OnDisable` → `_server.Stop()` → 销毁 pump | 主线程 | PASS | `Stop()` 关闭 socket；无后台线程/任务需要 join |
| MT-07 | `src/Bridge/BridgeServer.cs:566-613`（`/operation` 处理） | Bearer 校验 → `EndpointPolicy.Refusal` → `QueryValues.Get` → `_operationProvider`（`ActionRouter` 只读回读验证器 `ActionRouter.cs:96-99,409-413`） | 在 `Tick()` 内执行 → 主线程；只读、不建快照、不派发 | PASS | `test/PawnMind.Tests/OperationEndpointTests.cs:14-217` 覆盖 auth 401/mode 403/unknown/pending 等（L2 无游戏）；**未新增线程或监听器** |

---

## 9. Manifest / 版本 / dist 一致性

### 9.1 Manifest 静态检查

| 编号 | 检查项 | 结果 | 等级 | 说明/证据 |
|---|---|---|---|---|
| M-01 | `<ID>` = `pawnmind` | 合法（字母/数字，非 `.` 开头，≤64） | PASS | `ModLoader.cs:1123-1132` 规则 |
| M-02 | `<Assemblies>` 非空、相对路径、`.dll` | `PawnMind.dll` ✓ | PASS | `ModLoader.cs:1133-1169` |
| M-03 | `<Capabilities>` 元素存在、能力名已知 | `network`、`reflection-external` ✓（均属 9 种已知） | PASS | `ModLoader.cs:1170-1187`；manifest 内容在 C/D 合并后未变 |
| M-04 | **能力声明完整性** | 预测检测集合 = `{network, reflection-external, files}`；`files` **未声明** → 启用链会在 `ModLoader.cs:739-743` 拒绝 | **高（预测）** | 见 §11 F-01；`files` 检测点：`ScreenshotService.cs:157,197,201,207`、`RecordingSaveAdapter.cs:106,110,151` |
| M-05 | 降级/提示语义 | 声明 `reflection-external`（retiring）→ 启用时出现"下个版本将拒绝"的 DEPRECATED 提示；`network` 会显示能力确认 | 中 | `ModCapability.cs:37,44-51`；`ModSecurityDebug.cs:54-58`；`ModLoader.cs:745-746` |
| M-06 | Workshop 专属限制 | 2026-12-10 起 Workshop Mod 声明 `files`/`network` 需开发者验证（本构建开关仍为 false，但文案已在游戏内） | 高（发布计划） | `ModSecurityDebug.cs:67-71`；`ModTrustList.cs:117-119`；`ModLoader.cs:694-699` |
| M-07 | `<GameVersions>` = `0.46D` | 与 README/已知限制一致（0.46D）；内部构建号文档写 `100-Rev6`/`100-Rev13`，两者不是同一字段，未核实 | 低 | `ModUIManager.cs:873,954` 仅在不匹配时给 issue 行，不是拒绝 |
| M-08 | 未使用 `<Prerequisites>` | 无 | PASS | 无需前置 Mod；`ModReflect`/`Prerequisites` 规则不涉及 |

### 9.2 版本号 / 发布元数据一致性

| 来源 | 值 | 证据 |
|---|---|---|
| `manifest.xml:6`（src 与 dist 相同 SHA256） | `ModVersion = 0.3.0` | `sha256sum`：`fc14f3b7e65313c1cd9afef17480b1a4733422c3355cccf2ba2f439314ce124c`（src 与 dist 相同） |
| git tag（PawnMind 最新） | `v1.0.1`（`6b01756`）；最终复核时 HEAD = `f789a92`，`git describe` = `v1.0.1-45-gf789a92` | `git tag --list`、`git log -1 v1.0.1`、`git describe --tags --always` |
| README / AGENTS | 复核时已由其他模块更新为 `manifest 0.3.0`（旧版写 `0.2.1`，属于历史文档漂移） | `README.md`、`AGENTS.md`（工作树同时被其他模块修改） |
| `/health` 的 `version` / `mod_version` | 取自 manifest（`PawnMindMod.cs:98`；`BridgeServer.cs:748-760` 的 `HealthJson`） | 因此实际报告 `0.3.0` |
| 其他 tag | `v1.9.4` 属于 **CompactSort**（`git log -1 v1.9.4` = `feat(compact-sort): 1.9.4 …`），不要误当 PawnMind | — |
| **MCP 工具数** | full **116** / recording **106** | `docs/audit/tools-inventory.json`（`full:116`、`recording:106`）；`STRICT_ALLOWLIST_DECISIONS.md:4`（真实 tools/list 116）；C/D 提交说明 114→116 / 104→106 |
| **策略 policy sha256** | `e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc` | `src/Core/PolicyCatalog.g.cs:11`；`.venv/bin/python tools/gen_policy_catalog.py --check` → `ok: … matches tool_policy.json (sha256 e0da5a24…)`，退出码 0 |
| **本批次构建产物** | `src/bin/Release/netstandard2.1/PawnMind.dll`：SHA256 `d29209a4…`，338,432 B（构建 gate 于 20:41Z 确认可编译；文件 mtime 20:35 表示增量构建已 up-to-date） | `sha256sum src/bin/Release/netstandard2.1/PawnMind.dll`；§10.1 `NR-06` |

> 结论：**manifest 版本（`0.3.0`）仍未与 git tag（`v1.0.1`）统一**，发布前必须统一；工具数（116/106）与 policy sha（`e0da…`）本批次已在本机核对一致。等级：中（版本卫生）。

### 9.3 dist 产物身份与新鲜度

| 编号 | 检查项 | 结果 | 等级 | 说明 |
|---|---|---|---|---|
| D-01 | dist 文件清单 | 只有 `manifest.xml` + `PawnMind.dll` | PASS | `find dist -type f -printf '%p %s\n'`；本任务**未改写 dist** |
| D-02 | dist DLL SHA256 / 大小 | `ad3359875348ff672ff15f520ac9dd518f38221e1eb3ae85a287ea13930185f7` / 296,960 B | 记录 | `sha256sum`；与历史文档 `docs/audit/BASELINE_CURRENT.md:30` 一致（`ad335987…`） |
| D-03 | dist manifest SHA256 | `fc14f3b7e65313c1cd9afef17480b1a4733422c3355cccf2ba2f439314ce124c`，与 `src` manifest 相同 | PASS | `sha256sum manifest.xml dist/PawnMind/manifest.xml` |
| D-04 | **dist 是否对应当前源码** | **不对应（STALE）**：dist DLL mtime `2026-11-16 17:58:22Z`；最终复核时 **26 个源码 `.cs` 文件比它新**（排除 `obj/bin`；列表见 §14 命令）。本机编译 gate 产出的新 DLL（`d29209a4…`，338,432 B）与 dist（`ad335987…`，296,960 B）**哈希不同** → dist 确定不是当前代码构建 | **高** | `find src … -newer dist/PawnMind/PawnMind.dll`；`sha256sum src/bin/Release/netstandard2.1/PawnMind.dll dist/PawnMind/PawnMind.dll` |
| D-05 | dist 是否可被 git 校验 | 不可：`.gitignore:4` `**/dist/` | 中 | `git check-ignore -v PawnMind/dist/PawnMind/PawnMind.dll` → `.gitignore:4:**/dist/` |
| D-06 | build 产物 / 编译门禁 | **编译 gate 实测 PASS**（§10.1 `NR-06`）；产物 `d29209a4…`/338,432 B；DLL MVID/程序集版本仍未读取 | PASS（编译）/ NOT_RUN（MVID） | `PAWNMIND_BUILD_ONLY=1 ./build.sh` 退出码 0；MVID 读取见 §10.2 `NR-10` |
| D-07 | 与已安装 DLL 比较 | 已安装目录本机不可访问（无 `/mnt`、无 Probably Stolen 安装；`<game-managed>` 只是 Managed 参考目录，不是 `Mods/PawnMind`） | **NOT_RUN** | §10.2 `NR-04`；历史记录：已安装 `e9e1bd07…`、295,424 B（`docs/audit/BASELINE_CURRENT.md:31`、`docs/plans/PLAYER_PARITY_IMPLEMENTATION-v2.md:29`）→ 与 dist `ad335987…`、296,960 B **不一致** |
| D-08 | 是否"只记录不覆盖" | **是**：本批次未复制、未安装、未覆盖任何游戏目录；唯一构建动作是 `PAWNMIND_BUILD_ONLY=1`（打包前退出） | PASS | 日志 `/tmp/pawnmind_build_gate_full.log` 末行：`PAWNMIND_BUILD_ONLY=1: build gate complete; skipping dist packaging.` |

> **验收含义**：`dist` ≠ 当前源码，且 `dist` ≠ 已安装 DLL（历史记录）。编译 gate PASS 只说明"能编译"，**不说明 dist/安装已更新**。因此**任何当前游戏内现象都不能归因于仓库 HEAD 代码**。等级：**高（验收归因）**。

---

## 10. 官方 ModScanner / APIChanges 与编译 gate 的运行状态

> 纪律：没有本地运行结果，就**不写"0 硬旗""兼容"结论**。编译 gate 本机可跑，已实测；扫描器/安装/游戏相关仍 `NOT_RUN`。

### 10.1 本机实测 PASS：编译 gate（原 `NR-06`，已改正）

| 项 | 内容 |
|---|---|
| 结论 | **PASS（实测）**。上一版"Linux 无 Windows dotnet 目标 → NOT_RUN"的表述**错误**，本版已改正 |
| 时间 / HEAD | `2026-11-16T20:40–20:41Z`（UTC）@ `f789a92`（`v1.0.1-45-gf789a92`） |
| 命令（精确） | `cd <repo>/PawnMind && set -a; . ./.env.server; set +a; PAWNMIND_BUILD_ONLY=1 ./build.sh` |
| 环境 | `<dotnet>/dotnet`（.NET SDK 9.0.321）；`GAME_MANAGED=<game-managed>`；`PAWNMIND_NEWTONSOFT_DLL=<game-managed>/Newtonsoft.Json.dll`（来自 host-local `.env.server`，已 gitignore） |
| 退出码 | **0**（`EXIT=0`） |
| 测试摘要 | C# 纯逻辑 `passed=749 failed=0`（审阅修复后 750/0）；脚本门禁 `passed=53 failed=0`；适配器契约 `contract checks: 503` / `adapter contract OK`；日志内 `PASS` 行 1305、`FAIL` 0、`SKIP` 0 |
| 编译摘要 | `Build succeeded. 0 Warning(s) 0 Error(s)`；`PawnMind -> src/bin/Release/netstandard2.1/PawnMind.dll` |
| 产物 | `src/bin/Release/netstandard2.1/PawnMind.dll`：SHA256 `d29209a44d2ede7e2e41010ff89abfa4409493155f12d357effe70b544c87eec`，338,432 B |
| dist / 安装 | **未改写 dist**（`PAWNMIND_BUILD_ONLY=1` 在打包前退出；dist 哈希保持不变）；未安装、未覆盖游戏目录 |
| 日志 | `/tmp/pawnmind_build_gate_full.log`（1,342 行，本机临时文件，不入库） |
| 边界说明 | 该命令内部会先跑 `./test.sh`；由 lead 明确授权。只编译不打包；**不能**据此宣称"dist 已更新"或"Scanner/游戏验收通过" |

### 10.2 `NOT_RUN` 与获取方式

| NR | 项 | 本机状态 | 精确命令/获取方式 | 期望记录 |
|---|---|---|---|---|
| `NR-01` | 官方扫描命令 `mod-scan`（游戏内） | `NOT_RUN`（无游戏进程） | 启动 0.46D → 加载 PawnMind → 打开开发者控制台（F8）→ 执行：`mod-scan pawnmind`；未加载时：`mod-scan "D:\Program Files\steam\steamapps\common\Probably Stolen\Mods\PawnMind"` | 完整输出：`declared` / `detected` / `UNDECLARED (enable would be refused)` / `DEPRECATED` / `VERIFICATION` / `HARD FLAGS` / `findings` / `fingerprint` |
| `NR-02` | 离线 ScannerCheck harness | `NOT_RUN`（需游戏程序集；构建会写 `CompactSort/test/ScannerCheck` 的 `bin/obj`，本任务未授权） | `cd CompactSort/test/ScannerCheck && dotnet run -c Release -- "D:\Program Files\steam\steamapps\common\Probably Stolen\Mods\PawnMind"`（`Program.cs:8-10` 硬编码 Managed 路径；需要游戏 0.46D 默认安装位置） | `error=`、`refused=`、`caps=`、`methods=`、`hard=`、`findings=`；注意它**不打印** declared/undeclared/verification |
| `NR-03` | `APIChanges.md` / `ModdingGuide.md` | 仓库内**不存在**（`find` 0 命中）；内容未在本地核实 | `cd <repo> && find . -path ./.git -prune -o \( -iname 'APIChanges*' -o -iname 'ModdingGuide*' \) -print`；仓库内文档引用的获取地址（**仅获取方式，未核实内容，禁止当作本地证据**）：`https://github.com/dragonpan2/probably-stolen-modding/blob/main/APIChanges.md`、`.../ModdingGuide.md`（见 `docs/PAWNMIND_AGENT_BRIEF_NEXT_PHASE_2026-11-17.md:329-332`） | 下载后记录 commit/hash 与读取日期；与 `_decomp/ModScanner.cs` 规则对比 |
| `NR-04` | dist vs 已安装 DLL / manifest | 安装目录不可访问 | Windows/WSL 构建机：`sha256sum "/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Mods/PawnMind/PawnMind.dll"` + `stat -c '%s %y' …`；`sha256sum …/manifest.xml`；`cat …/manifest.xml` | 与 dist `ad335987…`/296,960 B **及**新构建产物 `d29209a4…`/338,432 B 对比；历史安装 `e9e1bd07…`/295,424 B；**只记录，不覆盖** |
| `NR-05` | 运行中版本/MVID/`/health` | `NOT_RUN`（无游戏） | 游戏运行后：`curl -s http://127.0.0.1:15528/health`（`/health` 免 token）；记录 `ok`、`version`、`game_version`、`game_assembly_mvid`、`expected_*`、`write_gate`、`latest`；或游戏内 `mod-trust` | 与 `bridge.conf` 的 `expected_*` 对比；`latest=UNVERIFIED` 是预期 |
| `NR-07` | Workshop 上传/验证/预览素材 | `NOT_RUN`（未发布，按边界不执行） | 由 Steam 客户端/游戏内工坊 UI 完成；上传前需 `mod-scan` 0 硬旗 +（若声明 files/network）开发者验证；本仓库未找到离线上传工具 | 记录工坊 item id、版本、验证状态；**本批次不上架** |
| `NR-08` | 真实启用/拒绝/能力提示行为 | `NOT_RUN`（无游戏） | 见 `NR-01` 前：把 dist 装到**只读验证环境**后启用，记录 `ModEnableCheck` 的 refusal/prompt 文案 | 预测：`UNDECLARED files` 拒绝（见 §11.1）；`reflection-external` DEPRECATED 提示 |
| `NR-09` | 真实存档/ModData 路径与方法 | `NOT_RUN`（本机无 Windows 用户环境） | 游戏运行后查看 `%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\pawnmind\bridge.conf`（只读）；存档目录只做定位，**不读写** | 路径仅用于安装草稿的"禁止触碰"清单 |
| `NR-10` | dist/构建 DLL 的引用/IL 级检查（unlisted-reference/native 的二进制证据） | `NOT_RUN`（无 `ilspycmd`，且本任务不允许运行额外工具/写构建产物） | 构建机：`ilspycmd -l c dist/PawnMind/PawnMind.dll`（或 `mod-scan`）；检查 AssemblyReferences 是否只有游戏程序集（Assembly-CSharp/UnityEngine.*/Unity.TextMeshPro/Newtonsoft.Json）与 P/Invoke/unsafe；也可执行 MVID 读取命令（`dotnet` 自写 harness 需新增文件，未授权） | 若出现非游戏引用 → `unlisted-reference` 硬旗风险 |
| `NR-11` | **dist 打包 + 安装/覆盖验证** | `NOT_RUN`（需 Windows/游戏安装环境、用户在场；本任务禁止改写 dist、禁止安装） | 构建机：`cd PawnMind && ./build.sh`（完整流程，会重写 `dist/PawnMind/`）→ 记录新 dist SHA256 → 退出游戏 → 备份旧 `Mods/PawnMind` → 按 `docs/plans/WORKSHOP_INSTALL_DRAFT.md` 安装 → `curl -s http://127.0.0.1:15528/health` | 记录 dist/安装 SHA256、MVID、`/health` 值；**只记录，不覆盖本机安装** |

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
| F-09 | `src/PawnMindMod.cs:989,1009`；`src/Bridge/Diagnostics.cs:46`；`src/Game/ScreenshotService.cs:135` | `ModHelper.ReadModDataFileText / WriteModDataFileText / WriteModDataFileBytes` | 不触发（游戏许可 API） | PASS | 受路径穿越防护（`RequireInside`） | 保持 |
| F-10 | `src/Bridge/BridgeServer.cs:3,115,389` | `System.IO.MemoryStream` | 不触发（不属于 FileTypes） | PASS | 仅内存缓冲 | 保持 |

> **F 组结论**：当前 manifest **缺 `files`**。按本地启用链（`ModLoader.cs:739-743`），哪怕其余都干净，启用也会以"uses capabilities its manifest does not declare"被拒。C/D 合并**未新增**任何 `files` 命中点。修复方式不是"猜"，而是先跑 `NR-01` 确认命中集合，再决定"声明 files + 申请验证"还是"改造/关闭该特性"。

### 11.2 `reflection-external`（未来阻塞）

预测命中 14 处 computed-`Type` 反射：`RecordingSaveAdapter.cs:402,410`；`NewGameMenuAdapter.cs:401,403,405,407,1280,1288,1298,1306,1321,1331,1346,1354`。manifest 已声明该能力（当前允许，但 retiring），**2026-12-10 更新起拒绝**。C/D 合并未新增反射点。整改路径见 §2.1 方案 A/B/C。等级：高（未来发布阻塞）。

### 11.3 `network`（已声明、形态合规）

`BridgeServer.cs:218` 的回环 `TcpListener` + `Socket`（§4）预测为 `network`；manifest 已声明；无公网绑定/外联。C/D 新增的 `GET /operation`（`BridgeServer.cs:566-613`）复用同一监听器，**不新增能力**；Bearer 必需、只读。等级：低。注意：能力提示会被玩家看到（"Access the internet"），安装草稿必须解释"仅 127.0.0.1"。

### 11.4 其他零命中

`process` / `dynamic-code` / `native` / `steam` / `steam-auth` / `threads` 预测 **0 命中**（依据：§3/§6 的 0 结果 + §1.2 规则；C/D 合并后复核仍为 0）。仍以 `NR-01` 为准。

### 11.5 C/D 合并新增面复核（结论：无新增高风险）

| 新面 | 复核结果 |
|---|---|
| `src/Game/GameTradePort.cs` 直读原生字段（`StoreClient.*`、`PlayerStore.CanPay/CanBuyItemFromTable`、`NegociationUIManager.*`） | 允许（Game 层）；非隐藏字段；只读守卫 + 唯一原生点击；契约测试 503 checks 全过（编译 gate）。等级：中（原生契约脆弱，非扫描器风险） |
| `GET /operation?id=` 新端点 | 只读、不建快照、不派发；Bearer 必需（401）、模式门禁（403）、未知 id 返回 `unknown_operation`；同一 `Tick()` 主线程；无新监听器/线程/能力。等级：低 |
| `ActionGate` 双轴 / `Operation*` / `TradeGuard` 纯逻辑 | Core 纯逻辑；0 Unity/游戏类型；已有 `Operation*Tests`/`TradeServiceTests` 覆盖（L0/L2）。等级：低 |
| `GameTradePort` → `using PawnMind.Bridge` | 单程序集内可编译的反向依赖；不影响扫描器/运行时。等级：低（未来拆程序集时需处理，见 L-06） |

---

## 12. 统计汇总

> 口径见 §14 命令；快照 = §0.1（HEAD `f789a92`）。其他模块仍在更新文档，但生产代码计数以该快照为准。

| 指标 | 数量 | 备注 |
|---|---|---|
| **反射 API 调用点** | **26 行**：成员查找 19（`GetField` 11 + `GetMethod` 7 + `GetProperty` 1）+ `MethodInfo.Invoke` 7 | 另有 `UnityEvent.Invoke` 2 行**不计**反射；`using System.Reflection` 生产文件 4 个 |
| `typeof(...)` | **68 行** / 4 文件（NewGameMenuAdapter 61、RecordingSaveAdapter 3、GameVersionAdapter 2、GameState 2） | 字面量 game 类型预测不触发 |
| 预测反射 `reflection-external` 命中 | **14 处**（computed Type） | manifest 已声明，但 retiring |
| **后台线程** | **0**（Thread/ThreadPool/Task/Timer/Parallel/async/await 全 0） | C/D 合并后复核仍 0 |
| **协程** | **1 处**（2 行：`StartCoroutine` + `IEnumerator`） | `ScreenshotService`；自身 MonoBehaviour 预测不命中 `threads` |
| **网络监听器** | **1**（`TcpListener`，`127.0.0.1`，`BridgeServer.cs:218`）+ `Socket` API **23 行** | `HttpListener`/WebClient/HttpClient/UnityWebRequest = 0；`/operation` 复用同一监听器 |
| **P/Invoke / DllImport / unsafe** | **0** | C/D 合并后复核仍 0 |
| 预测 `files` 命中 | **8 处**（F-01~F-07，其中 F-08 不触发） | manifest 未声明 → 预测拒绝启用；C/D 未新增 |
| **Python/脚本进 dist** | **0**（dist 只有 2 个文件） | dist SHA256 已记录 |
| **Core 中的 UnityEngine 调用** | **0** | Core 纯逻辑保持（含 C/D 新增 Core 文件） |
| **主线程约束绕过** | **0（静态）** | 含新 `/operation`（MT-07）；无后台线程 |
| dist 文件数 | 2（DLL 296,960 B + manifest 1,138 B） | `ad335987…` / `fc14f3b7…`；本任务未改写 |
| 比 dist 新的源码文件 | **26**（快照时，排除 `obj/bin`） | dist STALE |
| 编译 gate 结果 | **PASS**：C# 749/0（审阅修复后 750/0）、门禁 53/0、契约 503 OK、0 warn/0 err、EXIT=0 | `PAWNMIND_BUILD_ONLY=1 ./build.sh` @ 20:41Z |
| 构建产物 vs dist | `d29209a4…`（338,432 B）≠ `ad335987…`（296,960 B） | 构建 gate 产出；dist 未更新 |
| Manifest 版本 vs tag | `0.3.0` vs `v1.0.1` | 发布前需统一 |
| MCP 工具数 | full **116** / recording **106** | `tools-inventory.json`、`AGENTS.md`、C/D 提交说明一致 |
| 策略 policy sha256 | `e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc` | `gen_policy_catalog.py --check` PASS |

---

## 13. Top 风险与发布建议（不修改源码，仅建议）

### Top 3 最大风险（C/D 合并复核后**排序未变化**）

1. **【高·发布阻塞-预测】manifest 未声明 `files` capability → 启用被拒**
   - 证据：检测点 `ScreenshotService.cs:157,197,201,207`、`RecordingSaveAdapter.cs:106,110,151`；拒绝链 `_decomp/ModLoader.cs:739-743`；规则 `_decomp/ModScanner.cs:50-56,681-700`。C/D 合并**未新增** `files` 命中。
   - 复现：`NR-01`（游戏内 `mod-scan pawnmind`）→ 看 `UNDECLARED (enable would be refused)`。
   - 决策树（需实机确认后选）：①声明 `files` → 接受"2026-12-10 起 Workshop 需开发者验证"（`ModSecurityDebug.cs:67-71`）并准备验证材料；②改造：截图用量统计改走 `ModHelper`/去掉；存档索引读出无官方公开替代（HYPOTHESIS），若无法消除则必须走①。
2. **【高·未来阻塞】`reflection-external` 2026-12-10 起被拒，当前 14 处 computed-`Type` 反射 + manifest 仍声明**
   - 证据：`_decomp/ModSecurityDebug.cs:54-58`（游戏内文案）、`_decomp/ModCapability.cs:37,44-60`；命中点见 §11.2。
   - 建议：2026-12-10 前按 §2.1 方案 B（`ModReflect`）/A（字面量下放），然后把 `<Capability>reflection-external</Capability>` 从 manifest 移除；**在完成前不要把工坊发布为"长期可用"**。
3. **【高·验收归因】dist STALE + dist ≠ 已安装 + 版本三处不一致（但编译 gate 已实测 PASS）**
   - 证据：dist DLL mtime 17:58:22Z 早于 **26 个**源码 `.cs`（§9.3 D-04）；编译 gate 产物 `d29209a4…`/338,432 B / dist `ad335987…`/296,960 B / 历史安装 `e9e1bd07…`/295,424 B 三者不同；manifest `0.3.0` vs tag `v1.0.1`。
   - **变化点**：上一版"编译不可跑 → NOT_RUN"已改正为 **编译 gate PASS**（§10.1）。这降低了"代码不可编译"的风险，但**不改变**"dist/安装未更新 → 游戏现象不可归因"的结论。
   - 建议：在构建机跑 `NR-11`（完整 `./build.sh` 重写 dist）并在**用户在场**时按 `docs/plans/NEXT_LIVE_SESSION.md` 安装；记录三份 SHA256 + MVID + `/health`；在此之前所有游戏内结论标注 `NOT_ATTRIBUTABLE`。

### 发布前建议（只记录，不在本批次执行）

1. 先跑 `NR-01`/`NR-08`：取得真实 `mod-scan` 输出，替换本矩阵所有"预测"。
2. 按结果修 manifest（`files` 是否需要）与反射（2026-12-10 前）。
3. 统一版本号（manifest/tag/README/工坊），完整构建并记录 dist SHA256/MVID（`NR-11`）。
4. 打包只用 `dist/PawnMind/`；安装/回滚按 `docs/plans/WORKSHOP_INSTALL_DRAFT.md` 草稿执行；**不覆盖用户 DLL、不碰存档**。
5. 取得 `APIChanges.md`/`ModdingGuide.md` 最新版并记录 commit；与本地反编译规则差异记入下轮审计。

---

## 14. 附录：证据命令（可复现 §12）

```bash
cd <repo>/PawnMind

# 0) 快照
date -u; git -C .. rev-parse HEAD; git -C .. describe --tags --always; git -C .. status --porcelain

# 1) 反射（生产文件，排除 obj/bin）
grep -rn "GetField("  src --include='*.cs' | grep -v '/obj/' | wc -l      # 11
grep -rn "GetMethod(" src --include='*.cs' | grep -v '/obj/' | wc -l      # 7
grep -rn "GetProperty(" src --include='*.cs' | wc -l                      # 1
grep -rn "\.Invoke("  src --include='*.cs' | wc -l                        # 9（含 2 个 UnityEvent）
grep -rn "typeof("    src --include='*.cs' | wc -l                        # 68
grep -rn "Assembly\.Load\|Activator\.\|CreateDelegate\|Reflection\.Emit\|GetTypes(\|AppDomain" src  # 0
grep -rln "System.Reflection" src --include='*.cs' | grep -v '/obj/'      # 4 个生产文件

# 2) 线程/协程
grep -rn "System.Threading\|ThreadPool\|\bTask\b\|new Thread\|Timer\|await \|async " src | grep -v '/obj/'  # 0
grep -rn "StartCoroutine\|IEnumerator" src                                # 2 行
grep -rn "Thread\.Sleep" src                                              # 0

# 3) 网络
grep -rn "TcpListener" src                                                # 2 行（1 个实例，BridgeServer.cs:218）
grep -rn "Socket" src | wc -l                                             # 23
grep -rn "HttpListener\|WebClient\|HttpWebRequest\|HttpClient\|UnityWebRequest\|UdpClient" src  # 0
grep -rn "IPAddress\.Any\|0\.0\.0\.0" src                                 # 0

# 4) 原生
grep -rn "DllImport\|extern \|Marshal\.\|unsafe\|System.Runtime.InteropServices" src  # 0

# 5) Core 纯度
grep -rn "using UnityEngine" src/Core | wc -l                             # 0
grep -rn "using UnityEngine" src/Bridge                                    # 6 行（分层偏差，见 §7）
grep -rn "using UnityEngine" src/Game                                      # 允许层（含 GameTradePort 本身不含 Unity）

# 6) 文件能力（预测 files）
grep -rn "System\.IO\.\|ScreenCapture\." src                              # 5 行
grep -rn "SaveFiles\." src                                                # 3 行
grep -rn "ModHelper\." src                                                # 5 行（授权 API）

# 7) dist / 构建
find dist -type f -printf '%p %s bytes\n'
sha256sum dist/PawnMind/PawnMind.dll dist/PawnMind/manifest.xml manifest.xml
find src -path '*/obj' -prune -o -path '*/bin' -prune -o -name '*.cs' -newer dist/PawnMind/PawnMind.dll -print | wc -l   # 26
sha256sum src/bin/Release/netstandard2.1/PawnMind.dll     # d29209a4…（编译 gate 产物）vs dist ad335987…
git -C .. check-ignore -v PawnMind/dist/PawnMind/PawnMind.dll

# 8) 编译 gate（本任务已实测 PASS；会先跑 test.sh，约 1–2 分钟；dist 打包前退出）
cd <repo>/PawnMind
set -a; . ./.env.server; set +a
PAWNMIND_BUILD_ONLY=1 ./build.sh        # 期望: C# 749/0（审阅修复后 750/0）, 门禁 53/0, 契约 503, Build succeeded 0/0, EXIT=0
# 日志示例（本机临时）: /tmp/pawnmind_build_gate_full.log

# 9) 工具数 / 策略 sha
python3 -c 'import json;d=json.load(open("docs/audit/tools-inventory.json"));print(len(d["full"]),len(d["recording"]))'  # 116 106
.venv/bin/python tools/gen_policy_catalog.py --check      # sha256 e0da5a24… matches tool_policy.json

# 10) 官方规则副本定位（仅为推导，不是运行结果）
ls <repo>/_decomp/ModScanner.cs <repo>/_decomp/ModSecurityDebug.cs
grep -n "VERSION = 4\|REFUSE_UNSCANNABLE_CAPABILITIES" <repo>/_decomp/ModScanner.cs
```

---

## 15. 审阅者须知

- 本文件是 **static audit + 编译 gate 实测**：`PASS`/`FAIL` 只用于静态项与编译 gate；所有需要游戏/Scanner/安装的项均为 `NOT_RUN`（§10.2）。
- 预测来自本地反编译规则副本（`_decomp/ModScanner.cs` VERSION=4），**不是**官方运行结果；其与已安装构建的 MVID 对应关系未核实（`UNKNOWN`）。
- 未引用任何网传兼容性口径；外部链接仅作获取方式记录。
- **本版已改正**上一版的错误结论：编译 gate 不是 `NOT_RUN`，而是在本机 Linux/build host 实测 **PASS**（§10.1）；仅 dist 打包/安装保留 `NOT_RUN`（`NR-11`）。
- **共享树是移动的**：审计跨越 `4846189`（首扫）→ `6ab3333`（Nightly B）→ `f789a92`（C/D/E 合并后最终复核）；C/D 合并后重扫确认计数无新增。合并/提交后仍必须用 §14 重跑并据此复核本矩阵。
- 与本文件配套阅读：`docs/plans/WORKSHOP_INSTALL_DRAFT.md`（安装/回滚草稿，未上架）。
