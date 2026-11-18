# FIRST_WEEK_BENCHMARK_SPEC — PawnMind 首周经营离线评测规范

> **状态：`OFFLINE_SPEC`（可复现规范；2026-11-17 Nightly Batch A / E，2026-11-19 R5-A4 + R5-B1 修订）。**
> 本文件定义“怎样才算可复现地评测一局首周”，**不是评测结果**。
> 本批次**没有运行 benchmark**，没有游戏进程，没有创建/读取存档：
> **通关率 = `NOT_MEASURED`；效率提升 = `NOT_MEASURED`；本文件不包含任何跑分。**
> **`spec_version` 只标识规范文本版本（当前 `2026-11-19-r5b1`），不是实测环境**；每次 run 的环境（HEAD、DLL/policy
> 哈希、工具面、模型）必须按 §2 实测记录，不得把规范里的历史基线当作本次 run 的环境。
> 真机（L3）证据：`NOT_RUN`（无 Windows/游戏进程/授权）；R5-A/R5-B1 的 C#/Python 改动只有离线单测证据。
> R5-B1（commit `704b5b88`）新增玩家等价暂存工具 `stage_negotiation_item`（在 recording 面），工具面 full=117 / recording=107，
> policy sha256 离线实测 `774fbfdd…`；本规范只把它纳入交易工作流与轨迹字段，**不代表 L3 已通过**。
>
> 关联：`skills/pawnmind-first-week/SKILL.md`（被测策略）、`agent/trace_validator.py`（轨迹合规校验）、
> `docs/audit/NIGHTLY_TEST_MATRIX.md`（PP-* 编号风格）、`docs/plans/NEXT_LIVE_SESSION.md`（实机前置）。

---

## 0. 图例与证据等级

| 标记 | 含义 |
|---|---|
| `PASS` | 有真实运行记录（命令 / 日志 / 轨迹文件），可复现 |
| `FAIL` | 有真实运行记录且失败，附失败码与证据路径 |
| `NOT_RUN` | 缺少环境（游戏/授权/存档/宿主）而未执行 |
| `NOT_MEASURED` | 设计上不在本批次测量，**不得填数字** |
| `BLOCKED` | 需要先修复上游模块（B/C/D/A）才能开始 |
| `HYPOTHESIS` | 未经证实的推断，只可作为待验证项 |

**铁律**：空白 ≠ PASS；fake 证据 ≠ live 证据；一次成功轨迹 ≠ 通关率。
本 benchmark 的“通关率/效率”在没有多次实机运行前恒为 `NOT_MEASURED`。

---

## 1. 评测对象（一个 run 的定义）

一次 benchmark run = 从**受控初始存档**开始，由被测 agent（模型 + 首周 Skill + 声明工具集）
只通过 MCP 工具完成一次首周经营，直到满足以下任一终点：

1. 首周租金支付完成且跨过租金结算日（目标达成）；
2. 明确失败（破产/漏租/预算耗尽/硬停）；
3. 评测上限（回合数/墙钟/成本）触发。

评测器**不得**在 run 中修改游戏状态、替模型决策、手动推进对话或补存档。

---

## 2. 可复现性字段（每个 run 必须记录）

> 缺任何一项 → 该 run 记为 `NOT_RUN` / `BENCH_ENV_MISMATCH`，不能进入成绩统计。
>
> **规范版本 ≠ 实测环境。** 本文件只固定“要记录哪些字段、怎么判”，不固定任何一次 run 的 HEAD/哈希/工具数。
> 下表右列是**起草时基线（历史参考）**：`2026-11-17` 起草基线 + `2026-11-18T19:16Z` R5 启动基线
> （`docs/reviews/R5_BASELINE.md`）。它们只用于对照漂移，**不得照抄进 run 记录**。

### 2.1 代码与环境

| 字段 | 采集方式 | 起草时基线（历史参考，非本次 run 值） |
|---|---|---|
| workspace HEAD | `git rev-parse HEAD`（在 `<repo>` 跑），并记 `git status --porcelain` | 起草基线 `f7ae62c`（2026-11-17，含修复 `7548ddb`，**仅历史参考**）；R5 启动基线 `a043449a`（`R5_BASELINE.md`，2026-11-18T19:16Z，**本轮仍可能变动**）。run 时一律实算，禁止照抄 |
| PawnMind 工作树状态 | `git status --porcelain` | 原样记录（评测时不得有未声明改动）；评测必须使用**干净构建**并记录 diff 清单 |
| 游戏版本 | 游戏 UI / `health.game_version` | *Probably Stolen* **0.46D**，`VERSION 100-Rev13`（`docs/plans/NEXT_LIVE_SESSION.md`） |
| 游戏 MVID | `bridge.conf.expected_assembly_mvid` + `health.game_assembly_mvid` | 两者必须一致；未配置时记录实际值 |
| Mod 构建 SHA256 | `sha256sum dist/PawnMind/PawnMind.dll` | 评测前重新构建 |
| 已安装 DLL SHA256 | 安装目录 `PawnMind.dll` | **必须与 dist 相等**，否则整个 run `NOT_MEASURED`（现象不可归因） |
| `run_mode` | `bridge.conf` / `health.run_mode` | 实机写动作需要 `play`（且有 live evidence）或显式 `test`；`bootstrap` 只读 |
| `allow_test_mode` | `bridge.conf` | 评测应为 `false`（不得用 test 便利绕过 live 门禁） |
| Python | `.venv/bin/python -V` | 3.12.3（本机） |
| MCP SDK | `pip show mcp` / 包版本 | 以实际安装为准，记录版本 |
| 宿主 | DSH / OpenCode 版本与配置 | 记录宿主名、版本、tool-call 上限、超时设置 |

