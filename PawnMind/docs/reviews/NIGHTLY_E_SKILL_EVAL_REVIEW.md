# NIGHTLY_E_SKILL_EVAL_REVIEW — E 模块（首周 Skill、离线评测规范、trace validator）

Status: **APPROVE**
（E 交付为文档 + 新离线校验器；首轮发现的“文档/测试双写不同步”（旧工具数、get_operation 未注册、58/12 条测试）
与 legal_actions availability 问题都已由作者 task-3 同步 + `7548ddb` 修复，最终提交全绿。残留 Low 级一致性建议
不阻塞。所有“通关率/效率/成本”均为 `NOT_MEASURED`，未发现伪造 live evidence。）
Reviewing commit(s): E 本体 `299ecd489d68cb8ca06a0e999ce8c3a7a60cb7d2`；
字段/协议同步文档提交 `f7ae62c117995dffb43a56da70ddefd6254ccb5b`；availability 修复 `7548ddb23a8fb9a0ea10a08740e2c82352891b07`；
**最终 HEAD `f7ae62c`**（审阅基准：最终提交，工作树除 `docs/reviews/` 外干净）。
Change scope: `skills/pawnmind-first-week/SKILL.md`、`docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md`、
`agent/trace_validator.py`、`agent/tests/test_trace_validator.py`、`agent/tests/test_skill_tool_refs.py`、
`agent/mcp_server.py`（仅 `--tools` 帮助文案 114/104→116/106）、`agent/fake_game.py`（E 附带：
`/health` 增加 policy 指纹，见 A-2 修复）。

## Real evidence（本 Agent 亲自运行）

```text
$ .venv/bin/python -B -m unittest discover -s agent/tests -p 'test_trace_validator.py'
Ran 77 tests in 0.006s
OK                                      # 最终提交 f7ae62c；提交时该文件为 58 条，作者按 task-3 扩到 77 条并同步文档

$ .venv/bin/python -B -m unittest discover -s agent/tests -p 'test_skill_tool_refs.py'
Ran 16 tests in 0.015s
OK                                      # 最终提交；提交时为 12 条，含“工具名必须真实存在于 116 工具注册表”AST lint

$ .venv/bin/python tools/gen_policy_catalog.py --check
ok: ... PolicyCatalog.g.cs matches tool_policy.json (sha256 e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc)

$ .venv/bin/python -B -m unittest discover -s agent/tests      # 最终提交全量（E 相关已含）
Ran 365 tests in 56.446s
OK                                      # 日志 /tmp/nightly_review_pytests_committed.log（最终 HEAD f7ae62c）

$ grep -rn "NOT_MEASURED|NOT_RUN" docs/eval/FIRST_WEEK_BENCHMARK_SPEC.md | head -30   # 见 §Invariants
$ grep -n "import " agent/trace_validator.py
argparse / json / re / dataclasses    # 无 mcp_server、无 pawnmind_client、无网络 import
$ grep -n "VALIDATOR_VERSION" agent/trace_validator.py
VALIDATOR_VERSION = "1.1.0"
```

交叉核对（不仅看文档）：

```text
$ git show 299ecd4 --stat                    # 5 文件、+2820 行；仅新增文件（未改 Agent 主循环）
$ git diff --stat -- skills docs/eval agent/trace_validator.py agent/tests/test_trace_validator.py \
    agent/tests/test_skill_tool_refs.py      # 工作树同步增量
$ grep -n "def test_" agent/tests/test_trace_validator.py | wc -l   # 77
$ grep -n "def test_" agent/tests/test_skill_tool_refs.py | wc -l   # 16
$ grep -n "last_rent_payment|rent_receipt|settlement_event" agent/trace_validator.py
# 180-182 行别名表；286 _find_rent_receipts；907 使用 → 租金外部证据规则真实存在
$ grep -n "TRADE_EVIDENCE_MISSING|END_DAY_EVIDENCE_MISMATCH|REJECTED_WITH_DISPATCHED_TRUE" agent/trace_validator.py
# 规则 17/18/19 已实现（含 D-1 对应的 dispatched 轴一致性）
$ grep -n "FAKE_POLICY_SHA256|policy_sha256" agent/fake_game.py agent/fake_game.py  # fake 桥镜像真实 /health 指纹（A-2 修复配套）
```

## Invariants checked（逐条 PASS/FAIL/PARTIAL）

