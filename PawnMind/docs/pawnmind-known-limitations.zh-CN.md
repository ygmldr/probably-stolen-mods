# PawnMind 已知限制

> 版本分节（不擦除历史）：**§1–§10 是 2026-11-10 基线原文（保留）**；**§11 是 2026-11-19 R5-A 离线修订**。
> 两处冲突时以 §11 为准，旧原文不删除。每条都对应代码里的实际行为，不是“以后可能”。
> 当前游戏版本 0.46D / 100-REV6（旧档记录），其他版本未验证。
> 证据等级：**L3 真机 = 本轮 `NOT_RUN`**（无 Windows/游戏进程/授权）；
> R4 真机结论来自开发者报告（`REPORTED`），未由本文档作者复核；离线源码/C#/Python 单测为 L1/L2。

---

# 第一部分：2026-11-10 基线（历史原文，保留）

> 以下 §1–§10 为 2026-11-10 原文。其中“Python 8 项”“只验证顾客卖→玩家买”“未跨多日”等表述反映当时的证据状态，
> **已过时**；请对照 §11 的 2026-11-19 修订阅读，不要直接把旧条目当当前事实。

## 1. 布局 / 形状

- `list_inventory` / `get_item` 的 `x/y/width/height` 是**包围盒**；真实占格看 `mask` 和 `cells`。
- `get_layout` 的 `rows` 是每格**最上层**的 item id；同一格不同 layer 的完整占用在 `cells`（含 layer）里。
- `get_layout` 只支持格子容器（main/front/back/showcase/hidden/afterhour）；垃圾桶/已售这类槽位容器返回 `not_a_grid_container`。
- `move_item` 的“右下角”按包围盒算；异形物品可能视觉上有空角。
- 旋转支持 0/90/180/270（`turns=0..3`）；异形旋转后的包围盒/锚点仍按 bbox。
- 指定位置放置失败会回滚；但“自动摆放失败 → 调 CompactSort 重排再试”还没接。
- 机器内部容器（熔炉/水培/打印机等）没有纳入 `list_containers`。

## 2. 买卖

- 只验证过“顾客卖 → 玩家买”；`sell` 代码存在但未真机验证。
- 商品目录只覆盖顾客前台/后台物品；补给站（Commissary）、博士（Dr. Jackson）、批发（wholesale）还没接。
- 卖价只能走砍价面板 10/25/50/75，不支持自由定价。
- 砍价接口返回成功/失败，但**成功率、走人率、声望增减**还没暴露。
- 批量买/卖（buy_all/sell_all）未实现。

## 3. 夜间

- `list_locations` / `go_out` / `scavenge` / `commissary_*` / `return_home` 全部未实现。
- 拾荒的受伤/空手/时间限制、事件关闭地点都未接。

## 4. 阅读

- `list_books` / `read_book` / `read_item` 未实现。
- 只计划支持已拥有/已解锁的书；不读隐藏鉴定答案。

## 5. 视觉 / 鉴定

- 明确不做截图/多模态；不调用 `AppraisalHelper.GetCorrectBaseValue`。
- 鉴定相关只保留文本级物品详情。

## 6. 调试 / 兼容

- F7 只在游戏窗口聚焦时生效；`debug_pulse` 的日志行这次没出现在 `Player.log`（`debug_dump` 文件正常）。
- 不注入系统键鼠；所有自动化走 HTTP。
- 全主线程非阻塞 HTTP，无 Thread/ThreadPool/Task；`threads` 退役项在扫描中**未命中**
  （`ScreenshotService` 用的是自有 `MonoBehaviour` 的 `StartCoroutine`，官方规则属免费项）。
- `load_game` 只支持菜单读档；不会主动读玩家存档列表。
- 测试档会新建存档槽；旧档不会被修改。

## 7. 条件性未验证（2026-11-10）

- 批发成功扣款：未遇到“有货 + isOfferWholesale”的顾客；代码按 OnWholesaleClick 复刻，错误路径 `no_items` 已验证。
- 补给站购买：`redeemed_credit` 需要食品券兑换；测试档为 0，`insufficient_credit` 已验证。
- `end_day` 在部分店态返回 `cannot_end_day`；`sleep` 可推进，已记录。

## 8. 测试覆盖

- C# 纯逻辑 + 适配器契约 + Python 8 项；真机只覆盖了“新档第 1 天：开店/叫客/对话/买一件/睡觉/移动/旋转/布局”。
- 未覆盖：多顾客、砍价、卖出、夜间、阅读、跨多日、异常回滚、Mod 组合。


## 9. P6–P10 扩展限制（2026-11-10）

- 化扫未真机；植物分析仪依赖 UI 文本。
- 标签条件 ID 是本地化显示串。
- WildNetworkUI 本体未打开，用 `list_upgrades/buy_upgrade` 替代。
- 彩票测试档未解锁、无中奖号，兑奖成功路径未验。
- 远征第二次 `advance_expedition` 触发游戏 POI NRE，已 `EndExpedition` 收尾。
- 安检/突袭、天气、陈酿架、养殖投喂未接。
- 需求/通缉/Barter 成功路径未遇到样本。

