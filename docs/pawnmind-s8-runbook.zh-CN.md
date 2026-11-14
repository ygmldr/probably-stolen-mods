# PawnMind S8 真机联调 Runbook（需用户共同执行）

**性质**：这是**尚未执行**的真机步骤书。离线阶段只证明了代码与假游戏行为；S8 必须由用户在场、逐项确认后手动推进。任何一步不符合预期立即停机，回到对应离线阶段修，不得“先拍完再说”。

## 0. 开始前需要你拍板的事

| 决策 | 选项 | 影响 / 我的建议 |
|---|---|---|
| **运行环境** | A. 全新隔离的 Windows 用户或独立 LocalLow；B. 你现有已游玩配置 + 新专用槽 | A 最安全，但完全空白的配置 `totalRunCount==0`，**首局特质是锁定的**，AI 无法选特质。B 特质已解锁，但你的其它存档与录制同处一个配置，尽管代码只允许绑定槽、仍有人为误操作风险。 |
| **特质怎么办** | 若选 A：a) 接受首局无特质（诚实记录）；b) 你在隔离环境里先手动跑一局解锁特质，再删除该档，然后开始录制 | b 能同时满足“隔离”和“AI 自由选特质”，但需要你在隔离环境里额外操作并可接受该环境被写过一次；所有操作都在隔离环境内，不接触你原档。 |
| **录制槽号** | 必须等于游戏**当时的下一个自动分配槽** | 空白隔离环境通常是 `0`。若填错，桥会在开始前用 `recording_slot_not_next_free` 拒绝，不会写盘。 |
| **AI 宿主** | OpenCode + OpenCode Go + DeepSeek V4.1 Flash | 需要你提供已登录的 OpenCode 环境/额度；模板键名要在 S8 现场对照实际版本核对。 |
| **录制方式** | OBS 窗口捕获 / 全屏 | 建议 1080p60 窗口捕获，保留鼠标；不要为等待模型而暂停游戏或剪辑掉思考过程。 |
| **一次原生新局写盘** | 明确授权一次 `confirm_new_game` | 它会触发游戏 `InitialSave` 真正写入专用槽；这是整个流程唯一允许的“新档创建”动作。 |

## 1. 版本与备份（先做，不启动新局）

1. Steam 切到**正式稳定分支**（不要 Beta），确认客户端已是最新；记录客户端显示的版本号与更新时间。
2. 记录 `Assembly-CSharp.dll` 的 SHA256、字节数、`VERSION_NUMBER`、MVID（`REQUIRE_GAME=1 ./test.sh` 会打印；`/health` 也会显示运行时值）。
3. 若与离线基线（`VERSION 100-Rev13`、SHA256 `725c…1d05`）不同：**停止 S8**，先按新版重做契约与回归。
4. 备份你要保留的存档（复制到游戏目录之外）；确认隔离环境与原档不在同一 `LocalLow`。

## 2. 构建与安装 Mod

```bash
cd PawnMind
REQUIRE_GAME=1 ./test.sh          # 必须 0 fail、适配器无 SKIP
./build.sh                        # 生成 dist/PawnMind/{manifest.xml,PawnMind.dll}
```

把 `dist/PawnMind` 复制到 `...\Probably Stolen\Mods\PawnMind\`。安装前确认游戏已退出（DLL 被占用时构建/覆盖会失败）。

## 3. 配置 `ModData/pawnmind/bridge.conf`

```ini
port=15528
token=<保持不变，勿外泄>
allow_test_items=false

# 只有在你确认了第 1 步的版本与程序集后才填写
expected_game_version=VERSION 100-Rev13
expected_assembly_mvid=911508a6-e0eb-4cc0-91e8-e14b16ea28d7

recording_mode=recording
recording_slot=0
recording_isolation_ack=true
```

- `recording_slot` 必须是你确认过的“下一个空闲槽”；`recording_isolation_ack=true` 表示你确认当前是隔离环境。
- 改完配置必须重启 Mod/游戏，门禁只在 `OnEnable` 计算一次。
- 不确定就先留空：所有写操作会被拒绝，读操作仍可用。

## 4. 分步验证（每步通过再做下一步）

**P0 启动与身份**
1. 启动游戏，进入主菜单，不点新游戏。
2. `curl -s http://127.0.0.1:15528/health`：确认 `game_version`、`game_assembly_mvid`、`write_gate=open`、`latest=UNVERIFIED`。
3. MCP 侧：`ping`、`get_state`（应显示 `in_game=false`）、`get_new_game_options`（`phase=closed`）。

