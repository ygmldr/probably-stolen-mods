# Probably Stolen Mods

[![python-tests](https://github.com/ygmldr/probably-stolen-mods/actions/workflows/python-tests.yml/badge.svg)](https://github.com/ygmldr/probably-stolen-mods/actions/workflows/python-tests.yml) [![secret-scan](https://github.com/ygmldr/probably-stolen-mods/actions/workflows/secret-scan.yml/badge.svg)](https://github.com/ygmldr/probably-stolen-mods/actions/workflows/secret-scan.yml)


《深空当铺：可能是偷的》(*Probably Stolen* 0.46D) 的非官方 Mod 集合。
仓库 `ygmldr/probably-stolen-mods`。

> **免责声明**：本项目与 Questing Goose Studio 无关，不是官方内容。使用 Mod 可能影响存档，
> 请自行备份。所有能力都只在本机生效：不联网、不上传、不改游戏资产。
> 游戏内 Mod 只通过原生 ModLoader 加载，不注入进程、不修改游戏文件。

| Mod | 作用 | 状态 |
|---|---|---|
| [`CompactSort/`](CompactSort/) | 背包 / 货架二维紧凑重排，减少碎缝 | 可用 |
| [`PawnMind/`](PawnMind/) | 把游戏状态与经营动作暴露为本地 HTTP + MCP，供 AI/脚本操作 | 接口可用，agent 策略仍在早期 |

---

## 快速开始

### 环境要求

| 项 | 版本 |
|---|---|
| 游戏 | *Probably Stolen* 0.46D（主菜单 `VERSION 100`，原生 ModLoader） |
| 构建 | .NET SDK **9.0**（两个 Mod 的工程都是 `net9.0` / `netstandard2.1`） |
| Python | 3.12（只有 PawnMind 的 MCP 端需要） |
| 系统 | Windows；开发脚本按 Windows + WSL 两种路径都处理 |

测试要引用游戏自带的 `Assembly-CSharp.dll` 与 `Newtonsoft.Json.dll`，用环境变量指向游戏目录：

```bash
export GAME_MANAGED="/path/to/Probably Stolen/Probably Stolen_Data/Managed"
export PAWNMIND_NEWTONSOFT_DLL="$GAME_MANAGED/Newtonsoft.Json.dll"
```

### CompactSort

```bash
cd CompactSort
./test.sh      # 纯算法测试 + 自检
./build.sh     # 测试通过后打包
```

把 `dist/CompactSort/` 复制到 `...\Probably Stolen\Mods\CompactSort\`。

### PawnMind

```bash
cd PawnMind
./test.sh      # C# 纯逻辑 + 脚本门禁 + 适配器契约
./build.sh     # 先测试，再生成 dist
```

安装：退出游戏，把 `dist/PawnMind/` 整个复制到 `...\Probably Stolen\Mods\PawnMind\`，
第一层必须是 `manifest.xml` + `PawnMind.dll`。

首次启动会在下面生成配置（含本地 token，**不要提交、不要截图、不要发到公开渠道**）：

```
%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\pawnmind\bridge.conf
```

启动 MCP：

```bash
cd PawnMind
python -m venv .venv
.venv/Scripts/python.exe -m pip install mcp
.venv/Scripts/python.exe agent/mcp_server.py --config "<...>/ModData/pawnmind/bridge.conf"
```

`bridge.conf` 里的 `run_mode` 决定权限，默认（缺失）是 **locked**，除 `/health` 外全部拒绝：

```ini
run_mode=bootstrap     # 主菜单阶段
run_mode=play          # 进入游戏后；只放行有实测证据的工具
allow_test_mode=false  # test 需要显式改成 true，且只用于测试档
```

---

## PawnMind 架构

```
Game (Unity, 主线程)
   │  强类型适配 / 反射
   ▼
PawnMind.dll  ── HTTP 127.0.0.1:15528 ──  Python MCP (stdio)
   │  Bearer token / rev / action_id        │
   │  MainThreadPump                         ▼
   └──────────────────────────────────  AI / DSH / 脚本
```

| 层 | 职责 |
|---|---|
| `src/Core/` | 纯逻辑，**不允许** `UnityEngine` 或游戏类型 |
| `src/Bridge/` | HTTP、鉴权、revision、状态快照、动作路由、operation 状态 |
| `src/Game/` | 唯一允许直连/反射游戏类型的层，所有访问都在主线程 |
| `agent/mcp_server.py` | stdio MCP 服务，117 个工具（`--tools recording` 只注册 107 个） |
| `agent/pawnmind_client.py` | HTTP 客户端（长轮询 `/events`） |
| `agent/orchestrator.py` | 最小 observe→decide→act 闭环 + `RulePolicy` 基线 |

### 安全不变量

- 只监听 `127.0.0.1`；除 `/health` 外都要 Bearer token
- 写动作必须带 `rev` + `action_id`；`rev` 过期一律拒绝
- 运行模式 fail-closed：缺配置 = 全锁；`test` 需要二次开关
- 不读隐藏字段（鉴定答案、隐藏身份、真实水质等）
- 不改钱、不生成物品、不执行任意代码
- 游戏对象只允许 Unity 主线程访问
- 不读写用户存档；录制只允许用户显式绑定的专用槽

## 能做什么（按系统）

| 层 | 已接 |
|---|---|
| 观察 | `/state`：日期、现金、店态、顾客、意图/预算/是否通缉、对话正文+选项、议价物、背包/布局、`legal_actions` + `candidate_actions`、`rent` 与回执 |
| 经营 | 开店、叫客、对话/选项、买卖、砍价、关店/睡觉、存读档 |
| 新局 | 只读菜单选项、单步选择/翻页/继续、`confirm_new_game` 经专用槽授权后触发一次原生开局 |
| 交易 | `confirm_trade` 前置守卫（B10–B16）、结果证据分层（`business_complete` / `items_transferred` / `cash_*`） |
| 操作状态 | `execution_status` / `delivery_status` 两轴 + `get_operation` / `wait_operation` / `GET /operation` |
| 录制保护 | `recording_mode` / `recording_slot` 全锁；绑定后只允许该槽 save/load/sleep |
| 物品 | 背包分页、精确形状/旋转放置、二维布局、自动重排、阅读/书籍 |
| 夜间 | 垃圾场、配给站、杰克逊博士、搜刮、补给站代币、批发 |
| 机器 | 枚举/状态/槽位/放入/收取、熔炉、水培、酿酒发酵、催陈器入酒 |
| 通用交互 | activate/toggle/target、水质/植物/化扫/基因分析仪、标签、鱼类标记笔 |
| 电话/网络 | 联系人/拨号/选项/挂断、供应商订货、网络升级、彩票只读+兑奖 |
| 社交/风险 | 派系声望、健康、需求、通缉、Barter、举报任意顾客、保险/夜间服务、安检/违禁品 |
| 世界 | 远征快照/派遣、养殖快照、招牌、事件 |

## 当前状态（2026-11-17）

- MCP 工具：**117 个**（`--tools recording` → 107；R5-B1 新增 `stage_negotiation_item`）
- 测试：C# **750** + 脚本门禁 **53** + 适配器契约 **503** + Python **365**，全绿
- 策略指纹：`policy_schema=2`，`sha256=e0da5a24…`；`play_allowlist` 仍为 **0**
- **尚未真机验证**：离线 PASS 都是静态 / fake / loopback 证据；实机清单见
  [`NEXT_LIVE_SESSION.md`](PawnMind/docs/plans/NEXT_LIVE_SESSION.md)
- `dist/` 未随源码重建，与已安装 DLL 不一致；发布前需要统一版本号与产物

## 已知限制

1. **没有视觉鉴定**：不读鉴定答案，也不返回截图。
2. **没有 LLM 策略**：`orchestrator.py` 仍是规则脚本。
3. **远征是 debug-only**：正常流程不启用。
4. **突袭 RaidScene 正常流程不可达**：未接。
5. **需求/通缉/Barter 成功路径未验**：只覆盖了错误路径。
6. **WildNetworkUI 未接**：用 `list_upgrades` / `buy_upgrade` 替代。
7. **陈酿 1 天品质未变**：只验证了入槽。
8. **标签条件 ID 是本地化字符串**，跨语言/版本脆弱。
9. **彩票兑奖成功路径未验**。
10. **版本号不一致**：`manifest.xml` 0.3.0、git tag v1.0.1。

## 文档

- [PawnMind README](PawnMind/README.md)
- [能力矩阵](PawnMind/docs/pawnmind-capability-matrix.zh-CN.md)
- [已知限制](PawnMind/docs/pawnmind-known-limitations.zh-CN.md)
- [迭代问题](PawnMind/docs/pawnmind-iteration-issues.md)
- [AI 宿主配置](PawnMind/docs/pawnmind-ai-host-setup.zh-CN.md)
- [首周 Skill](PawnMind/skills/pawnmind-first-week/SKILL.md)
- [离线批次报告](PawnMind/docs/reports/NIGHTLY_BATCH_A_REPORT.md)

## 贡献

- 任何 `src/` 变更都要求 TDD：先写失败测试，再实现，提交前跑完整套件。
- 不要提交 `_decomp/`、`dist/`、`.venv/`、`bridge.conf`、存档或任何凭据。
- 涉及游戏写操作的新接口，必须说明原生玩家入口、守卫谓词与回读证据。

## License

MIT，见 [LICENSE](LICENSE)。游戏本身、游戏资产与第三方 Mod 的版权归各自权利人；
本仓库只包含独立编写的 Mod 代码与分析结论，不包含游戏程序集或反编译产物。
