# HANDOFF —— PawnMind 长期记忆系统（给接手的工程师）

> 写于 2026-11-15，仓库 commit `8bdf393`。
> 本文假设你**没有**参与之前的对话，也不了解这个游戏或这个仓库。读完你应该能：独立跑测试、看懂已完成的部分、知道哪些坑不能踩、并直接开始 P2。

---

## 1. 这是个什么项目

《深空当铺：可能是偷的》(Probably Stolen) 是一个太空站当铺模拟经营游戏。我们在给它做一个本地 Mod（**PawnMind**），把游戏状态和操作暴露成：

```
Unity 游戏 (C# Mod)
   │  反射/强类型适配，只在主线程
   ▼
PawnMind.dll ── HTTP 127.0.0.1:15528 ── Python MCP stdio 服务 ── AI 宿主 (DSH)
                  Bearer token / rev / action_id          opencode-go / deepseek-v4.1-flash
```

最终目标是**让 AI 自己玩一局**并录成视频：从新游戏菜单开始，自选难度/特质，经营一周，交上第一周租金，然后存读档核对。

长期记忆系统是这个目标下的一个子项目：让 AI 在多次会话、多次周目之间积累经验，而不是每次从零开始。

**记忆项目的设计规范**在 [Probably_Stolen_Agent_Memory_Architecture_v1.md](<Probably_Stolen_Agent_Memory_Architecture_v1.md>)（1172 行，别人写的，质量很高）。我们的实现是它的裁剪版，偏离之处全部记录在 [ADR-001-memory-architecture.md](<ADR-001-memory-architecture.md>)。

---

## 2. 现在到哪一步了

规范把工作分成 P0–P5 六个阶段。**当前 P0、P1 已完成，P2 未开始。**

| 阶段 | 内容 | 状态 |
|---|---|---|
| **P0** | 审计现有 MCP 能力，产出能力清单与架构决策 | ✅ 完成并通过用户复审（有 6 项修订） |
| **P1** | 可靠事件记录（事件库、recorder、快照差分、时间线、未知结果核对、历史查询） | ✅ 完成 |
| **P2** | 记忆存储与检索（手工种子） | ⬜ 未开始，**这是你的起点** |
| P3 | 反思触发、LLM 提炼候选、规则校验、冲突治理 | ⬜ |
| P4 | 决策闭环/目标树 | ⬜ 建议不做（见 §9） |
| P5 | A/B/C 三组评测 | ⬜ |

### 测试基线（真实跑过，不是估计）

```bash
cd PawnMind
./test.sh                                              # C# 502 + 脚本门禁 53 + 适配器契约 392
.venv/bin/python -B -m unittest discover -s agent/tests  # Python 139
```

全部 0 fail、0 skip。其中记忆相关 45 项（store 19 / recorder 15 / CLI 8 / wiring 3）。

### 关键文档

| 文档 | 作用 |
|---|---|
| [CAPABILITY_AUDIT.md](<CAPABILITY_AUDIT.md>) | P0 取证：真实工具清单、传输通道、游戏身份、特殊行为、缺口 |
| [audit/tool-matrix.md](<audit/tool-matrix.md>) | 102 个工具逐个的风险等级与真机证据两列 |
| [ADR-001-memory-architecture.md](<ADR-001-memory-architecture.md>) | 12 条架构决策（**先读这个**） |
| [P0_REMEDIATION.md](<P0_REMEDIATION.md>) | P0 复审发现的漏洞与修复证据 |
| [P1_REPORT.md](<P1_REPORT.md>) | P1 交付、测试、已知限制 |

---

## 3. 已完成的东西长什么样

### 3.1 代码地图

```
PawnMind/agent/
├── mcp_server.py            102 个 MCP 工具；--tools {full,recording}；--memory {on,off}
├── pawnmind_client.py       HTTP 客户端（唯一出口：action / get_state / wait_events）
├── memory/
│   ├── store.py      (514行) SQLite 事件库：timelines / snapshots / events
│   ├── recorder.py   (331行) RecordingClient：包装上面三个方法，写历史
│   ├── classification.py(96行) 只读/异步/写盘/读档的语义分类
│   └── cli.py        (183行) memory_history 的 CLI：history/timelines/status/reconcile
├── fake_game.py             127.0.0.1 假游戏（96 工具），测试与演示用
├── demo_agent.py            脚本策略，走真 MCP stdio 跑完整首周
└── dsh/pawnmind.patch.yml   DSH 宿主补丁（MCP + 模型 + --memory on）
```

