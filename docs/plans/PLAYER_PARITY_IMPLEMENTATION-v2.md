# 玩家等价实施计划 v2（取代 v1）

- **状态**：Proposed
- **前提**：`docs/ADR-PLAYER-PARITY-v2.md`、`docs/ROUND2_DISPOSITION.md`
- **基线**：commit `3fed61c`；C# 656 / 契约 491 / 脚本门禁 53 / Python 240（含新增 6 条工具策略测试）
- **与 v1 的差异**：新增 **D0a**（快照纯度，排在 D1 前）；D2/D6 的验收不再接受 `not_run`；
  白名单改为**由证据生成**；`get_clientele` 与 `bargain_preview` 移出 strict。

---

## 阶段总览

| 阶段 | 主题 | 阻塞录像 | 出口（硬条件） |
|---|---|---|---|
| **D0** | 二进制一致 + 模式闸门 + 工具登记表 | ✅ | 安装 DLL == `dist` 且 `/health` MVID 相符；普通 op 直连 HTTP 被拒；矩阵无 `unverified` 主路径项 |
| **D0a** | 快照纯度（`get_state` / `TryRefreshRevision`） | ✅ | 快照路径 `Refresh()` 调用数 = 0（Core 断言 + 真机 `PP-READ-01/02`） |
| **D1** | `confirm_trade` 三段式 + operation 两轴 | ✅ | 7 个静默分支各有具名拒绝；成交后 `advance_dialogue` 不被锁；重复提交必拒 |
| **D2** | `end_day`/`sleep`/`open_shop` 完成证据 | ✅ | 真机 `P0-LANDLORD-02`、`P0-OPEN-01`、`PP-ASYNC-01/02` **PASS**（`not_run` 不算） |
| **D3** | 其余伪只读清理 + 字段可见性 | ✅ | strict 内无 `cached_impure`/`mutating`；字段级 visibility 有证据 |
| **D4** | 主路径 B 缺口清扫 | ✅（仅主路径） | 白名单内每个工具的 B 缺口 `fixed` |
| **D5** | L3 UI 对照探针 | ⬜ | 未做则不得宣称"所有工具玩家等价" |
| **D6** | 生成式 allowlist + E2E | ✅ | 白名单由 `tool_policy.strict_allowlist()` 生成并与 `tools/list` 逐名比对 |

---

## D0：一致性与闸门（阻塞）

1. `./build.sh` → 备份 → 覆盖安装 → 重启游戏 → 记录 `/health`（version + MVID + DLL sha256）。
   当前 `dist` = `ad335987…`，已安装 = `e9e1bd07…`，**不一致**。
2. 部署 `ModePolicy`：普通 op 也做模式校验，`act` 展开后再次校验（ADR-PP-11）。
3. 录制面改为 strict 名单：MCP `--tools recording` 与 `strict_allowlist()` 同源生成。
4. 用真实 `tools/list` 重新生成 `docs/audit/tools-inventory.json`，并跑 `test_tool_policy.py`。
5. 复核矩阵里 `unverified=yes` 的 5 行，主路径上的必须降级或禁用。

**证据**：`/health` 输出、`sha256sum` 对比、`tools-inventory.json` 的生成命令与 diff。

---

## D0a：快照纯度（阻塞，先于 D1）

1. 抽出可测的"快照源"接口（Core），把 `CanOffer*`/`CanReport` 的调用移出快照构建路径，
   或改为"仅当谈价面板打开时读取"。
2. Core 测试：构建快照 N 次，断言对 fake 的 `Refresh` 调用计数为 **0**。
3. 真机：`PP-READ-01`（连读 10 次：谈价状态位、UI 对象、RNG 计数不变）、
   `PP-READ-02`（过期 rev 的写动作被拒后同样不变）。

**注意**：不可用"`rev` 没变"代替该断言（`rev` 是序列化指纹，见 ADR-PP-3 v2）。

---

## D1：`confirm_trade`（阻塞）

- 前置：`IsTradeLocked`、`clientIntent`、`isBusinessComplete`、以及 BARTER/INFO/GUNSMITH/BUY/SELL 各自原生谓词。
- 动作：`OnOfferAcceptClick()` 一次。
- 证据：`back/front/main` 的**物品 id 集合** + `isBusinessComplete`；现金仅辅助。
- 完成语义（ADR-PP-9）：`completed` + `workflow.next_required_action="advance_dialogue"`；
  `client_departed` 属工作流终态，不进 operation 状态。
