本次是**试跑（dry run）**，不是正式录制。

## 与本目录 AGENTS.md 的差异（仅本次）

- 目标不是"交上第一周租金"，而是**在真实游戏上把关键链路和从未验证的工具跑一遍**，找出坏掉的地方。
- `play/AGENTS.md` 里"等用户明确同意再 continue"这一步**已被用户授权跳过**：你可以直接选场景/特质/难度并 `confirm_new_game` 开局，但其余安全规则（单步、回读、rev、不重试未知结果、停机条件）**全部继续有效**。
- 不追求利润，不追求把一周玩完。**跑完下面清单就停**。

## 已确认的环境

- 录制槽 40 已 armed，`next_free_slot=40`，`in_game=false`（主菜单）。
- 这一局会在槽 40 建一个**临时存档**，试跑结束后由用户手动删除；所以**不要在游戏里做任何"不可逆"的事**（不要删除任何档、不要改设置）。

## 试跑清单（按顺序，每步都回读；某步失败就记录错误码并跳到下一步）

### A. 开局链路（最重要）
1. `get_state` 确认 `in_game=false`、记下 `rev`。
2. `get_new_game_options` 确认 `phase=closed`。
3. `get_recording_save_status` 确认 `armed`、`configured_slot=40`。
4. `open_new_game_menu` → 回读，确认 `phase=specialization`；记录**场景页一共几个选项、当前页有几个、Continue 是否可用**。
5. 选第一个 `enabled=true` 的场景 → 回读确认 `selected_id`。
6. `new_game_continue` → 回读，确认 `phase=perks`；记录特质页**已选数/上限/剩余点数**，以及是否 `first_run=true`（如果是，说明这局不能选特质，是重要发现）。
7. 若可选：选 1–2 个便宜特质 → 回读。
8. `new_game_continue` → 回读，确认 `phase=difficulty`；记录难度页选项。
9. 选一个难度 → 回读确认。
10. `confirm_new_game` **只调一次** → 记录返回（`start_requested` / `bound` / `binding.slot`）。
11. 轮询 `get_recording_save_status` 到 `bound=true`，确认 `binding.slot == 40`、`slot_matches/run_matches`。
12. `get_state` 确认 `in_game=true`、`day`、`cash`、`rent.due`、`rent.days_left`。

### B. 交互与观察
13. `list_goods(scope="all")`、`list_containers`、`list_inventory(container="main")`、`get_layout(container="main")`。
14. 若有可买物：`confirm_trade` 买 1 件最便宜的 → 回读现金与库存；再 `move_item` 把它从 back 移到 main（或反向），确认形状/位置返回正常。
15. `open_shop` → `call_next_customer`（若可用）→ 读对话与 `choices[].enabled`。
16. 若有顾客对话：用 `advance_dialogue` / `choose_dialogue` 走完一轮。

### C. 系统探路（有就试一次，没有就记"当前不可用"，不要硬买）
17. `list_books`；若有书 → `read_book(book_id, page=0)` 读一页。
18. `list_machines`；若有 → `machine_status`，再 `machine_insert` 一件物品试一次。
19. `list_wine`；若有酒瓶 → `wine_status`，再 `wine_add_ingredient` 试一次。
20. `list_upgrades`（只看，不要买）。
21. `list_services`、`list_contacts`、`list_locations`（只看）。
22. `lottery_status`、`list_tickets`（只看）。
23. `list_analyzers`；若有分析仪且背包里有可测物 → `analyze_item` 一次。
24. `list_requests`（只看）。
25. `go_out` 到任一已探索地点 → `scavenge` 一次 → `return_home`（若任一失败，记录错误码并回到店里）。

### D. 跨日
26. `end_day` → 回读。
27. `sleep` → 回读；若 `day` 未立即增加，用 `wait_for_events` 等一次再看。
28. `get_state` 确认 `day`、`cash`、`rent.days_left`。
29. 再 `sleep` 一次（第二天）确认可重复跨日。

### E. 取证
30. 每一步遇到 `ok:false` 时，如果错误码属于硬失败，先 `capture_screenshot(reason="...")`。
31. 结束时 `list_screenshots(limit=10)` 确认截图数量与大小。

## 预算与停止

- 总工具调用 **≤ 90 次**。到 80 次时开始收尾。
- 遇到 `stale_rev` / `outcome_unknown` / `recording_*` / 连续两次同一拒绝 → **立即停机**，不要再试。
- 不要为了"跑完清单"而重复调用失败的工具；每个系统失败一次就跳过。

## 输出

最后用这个结构报告（中文，简洁）：

```
试跑结果：完成 / 提前停机（原因）
开局：场景 / 难度 / 特质（或 first_run 锁定）/ 绑定槽 /
第 1 天：现金、租金、天数
系统清单：
  - <系统名>：可用 / 失败（错误码）/ 未解锁（原因）
异常：错误码列表、任何与预期不符的返回
截图：关键截图文件名
调用数：N
```