C# 侧（记忆项目**不碰**，只在 P0 修过一个安全漏洞）：`src/Core` 纯逻辑 / `src/Bridge` HTTP 与路由 / `src/Game` 唯一允许接触游戏类型的层。

### 3.2 P1 的数据模型

```sql
timelines(timeline_id, agent_session_id, run_id, save_id, slot, run_number,
          parent_timeline_id, reason, game_version, game_mvid, instance_id, created_at)

snapshots(snapshot_id, timeline_id, state_json, state_hash, delta_json,
          game_day, rev, completeness, observed_at)

events(event_id, timeline_id, seq, action_id, event_type, tool_name,
       args_json, result_status, result_json, before_snapshot_id,
       after_snapshot_id, delta_json, capture_quality, game_day, rev, occurred_at,
       UNIQUE(timeline_id, seq))
```

- 一条写动作 = **一行** `intent`（`result_status=pending`），结果 `UPDATE` 回同一行。
- `event_type`：`intent` / `observation` / `external_state_change` / `sleep_completed` / `sleep_unresolved`。
- 库默认在 `PawnMind/play/memory/memory.db`（已 gitignore）。

### 3.3 P1 的核心不变量（改动时必须保住）

1. **intent 先落盘再 dispatch**：history 写不进去 → 动作根本不发（fail closed）。
2. **未知结果不重试**：异常 → 记 `unknown`，同一 `action_id` 不重放。
3. **进程重启后 pending 一律转 unknown**（`reconcile_pending`），幂等。
4. **`sleep` 只有观察到 `day` 增加才算完成**；未跨日 → `sleep_unresolved`，之后拒绝再 `sleep`。
5. **分类由语义表决定，不按 HTTP 方法**：`POST /action` 里同时有 42 个只读 op 和 54 个写 op。
6. **timeline 隔离**：换局 / 读档 / 槽内容被替换 → 新 timeline，旧的永久保留。

这六条都有对应测试，改代码时跑 `agent/tests/test_memory_*.py` 会立刻发现回归。

---

## 4. 必须先知道的 10 个事实（都是踩过的坑）

1. **`act` 曾经能直达 `debug_*` 作弊入口**。`act` 会递归 dispatch 任意 op，而录制门禁只保护 8 个 op。已在 P0 修复：`act` 永不可达 5 个 `debug_*`；录制态直接调用也拒。**不要再打开这个口子。**
2. **游戏没有稳定的存档 UUID**。只有槽号 + `PlayerStore.runID`。所以 `save_id` 是 `slot:<n>` 的近似，`timeline_id` 是我们自己生成的。不要假装它是游戏事实。
3. **主菜单阶段没有任何存档身份**（`PlayerStore.HasInstance == false`）。所以 `run_id` 只能在开局绑定后才有——`memory_bootstrap` 必须支持“绑定前/绑定后”两阶段。
4. **SQLite FTS5 在中文上不可用**。实测：`trigram` 要求 ≥3 字符，查「租金」返回 0 条；`unicode61` 不切分 CJK。**P2 不要用 FTS5**，用标签 + 子串（记忆量是百级，Python 扫描是微秒级）。
5. **不要按 HTTP 方法判断读写**。见 §3.3 第 5 条。
6. **`sleep` 是异步的**：原生 0.5 秒黑屏协程之后才 `BeginDay()` + `SaveGame()`。立即回读 `day` 通常没变，这**不是失败**。
7. **游戏自己会发生很多事**（顾客到访、自动存档、租金结算），MCP 看不到归因。只能记 `external_state_change`，`capture_quality=partial`，**禁止归因**给上一条动作。
8. **6 个工具从未在真机验证成功路径**：`redeem_ticket`、`deliver_request`、`barter_accept`、`buy_upgrade`、`start_fermentation`、`wine_add_ingredient`。它们的失败可能只是 bug，不能写成“游戏机制”。
9. **MCP 服务是短生命周期的**：DSH 每次会话 spawn 一个新进程。所有状态必须落盘，内存只能做缓存。
10. **服务端幂等缓存只在进程内**（1024 条），不跨重启。真正的去重责任在事件日志层。