| 不变量 | 结论 | 依据 |
|---|---|---|
| SKILL 覆盖启动/观察/交易/对话/关店/睡觉/租金/错误分类/停止条件 | **PASS** | SKILL §1–§10 标题齐全；`test_skill_tool_refs.REQUIRED_SECTIONS` 强制校验，16 条测试绿 |
| SKILL 只引用真实 MCP 工具名（无幻觉工具） | **PASS** | `test_skill_tool_refs.py` 用 `ast` 解析 `mcp_server.py` 的 `@tool()` 注册表 + `tool_policy.json`，SKILL 引用的工具名必须存在；16 条绿；`get_operation`/`wait_operation` 已注册（116/106），旧 PENDING 例外已删 |
| `legal_actions`（available）与 `candidate_actions`（unknown/native_guard）区别写清 | **PASS（最终工作树）** | SKILL §2.3/§13；B 侧 `StateSnapshot` 现已对两类行都输出 `availability`；candidate 另带 `requires`（本审阅 B-1 修复复核） |
| pending/uncertain 不得盲重试；只查 operation 或复用原 action_id | **PASS（文档 + 校验器）** | SKILL §3 规则 7–10、§9.3；validator 规则 1/2/3/15/20；`test_uncertain_*` / `test_*pending*` 用例 |
| 租金成功只认外部可验证证据（回执/状态增量），模型自述不算 | **PASS（L1）** | validator 规则 9 `RENT_EVIDENCE_MISSING/INCONSISTENT` + 别名表；SKILL §8.3 明确“只认回执/状态证据”；`test_missed_week_counter_increase_invalidates_rent_claim` 等用例 |
| 交易/关店/落盘证据规则与 D/C 冻结 schema 一致 | **PASS** | validator 规则 6/11/17/18/19（`business_complete + client_unchanged + evidence`、`shop_closed_after`、`save_evidence`、`ok`/`dispatched`/`delivery` 一致性）；SKILL §13/benchmark §11 与 `src/` 实际字段逐项核对（本审阅抽查：写响应、trade evidence、end_day、sleep、get_operation、工具数） |
| benchmark spec 包含存档/seed、版本、模型、工具集版本、成功判据、失败码、成本、回合时长、人工介入 | **PASS** | spec §2（含 `policy_sha256`、HEAD、DLL SHA、MVID、模型/提示词版本、seed `NOT_RUN`）、§3 判据、§4 失败码、§5 成本/时长/人工、§8 结果模板 |
| 通关率/效率/成本/人工介入一律 `NOT_MEASURED`，无编造跑分 | **PASS** | spec §9 全部 `NOT_MEASURED`；header 三行醒目 `NOT_MEASURED`；SKILL header `NOT_RUN`/`NOT_MEASURED`；无任何“X% 通关”数字 |
| 未把 fake 证据写成 live / 未填 `play_allowlist` | **PASS** | `tool_policy.json` 116 个工具 `live_evidence` 全为 `[]`；`play_allowlist == []`；SKILL/benchmark 明示 L3 `NOT_RUN` |
| 不改 Agent 主循环 / 不新增泛化 act | **PASS（静态）** | `git show 299ecd4 --stat` 只新增 5 文件；`agent/trace_validator.py` 仅 import stdlib；未新增 MCP 工具 |
| 轨迹必须由可信调用层记录（run_id/sequence/action_id/op/args/rev + 响应两轴） | **PASS（规范）** | spec §6 明确要求；trace 格式 docstring 定义；无“模型自述=证据”路径（validator 规则 6/12/13 覆盖） |
| validator 覆盖“禁止用新 action_id 重试 uncertain”“pending 时不得冲突写”“租金需外部证据”三项硬要求 | **PASS** | 规则 1/3/9 实现 + 77 条测试（含 `test_uncertain_*`、`test_*conflicting*`、`test_*rent*`）；本 Agent 抽查规则实现代码（§Real evidence grep） |
| 文档中“schema 已核对”与最终代码一致 | **PASS（复核后）** | 提交时曾写“get_operation/wait_operation 尚未注册、58/12 条测试”（已被作者在 task-3 修正为 116/106、77/16）；当前工作树无此 stale 说法 |
| validator 无外部依赖/无副作用（可 CI） | **PASS** | 仅 `argparse/json/re/dataclasses`；无 socket/import mcp；`main()` 三段退出码 0/1/2；`test_cli_*` 覆盖 |

## Critical findings

无 Critical / High。

### E-1（Low，**已在 `7548ddb` 修复**）legal_actions 的 availability 在 fake/真实快照间曾不同步

- **原缺陷**：真实 C# 快照的 legal 行缺 `availability`，`fake_game._legal_actions()` 也缺；`test_snapshot_compat.py`
  只钉了 candidate 的 availability。
- **修复**：`7548ddb` 让 C# 对所有 legal_action 行输出 `availability`（legal=`"available"`，candidate=`"unknown"`），
  `fake_game` 的 legal 行同步 `availability:"available"`，并新增
  `test_snapshot_compat.test_fake_legal_rows_carry_availability_available`。
- **复核**：本 Agent 读 diff + 单独跑 `test_snapshot_compat.py`（Ran 9 tests OK）+ 全量 Python 365 OK；与
  SKILL/benchmark 的 schema 表述一致。无残留。