### 2.2 工具集版本（**以哈希为准，不以“工具数”为准**）

| 字段 | 采集方式 |
|---|---|
| `policy_schema` | `health.policy_schema` 或 `agent/policy/tool_policy.json.schema` |
| `policy_sha256` | `.venv/bin/python -c "import sys;sys.path.insert(0,'agent');import tool_policy as p;print(p.policy_sha256(p.load_policy()))"`；起草/R5 启动基线 `e0da5a24…`、R5-B1 后离线基线 `774fbfdd7b44ef5c6c7b616f885f1b624758b862b3e750497f8a39cc26763277`（都是**历史/离线参考**，run 时实算） |
| 工具面 | `--tools full` 或 `--tools recording`；记录 `docs/audit/tools-inventory.json` 的 schema 与文件 SHA256 |
| 工具数量 | 只作旁证：起草/R5 启动基线 full=**116**、recording=**106**；R5-B1 后 full=**117**、recording=**107**（新增 `stage_negotiation_item`，在 recording 面）。**数量变化不算换基准**，哈希变化才算；工具数不是协议版本号 |
| 工具白名单 | 本次 run 实际允许的 tool 名单（含读取工具）+ 宿主侧 allowlist 的 SHA256 |

要求：MCP 侧 `expected_policy_sha256` 与游戏内 `/health.policy_sha256` 一致；
不一致时客户端会在写动作前拒绝（`policy_mismatch`），该 run 记 `BENCH_TOOLSET_MISMATCH`。

### 2.3 模型 / 提示词 / 策略版本

| 字段 | 采集方式 |
|---|---|
| 模型 provider / model id / 版本 | 宿主配置（例如 DeepSeek V4.1 Flash；以实际签入的模型 id 为准） |
| 生成参数 | temperature / top_p / max_tokens / seed（若宿主支持）；**不支持 seed 时记录 `seed=NOT_SUPPORTED`** |
| 系统提示词 SHA256 | 宿主 prompt 文件或导出文本的 SHA256 |
| Skill 版本 | `sha256sum skills/pawnmind-first-week/SKILL.md`；记录 git blob hash |
| Agent 主循环版本 | 宿主/编排代码的 commit + 入口文件 SHA256（本项目不得为 benchmark 新建第二套主循环） |
| 记忆/上下文配置 | memory on/off、注入的 memory 条数、token 预算 |

### 2.4 初始存档与 seed 约定

**关键事实**：离线环境**无法创建存档**（`new_game`/`confirm_new_game` 会真实写盘，需要游戏进程 + 用户授权），
所以本批次：

- 初始存档准备：`NOT_RUN`（无游戏、无授权、不读写存档）；
- 离线 fake 场景：`agent/fake_game.py` 的 L1 假局**不是** benchmark 初始存档，只能验证管道（见 §7）；
- seed（RNG 种子）：协议未暴露可设置的 RNG seed（`HYPOTHESIS`：游戏 RNG 由内部状态驱动），
  因此 **seed 可复现性 = `NOT_MEASURED`**；不得声称“固定 seed 可复现”。

实机 benchmark 的**受控初始存档**（下次真机会话执行，当前 `NOT_RUN`）：

1. 用户明确授权一个**专用录制槽**（非玩家存档；`save_11*`、`save_0/1/3/4/7`、`save_13` 等绝对禁止）。
2. 从主菜单原生流程开始（`get_new_game_options()` → `open_new_game_menu()` → `select_new_game_option()` →
   `new_game_continue()` → `confirm_new_game()`），或由用户确认后 `load_recording_game()` 读回已绑定槽。
   注意 R5/A1：first-run prompt 确认（`prompt_confirmed:true` + `run_change:"not_issued"`）是独立 UI 操作，
   **不是** run start；确认后可以再次 `confirm_new_game()` 真正开局（详见 SKILL §1.2），不要按“整局只调一次”记录环境步骤。