---

## 5. 你接手后要做的 P2

### 5.1 目标

让 AI 能“记住并想起来”：存得对、找得到、隔离得住。**P2 不写自动提炼**——记忆由人工/脚本灌入，先证明存储与检索正确。

### 5.2 任务（按依赖顺序）

**P2.1 表结构**
新增 `memories` / `memory_evidence` / `memory_revisions`（schema 见规范 §5 的 `memories` 部分），加 `memories_fts` **暂不建**。迁移沿用 `store.py` 的 `meta.schema_version` 机制，版本升到 2，并保证 v1 库能就地升级（写测试）。

**P2.2 状态机与校验**
`proposed → hypothesis → verified → superseded`，外加 `rejected` / `stale`。校验规则见 ADR-005，核心是**两件事分开**：

- **Evidence Integrity**：引用的 `event_id` 是否存在、是否同 `timeline_id`、`capture_quality` 是否够；
- **Claim Support**：这些证据**够不够**支持这个结论。

`fact`/`price_observation` 1 条完整证据可 `verified`；`mechanism`/`lesson`/`strategy` **最多 `hypothesis`**，除非有受控对比或官方/游戏内文本来源。`conditions` 为空的机制类记忆强制 `hypothesis`。

**P2.3 四个工具**

| 工具 | 语义 |
|---|---|
| `memory_bootstrap` | 开局必读；绑定前只给版本级经验，绑定后加本 run |
| `memory_retrieve` | 按情景检索，带 token 预算，返回必须附 `memory_id`/`status`/证据摘要/适用条件 |
| `memory_get` | 取单条 + 证据 |
| `memory_status` | 计数、冲突数、丢事件数、检索次数 |

建议**单独做成一个 MCP 服务**（`mcp__memory__*`），不要塞进游戏 MCP——理由见 ADR-001/ADR-002。

**P2.4 手工种子**
灌 4 类记忆各至少 1 条，作为检索评测的靶子：一条已验证规则、一条错误规则（应被拒或降级）、一对互相冲突、一条带 `conditions` 的受限规则。全部标 `source=seed`。

**P2.5 评测集**
30 条“情景 → 应召回哪条”的标注（中文、英文字段名、物品 id、跨 timeline、版本不符）。门槛 `Recall@5 ≥ 0.90`、`Precision@5 ≥ 0.70`，并单独报高风险警告的漏召回率。

**P2.6 预算与日志**
`bootstrap` 500–1200 token、普通决策 300–900、重大风险 ≤1500；检索日志记录 query/命中/耗时/token，供 P5 分析。

### 5.3 验收（规范 §12 Phase 2，C-RET-001..007）

1. 有一个失败警告在匹配决策中进入 top-k；
2. 条件不匹配的规则不作为适用策略返回；
3. `rejected` / `superseded` 不出现在默认建议里；
4. 不泄漏另一个 run 的现金/库存/剧情进度；
5. 中文、英文、物品 id、NPC id 各有可解释样例；
6. 总 token 不超过预算，超长时保留最高优先级警告；
7. 同一数据库快照下，检索可重放出同一排序。

### 5.4 P2 不要做的事

- 不要加 FTS5（§4 第 4 条）；
- 不要加第二个 LLM 做校验（ADR-005：规则足够，且可复现）；
- 不要让模型自己把记忆标成 `verified`；
- 不要写自动提炼（那是 P3）；
- 不要引入向量库、图数据库、embedding（ADR-007）。

---

## 6. 怎么跑、怎么验

