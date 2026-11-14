# PawnMind S6A：HTTP 传输加固 + 隐藏信息清理（离线证据）

**边界**：本轮没有启动游戏、没有打开菜单、没有调用真实 `/action`、没有创建/读取/写入/复制/删除任何真实存档，也没有改动 Steam、安装目录、`manifest.xml`、`dist/`、`build.sh` 或其它构建脚本。所有验证均为 loopback 假客户端、纯逻辑单测、脚本门禁与已安装 `Assembly-CSharp.dll` 的只读契约。隐藏信息结论只对照 `src/` 与本地既有 `_decomp/` 的类型/方法名（未复制反编译代码、未生成新产物）。

## A. 传输加固

### 契约（`src/Bridge/BridgeServer.cs`）

- 所有 socket 以 `Blocking = false` 工作；`Send` 前只用 `Poll(0, SelectWrite)` 探测，不再设置 `SendTimeout`/阻塞发送。HTTP 处理仍完全由 Unity `Update → MainThreadPump → Tick()` 驱动，无线程/任务/协程。
- 每个连接只保存一份响应字节和读写游标（`OutBytes/OutOffset/OutLength`）；一次 `Send` 的部分写只推进游标，剩余字节在后续 Tick 继续，不会丢包。
- `BridgeLimits` 默认上限：

| 限制 | 默认 | 超限行为 |
|---|---|---|
| `MaxConnections` | 16 | 新连接 accept 后立即关闭，计数并写日志；不加入客户端列表 |
| `MaxEventWaiters` | 8 | 立即返回稳定 envelope `{ok,rev,changed:false,timeout:true,busy:true}` |
| `MaxPendingBytesPerClient` | 1 MiB | 丢弃响应、关闭连接、写日志；写动作同时上报 undelivered |
| `MaxWriteBytesPerTickPerClient` | 64 KiB | 余量留到下一 Tick |
| `MaxWriteBytesPerTickTotal` | 256 KiB | 本 Tick 不再写其它客户端，下一 Tick 继续 |
| `WriteTimeoutMs` | 5 s | 关闭并写日志；写动作上报 undelivered |
| `RequestTimeoutMs` | 10 s | 未完成请求的连接关闭 |
| `MaxAcceptsPerTick` / `MaxReadsPerTickPerClient` / `ReadChunkBytes` | 8 / 16 / 4 KiB | 限制单帧 accept/read 工作量 |

- 连接释放：`Tick` 前半段用 `Poll(0, SelectRead) && Available == 0` 识别对端已关闭但没有待读字节的空闲连接，避免只靠 10 s deadline 才清理。
- 请求上限沿用 `HttpRequestParser.MaxHeaderBytes=8192` / `MaxBodyBytes=65536`：声明超限、实际读入超限或 header 超限均返回 `413 Payload Too Large`，错误码 `body_too_large` / `header_too_large`；未知 `Transfer-Encoding` 仍为 `400 unsupported_transfer_encoding`。
- `Content-Length` 始终取 UTF-8 字节数；响应体先按字节构造再计数，多字节文本有回归测试。

### 断线与“结果未知”（只在 S1 缓存语义上加）

1. `BridgeServer` 在 `/action` 响应入队时记住原始请求体与 handler 原文，并把 `Dispatched=true`。
2. 发送抛错、写超时或响应超过 pending 上限时，调用 `ReportUndelivered`：计数、写日志（不含 token/body），并回调 ctor 新增的 `undeliveredHandler`。
3. `ActionRouter.MarkResponseUndelivered(requestBody, responseBody)`（`src/Bridge/ActionRouter.cs`）只对非只读 op 且有 `action_id` 的请求生效；用 `ActionGate.Replace` 仅替换“与缓存原文完全一致”的条目（`src/Core/ActionGate.cs`），因此：
   - 已 dispatch 的写动作缓存被替换为 `{"ok":false,"error":"outcome_unknown","detail":"transport_undelivered","unknown":true,"dispatched":true,...}`；
   - 同一 `action_id` 的下一次请求命中 S1 Duplicate 缓存，返回该 unknown，不再 dispatch；
   - `ping`、只读 op、`get_action_history`、以及 stale/conflict/缺字段等“根本没执行”的拒绝不会被改写。
