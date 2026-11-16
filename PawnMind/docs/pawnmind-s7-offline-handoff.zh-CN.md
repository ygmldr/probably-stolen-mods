# PawnMind S7 离线交接包（S8 前最后一站）

**状态**：S0–S7 离线工作完成，**未启动游戏、未安装 Mod、未读写任何真实存档、未改 Steam**。下一站是 S8，必须由用户在场共同执行；本文是交接清单，不是“已可发布”的声明。

## 1. 最终验证（主代理独立复跑，2026-11-14）

| 项 | 结果 |
|---|---|
| `REQUIRE_GAME=1 ./test.sh` | C# **462** pass / 0 fail；脚本门禁 **53**；适配器契约 **392**，`adapter contract OK`，`strict mode: REQUIRE_GAME=1`，**0 SKIP** |
| `agent/tests` | **81** pass / 0 fail / 0 skip |
| `dotnet build src/PawnMind.csproj -c Release` | 0 警告 / 0 错误 |
| 已安装程序集 | `VERSION 100-Rev13`，SHA256 `725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05`，MVID `911508a6-e0eb-4cc0-91e8-e14b16ea28d7`；**是否拍摄时 Steam 最新仍为 UNVERIFIED** |
| `git diff --check` | 干净 |

`./build.sh` 的打包段**未执行**：manifest 仍是 0.2.1，需在 S8 版本核对后决定是否升版；此时打包会生成“新 DLL + 旧 manifest”的不一致产物。Release DLL 已单独编译通过。

## 2. 已完成的离线能力

| 阶段 | 内容 | 证据 |
|---|---|---|
| S0 | 版本/开局/存档静态核查，标出待真机项 | [S0](<pawnmind-s0-offline-evidence.zh-CN.md>) |
| S1 | 禁重定向、Bearer 不跨端、rev/action_id 必填、指纹绑定、失败不记成功 | [S1](<pawnmind-s1-offline-evidence.zh-CN.md>) |
| S2 | 可指定程序集、严格模式禁 SKIP、版本/MVID 拒写门禁、`/health` 身份 | [S2A](<pawnmind-s2a-offline-evidence.zh-CN.md>) / [S2B](<pawnmind-s2b-offline-evidence.zh-CN.md>) |
| S3 | 新局菜单只读/单步选择/分页/继续/hover 消歧，7 个语义工具 | [S3A](<pawnmind-s3a-offline-evidence.zh-CN.md>) / [S3B](<pawnmind-s3b-offline-evidence.zh-CN.md>) |
| S4 | 专用录制槽状态机、所有写入口统一拦截、绑定回读 | [S4](<pawnmind-s4-offline-evidence.zh-CN.md>) |
| S5 | 禁用对话选项拒绝、付租回执、跨日回读、隐藏字段清理 | [S5](<pawnmind-s5-offline-evidence.zh-CN.md>) |
| S6 | HTTP 非阻塞/有界、断线 unknown、隐藏字段审计；AI 宿主模板 + fake 游戏端到端 | [S6A](<pawnmind-s6a-offline-evidence.zh-CN.md>) / [S6B](<pawnmind-s6b-offline-evidence.zh-CN.md>) |
| S7 | 录制模式工具面（94 个玩家等价工具，排除 6 个旁路）、文档同步、S8 Runbook | 本文 + [Runbook](<pawnmind-s8-runbook.zh-CN.md>) |
| S7+ | DSH 宿主补丁（`mcp-pawnmind` + 默认模型 `opencode-go/deepseek-v4.1-flash`）、试玩剧本 `PawnMind/play/AGENTS.md`、只读预检脚本 `agent/preflight.py`、`next_free_slot` 只读字段 | [宿主说明](<pawnmind-ai-host-setup.zh-CN.md>) |

## 3. 关键行为（给 S8 的预期）