3. run 开始前记录存档指纹（来自 `get_recording_save_status()` 的 `binding`）：
   `slot`、`run_id`、`run_number`、`start_type`、`start_scenario_id`。
4. 再记录初始状态指纹（来自第一次 `get_state()`）：
   `day`、`starting_day`、`cash`、`rent.due`、`rent.days_left`、`inventory_count`、`shop_state`、`rev`。
5. 重复 run 必须从**同一存档指纹**恢复；恢复方式只能是用户操作 + `load_recording_game()`；
   不允许“读档到一半直接开始”，也不允许修改存档文件。
6. 若存档指纹与上一次 run 不同（游戏自动存档/时间推进/槽位变化），该 run 标记
   `BENCH_START_SAVE_UNAVAILABLE` 或 `BENCH_ENV_MISMATCH`，不进入统计。

7. 交易工作流（R5-B1）：先 `stage_negotiation_item(item_id, rev)` 暂存（completed 需 `staged_item_id_after==item_id`
   + `item_on_weighted_counter` + `negotiation_open` + `client_identity_unchanged` 全为真），再 `confirm_trade`；
   暂存 uncertain 时回读 `get_state.negotiated_item`，不得同 item 新 `action_id` 重试、不得直接 confirm_trade。
8. 原生弹窗工作流（R5-B2）：`get_pending_prompt()` 按 `kind`（`confirmation` / `negotiation_confirm`）与
   `target`（`generic_confirmation` / `negotiation_tablet_confirm`）识别模态，读 `body`/`confirm_text`/
   `cancel_text`/`confirm_enabled`/`cancel_enabled`；`answer_prompt(choice)` 按身份路由到原生回调。
   成功需 `prompt_closed:true` + `state.shop_state` 回读；两个模态同开 → `prompt_ambiguous`（不点）、
   模态变化 → `prompt_changed`（未派发）、点后未关 → `prompt_readback_mismatch`。**正式流程不用 `press`**。
   覆盖层（R5-B2b）：`close_windows(rev)` 的 `closed` 可含 `"overlays"`（教程提示覆盖层 / 对话与打字机设置），
   `remaining` 必须干净；覆盖层仍在 → `window_readback_mismatch`。

> `HYPOTHESIS`：同一存档 + 相同动作序列是否产生相同结果，取决于游戏内部 RNG；在获得多次实机对照前，
> 不承诺“同存档可完全复现同轨迹”。

---

## 3. 首周目标与成功判据

首周目标：**从受控初始存档开始，经营到第一周租金实际结算完成**（不破产、不漏租）。
天数/金额一律以运行时 `get_state().rent` 与 `get_calendar()` 的可见值为准，**不得**照抄 fake 常量。

一个 run 记为 **`PASS`** 必须同时满足（缺一即 `FAIL` 或 `NOT_MEASURED`）：

| ID | 判据 | 证据（外部可验证） |
|---|---|---|
| `BENCH-G1` 存活 | 到目标日 `cash >= 0`，未破产/未被没收店铺；`rent_weeks_missed` 未增加 | 每回合 `get_state()` 序列 |
| `BENCH-G2` 租金实际支付 | `rent.due` 对应金额被扣除且租期重置 | 写响应 `last_rent_payment`（或 `rent_receipt`/`settlement_event`）**或** 后续 `get_state().rent.last_payment`；`amount == due`、`cash_delta == -amount`、`rent_days_left_after > 0`、`rent_weeks_paid` 增加 |
| `BENCH-G3` 轨迹合规 | 全轨迹通过 `agent/trace_validator.py`（`VALIDATOR_VERSION` 以记录为准，当前 `1.3.0`），0 个 error；trace 必须声明 `schema_version: 2` | 校验输出 JSON（见 §6） |
| `BENCH-G4` 无盲重试 | 不存在“uncertain 后用新 `action_id` 重试”；不存在 pending 期间的**冲突**写（按 calibrated relation：`same_op`/`same_item`/`run_changing`/`unknown` 均为冲突；call-scoped op 为 `confirm_trade` + `stage_negotiation_item`；`independent` 的不同 item 交易/暂存与 `modal_resolution` 的弹窗/对话动作是允许的）；不存在未 confirmed 跨局动作的重发 | trace validator（`UNCERTAIN_RETRY_WITH_NEW_ACTION_ID` / `CONFLICTING_WRITE_WHILE_PENDING` = 0） |
| `BENCH-G5` 无假成功 | 拒绝步骤不得被当作成功；`ok:true` 只在 `completed`；租金/交易/关店/换天/落盘均有外部证据；单笔成交按 R5/A3 判据（`client_unchanged` + `items_transferred`/`cash_moved`，`business_complete`/`visit_complete` 只是辅助归因） | trace validator（`OK_WITHOUT_COMPLETED_EXECUTION`、`MODEL_SELF_REPORT_IS_NOT_EVIDENCE`、`RENT_EVIDENCE_*`、`TRADE_EVIDENCE_MISSING`、`END_DAY_EVIDENCE_MISMATCH`、`DAY_CHANGE_EVIDENCE_MISSING`、`SAVE_EVIDENCE_MISSING`、`REJECTED_*`、`INVALID_RUN_CHANGE_FIELD`、`TRACE_SCHEMA_VERSION_UNSUPPORTED` = 0；`TRACE_SCHEMA_VERSION_MISSING` 只允许出现在历史 trace 且必须逐条披露并重导） |
| `BENCH-G6` 工具面合规 | 只使用声明的工具面；不用 `act`/`press`/debug/test-only 路径；不访问非专用存档 | MCP 日志 + 工具名清单比对 |
| `BENCH-G7` 证据完整 | 每个写动作有 `action_id` + `rev`；关键操作有 operation 两轴状态（`operation_id`/`execution_status`/`delivery_status`/`dispatched`）与 `get_operation` 回查能力；final state 回读 | 轨迹 + 调用层持久日志 |
| `BENCH-G8` 人工介入已披露 | 人工介入次数与类型逐条记录；未披露的人工介入 = `FAIL` | 人工介入表（§5.3） |

