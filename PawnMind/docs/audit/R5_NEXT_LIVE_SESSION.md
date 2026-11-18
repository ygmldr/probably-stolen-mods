# R5_NEXT_LIVE_SESSION — 下一次 Windows 真机会话操作手册（未执行）

> 状态：`OFFLINE_PLAN`。**本轮（Linux、无游戏进程）没有执行任何一条真机步骤**；
> 本文是下一次需要用户明确授权的 Windows 会话的逐步操作、停止与回滚手册。
> 关联：[`R5_HANDOFF_2026-11-19.md`](../reviews/R5_HANDOFF_2026-11-19.md) §5、
> [`R5_LLM_PILOT_PROTOCOL.md`](../eval/R5_LLM_PILOT_PROTOCOL.md)、
> [`R5_BASELINE.md`](../reviews/R5_BASELINE.md)。
>
> **没有任何步骤可以在未获授权时自动开始。** 所有 L3 结论在本轮一律 `NOT_RUN`。

## 0. 用户授权清单（全部勾选才可开始）

- [ ] 授权**启动/关闭游戏**（含允许的启动方式与次数）。
- [ ] 授权**新的专用存档槽**（记录原生槽号；明确不触碰 `save_11*`、`save_0/1/3/4/7`、`save_13`、槽 53）。
- [ ] 授权**真实写操作**（`run_mode=test` 或 `play`，标明哪一种）。
- [ ] 授权**安装/替换 DLL**（允许备份与回滚）。
- [ ] 授权**最大运行时间 / 最大动作数 / 是否允许重启进程**。
- [ ] 确认本轮**不发布 Workshop**、不跑 `dev/scan`、不改 manifest 绕过 scanner。
- [ ] 若使用 `press`（test-only）→ 该 run 必须标 `TEST_MODE_ONLY`，不得作为正式 benchmark。

## 1. 前置核验（D0，任一不符即停止）

```text
1) 游戏内 /health 与 MCP 侧：
   - game_version = "VERSION 100-Rev13"
   - game_mvid    与安装的 Assembly-CSharp.dll 一致
   - policy_sha256 两侧一致（A/B 段后由 tools/gen_policy_catalog.py 生成值）
   - run_mode / recording_mode / recording_slot 与本次授权一致
2) 代码与构建：
   - git rev-parse HEAD（记录 SHA；工作树 dirty diff 必须记录并说明）
   - dist/PawnMind/PawnMind.dll 与将安装文件逐字节一致（SHA256）
   - 工具面：full=117 / recording=107（B1 落地后；以实际注册与 policy 校验为准）
   - 弹窗/覆盖层 typed 路径：`get_pending_prompt` 的 `kind`/`target`/enabled 字段与 `close_windows` 的 `overlays` 已就绪；Play 仍需 promotion
   - trace validator v1.3.0 / trace schema v2
3) 存档：
   - 记录目标专用槽的原生索引与"游戏下一个自动分配槽"（必须相等）
   - 记录现有玩家存档列表（只读；不打开、不写入）
4) 备份：
   - 备份当前已安装 PawnMind.dll → _internal/install-backup-<date>/
   - 记录备份路径与字节 SHA
```

**停止条件**：版本/MVID/policy SHA/槽绑定/run identity 任一无法核对 → 不开始写操作。

## 2. 安装与回滚

```text
安装：
  cp dist/PawnMind/PawnMind.dll "<游戏>/Mods/PawnMind/PawnMind.dll"
  校验安装后 SHA256 == dist SHA256
  bridge.conf: run_mode=<authorized>; recording_mode=recording;
               recording_slot=<游戏下一个自动分配槽>（禁止任意传 slot 绕过权限）
回滚：
  退出游戏 → 用备份 DLL 覆盖 → 校验 SHA256 → 记录回滚时间与原因
  （不自动改存档、不清理旧槽、不删除证据）
```

## 3. 真机验收步骤（每步都记录请求/回执原文）

> 所有写操作必须带 `action_id` 与 `rev`；`uncertain` 一律先查询 `get_operation`，
> 不得用新 `action_id` 盲重试。以下预期字段来自 R5-A/B 的离线契约（L1），
> **真机是否成立 = 本会话要证明的事**。

