# CAPABILITY_AUDIT — PawnMind × 长期记忆系统（P0）

**审计时间**：2026-11-14
**基线 commit**：`fd287da9b6bef82e5de56333cf51b38f028f4e0e`（工作树干净）
**审计性质**：只读。未启动游戏、未执行任何写动作、未读写任何真实存档文件。
**对应规范**：[Probably_Stolen_Agent_Memory_Architecture_v1.md](Probably_Stolen_Agent_Memory_Architecture_v1.md) 第 12 节 Phase 0（P0.1–P0.7）

---

## 0. 结论摘要

| 问题 | 结论 |
|---|---|
| 能否不改动 Game MCP 就完成事件记录？ | **能**。102 个 MCP 工具（含截图取证）全部经过 `agent/pawnmind_client.py` 的三个方法，改造点收敛在一处 |
| 事件记录应放在哪一层？ | **Python 客户端层**，不是 Unity C# 层（理由见 ADR-002） |
| 存档身份能否满足规范的 `save_id`？ | **部分**。游戏只提供槽号 + `runID`，没有稳定存档 UUID；且主菜单阶段完全无身份 |
| 中文全文检索能否直接用 FTS5？ | **不能**。实测 2 字中文查询召回为 0（见 §6.2） |
| 现有测试与模拟设施是否够做 P1？ | **够**。已有 96 工具的 fake 游戏、脚本策略与端到端测试 |
| 是否存在文档未提及的高危入口？ | **有**。C# 侧 5 个 `debug_*` op 存在但未暴露给 MCP，必须保持不暴露 |

---

## 1. P0.1 项目结构与基线（已冻结）

### 1.1 仓库构成

```
PawnMind/
├── src/Core/     纯逻辑，无 Unity 依赖（规则、策略、版本门禁、录制槽状态机）
├── src/Bridge/   HTTP、鉴权、rev/action_id、快照、动作路由、各业务服务
├── src/Game/     唯一允许直连/反射游戏类型的层（主线程）
├── test/         C# 纯逻辑 + 对已安装 Assembly-CSharp 的契约测试
├── agent/        Python：MCP stdio 服务、HTTP 客户端、fake 游戏、演示策略、预检
│   └── dsh/      DSH 宿主补丁
└── play/         DSH 会话工作目录（project instructions = 试玩剧本）
```

### 1.2 测试基线（本次实跑）

| 套件 | 命令 | 结果 |
|---|---|---|
| C# 纯逻辑 + 脚本门禁 | `cd PawnMind && REQUIRE_GAME=0 ./test.sh` | `passed=469 failed=0`；`contract gate tests: passed=53 failed=0`；`adapter contract OK` |
| Python | `.venv/bin/python -B -m unittest discover -s agent/tests` | `Ran 81 tests ... OK`（0 skip） |

说明：适配器契约在 DLL 存在时执行（本次 392 项）。`REQUIRE_GAME=0` 允许 DLL 缺失时跳过适配器，本次 DLL 存在故实际执行。

### 1.3 部署与运行事实