4. 已知 TCP 局限：若服务端已把完整响应写入内核缓冲、客户端之后才断开，服务端无法判定“未送达”。可判定的是 Send 失败、写超时、超限与 shutdown 时的未发完响应；文档与报告均按此口径，不承诺 exactly-once。

### `/events`

- 首次判定：`since < 0` 或 `rev != since` 立即返回 `{ok,rev,changed:true,state}`。
- 等待：记录 `EventDeadline`；每 `EventCheckIntervalMs=200ms` 检查一次 revision；超时返回 `{ok,rev,changed:false,timeout:true}`；并发超过上限返回同上 + `busy:true`。
- release 时机：超时/变更都在下一次 Tick 入队，写完后关闭连接；等待期间不读请求、不阻塞主线程，离开等待只由 `EventDeadline`、变更或写超时驱动。

### 离线无法证明的部分（fail-closed 记录）

- **“阻塞 Send 卡住 Unity 帧”在本机无法复现**：WSL2 loopback 的发送/接收缓冲会吸收接近 1 MiB 的响应（实测 900 KiB 单次阻塞 `Send` 仍在 11 ms 内返回并最终完整送达，客户端 2 KiB 接收缓冲也不触发 2 s `SendTimeout`）。因此本轮不声称“真机帧率已通过”；改为断言可离线证明的设计不变量：socket 非阻塞、没有阻塞式发送调用、每 Tick 写入 ≤ 配置预算、非读客户端能在有限 Tick 内收完整响应。Unity/Mono 运行时与 Windows 内核下的阻塞行为留 S8。
- **内核级“短写返回”无法强制**：用 2 KiB 客户端接收缓冲 + 4 KiB/Tick 预算强制“单帧只能写一部分”的路径，验证续写不丢包；真实内核的短写由同一 `OutOffset/OutLength` 逻辑覆盖。
- 连接上限、事件上限、pending 上限、写超时都是硬上限；超过时断开并记录，不做无限缓冲。

## B. 隐藏信息审计（计划 D.5）

