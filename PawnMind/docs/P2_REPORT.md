# P2 第二半报告：记忆接入 MCP 与检索评测

- 基线：`f76dd50`（P2 第一半，存储层）
- 本报告对应的工作树改动**未提交**
- 测试数据：`PawnMind/agent/memory/seeds/verified_rules.json` 与
  `retrieval_cases.json`，两者都标了 `test_data=true`。它们是人工种子，
  **不是真实录制，也不是真实存档观察**。
- 本任务期间 `PawnMind/play/` 只改了 `AGENTS.md`；没有碰
  `play/memory/*.db`、游戏桥（127.0.0.1:15528）、`dist/`、`build.sh` 或任何真实存档。
  所有测试数据库都在临时目录里。

## 0. 结论速览

### 0.1 新增 / 修改文件

新增：

| 文件 | 作用 |
|---|---|
| `PawnMind/agent/memory/retrieval_eval.py` | 30 条情景的可复现评测 CLI（Recall@k / Precision@k / 高风险警告漏召回 / 逐条 PASS） |
| `PawnMind/agent/tests/test_memory_store_v3.py` | schema v2→v3 就地迁移、证据角色、`add_evidence`、contradict 计数、events 只读检查 |
| `PawnMind/agent/tests/test_memory_retrieval.py` | 30 条情景断言、state 条件过滤、评测口径、四个 MCP 工具端到端、只读分类 |
| `P2_REPORT.md` | 本报告 |

修改：

| 文件 | 作用 |
|---|---|
| `PawnMind/agent/memory/memories.py` | schema v3；`evidence_role` / `added_at`；`add_evidence`；`retrieve`；state 过滤；`recall_reason`；contradict 计数；`events_status` / `event_details` |
| `PawnMind/agent/mcp_server.py` | `--memories-db`；四个只读工具；惰性打开 memories DB；`--tools recording` 注册；计数说明 |
| `PawnMind/agent/memory/classification.py` | 四个工具加入 `MCP_STATE_READS`（→ `READ_ONLY_OPS`），不改 C# 契约集合 |
| `PawnMind/agent/tests/test_mcp_tools_mode.py` | recording 100→104、full 110→114；四个工具参数面/无 `rev` 断言 |
| `PawnMind/agent/tests/test_memory_store_v2.py` | schema 3、`fulltext`/`keyword` 命名、candidate/conflict 默认不可检索 |
| `PawnMind/play/AGENTS.md` | 开头计数 104；新增「记忆检索」工具行；说明 full 多出的 10 个工具 |
| `AGENTS.md`、`PawnMind/AGENTS.md` | 仅同步工具计数 114/104（纯文档） |

### 0.2 Red：先写测试时的失败原因

第一批新测试在实现前运行，失败在**缺失的 API 与模块**上，符合预期：

```text
ImportError: cannot import name 'EVIDENCE_ROLES' from 'agent.memory.memories'
ModuleNotFoundError: No module named 'agent.memory.retrieval_eval'
```

实现初版后，测试继续红，暴露出真正的行为问题（这些都已修）：

1. `NameError: _query_tokens`：删除旧的 `_like_patterns` 时误删了
   `_query_tokens` / `_build_fts_expression`，检索直接不可用。
2. state 路径 `rent_days_left_max` 解析不到嵌套的
   `{"rent": {"days_left": 1}}`；修法是按下划线逐段尝试嵌套路径，并保留扁平键。
3. `zh_inventory_without_state` 召回 0 条：`superseded` 行的
   `requires_any: ["库存", "进货"]` 把分母撑到 2，inv_capacity 只覆盖 1/2。
   把 coverage 门槛定为 0.5 后恢复。
4. 三个误召回：
   - `mem_seed_run_private` 因正文含「库存占用 0.92」被 FTS 命中，
     在库存查询里冒充规则；
   - `mem_seed_trade_getstate` 因正文含 `confirm_trade` 被
     `en_never_purchase` 的 FTS 命中；
   - `BETA-9` 的裸数字前缀 `"9"*` 命中 `90%` / `0.92`。
   修法：**声明了 `conditions.requires_any` 的记忆必须先命中其中一个义务词**，
   FTS 只能加分，不能单独放行；纯数字/单字符 token 不算强 FTS。
