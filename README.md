# game_mods · Probably Stolen Mods

《深空当铺：可能是偷的》(Probably Stolen 0.46D) 的两个本地 Mod：

| 目录 | Mod | 作用 | 状态 |
|---|---|---|---|
| `CompactSort/` | 紧凑整理 | 背包/货架二维紧凑重排，减少碎缝 | 已发布 |
| `PawnMind/` | PawnMind · AI Bridge | 把游戏状态与经营动作暴露为本地 HTTP + MCP，供 AI/脚本操作 | 接口可用，agent 策略仍在早期 |

> 游戏版本：正式版 0.46D（主菜单 VERSION 100，原生 ModLoader）。
> 反编译/逆向产物统一放在根目录 `_decomp/`，不提交、不分发。

## 当前状态（2026-11-14）

- PawnMind tag：`v1.0.1`（最新提交 `3eb3154`；本工作树有大量未提交的离线改造）
- MCP 工具：**100 个**（`--tools recording` 只注册首周录制用 28 个）
- 测试：C# **462**（含脚本门禁 53）+ 适配器契约 **392** + Python **81**，均 0 skip
- 构建产物：`PawnMind/dist/PawnMind/{manifest.xml, PawnMind.dll}`
- 已离线完成：新局菜单语义、专用录制槽授权、首周付租回执、传输加固与隐藏字段清理
- **尚未真机验证**：以上全部只经过假 UI/假桥与已安装程序集契约；真机步骤见 [`PawnMind/docs/pawnmind-s8-runbook.zh-CN.md`](PawnMind/docs/pawnmind-s8-runbook.zh-CN.md)
- **还没有**接入真实 LLM 宿主；`agent/orchestrator.py` 只有 `RulePolicy`，`agent/demo_agent.py` 是脚本化演示

## PawnMind 架构

```
Game (Unity, main thread)
   │  反射/强类型适配
   ▼
PawnMind.dll  ── HTTP 127.0.0.1:15528 ──  Python MCP stdio
   │  Bearer token / rev / action_id        │
   │  MainThreadPump                         ▼
   └──────────────────────────────────  AI / DSH / 脚本
```

- `src/Core/`：纯逻辑，无 Unity 依赖
- `src/Bridge/`：HTTP、鉴权、revision、状态快照、动作路由
- `src/Game/`：唯一允许直连/反射游戏类型的层，所有访问在主线程
- `agent/mcp_server.py`：stdio MCP 服务，100 个工具（`--tools recording` 只注册录制用 28 个）
- `agent/pawnmind_client.py`：HTTP 客户端（长轮询 `/events`）
- `agent/orchestrator.py`：最小 observe→decide→act 闭环 + `RulePolicy` 基线

### 安全不变量

- 只监听 `127.0.0.1`；除 `/health` 外都要 Bearer token
- 动作带 `rev`，过期一律拒绝；`action_id` 幂等
- 不读隐藏字段（鉴定答案、隐藏身份等）
- 不改钱、不生成物品、不执行任意代码
- 游戏对象只允许 Unity 主线程访问

## 能做什么（按系统）

| 层 | 已接 |
|---|---|
| 观察 | `/state`：日期、现金、店态、顾客、意图/预算/是否通缉、对话正文+选项（含 `enabled`）、议价物、背包/布局、合法动作、`rent` 与租金回执 |
| 经营 | 开店、叫客、对话/选项（禁用项拒绝且不误报）、买卖、砍价、关店/睡觉、存读档 |
| 新局 | 只读菜单选项、单步选择/翻页/继续、hover 消歧、首局弹窗只报告；`confirm_new_game` 经专用槽授权后触发一次原生开局 |
| 录制保护 | `recording_mode`/`recording_slot`/`recording_isolation_ack` 全锁；绑定槽后只允许该槽 save/load/sleep，`act`/`press`/`new_game`/`load_game` 一律拦截 |
| 物品 | 背包分页、精确形状/旋转放置、二维布局、自动重排、阅读/书籍 |
| 夜间 | 垃圾场、配给站、杰克逊博士、搜刮、补给站代币、批发 |
| 机器 | 枚举/状态/槽位/放入/收取、熔炉、水培、酿酒发酵、催陈器入酒 |
| 通用交互 | activate/toggle/target、水质/植物/化扫/基因分析仪、标签、鱼类标记笔 |
| 电话/网络 | 联系人/拨号/选项/挂断、供应商订货、网络升级、彩票只读+兑奖 |
| 社交/风险 | 派系声望、健康、需求、通缉、Barter、举报任意顾客、保险/夜间服务、安检/违禁品 |
| 世界 | 远征快照/派遣、养殖快照、招牌、事件 |