| # | 项 | 读取/暴露点 | 结论 | 处理 |
|---|---|---|---|---|
| 1 | `stealth_wanted` | `src/` 全量 grep | 0 命中，S5 移除后无残留 | 保持；静态守卫禁止 `stealth_wanted`/`stealthWanted`/`isStealthWanted` |
| 2 | 未展示电话联系人 | `PhoneService.ListJson(include_hidden)` | 旧实现 `include_hidden=true` 可无条件展开，模型可看未展示联系人 | **已修**：仅 `allow_test_items=true`（debug 配置）且未 armed recording 时展开；否则 `{"ok":false,"error":"hidden_contacts_forbidden"}`；MCP `list_contacts` 描述同步 |
| 3 | 鉴定正确答案 | `AppraisalHelper.GetCorrectBaseValue` | `src/` 0 命中；物品值只读 `GetNegociatedValue`（可见议价/标价口径） | 保持；静态守卫禁止该成员 |
| 4 | 偷窃/走私真实判定 | `ContrabandService`：`ContrabandHelper.GetContrabandLevelInInt` / `IsContraband` / `StolenHelper.IsStolenItem` | 原生 tooltip（`CreateContrabandItemTooltip`、`CreateStolenItemTooltip`）向玩家显示违禁等级与 stolen 风险，属于可见信息 | 保留；不新增更细的真实判定（如内部 heat） |
| 5 | 顾客隐藏身份/钱包 | `StateSnapshot.DescribeClient` | `is_wanted`/`wanted_poster_id`/`report_reward` 属通缉海报/报告流程可见信息；`stealth_wanted` 已无；`clientCash` 是游戏私有字段（本地 Rev13 中只有声明，UI 只显示 `GetBudget()`）；`impatience` 是内部节奏值，不是身份答案 | **已删 `client.cash`**；`client.budget`（谈判 UI 可见）保留；`impatience` 保留并列入 S8 复核 |
| 6 | 水质真实组成 | `WineService.StatusJson` 的 `water_quality` / `contaminant_per_mille` | 前者由 `WineHelper.GetWaterQualityScore → WaterHelper.GetWaterPurity`（真实纯度档）派生；后者为 `WINE_COUNTERFEIT_TAG` 的隐藏判定。玩家只看到 label 投影（`GetCurrentWaterDisplay` / `GetCurrentItemCondition`） | **已删两字段**；保留由可见 `quality_tier` 派生的 `extract_per_mille`；静态守卫禁止真实纯度/伪酒判定成员 |
| 7 | 标签“当前条件” | `LabelService.ListJson` 的 `current_id/current_display` | `ItemFeature.GetCurrentItemCondition()` 是 `IsExposed() ? realCondition : fakeCondition`：未曝光时返回假标签，不是真实答案 | 保留；静态守卫禁止直接读 `realCondition` |
| 8 | 分析仪结果 | `AnalyzerService` 的 `tested/labeled/contaminant` 等 | 来自 `WaterScannerUIManager`/`ChemScannerUIManager`/`PlantAnalyserUIManager` 的 UI 文本，等同于玩家实际使用扫描仪看到的内容 | 保留 |
| 9 | `/state` 其它字段 | `StateSnapshot` | 物品 `value=GetNegociatedValue()`；对话/租金 S5 已去身份化；无鉴定答案/真实水质/隐藏条件 | 保持 |
| 10 | `get_ui_snapshot` | `UiSnapshot` | 只输出 active 控件的 `text/enabled/path`，不含游戏状态答案键 | 保持；S8 对照真实画面确认没有“active 但不可见”的调试控件 |
| 11 | `legal_actions` | `LegalActionBuilder` | 由可见 flags 推导，disabled 对话选项不生成，无隐藏条件文本 | 保持 |
| 12 | 新局菜单 | `NewGameMenuViewBuilder` / `NewGameMenuRules` | `Hidden` 候选被 ViewBuilder 过滤，选择规则拒绝 `option_hidden` | 保持 |
| 13 | 安保内部计数 | `SecurityService` 的 `dirty_water`/`mislabeled_water`/`bad_chem_*`/`bribe_tracker` | `evidenceLevel` 在 `RecordUIManager` 显示；这些计数器只在 `DebugPanel` 打印，不在正常 UI 暴露，但玩家可由自身行为推知，不是“答案键” | 本轮保留，列入 S8 画面复核；如需更严格可后续只在 debug 暴露 |
| 14 | 彩票答案 | `LotteryService` 的 `winning_number/matches` | 开奖时 `OnDrawWiningNumber` 把中奖号写入玩家票据，`CreateLotteryTicketTooltip` 按命中着色显示 | 保留（可见） |

审计方式：对 `src/` 逐项 grep（`GetCorrectBaseValue`、`stealth_wanted`、`isStealthWanted`、`realCondition`、`GetWaterPurity`、`GetWaterQualityScore`、`GetWineContaminantPerMille`、`include_hidden`、`clientCash`），`clientCash` 已加入静态守卫的禁止列表，并对照本地 `_decomp/` 的可见性（`ItemFeature.GetCurrentItemCondition`、`ContrabandHelper.CreateContrabandItemTooltip`、`StolenHelper.CreateStolenItemTooltip`、`ScavHelper` 的 UI 文案、`LotteryHelper`、`RecordUIManager`）。新增 `HiddenInfoGuardTests` 把“禁止读答案键”固化为回归门禁。

## C. 错误码与语义

