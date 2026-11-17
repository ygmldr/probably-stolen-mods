# PawnMind S4：专用录制存档槽与授权门禁（离线证据）

**边界**：本轮没有启动游戏、没有打开菜单、没有调用真实 `/action`、没有创建/读取/写入/复制/删除任何真实存档，也没有改动 Steam、安装、manifest、`dist/`、构建脚本或 Python。所有验证均为纯逻辑 / fake 端口 / 已安装 `Assembly-CSharp.dll` 的只读契约。C# 与适配器契约已完成；Python/MCP 工具按用户安排另行接入。

## 交付

| 层 | 内容 |
|---|---|
| 配置 | `BridgeConfig` 新增 `recording_mode`（空/`off` 默认锁定）、`recording_slot`（空=未选定，保留非法原文但不拖垮整份配置）、`recording_isolation_ack`（只接受 `true`/`1`）、`recording_dedicated_slot_ack`（只接受 `true`/`1`；诚实声明“沿用现有已游玩配置 + 新专用槽”，不冒充隔离）。两个 ack 至少一个为 true 才解锁；默认序列化不会自动填槽或自动确认。 |
| Core 策略 | `RecordingSlotPolicy`：`Disabled → Armed → Bound`，任何绑定/回读不符进入 `Locked`；不自动复用旧绑定，不回滚，不重试。`RecordingSettings` 只从用户配置解析。 |
| Core 端口 | 注入 `IRecordingGameStatePort`、`IRecordingSlotInventoryPort`、`IRecordingNativeSavePort`、`IRecordingSaveMetadataPort`。Core 不引用 Unity/游戏类型。 |
| Bridge 服务 | `RecordingSaveService`：`get_recording_save_status`（只读）、`save_recording_game`、`load_recording_game`、`confirm_new_game`，以及旧入口 `new_game` / `load_game` / `save_game` / `sleep` / `press` 的统一策略。 |
| Bridge 路由 | `RecordingDispatchGuard`：`act` 包裹受保护 op 直接 `recording_act_wrapper_forbidden`；`press` 始终拒绝；旧写入口在进入原生层前被拦。`get_recording_save_status` 加入 `ActionRouter` 只读白名单。 |
| Game 适配器 | [RecordingSaveAdapter.cs](<../src/Game/RecordingSaveAdapter.cs>)：只读 `SaveFiles.IndexLoad` 槽索引 / `PlayerStore` 身份；原生 `SaveGame`、`StartNewGameClick`（经难度页原生 Start）、`EndDayUIManager.OnEndDayButtonClick`、`ConfirmPanelUIManager.OnYesClick`。不直接调用 ES3，不设置新局目标槽，不读存档预览内容。 |
| 菜单接线 | S3B 的 7 个菜单写操作改为经同一 `CheckMenuWrite()`；旧固定拒绝 `new_game_slot_policy_unavailable` 已从生产代码移除。 |
| 适配器契约 | 新增 `PlayerStore.HasInstance`、`ConfirmPanelUIManager.OnYesClick`；既有 `SaveFiles.IndexLoad/Slot/NextFreeSlotId`、`NewGameData.saveSlot`、`PlayerStore.saveSlotId/runID/runNumber/startType/startScenarioId/lastSaveTime`、`SavePreviewData.lastSaveDateTime` 均在 Rev13 程序集上通过。 |

## Rev13 关键结论（决定“不预写目标槽”）

- `NewGameData.saveSlot != -1` 在 `EmporiumEntry.Start` 中走 `PlayerStore.LoadGame()`；`-1` 才走 `StartNewGame()` + `InitialSave()`。
- `PlayerStore.InitialSave()` 自己调用 `SaveFiles.NextFreeSlotId()` 分配槽号并写盘，**不读取** `NewGameData.saveSlot` 作为分配目标。
- 因此 S4 不把目标槽写进 `NewGameData.saveSlot`。受控开始路径是：菜单难度页原生 Start → 清空 `saveSlot` 为 `-1` → 场景加载 → `InitialSave` 自动分槽 → 回读 `PlayerStore.saveSlotId/runID/runNumber/startType/startScenarioId`，必须与配置槽号及启动意图完全一致才进入 `Bound`；不符立即 `Locked`，不回滚、不重试。
- 开始前只读检查：`activeSaveSlotId` 索引 + `slotIdGenerator` 预测下一个空闲槽；目标槽必须在索引中未占用且等于预测的下一个自动分配槽，否则在调用原生 Start 前拒绝（`recording_slot_occupied` / `recording_slot_not_next_free` / `recording_slot_allocation_unknown` / `recording_slot_inventory_unavailable`）。

## 配置模板（两种确认方式二选一）