5. `deprecated_rule_hidden` 期望空集，但 inv_capacity 只靠一个「货架」
   就想顶上来。修法：用「查询概念覆盖率」作为精度闸门（见 §3.4）。
6. 旧测试 `search` 返回了 candidate/conflict 行（第一半只排除了
   deprecated/superseded）；按本任务「默认只返回 verified」改成硬过滤，
   并同步了两条旧测试的断言。

### 0.3 Green：实测数字

| 套件 | 结果 |
|---|---|
| `.venv/bin/python -B -m unittest discover -s agent/tests` | **234 tests，OK**（0 fail；新增 37 条：16 + 19 + tools-mode 2） |
| `./test.sh` | **exit 0**；C# `passed=651 failed=0`；脚本门禁 `passed=53 failed=0`；适配器契约 `contract checks: 490` |
| 双库 CLI `status` | 正常：memories=15、`memory_schema_version=3`、events=20、pending=0 |

### 0.4 Recall@5 / Precision@5（30 条人工种子）

```text
passed=30/30 recall@5=1.0000 precision@5=1.0000 negative=9/9 warning_miss=0/15
total_gold=26 total_returned=26 mean_returned=0.87
```

**重要限定**：这 30 条也是调参（coverage 门槛、`requires_any` 优先）所用的集合，
没有留出集。1.0 表示「实现与这批标注一致」，**不等于**在真实录制上泛化。
P3/P4 应另建留出集再报告。

### 0.5 实现与设计不符处（详见 §7）

1. `memory_evidence` 主键仍是 `(memory_id, event_id)`，不是 P3 的
   `(memory_id, event_id, evidence_role)`。同一事件不能同时是
   support 和 contradict；尝试改角色会返回 `evidence_role_conflict`，不静默覆盖。
2. `memory_revisions` 只有 `changed_fields_json`，没有 P3 的 `old_json` /
   `new_json` / `job_id`。能审计「改了哪些字段」，不能还原旧正文。
3. `memories` 缺 P3 要求的 `created_by_job_id`、`last_seen_at`、`source_type`、
   `source_json`、`outcome_polarity`、`competing_explanations_json`、
   `verification_plan`、`verified_by`、`verified_at`、`stale_reason`、`tainted`、
   `revalidate_after`。
4. 状态词表是 `candidate/verified/conflict/deprecated/superseded`，没有 P3 的
   `proposed/hypothesis/stale/rejected`；检索把 `proposed` 当作隐藏状态一并过滤。
5. `verify` 接受 `reported` 证据（P3 的 `fact/warning/npc_profile` 要求
   `observed`）；这是第一半为绕开 P1 `capture_quality=partial` 的取舍，本半未改。
6. 增加的 `memory_bootstrap(scope=...)` 参数不在任务给的签名里。没有 scope
   就无法做「作用域匹配」；省略时只返回 `save_id/run_id` 均为空的全局记忆，
   避免泄漏别的 run。
7. `memory_status` 的 events 段只在显式传了 `--memory-db`（或 `--memory on`）时
   配置；不会去猜默认 events 库是否存在。

---

## 1. 检索实现

### 1.1 三个召回通道

| `recall_reason` | 来源 | 说明 |
|---|---|---|
| `fulltext` | FTS5 `MATCH` | ASCII 用完整 token 前缀；中文用前两个汉字前缀（unicode61 把整段中文当一个 token）。强命中要求有意义的 token 真的出现在正文里，裸数字不算 |
| `entity` | `scope_extra.entities` | 英文查询靠这类标签命中中文规则（`cash`/`rent` → 现金/租金）；必须同时命中 `conditions.requires_any` 才会成为候选 |
| `keyword` | 全 token 子串兜底 | 只在没有任何概念词命中时使用，例如查询「要继续进货」而正文是「……不要继续进货……」。旧的 2-gram 子串兜底会引入「不要」这类噪声，已删除 |

