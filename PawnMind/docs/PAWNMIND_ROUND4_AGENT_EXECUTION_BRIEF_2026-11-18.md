# PawnMind 下一轮工程推进指令（Round 4）

> **交付对象**：负责 `ygmldr/probably-stolen-mods` / `PawnMind` 的编码、测试与审计 Agent  
> **编写日期**：2026-11-18  
> **审阅时的仓库 HEAD**：`4a5b8d1d77a19cab8ddb76e0ba0216ac2da110e0`；F1–F6 主要修复提交为 `509bfd1`。  
> **本轮目标**：关闭 F1b、F4b 两项操作语义缺陷；收敛 F3b、F5b 协议/暴露行为；取得 F7 的官方扫描器证据并明确发布阻塞。**不是**重新实现整套 PawnMind，也**不是**直接宣布 Play 可用。  
> **约束**：如接手时 HEAD 已变化，先重读目标文件、做 `git diff 4a5b8d1..HEAD` 并更新基线；本文描述的是审阅时源码，不得把过时问题强行修复第二次。

## 0. Agent 执行契约：先验证，再修改

### 0.1 首先读取的来源

1. [外部审阅回应 `REVIEW_RESPONSE_2026-11-17.md`](https://github.com/ygmldr/probably-stolen-mods/blob/main/PawnMind/docs/reviews/REVIEW_RESPONSE_2026-11-17.md)；其中 F0–F7 是上一轮问题的正式 disposition。
2. `PawnMind/src/PawnMindMod.cs`：`DispatchAction`、`IsRunChangeOp`、`VerifyOperation`、模式初始化。
3. `PawnMind/src/Bridge/ActionRouter.cs`：`HandleWrite`、`DecorateCached`、`DecorateResponse`、`DispatchedClaim`、`Classify`、`GetOperationJson`。
4. `PawnMind/src/Core/ActionGate.cs`、`PawnMind/src/Core/OperationStatus.cs`、`PawnMind/src/Bridge/ActionResult.cs`。
5. `PawnMind/src/Bridge/RecordingSaveService.cs`：`ConfirmNewGame`、`Load`、`StartSuccess`、`ConfirmPrompt`。
6. `PawnMind/src/Bridge/NewGameMenuService.cs`、`PawnMind/src/Bridge/FirstWeekService.cs`：原始 JSON 生产者。
7. `PawnMind/agent/mcp_server.py`、`PawnMind/agent/tool_policy.py`、`PawnMind/agent/policy/tool_policy.json`、`PawnMind/src/Core/PolicyCatalog.g.cs`。
8. `PawnMind/src/Game/ScreenshotService.cs`、`PawnMind/manifest.xml`；[官方 ModdingGuide — Capabilities and the Scanner](https://github.com/dragonpan2/probably-stolen-modding/blob/main/ModdingGuide.md#capabilities-and-the-scanner)。
9. 对应的 C#、Python 测试，项目内 `SKILL.md`、benchmark/协议说明（先查找实际路径）。

### 0.2 证据分级（每个结论必须标注）

- **SRC**：实际仓库源码及精确 commit/path/line；只能证明静态行为。
- **OFFLINE**：本地运行的 C# / Python / fake-game 测试，必须附命令、结果和测试名。
- **CI**：对应 HEAD 的 GitHub Actions 链接和结果；不能用旧 HEAD 的绿灯证明新 HEAD。
- **LIVE**：真实游戏版本、安装 DLL、运行时健康信息、ModScanner 输出、操作录像/日志。
- **INFERRED**：分析推断或待验证假设，不得写成已实机验证。

**基线事实**：上一轮回应宣称 C# 754 tests、门禁 53、契约 503；本次审阅未独立重跑这些数字。GitHub 的 Python 测试曾核实 `371 tests OK`，但本轮提交后必须重新运行。`play_allowlist` 当时为空，F7 官方扫描器仍为 `NOT_RUN`。不得据此声称“已完成 Play/L3”。

### 0.3 不可违反的安全不变量

- 不对真实玩家存档执行 `new_game` / `load_game` / `sleep` / `end_day` 等验证；测试仅在专用录制/可丢弃存档上进行。
- 不以 `result.Ok`、`load_requested`、`start_requested`、`rev` 变化或 `last_save_time` 单独证明跨局、完成或落盘。
- 不把缺失的派发证据自动解释为 `dispatched=true` 或 `dispatched=false`。**未知不是未派发。**
- 对 `uncertain`、响应丢失、原生回调可能已经执行的请求，绝不以新 `action_id` 盲目重试。
- 不放宽 C# `ModePolicy`、`GameVersionGate`、录制存档绑定、`tool_policy.json`、`PlayOps`，不为让测试通过而填入虚假的 `live_evidence`。
- 不将 `get_state` 的静态纯度检查冒充 `PP-READ-01` 实机无副作用验证；不把 `save_observed` 冒充 crash-durable。
- 不为了发布扫描器绕过官方限制；若能力无法移除，保持 Workshop 发布阻塞。

---

## 1. 本轮问题清单与顺序

| ID | 优先级 | 已观察到的事实 | 目标 | 是否依赖实机 |
|---|---|---|---|---|
| **F1b** | **P0 correctness** | `DispatchAction` 在 `result.Ok && IsRunChangeOp(op)` 时立即 `ResetRentReceipts()` + `WithRunChanged()`；`load_pending` / 首次提示确认也可能 `ok:true` | 只有**跨局已确认**才能驱逐旧证据 | 离线可防回归；最终需要 LIVE |
| **F4b** | **P0 correctness** | `Classify` 对缺少 `dispatched` 的 RawJson 使用 `result.RawJson != null || result.Ok`；`DispatchedClaim` 缺字段或解析失败也可返回 true | 派发事实显式、未知保守，JSON/Operation 同源 | 离线为主 |
| **F3b** | P1 protocol | `DecorateCached` 每次重试从旧缓存重构 `initial_result`；上一轮文档却声称只出现一次；部分旧 `readback` 可能仍在晋升后的顶层 | 规定确定性重试与证据层次，不生成矛盾快照 | 离线为主 |
| **F5b** | P1 exposure | MCP `--mode auto` 在 `/health` 不可达/未知时回退 `test`，`test` 不过滤工具 | 默认模式故障关闭，工具可见性与 Bridge 模式一致 | 离线为主 |
| **F7** | **P0 release gate** | `manifest.xml` 声明 `reflection-external`；`ScreenshotService` 使用 `Directory`/`FileInfo` 和 `ScreenCapture`；官方扫描器尚未运行 | 获取真实 scanner finding，分类并消除发布阻塞 | **需要 LIVE** |

**推荐批次**：A = F1b + F4b（先做，阻止错误的操作事实）；B = F3b + F5b（协议与 MCP）；C = F7（先采集 scanner 证据，按证据修代码）。若具备游戏环境，F7 的**只读扫描采集**可与 A 并行；不要在拿到扫描输出之前猜测并大改截图实现。

---

## 2. A1 / F1b — 区分跨局请求和跨局完成

### 2.1 代码定位与风险

- `PawnMindMod.DispatchAction` 现有条件：`result != null && result.Ok && IsRunChangeOp(op)`；命中后立即重置租金收据，并设置 `ActionResult.RunChanged`。
- `IsRunChangeOp` 包含 `load_game`、`load_recording_game`、`new_game`、`confirm_new_game`。
- `RecordingSaveService.ConfirmNewGame` 在处理原生 first-run prompt 后可以返回 `prompt_confirmed:true`，但**尚未进入新 run**。
- `RecordingSaveService.Load` 返回 `load_requested:true`、`load_pending`，表示加载请求被接受，**不是**运行身份已经改变。
- `RecordingSaveService.StartSuccess` 返回 `start_requested:true`、`start_pending`，同样不能单凭 `ok:true` 推断切换完成。
- `ActionRouter.HandleWrite` 目前在 `_gate.Complete` 后按 `result.RunChanged` 调用 `ClearOperationsExcept(actionId)`；这是 F1 原始缓存丢失问题的修复，**保留其“当前 action_id 不丢失”的性质**。

### 2.2 要求实现的语义

将三个概念显式分离：

1. **`run_change_requested`**：已尝试/已提交原生加载或开始游戏请求；可能异步。
2. **`run_change_confirmed`**：来自可信原生完成事件或可信前后 readback 的运行身份确实发生改变。
3. **`run_change_unknown`**：原生可能已执行，但无可靠完成证据；保持不确定，不重复执行。

实现方式可以选最小侵入方案，不强制新建大模块。但以下要求必须同时成立：

- 只在 `run_change_confirmed` 后清理旧 run 的 `OperationRecord` 和 `RentReceipt`；只确认 prompt、仅发出 load/start 请求、`pending=true`、超时/异常都**不能**提前清理。
- 清理旧证据时仍保留本次切换操作的 `action_id`，重试相同 `action_id` 必须命中缓存，原生派发次数不变。
- 如无法从现有 game readback 提取可靠 run identity，先报告 **BLOCKED: missing reliable run identity**，不得用 `rev`、day、scene 或 `Ok` 凑出“已确认”；可以暂不驱逐、将状态留在 pending/uncertain，等待 LIVE 证据。
- 不得在跨局期间将旧 run 的状态证据归属新 run。若旧状态可见，必须明确 `run_epoch`/证据所属或锁定相关写操作直到完成判定。
- 必须检查 `act` 包装、`new_game` 兼容路径、同步/异步完成、异常与响应丢失路径；若最终拒绝的别名不会被允许，记录证据，不必为不可达路径重构。
- `GetOperationJson` 的语义与缓存过期提示应准确区分**运行切换**、**Mod 重启**、**缓存驱逐**，不得把任一情况硬编码成另一情况。

### 2.3 建议的测试（先 Red，再 Green）

| Test ID | 场景 | 通过条件 |
|---|---|---|
| `R4-F1-01` | `confirm_new_game` 只确认 first-run prompt | 旧 operation、旧 receipt 仍在；不标记 confirmed |
| `R4-F1-02` | `load_game` 返回 `load_requested:true,load_pending:true` | 不清缓存，不重置 receipt；不宣称 run changed |
| `R4-F1-03` | `start_requested:true,start_pending:true` | 同上 |
| `R4-F1-04` | 可信证据确认新 run | 旧 operation/receipt 驱逐，当前切换 action 保留 |
| `R4-F1-05` | 同一切换 `action_id` 重试两次 | 不二次派发；返回与 operation 状态一致 |
| `R4-F1-06` | 原生抛异常 / readback 不可用 / 响应丢失 | uncertain，不误判 confirmed，不盲目重试 |
| `R4-F1-07` | 未知 run identity / 仅 `rev` 或 day 改变 | 不能作为跨局完成证据 |

**禁止的捷径**：把 `if (result.Ok)` 改成 `if (!pending)` 但不检查原生完成证据；在 load/start 返回 `ok:true` 时直接设置 `RunChanged`；为通过测试直接把 `RunChanged` 永远关掉却不记录阻塞与后续收敛路径。

---

## 3. A2 / F4b — RawJson 派发证据不得靠存在性猜测

### 3.1 代码定位

- `ActionRouter.Classify`：当 `json["dispatched"] == null`，现用 `result != null && (result.RawJson != null || result.Ok)`。
- `ActionRouter.DispatchedClaim`：RawJson 缺少 `dispatched` 时返回 true，JSON 解析异常时也返回 true。
- `NewGameMenuService` 的 `SelectJson` / `ActionResultJson` 可产生 `ok:false` 的 RawJson，却未必带 `dispatched`。
- `OperationRecord.Dispatched` 目前为 `bool`；`ExecutionStatus.Classify(ok,pending,uncertain,dispatched)` 依赖它区分 `Rejected` / `Failed`。因此**简单将缺失字段默认为 false 同样危险**。

### 3.2 实施要求

1. 审计全部 `ActionResult.SuccessRaw(...)`、RawJson 工厂、原生服务及其返回分支；产出逐项矩阵：`op`、生产函数、是否调用原生、是否显式 `dispatched`、是否存在失败后副作用不确定、修复位置。
2. 每个 RawJson 写响应必须明确声明**派发事实**，包括：守卫拒绝 `false`、原生已调用 `true`、无法确认的 `unknown`（或等价三态）。不要从 `ok`、RawJson 是否存在或是否异常推断。
3. 对历史/异常路径缺少派发证据的 RawJson，采取 **fail-closed / uncertain / no blind replay**。可使用 `dispatch_status = not_dispatched | dispatched | unknown`，或 `dispatched` 可空 + `dispatch_known`；由实现者选择与现有 API 最兼容的形式，但**不允许把 unknown 序列化成 false 并归类 Rejected**。
4. `Classify`、`DispatchedClaim`、`ActionGate.Complete`、`OperationRecord`、`GetOperationJson`、缓存重投递必须共用一份派发判定，不能 JSON 写 `false` 而记录写 `true`。
5. `pending` / `uncertain` 的优先级保留；即使 `ok:false`，如果原生可能执行也不得归为“未派发的安全拒绝”。
6. 对无法安全迁移的生产者，保留受控兼容层并记录 **OPEN**，不要静默降级。

### 3.3 测试

| Test ID | 场景 | 通过条件 |
|---|---|---|
| `R4-F4-01` | RawJson `ok:false` 且显式 `dispatched:false` | `Rejected`，记录和 JSON 均 false |
| `R4-F4-02` | RawJson `ok:false` 且显式 `dispatched:true` | `Failed` 或 `Uncertain`（依事实），记录和 JSON 均 true |
| `R4-F4-03` | RawJson `ok:false` 且**缺少** `dispatched` | 不假报 true，也不假报安全未派发；不得自动重试 |
| `R4-F4-04` | RawJson 非法 JSON / `dispatched` 非布尔 | 协议错误或 uncertain；不二次派发 |
| `R4-F4-05` | `NewGameMenuService` 前置守卫拒绝 | 原生调用次数 0，派发明确 false |
| `R4-F4-06` | 原生调用后异常 / readback 丢失 | 派发事实与不确定结果分离 |
| `R4-F4-07` | `get_operation`、同 ID 重试、丢失响应后重投递 | 三个视图的派发声明一致，原生次数恒为 1 |

**重要**：原始 F4 的回归 `F4_Dispatched_Follows_The_Service_Claim` 必须继续通过；新增测试必须能在旧实现上先失败，避免只测试自己新写的 helper。

---

## 4. B1 / F3b — 晋升、缓存重试和原始证据的统一契约

### 4.1 现状

`DecorateCached` 每次重复请求都读取旧缓存并重新 `DecorateResponse`。当 operation 从 `running` 晋升 `completed` 后，`DecorateResponse` 会清掉 `pending` / `error`，并再次创建 `initial_result`。因此回应文档的“只在该次晋升时出现一次”与实际行为不符。

另外，代码将顶层 `shop_closed_after` 改成 `true`，但旧 `readback`、`can_end_day_after` 等字段可能仍是派发时的旧快照。若无独立完成证据对象，模型会同时看到“已完成”和“旧快照仍为 OPEN”。

### 4.2 本轮选定契约（除非提交 ADR 说明替代方案）

- **重试应确定性且可重复**：一旦 operation 晋升，所有相同 `action_id` 的重试都可以带同一份 `initial_result`，它是**原始派发时证据**；不是“仅首次返回一次”。更新回应文档和 Agent Skill。
- `initial_result` 必须稳定，不嵌套自身，不随重试次数膨胀；重复读取不能触发第二次原生派发。
- **顶层只放当前已证实事实**。历史 readback 要么移入 `initial_result`，要么显式标注 `initial_readback`；不要把旧 readback 当作完成时快照。
- 完成态若需 `shop_closed_after:true`，必须有 verifier 的 `Readable && InGame && CLOSED` 证据；最好提供 `completion_evidence`（来源、观察状态、时间或 revision，实际能证明什么就写什么）。不能靠 `execution_status=completed` 反推不存在的字段。
- `get_operation`、写请求重试、`wait_operation` 应使用一致的状态与来源；`delivery_status` 独立于业务完成态，保持 F3 原有修复。

### 4.3 测试

`R4-F3-01`：晋升前 `pending:true`，晋升后连续两次相同 ID 重试；两次都 `ok:true`、无顶层 stale `pending/error`、包含相同的原始证据，原生派发仅一次。  
`R4-F3-02`：晋升后顶层不能同时包含互相矛盾的 `shop_closed_after:true` 和未标明为历史的 OPEN readback。  
`R4-F3-03`：没有 verifier 完成证据时，`running/uncertain` 不得晋升 `completed`。  
`R4-F3-04`：响应丢失 + 重投递时，`delivery_status` 变化不抹掉执行证据。  
`R4-F3-05`：原始响应含嵌套 JSON、重复重试 10 次，不发生 `initial_result` 递归增长。

---

## 5. B2 / F5b — MCP auto 模式故障关闭

### 5.1 现状

`agent/mcp_server.py::_detect_mode(client, requested)` 在 `requested == "auto"` 且 Bridge 不可达时返回 `test`；健康信息未知也返回 `test`。`allowed_tools_for_mode("test")` 返回 `None`，即**不做注册过滤**。因此 Bridge 未就绪时可向模型展示全部工具，虽然 C# 的最终权限检查仍能拒绝实际写操作。

### 5.2 实施要求

- 默认 `auto`：Bridge 不可达、健康信息缺失、`locked`、未知模式、策略哈希不一致时，不得回退 `test`；应返回 `locked/unavailable`，注册最小安全诊断集合或明确拒绝启动。不要隐式暴露 116 工具。
- `test` 必须由**显式** `--mode test` / 受控配置启用，并核对 Bridge 真实 `run_mode=test` 和相应授权；测试便利性不应成为默认降级策略。
- `play` 只展示 policy 中有真实 live evidence 的工具；`play_allowlist` 为空时仍为空，不为演示擅自填充。
- `bootstrap` 不暴露 snapshot readers；保持已有快照纯度边界。
- 检查 `LOCAL_TOOL_NAMES` 是否真的都是无需 Bridge 权限的本地工具；如有持久化/写入能力，要单独审计其影响，不能因“本地”自动放行。
- 处理启动后 Bridge 模式变化：如果 MCP 的工具注册表不能动态刷新，至少检测到模式/策略不匹配后拒绝相应调用并要求重启 MCP；不得继续以过期 `tools/list` 暴露高权限工具。
- 不修改 C# 权限为适应 MCP；C# 仍是最终门禁。

### 5.3 测试

`R4-F5-01`：Bridge 连接失败，默认 `auto` 不注册完整工具集。  
`R4-F5-02`：健康模式为 `locked`、空、未知值，均 fail closed。  
`R4-F5-03`：Bridge `play` 且 live evidence 为空，不出现任何未批准的游戏写工具。  
`R4-F5-04`：显式 `--mode test` + Bridge `test` + 授权才展示测试工具；否则拒绝或锁定。  
`R4-F5-05`：启动后 Bridge 模式/策略变化，不得继续执行原注册表中的越权工具。  
`R4-F5-06`：bootstrap 的 6 个 snapshot readers 继续不可见；未知新工具默认拒绝。

---

## 6. C / F7 — 官方 ModScanner 与发布兼容性

### 6.1 需要纠正的既有假设

上一轮回应已发现 `ScreenshotService.cs` 中 `System.IO.Directory`、`FileInfo` 可能触发 `files` 扫描。但官方 ModdingGuide 的 capability 表还将 **`ScreenCapture`** 列为 `files` 的触发项；源码使用 `ScreenCapture.CaptureScreenshotAsTexture()`。因此**仅把 `Directory` / `FileInfo` 换成 `ModHelper`，不能保证消除 `files` finding**。

`PawnMind/manifest.xml` 当前声明 `network`、`reflection-external`，未声明 `files`。官方指南说明：`reflection-external`、`dynamic-code`、`native`、`threads` 在计划的 **2026-12-10** 游戏更新后会被拒绝；在此之前也会以 deprecated 警告显示。此处是**官方公布的计划**，不是我们已观察到该更新上线。

### 6.2 采集步骤（真实游戏，非模拟扫描器）

1. 记录 `git rev-parse HEAD`、构建命令、`PawnMind.dll` SHA256、安装位置、`dist` DLL SHA256；若安装与构建不一致，先解决一致性再扫描。
2. 记录游戏精确版本、ModLoader/Scanner 版本或 fingerprint、manifest 内容、`/health` 的 game version/MVID/policy hash（仅记录非敏感值）。
3. 在开发/测试环境启用官方 F8 控制台，执行 `mod-scan pawnmind`（或按官方指南使用文件夹路径），保存**完整原始输出**，包括 `declared`、`detected`、`DEPRECATED`、`ERROR`、hard flags、fingerprint。
4. 建立 finding 表：capability → 触发方法 → 代码位置 → 是否 hard refusal → 允许的替代 API → 处理方式 → 复扫结果。
5. 如果扫描器报 `files`：分别追溯 `System.IO` 和 `ScreenCapture`；可选方案包括声明必要 `files` 并清晰披露、替换为官方允许的截图路径、或暂时关闭截图功能。**选择必须基于官方规则与产品安全约束**，不能只删除 manifest 字段。
6. 如果报 `reflection-external`：逐一消除动态反射触发点，优先使用静态 `typeof(...)` / 官方 `ModReflect` 或受支持的游戏类型访问。无法消除时，标记 **Workshop BLOCKED**；不通过关闭 scanner、开发模式豁免或篡改 trust 层绕过。
7. 扫描通过后，再做录制存档上的最小启动/读写 smoke test；不以扫描通过替代玩法或数据安全测试。

### 6.3 F7 交付门槛

- `R4-F7-01`：有原始官方 scanner 日志、准确 game/MVID/DLL/manifest 指纹。
- `R4-F7-02`：扫描中每项 `files` / `reflection-external` / 其他能力都有具体定位与 disposition。
- `R4-F7-03`：复扫无硬拒绝，声明覆盖 detected；如不能满足，发布明确 BLOCKED。
- `R4-F7-04`：已安装 DLL 与本轮构建匹配；若未匹配，不进行 LIVE 通过判定。
- `R4-F7-05`：兼容性记录明确区分“当前可启动”与“2026-12-10 后预计可启动”，不偷换时间条件。

**如果 Agent 无法操作游戏**：不得伪造 scanner 输出。只提交静态发现和供操作者执行的采集步骤，将 `LIVE/Scanner = NOT_RUN`、`Workshop = BLOCKED`，等待用户提供日志。可继续完成 A/B 批次。

---

## 7. 回归、构建和证据要求

### 7.1 每个修复遵守 Red → Green → Regression

每项至少记录：

- **Red**：旧实现上的失败测试（断言失败优先；如果是编译失败，说明为什么没有行为证据）。
- **Green**：修复后的同一测试通过，附原始命令与摘要。
- **Regression**：旧有 F1–F6、operation 两轴、lost response、幂等、policy catalog、版本锁定、租金/房东负面测试不回退。
- **Scope**：变更文件清单、是否修改 public protocol、是否修改 `PlayOps` / manifest / native call。

### 7.2 建议命令（以实际仓库脚本为准）

```bash
cd PawnMind
./test.sh
python3 -B -m unittest discover -s agent/tests
python3 tools/gen_policy_catalog.py --check
# 从仓库根目录：
python3 -B scripts/security/scan_git_secrets.py

# 辅助审计，不能代替逐分支阅读：
rg -n 'SuccessRaw\(|RunChanged|WithRunChanged|ResetRentReceipts|ClearOperationsExcept' src
rg -n 'dispatched|initial_result|load_pending|start_pending|prompt_confirmed' src test agent
rg -n 'ScreenCapture|System\.IO\.Directory|FileInfo|reflection-external' src manifest.xml
```

如果某条命令在当前环境不可用，写明环境依赖和失败原因，不把 `NOT_RUN` 填成 `PASS`。如果项目 `./test.sh` 同时运行多个测试套件，报告其真实输出，不沿用旧计数。

### 7.3 本轮必须生成的仓库内文档

| 产物 | 必须包含 |
|---|---|
| `docs/reviews/ROUND4_DISPOSITION.md` | F1b/F4b/F3b/F5b/F7 逐项 OPEN/FIXED/BLOCKED；SRC/OFFLINE/CI/LIVE 证据；剩余风险 |
| `docs/audit/RUN_CHANGE_EVIDENCE.md` | 每种新建/加载/提示路径的请求、原生完成与运行身份判据；没有可靠证据时明确缺口 |
| `docs/audit/RAW_DISPATCH_AUDIT.md` | 全部 RawJson 写生产者和拒绝/调用/异常分支的派发事实矩阵 |
| `docs/audit/MOD_SCANNER_ROUND4.md` | 真实 scanner 原始输出、DLL/游戏指纹、finding 与复扫；无游戏时 `NOT_RUN` |
| `docs/reports/ROUND4_REPORT.md` | HEAD、修改文件、测试 Red/Green、CI 链接、风险、下轮建议 |
| Agent Skill / 协议文档 | `initial_result` 重试规则、`completion_evidence`、`dispatched unknown`、MCP 模式行为；查找真实文件位置 |

所有文档须能让下一位审计 Agent **只靠 commit、命令和日志**复现；不要写“测试全部通过”而不给版本和证据。

---

## 8. 本轮决策门槛（Gate）

### Gate A — Operation 事实正确性

通过条件：`R4-F1-*`、`R4-F4-*` 全部通过；无提前跨局清理；无派发事实猜测；旧 F1/F4 回归通过。**未通过则不进入 Play/LIVE 写操作**。

### Gate B — 协议与工具暴露

通过条件：`R4-F3-*`、`R4-F5-*` 全部通过；重试证据无自相矛盾；MCP auto 故障关闭；C# final gate 保持不变；文档与代码一致。

### Gate C — 发布兼容

通过条件：官方 `mod-scan` 完整证据，已安装 DLL 匹配，未留下 hard refusal / deprecated 未来阻塞；如未有 LIVE scanner，**Gate C = BLOCKED**，不能用静态扫描或单元测试代替。

### Gate D — Play / First-week rent

**本轮默认不授予通过**。只有先前 S0–S4 的真实游戏验证、D0a 读路径无副作用、trade B10–B16、异步完成、房东保护、录制存档与真实 DLL 一致性、policy live evidence 等全部达到既定标准，才能进入首次完整第一周租金自主实机跑。不得因为 Round 4 全绿而填满 `play_allowlist`。

### 必须停下并上报的情况

- 找不到可靠的 run identity/原生完成事件，无法证明 F1b 的 `confirmed`。
- 某 RawJson 生产者无法区分“调用前拒绝”与“调用后结果未知”。
- 需要修改真实存档、取消版本/模式门禁、修改官方信任机制才能继续。
- ModScanner 的能力要求与预期不同，或 manifest 需要新增高权限但尚无风险评估。
- 发现 HEAD 已修复问题、测试预期不再成立：先更新 disposition，避免重复改动。

---

## 9. 建议提交顺序与交接格式

1. `test(round4): reproduce premature run eviction and raw dispatch ambiguity` — Red tests / 审计矩阵。
2. `fix(round4): confirm run changes before evicting evidence` — F1b 最小实现。
3. `fix(round4): make raw dispatch evidence explicit` — F4b 实现与回归。
4. `fix(round4): stabilize promoted operation replay` — F3b。
5. `fix(round4): fail closed on MCP auto mode` — F5b。
6. `docs(round4): scanner findings and handoff` — F7 证据、Skill、disposition、报告；如果没有实机，只写静态证据和 `NOT_RUN`。

允许按仓库惯例合并提交，但必须能追踪每项问题到对应 diff/test。不要把 F7 的大规模反射重构和 F1b/F4b 的语义修复混进同一个不可审计提交。

**最终回复必须给出**：最终 HEAD；5 项 disposition；Red/Green/CI 证据；所有测试命令与结果；修改文件列表；是否改动 policy/manifest；官方 scanner 状态；已安装 DLL 是否匹配；本轮仍需用户操作的精确步骤；是否满足 Gate A/B/C/D。

---

## 10. 可直接投喂编码 Agent 的执行指令

> 你是 PawnMind Round 4 的实现与审计 Agent。请以本文为工作契约，从实际仓库 HEAD 开始复核 `REVIEW_RESPONSE_2026-11-17.md` 和相关源码，不得只相信报告。先完成 Red 测试和 F1b/F4b 源码审计，再用最小变更修复跨局提前清理、RawJson 派发证据歧义；随后完成 F3b 重试证据契约与 F5b MCP auto 故障关闭。F7 必须采集真实官方 ModScanner；若无法操作游戏，标记 NOT_RUN/BLOCKED，不要假造实机结论。严格保持 C# 模式/版本/录制存档门禁、同 ID 幂等和 player-parity 约束。每个任务提交 Red→Green→Regression 证据，更新 ROUND4_DISPOSITION、RUN_CHANGE_EVIDENCE、RAW_DISPATCH_AUDIT、MOD_SCANNER_ROUND4、ROUND4_REPORT 与 Skill。禁止扩大 `play_allowlist` 或宣布 Play ready，除非既定 live gates 已独立通过。最终提供 commit、diff、测试结果、剩余阻塞和用户需执行的精确操作。

---

## 11. 本文来源与置信度

- **SRC**：`509bfd1`/`4a5b8d1` 的 `ActionRouter.cs`、`PawnMindMod.cs`、`RecordingSaveService.cs`、`ActionResult.cs`、`OperationStatus.cs`、`NewGameMenuService.cs`、`FirstWeekService.cs`、`mcp_server.py`、`ScreenshotService.cs`、`manifest.xml`，通过 GitHub 仓库文件读取核对。审阅时 `509bfd1..4a5b8d1` 为后续文档提交，主要实现仍在 `509bfd1`。
- **SRC**：`REVIEW_RESPONSE_2026-11-17.md` 的 F0–F7 处置和已知测试声明；声明不等于本轮复测。
- **OFFLINE/CI**：上一轮独立检查到 GitHub Python workflow `371 tests OK`；本轮所有代码改动后仍须重跑。
- **官方文档**：`dragonpan2/probably-stolen-modding` 的 ModdingGuide，`files` 触发项包括 `ScreenCapture`，并公布 2026-12-10 deprecated 能力计划。
- **INFERRED**：F1b 的提前驱逐风险、F4b 的派发歧义、F3b 的历史 readback 冲突、F5b 的工具暴露风险均由上述代码路径推导；本文提出的实现与测试是工程建议，**不是**已经执行的补丁或 LIVE 结果。