- 突袭战斗未接（RaidManager 需要 RaidScene，未暴露只读入口）。
- 陈酿 1 天无品质变化，需多日验证。

## 10. 扫描器与发布阻塞（2026-11-18 实测）

官方 `mod-scan pawnmind`（游戏 F8 控制台，原始输出见 `docs/audit/MOD_SCANNER_ROUND4.md` §6）：

```
declared: network, reflection-external
detected: files, network, reflection-external
UNDECLARED (enable would be refused): files
findings (41): files 5 / network 22 / reflection-external 14
DEPRECATED: reflection-external refused by the game update of December 10, 2026
```

三条独立阻塞：

1. **今天：`files` 未声明** → 普通玩家激活会被拒。本机之所以能跑，是因为
   `trust=DeveloperMode`（开发者模式跳过未声明能力检查）。修法二选一：
   在 `manifest.xml` 声明 `files`（并披露截图 + 存档索引读取），或去掉
   `ScreenCapture` 与 `System.IO`（但 2026-12-10 起 `SaveFiles.IndexLoad` 仍计 `files`）。
2. **2026-12-10 起：`reflection-external` 被拒**。14 条里 10 条可用"调用点内联
   `typeof(...)`"消除；剩下 **4 条结构性不可消除**——`NewGamePerksUI.SlotView` 是
   私有嵌套类，外部拿不到 `typeof(SlotView)`，只能 `view.GetType()`。
   需要游戏作者提供公开访问器，或放弃"逐行 perk 卡片"读取。
3. **工坊：`network` 需要"开发者已验证"**（扫描器 `VERIFICATION` 行）。
   本地 `Mods/` 安装不受影响；上工坊则需申请验证。

> 结论：**PawnMind 目前只能本地使用**；工坊发布在 (1)(2)(3) 解决前是 BLOCKED。
> 2026-11-18 决定：三项均暂缓，优先做玩法；清单与 `src/Game` 暂不改动。

### 10.1 会话/续档限制（2026-11-18 实测）

- **录制绑定只存在内存**：Mod 进程重启后 `load_recording_game` 返回
  `recording_slot_not_bound`，槽里那一局**读不回来**；只能再开一个新局（新槽）。
  一个 Mod 进程 = 一局，中途不能重启。
- **`press` 只在 `run_mode=test` 下、且只对三个覆盖层按钮放行**
  （`_ControlOverLay/CloseOverlayButton`、`_DialogueOptionUI/Panel/CloseButton`、
  `ApplyButton`）。其余控件 `control_not_allowed`；其它模式 `press_forbidden`。

---

# 第二部分：2026-11-19 R5-A/B 离线修订（历史口径更新，L3 一律 NOT_RUN）

## 11. R5-A 协议/证据口径更新

> 依据：`docs/reviews/R5_HANDOFF_2026-11-19.md` §2、`docs/reviews/R5_BASELINE.md`、
> `agent/trace_validator.py`（`VALIDATOR_VERSION = "1.3.0"`，trace schema 2；1.2.0 为 R5-A 时的版本，
> 1.3.0 对齐 R5-B1 的 call-scoped staging 判定）、`skills/pawnmind-first-week/SKILL.md`。
> 真机（L3）证据：本轮 **`NOT_RUN`**；R5-A/B 只有源码 + 单测/伪端口证据。

### 11.1 交易：整趟访问完成 ≠ 单笔成交（R5/A3）

- 单笔 `confirm_trade` 的 `completed` 判据：`client_unchanged == true` 且
  （`items_transferred == true` **或** `cash_moved == true`），`evidence` 对象需与顶层证据一致。
- `business_complete` / `visit_complete` 只描述“这位顾客整趟逛完了没有”，是**辅助归因**，
  **不能单独证明单笔成交**：`business_complete:true` 且零 item/零现金变化必须 `uncertain`。
- 反向保留 R4 已证实行为：`business_complete:false` 的真实 partial trade（顾客还要继续买）仍应 `completed`。
- 证据漂移：旧 validator（`1.1.0`）和旧 Skill §5.2 把 `business_complete:true` 当成交必要条件，**该口径作废**；
  trace 用 `schema_version: 2` 表达新口径，无版本历史 trace 按 v1 校验并给 `TRACE_SCHEMA_VERSION_MISSING` warning。
- 旧的“只验证顾客卖→玩家买”已被 R4 报告（买卖两向、首周 8+16+8 笔）覆盖主张，但**原始轨迹缺失**
  （`R5_BASELINE.md` E1/E2：32 vs 约 45 笔口径冲突、无 JSONL），仍只能标 `REPORTED`，不升级为 `VERIFIED`；
  买卖成功路径的 L3 复核 = `NOT_RUN`。

### 11.2 开局/跨局：first-run prompt 不是 run start（R5/A1）