### S1. 启动与只读健康检查（不写）

```text
get_state                      → in_game, day, cash, rent, shop_state, prompt
/health                        → policy_sha256, game_version, game_mvid 一致
get_recording_save_status      → next_free_slot == recording_slot
list_goods / get_pending_prompt（只读）
```

### S2. 新局 + 首次 prompt（R5-A1）

```text
1) 原生菜单走到 difficulty（specialization/perks/difficulty 用现有菜单动作）
2) get_new_game_options → 选择项可见/可用
3) confirm_new_game（第一次，触发 first-run prompt）
   预期：ok:true, prompt_confirmed:true, run_change:"not_issued",
        run_change_status 字段不存在（prompt 不是 run change）
4) confirm_new_game（第二次，真实开局）
   预期：start_requested:true, run_change:"issued",
        run_change_status: requested→confirmed（只有真实 run identity 出现才 confirmed）
   反向：若第 2 步之后直接再发 start 而被 run_change_pending 拒绝 = FAIL（旧缺陷回归）
5) 若 run identity 读不到：预期 unknown，不得报 confirmed
```
证据：两次响应原文 + `get_operation` 回查 + run-id 读回；旧证据不因 prompt 被驱逐。

### S3. 日终/协商确认框（R5-B2）：

```text
1) get_pending_prompt
   若协商确认框打开 → 预期 kind:"negotiation_confirm",
        target:"negotiation_tablet_confirm", body/confirm_text/cancel_text 可读,
        confirm_enabled/cancel_enabled 反映原生按钮
   若两个模态同时打开 → 预期 prompt_ambiguous（拒绝回答，不猜测）
2) answer_prompt(choice="confirm"|"cancel")
   预期：ok:true, dispatched:true, prompt_closed:true, state.shop_state 回读
   反向：
   - 无弹窗时 answer_prompt → no_prompt / 0 次 native click
   - 面板未关闭 → prompt_readback_mismatch（不是 ok）
   - 模态已变化 → prompt_changed + dispatched:false（未点击）
3) 正式 Play 路径禁用 press：记录 press 是否被拒（press_forbidden）
```

### S4. 覆盖层 typed dismiss（R5-B2b）

```text
在出现教程覆盖层/对话与打字机设置对话框时：
  close_windows(rev)
  预期：closed 列表含 "overlays"；remaining 干净；不需要 press
反向：overlay 仍在 → readback_mismatch（不是 ok）
```

### S5. sleep 异步收敛（R5-A2；与真机耗时校准）

```text
1) end_day → 等 shop_closed_after=true（running 不得当成功）
2) sleep → get_operation(op_id) 连续回读：
   - 10 秒观察窗内 day 未变：仍 running/未决（不得出现 failed/day_not_changed）
   - day 真实增长 → completed（记录从派发到 day 变化的耗时，3 次样本）
   - 超过观察窗且 day 未变、无强证据 → uncertain/sleep_unconfirmed（非终态）
     之后若 day 真实增长 → completed（安全恢复）
3) sleep 卡住时 get_operation("*")：
   预期 count/total_non_terminal/truncated/truncation_note 字段存在
4) 重开进程/换局后：旧 sleep 不得被新 run 的 day 数判完成
```
产出：真机 sleep 时延样本（用于校准 10s 窗口）。

### S6. 交易 staging（R5-B1；2 位顾客、多笔成交）

```text
准备：同一顾客需要 A、B 两件可交易商品（在后台/称重台或可从背包放上）
1) stage_negotiation_item(itemA) → completed：
   staged_item_id_before/after 正确、negotiation_open:true、client_identity_unchanged:true
2) confirm_trade(itemA) → completed（单笔证据：items_transferred 或 cash_moved）
3) stage_negotiation_item(itemB) → before=A, after=B（同一顾客切商品）
4) stage_negotiation_item(itemA) → before=B, after=A（A→B→A）
5) 买入方向同样测一次
反向（必须 rejected，且 0 次 native dispatch）：
   - item 不在可放置容器 / 已被卖 / 不属于玩家
   - 模态窗口遮挡、UI 不可读、stale rev
   - 同一 item+args 的 uncertain 不得用新 action_id 重试
```
产出：每位顾客 ≥2 笔真实成交、A→B→A 证据、staging 失败路径。