`agent/memory/memories.py::retrieve()` 返回：

```json
{
  "items": [...],
  "candidates": 2,
  "state_filtered": 0,
  "token_estimate": 37,
  "token_budget": 900,
  "truncated": false,
  "dropped": 0,
  "concepts": ["库存"]
}
```

### 1.2 硬过滤（先过滤，后打分）

1. **状态**：只允许 `verified`。`proposed`/`candidate`/`conflict`/`deprecated`/
   `superseded` 一律不返回。`proposed` 是 P3 词表，提前放进黑名单。
2. **有效期**：`valid_from <= now` 且（`valid_until IS NULL` 或 `> now`）。
3. **作用域**：`game_id` / `game_version` 精确匹配；run 作用域要求
   save+run+timeline 同时匹配；save 作用域要求 save 匹配；`scope=None`
   只返回全局行。
4. **state 条件**：`scope_extra.conditions.state` 必须被调用方 state 满足。
   没传 state、缺字段、类型不符、未知算子 → **不适用**（fail closed）。

### 1.3 state 条件语法

支持两种写法，路径可以是嵌套的 `rent.days_left`，也可以是扁平的
`rent_days_left`：

```json
{"conditions": {"state": {
  "in_game": true,
  "rent.days_left": {"max": 1},
  "inventory_occupancy_min": 0.9
}}}
```

- 键后缀：`_min` / `_max` / `_eq` / `_ne` / `_lt` / `_lte` / `_gt` / `_gte` /
  `_in` / `_contains`。
- 值也可以是算子字典：`{"rent.days_left": {"max": 1}}`。
- `True` 不会当成 `1`；`bool` 与数字比较一律不匹配。
- 种子文件里的 `inventory_occupancy_min: 0.9` 就是这条路径的实测样例。

### 1.4 精度闸门：查询概念覆盖率

`conditions.requires_any` 是「这条记忆何时适用」的权威词表，`entities` 只是
描述标签。候选必须先命中至少一个 `requires_any` 词；同时：

```text
coverage = 命中的 requires_any 词数 / 查询命中的全部概念词数
coverage >= 0.5 才放行
```

概念词分母取自**全库**（含隐藏状态），不是只看 verified：这样
「每天结束前要清空货架吗？」会同时命中 deprecated 规则的
`货架/结束/清空`，inv_capacity 只覆盖 `货架`（1/3），不会顶上来冒充答案。
这条规则是 P2 的精度取舍，不是 P3 设计里的字段。

### 1.5 排序、预算、重放

- 排序：`score`（keyword/entity/fulltext/coverage）→ `confidence` →
  `hit_count` → `last_used_at` → `created_at` → `memory_id`，完全确定性。
- 预算：`token_budget` 默认 900；按排序装填，**第一条永远保留**（P2.6
  「超长时保留最高优先级警告」），后续放不下就丢弃并返回
  `truncated/dropped`。种子里的预算案例是 30。
- 命中后更新 `hit_count` / `last_used_at`；`search()` 是 `retrieve()` 的列表视图，
  旧调用不变。

---

## 2. 存储层 v3

### 2.1 schema 与迁移

`SCHEMA_VERSION` / `MEMORY_SCHEMA_VERSION` 从 2 升到 3。
`memory_evidence` 新增：

```sql
evidence_role TEXT NOT NULL DEFAULT 'support'
  CHECK (evidence_role IN ('support','contradict','context')),
added_at TEXT NOT NULL DEFAULT ''
```

迁移是**加列**，不重建表：

1. 读 `meta.schema_version`；接受 1/2/3，其它值仍拒绝。
2. `executescript` 创建缺失的表/索引/FTS/触发器。
3. `PRAGMA table_info(memory_evidence)` 检查列，缺哪列 `ALTER TABLE ... ADD COLUMN`。
4. 旧行的 `added_at` 回填为该记忆的 `created_at`（不是真实证据时间，见 §7）。
5. 重建 FTS，更新 `meta.schema_version=3`。