- `confirm_new_game` 有两个子动作：**first-run prompt 确认**（`prompt_confirmed:true`，`run_change:"not_issued"`；
  UI 操作，不登记 run change、不会永久阻塞后续开局）与**真正新局 start**（`run_change:"issued"`，进入防盲重试）。
- prompt-only 结果**不带** `run_change_status`/`run_change_confirmed`；`run_change_status:"confirmed"` 只能由
  真实 run identity 读回产生（revision/day/scene 不能替代）。
- 旧 Skill §1.2 “`confirm_new_game` 整局只调一次”作废：它把 prompt 确认与 run start 混为一谈。
- 已知风险：pre-A1 轨迹可能在 prompt 确认上残留 `run_change_status:"requested"`；按 `prompt_confirmed` 为准。
- 录制绑定只存在内存、重启后读不回的问题（§10.1）**未修复**；L3 复核 = `NOT_RUN`。

### 11.3 睡觉/异步：没有“轮询 N 次就判失败/安全重试”（R5/A2）

- `sleep` 的 `day` 不变只是**尚未观测到完成（inconclusive）**，不是失败；bridge 用 10s 单调时钟观察窗口，
  窗口内无判决，超时无强证据 → `execution_status:"uncertain"` + `error:"sleep_unconfirmed"`。
- `uncertain` 非终态：同 op 用新 `action_id` 再发会被 `operation_pending` 拒绝（防双推进）；后续观测到 day 增加
  仍可 promote `completed`。只有可证明未派发（`native_not_called` + `dispatched:false`）才允许安全重试。
- 旧口径“`sleepPolls >= 3` 且 day 未变 → `failed("day_not_changed")`”、“连续 `get_operation(id)` 两三次即收敛”
  **作废**；轮询次数不是证据。
- `get_operation("*")`：非终态列表上限 **20 条**，R5/A2 后响应带 `count`/`total_non_terminal`/`truncated`/
  `truncation_note`；旧构建可能没有 `truncated` 字段，`count == 20` 时按“可能被截断”处理，不能当完整证据。
- L3 复核 = `NOT_RUN`。

### 11.4 UI 自动化 / press

- `press` / `act` 是 **test-only** 通道（`run_mode=test` + `UiPressPolicy` 白名单），
  **正式 Play 永远不依赖、不放开通用按键**；若是某工作流“必须 press 才能过”，停止并报告，不要把它写成 Play 能力。
- §10.1 历史文本写“只对三个覆盖层按钮放行”；R4 代码 `UiPressPolicy` 实际已有**第四个日终确认路径**（`/Confirm/Buttons/Button`）。
  该 test-only 路径不是 Play 能力；`UiPressPolicy` 注释已在 `c3f34bb6` 同步（四个路径 + test-only 标识）。
- 日终 typed 玩家等价动作（R5/B2）：**已实现并合入**（`a0ac8848`，`get_pending_prompt`/`answer_prompt`
  的 `negotiation_confirm` 模态 + `close_windows` 的 typed overlay dismissal `643003c9`），离线 L1 测试通过；
  **真机 L3 = `NOT_RUN`**（无 Windows 授权）。typed 路径已就绪，但正式 Play 仍
  `BLOCKED_BY_PLAY_POLICY`（`play_allowlist` 为空，需 L3 逐工具证据 + promotion）；
  当前 test 模式/离线路径上，这两类阻塞不再需要 `press`。

### 11.5 测试覆盖（旧 §8 的 “Python 8 项” 已过时）

- R5 启动基线（2026-11-18T19:16Z，`R5_BASELINE.md` 实测）：C# 纯逻辑 **794/0**、脚本门禁 **53/0**、
  适配器契约 **506**、Python **377/0**、policy catalog 一致。
- R5-A4（2026-11-19）：`agent/tests/test_trace_validator.py` 扩到 **115 项**（含 `agent/tests/fixtures/` 6 条正反轨迹），
  全 Python 套件以本轮实跑输出为准；C# 侧 R5-A 数字以 Lead 报告为准（未由本文档作者复核）。
- R5-B1（2026-11-19，提交 `704b5b88`）：新增 `stage_negotiation_item`（full=117 / recording=107，policy sha256
  `774fbfdd…`）；validator 1.3.0 起把该 op 纳入 call-scoped 冲突判定；`agent/tests/test_trace_validator.py` 117 项。
- R5-C（审计记录器/决策接口/fake 六情景）：`agent/trace_recorder.py`、`agent/decision_interface.py`、
  `agent/fake_game.py` 场景测试；均为 **L1-fake/离线**，`L3 NOT_RUN`；没有 LLM 真机 pilot。
- 仍未覆盖/未验证：多日真实经营、Play 模式、typed 日终弹窗真机、staging 真机行为、重启续档、跨局真机对账。
  全部 `NOT_RUN` 或 `REPORTED`。
- 正式 Play benchmark：`play_allowlist` 仍为空 → 终态 `BLOCKED_BY_PLAY_POLICY`（不是 PASS，也不是 FAIL）。