### S7. 交易证据与预算（R5-A3）

```text
1) partial sale：business_complete=false 且物品移动 → completed
2) buy：staged 指针不变但现金变动 → completed
3) 顾客预算不足（可见 client.budget < 议价台价格）：
   预期 client_budget_insufficient + dispatched:false + 无 native click
4) visit complete 但无移动：预期 uncertain/no_trade_evidence（不得 ok）
```
产出：现金/物品/库存前后回读与响应逐笔对应。

### S8. 可信轨迹 + 导出（R5-C2）

```text
1) 全程 JSONL 记录器开启：每 call 的 intent/result/recovery/stop/intervention
2) 结束导出 trace（schema_version 2）：
   python agent/trace_validator.py <trace>  → 0 ERROR
   所有 WARN 逐条披露；人工介入次数/类型如实记录
3) 随机抽 3 步：原始 request/result 与导出 trace 字节级一致
```

### S9. LLM pilot（仅 D1a 授权时）

```text
按 R5_LLM_PILOT_PROTOCOL.md：登记 model_id/host/prompt/skill/policy SHA/工具面/
recording_slot/预算；LLM 自行选择动作（不是 week.py 预编排）；达到预算或失败即安全停止；
最终判定只用 JSONL + 真机回执 + validator。
若 Play allowlist 为空 → 终态 BLOCKED_BY_PLAY_POLICY（不是 FAIL/PASS）。
```

## 4. 证据采集清单（每次会话）

| # | 证据 | 位置/形式 |
|---|---|---|
| 1 | 授权记录（用户原话摘要 + 时间） | 会话日志 |
| 2 | HEAD / dirty diff / dist DLL SHA / 安装后 SHA | 文本 |
| 3 | `/health` 原文（version/mvid/policy_sha256/run_mode） | JSON |
| 4 | 工具面清单（117/107 与 policy 校验） | JSON/日志 |
| 5 | 每次写操作的请求/响应原文 + `get_operation` 回查 | JSONL（C2 记录器） |
| 6 | 交易/租金/关店/换天回执 | 响应字段 + 状态前后 |
| 7 | 导出的 validator trace + 0 ERROR 报告 | JSON + CLI 输出 |
| 8 | 人工介入表（时间/原因/动作） | 表格 |
| 9 | 成本/时长/动作数（不可测写 NOT_MEASURED） | 汇总表 |
| 10 | 回滚记录（若发生） | 文本 |

## 5. 停止与回滚（立即执行）

- 无法核对版本/MVID/policy SHA/录制槽绑定/目标 run identity；
- `dispatched=unknown` / `execution_status=uncertain` 且不能可靠收敛；
- sleep 可能已执行但 Day 不可读（重复推进风险）；
- 交易现金/物品与证据不一致或重复交易风险；
- 弹窗文本不可读、无法确认原生控件身份；
- 工具权限越界、可能影响非专用存档、意外开放 dev/test；
- 达到预算（动作/成本/时长/连续失败）；
- 轨迹无法保存或记录器失败。

**回滚**：退出游戏 → 备份 DLL 还原并校验 SHA → 保留全部日志/轨迹 → 报告用户。
**禁止**：改存档、删证据、清理旧槽、为了“通过”而放宽断言或改配置绕过门禁。

## 6. 本轮（R5 跨夜）明确未验证项

| 项 | 状态 |
|---|---|
| 以上 S1–S9 全部真机步骤 | **NOT_RUN**（无 Windows/游戏/授权） |
| 正式 Play benchmark / 通关率 | **BLOCKED_BY_PLAY_POLICY**（`play_allowlist` 为空） |
| 真机 press 以外路径的可用性（A1/A2/B1/B2/B2b） | **NOT_RUN** |
| 真机 sleep 时延样本 / staging 真实行为 / 预算拒绝真实行为 | **NOT_RUN** |
| 历史首周运行原始轨迹复核 | **NOT_RUN**（证据缺口 E2） |
