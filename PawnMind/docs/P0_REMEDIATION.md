# P0 修订记录（P0_REMEDIATION）

- **日期**：2026-11-15
- **基线 commit**：`8f64166`（截图取证特性之后；P0 初审提交 `fd287da` 之后仓库已前进）
- **触发**：用户对 P0 的复审结论——**有条件通过**，要求先补齐 6 项架构契约再进入 P1
- **结论**：6 项全部处理完毕；其中 **1 项是真实安全漏洞，已修复并加回归测试**

---

## 总览

| # | 要求 | 处理 | 结果 |
|---|---|---|---|
| 1 | 检查 `act` 是否可调用 `debug_*`，给静态调用链与拒绝测试 | **发现漏洞并修复** | ✅ 已封闭，3 项测试 |
| 2 | 明确 `action_id` 生成、intent 持久化、dispatch、核对顺序 | 写入 ADR-010 | ✅ |
| 3 | ADR-004 增加内部 `timeline_id` 隔离读档/回档/槽复用 | 写入 ADR-004（修订） | ✅ |
| 4 | Recorder 必须处理只读/异步/DB 故障/跨进程，不能按 HTTP 方法分类 | 写入 ADR-008 | ✅ |
| 5 | `sleep` 只在观察到实际跨日后确认；P1 不做反思 | 写入 ADR-009 | ✅ |
| 6 | 风险等级与真机验证状态分离；修复 schema 必填不一致 | 重建矩阵 + 修 `wine_add_water` | ✅ 2 项测试 |

---

## 1. `act` → `debug_*` 可达性：**确认存在漏洞，已修复**

### 1.1 静态调用链（修复前）

```
MCP/HTTP  op="act", request.action="debug_grant"
   │
   ├─ PawnMindMod.DispatchAction("act", req)                  src/PawnMindMod.cs:206
   │     │
   │     └─ RecordingDispatchGuard.TryHandle("act", req)      src/Bridge/RecordingDispatchGuard.cs:21
   │           target = "debug_grant"; viaAct = true
   │           IsProtected("debug_grant") → false             ← 只覆盖 8 个 op
   │           return false   // 不拦截
   │
   ├─ if (op == "act") → DispatchAction(inner="debug_grant")   src/PawnMindMod.cs:206-212
   │
   └─ switch(op) case "debug_grant": DebugService.GrantJson(...)   ← 作弊入口执行
```

**结论**：`RecordingDispatchGuard.IsProtected` 只列了 `new_game / load_game / save_game / sleep / press / save_recording_game / load_recording_game / confirm_new_game` 八个 op，5 个 `debug_*` 全在其外。因此任何持 token 的客户端（或 `--tools full` 模式下的模型）都能用 `act` 绕过录制门禁直接改钱、造物、解锁远征。

加剧因素：`act` 本身在录制集里已被排除，但**服务端才是安全边界**，且 `--tools full` 会暴露它。

### 1.2 修复（先红后绿）

**Red**：新增 3 个测试后编译失败——`RecordingSaveErrors` 不含 `DebugForbidden`。

**Green**（[RecordingDispatchGuard.cs](<../src/Bridge/RecordingDispatchGuard.cs>)）：

```csharp
if (viaAct && IsDebugOp(target))          // act 永不可达 debug_*
    → recording_act_wrapper_forbidden
if (!IsProtected(target)) {
    if (!IsDebugOp(target)) return false;
    if (service == null) → recording_service_unavailable   // fail closed
    if (!service.RecordingActive) return false;            // 仅本地开发保留
    → recording_debug_forbidden
}
```

`RecordingSaveService.RecordingActive`（新增）：

```csharp
public bool RecordingActive
{
    get { return policy != null && policy.State != RecordingSlotState.Disabled; }
}
```

### 1.3 测试证据

```
$ cd PawnMind && ./test.sh Guard_
PASS RecordingDispatchGuardTests.Guard_Act_Cannot_Reach_Debug_Ops
PASS RecordingDispatchGuardTests.Guard_Denies_Direct_Debug_Ops_While_Recording
PASS RecordingDispatchGuardTests.Guard_Leaves_Direct_Debug_Ops_When_Recording_Disabled
...
passed=14 failed=0
```

覆盖：
- `act{action:debug_grant|debug_fill_water|debug_unlock_expedition|debug_pulse|debug_dump}` 全部拒绝，错误码 `recording_act_wrapper_forbidden`；
- 录制态直接调用 5 个 debug op → `recording_debug_forbidden`；
- 录制关闭时直接调用不被 guard 消费（本地开发不受影响）；
- 原有 `act → save_game/new_game/press/confirm_new_game` 拒绝测试仍通过。

### 1.4 fake 游戏侧拒绝测试（不碰真实存档）

`agent/tests/test_write_rev_required.py` 通过真实 stdio MCP 握手断言：录制集**不注册** `act`、`press`、`new_game`。这是 MCP 层的第二道保险——即使 C# 修复回退，录制会话里也没有 `act` 可调。

> 说明：fake 游戏是 Python 替身，无法执行 C# 逻辑，因此 `act→debug` 的**执行层**证据只能来自 C# 单元测试；fake 测试负责证明**工具面**不暴露。两层都覆盖，不复述为“真机已验证”。