```ini
recording_mode=recording
recording_slot=<必须等于游戏当时的 next free slot>

# A. 隔离环境：独立 Windows 用户 / 独立 LocalLow，与本机原档分离
recording_isolation_ack=false
# B. 同配置专用槽：沿用现有已游玩配置，但只允许绑定这个新专用槽
recording_dedicated_slot_ack=true
```

- `recording_isolation_ack=true` 表示用户声明 A：当前是隔离环境。它是用户声明，代码无法验证 OS 用户/LocalLow/Steam 云同步是否真的分离。
- `recording_dedicated_slot_ack=true` 表示用户声明 B：**不**使用隔离环境，而是沿用现有已游玩配置，只把录制写入限制在这个新专用槽。这个声明不得被理解为隔离；它只是让配置与实际选择一致，避免把 isolation ack 填成不实的 true。
- 两者至少一个为 true 才可能 `Armed`；两者都 false/缺失时错误码仍为 `recording_isolation_unacknowledged`（保持不变，避免破坏既有协议/测试），所有录制写操作保持锁定。
- 两者都 true 时 `Armed`，但状态里的 `acknowledgment` 优先报 `isolation`；实际只用一种时应把另一种留 false。

## 强制入口与状态语义

| 入口 | Disabled | Armed | Bound | Locked |
|---|---|---|---|---|
| `new_game` | `recording_unconfigured` | `recording_new_game_forbidden` | `recording_already_bound` | `recording_locked` |
| `save_game` / `save_recording_game` | 配置错误 | `recording_not_bound` | 允许（先回读槽/run） | `recording_locked` |
| `load_game` / `load_recording_game` | 配置错误 | `recording_not_bound` | 仅绑定槽；其他槽 `recording_slot_not_bound` | `recording_locked` |
| `sleep` | 配置错误 | `recording_not_bound` | 允许（自动保存前后回读） | `recording_locked` |
| `press` | 配置错误 | `recording_press_forbidden` | `recording_press_forbidden` | `recording_locked` |
| `act` 包裹上述 op | `recording_act_wrapper_forbidden` | 同左 | 同左 | 同左 |
| S3B 菜单写 | `recording_unconfigured` | 允许（准备） | `recording_already_bound` | `recording_locked` |
| `confirm_new_game` | 配置错误 | 允许一次原生新局许可 / 首局 prompt Yes | `recording_already_bound` | `recording_locked` |

启动 pending 期间所有写操作返回 `recording_start_pending`；加载 pending 期间返回 `recording_load_pending`。回读不符后操作统一返回 `recording_locked`，`get_recording_save_status.error` 保留具体原因（`recording_slot_mismatch`、`recording_run_mismatch`、`recording_start_identity_mismatch`、`recording_run_identity_missing`、`recording_readback_unavailable`）。

## 只读状态接口

`get_recording_save_status` 只输出：`recording_mode`、`acknowledgment`、`configured_slot`、`bound`、`binding{slot,run_id,run_number,start_type,start_scenario_id}`、`slot_matches`、`run_matches`、`last_save_time`、`error`。`acknowledgment` 是只读枚举，只能是 `"isolation"`、`"dedicated_slot"` 或 `null`（配置未启用/槽非法/两个 ack 都未给时）；两者都声明时优先报 `isolation`。不输出其他槽号/预览/路径/寄存器。`last_save_time` 仅在已绑定时读取该专用槽的 `preview_<slot>` 索引时间；未绑定时不读取任何槽预览。

## 测试证据

- **Red**：先加 6 个测试文件，编译失败确认缺少 `RecordingSaveService` / `RecordingDispatchGuard`；随后补 Core/Bridge 后实际出现 58 pass / 6 fail，失败集中在：非法槽配置保留、Armed 菜单开页、锁定错误码统一、未完成 load pending 后再次 load、save/start 回读不匹配后的二次拒绝。
- **Green（主代理可复跑）**：
  - `REQUIRE_GAME=1 ./test.sh`：C# **374**、脚本门禁 **53**、适配器契约 **384**，`adapter contract OK`，`strict mode: REQUIRE_GAME=1`，**无 SKIP**。
  - `dotnet build src/PawnMind.csproj -c Release`：**0 警告 / 0 错误**。
- 覆盖：默认全锁；ack 缺失；仅 isolation ack / 仅 dedicated-slot ack / 两者都有（状态优先报 isolation）；槽缺失/非法/被占；预测槽不一致；回读槽号不符 → Locked 且只调用一次原生；只有绑定槽 save/load；其他槽不触达原生；`act` / `press` 绕过拒绝；sleep 未绑定拒绝；进程新实例不自动复用绑定；版本门禁锁定即使录制配置合法也拒绝；状态 JSON 含 `acknowledgment` 且不含其他槽/预览/路径；错误码稳定。

## S8 必须人工确认（离线无法证明安全）

