# 首周真机会话报告 · 2026-11-18 → 11-19

> 一句话：**PawnMind 第一次在真机上独立完成「开新局 → 经营 7 天 → 付掉租金」**，
> 过程中暴露的 7 个缺陷全部修掉并留了测试；另有 2 处错误判断被回滚，教训写在第 4 节。

| 项 | 值 |
|---|---|
| 会话时间 | 2026-11-18 23:20 → 2026-11-19 02:52（约 3.5 小时） |
| 开局次数 | 10 次（槽 43 → 53，每修一处缺陷就要重启一次） |
| 最终结果 | **首周完成**：Day 7 租金 200 已付，凭据完整 |
| 代码 | 9 个提交，C# 794 passed / 0 failed，Python 377 OK，契约 506，门禁 53 |
| 结束状态 | 游戏已关闭，`dev.txt` 已删除，安装在 `25c7dbf2…` |

---

## 1. 时间线

| 时间 | 动作 | 结果 |
|---|---|---|
| 23:20 | 装 Round 4 构建 | 桥可跑，`run_mode=test` |
| 23:3x | 开 dev 控制台跑 `mod-scan` | 41 条命中：`files` 5 / `reflection-external` 14 / `network` 22；Gate C 保持 BLOCKED |
| 23:5x | 槽 43 首次开局并做第一笔交易 | 卖出成功（700→729），但报 `uncertain`，随后交易被锁死 |
| 00:0x | 修「按笔判定交易证据」+「跨局确认」 | 重建、装 `abc9e57e…` |
| 00:1x | 槽 44 重开 | 发现买入方向同样误判 |
| 00:3x | 修「现金变动也算成交」+ 补 `press` dispatch | 装 `8f719cb3…` |
| 00:4x | 槽 47 | `end_day` 卡住 → 发现「一次 uncertain 锁死整个 op 类」 |
| 00:5x | 改按「op + 参数指纹」阻塞 + sleep verifier | 装 `16ce7b31…` |
| 01:0x | 槽 48 | sleep 仍会卡；定位到「卡住的操作掉出 history 后拿不回 id」 |
| 01:1x | `get_operation("*")` + 回滚错误的 `EndOfDayUIManager.IsOpen` 闸门 | 装 `67fd9090…` |
| 01:2x | 槽 49 → 通过 Steam 重启才起来 | 退出码 53 = Steam 重拉失败 |
| 02:0x | 槽 50 / 51 / 52 反复 | 定位到「日终确认框无人能按」→ 加进 `press` 白名单 |
| 02:39 | 槽 53 重开 | — |
| 02:52 | **Day 7 结束，租金付掉** | 首周完成 |

> 说明：中途用户口头授权「你可以自己关自己开的」，本次会话内的关/开游戏由 agent 执行。
> 这条授权仅限本次会话，不改变项目里「不代替玩家启动游戏」的默认约定。

---

## 2. 修掉的缺陷

| # | 现象 | 根因 | 修法 | 测试 |
|---|---|---|---|---|
| 1 | 一笔成交后报告 `uncertain`，随后所有交易被锁 | 交易证据用的是「整趟访问完成」（`business_complete`），而一位顾客可以连买多件 | 拆成 `business_complete`（整趟）与「这一笔」（物品移动或现金变动），且必须同一顾客 | `R4_TRADE_01/02/03`、`PP_TRADE_*` |
| 2 | 买入方向仍判 `uncertain` | 买下的商品仍留在议价台，`currentNegociatedItem` 不变 → `items_transferred=false` | 现金变动同样算作这一笔的证据 | `R4_TRADE_04/05` |
| 3 | 新开局 `run_change_status` 永远 `unknown` | 没有存档时 `ReadRunId()` 返回空，无法区分「确定没有 run」和「读不到」 | `RunChangePolicy.NoRun` 哨兵；`NoRun → real` 判 confirmed | `R4_F1_11` 等 |
| 4 | `press` 报 `unknown_op` | 守卫一直拦截，mod 端从没有这个 dispatch 分支 | 补 `case "press"` + `UiPressPolicy` 白名单 | `UiPressPolicyTests`、`Guard_Press_Is_Released_Only_In_Test_Mode` |
| 5 | 一次 `uncertain` 让**换一件商品**也被拒 | `FindPendingFor` 只按 op 名匹配 | 收窄到「op + 参数指纹」，仅 `confirm_trade` 走这条规则 | `R4_TRADE_06` |
| 6 | `sleep` 卡在 `running` 后整局报废 | 点击被模态面板吞掉，操作永不收敛 | `TryResolveSleep` 按 day 回读收敛；`get_operation("*")` 列出非终态操作 | `Sleep_Verifier_*`、`R4_F1_14` |
| 7 | 第 7 天队伍被「荒野网络」窗口挡住 | `close_windows` 只认 `PaperUIManager` 的面板 | 新增 `WindowKinds.Custom` → `CustomUIManager.CloseAllWindows()` | `Custom_Windows_Are_Closed_With_CloseAllWindows` |

另外两处小修：

- 「日终确认框」（`NegotiationTablet/Confirm`）没有任何类型化入口，`answer_prompt` 报 `no_prompt`
  → 加进 `press` 白名单，按快照里的文本选「是」。
- 测试夹具 `dead_config()` 硬编码端口 15528，和真机桥撞车 → 改成探测空闲端口。

---

## 3. 首周结果

```
槽 53 · Day 1 现金 700 → Day 8 现金 88
租金 200 已付：rent_paid_total 200 / weeks_paid 1 / weeks_missed 0
下周租金 209（涨了），days_left 6
```

支付凭据（`get_state().rent.last_payment`）：