---

## 2. `action_id` 生命周期与执行顺序 → ADR-010

固定五步：生成 → **intent 先落盘** → HTTP dispatch → result → 可选快照/对账。

关键约束：
- intent 写失败 → **不发起调用**（fail closed），保证崩溃后可发现“可能已下发”的动作；
- 启动时扫描 `status=pending` → 转 `unknown`，要求人工/只读核对；
- `unknown` 不自动重试、不换新 `action_id` 重放；
- 只读 op 不写 intent，只写 observation。

---

## 3. `timeline_id` → ADR-004（修订）

新增本系统自有的 `timeline_id`，用于隔离：

| 场景 | `reason` | 处理 |
|---|---|---|
| 新局绑定 | `new_game` | 新 timeline，`parent` 指向该槽上一次 timeline |
| 同 run 回档 | `rollback` | 新 timeline，旧 timeline 永久保留 |
| 槽内容被换（删档重开/外部替换） | `external_replace` | 新 timeline，不继承旧进度 |

约束：检索默认只查当前 timeline；跨 timeline 仅允许 `scope=game_version` 的机制经验，且需显式请求；现金/库存/任务进度**永不可跨 timeline 可见**。

---

## 4. Recorder 分类不按 HTTP 方法 → ADR-008

明确四点：

1. 分类由**语义表**决定（`/action` 上 42 个只读 + 52 个写混在一起）；
2. 只读 → 只写 `observation`，不写 intent，不参与对账；
3. 数据库故障：intent 失败 → 拒绝执行；result 失败 → 有界缓冲后标 `partial` 并暴露丢事件计数；
4. 跨进程并发：SQLite `BEGIN IMMEDIATE` + 单一 writer，`seq` 由数据库分配；
5. 短生命周期：状态全部落盘，内存仅缓存。

---

## 5. `sleep` 完成判据 → ADR-009

`sleep` 返回只代表**请求已下发**；必须轮询到 `day > 采样前 day` 才写 `sleep_completed`。超时写 `sleep_unresolved`，禁止重发、禁止记为成功。`day_advanced=false` 不是失败。**P1 不触发任何 LLM 反思。**

---

## 6. 风险与证据分离 + schema 修复

### 6.1 工具矩阵重建（102 工具）

[audit/tool-matrix.md](<audit/tool-matrix.md>) 现在有两列独立事实：

| 列 | 含义 |
|---|---|
| `risk` | 语义分级：`read` 50、`ui` 6、`low` 22、`medium` 16、`high` 8 |
| `evidence` | 真机证据：`n/a` 50、`partial` 18、`yes` 7、`no` 27 |

一个 high 工具可以 `evidence=yes`，一个 low 工具也可以 `evidence=no`；不再把“没验证过”当成风险等级。

### 6.2 Schema 必填字段修复

**问题**：`wine_add_water(item_id, ml=750, rev=None)` —— 写动作的 `rev` 可选，与“写动作必须带 rev”矛盾；服务端实际会返回 `missing_rev`，属于声明与行为不一致。

**修复**：改为 `wine_add_water(item_id: int, rev: StrictInt, ml: int = 750)`。

**测试**（[test_write_rev_required.py](<../agent/tests/test_write_rev_required.py>)）：

- `test_every_write_tool_requires_rev`：扫描全部 102 工具的 `input_schema`，任何写工具若声明 `rev` 却不在 `required` 中即失败；
- `test_wine_add_water_is_not_advertised_with_optional_rev`：专项断言。

Red → Green 已记录（先 `FAILED (failures=2)`，修复后 `OK`）。

---

## 7. 修订后的全量测试

| 套件 | 命令 | 结果 |
|---|---|---|
| C# + 脚本 + 适配器 | `cd PawnMind && ./test.sh` | `passed=502 failed=0`；`contract gate tests: passed=53 failed=0`；`adapter contract OK` |
| Python | `.venv/bin/python -B -m unittest discover -s agent/tests` | `Ran 94 tests ... OK` |

相比 P0 初审（469 / 53 / 81），新增：3 项 guard 测试、1 个新测试模块（2 项 schema 契约）、以及截图特性带来的其余用例。

---

## 8. 未解决 / 留给后续

| 项 | 状态 |
|---|---|
| `CAPABILITY_AUDIT.md` 中的 100/94 计数 | 已在本表与矩阵更新为 102/96；正文数字将在 P1 报告里统一引用新值，不重写初审历史 |
| `act` 对**非** debug 的通用 op 仍可透传 | 符合设计（`act` 是便利入口），但录制集已不暴露它 |
| `timeline_id` 的槽替换识别 | 依赖 `run_id` 变化；若同槽、同 runID 的极端情况无法区分，需 S8 人工确认 |
| C# 侧事件记录 | 明确不做（ADR-002） |

---

## 9. 审计与修复期间未做的事

未启动游戏、未调用真实 `/action`、未读写任何存档文件、未修改游戏安装、未引入新依赖。所有验证为纯逻辑测试、真实 stdio MCP 握手（连的是本地 stub 配置而非真桥）与已安装程序集的只读契约。