1. **目标槽为空**：生产库存只读 `SaveFiles` 索引。若存在“文件在、索引不在”的孤儿槽，预测可能变化；S8 必须由用户确认专用槽在游戏菜单中为空，并接受启动后回读不符即停机。
2. **运行环境声明**：`recording_isolation_ack` 和 `recording_dedicated_slot_ack` 都只是用户声明；代码不能证明 OS 用户/LocalLow/Steam 云同步与原用户档分离。S8 需人工核验并确认专用槽号。若用户明确选择“现有已游玩配置 + 新专用槽”，必须用 `recording_dedicated_slot_ack=true` 诚实声明，不得把 isolation ack 填成 true；此时其它用户存档与录制同处一个配置，唯一保护是策略只允许绑定这一个专用槽，需人工接受剩余误操作风险。
3. **原生新局真实写盘**：`confirm_new_game` 的离线实现只调用 fake；真机上难度页 Start 会立即触发 `InitialSave` 写盘。用户必须在 S8 对所选运行环境（隔离环境或同配置专用槽）和“一次原生新局写盘”明确授权。
4. **重启后绑定**：新 Mod/游戏进程实例只回到 `Armed`，不会复用旧 `Bound`；当前没有离线可证的自动重新绑定。S8 需人工决定重启后的继续方式（重新受控开局或人工重新绑定），不得自动读用户槽。
5. **首局 prompt**：只接受本地化文本识别为 `first_run_confirm` / `skip_intro_first_run_confirm` 的弹窗，且走原生 `OnYesClick`。若正式版文本/行为变化，会 fail closed；S8 需核对真实弹窗。
6. **最近保存时间**：Rev13 的 `PlayerStore.lastSaveTime` 静态代码未见写入；状态改用绑定槽的 `preview_<slot>.lastSaveDateTime`。若保存/跨日自动保存的索引写入滞后，时间可能短暂偏旧；S8 需以游戏 UI 存档时间为准。
7. **存档索引自愈**：`SaveFiles.IndexLoad` 在索引损坏时可能触发游戏自身的隔离/重建逻辑；这是游戏原生行为，离线未触发。S8 需在正常索引上验证，不主动制造损坏。
8. **`sleep` 自动保存时序**：`EndDayUIManager.OnEndDayButtonClick` 的真实保存/跨日完成时序未真机验证；S4 只保证 Bound 前置门禁、回读槽/run 和出错即锁，不断言保存一定完成。
9. **Python/MCP 已补接（同日）**：本轮 C# 未改 `agent/`；随后独立 Python 子卡注册了 `get_recording_save_status` / `save_recording_game` / `load_recording_game` / `confirm_new_game` 四个工具（无 slot 参数、写工具单步一次调用、非法 rev 本地拒绝），Python 全量 29 项通过。MCP↔C# 的真实联调仍需 S8；离线只能证明两边 op 名与参数形状一致。

## 主代理独立复核（2026-11-14）

- 亲自复跑 `REQUIRE_GAME=1 ./test.sh`：C# **374**、脚本 **53**、适配器 **384**，`adapter contract OK`、`strict mode: REQUIRE_GAME=1`、无 SKIP；`git diff --check` 无错误。
- 逐段审阅 `RecordingSlotPolicy`（`startIssued` 在绑定成功时清零、绑定/在局不符即 `Lock` 且不回滚）、`RecordingSaveService`（开始/保存/加载/跨日均为“先判策略→一次原生调用→回读→不符即锁”）、`RecordingDispatchGuard`（`act` 包裹受保护 op 一律拒绝、`press` 恒拒绝）与 `RecordingSaveAdapter`（只读索引/身份，唯一原生回调）。
- 核对 `_decomp/SaveFiles.NextFreeSlotId`：原生实现同时检查 `ES3.FileExists(Slot(i))` 与索引并**写回** `slotIdGenerator`；适配器只读索引、不写生成器，因此“文件存在但索引缺失”的孤儿槽会让预测偏移。当前处理是启动前 `recording_slot_not_next_free`/`recording_slot_occupied` 拒绝、启动后回读不符即 `Locked` 停机，不会覆盖或删除任何文件；S8 需由用户确认专用槽在游戏 UI 中为空。
- 另核对 `PlayerStore.EndDay()` 只关店、不写盘，故 `end_day` 不属于必须绑定的保存入口；真正跨日保存由 `sleep` 负责。

**因此 S4 出关条件**：以上离线证据成立，且用户已知悉“录制槽必须等于游戏当时的下一个自动分配槽（空白隔离配置通常为 0）”。

## 未触碰

未启动游戏、未安装/覆盖 Mod、未写 `dist/`、未改 manifest/构建脚本/Steam/云同步、未读写任何真实存档、未生成 `_decomp/` 产物、未改 Python。