**租金成功只认外部结算证据，不认模型自述**；`action_history` 也不是权威（内存环形缓冲，进程重启即丢）。
若模型/报告声称成功但 §3 证据缺失：该 run 记 `BENCH-RENT_EVIDENCE_INVALID`，不得改判为 `PASS`。

次级指标（只记录，不设通过线；本批次一律 `NOT_MEASURED`）：
总 model 成本、总 token、工具调用次数、每个决策回合的墙钟时长、每日墙钟时长、
非法调用次数、重复失败数、最终净资产、现金流。

---

## 4. 失败码（benchmark 层，稳定可判断）

| 失败码 | 触发条件 | 记录内容 |
|---|---|---|
| `BENCH_NOT_RUN` | 无游戏/无授权/无宿主/无存档 | 缺什么 + 复现步骤 |
| `BENCH_ENV_MISMATCH` | dist 与已安装 DLL 哈希不同；游戏版本/MVID 不符；HEAD 与记录不符 | 两个哈希、版本、MVID |
| `BENCH_TOOLSET_MISMATCH` | `policy_schema`/`policy_sha256` 不一致；实际工具面超出声明 | 两侧哈希、工具清单 diff |
| `BENCH_START_SAVE_UNAVAILABLE` | 专用槽未绑定；存档指纹缺失/不一致；无法回到主菜单 | 指纹字段 + 人工步骤 |
| `BENCH_ILLEGAL_ACTION` | 非法/越权工具调用、`act`/`press`、debug 路径、访问非专用存档 | 工具名 + 参数 + step 索引 |
| `BENCH_BLIND_RETRY` | uncertain 后新 `action_id` 重试；pending 期间冲突写 | trace validator 违规码 |
| `BENCH_FAKE_SUCCESS` | 拒绝被当成功；`ok:false` 继续推进；自述代替证据 | 违规码 + 模型文本 |
| `BENCH_TRADE_EVIDENCE_INVALID` | `confirm_trade` 报 completed 但 `client_unchanged != true`，或 `items_transferred == false` 且 `cash_moved == false`（`business_complete`/`visit_complete` **不能**单独证明单笔成交；R4 已证实的 partial trade——`business_complete:false` 但本笔 item/cash 证据成立——是**有效**的，不得据此判失败），或 `evidence` 对象与顶层证据矛盾；现金变化不得作为唯一成功判据 | 交易响应 + 前后状态 |
| `BENCH_END_DAY_FAKE_SUCCESS` | `end_day` 报 completed 但 `shop_closed_after != true`；或把 `pending:true + shop_still_open` 当成功 | end_day 响应 + readback |
| `BENCH_SAVE_EVIDENCE_INVALID` | 只有 `last_save_time`/`saved` 就写“落盘成功”，或把 `save_evidence:"save_unknown"` 当 completed | 落盘检查点证据 |
| `BENCH_OPERATION_PROTOCOL` | `wait_operation` 超时缺 `operation_id`/`next`；未知 operation 未标 `known:false`/`process_restarted`；两轴字段缺失或互相矛盾 | 违规码 + operation 响应 |
| `BENCH_RENT_EVIDENCE_INVALID` | 租金回执缺失/金额现金不一致/漏租周 | 回执 JSON + 前后状态 |
| `BENCH_RENT_NOT_PAID` | 到目标日租金仍未实际支付（回执全缺） | 最后 `rent` 快照 |
| `BENCH_BANKRUPT` | cash < 0 或店铺被没收 | 状态序列 |
| `BENCH_OUTCOME_UNCERTAIN` | run 结束时仍有未结案 operation（pending/uncertain） | operation id + 最后查询结果 |
| `BENCH_SAVE_UNKNOWN` | 需要落盘证据的检查点只有 `save_unknown` | 检查点 + 可用证据 |
| `BENCH_STEP_BUDGET_EXCEEDED` | 超过动作/回合预算 | 预算值 + 实际值 |
| `BENCH_TIMEOUT` | 墙钟预算耗尽 | 预算 + 实际 |
| `BENCH_MODEL_ERROR` | 宿主/模型中断、空响应、工具调用格式错误超阈值 | 错误 + 次数 |
| `BENCH_HOST_LOST` | MCP 子进程/宿主崩溃、连接断开 | 时间点 + 恢复动作 |
| `BENCH_MANUAL_INTERVENTION` | 未披露的人工介入（点击/对话/改状态/补档） | 介入类型 + 时间 |
| `BENCH_TRACE_INVALID` | 轨迹缺字段/无法解析/时间戳乱序；**未声明 `schema_version: 2`**（legacy 轨迹按 v1 旧口径校验并给 `TRACE_SCHEMA_VERSION_MISSING` warning，必须重导后才能计入当前基准）；或声明了不支持的版本（`TRACE_SCHEMA_VERSION_UNSUPPORTED`） | trace validator 输出 + 原始轨迹路径 |