实测覆盖：v2 文件 → 打开 → 行/证据/revision 全在、角色默认 `support`、
`added_at` 非空；再次打开列数不变（幂等）；`schema_version=99` 仍拒绝。

### 2.2 证据角色

- `add_candidate(...)` 的 evidence 现在接受字符串、`{"event_id": ..., "role": ...}`
  或旧式 `{"event_id":..., "weight":..., "evidence_grade":...}`；
  也接受 `evidence_role` 作为别名。默认 `support`。
- 新公开方法 `MemoryStore.add_evidence(memory_id, evidence, actor=..., reason=...)`：
  - 只追加；已存在的事件是 no-op（幂等，不写 revision）；
  - 缺事件/缺库 → `no_evidence`（带 `missing_event_ids`）；
  - 同一事件改角色 → `evidence_role_conflict`，不覆盖；
  - 真正插入时更新 `updated_at` 并写一条 `changed_fields=["evidence"]` 的 revision。
- `verify()` 现在把 `contradict` 证据当作阻断项，返回
  `unverifiable / contradictory_evidence: ...`。已经 verified 的记忆后来追加
  contradict 不会自动降级——P3 的 `verified → stale` 状态机不在 P2 范围内，
  P2 只暴露 `contradict_count`。

### 2.3 计数与只读检查

- `get_memory()` 返回 `support_count` / `contradict_count` / `context_count`；
  `search`/`bootstrap` 的 SQL 也用子查询带这三个计数。
- `bootstrap()` 的 `why` 变成：
  `verified confidence=0.90 hits=0 evidence=1 contradict=0`。
- `MemoryStore.event_details(event_ids)`：只读 ATTACH events 库，返回
  event_type/tool_name/result_status/capture_quality/game_day/rev/occurred_at。
- `MemoryStore.events_status()`：只读 ATTACH，返回 schema_version、timelines、
  events、by_result_status、by_event_type、last_event_at；未配置/文件缺失/
  不是 events 库时返回 `configured=false` + `reason`，不抛异常。

---

## 3. MCP 接线

- `mcp_server.py` 新增 `--memories-db`，默认
  `<PawnMind>/play/memory/memories.db`；可用 `PAWNMIND_MEMORIES_DB` 覆盖。
- `build_server(client, tool_set, memory_provider=None)`：memory store **惰性打开**。
  只列工具（`tools/list`）不会创建 `play/memory/memories.db`；只有真正调用四个
  工具才打开。测试因此可以只传 `--memories-db <temp>`。
- events 库来源：`--memory-db`（显式）或 `--memory on` 时的默认 events 库；
  两者都没有时 `memory_status.events.configured=false`。
- 工具注册：
  - full：110 + 4 = **114**
  - recording：100 + 4 = **104**
- 四个工具都**无 `rev`**，都只读；`test_mcp_tools_mode.py` 断言了参数面与
  required 集合，`test_memory_retrieval.py` 断言了 recording 模式下无 bridge
  也能调用成功。
- `classification.py`：加入 `MCP_STATE_READS`，从而进入 `READ_ONLY_OPS`，
  但**没有**加入 `READ_ONLY_ACTION_OPS`，所以和 C# `ReadOnlyOps` 的契约测试
  仍然通过。

### 3.1 返回样例（脱敏，来自临时双库）

`memory_bootstrap`（截取第一条）：

```json
{
  "ok": true,
  "count": 3,
  "scope": {"game_id": "probably_stolen", "game_version": "VERSION 100-Rev13",
            "save_id": "slot:40", "run_id": "run-seed-a",
            "timeline_id": "tl_seed_main"},
  "memories": [{
    "memory_id": "mem_seed_cash_rent",
    "kind": "warning",
    "text": "测试数据：现金不足以支付本周租金时不要进货，先卖库存或取消订单。",
    "status": "verified",
    "confidence": 0.9,
    "why": "verified confidence=0.90 hits=0 evidence=1 contradict=0",
    "evidence_count": 1,
    "contradict_count": 0,
    "scope": {"game_id": "probably_stolen",
              "game_version": "VERSION 100-Rev13",
              "save_id": null, "run_id": null,
              "timeline_id": "tl_seed_main"}
  }]
}
```