- 测试：`PP-TRADE-01/02/03` + 7 条分支拒绝（每个 `B10–B16` 一条）+ DSH E2E（pending 不被当 `isError`）。

---

## D2：异步完成证据（阻塞）

- 接入 `FireOnShutterClosed*`、`FireOnGoingSleep*`、`FireOnHandlingNightlyServices*`（只读订阅）。
- `sleep` 返回 `day_changed` 与 `save_completed` 两个字段（ADR-PP-12）。
- 真机用例：`P0-LANDLORD-02`、`P0-OPEN-01`、`PP-ASYNC-01`、`PP-ASYNC-02`。
- **`not_run` 不允许作为放行理由**：白名单内工具的真机 P0 用例必须 PASS，否则从白名单移除并说明降级。

---

## D3：其余只读与可见性

- `get_state` 其余 getter 逐个分类（`needs_instrumentation`）。
- 字段级 `visibility`：隐藏字段、未来事件、顾客隐藏身份不得出现（扩展现有 `HiddenInfoGuardTests`）。
- strict 名单不含任何 `cached_impure`/`mutating`。

---

## D4：主路径 B 缺口

只清"一周租金主路径"用到的：`buy_wholesale`（B18）、`barter_accept`（B26）、`move_item`（B29/B30）、
`go_out`（B23/B24）、`advance_dialogue`（B43）、`end_call`（B39）。
非主路径的保持禁用，不为了"全绿"而放宽。

---

## D5：L3 UI 对照（非阻塞）

`PointerInputModule.inputOverride` + `BaseInput` 探针，对 5 个试点动作做差分。
未完成时，**不得**在任何文档/视频里宣称"全部工具玩家等价"。

---

## D6：生成式 allowlist 与 E2E

```python
strict = strict_allowlist(tools_inventory["full"])   # 由注册表生成
assert strict == set(tools_list_recording)           # 与服务器实际导出一致
```

每个放行工具必须带：`evidence_id`（矩阵行号或 `PP-*`）、`game_version`、`failure_case`、
`mode_policy_check`。缺一不得放行。

---

## 录像主路径允许集（目标，当前未达成）

观察 / 等待 / 情报（除 `get_clientele`）/ 开局菜单 / 存档状态 / 店铺生命周期 /
`confirm_trade` / 对话推进 / `move_item` / 取证 / 弹窗 / `close_windows` / 记忆只读。

**明确排除**：`bargain_preview`、`get_clientele`、`save_game`、`new_game`、`act`、`press`。

---

## 回归用例清单（新增）

| ID | 场景 | 状态 |
|---|---|---|
| `PP-READ-01` | 连续 10 次 `get_state`，谈价状态位/UI/RNG 不变 | 待建（D0a） |
| `PP-READ-02` | 过期 rev 写动作被拒，零副作用 | 待建（D0a） |
| `PP-TRADE-01` | 成交 → 对话 → 送客 → 可接客 | 待建（D1） |
| `PP-TRADE-02` | 重复 `confirm_trade` 拒绝且零转移 | 待建（D1） |
| `PP-TRADE-03` | B10–B16 七个分支各一条拒绝 | 待建（D1） |
| `PP-ASYNC-01` | `end_day` pending → `wait_operation` → completed | 待建（D2） |
| `PP-ASYNC-02` | completed + 响应丢失 → 保留 completed（两轴） | 待建（D2） |
| `PP-MODE-01` | MCP 隐藏 / 直连 HTTP / `act` 三条路径都拒绝 | 待建（D0） |
| `PP-TOOLS-01` | 工具清单、矩阵、strict 名单一致 | **部分完成**（`test_tool_policy.py`） |
| `PP-RECORD-01` | 版本 + MVID + DLL 与构建一致 | 待建（D0） |

---

## 本轮之后的下一步（建议）

1. **D0a 代码修复**（Core 抽象 + 测试），因为它是 D1 的前置，且不需要真机就能验证一部分。
2. **D0 重装 + 模式闸门**，需要用户在场重启游戏。
3. D1 的 `confirm_trade` 三段式。