失败码只用于“这次 run 怎么了”，**不是**通关率分母；分母统计需要多次同基准 run（当前 `NOT_MEASURED`）。

---

## 5. 成本 / 时长 / 人工介入（怎么量）

### 5.1 成本

每次 run 记录（宿主可提供则自动采集，否则标注 `NOT_MEASURED`）：

- 输入 token、输出 token、缓存命中 token、reasoning token（若可分开）；
- 模型调用次数与总费用（按 provider 计费口径；币种注明）；
- MCP 工具调用次数（按工具名分组）；
- 本地 CPU/GPU 成本可不计，但要说明口径。

`NOT_MEASURED` 场景：宿主不提供 token/账单 → 全部标 `NOT_MEASURED`，**不得估算成数字**。

### 5.2 回合时长

定义（全部以轨迹时间戳为准，宿主需在每个 step 记录单调时间）：

- **决策回合（turn）**：一次 `get_state()` 到下一次写动作之间的完整 observe→decide→act 循环；
- **工具往返延迟**：单次 MCP call 的 start/end 时间差；
- **每日墙钟**：`sleep()` 完成到下一次 `end_day()` 完成；
- **总 run 墙钟**：第一次工具调用到最后一次工具调用（含等待）。

统计输出：min / median / max / p95。样本 < 5 时只记录原始值，不报分布（避免编造统计）。

### 5.3 人工介入次数

每类分别计数，全部必须披露：

| 类型 | 例子 | 计入方式 |
|---|---|---|
| 环境准备 | 构建/安装 DLL、重启游戏、改 `bridge.conf` | 不计入 run 内，但记录 |
| 授权操作 | 批准专用槽、批准新局 | 记录 1 次 |
| 回主菜单 | 读档前由人退出当前局 | 每次 1 次（这是文档化的 host 步骤） |
| 紧急停止 | 人按停止/杀进程 | 每次 1 次；有未披露即 `FAIL` |
| 状态修复 | 人手动改游戏状态/补存档 | **禁止**；出现即 run 无效 |

**`BENCH-G8` 要求**：run 内人工介入次数为 0（环境准备除外，且必须标注）；
若记录到任何“替模型决策/推进游戏”的人工操作 → `BENCH_MANUAL_INTERVENTION` = `FAIL`。

---

## 6. 轨迹校验（强制步骤）

每个 run 必须生成 JSON 轨迹（格式见 `agent/trace_validator.py` docstring）并运行：

```bash
cd <repo>/PawnMind
.venv/bin/python -B agent/trace_validator.py /path/to/run-trace.json
# 轨迹顶层必须是 {"schema_version": 2, "steps": [...]}（当前 schema）
# exit 0 = 无 error（warning 允许但必须披露）；1 = 有违规；2 = 输入不可读
```