## 快速开始

### 构建 PawnMind

```bash
cd PawnMind
./test.sh          # 纯逻辑 + 适配器契约
./build.sh         # 先测试，再生成 dist
```

安装：退出游戏，把 `dist/PawnMind/` 整个复制到：

```
...\steamapps\common\Probably Stolen\Mods\PawnMind\
```

确保第一层是 `manifest.xml` + `PawnMind.dll`。

### 启动 MCP

```bash
cd PawnMind
python -m venv .venv
.venv/Scripts/python.exe -m pip install mcp
.venv/Scripts/python.exe agent/mcp_server.py --config "<...>/ModData/pawnmind/bridge.conf"
```

配置由 Mod 首次启动生成：

```
%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\pawnmind\bridge.conf
```

不要把 `token` 提交、截图或发到公开仓库。

### 跑基线循环

```bash
.venv/Scripts/python.exe agent/orchestrator.py --config "<...>" --dry-run
.venv/Scripts/python.exe agent/orchestrator.py --config "<...>" --steps 8
```

## 已知限制（审查重点）

1. **没有视觉鉴定**：不读鉴定答案，MCP 也不返回截图；AI 只能交易已知货。
2. **没有 LLM 策略**：orchestrator 仍是规则脚本，不能自主经营。
3. **远征是 debug-only/未上线内容**：正常 Perk 不启用；`debug_unlock_expedition` 仅测试。
4. **突袭 RaidScene 正常流程不可达**：属于独立飞船/站点战斗脚手架，未接。
5. **需求/通缉/Barter 成功路径未验**：测试档没刷到样本，只验了错误路径。
6. **WildNetworkUI 未接**：用 `list_upgrades`/`buy_upgrade` 替代。
7. **陈酿 1 天品质未变**：只验证了入槽和槽内查找。
8. **标签条件 ID 是本地化字符串**（如“纯水”），跨语言/版本脆弱。
9. **彩票兑奖成功路径未验**：测试档未解锁/无中奖号。
10. **版本号不一致**：`manifest.xml` 仍为 0.2.1，git tag 为 v1.0.1。
11. **测试开关**：本地 `bridge.conf` 可能有 `allow_test_items=true`，发布包不要带。

## 文档

- [PawnMind README](PawnMind/README.md)
- [能力矩阵](PawnMind/docs/pawnmind-capability-matrix.zh-CN.md)
- [已知限制](PawnMind/docs/pawnmind-known-limitations.zh-CN.md)
- [迭代问题记录](PawnMind/docs/pawnmind-iteration-issues.md)
- [系统扩展计划](PawnMind/docs/pawnmind-systems-expansion-plan.zh-CN.md)
- [测试档说明](PawnMind/docs/pawnmind-test-save.md)

## 给审查模型的建议

1. 先读本文件 + `AGENTS.md`，再看 `PawnMind/README.md` 顶部状态。
2. 对照 `PawnMind/docs/pawnmind-capability-matrix.zh-CN.md` 核验“真机/代码/未接”是否与代码一致。
3. 重点看：
   - `src/Bridge/` 的 rev/token/主线程/回滚
   - `src/Game/` 是否读了隐藏字段
   - `agent/mcp_server.py` 的工具参数与错误码
   - 测试是否覆盖失败路径，而不只是 happy path
4. 运行 `cd PawnMind && ./test.sh`；Python 测试用 `.venv/bin/python -m unittest discover -s agent/tests`。
5. 不要运行 `new_game`，不要动 `save_11*`、`save_0/1/3/4/7`、`save_13` 等用户存档；只允许槽 17。