`memory_retrieve`（state=0.95，两条规则都适用；截取第一条）：

```json
{
  "ok": true,
  "query": "库存快满了，还要继续进货吗？",
  "count": 2,
  "memories": [{
    "memory_id": "mem_seed_inv_capacity",
    "kind": "warning",
    "text": "测试数据：库存占用超过九成时不要继续进货，先卖出或整理货架，否则新货可能放不下。",
    "status": "verified",
    "confidence": 0.9,
    "recall_reason": "fulltext",
    "matched_terms": ["库存"],
    "conditions": {"requires_any": ["库存", "inventory", "capacity", "货架"]},
    "entities": ["inventory", "库存", "capacity", "货架"],
    "evidence_count": 1,
    "contradict_count": 0,
    "why": "fulltext match confidence=0.90 evidence=1 contradict=0"
  }],
  "candidates": 2,
  "state_filtered": 0,
  "token_estimate": 37,
  "token_budget": 900,
  "truncated": false,
  "dropped": 0
}
```

`memory_get`（截取）：

```json
{
  "ok": true,
  "memory": {
    "memory_id": "mem_seed_cash_rent",
    "kind": "warning",
    "status": "verified",
    "text": "测试数据：现金不足以支付本周租金时不要进货，先卖库存或取消订单。",
    "confidence": 0.9,
    "evidence": [{
      "event_id": "ev_seed_rent_1",
      "weight": 1.0,
      "evidence_grade": "observed",
      "evidence_role": "support",
      "added_at": "2026-11-16T17:11:13Z",
      "event": {"timeline_id": "tl_seed_main", "event_type": "intent",
                "tool_name": "confirm_trade", "result_status": "rejected",
                "capture_quality": "complete", "game_day": 5}
    }],
    "evidence_count": 1,
    "support_count": 1,
    "contradict_count": 0,
    "context_count": 0
  },
  "revisions": [
    {"revision": 1, "reason": "seed_import", "actor": "seed",
     "changed_fields": ["created"]},
    {"revision": 2, "reason": "verify", "actor": "seed",
     "changed_fields": ["status"]}
  ]
}
```

`memory_status`（截取；路径为临时目录）：

```json
{
  "ok": true,
  "memories": {
    "db": "<temp>/memories.db",
    "events_db": "<temp>/events.db",
    "events_db_available": true,
    "schema_version": 3,
    "memories": 15,
    "by_status": {"candidate": 1, "conflict": 2, "deprecated": 1,
                  "superseded": 1, "verified": 10},
    "by_kind": {"fact": 1, "mechanism": 2, "npc_profile": 1,
                "price_observation": 1, "procedure": 2, "strategy": 3,
                "warning": 5},
    "evidence": 20,
    "evidence_by_role": {"support": 20},
    "contradict": 0
  },
  "events": {
    "configured": true,
    "path": "<temp>/events.db",
    "available": true,
    "schema_version": 1,
    "timelines": 3,
    "events": 20,
    "by_result_status": {"rejected": 5, "success": 15},
    "by_event_type": {"intent": 5, "observation": 15},
    "last_event_at": "2026-11-16T17:11:13Z"
  }
}
```

---

## 4. 30 个检索情景

### 4.1 通过情况（30/30）