- 轨迹必须由**可信调用层**记录，不是模型自述：写动作发出前持久化
  `run_id + sequence + action_id + op + args + rev`，响应记录 `operation_id`、`execution_status`、
  `delivery_status`、`dispatched`、`error`、`redelivered` 与关键证据字段
  （暂存 `staged_item_id_before`/`staged_item_id_after`/`item_on_weighted_counter`/`negotiation_open`/
  `client_identity_unchanged`/`native_primitive`；交易 `business_complete`/`visit_complete`/`client_unchanged`/
  `items_transferred`/`cash_moved`/`cash_before`/`cash_after`；跨局 `run_change`（`issued`/`not_issued`）/
  `run_change_status`/`run_change_confirmed`；关店 `shop_closed_after`；睡觉 `day_changed`/`save_evidence`/
  `save_completed`/`save_unknown`；弹窗 `kind`/`target`/`body`/`confirm_text`/`cancel_text`/`confirm_enabled`/
  `cancel_enabled`/`prompt_closed`/`state.shop_state`；覆盖层 `closed`(含 `overlays`)/`remaining`；
  查询 `known`/`result_rev`/`next`/`process_restarted`/`truncated`）。
- **轨迹版本纪律**：当前 schema `2`。不带 `schema_version` 的历史轨迹按 v1 旧口径校验并产生
  `TRACE_SCHEMA_VERSION_MISSING` warning（不会被静默改写），必须重导为 schema 2 后才能计入当前基准；
  未知版本直接以 `TRACE_SCHEMA_VERSION_UNSUPPORTED` 拒绝（fail closed）。
- 校验器必须为 0 个 error 才能进入 §3 的判定；warning 需在报告中逐条列出。
- 校验通过 ≠ 真机成功；它只证明协议自洽与证据链没有明显伪造。

**L0 自检**（离线可跑；R5-A4/R5-B1 后实际运行，2026-11-19）：

```bash
cd <repo>/PawnMind
.venv/bin/python -B -m unittest discover -s agent/tests -p 'test_trace_validator.py' -v
# Ran 117 tests ... OK（含 agent/tests/fixtures/ 的 8 条正反轨迹 fixture）
.venv/bin/python -B -m unittest discover -s agent/tests -p 'test_skill_tool_refs.py' -v
# Ran 16 tests ... OK（Skill 引用的工具名全部存在于 MCP 注册表，且访问 tool_policy.json）
```

---

## 7. 运行协议：离线（L1）与实机（L3）

### 7.1 离线（L1/L2，本批次可做，**不是 benchmark 结果**）

用途：验证管道、动作序列、rev/action_id 纪律、轨迹校验。**不能**外推为“AI 能玩”。

可用资产：

- `agent/fake_game.py`：离线假桥（无游戏进程、无存档）；
- `agent/demo_agent.py`：确定性脚本策略（**不是 LLM**，文档已声明它不是 AI 证据）；
- `agent/trace_validator.py`：轨迹校验；
- `agent/tests/`：fake 合同测试、MCP 工具面测试、E2E。

离线结论只能写 `L1-fake PASS` 或 `L2-integration-no-game PASS`，并在报告中明确
“scripted policy，not an LLM and not evidence of real-game play”。

### 7.2 实机（L3，当前 `NOT_RUN`）

前置（全部满足才可开始）：

1. 用户明确授权：专用录制槽 + 新局/读档 + 真机写操作；
2. `dist/PawnMind/PawnMind.dll` 与安装目录 DLL **哈希一致**；
3. 游戏版本/MVID 与 `bridge.conf` 一致；`/health` 的 `run_mode` 为 `play`（或经批准的 `test`），
   `policy_sha256` 与 MCP 侧一致；
4. `play_allowlist` 非空（需要真实 live evidence；**不得**用离线 fake 证据填值）；
5. 宿主（DSH/OpenCode）能真实调用 MCP 工具、记录完整轨迹与成本；
6. 评测器就位，且承诺不在 run 中干预游戏。

L3 run 的失败/成功都必须保留：原始轨迹 JSON、MCP 日志、截图/录像（如授权）、最终状态回读、
失败码。任何缺证据的结论按 `NOT_RUN` 处理。

### 7.3 可复现命令（起草时状态）

```bash
cd <repo>
git rev-parse HEAD
git status --porcelain

cd PawnMind
.venv/bin/python -V
sha256sum dist/PawnMind/PawnMind.dll 2>/dev/null || echo "dist: NOT_BUILT"
.venv/bin/python -c "import sys;sys.path.insert(0,'agent');import tool_policy as p;print('policy_sha256', p.policy_sha256(p.load_policy()))"
sha256sum skills/pawnmind-first-week/SKILL.md
.venv/bin/python -B -m unittest discover -s agent/tests -p 'test_trace_validator.py' -v
.venv/bin/python -B -m unittest discover -s agent/tests -p 'test_skill_tool_refs.py' -v
```

---

## 8. 结果记录模板（每次 run 一份 JSON/Markdown）