### E-2（Low）validator 的 `CONFLICTING_WRITE_WHILE_PENDING` 比桥更严格，需在文档写明“有意保守”

- 规则 3 把 prepared/running 期间的**任何**其他写都判 error；而 C# 桥只阻止**同一 op** 的新 `action_id`
  （`PP-ASYNC-04` 明确允许 `advance_dialogue` 在 `end_day` running / trade uncertain 时继续）。SKILL §3 规则 8
  也写成“不允许发出冲突写动作（不同 op 或不同 rev）”。
- 影响：如果出现“桥允许、validator 拒绝”的合法轨迹（例如 pending 期间推进无关对话），validator 会误报 error。
  对首周流程该场景少见（running end_day 时无对话），且保守方向不会放过危险轨迹；建议在 spec §6 或 validator
  docstring 注明“validator 采用比桥更严格的客户端策略；该规则是安全策略而非桥的硬约束”，或为已知独立 op
  建 allowlist。

### E-3（Low/文档）benchmark spec 的版本足迹停在 `HEAD f09314e`，未覆盖后续 A/D/E 修复

- spec §11 写“已与 HEAD `f09314e` 核对”；最终工作树另有 A-1/A-2/D-1/E-1 修复（未提交）。这些不改变 E 规范
  的字段结论，但用户按 spec 复现时若看最终提交，版本足迹应更新为最终 SHA（Lead 提交后由 Lead 同步即可）。

### E-4（Informational）SKILL 所述“读 `rent`/`dialogue` 字段”已逐项抽查属实

- `rent.days_left/due/can_pay/rent_paid_total/rent_weeks_paid/rent_weeks_missed/last_payment` 在
  `src/Bridge/FirstWeekJson.cs` 存在；`dialogue.readable/available/choice/ended/typing/choices[]` 在
  `FirstWeekJson.Dialogue` 存在；`can_call_next` 在 `StateSnapshot` 存在；`choose_dialogue` 成功/失败/未知
  响应都带 `last_rent_payment` + `receipt_error` + `rent`（`src/Bridge/FirstWeekService.cs`）。未发现硬编码
  index/隐藏字段。

## Potential regressions

1. Skill 是“提示材料”，模型可能把 `candidate_actions` 当可执行 → SKILL 已用“只看 legal_actions 做计划”、
   “candidate 失败是正常拒绝”两条约束；若宿主不执行 System Prompt，仍存在误用风险（L3 才能评估）。
2. validator 规则 3 的严格策略（见 E-2）可能导致合法轨迹被判 error；建议在 benchmark 报告里把
   `CONFLICTING_WRITE_WHILE_PENDING` 的判定口径写清，避免后续把“桥允许但 validator 拒绝”当成桥 bug。
3. `test_skill_tool_refs` 依赖 `mcp_server.py` 的 AST 解析；若未来工具注册方式改为动态/条件注册，lint 可能失效
   （当前 116 工具全部是 `@tool()` 装饰的静态函数，风险低）。
4. 文档中的工具数/策略 sha 是易漂移值：spec 已声明“以哈希为准、不写死数量”，但 SKILL header 仍写 116/106 与
   sha 前缀；变更时需同步（Lead 已在 baseline/report 登记）。

## NOT_RUN / UNKNOWN

- **L3-real-game 全部 NOT_RUN**：Skill 在真实游戏中的可执行性、首周通关、租金结算、关店/睡觉异步真实时序。
- **NOT_MEASURED**：首周通关率、Skill 效率提升、平均成本/回合时长、人工介入均值、seed 可复现性、竞品对比
  （spec §9 全部 `NOT_MEASURED`，本审阅确认没有数字被填）。
- **NOT_RUN**：DSH 宿主真实 MCP E2E 与轨迹录制（需要宿主 + 游戏；本批次只有 fake/loopback）。
- **UNKNOWN**：真实游戏里 `confirm_trade`/`end_day`/`sleep` 的实际证据字段是否与冻结 schema 完全一致（validator
  只能校验轨迹自洽，不能证明游戏真实行为）。

## Reviewer independence

独立审阅 Agent `nightly-reviewer`（fresh spawn 会话，未参与实现；E 实现者为 teammate `eval-scribe`，fork
上下文，与本审阅者不同 agent/会话）。本会话未写 E 任何文件；只读 diff/文档、跑 77+16 条 E 测试与全量 Python
（365 OK），并逐项抽查 SKILL/benchmark 的字段声明与 `src/` 实际实现。E 的作者 task-3 同步与 Lead 的 `7548ddb` 修复均已合入最终提交 `f7ae62c`，
本文件以最终提交为准（工作树除 `docs/reviews/` 外干净）。与实现者非同一 agent，非 self-review。