| # | case | 类别 | 前置 | 期望 | 结果 |
|---|---|---|---|---|---|
| 1 | `zh_inventory_both_rules` | chinese | state=0.95 | inv_capacity, inventory_condition | PASS |
| 2 | `zh_inventory_without_state` | chinese | — | inv_capacity | PASS |
| 3 | `zh_inventory_condition_high` | chinese | state=0.95 | inv_capacity, inventory_condition | PASS |
| 4 | `zh_inventory_condition_low` | chinese | state=0.3 | inv_capacity | PASS |
| 5 | `zh_empty_gibberish` | empty | — | — | PASS |
| 6 | `zh_cash_rent` | chinese | — | cash_rent | PASS |
| 7 | `zh_rent_only` | chinese | — | cash_rent | PASS |
| 8 | `en_cash_rent` | english | — | cash_rent, english_warning | PASS |
| 9 | `en_never_purchase` | english | — | cash_rent, english_warning | PASS |
| 10 | `en_rent_only` | english | — | cash_rent, english_warning | PASS |
| 11 | `zh_trade_rev` | chinese | — | trade_getstate | PASS |
| 12 | `en_trade_rev` | english | — | trade_getstate | PASS |
| 13 | `zh_contraband` | chinese | — | contraband_check | PASS |
| 14 | `en_contraband` | english | — | contraband_check | PASS |
| 15 | `zh_water_wholesale` | entity | — | water_price | PASS |
| 16 | `zh_water_entity_only` | entity | — | water_price | PASS |
| 17 | `en_water_entity` | entity | — | water_price | PASS |
| 18 | `zh_landlord_same_save` | cross_save | — | landlord | PASS |
| 19 | `zh_landlord_other_save` | cross_save | — | — | PASS |
| 20 | `run_private_same_run` | cross_run | — | run_private | PASS |
| 21 | `run_private_other_run` | cross_run | — | — | PASS |
| 22 | `save_private_same_save` | cross_save | — | landlord | PASS |
| 23 | `save_private_other_save` | cross_save | — | — | PASS |
| 24 | `version_old_rule_current_scope` | version | — | — | PASS |
| 25 | `version_old_rule_old_scope` | version | — | old_version | PASS |
| 26 | `conflict_high_query` | conflict | — | — | PASS |
| 27 | `conflict_low_query` | conflict | — | — | PASS |
| 28 | `deprecated_rule_hidden` | status | — | — | PASS |
| 29 | `candidate_rule_hidden` | status | — | — | PASS |
| 30 | `budget_keeps_top_warning_only` | budget | state=0.95, budget=30 | inv_capacity | PASS |

`mem_seed_` 前缀在表中省略。9 条空期望的负例全部为空结果；15 条 warning
gold 全部召回（`warning_miss=0/15`）。

### 4.2 指标口径

- `Recall@5`：只在 21 条非空 gold 上取宏平均；
  `|gold ∩ top5| / |gold|`。空 gold 的负例不计入 recall。
- `Precision@5`：30 条全部计入，`|gold ∩ top5| / |top5|`；
  空结果记 1.0（没有假阳性），空 gold 但返回了行记 0.0。
- `passed`：`set(expected) == set(top5)` 的条数。
- `warning_miss_rate`：gold 中 `kind=warning` 的条目未进入 top5 的比例。

### 4.3 复现命令与实测

```bash
cd PawnMind
TMP=$(mktemp -d)
.venv/bin/python -B -m agent.memory.seed \
    --memories-db "$TMP/memories.db" --db "$TMP/events.db"
.venv/bin/python -B -m agent.memory.retrieval_eval \
    --memories-db "$TMP/memories.db" --events-db "$TMP/events.db" --k 5
```

实测（2026-11-17，临时双库）：

```text
passed=30/30 recall@5=1.0000 precision@5=1.0000 negative=9/9 warning_miss=0/15
```

同一命令加 `--format table` 会逐条打印 expected/got；`--format json` 含
每条的 `recall` / `precision` / `returned`，便于 P3/P4 做回归对比。

### 4.4 为什么不能把 1.0 当泛化指标

1. 30 条和 15 条种子是同一个作者写的，词表（`requires_any`/`entities`）就是
   为这些查询准备的。
2. coverage 门槛 0.5、`requires_any` 优先、裸数字 FTS 降权都是看着这批用例
   调出来的；没有留出集。
3. 数据是 `test_data=true` 的合成规则，不含真实顾客对话、真实物品 id 或
   真实版本差异。
4. 下一步应把 real recording 的 query/记忆另存为 holdout，先冻结参数再跑；
   如果 holdout 掉到 0.9/0.7 以下，优先改召回通道，而不是继续调门槛。

---

## 5. 测试与验收

### 5.1 Python

```text
$ .venv/bin/python -B -m unittest discover -s agent/tests
Ran 234 tests
OK
```