- Mod 安装于 `...\Probably Stolen\Mods\PawnMind\`，manifest `0.3.0`。
- 桥只监听 `127.0.0.1:15528`，除 `/health` 外需 Bearer token（`ModData/pawnmind/bridge.conf`）。
- MCP 服务由宿主（DSH）以 **stdio 子进程**方式启动，**每次会话一个新进程** → 任何状态的持久化必须落盘，不能放进程内存。
- 宿主工具已收紧到 0 个（`play-headless.patch.yml`），模型只能看到 `mcp__pawnmind__*`。

---

## 2. P0.2 真实工具清单与 Schema

**取证方式**：以真实 stdio 客户端调用 `tools/list`，不依赖源码推断。

产出（审计附件）：

- [mcp-tools-full.json](audit/tools-inventory.json) — 102 个工具的完整 `input_schema` 快照（P0 初审时为 100，截图取证特性后增加 2 个）
- [mcp-tools-recording.json](audit/tools-inventory.json) — 96 个（录制集）
- [tool-matrix.md](audit/PLAYER_ENTRY_MATRIX.csv) — 逐工具的可读/写/风险/rev/参数/验证状态表

### 2.1 数量与集合差异

| 集合 | 数量 |
|---|---|
| 全部 MCP 工具 | **100** |
| `--tools recording` | **96** |
| 录制集排除 | **6**：`act`、`press`、`get_ui_snapshot`、`wait_until`、`wine_add_water`、`new_game` |

### 2.2 传输通道分类（决定埋点位置）

| 通道 | 工具数 | 说明 |
|---|---|---|
| `POST /action` | **96** | 全部经 `BridgeClient.action()`；含 42 个只读 op 与 54 个改状态 op |
| `GET /state` | 5 | `get_state`、`get_run_summary`、`get_legal_actions`、`wait_for_change`、`wait_until` |
| `GET /events` | 1 | `wait_for_events`（长轮询，最长 25s） |

### 2.3 风险分布

| 级别 | 数量 | 代表 |
|---|---|---|
| `read` | 48 | `get_state`、`list_*`、`*_status` |
| `ui` | 6 | 新局菜单 6 个单步操作 |
| `low` | 20 | 开店、叫客、对话推进、搬运、标签 |
| `medium` | 12 | `confirm_trade`、`bargain`、`buy_upgrade`、`machine_*`、`deliver_request` |
| `high` | 8 | `confirm_new_game`、`save_game`、`load_game`、`save/load_recording_game`、`sleep`、`press`、`new_game` |
| `unknown`（从未真机验证） | 6 | `redeem_ticket`、`deliver_request`、`barter_accept`、`buy_upgrade`、`start_fermentation`、`wine_add_ingredient` |

> `unknown` 含义：代码存在、离线测试通过，但**成功路径从未在真机确认**。记忆系统不得把这类工具的首次失败写成“游戏机制结论”，只能写成 `observation`。

### 2.4 未暴露但存在的高危 op

C# `ActionRouter` 的 dispatch 中另有 5 个 op **不在 MCP 工具表里**：

`debug_pulse`、`debug_dump`、`debug_fill_water`、`debug_grant`、`debug_unlock_expedition`

- 它们直接改钱/造物/解锁，属于作弊入口。
- 当前 MCP 不可达，这是**安全属性**，不是遗漏。
- 记忆系统与任何新增工具都**不得**把它们暴露给模型；P1 的埋点也不应记录它们的调用（若被调用本身就是异常）。

---

## 3. P0.5 写操作的包裹点（关键结论）

### 3.1 为什么是 Python 层

三个候选点：

| 候选 | 覆盖 | 风险 | 结论 |
|---|---|---|---|
| Unity C# `ActionRouter.Dispatch` | 只覆盖 `/action` 的 96 个，且拿不到 `/state` 读取上下文 | 需改游戏内 Mod、涉及主线程与持久化、要重新过 ModLoader 信任 | **不选** |
| MCP Server（`agent/mcp_server.py`） | 100/100 工具 | 无 Unity 依赖，纯 Python | **选** |
| `agent/pawnmind_client.py` 的三个方法 | 100/100 工具，**且只有一个文件** | 同左 | **选（更优）** |

### 3.2 实测的调用收敛

```
client.action(...)      96 个工具  →  POST /action
client.get_state()       5 个工具  →  GET /state
client.wait_events()     1 个工具  →  GET /events
```

除 `agent/fake_game.py`（测试替身）外，`agent/` 下**没有**任何直接 `urllib`/直连 HTTP 的旁路。

因此 P1 只需在 `BridgeClient` 的三个方法上包一层 recorder，即可获得 MCP 侧的 100% 调用覆盖。

### 3.3 幂等键已存在

`BridgeClient.action()` 每次调用自动生成 `action_id = uuid4()`（[pawnmind_client.py](<../agent/pawnmind_client.py>)）。

- 该值同时是服务端幂等缓存键，可直接作为事件的 `idempotency_key`。
- `rev` 由模型显式传入，写入事件时一并记录。
- **注意**：服务端幂等缓存是进程内 1024 条，不跨重启；`action_id` 的持久语义只到事件日志层。

---

## 4. P0.4 游戏身份与作用域可得性

### 4.1 可读取的身份字段

| 来源 | 字段 | 可用性 |
|---|---|---|
| `GET /health` | `game_version`（`VERSION 100-Rev13`）、`game_assembly_mvid`、`expected_*`、`write_gate`、`latest` | 任何时候可读，免 token |
| `get_recording_save_status` | `recording_mode`、`acknowledgment`、`configured_slot`、`next_free_slot`、`configured_slot_occupied`、`bound`、`binding{slot, run_id, run_number, start_type, start_scenario_id}`、`slot_matches`、`run_matches`、`last_save_time`、`error` | 任何时候可读 |
| `get_state`（进游戏后） | `in_game`、`day`、`cash`、`shop_state`、`rent{…}`、`dialogue{…}`、`inventory`、`legal_actions`、`rev` | 仅进游戏后 |

### 4.2 关键限制：主菜单阶段没有存档身份

- 主菜单时 `PlayerStore.HasInstance == false`，`get_recording_save_status` 返回 `bound=false`、`binding=null`。
- 因此 `run_id` **不能在会话开始时确定**，只能在 S4 绑定成功（`confirm_new_game` 后回读）时创建。
- 这会直接影响记忆作用域设计：`memory_bootstrap` 在开局前只能返回“版本级 + 跨局经验”，本局记忆要等绑定后才开放。

### 4.3 规范中的 `save_id` 无法满足

游戏不提供稳定的存档 UUID。可用的只有：

- 槽号（`binding.slot`）
- `PlayerStore.runID`（字符串，进游戏后可读）
- `runNumber`

映射建议：`save_id := slot:<n>`，`run_id := <runID>`，并记录 `parent_run_id`（若发现槽内容被外部替换则需人工确认）。**必须在 ADR 中声明这是近似，不是规范要求的强身份。**

### 4.4 版本锁

`game_version` 与 `MVID` 由 S2 门禁强制，写操作在版本不符时全部拒绝。事件的 `game_version` 字段直接取 `/health` 的实际值，不写死。

---

## 5. 状态字段清单（供 P1 差分使用）

### 5.1 `/state` 顶层字段（源码枚举）

`rev`、`timestamp`、`in_game`、`legal_actions`、`day`、`shop_state`、`outside`、`location`、`afterhour_count`、`cash`、`rent_days_left`、`rent_value`、`starting_day`、`client_arrived`、`back_counter_count`、`back_counter`、`front_counter_count`、`front_counter`、`client_item_count`、`client_items`、`negotiation_open`、`bargain`、`inventory_count`、`inventory`、`can_call_next`、`can_end_day`、`dialogue`、`client`、`negotiated_item`、`run_summary`、`rent`

### 5.2 `rent` 对象（16 字段）

`readable`、`error`、`due`、`rent_value`、`temporary_rent`、`days_left`、`days_until_rent`、`can_pay`、`rent_paid_total`、`rent_weeks_paid`、`rent_weeks_missed`、`starting_rent`、`cash`、`day`、`last_payment`、`receipt_error`

### 5.3 `dialogue` 对象

`readable`、`error`、`available`、`choice`、`ended`、`typing`、`speaker`、`text`、`choices[]`（每项含 `enabled`，由原生 `IsChoiceEnabledAt` 同源）

### 5.4 注意：不是所有变化都能被 `/state` 观察到

`/state` 是**快照**，不是差分流。以下变化只有在下一次读取时才可见，且无法归因：

- 顾客自行到访/离开
- 跨日自动保存
- 租金结算
- 夜间服务结算

→ P1 必须引入 `external_state_change` / `unknown_cause` 事件，且 `capture_quality` 标 `partial`。**不能声称 100% 游戏行为覆盖**；只能声称“本系统发起的动作 100% 覆盖”。

---

## 6. P0.6 特殊行为与坑

### 6.1 异步与写盘

| 行为 | 事实 | 对记忆系统的影响 |
|---|---|---|
| `sleep` | 原生 `OnEndDayButtonClick` 启动 0.5s 黑屏协程后才 `BeginDay()` + `SaveGame()`；立即回读 `day` 常未变 | 事件需允许 `unknown`；不得把“day 未变”判为失败；不得重发 |
| `confirm_new_game` | 触发场景加载 + 原生 `InitialSave()` 真正写盘 | 事件必须记 `dispatched=true` 且结果需回读确认；不可重试 |
| `load_game` | 仅主菜单可调用；触发场景加载 | 读档后 `rent.last_payment` 被清空，属预期 |
| `wait_for_events` | 长轮询最长 25s | 超时不是失败；不得记为动作 |

### 6.2 中文检索（实测）

本机 `sqlite 3.45.1`，FTS5 可用，但：

```
trigram 分词器：查 "第一周"(3字) → 命中；查 "租金"(2字) → 0 条
unicode61：不切分 CJK，整段中文成为一个 token
```

结论：**v1 不使用 FTS5 做中文召回**。改为结构化标签/实体精确过滤 + Python 侧子串匹配（记忆量级为百级，成本可忽略）。若未来超万条再评估 bigram 索引。

### 6.3 其他坑

1. **`get_action_history` 不是权威**：C# 侧是内存环形缓冲 100 条，Mod 重启即丢。事件日志必须独立持久化。
2. **服务端幂等不跨重启**：`ActionGate` 缓存随进程消失，`action_id` 去重只在事件日志层有效。
3. **`list_contacts(include_hidden)`** 仅在 `allow_test_items=true` 且未 armed recording 时展开；录制配置下必然拒绝。
4. **`get_ui_snapshot` 需要 `rev` 却是只读**：设计不一致，录制集已排除，不影响。
5. **MCP 子进程每次会话重建**：标记“本次会话已 bootstrap”这类状态必须落盘。

---

## 7. P0.3 受控模拟与回放设施（现有资产）

| 资产 | 位置 | 能力 |
|---|---|---|
| fake 游戏桥 | [agent/fake_game.py](<../agent/fake_game.py>) | 127.0.0.1 随机端口，实现 96 工具子集：四阶段新局菜单、录制三态、交易原子性、房东对话、跨日、租金回执、存读档绑定 |
| 脚本策略端到端 | [agent/demo_agent.py](<../agent/demo_agent.py>) | 走真实 MCP stdio，19 个 allowlist 工具跑完整首周 |
| 预检脚本 | [agent/preflight.py](<../agent/preflight.py>) | 只读健康检查，退出码 0/1/2 |
| 端到端测试 | `agent/tests/test_demo_agent_e2e.py` 等 | 81 项 Python 测试全绿 |

**P1 可直接复用**：fake 游戏作为“可重放的受控模拟”，事件回放无需启动真实游戏。

**缺口**：fake 游戏不是真实游戏的事件录制，只覆盖已实现语义；未覆盖机器/酿酒/彩票等系统的真实行为。

---

## 8. P0.7 缺口清单（进入 P1 前必须知道）

| # | 缺口 | 影响 | 建议 |
|---|---|---|---|
| G1 | 无事件存储 | 无法构建时间线 | P1 首要任务 |
| G2 | 主菜单无存档身份 | `run_id` 延迟创建 | bootstrap 分两阶段：预绑定 / 已绑定 |
| G3 | 无稳定 `save_id` | 无法强绑定存档 | 用 `slot + runID` 近似，写进 ADR 并在事件中标注 |
| G4 | 游戏内变化不可归因 | 因果判断可能错 | 强制 `external_state_change` / `capture_quality=partial` |
| G5 | 6 个工具从未真机验证 | 经验可能建立在 bug 上 | 首次失败只记 `observation`，不升级为机制 |
| G6 | FTS5 中文不可用 | 检索只能靠标签/子串 | v1 不用 FTS5 |
| G7 | `sleep` 异步 | 事件结果不确定 | 允许 `unknown`，禁止重试 |
| G8 | MCP 进程生命周期短 | 会话状态丢失 | 全部状态落盘 |
| G9 | 94 工具 schema 较大 | 每回合提示词开销 | bootstrap/retrieve 预算必须严格（规范已给 500–1200 / 300–900 token） |

---

## 9. 对规范 P0 验收的逐条自检

| 验收项 | 状态 |
|---|---|
| 完整真实工具清单及 schema 快照，不含虚构工具名 | ✅ `mcp-tools-full.json` / `mcp-tools-recording.json` / `tool-matrix.md` |
| 现有测试运行结果有日志 | ✅ §1.2，命令与计数 |
| 每种高风险动作都标注是否可安全测试 | ✅ `tool-matrix.md` 的 `risk` 与 `verified` 列 |
| 至少一种可运行的受控模拟工具调用/回放 | ✅ `agent/fake_game.py` + 81 项测试 |
| 未知项记录为 TODO，不伪造“支持” | ✅ §8 九项缺口 |

**P0 出口条件达成**：可以据此进入 P1，无需再猜任何工具名或参数。

---

## 10. 审计期间未做的事

- 未启动游戏（审计时游戏已关闭，`/health` 连接被拒是预期）
- 未执行任何写动作
- 未读写任何存档文件，未访问 `SaveFile.es3`
- 未修改任何生产代码或测试
- 未引入新依赖

新增文件仅限 `audit/` 下的四份取证材料与本文档。
