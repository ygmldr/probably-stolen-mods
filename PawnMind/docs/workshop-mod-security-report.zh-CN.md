# 创意工坊 Mod 安全检查报告

- 检查时间：2026-11-09 02:26–02:55（Asia/Shanghai）
- 游戏：Probably Stolen（Steam AppID 4348910），游戏版本 0.46D
- 检查范围：`steamapps/workshop/content/4348910` 下全部 5 个已下载的工坊项目
- 检查方法：文件完整性核对、PE/.NET 结构检查、`ilspycmd` 反编译、IL 指令扫描、游戏自带 `ModTrustList` / `ModSteamGuard` 审计日志交叉验证
- 检查性质：静态分析，未主动运行 DLL。其中 3 个 Mod 已被游戏加载过，2 个处于禁用状态，游戏尚未扫描。

## 结论

**未发现投毒证据。**

这 5 个 Mod 都只引用游戏、Unity、Harmony 等正常依赖；没有网络访问、进程启动、注册表/启动项持久化、原生代码、动态程序集加载或反射执行任意代码的行为。目录总大小与 Steam 记录的 `SizeOnDisk` 完全一致，没有隐藏文件或附加载荷。

唯一需要解释的信号是 **Appraisal Assistant 的 `trust=Updated`**：这是 1.0.8 → 1.0.10 的正常版本更新导致本地信任记录里的组合哈希变化，不是黑名单命中。另有 2 个 Mod 因禁用而未被游戏扫描，本次手动反编译检查同样未发现异常。

## 检查对象

| 工坊 ID | 名称 | 版本 | Manifest 作者 | 所有者 SteamID | DLL 大小 | SHA-256（前 16 位） | 游戏内信任状态 |
|---|---|---|---|---|---|---|---|
| 3828921912 | Appraisal Assistant（鉴定助手） | 1.0.10 | kale | 76561198325245745 | 355,840 | `6ff713017859cfc8…` | `Updated` |
| 3828938065 | 客户需求高亮 | 1.3.6 | hollow | 76561198310244144 | 64,512 | `c7f3974954fcc254…` | `NotChecked`（禁用） |
| 3829053332 | 声望飘字 Reputation Pop | 1.0.0 | User | 76561199237236549 | 13,824 | `3d55332b88bc2db0…` | `Match` |
| 3829206666 | Smart Inventory Arrange | 3.17.47-mono.1 | xiepeixuan | 76561198166860494 | 746,496 | `9fa7244ba20de549…` | `NotChecked`（禁用） |
| 3830661318 | 批量标签 | 1.0.0 | Alikes·Illusions | 76561199021295624 | 14,336 | `2a77b801c0bd849c…` | `Match` |

说明：`Match` 表示本地文件哈希与该 Mod 上次被用户批准时一致；`Updated` 表示文件已变化、需要重新确认；`NotChecked` 表示 Mod 被禁用，游戏没有计算指纹。

## 检查结果

### 1. 文件与清单