新增覆盖：

- `test_memory_store_v3.py`
  - v2 库就地升级、行/证据/revision 不丢、`added_at` 回填、重开幂等；
  - 三种角色、默认 support、非法角色拒绝；
  - `add_evidence` 幂等、缺事件、角色冲突、revision 只写一次；
  - contradict 阻断 verify；verified 后追加 contradict 保留状态但计数+1；
  - `event_details` / `events_status`（配置与降级）。
- `test_memory_retrieval.py`
  - 30 条逐条精确集合断言（不是只查 recall）；
  - state 缺失/为假/越界/类型不符 → 不返回；
  - `rent.days_left` 嵌套、`rent_days_left` 扁平、`rent_days_left_max` 后缀；
  - 预算只保留最高优先级 warning；
  - 隐藏状态/跨 save/跨 run/旧版本不泄漏；
  - 评测口径；四个工具在 `--tools recording` 下无 bridge 的端到端调用；
  - `READ_ONLY_OPS` 分类（且不在 C# action 集合里）。
- `test_mcp_tools_mode.py`
  - recording 104 / full 114；
  - 四个工具无 `rev`、参数面固定、`memory_get`/`memory_retrieve` 的 required。

### 5.2 `./test.sh`

```text
passed=651 failed=0
contract gate tests: passed=53 failed=0
contract checks: 490
adapter contract OK
```

### 5.3 CLI 验收

```bash
.venv/bin/python -B -m agent.memory.cli \
    --db "$TMP/events.db" --memories-db "$TMP/memories.db" status
```

实测输出（节选）：

```text
events: 20
memories: 15
memories_by_status: {'candidate': 1, 'conflict': 2, 'deprecated': 1,
                     'superseded': 1, 'verified': 10}
memory_schema_version: 3
pending: 0
timelines: 3
```

---

## 6. 与 P3 设计的字段差异（逐列核对 `memories.py`）

### 6.1 `memories`

实际列：

```text
memory_id, game_id, game_version, save_id, run_id, scope_extra_json,
kind, text, status, confidence, source, dedupe_key, timeline_id,
valid_from, valid_until, superseded_by, created_at, updated_at,
hit_count, last_used_at, evidence_unverified
```

P3 §9.1 要求但**没有**的列：

| 缺列 | 影响 |
|---|---|
| `created_by_job_id` | 无法回答「这批记忆是哪个反思作业产的」 |
| `last_seen_at` | 无法区分「新记忆」与「又见到一次」 |
| `source_type` / `source_json` | `source` 只是自由字符串，没有来源四元组 |
| `outcome_polarity` | 冲突检测只能靠人工写 `conflict`，不能自动判正负 |
| `competing_explanations_json` | P3 的候选备择解释无处落库 |
| `verification_plan` | 升级计划无处落库 |
| `verified_by` / `verified_at` | 只有 revision 的 `actor`，没有「谁批准」的字段 |
| `stale_reason` / `tainted` / `revalidate_after` | 无法表达「为什么失效/是否被污染/何时重验」 |

另外：

- 没有 `scope` / `scope_key` / `topic` 列；作用域由
  `game_id/game_version/save_id/run_id/timeline_id` 推导。
- `dedupe_key` 是**全局 UNIQUE**；P3 要的是
  `UNIQUE(scope, scope_key, kind, dedupe_key)`。同一条文本换 scope 会被
  P2 判成重复。
- 状态词表不同（见 §0.5）。
- `valid_until` 有，但没有 `stale` 状态，过期行只是检索不到。

### 6.2 `memory_evidence`

实际列：

```text
memory_id, event_id, weight, evidence_grade,
evidence_role, added_at
PRIMARY KEY(memory_id, event_id)
```

P3 要求但**没有**：

- `added_by_job_id`；
- 主键里的 `evidence_role`：同一事件不能既 support 又 contradict。
  这是从 v2 平滑迁移的代价，`add_evidence` 用
  `evidence_role_conflict` 明确拒绝，而不是覆盖。
- `added_at` 在旧库迁移时回填为 **memory.created_at**，不是证据真正加入的时间；
  v3 之后新写路径才写真实 `_now()`。