```json
{
  "id": "rent_1_200", "amount": 200,
  "cash_before": 293, "cash_after": 93, "cash_delta": -200,
  "rent_days_left_before": 0, "rent_days_left_after": 7,
  "source": "dialogue_choice", "day": 7, "paid_at_rev": 417,
  "weeks_paid_delta": 1, "detection_complete": true
}
```

第 7 天的钱是走**房东对话**扣的：桥在 `choose_dialogue` 里选了付钱那一项。这条链路此前从未在真机验证。

逐日：成交 **8 / 16 / 8 / 0 / 0 / 0 / 0** 笔（共 32 笔，买卖各半）。

### 后 4 天为什么没成交

`no_negotiation` / `item_not_staged` 占了 Day 4 之后的全部尝试。原因是脚本的
「把商品拖到议价台」两步法（`move main → back`）在**同一位顾客连续多笔**时
切不动已暂存的商品。桥的拒绝是正确且安全的；这是**脚本层缺陷**，见第 5 节。

---

## 4. 两次错误判断（都已回滚）

### 4.1 `EndOfDayUIManager.IsOpen` 当闸门

想用「日终面板是否打开」来防止空点击，结果装机后**每次 `sleep` 都被拒**
（`end_of_day_panel_closed`），而打补丁之前同样的局面睡眠是成功的。反编译：

```csharp
public bool IsOpen { get { if (UI != null) return UI.activeSelf; return false; } }
```

`UI` 是序列化字段，游戏里没接线，恒为 null → `IsOpen` **恒为 false**。

### 4.2 `CustomUIManager.HasEscapeTarget()` 当检测

`close_windows` 用它判断「有没有自定义窗口」，结果荒野网络那个窗口
`closeOnEscape` 没设，检测恒为 false，窗口永远关不掉。

### 教训

**契约测试只能证明成员存在，证明不了它在这个版本里被赋值。**
用任何新读数字段去**否决**一条本来可用的路径之前，必须先在真机上确认它在
「已知可用」的情形下确实为真；否则宁可不预判，改做「可发现 + 可收敛」
（这正是 `get_operation("*")` + sleep verifier 的价值）。

---

## 5. 仍未解决

| 优先级 | 问题 | 建议 |
|---|---|---|
| 高 | 交易 staging 不可靠：`move main→back` 不能切换已暂存商品 | 做一等公民 `open_negotiation(item_id)`（会把工具面从 116/106 改成 117/107，policy 哈希变化，需单独一轮）；或在脚本里先读 `negotiated_item` 名称做校验 |
| 中 | 录制绑定只在内存，进程重启即失效 | 把 `recording_slot` 的绑定持久化，允许续跑已保存的局 |
| 中 | 一位顾客买不起时点下去是静默拒绝，产生一个死操作 | 在 guard 里加预算预检（`client.budget` 与议价值比较） |
| 低 | `get_action_history` 只有 100 条 | 已有 `get_operation("*")` 兜底，可不改 |
| — | `files` / `reflection-external` | 用户已决定暂缓，Gate C 保持 BLOCKED |

---

## 6. 收工时的现场

| 项 | 状态 |
|---|---|
| 游戏进程 | 已关闭 |
| `dev.txt` | **已删除**（F9 开发菜单不可用，避免误改存档） |
| 已安装 DLL | `25c7dbf2…`，MVID `3e52e2b4-667e-4d07-a945-60f0923623bc`，逐字节等于 `dist/` |
| `bridge.conf` | `run_mode=test`、`allow_test_mode=true`、`recording_mode=recording`、`recording_slot=53` |
| 存档 | 槽 53 = 首周结束的 Day 8（现金 88）；槽 43–52 是本次会话的试验局 |
| 回滚 | `_internal/install-backup-2026-11-19{a..j}/` 各版本 DLL |

> 若下次不再需要 test 模式，把 `bridge.conf` 里 `run_mode=test` 改成 `locked`
> （并去掉 `allow_test_mode`）即可回到默认全锁状态。

---

## 7. 怎么复现

```bash
# 1) 构建并安装
cd /mnt/d/code/game_mods/PawnMind && ./test.sh && ./build.sh
cp dist/PawnMind/PawnMind.dll "/mnt/d/Program Files/steam/steamapps/common/Probably Stolen/Mods/PawnMind/"

# 2) 配置：run_mode=test / allow_test_mode=true / recording_mode=recording
#    recording_slot 必须等于游戏当前的下一个自动分配槽

# 3) 开游戏到主菜单，然后
cd /mnt/d/code/game_mods/_internal && python3 -c "exec(open('week.py').read()); print(c.action('get_recording_save_status'))"
```

当日脚本在 `_internal/week.py`（gitignored）：`serve()` 经营、`finish_day()`
关店睡觉、`resolve_stuck()` 收敛卡住的操作、`answer_yes()` 按日终确认框。

---

## 8. 提交

```
1721cb4  close CustomUIManager windows so a modal cannot stall the day
e833176  Revert "refuse a no-op sleep" — the signal was wrong
3917f07  refuse a no-op sleep and keep stuck operations reachable
f87bc3a  docs(live): record the two remaining blockers
ac856c5  scope the pending block per call and let sleep resolve
3fa0ff5  count a purchase as a trade, and dispatch press
dc724e2  let test mode dismiss the two adapter-less overlays
e212c20  per-trade evidence, run-start confirmation, honest dead-port fixture
```

相关文档：[LIVE_SESSION_2026-11-18.md](../audit/LIVE_SESSION_2026-11-18.md) ·
[已知限制](../pawnmind-known-limitations.zh-CN.md) ·
[首周 Skill](../../skills/pawnmind-first-week/SKILL.md)