```bash
# 全量
cd PawnMind && ./test.sh
cd PawnMind && .venv/bin/python -B -m unittest discover -s agent/tests

# 只跑记忆相关
cd PawnMind && .venv/bin/python -B -m unittest agent.tests.test_memory_store \
    agent.tests.test_memory_recorder agent.tests.test_memory_history \
    agent.tests.test_memory_wiring -v

# 看历史（不需要游戏）
cd PawnMind && .venv/bin/python -m agent.memory.cli --db play/memory/memory.db status
cd PawnMind && .venv/bin/python -m agent.memory.cli --db play/memory/memory.db \
    --format json history --limit 20

# 用假游戏跑一次带记录的 MCP（不碰真游戏）
# 见 agent/tests/test_memory_wiring.py 的写法：FakeGame() + StdioServerParameters
```

**红线**：任何测试都不许启动真实游戏、不许读写真实存档、不许调真实 `/action`。假游戏（`agent/fake_game.py`，127.0.0.1 随机端口）足够覆盖。

---

## 7. 仓库约定（必须遵守）

来自根 `AGENTS.md` 与 `PawnMind/AGENTS.md`：

- **TDD 铁律**：先写失败测试 → 跑出 Red → 最小实现 → 全绿 → 重构。
- `src/Core/` 不许 `using UnityEngine` 或游戏类型；`src/Game/` 是唯一能碰游戏类型的层。
- 游戏对象只能在 Unity 主线程访问；HTTP 线程只入队。
- 不读隐藏字段（鉴定答案、隐藏顾客身份、真实水质）。
- 不调用 `AddCredit`、不碰 ModLoader/信任/Steam 写接口。
- 逆向产物只能放根目录 `_decomp/`，不进提交。
- 提交前跑完整测试，`git diff --check` 必须干净。

---

## 8. 当前仓库状态

```
commit 8bdf393  docs: refresh capability audit counts after screenshot feature
commit 239c277  feat(pawnmind): P0 remediation and P1 reliable interaction history
commit 8f64166  feat(pawnmind): screenshot evidence capture and debug bundle
```

工作树干净。102 个 MCP 工具（录制集 96）；manifest 0.3.0；游戏装在 Steam 默认目录，Mod 已安装；**游戏当前是关闭的**。

与录像相关的部分（不属于你的 P2，但别破坏）：`play/AGENTS.md` 是给 AI 的试玩剧本，`agent/dsh/pawnmind.patch.yml` 是宿主补丁（已开 `--memory on`）。

---

## 9. 需要你判断或跟用户确认的事

1. **P4 要不要做**。规范要求实现目标树与独立决策循环，但 `play/AGENTS.md` 已经是决策循环，再写一个会出现“两套规则打架”。ADR-006 建议不做，等 P5 显示“未检索决策率”过高再说。
2. **P2 的评测集从哪来**。30 条标注需要真实情景；可以先用假游戏造，但真实游戏里才有“顾客到店要买水”这种语境。建议跟用户确认能否在录像后补充真实样例。
3. **`memory_history` 是否要给模型用**。P1 只有 CLI；P2 若做成 MCP 工具，模型就能自己查历史——这会影响 token 预算和工具面（录制集目前 96 个）。

---

## 10. 一页速查

| 我想… | 去看 |
|---|---|
| 知道有哪些工具 | `audit/tool-matrix.md` |
| 知道为什么这么设计 | `ADR-001-memory-architecture.md` |
| 知道 P0 发现了什么漏洞 | `P0_REMEDIATION.md` §1 |
| 知道 P1 做了什么、限制是什么 | `P1_REPORT.md` §3 / §5 |
| 改 recorder 前 | `PawnMind/agent/tests/test_memory_recorder.py`（15 条不变量） |
| 改 store 前 | `PawnMind/agent/tests/test_memory_store.py`（19 条） |
| 加 MCP 工具前 | `PawnMind/agent/tests/test_write_rev_required.py`（写工具必须带 rev） |
| 确认没把读写分类搞错 | `test_read_classification_matches_csharp_allowlist`（与 C# 白名单逐项比对） |

---

有问题先看 ADR，再看对应测试；两者冲突时**以测试为准**并回来改 ADR。