### 6.3 `memory_revisions`

实际列：

```text
memory_id, revision, changed_fields_json, reason, actor, created_at
```

P3 要求但**没有**：

- `revision_id`（用的是 `(memory_id, revision)` 复合主键）；
- `old_json` / `new_json`：只记「改了哪些字段」，不记旧值/新值；
- `job_id`。

映射关系：`actor` ≈ `changed_by`，`created_at` ≈ `changed_at`。

### 6.4 索引 / 表

- P3 的 `ux_mem_dedupe` 是 scope 维度的部分唯一索引；P2 是
  `dedupe_key TEXT UNIQUE`（列级唯一）。
- `idx_mem_job`、`idx_mem_scope2`、`idx_rev_memory` 不存在。
- `memory_conflicts`、`reflection_jobs`、`reflection_candidates` 均未建；
  P3 §3/§6 的表不在本半范围。
- `memories_fts` 是 external-content FTS5 + 三个触发器；这是 P2 自己的选择，
  P3 设计没有禁止。

---

## 7. 未实现 / 已知限制

1. **没有写记忆的 MCP 工具**：本半只交付四个只读工具；`add_candidate` /
   `add_evidence` / `verify` / `reconcile` 仍是进程内 API。
   P3 的反思作业需要在进程内调用它们，或者另开受控写工具。
2. **没有 P3 的 `proposed → hypothesis → verified` 状态机**；`verify` 还是
   人工/种子路径。`hypothesis` 只能落成 `candidate`。
3. **没有 `memory_conflicts` 双向表**；冲突是人工标 `conflict` 状态。
4. **没有自动版本失效**：旧版本记忆靠 scope 过滤，不靠
   `stale_reason`/`valid_until` 批处理。
5. **`bootstrap` 不带 state**：它只保证 verified/未过期/scope 匹配，
   条件受限的 verified 规则仍可能出现在 bootstrap 里；调用方必须在具体决策前
   用 `memory_retrieve(state=...)` 复核。
6. **`memory_get` 的证据事件详情依赖 events 库**：未配置时只有
   `event_id/evidence_grade/evidence_role/added_at/weight`。
7. **检索是 Python 全表扫描 + 评分**（先 SQL 硬过滤，再在 Python 里做概念词/
   entity 匹配）。P2 的单用户、几十到几百条记忆规模足够；上千条以上需要
   把 `requires_any` 做成倒排表，本次没做。
8. **中文召回用两字前缀 + 全 token 子串**，没有分词器；对「同义不同词」仍然
   无能为力（例如查询「贵不贵」不会命中「价格高」），只能靠人工
   `entities`/`requires_any` 标注。
9. **30 条是调参集**，见 §4.4；不要把 1.0/1.0 写进对外能力声明。

---

## 8. 复现清单

```bash
cd PawnMind

# 1) Python
.venv/bin/python -B -m unittest discover -s agent/tests

# 2) C# + 脚本门禁 + 适配器契约
./test.sh

# 3) 临时双库
TMP=$(mktemp -d)
.venv/bin/python -B -m agent.memory.seed \
    --memories-db "$TMP/memories.db" --db "$TMP/events.db"

# 4) 检索评测
.venv/bin/python -B -m agent.memory.retrieval_eval \
    --memories-db "$TMP/memories.db" --events-db "$TMP/events.db" --k 5

# 5) CLI 双库 status
.venv/bin/python -B -m agent.memory.cli \
    --db "$TMP/events.db" --memories-db "$TMP/memories.db" status

# 6) 以 recording 面启动（Ctrl-C 退出；本步骤只验证能启动，
#    且不应因为 tools/list 创建 play/memory/memories.db）
.venv/bin/python agent/mcp_server.py --config <bridge.conf> \
    --tools recording --memories-db "$TMP/memories.db"
```

所有数据库都在 `$TMP`。不要用 `PawnMind/play/memory/memory.db` 或
`memories.db` 做测试；那两个是录制数据，任务期间只允许改 `play/AGENTS.md`。