- 5 个条目各有且仅有 1 个 manifest、1 个 DLL，没有额外 DLL、脚本、压缩包或可执行文件。
- 目录实际总大小 **1,273,814 字节**，与 `appworkshop_4348910.acf` 里的 `SizeOnDisk = 1273814` 完全一致 → 说明 Steam 写入后没有被追加/删除文件。
- 5 个 `manifest.xml` 都是普通 XML，没有 `DOCTYPE`、`ENTITY`、外部实体或网络引用；`<Capabilities>` 均为空。
- 唯一的图片资源 `preview.png` 在 `IEND` 之后没有附加数据；目录下没有符号链接，没有异常文件名。
- 每个 manifest 声明的 `<Assembly>` 都是简单文件名，没有 `..\` 路径穿越。

### 2. PE / .NET 结构

- 5 个 DLL 都是 PE32 Mono/.NET 程序集，3 个节（`.text` / `.rsrc` / `.reloc`），**没有 overlay**，节熵 5.1–6.1，属于正常 .NET 程序集范围，没有加壳/压缩迹象。
- 均无 Authenticode 签名（游戏 Mod 的常态），`.rsrc` 只有常规版本信息。
- 没有 `SuppressIldasm`、`Obfuscation` 之类特性；类型名、方法名、字符串都可读，反编译结果完整，不是混淆过的样本。
- 引用的程序集只有 `Assembly-CSharp`、`UnityEngine.*`、`Unity.Localization`、`0Harmony`、`Unity.TextMeshPro`、`Newtonsoft.Json` 等游戏/框架程序集；**没有 `System.Net.*`、`System.Diagnostics.Process`、`System.Management`、`Microsoft.Win32`**。

### 3. 能力扫描

| 能力 | 结果 |
|---|---|
| 网络（HttpClient / WebClient / WebRequest / Socket / UnityWebRequest） | 0 处 |
| 启动进程（`Process.Start` / ShellExecute） | 0 处 |
| 原生代码（DllImport / P/Invoke / `calli`） | 0 处 |
| 动态代码（`Assembly.Load` / `Reflection.Emit` / `CreateDelegate`） | 0 处 |
| 注册表、启动项、计划任务、服务 | 0 处 |
| 文件写入 | 仅通过游戏 API `ModHelper.WriteModDataFileText`，写入各 Mod 自己的 `ModData` 目录；该 API 带路径穿越防护。`PlayerPrefs` 仅用于保存热键 |
| 反序列化 | `TypeNameHandling.None`，没有 `BinaryFormatter` / `LosFormatter` |
| 混淆或加密字符串 | 未发现；没有 `FromBase64String` 解密逻辑 |
| 内嵌资源 | 仅 SmartInventoryArrange 内嵌一个 JSON 偏好模型，按纯数据解析 |

### 4. 代码行为

- 5 个 `IMod` 的 `Init/OnEnable` 都只做配置读取、UI 创建、Harmony 补丁注册或后台求解器初始化。
- Harmony 补丁目标全部是游戏 UI、库存、交易、输入相关类型；**没有任何 Mod 去补丁 `ModSteamGuard`、`ModTrustList`、`ModLoader` 或存档系统**。
- AppraisalAssistant 的反射/IL 解释器是这批 Mod 里最复杂的部分：它只对 `GameItem`、`TagState`、`Math`、`string` 等白名单方法调用，第三方价格规则在受限解释器里只读执行、步数受限；没有发现可以逃逸到 `System.IO`、`System.Net` 或 `Process` 的路径。
- SmartInventoryArrange 的 `Task.Run` 只做排列求解，不做 I/O；文件读写只发生在 `ModData` 的设置/记录文件。
- 没有环境指纹、反调试、时间炸弹或条件触发逻辑；唯一的 `DateTime.Now` 用于错误日志时间戳。

### 5. 游戏自带信任机制

- 信任列表签发于 `2026-11-02T06:11:56Z`，由游戏内置 RSA 公钥验签；当前 `killSwitch` 关闭，`verified` 为空。
- 黑名单包含：
  - 2 个文件哈希：`55351101b261…`、`2186b38d2d8c…`，理由均为“Contains malware: it starts a hidden program on your computer.”
  - 2 个作者：`testtesttest`、`Jkki`。
- 5 个 Mod 的**每一个文件**的 SHA-256 都不匹配上述文件哈希；Manifest 作者也不在黑名单内。
- 本次运行日志：
  - `batch_labeler`：`trust=Match`，`caps=none`
  - `reputation_pop`：`trust=Match`，`caps=none`
  - `kale.appraisal_assistant`：`trust=Updated`，`caps=none`
  - `smart_inventory_arrange` / `hollow.clientdemandhighlight`：`trust=NotChecked`，`caps=declared none`（禁用）
- 所有日志中没有出现 `Blocked` 或 `Stopped by ModSteamGuard` 事件。
- 游戏运行时守卫了 **85 个 Steam 方法**和 **6 个 `Process.Start` 重载**；本次没有任何 Mod 触发。

### 6. 两个需要解释的信号

#### Appraisal Assistant 的 `trust=Updated`

- 上一次运行记录的是 **1.0.8**，组合哈希 `23621181591b…`，状态 `Match`。
- 本次为 **1.0.10**，组合哈希 `3d93a0368b3f…`，状态 `Updated`。
- 工坊记录 `timeupdated` 为 2026-11-08 14:50（本地时间），Manifest、程序集版本、README 都对应 1.0.10，更新说明是修复价值鉴定显示与排错日志。
- 结论：这是正常版本更新，不是投毒。游戏的 `Updated` 语义只是“你上次批准的哈希变了”，需要用户重新确认。

#### 两个 `NotChecked`

- SmartInventoryArrange 和 ClientDemandHighlight 处于禁用状态，游戏没有计算指纹、也没有做能力扫描。
- 本次手动反编译检查未发现网络、进程、持久化、原生代码或动态加载；如果启用，游戏会重新扫描，并可能弹出能力确认。
- 这两个 Mod 的 Manifest 都声明 `<Capabilities />` 为空；如果游戏扫描出 `files`、`network`、`reflection-external` 等未声明能力，会拒绝启用并要求作者更新 Manifest。

#### 小瑕疵（不影响安全）

- `批量标签`：Manifest 版本是 1.0.0，程序集版本是 2.0.0.0，属于打包/版本号不一致。
- 游戏的信任列表字段从 `blockedAuthors` 变成了 `blockedPublishers`，但当前缓存的 11-02 列表仍使用旧字段名，所以**作者封锁目前实际未生效**；文件哈希封锁仍然生效。这是游戏侧的问题，你的 5 个 Mod 不在作者名单里。

## 残余风险

- Mod 与游戏同进程运行，拥有与游戏相同的 Windows 用户权限。游戏的 `ModSteamGuard` 只覆盖 `Process.Start` 和 Steam API，**不覆盖任意 `System.IO` 或原始网络调用**；一个恶意的未来更新仍可能读写用户文件，除非它被文件哈希黑名单或能力扫描拦住。
- AppraisalAssistant 的第三方规则解释器是这批 Mod 里最大的攻击面。当前实现是白名单只读沙箱，但解释器复杂度高，建议只从可信作者更新。
- 两个禁用 Mod 尚未经过游戏扫描；首次启用时请阅读能力提示。
- 工坊条目可能被作者账号被盗后替换；`trust=Updated` 是发现这种替换的主要信号。

## 建议

1. **保留这 5 个 Mod**，目前没有删除的必要。
2. Appraisal Assistant 更新到 1.0.10 后，先核对工坊页面/更新说明，再在游戏里批准这次 `Updated`；如果不放心，可以先禁用，其他 4 个不受影响。
3. 启用 SmartInventoryArrange / ClientDemandHighlight 时一次只启用一个，留意游戏的能力确认弹窗；若出现 `files`、`network`、`process`、`native` 等未声明能力，拒绝启用。
4. 保持游戏自带信任列表和 `ModSteamGuard` 开启；不要用管理员权限运行游戏。
5. 继续保留游戏的自动存档备份（`save_*` 文件），重要存档另存一份。
6. 可选：用 Windows Defender 对 `steamapps/workshop/content/4348910` 做一次自定义扫描。未签名 Mod DLL 在 VirusTotal 上可能出现误报，需要结合本报告判断。

## 附录：完整校验值

### DLL SHA-256

```
6ff713017859cfc823e5a950452192646c78fbcab0a4f210dfad2bc457563d8f  AppraisalAssistant.dll
c7f3974954fcc254cc62859009ee0ea4a388dfa70b5013fd5f39ba4ae7c85836  ClientDemandHighlight.dll
3d55332b88bc2db06a909093b8a8ff7795cc66b9a3b288b50d317c8d8f71cbb9  ReputationPop.dll
9fa7244ba20de549feb74731875d31c17e14f06e2024eae36d9fd1bb3e34dc33  SmartInventoryArrange.Mono.dll
2a77b801c0bd849c6872e7f4f6debbcdafee321d4d917718331d2dcfbe589c2f  BatchLabeler.dll
```

### 游戏组合指纹（manifest 哈希 + 各 Assembly 哈希）

```
3d93a0368b3f6850fe6070e3e7ff9b83c3af5ebf21de90e01842dadd47b0c3c2  kale.appraisal_assistant 1.0.10
9ed72ebc6aeb6dd72673cec39c031f70b12eb177e8d3c5e37cea0d9eb9e698a0  hollow.clientdemandhighlight 1.3.6
54419d7bba359d2a888333316aeaf4827f6bc31fbdb8d099a99166465763ed53  reputation_pop 1.0.0
00d57c85baedebe65485c1503fcd0867b9be12b440c94287f7e055f93bc86ceb  smart_inventory_arrange 3.17.47-mono.1
271529edc452f4b8d3782f6311b765d5e3af1406ab65ecd95ed45544cd4c7731  batch_labeler 1.0.0
```

其中 appraisal、reputation、batch 三项与游戏日志中的 `hash=` 完全一致，说明本地文件就是游戏实际审计的那一份；另外两项因为禁用未出现在日志里，由本次检查按同一算法计算。

### 信任列表已知恶意文件哈希

```
55351101b2610435291875d3ee0ecf3d9d245abde689a1b6c4a12af2386c42e7
2186b38d2d8ca774a302f2cf76e6769ad2e12ffc847354efd62ab0c41950ce2c
```

5 个 Mod 的所有文件均不匹配。
