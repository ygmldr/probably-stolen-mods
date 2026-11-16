# PawnMind 竞品调研（截至 2026-11-17）

> 范围：Steam 创意工坊 + GitHub 上围绕《深空当铺：可能是偷的》(Probably Stolen, App 4348910) 的
> AI / MCP / Agent 项目。
> 口径：只统计公开可见信息；订阅、浏览、收藏为 2026-11-17 02:00（CST）快照；作者自述的能力未逐条复现。
> 结论先行：**竞品数量是真的，竞争强度是假的。** 真正的战场不是工具数量，而是"5 分钟内连上 + 连续玩 3 天不出事"。

---

## 0. 一句话结论

游戏上线 20 天，至少冒出 **15 个** AI/MCP 项目；但工坊 9 个条目**订阅合计只有 279**，最高的 107 还是开发者工具。
品类处于"定义期"，没有头部，窗口还有 2–4 周。

PawnMind 的底子（114 工具、rev/action_id/回滚、契约测试、记忆）是全场最厚的，
但**没上架、没真机证据、没有 Skill、要装 Python**——在货架上等于不存在。

---

## 1. 市场盘子：竞品很小，玩家很大

| 指标 | 数值 | 来源 |
|---|---|---|
| 上线日期 | 2026-10-28 | [3DM / 网易转载](https://m.163.com/dy/article/LB74FHOU0526D8LR.html) |
| 同时在线峰值 | **174,382**（11-04，SteamDB） | 同上 |
| 销量 | 官方宣布 **20 万**（后续报道称 50 万） | [Steam 新闻](https://store.steampowered.com/news/app/4348910/view/676261964507251285) |
| 价格 / 评价 | $17.99 / 好评如潮 | 同上 |
| 工坊 AI/MCP 条目 | **9 个**，订阅合计 **279**、浏览 **2,703**、收藏 **62** | 工坊搜索 "MCP" 快照 |
| 渗透率 | 279 / 200,000 ≈ **0.14%** | 推算 |

三个直接推论：

1. **这不是红海，是真空。** 一个 20 万销量的游戏，AI 接口品类合计不到 300 订阅。
2. **品类认知已被验证。** 13 天内从 0 到 15 个项目，说明需求真实存在，不需要再教育市场。
3. **先发优势正在形成。** 工坊的订阅/收藏/更新频率会滚雪球；现在缺位，3 个月后要花 10 倍成本追。

---

## 2. 竞品全景

### 2.1 工坊已上架（9 个）

| # | 条目 | 作者 | 首发 | 订阅 / 浏览 | 定位 | 接入方式 | 工具面 | 认证 / 安全 | 可借鉴 |
|---|---|---|---|---|---|---|---|---|---|
| 1 | [深空当铺：小肥鱼当家](https://steamcommunity.com/sharedfiles/filedetails/?id=3833622336) | bakapeng | 11-04 | **81 / 1000** | 玩家向 + **内置自动游玩** | 游戏内 MCP + `token.txt`，可选 OpenAI 兼容 API | 未公开 | 明文 token 文件；无 rev/幂等 | 零客户端也能玩；右上角实时思考 |
| 2 | [Build MODs with AI](https://steamcommunity.com/sharedfiles/filedetails/?id=3835953432) | Mr_Ha | 11-08 | **107 / 890** | 开发者向（写 mod） | 工坊 + 自带 MCP 配置 | 未公开 | **无认证**；Full 版热重载 | 品类里订阅最高；"AI 写完自己验"叙事 |
| 3 | [深空当铺 MCP Bridge（实验版）](https://steamcommunity.com/sharedfiles/filedetails/?id=3833038046) | 一灯灯灯灯大师 | 11-03 | 29 / 146 | 玩家向，能力边界诚实 | Mod + **外部 Python 3.11** MCP | **32** | 连接配置 + token | 首个上架；能力矩阵/未验证项写得比我们细 |
| 4 | [MCP Bridge（开发桥）](https://steamcommunity.com/sharedfiles/filedetails/?id=3834799226) | DsW-4 | 11-07 | 25 / 144 | 开发者向 | 游戏内 HTTP MCP `:28910` | **24** | ModScanner 0 硬旗；反射可读写 | 资产热重载、机器快进、日志 ring buffer、`AGENT_GUIDE.md` |
| 5 | [Probably Stolen MCP / AI 游玩](https://steamcommunity.com/sharedfiles/filedetails/?id=3837662737) | Jerod | 11-11 | 14 / 95 | 玩家向 + **Skill** | 游戏内 `:28739` + Node stdio 桥 + `connection.json` | 未公开 | token 每次启动轮换 | Skill + 经营方法论；"6 小时买下房子"的可传播故事 |
| 6 | [AI Data Interface V0.3](https://steamcommunity.com/sharedfiles/filedetails/?id=3835788758) | — | 11-08 | 13 / 281 | 叙事 / GM / 沙盒 | 游戏 DLL（无网络）+ 外部 bridge | **39 读** + 写接口 | 源码 + SHA256；异步快照 | RimWorld 式 storyteller、自定义访客、±credits 做实验 |
| 7 | [PSAI](https://steamcommunity.com/sharedfiles/filedetails/?id=3838235633) | Iksia | 11-12 | 6 / 31 | 玩家向（agent 观察） | 原生 mod + CLI/MCP，Node 24 | 未公开 | 未声明额外权限 | 官方 JSON 数据接口；按"当前可做动作"分组，避免误判事件 |
| 8 | [MCP 测试桥](https://steamcommunity.com/sharedfiles/filedetails/?id=3839603565) | 双向若叶睦 | 11-14 | 3 / 45 | 开发者向（自动测 mod） | 订阅 + Python 标准库 | 未公开 | **无认证**；虚拟存档沙盒 | **离屏 960×540 启动 + 存档沙盒**；按屏幕文字点击 |
| 9 | [星际掌柜](https://steamcommunity.com/sharedfiles/filedetails/?id=3840057532) | Arom-corgi | 11-15 | 1 / 71 | 玩家向，**零依赖** | 游戏内 Streamable HTTP `:20873`（官方 MCP SDK） | 未公开，有 `game_capabilities` | SHA256 + ILSpy 审计指引；可选 stdio + SQLite 记忆 | **无需 Python/脚本**；能力发现工具；隐私说明单独成文 |

### 2.2 未上架 / 相邻项目（6 个）

| 项目 | 形态 | 关键点 | 威胁度 |
|---|---|---|---|
| [mcxufly/ProbablyStolenMcp](https://github.com/mcxufly/ProbablyStolenMcp) | Mod + Python 桥 → 本机 TCP，**13 工具** | 0.4.2；`verification/LIVE-*.md` 逐条真机记录；"自动测试 ≠ 实测" | **高**：验证纪律最像我们，补上分发就会咬人 |
| [detyse/probably-stolen-mcp](https://github.com/detyse/probably-stolen-mcp) | Mod + `uv` 自动装 Python | 0.2.7，前身叫 **PawnPilot**；英文优先 + 用户指南 | 中：把"装 Python"这一步自动化了 |
| [OPFIMISS/ProbablySage](https://github.com/OPFIMISS/ProbablySage) | 源码/官方文档查询 MCP | 给 modder 查真实类型与方法；附 modding Skill | 低（相邻）：抢的是开发者入口 |
| [Commoner-ai/EZapi](https://github.com/Commoner-ai/EZapi) | Mod 前置框架 + AI 可读索引 | MIT，0.1.87，39 组 API | 低（可结盟）：潜在分发渠道 |
| [h752603-source/Probably-Stolen-AIprobe](https://github.com/h752603-source/Probably-Stolen-AIprobe) | **非 MCP**，确定性自动店长 | 不联网、不调 LLM、只点原生按钮、JSONL 流水 | 中：吃掉"不想配 AI"的那部分人 |
| [Chobits01/Probably-Stolen-DeepSeek](https://github.com/Chobits01/Probably-Stolen-DeepSeek) | 视觉 + 鼠标（SendInput）+ 虚拟形象 | DeepSeek 看画面玩，B 站录播 | 低（路线不同）：证明"纯文本接口"不是唯一解 |

### 2.3 时间线：13 天从 0 到 15 个项目

```
10-28  游戏上线
11-03  一灯 MCP Bridge（首个工坊 MCP）
11-04  小肥鱼当家（内置自动游玩）
11-07  DsW-4 开发桥
11-08  Build MODs with AI、AI Data Interface
11-11  Jerod（Skill）、detyse（uv 安装）
11-12  PSAI
11-13  mcxufly（LIVE 验证文档）
11-14  测试桥（离屏 + 存档沙盒）、Chobits（视觉路线）
11-15  星际掌柜（零依赖 + 能力发现）
11-16  AiProbe（确定性自动店长）
```

**读法**：这不是"好多竞品"，这是"一个新品类的抢滩"。所有人都在 2 周内做到 MVP，**没有人做到"可信的长期无人值守"**。

---

## 3. 四条赛道，四种赢法

### A. 零依赖玩家向 —— 赢在"第一次成功连接"
代表：星际掌柜（游戏内 MCP，订阅即用）、小肥鱼（连客户端都不用，填 API Key 就自动玩）。
**打法**：把 Python、venv、配置文件、端口全部藏起来。
**弱点**：小肥鱼的 Key 明文、无 rev；星际掌柜太新（1 订阅），还没被验证。

### B. Skill 与策略 —— 赢在"AI 知道该怎么玩"
代表：Jerod（配套 Skill + 实战经营方法 + 6 小时通关故事）、星际掌柜（可选 stdio 提供 SQLite 跨会话记事）。
**打法**：接口只是管道，方法论才是产品。
**弱点**：Skill 质量无法量化，容易变成"提示词玄学"。

### C. 开发者向 —— 赢在"改一行就能看到结果"
代表：DsW-4（热重载、机器快进、任意反射）、测试桥（离屏启动、虚拟存档、按屏幕文字点击）、Build MODs（订阅最高）。
**打法**：把 modder 的"改-测"循环压到秒级。
**弱点**：无认证 + 任意反射，一旦出事会连累整个品类。

### D. 非 MCP 替代路线 —— 赢在"不用配 AI"
代表：AiProbe（确定性规则，不联网、不烧 token）、Chobits（视觉 + 鼠标）。
**打法**：服务"不想折腾"和"想看 AI 出洋相"的两拨人。
**弱点**：能力上限低；AiProbe 明确说"不保证通关"。

> **对我们的启示**：A 和 B 是玩家市场，C 是开发者市场，D 是内容市场。
> PawnMind 现在四条都不占：比 A 难装，比 B 缺 Skill，比 C 保守（这是对的），比 D 贵（要模型）。

---

## 4. 与 PawnMind 的对比

| 维度 | PawnMind | 星际掌柜 | 小肥鱼 | Jerod | 一灯 | mcxufly | DsW-4 |
|---|---|---|---|---|---|---|---|
| 工坊分发 | ❌ 未见条目 | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| 接入依赖 | Python venv + `mcp` + `bridge.conf` | **无** | **无** | Node 18+ | Python 3.11+ | Python 桥 | 无 |
| MCP 形态 | stdio（Python）+ 本机 HTTP | 游戏内 Streamable HTTP | 游戏内 HTTP | 游戏内 HTTP + stdio | 外部 stdio | stdio | 游戏内 HTTP |
| 工具数 | **114**（文档另有 89/100 两个数） | 未公开 + 能力发现 | 未公开 | 未公开 | 32 | 13 | 24 |
| 认证 | **Bearer + rev + action_id** | 本机 + token | 明文 token | 启动轮换 token | token | 未公开 | 未公开 |
| 幂等 / 回滚 | ✅ 动作幂等 + 放置回滚 | 未公开 | ❌ | 未公开 | 部分 | 未公开 | 未公开 |
| 跨会话记忆 | ✅ memory_* 工具 | ✅ SQLite（可选） | ❌ | Skill 文档 | ❌ | ❌ | ❌ |
| 截图 / UI 快照 | ✅ `capture_screenshot` / `get_ui_snapshot` / `press` | 有 UI 操作 | 有 | 有 | 部分 | 有射线采样 | 有 |
| Skill / 策略包 | ❌ | 部分 | 内置 prompt | ✅ | ❌ | ❌ | `AGENT_GUIDE.md` |
| 真机证据 | ⚠️ 最新改动"尚未真机验证"（README 自述） | 少量 | 自述 | "6 小时通关"自述 | 详细 | ✅ LIVE 文档 | 自述 |
| 源码 / 可审计 | ✅（本地仓库） | ✅ + SHA256 | ❌ | ❌ | 部分 | ✅ | ✅ |

### 解读

- **工具数量我们赢了 3–9 倍，但这恰恰是负担。** 竞品刻意做小（13 / 24 / 32 / 39），因为工具越多，模型选错工具的概率越高、上下文越贵。我们有 114 个，只有 `full` / `recording` 两档，没有面向玩家的"核心 40 个"。
- **安全契约我们独一档。** rev + action_id + 主线程 + 回滚 + 录制槽保护，竞品要么没有，要么只做到 token。但这件事目前只写在 `AGENTS.md` 里，玩家看不见。
- **接入成本我们垫底。** 竞品已经把 Python 藏进游戏进程（星际掌柜）或自动装（detyse 的 uv），我们还在让用户 `python -m venv` + 手填 `bridge.conf` 路径。
- **证据链我们最弱。** 竞品有 LIVE 文档、有视频、有"6 小时通关"；我们的 README 写着"尚未真机验证"。

---

## 5. 竞品做对了什么（可以直接抄）

1. **把"连接"做成一个动作**：星际掌柜 = 订阅 → 启用 → 填 URL；Jerod = 读 `connection.json` 自动拿 token；detyse = `uv` 自动装依赖。
2. **能力发现工具**：星际掌柜的 `game_capabilities` 让 AI 自己回答"这个版本能干什么"，比读 114 条工具描述便宜得多。
3. **可审计叙事**：SHA256 + ILSpy 步骤 + 权限逐条解释（星际掌柜、AI Data Interface）。玩家不是不信你，是不知道该怎么信。
4. **Skill 即产品**：Jerod 把"怎么讨价还价、怎么周转、什么时候停"写成文档，接口退居其次。
5. **真机日志文化**：mcxufly 每个版本一份 `LIVE-*.md`，写清楚"验了什么、没验什么、哪次进程退了"。
6. **开发者 DX**：热重载、日志 ring buffer、离屏启动、虚拟存档——这些让 modder 上瘾。
7. **诚实边界**：一灯把"不支持鉴定、不保证无人值守"写在最前面，反而建立了信任。
8. **视觉化反馈**：小肥鱼把 AI 的思考实时显示在右上角——玩家买的是"看得见的智能"。

---

## 6. 我们真正的优势与短板

### 优势（别人短期抄不走）

1. **动作契约**：rev 不匹配拒绝、action_id 幂等、失败回滚、录制槽隔离——这是"无人值守"的前提。
2. **广度**：夜间、机器、酿酒、电话、彩票、派系、安检、远征、养殖，114 个工具覆盖了竞品没碰的系统。
3. **测试资产**：C# 462 + 契约 392 + Python 81；改游戏版本时这是护城河。
4. **记忆**：`memory_bootstrap/retrieve/get/status` 已经在代码里，竞品只有星际掌柜有类似能力。
5. **CompactSort**：唯一的二维重排算法，可以变成"AI 摆货"的独占能力。

### 短板（每一条都在丢用户）

1. **不在货架上**：工坊搜不到 PawnMind，GitHub 也没有 release 页。
2. **装不上**：Python + venv + `bridge.conf` 路径，劝退 90% 的玩家。
3. **证不了**：README 自己写"尚未真机验证"；竞品有 LIVE 文档和视频。
4. **不会讲故事**：没有 Skill、没有"AI 经营到第几天"的战报、没有视频。
5. **工具太多**：114 个工具没有玩家档，模型选择成本和 token 成本都高。
6. **数字对不上**：manifest 0.3.0 / tag v1.0.1 / README 100 / PawnMind README 89 / AGENTS 114——对外这是"不专业"的信号。
7. **名字撞车**：PawnMind、PawnPilot（detyse 前身）、Pawn Console、Probably Pawned、Pawn Dashboard——"Pawn" 前缀已经拥挤。

---

## 7. 建议

### P0（本周，决定有没有资格上桌）

| # | 动作 | 验收标准 |
|---|---|---|
| 1 | **上架工坊 beta**（哪怕标 experimental） | 干净 Windows 机器：订阅 → 启用 → 复制一段 prompt → AI 首次 `get_state` 成功 **≤5 分钟**，全程不敲命令 |
| 2 | **补真机证据** | 固定存档连跑 3 天：开店/接客/成交/关店/睡觉/付租；注入一次 rev 冲突验证拒绝与重试；产出 `verification/live-*.md` |
| 3 | **消灭数字漂移** | 工具表由脚本从 `mcp_server.py` 生成并进 CI；manifest / tag / 两份 README / AGENTS 版本与工具数一致 |

> P0-1 的最短路径不是重写架构，而是：发布一个**自带 Python 运行时的启动器**（或把 `mcp` 依赖 vendor 进仓库），
> 首次运行自动写 `connection.json`（含轮换 token），让用户只需要"选文件、点下一步"。

### P1（2 周，建立差异化）

| # | 动作 | 说明 |
|---|---|---|
| 4 | **PawnMind Skill** | 操作手册 + 经营策略 + 6 类失败恢复（rev 冲突、超时重发、弹窗、教程、房东队列、店态不允许）+ 停手条件 |
| 5 | **玩家档 40 工具** | `--tools player`，只暴露经营闭环；`game_capabilities` 式发现工具替代长列表 |
| 6 | **安全白皮书** | 一页讲清 token / rev / 幂等 / 回滚 / 只监听 127.0.0.1 / 不读隐藏字段；附 SHA256 与自审步骤 |
| 7 | **UI 兜底** | 截图 + `get_ui_snapshot` + `press` 覆盖弹窗/教程/说明窗，agent 卡住时能自救（竞品已做） |
| 8 | **自动连接文件** | 启动写 `connection.json`，token 每次轮换；stdio 保留给老客户端 |

### P2（1 个月，拉开代差）

| # | 动作 | 说明 |
|---|---|---|
| 9 | **公开评测基准** | 固定存档 + 20 个任务（首周付租、日营业额、零违规、连续 5 天），跑分对比竞品；把"接口可用 ≠ 能玩"变成我们的叙事 |
| 10 | **CompactSort 联动** | `auto_arrange` 作为 `move_item` 失败 fallback + 展示柜最优摆放——竞品都没有 |
| 11 | **长期经营** | 把 memory 工具包装成"跨天经营记忆"，给 AI 一份"上周怎么亏的" |
| 12 | **英文文档** | detyse 英文优先、星际掌柜双语；海外 20 万销量里有一半我们没触达 |

### 明确不做

- **不拼工具数量**：114 已经最多，再加没有边际收益。
- **不做任意反射 `eval_member`**：DsW-4 / 测试桥已占位，且违背我们的安全不变量。
- **不做视觉鉴定 / 自动刮卡**：已明确不做，别被竞品的"全自动"带节奏。
- **不做内置 LLM 自动游玩**：小肥鱼 / AiProbe 已占，且要替用户承担 API 成本与 Key 安全。
- **不改钱、不生成物品**：这是我们和 AI Data Interface 的根本区别，别为了 demo 破例。

---

## 8. 风险与观察指标

### 风险

1. **安全事件连带**：任意反射 + 无认证的竞品一旦被恶意利用，平台/玩家会把所有 MCP mod 一起拉黑。我们的安全叙事既是差异化，也是品类保险。
2. **官方下场**：官方 modding 指南已在推（[dragonpan2/probably-stolen-modding](https://github.com/dragonpan2/probably-stolen-modding)），若官方出 MCP/API，纯桥接层会贬值——要往"策略/评测/记忆"上迁移。
3. **热度回落**：峰值已过（11-04），新增玩家会下降；越晚发布，获客越贵。
4. **单人维护带宽**：15 个竞品里至少 5 个在周更；我们一次大版本要同时改 C#/Python/文档。
5. **命名混淆**：PawnMind / PawnPilot / Pawn Console / Probably Pawned，搜索流量会被分走。

### 每周只看 5 个数

| 指标 | 现状 | 目标（4 周） |
|---|---|---|
| 工坊订阅 / 收藏 | 0 / 0 | 100 / 20 |
| 首次连接成功率 | 未测 | ≥80% |
| 7 日留存（连回来的玩家） | 未测 | ≥30% |
| 基准任务完成率（20 任务） | 未测 | ≥60% |
| "连不上"类 issue 占比 | 未测 | <20% |

---

## 9. 附录：数据与口径

### 工坊快照（2026-11-17 02:00 CST）

| 条目 ID | 订阅 | 浏览 | 收藏 | 首发 | 最后更新 |
|---|---|---|---|---|---|
| 3833622336 小肥鱼当家 | 81 | 1000 | 16 | 11-04 | 11-11 |
| 3835953432 Build MODs with AI | 107 | 890 | 35 | 11-08 | 11-09 |
| 3833038046 一灯 MCP Bridge | 29 | 146 | 1 | 11-03 | 11-05 |
| 3834799226 DsW-4 MCP Bridge | 25 | 144 | 2 | 11-07 | 11-07 |
| 3837662737 Jerod MCP | 14 | 95 | 1 | 11-11 | 11-11 |
| 3835788758 AI Data Interface | 13 | 281 | 6 | 11-08 | 11-09 |
| 3838235633 PSAI | 6 | 31 | 0 | 11-12 | 11-12 |
| 3839603565 MCP 测试桥 | 3 | 45 | 1 | 11-14 | 11-14 |
| 3840057532 星际掌柜 | 1 | 71 | 0 | 11-15 | 11-15 |
| **合计** | **279** | **2703** | **62** | | |

### 口径与局限

- 工坊检索用关键词 `MCP`，命中 9 条；不含标题/描述未提 MCP 的 AI mod（可能漏 1–2 个）。
- 订阅是累计值，不等于活跃用户；新条目（星际掌柜、测试桥）被时间低估。
- GitHub 项目按 `probably stolen mcp` / `probably-stolen agent` / `深空当铺 mcp` 检索，star 均为 0–2，说明还在极早期。
- 所有"已通关 / 6 小时 / 零权限"均为作者自述，未复现。
- 游戏销量 20 万为官方口径，50 万为媒体转述，取保守值计算渗透率。

### 主要来源

- [Steam 工坊：Probably Stolen](https://steamcommunity.com/app/4348910/workshop/)
- [官方 20 万销量公告](https://store.steampowered.com/news/app/4348910/view/676261964507251285)
- [3DM：峰值 174,382（网易转载）](https://m.163.com/dy/article/LB74FHOU0526D8LR.html)
- [官方 Mod 开发指南](https://github.com/dragonpan2/probably-stolen-modding)
- [星际掌柜仓库](https://github.com/Arom-corgi/probably-stolen-mcp) · [mcxufly 验证记录](https://github.com/mcxufly/ProbablyStolenMcp) · [detyse](https://github.com/detyse/probably-stolen-mcp) · [PSAI](https://github.com/Iksia177/PSAI) · [Jerod Skill](https://github.com/cloud-1104/probably-stolen-player) · [一灯 releases](https://github.com/yidengdengdashi/probably-stolen-mcp-releases)
- 仓库内对照：[能力矩阵](pawnmind-capability-matrix.zh-CN.md) · [已知限制](pawnmind-known-limitations.zh-CN.md) · [工具表](pawnmind-tool-surface.zh-CN.md)