| 场景 | 结果 |
|---|---|
| 声明/实际 body 超 65536，或 header 超 8192 | `413 Payload Too Large` + `body_too_large` / `header_too_large` |
| 未知 `Transfer-Encoding` 或非法 `Content-Length` | `400 Bad Request` + `unsupported_transfer_encoding` |
| 连接数超限 | 新连接立即关闭；日志 `connection limit reached`；`RejectedConnections++` |
| 响应超过 pending 上限 | 连接关闭；日志 `output limit exceeded`；写动作 → undelivered |
| 读动作响应超过 pending 上限 | 连接关闭 + 日志；不产生 unknown |
| 写动作响应发送失败/写超时/超限 | `UndeliveredResponses++`；同一 `action_id` 重试返回 `outcome_unknown`（`detail=transport_undelivered`） |
| `/events` waiter 超上限 | 立即返回 `timeout:true,busy:true`，不排队 |
| `list_contacts` 在生产/录制请求 `include_hidden=true` | `{"ok":false,"error":"hidden_contacts_forbidden"}`，返回内容不含隐藏联系人 |

## D. 测试证据

- **Red A（编译失败）**：先加入 `BridgeTransportTests`（limits/连接数/每 Tick 预算/undelivered/events）与 `HiddenInfoGuardTests`，`./test.sh BridgeTransport` 报 `CS0246 BridgeLimits`、缺少新 ctor 参数与诊断属性，确认生产 API 未实现。
- **Red B（行为失败，实现过程中）**：首版实现后 `./test.sh BridgeTransport` 为 `9 pass / 4 fail`：`/events` 在请求处理末尾被误关、空闲断连未被 `Poll` 识别导致连接槽不释放、事件 waiter 计数断言时序。修 `Tick` 的 event 分支、`TryRead` 的 EOF 探测后转绿。
- **Red C（隐藏答案键）**：`HiddenInfoGuardTests.Bridge_Source_Never_Reads_Hidden_Answer_Keys` 在 `WineService.cs` 命中 `GetWaterQualityScore` / `GetWineContaminantPerMille` 两处真实纯度/伪酒判定；删除字段后转绿。把 `clientCash` 加入同一守卫后再次先红（`StateSnapshot.cs`），移除 `client.cash` 后转绿。
- **413 口径**：改动前的既有测试断言同一 70000 字节请求返回 `400`；S6A 规范要求 `413`，测试已改为 `413 + body_too_large`，实现后通过。
- **Green（主代理可复跑）**：
  - `REQUIRE_GAME=1 ./test.sh`：C# **456** pass / **0** fail（原 435 + 21：传输 14、隐藏守卫 2、路由 3、Gate 1、电话 1）；脚本门禁 **53** pass；适配器契约 **392**，`adapter contract OK`，`strict mode: REQUIRE_GAME=1`，无 SKIP。
  - `dotnet build src/PawnMind.csproj -c Release --no-incremental`：**0 警告 / 0 错误**。
  - Python：`.venv/bin/python -m unittest discover -s agent/tests` → **30** OK（原 29 + `list_contacts` 描述/参数同步 1 项）。
  - `./test.sh BridgeTransport` 连续 3 次均 `14 pass / 0 fail`，无挂死。

覆盖：慢读/不读客户端、2 KiB 接收缓冲下的多 Tick 短写、UTF-8 `Content-Length`、半包请求、header/body 超限 413、连接上限与释放、断开后重连、响应超 pending 上限、`/events` 超时/变更/并发上限、写动作断线后同 `action_id` 返回 unknown 且不重复 dispatch、超限写响应同样返回 unknown。

## E. S8 必须真机确认（离线无法证明）