```json
{
  "benchmark": "pawnmind-first-week",
  "spec_version": "2026-11-19-r5b1",
  "status": "PASS | FAIL | NOT_RUN | NOT_MEASURED",
  "failure_codes": [],
  "environment": {
    "head": "<git rev-parse HEAD>",
    "game_version": "0.46D",
    "game_mvid": "<health.game_assembly_mvid>",
    "dist_dll_sha256": "<...>",
    "installed_dll_sha256": "<...>",
    "run_mode": "play",
    "policy_schema": 2,
    "policy_sha256": "<...>",
    "tool_surface": "full|recording",
    "tool_names_sha256": "<...>",
    "python_version": "3.12.3",
    "mcp_sdk_version": "<...>"
  },
  "model": {
    "provider": "<...>",
    "model_id": "<...>",
    "generation_params": {"temperature": null, "seed": "NOT_SUPPORTED"},
    "system_prompt_sha256": "<...>",
    "skill_sha256": "<...>",
    "memory_mode": "off|on"
  },
  "start_save": {
    "slot": null,
    "run_id": null,
    "run_number": null,
    "start_type": null,
    "start_scenario_id": null,
    "initial_state": {"day": null, "cash": null, "rent_due": null, "rent_days_left": null}
  },
  "outcome": {
    "day_reached": null,
    "rent_paid_receipt_id": null,
    "rent_paid_total": null,
    "rent_weeks_paid": null,
    "rent_weeks_missed": null,
    "final_cash": null
  },
  "trace": {
    "path": "<run-trace.json>",
    "schema_version": 2,
    "validator_version": "<agent/trace_validator.py VALIDATOR_VERSION，例如 1.3.0>",
    "validator_exit_code": null,
    "error_count": null,
    "warning_count": null
  },
  "cost": {"input_tokens": "NOT_MEASURED", "output_tokens": "NOT_MEASURED",
           "total_model_cost": "NOT_MEASURED", "tool_calls": null},
  "timing": {"total_wall_clock_s": null, "turn_latency_ms": "NOT_MEASURED"},
  "human_interventions": [],
  "not_measured": ["pass_rate", "efficiency_gain", "cost_average"],
  "notes": ""
}
```

---

## 9. 明确 `NOT_MEASURED` 的指标（禁止编造）

| 指标 | 本批次状态 | 需要什么才能测 |
|---|---|---|
| 首周通关率（pass rate） | **`NOT_MEASURED`** | ≥ N 次同基准 L3 run + 统计口径 |
| Skill 带来的效率提升 | **`NOT_MEASURED`** | skill on/off 对照 + 多次 run |
| 平均成本 / 平均回合时长 | **`NOT_MEASURED`** | 宿主 token/账单/时间戳 |
| 人工介入均值 | **`NOT_MEASURED`** | 多次 run 的人工介入表 |
| seed 可复现性 | **`NOT_MEASURED`** | 游戏是否暴露 RNG seed（当前 `HYPOTHESIS` 为否） |
| 与竞品对比 | **`NOT_MEASURED`** | 外部基准方法可复现后才谈对比 |

一次性成功（如果未来出现）只能写“可行性证据”，不能写“通关率”。

---

## 10. 预计下一步（用户醒来后）

1. 按 `docs/plans/NEXT_LIVE_SESSION.md` 完成构建/安装哈希一致性与 `/health` 记录（L3 前置）。
2. 用户授权专用录制槽与首周 run；用 §7.2 的清单逐项打勾。
3. 首次 run 只用于验证流程与收集 `NOT_RUN` 原因，不追求成绩；失败按 §4 失败码归档。
4. 只有积累多次 run 后，才允许在报告中出现 pass rate 等统计（且必须附分母与置信说明）。

---

## 11. schema 已核对（2026-11-17 起草基线；2026-11-19 R5-A/R5-B1/R5-B2 离线修订）

以下字段已与 `mcp_server.py` / `src/Bridge/` / `src/Core/` 对照核对。**HEAD/policy/工具数只是历史基线：
起草基线 HEAD `f7ae62c`、policy sha `e0da5a24…`、工具 full=116 / recording=106；R5-B1 后离线基线
policy sha `774fbfdd…`、full=117 / recording=107（run 时全部实算，见 §2）。**
R5-A/R5-B1/R5-B2 只有源码 + 单测/伪端口证据，**真机 L3 仍 `NOT_RUN`**；以下以离线目标契约为准。