- **默认全锁**：`recording_mode` 为空时，`new_game`/`save_game`/`sleep`/`press`/`act` 旁路与菜单写全部拒绝；只有读接口可用。
- **解锁条件**：`recording_mode=recording|on` + `recording_slot=<下一个自动分配槽>` + 版本门禁 `open` + 环境确认（`recording_isolation_ack=true` 用于隔离环境，**或** `recording_dedicated_slot_ack=true` 表示接受“现有配置 + 专用槽”的同配置风险）。
- **开局唯一路径**：菜单选择 → 难度页 → `confirm_new_game`（一次原生 Start，游戏自己 `InitialSave` 分槽）→ 回读槽号/runID/开局意图，全中才 `Bound`；任何不符 `Locked`，不回滚不重试。
- **绑定后**：只有该槽 `save/load` 与 `sleep` 自动保存可使用；其他槽 `recording_slot_not_bound`。
- **付租**：禁用选项 `choice_disabled` 且零变化；成功后有一次性 `last_payment` 回执。
- **失败语义**：`stale_rev`、`outcome_unknown`、`recording_locked` 都不允许换 `action_id` 重试扣款/存档。

## 4. 尚未验证（S8 必查，按 Runbook 执行）

1. Steam 正式稳定版是否仍为已安装的 Rev13；若不是，全部契约需按新 DLL 重做。
2. Unity/Mono 真实帧内 Tick、慢客户端、Windows 内核短写。
3. 真实菜单的选项/分页/可用性、首局弹窗本地化文本。
4. 原生新局真实写盘、槽号自动分配与预测一致。
5. 房东对话 index/禁用态、扣款与 `rentPaid` 增量、`dayUntilRent` 与 `GetRentDayCounter` 口径。
6. `sleep` 0.5s 黑屏协程与自动保存时序。
7. DSH 真正以 web profile 启动后 `mcp-pawnmind` 是否成功 spawn、`mcp__pawnmind__*` 是否出现在模型工具列表（headless + fake 桥已冒烟通过：模型返回 `PONG`，实际调用 `mcp__pawnmind__ping` 得到 `{"ok":true,"pong":true,"rev":0}`）；`opencode-go/deepseek-v4.1-flash` 认证与额度（本机为 `OPENCODE_GO_API_KEY`，冒烟通过）；宿主侧工具（bash/fs/web 等）是否如 profile 所配置全部禁用。
8. `list_contacts(include_hidden=true)` 在生产配置下被拒。

## 5. 待用户决定（阻塞 S8）

1. **环境**：全新隔离环境（特质锁定）vs 现有已游玩配置 + 新专用槽。
2. **特质**：若隔离，接受首局无特质，还是先在隔离环境手动跑一局解锁后删档。
3. **录制槽号**：确认游戏当时的“下一个自动分配槽”（空白隔离通常 0）。
4. **AI 宿主**：OpenCode + Go 订阅 + DeepSeek V4.1 Flash 是否就绪、额度是否足够。

## 6. 工作树与提交建议

- 本轮所有改动**未提交**。工作树包含：C# 生产代码与测试、`agent/` 新工具与 fake/demo、大量 `pawnmind-*.md` 证据、`AGENTS.md`/`README.md` 计数更新。
- 建议在 S8 前先做一次本地提交（不推送），提交信息示例：
  `feat(pawnmind): offline S0-S7 — new-game semantics, recording slot gate, rent receipts, transport hardening`
- 提交前确认：`git diff --check`、`REQUIRE_GAME=1 ./test.sh`、Python 全量三项全绿；不要把 `_decomp/`、`dist/`、`bin/`、`obj/`、`.venv/` 加入提交。
- `manifest.xml` 版本与 `README` 的“真机已验证”表述留到 S8 结束后再定，避免把离线结果说成真机结果。

## 7. 建议的 S8 顺序

按 [Runbook](<pawnmind-s8-runbook.zh-CN.md>)：版本核对 → 构建安装 → 配置 → `/health` → 菜单只读 → 开局 → 首周 → 付租 → 存读档 → 收尾。任一项不符立即停机。