1. **Unity/Mono 帧内 tick**：离线只证明“非阻塞 + 每 Tick 上限 + 完整送达”；真实帧率下的帧耗时、Unity Mono 的 `Socket.Poll/Send` 行为需真机复测。
2. **Windows 内核短写/慢客户端**：本机 loopback 缓冲会吞掉接近 1 MiB 的发送，无法复现“Send 阻塞”；真机慢速 MCP 客户端/暂停的调试器下确认每帧不卡。
3. **断线 unknown 的调用层纪律**：真实 MCP/agent 在收到 `outcome_unknown` 后不得用新 `action_id` 重试扣款/存档；需在录像客户端上验证。
4. **`/events` 并发**：真实 MCP 长轮询与第二个宿主/工具并发时的 `busy` 反馈；Release 客户端超时设置是否匹配。
5. **隐藏信息画面复核**：通缉海报（`is_wanted`/`report_reward`）、违禁品/stolen tooltip、水质扫描仪、酒瓶 label、安保 record 的 `evidenceLevel`/内部 trackers 是否与桥快照一致；确认没有“玩家看不到但桥能看到”的字段。
6. **生产配置**：`allow_test_items=false` 的发布包中 `list_contacts(include_hidden=true)` 必须返回 `hidden_contacts_forbidden`；录制 armed 时同样禁止。

## F. 本轮改动文件

- `src/Bridge/BridgeServer.cs`：`BridgeLimits`、非阻塞有界写出、连接/事件/pending 上限、EOF 清理、413、`ReportUndelivered` 与诊断计数、事件 `busy`。
- `src/Bridge/ActionRouter.cs`：`MarkResponseUndelivered`、`OutcomeUnknown` 增加 detail。
- `src/Core/ActionGate.cs`：`Replace`（仅在缓存原文匹配时替换）。
- `src/Core/PhoneDirectory.cs`：`HiddenContactsForbidden` + `TryResolveIncludeHidden`。
- `src/Bridge/PhoneService.cs`：`list_contacts` 门禁。
- `src/Bridge/WineService.cs`：移除真实水质分/伪酒判定两个答案键字段。
- `src/Bridge/StateSnapshot.cs`：移除 `client.cash`（游戏私有钱包字段）。
- `src/PawnMindMod.cs`：记录 recording armed、把 undelivered 回调接到 router、`list_contacts` 传 debug/recording 标志。
- `agent/mcp_server.py`：仅 `list_contacts` 描述同步。
- 测试：`BridgeTransportTests.cs`（新）、`HiddenInfoGuardTests.cs`（新）、`BridgeServerTests.cs`、`ActionRouterTests.cs`、`ActionGateTests.cs`、`PhoneDirectoryTests.cs`、`PawnMind.Tests.csproj`；`agent/tests/test_mcp_server.py`。

未触碰：游戏进程/存档/安装/Steam/云同步、`manifest.xml`、`build.sh`、`dist/`、`_decomp/` 产物、其它 Python 文件。

## G. 主代理独立复核（2026-11-14）

- 亲自复跑 `REQUIRE_GAME=1 ./test.sh`：C# **456**、脚本 **53**、适配器 **392**、`adapter contract OK`、无 SKIP；Python **30** 项 OK；`git diff --check` 无错误。
- 逐段审阅传输实现：所有 socket 均为 `Blocking=false`，发送前 `Poll(0, SelectWrite)`，部分写在 `OutOffset/OutLength` 上续写，单 Tick 写入按预算截断；连接/事件/pending 三类上限均“拒绝并断开 + 计数”，没有无界缓冲。
- `ActionGate.Replace` 只有在 `action_id` 存在且缓存原文与即将上报的响应**逐字节相同**时才替换；只读 op、`ping`、`history` 与所有拒绝路径都不会被改写，符合 S1 的“首次结果”语义。
- 隐藏字段：`stealth_wanted`、`clientCash`、真实水质分/伪酒判定已从桥快照移除，`AppraisalHelper.GetCorrectBaseValue` 无引用；`list_contacts(include_hidden=true)` 现在需要 `allow_test_items` 且非录制态。新增的源码静态门禁测试会阻止这些答案键回流。

**S6A 出关条件**：以上离线不变量成立；真机帧内耗时、慢客户端与 Windows 内核短写仍必须在 S8 由用户共同观察，离线不宣称已证。