| 主题 | 已核对 schema |
|---|---|
| 写响应 | `operation_id`、`execution_status`（prepared/running/completed/rejected/failed/uncertain）、`delivery_status`（pending_delivery/delivered/undelivered）、`dispatched`、跨局 `run_change`（`issued`/`not_issued`）/`run_change_status`/`run_change_confirmed`；`ok:true` 仅 completed；重复 ID → 原证据 + `delivery_status:"undelivered"` + `redelivered:true`；同 op 非终态时新 id → `operation_pending`/`run_change_pending` + `blocking_operation_id`（未派发，不产生新 operation） |
| 操作查询 | `get_operation` 返回 `known/ok/operation_id/op/execution_status/delivery_status/dispatched/run_change_status/run_change_confirmed/error/rev/result_rev/next`；未知 id `known:false` + `unknown_operation`（run 变化后带 `process_restarted:true`）；`wait_operation` 超时 `ok:false + status:"pending" + operation_id + execution_status + next`（另带 `timed_out:true`）；`get_operation("*")` 上限 20 条 + `count`/`total_non_terminal`/`truncated`/`truncation_note` |
| 交易证据 | 完成判据（R5/A3）：`client_unchanged == true` 且（`items_transferred == true` **或** `cash_moved == true`），`evidence` 对象需一致（`called`/`client_unchanged`/items/cash 前后值不矛盾）；`business_complete`/`visit_complete` 只是整趟归因；`cash_before`/`cash_after`/`cash_changed`/`staged_item_id`/`client_intent` 为对账字段；拒绝时 `dispatched:false`；静默 return/本笔无证据 → `error:"outcome_unverified"` + `execution_status:"uncertain"`（`detail:"no_trade_evidence"`/`"business_not_complete"`） |
| 暂存（R5-B1） | `stage_negotiation_item(item_id, rev)`：完成判据 = `completed` + `staged_item_id_after==item_id` + `item_on_weighted_counter:true` + `negotiation_open:true` + `client_identity_unchanged:true`；含 `staged_item_id_before`/`native_primitive`/`evidence`；拒绝 `dispatched:false` + 稳定错误码；silent/mismatch → `uncertain` + `next`（读 `get_state.negotiated_item`，同 item 不得新 id 重试，不同 item 允许）；`PendingScope.IsCallScoped` 覆盖 |
| 原生弹窗（R5-B2） | `get_pending_prompt`：`kind`（`confirmation`/`negotiation_confirm`）、`target`（`generic_confirmation`/`negotiation_tablet_confirm`）、`body`/`confirm_text`/`cancel_text`/`confirm_enabled`/`cancel_enabled`/`single_button`/`source_op`；`answer_prompt` 成功 = `ok:true + dispatched:true + prompt_closed:true + state.shop_state`；`prompt_ambiguous`（两个模态同开，不点）/`prompt_changed`（未派发）/`choice_unavailable`（disabled）/`prompt_readback_mismatch`（点后未关，非成功） |
| 覆盖层（R5-B2b） | `close_windows`：`closed` 可含 `"overlays"`（`_ControlOverLay/CloseOverlayButton`、`_DialogueOptionUI/Panel/{Apply,Close}Button`，原生 onClick）；`remaining` 不干净 → `window_readback_mismatch`；不依赖 test-only `press` |
| 关店 | `execution_status:"completed"` ⇔ `shop_closed_after:true`；仍 OPEN → `ok:false + pending:true + execution_status:"running" + error:"shop_still_open"` |
| 睡觉 | `day_changed`、`save_evidence`（`save_completed`/`save_unknown`）、`save_completed`、`save_unknown`；`last_save_time` 不算落盘证据；`day_changed:false` = running/pending/inconclusive（**不是失败**）；R5/A2：无 count-based failure，10s 单调时钟观察窗口内无判决，超时无证据 → `execution_status:"uncertain"` + `error:"sleep_unconfirmed"`（非终态，拒绝新 id 重发；后续 day 增加可 promote completed）；只有 `native_not_called`+`dispatched:false` 可安全重试 |
| 轨迹校验 | `agent/trace_validator.py` schema `2`（`VALIDATOR_VERSION` 实读）：`TRADE_EVIDENCE_MISSING` 按 A3 单笔证据；`CONFLICTING_WRITE_WHILE_PENDING` 按 relation（`same_op`/`same_item`/`run_changing`/`modal_resolution`/`independent`/`unknown`）；`TRACE_SCHEMA_VERSION_MISSING`（legacy warning）/`TRACE_SCHEMA_VERSION_UNSUPPORTED`（error）；`INVALID_RUN_CHANGE_FIELD` |
| 快照 | `legal_actions`（`availability="available"`）与 `candidate_actions`（`availability="unknown"`、`requires="native_guard"`）；举报用纯字段 `reported`/`is_wanted` |
| 租金回执 | `last_rent_payment` / `rent.last_payment`（写响应与状态快照同名投影；validator 兼容 `rent_receipt`/`settlement_event` 别名） |
| 工具面 | R5-B1 后 full = 117，recording = 107；`get_operation`/`wait_operation`/`stage_negotiation_item` 均已注册且都在 recording 面（起草时基线为 116/106，run 时以实算 policy 为准） |

若后续再改冻结协议：本表随 Lead 的协议变更同步；**不要再引入“待核对”字段**。
