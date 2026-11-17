# RUN_CHANGE_EVIDENCE — 跨局请求 / 跨局完成 / 运行身份（Round 4 / F1b）

> 基线：`4a5b8d1` → 本轮提交
> 证据等级：**SRC** + **OFFLINE**（`R4-F1-01 … R4-F1-09`）；**LIVE = NOT_RUN**
> 一句话：**"原生接受了读档/开新局请求"不是"运行身份已经改变"。**

## 1. 三个必须分开的事实

| 事实 | 字段 | 来源 | 允许做什么 |
|---|---|---|---|
| 请求已发出 | `run_change_status:"requested"` | 调度器返回 `ok:true` + 原生 `load_requested`/`start_requested`/`prompt_confirmed` | 只回读；**不得**驱逐旧 run 证据 |
| 已确认跨局 | `run_change_status:"confirmed"` | 派发前后两次读取的原生运行身份**都可读且不同** | 驱逐旧 `OperationRecord`、重置租金收据（各一次） |
| 无法确认 | `run_change_status:"unknown"` | 身份缺失/不可读（主菜单、加载画面、无 `PlayerStore`） | 保持证据、禁止新的跨局动作、等待回读 |

`RunChangePolicy.Classify(before, after)`（`src/Core/RunChangePolicy.cs`）：

- 两者非空且相等 → `requested`（身份可读，但还没变）；
- 两者非空且不同 → `confirmed`；
- 任一为空 → `unknown`。

**拒绝的替代证据**：`rev`、day、scene、`result.Ok`、`load_pending`、`prompt_confirmed`
都不能替代运行身份。`R4_F1_07` 用"只有 `rev` 变了、身份不可读"固定这一点。

## 2. 运行身份来自哪里

`GameState.ReadRunId()`（`src/Game/GameState.cs`）：

```csharp
if (!PlayerStore.HasInstance) return null;
string runId = PlayerStore.Instance.runID;
return string.IsNullOrEmpty(runId) ? null : runId;
```

- 与 `RentSnapshot.RunId`、`RecordingGameState.RunId` 同源（`store.runID`）；
- **不**进入任何快照 JSON，**不**给模型看到（只在 router 内部比较）；
- 不可读返回 `null`，由 `RunChangePolicy` 判 `unknown`，绝不当成"没变"。
- 未新增游戏成员访问：`runID` 已被 `GameState.ReadRentSnapshot` 与
  `RecordingSaveAdapter` 使用，因此无需新增适配器契约。

## 3. 每条进入新局的路径

| 路径 | 原生返回 | 本轮处理 | 依据 |
|---|---|---|---|
| `confirm_new_game`（只确认 first-run prompt） | `prompt_confirmed:true`，仍在旧 run | `requested`；旧 operation/收据保留 | `R4_F1_01` |
| `load_game` / `load_recording_game` | `load_requested:true` + `load_pending` | `requested`；不驱逐 | `R4_F1_02` |
| `new_game`（受控别名） | `start_requested:true` + `start_pending` | `requested`；不驱逐 | `R4_F1_03` |
| 原生加载异步完成 | 下一次 `get_operation` 读到新身份 | `confirmed` → 驱逐一次 + 收据重置一次 | `R4_F1_09` |
| 派发瞬间身份已变 | 前后两次读取不同 | `confirmed`（同一次写响应里就能看到） | `R4_F1_04` |
| 调度器抛异常 | — | `uncertain`；**不**驱逐 | `R4_F1_06` |
| 身份始终不可读 | — | `unknown`；**不**驱逐 | `R4_F1_07` |

## 4. 驱逐与幂等

- `ActionRouter.ApplyRunChange(actionId)`：只在 `confirmed && !RunChangeApplied` 时执行
  `_gate.ClearOperationsExcept(actionId)` + `_onRunChangeConfirmed()`，
  并把 `RunChangeApplied` 写回记录，因此**重试/重复回读不会重复驱逐或重复重置**。
- 触发点有两个：写完成时（`HandleWrite`）和异步回读时（`GetOperationJson` / 缓存重投递）。
- 被驱逐的是**旧 run 的 `OperationRecord`**；当前切换操作的记录保留，所以它的响应、
  证据与 `action_id` 幂等不受影响（旧 F1 回归 `F1_Run_Change_Evicts_Old_Operations_But_Keeps_Current` 仍在，且现在要求"身份真的变了"）。
- 租金收据（`FirstWeekService.ResetRentReceipts`）从"请求时"移到"确认时"，
  通过 `PawnMindMod.ResetRunBoundEvidence` 回调注入。

## 5. 盲重试防护

`ActionGate.FindUnconfirmedRunChange()`：存在"已派发但未确认"的跨局操作时，
新的跨局写（`load_game` / `load_recording_game` / `new_game` / `confirm_new_game`，
含 `act` 包装）返回：

```json
{"ok":false,"error":"run_change_pending","run_change_status":"requested",
 "run_change_confirmed":false,"blocking_operation_id":"…","next":"…"}
```

同一 `action_id` 的重放仍然命中缓存（`R4_F1_05`）。

**范围说明（OPEN）**：只阻止"同一类跨局操作"的二次派发，不阻止普通写动作。
理由：身份不可读可能长期存在（加载画面），全局加锁会死锁；而普通写动作的响应
始终以当前 `rev` 与当前状态为准，不会被当作旧 run 的证据。
残余风险：在切换未确认期间执行普通写，可能作用在即将被替换的 run 上。
该风险在 L3 前必须由"先 `get_operation` 到 `confirmed`"的 Skill 流程覆盖（见 `SKILL.md` §3）。

## 6. `get_operation` 语义

- 运行切换：记录仍在，`run_change_status` 反映真实状态；
- 缓存驱逐：`known:false` + 现有 `process_restarted:true` 提示（本轮未改文案）；
- Mod 重启：`known:false`（in-process 缓存，无跨进程 exactly-once）。
- 三者**没有**互相硬编码成彼此；`run_change_*` 只在操作本身是跨局操作时出现。

## 7. 复现

```bash
cd PawnMind && ./test.sh R4_F1
# 9 tests, 0 failed
```

## 8. 仍需实机（LIVE = NOT_RUN）

1. 真实 `load_game` 的异步完成时刻：身份在加载画面期间是否为 `null`，加载后是否变化；
2. `confirm_new_game` 之后原生 first-run 流程是否可能在同一帧切换 run；
3. `new_game` 在录制门的拒绝分支下是否真的从未到达原生；
4. 同一次会话中连续两次跨局（读 A → 读 B）时 `ClearOperationsExcept` 的实际次数。