**P1 菜单只读**
4. `open_new_game_menu`（写，带 rev）：应只打开原生菜单，不写盘。
5. `get_new_game_options`：逐项对照屏幕——场景卡、分页箭头、Continue 可用性必须与画面一致。
6. 翻页一次再翻回，确认 `page` 与画面同步。

**P2 开局选择（AI 自主）**
7. 由模型自己选场景 → 特质（可翻页、可撤销：`select` 失败时按 `focus`→`blur`→`select` 三步）→ 难度。
8. 若出现首局确认弹窗：桥只会返回 `prompt_pending`，**由 AI 显式调用一次 `confirm_new_game`** 走原生“是”，不得手动点掉。
9. `confirm_new_game` 后轮询 `get_recording_save_status`：`bound=true`、`binding.slot` 等于配置槽、`slot_matches/run_matches=true`。
10. **立刻核对画面**：难度、特质、起始资金/场景是否与选择一致；槽号若不符，停机。

**P3 首周经营**
11. 第 1–7 天正常买卖、`end_day`、`sleep`；每次写后回读 `get_state`，确认 `cash`、`inventory`、`rent.days_left` 与画面一致。
12. `sleep` 是异步黑屏：若 `day` 尚未增加，不代表失败；等 `wait_for_events` 或下一次 `get_state` 再确认。

**P4 首次付租（视频核心）**
13. 房东到访后 `get_state`：`rent.can_pay=false` 时付租选项必须 `enabled=false`；此时调用应返回 `choice_disabled` 且现金/租期/对话全不变（可现场演示一次，作为“不误报”的证据）。
14. 资金足够后选择付租项：响应应带 `last_payment`（金额、扣款前后现金、租期前后天数）；同一笔重复读取不得出现第二张回执。
15. 对照画面：扣款金额、租期归 7、已付周数 +1 必须一致。

**P5 存读档**
16. `save_recording_game` → `get_recording_save_status` 的 `binding` 不变。
17. 退回主菜单（这一步目前需要人操作），`load_recording_game` → 画面日期/现金/租期应与保存时一致；`last_payment` 被重置属预期。

**P6 收尾**
18. 停止录制，退出游戏，保留日志：Mod 日志、`/health` 输出、动作历史、状态快照。

## 5. 立即中止条件

- `/health` 的 `write_gate` 不是 `open`，或版本/MVID 与预填不符。
- `get_new_game_options` 与画面不一致（选项、分页、可用性任一项）。
- `confirm_new_game` 后 `binding.slot` 不是配置槽，或画面槽号不符。
- 任何 `outcome_unknown`、`recording_locked`、`stale_rev` 连续出现。
- 付租扣款/租期与画面不一致，或出现第二张同笔回执。
- 任何提示要读/写非专用槽，或需要手动改存档文件。

## 6. 验收清单（全绿才算 S8 通过）

- [ ] Steam 稳定版与 DLL 指纹已记录，且与预填 expected 一致
- [ ] 菜单选项、分页、可用性与画面逐项一致
- [ ] 开局选择（场景/特质/难度）与进入游戏后的实际状态一致
- [ ] 绑定槽 = 配置槽，未触碰任何其它槽
- [ ] 首周经营现金/库存/天数对账一致
- [ ] 付租回执金额、现金、租期、周数与画面一致，且只记一次
- [ ] 保存→回主菜单→读取后状态一致
- [ ] 全程无 `outcome_unknown`、无重试扣款、无 `act`/`press` 旁路

## 7. 录像注意

- 从**新游戏菜单**开始录，保留 AI 思考与工具调用过程，不做“等待模型”的暂停剪辑。
- 失败（例如第一周破产）照实记录，不补拍成成功。
- 画面中不要出现 token、配置文件、其它存档列表。
- 结束后把 `bridge.conf` 的 expected/recording 段清空或删除。

## 8. 当前已知未验证项（S8 要特别看）

1. Unity/Mono 帧内 Tick 与真实帧率（离线只证不变量）。
2. 慢客户端/调试器暂停下不卡帧；真实 MCP 的 `outcome_unknown` 纪律。
3. 真实房东对话的选项 index、禁用态、扣款与 `rentPaid` 增量。
4. `sleep` 的 0.5s 黑屏协程与自动保存时序。
5. `days_left`（`GetRentDayCounter`）与 `days_until_rent`（`dayUntilRent`）的口径差异。
6. 首局确认弹窗的本地化文本与真实行为。
7. OpenCode 配置键名、模型路由与额度限制（离线未安装）。
8. `list_contacts(include_hidden=true)` 在生产配置下必须被拒。
