# 《深空当铺：可能是偷的》MCP Mod 可行性分析

> 调研时间：2026-11-04 凌晨（北京时间）
> 目标游戏版本：正式版 0.46D / 主菜单 VERSION 100-REV6
> 本机环境：Unity 2021.3.45f2（Mono），原生 ModLoader，已装 CompactSort

## 0. 结论（TL;DR）

**技术上完全可行，而且门槛比想象中低；但"做一个 MCP mod"这件事已经有人做出来了，就在几小时前。**

- 可行：官方 ModLoader + Harmony + 未混淆的 `Assembly-CSharp` + 官方 hook/能力系统，`network` 能力就能开本机 HTTP；你的 CompactSort 已经验证了反射、Harmony、适配器测试这套工作流。
- 已有人做：`深空当铺 MCP Bridge`（作者 Yideng），v0.12.1 于 2026-11-03 23:19（北京时间）发布，Steam 工坊 + Python stdio MCP 服务，31 个工具，实验版，只发二进制未开源。
- 所以真正的问题不是"能不能"，而是"**你补哪个空白**"。现有实现自己列出的空白很明确：鉴定/真假、视觉、无人值守自动经营、多职业、评测。
- 我的建议：不要重做"31 个工具的桥"，做**垂直差异**——最值得的是「视觉鉴定 + 谈判策略 + 可复现评测」这条线；或者干脆做通用的 Unity 游戏 MCP 框架，用深空当铺当第一个 case。

---

## 1. 这个游戏适合让 AI 玩吗

《深空当铺》的核心循环是：开店 → 顾客上门（买 / 卖 / 鉴定 / 以物易物 / 批发 / 情报 / 维修）→ 定价与谈判 → 成交或拒绝 → 关店 → 夜间结算（房租、贷款、升级、外出）→ 次日。

这是 LLM agent 的"甜区"：

| 特征 | 对 AI 的意义 |
|---|---|
| 语言密集（对话、砍价、话术） | 强项，纯文本接口就够跑通大半 |
| 信息不完全（真假、顾客身份、赃物风险） | 需要推理，不是背攻略；有研究价值 |
| 回合/窗口式交互（窗口、对话、确认） | 天然适合"请求-响应"的 MCP 工具调用 |
| 资源管理（现金、房租、库存、展柜） | 可量化评测：日利润、破产率、误判率 |
| 有明确的"正确答案"（鉴定估值） | 可做 benchmark，也可做监督信号 |

不太适合的部分：实时操作（拖拽、瞄准）、纯视觉细节（赝品纹理）、需要毫秒级反应的场景。这些要么用高层动作封装，要么交给视觉模型。

---

## 2. 技术可行性

### 2.1 官方支持面比预期大

官方 modding 指南（[dragonpan2/probably-stolen-modding](https://github.com/dragonpan2/probably-stolen-modding)）确认：

- **Harmony 补丁**可用，且"打在游戏代码上是免费的"（不需要额外能力）。
- 官方 **hook points**：`OnGameLoaded*`、`OnShutterOpened*`、`OnGenerateCustomer*`、`OnWakingUp*`、`OnCreateTooltip*` 等，带优先级分层，比 Harmony 稳。
- **自定义 UI**、机器窗口、appraisal items、Mod 配置、Mod 数据目录（`ModData/<id>/`）都有官方 API。
- **Steam 工坊发布**有官方流程（steamcmd）。

明确"不支持"的：改核心循环（柜台、日循环、谈判）、改现有 UI、超大型 overhaul。但**读取状态 + 调用游戏已有动作**不属于"改"，用 Harmony/反射即可；只是游戏更新时要自己维护。

### 2.2 关键约束：能力扫描

ModLoader 会扫描 DLL 并强制声明能力：`files`、`network`、`process`、`dynamic-code`、`native`、`reflection-external`、`steam`。要点：

- 对**游戏自己的类型**做反射是免费的；对 `System.*`/`UnityEngine.*` 反射要声明 `reflection-external`。
- **`System.Diagnostics.Process` 是硬拒绝**：Mod 不能启动外部程序。所以 MCP 服务不可能由 Mod 拉起，必须由 AI 客户端（stdio）或用户手动启动。
- `network` 只影响本机 HTTP 也需要声明，玩家激活时会看到"访问互联网"的确认——这是发行时的信任成本。
- 启动另一个进程被禁止，意味着"一键安装"做不到，安装流程必然是"装 Mod + 配 MCP 客户端"两步。

### 2.3 三种架构

```
A（推荐，已被验证）
游戏(Unity/Mono) ──127.0.0.1:15527 HTTP+token──> Python MCP server(stdio) ──> AI 客户端
        Mod 侧：Harmony/反射读状态、执行动作；带 revision 防重放

B（少一个进程，但协议工作量大）
游戏 Mod 直接实现 MCP Streamable HTTP/SSE ──> 支持 URL 的 AI 客户端
        C# 里手写 JSON-RPC + SSE，Mono 下可用 HttpListener；客户端兼容性参差

C（不推荐）
外部程序读内存/截屏识别 ──> 无官方支持、脆弱、容易被判作弊
```

A 的现实理由：MCP 的 stdio 传输要求"客户端拉起服务进程"，而 Mod 不能起进程；把协议实现放在 Python 侧，C# 只做轻量 HTTP，是最省事、最稳的分工。现有项目正是这个结构。

### 2.4 版本与维护

- 0.46D 是刚发售的版本，更新会很频繁；反射路径随时可能断。
- 缓解：所有游戏内访问集中到一个 adapter 层；用 `mod-scan` 做发布前检查；用你的 AdapterTests 思路，对 `Assembly-CSharp.dll` 做契约测试（本机已有先例）。
- 官方 hook 优先，Harmony 只用于 hook 覆盖不到的地方。

---

## 3. 玩法可行性：四个硬问题

### 3.1 信息边界（最重要）

游戏故意隐藏"真假 / 顾客真实身份 / 水质组成"等字段。如果 MCP 直接暴露，AI 就在作弊，游戏也就没意义了。

正确做法：**只暴露玩家在屏幕上能看到的信息**（物品名、可见描述、试纸结果、对话文本、报价），隐藏字段一律不读。现有项目明确写了这条边界，值得继承。

### 3.2 鉴定：文本接口的天花板

鉴定是"看"的玩法：放大镜、纹理、细节比对、质疑流程。纯文本接口只能读名称和普通提示，做不了可靠真假判断。

两条路：

1. **视觉管线**：Mod 截取物品/放大镜画面 → 多模态模型 → 判断。技术上可行（`ScreenCapture` 需要 `files` 能力），但增加 token 成本与延迟，且要处理"这算不算作弊"的争议。
2. **放弃精细鉴定**，把 AI 定位成"估价 + 谈判 + 经营"助手，鉴定交给人。现有项目选的是这条。

这恰好是最大的差异化点：谁把"视觉鉴定"做稳，谁就补上了核心玩法。

### 3.3 节奏与延迟

LLM 一次工具调用 2–30 秒。如果顾客有耐心计时、砍价有倒计时，AI 会来不及。

需要实测：交易/对话窗口打开时游戏是否停表。如果不停，就得加"AI 模式"：进入交易时暂停时间（`Time.timeScale = 0` 或游戏自己的暂停），等 AI 决策后再恢复。这会改变游戏手感，需要产品判断。

### 3.4 上下文与成本

四个顶层物品区、每区最多 100 件、对话历史、报价、库存……全量塞进上下文不现实。需要：

- 结构化摘要 + 分页/按需查询（MCP resources 很适合）；
- 稳定 ID + 状态版本号（revision），避免 AI 拿旧状态下单；
- 只推增量（事件通知），而不是每次全量快照。

粗算：一个游戏日 50–200 次工具调用，按每次 1–3k token 计，约 10 万–50 万 token/日；开 prompt caching 能显著下降。这是"玩得起"的关键成本约束。

---

## 4. 市面调研：已经有人做到哪一步

### 4.1 同游戏：已经有人做了，而且很新

| 项目 | 形态 | 状态 |
|---|---|---|
| [深空当铺 MCP Bridge](https://github.com/yidengdengdashi/probably-stolen-mcp-releases) | 游戏内 Mod（`network`）+ 外部 Python stdio MCP 服务 | v0.12.1，2026-11-03 23:19 发布；31 工具；[Steam 工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3833038046) 0 订阅/19 浏览（查询时）；只发二进制，未开源 |
| PSApi（Nexus） | 游戏前置 API mod | 存在，具体能力未细看 |
| Probably Automated | 雇店员自动化的玩法 mod | 非 AI，但说明社区对"自动化"有需求 |
| 其他 mod（PrecisionLab、酿酒、无机化学等） | 内容/系统扩展 | 生态刚起步，发售一周 |

它的能力边界写得很诚实（值得一读）：

- 已实测：文字状态、单件/整批买卖、搬运/展示柜、叫客送客、部分对话电话、开关店/睡眠、档案箱、部分人脉升级、水处理/水培/酿酒/枪械的部分流程。
- 明确不支持：鉴定师职业与精细真假鉴定、截图/视觉、无人值守、任意砍价/以物易物、多职业全路线、隐藏字段读取。
- 明确不做：AI 模型、账号、自动经营策略、无限循环。
- 设计上：`revision` 防重放、`127.0.0.1` + 随机 token、拒绝重定向、动作后回读校验。

结论：**"桥"已经被修好了，而且修得不算差。** 但它停在"AI 协助经营"（人在环里、单步确认），不是"AI 自己玩"。

### 4.2 其他游戏：同架构的先例

| 项目 | 做法 | 参考价值 |
|---|---|---|
| [Balatro-MCP](https://github.com/Gdnaiteab/Balatro-MCP) | Steamodded Lua mod 开本机 HTTP + MCP server 包装 | 与深空当铺最像：商业独立游戏 + mod 桥 + 本地 HTTP |
| [minecraft-mcp-server](https://github.com/yuniko-software/minecraft-mcp-server) | Mineflayer 机器人 + MCP | 实时游戏 agent 的成熟范式 |
| [rimUIMCP](https://github.com/Hcrab/rimUIMCP) | RimWorld 状态探索 + 真实 UI 自动化 + MCP | 没有官方 API 时，UI 自动化是兜底路线 |
| [coop-anything](https://github.com/cranebeaker/coop-anything) | 通用"把 AI 指向任意单机游戏"+ MCP | 通用化路线，1.8k stars |
| [universal-modder](https://github.com/rehan-remade/universal-modder) | 让 Claude Code 改任意 PC 游戏（不是玩） | 9.3k stars，说明"AI + 游戏"热度 |
| Godot/Unity MCP 系列 | 编辑器/开发工具，不是玩游戏 | 生态参照，别混淆 |

### 4.3 学术侧

- Voyager（Minecraft 终身学习 agent）：技能库 + 自动课程，是"AI 自己玩"的经典范式。
- SIMA（DeepMind）：通用 3D 虚拟环境 agent，强调指令跟随与视觉 grounding。
- BALROG 一类 benchmark：把游戏变成可复现的 agent 评测。
- 空白：**没有看到"经济/谈判类游戏 + 隐藏信息"的公开 benchmark**，深空当铺其实很适合做一个。

### 4.4 一句话总结市场

"游戏 + MCP"在 2026 年已经是成熟套路；"深空当铺 + MCP"有了第一个实验品；"深空当铺 + 能自己玩好的 AI"还没有人证明。

---

## 5. 差异化机会（按性价比排序）

| 方向 | 内容 | 价值 | 难度 |
|---|---|---|---|
| A. 视觉鉴定 | 截图/放大镜 → 多模态模型 → 真假/估值 + 质疑话术 | 高（补核心玩法，现有实现明确放弃） | 中高（视觉、成本、判定标准） |
| B. 自主经营 agent | 日计划、预算护栏、止损、事件处理、无人值守 | 高（现有只做单步协助） | 高（长程规划、错误恢复） |
| C. 评测基准 | 固定种子/场景、日利润/误判率/破产率、可复现 | 中（研究/作品集价值） | 中 |
| D. 事件驱动低 token | MCP resources/notifications、增量状态、按需查询 | 中（体验/成本） | 中 |
| E. 通用框架 | 把桥抽象成"Unity 游戏 MCP 适配层"，本作是 case 0 | 中高（可复用） | 高 |
| F. 多智能体 | 鉴定师/谈判员/会计/采购分工 | 中（demo 好看） | 中 |

推荐组合：**A + C**（视觉鉴定 + 可复现评测），或 **B + C**（自主经营 + 评测）。前者更"游戏"，后者更"agent 研究"。

不推荐：再做一个 31 工具的桥——重复劳动，且对方已经占了工坊位。

---

## 6. 建议路线（如果要做）

### 阶段 0：验证（2–3 天）

- 装现有 Bridge，跑一个游戏日，记录：工具延迟、失败率、上下文大小、token 消耗、哪些窗口必须人工接管。
- 实测交易时游戏是否停表；实测鉴定窗口暴露了哪些可见信息。
- 产出：一页"瓶颈清单"。

### 阶段 1：MVP（1–2 周）

- 只做 8–12 个工具：`get_state`、`list_items`、`inspect_item`、`confirm_trade`、`choose_dialogue`、`call/dismiss_customer`、`open/close_shop`、`end_day`。
- Mod 侧：Harmony/反射 adapter + `network` 能力 + 本机 HTTP + token + revision。
- 外部：Python MCP server（stdio）+ 一个最小的 agent loop（Python + LLM API），能跑通"开店→接待→成交→关店→睡觉"。
- 验收：连续 3 个游戏日不崩、不丢物品、不重复扣款。

### 阶段 2：差异化（3–6 周）

- 视觉鉴定管线 + 质疑/砍价流程；
- 日计划与预算护栏；
- 评测脚本与指标；
- 工坊发布（注意 `network` 能力提示、版本兼容、README 写清边界）。

### 阶段 3：可选

- 多职业、多路线；
- 事件通知 / resources；
- 通用化框架。

### 工作量粗估

| 目标 | 人日（熟悉 C#/Unity 的单人） |
|---|---|
| 只读状态 + 5 个动作 + MCP 包装 | 5–8 |
| 可玩通一个游戏日（含错误恢复） | 15–25 |
| 视觉鉴定 + 谈判 + 评测 | 30–50 |
| 工坊级发布（多版本、文档、支持） | 60+ |

---

## 7. 风险清单

| 风险 | 影响 | 缓解 |
|---|---|---|
| 游戏频繁更新，反射断裂 | 高 | adapter 层 + 契约测试 + 只依赖官方 hook 优先 |
| 能力提示吓退玩家（network） | 中 | 文档写清只连 127.0.0.1；开源；可审计 |
| LLM 误操作毁档 | 高 | 自动备份、dry-run、revision 校验、金额上限 |
| 延迟导致错过顾客 | 中 | 交易时暂停 / AI 模式；或只做非实时环节 |
| 上下文爆炸 / 成本 | 中 | 摘要 + 分页 + 增量 + prompt caching |
| 隐藏信息泄露 = 作弊 | 高 | 明确信息边界；代码审查；不读隐藏字段 |
| 提示注入（游戏文本当指令） | 中 | 游戏文本一律当数据；工具白名单；不执行自由文本 |
| 版本/职业覆盖不足被差评 | 中 | README 写边界；先做一个职业做深 |
| 法律/ToS | 低 | 单机、不绕过 DRM、不分发游戏程序集 |

---

## 8. 参考链接

- 游戏 Steam 页：https://store.steampowered.com/app/4348910/
- 官方 modding 指南：https://github.com/dragonpan2/probably-stolen-modding
- 现有 MCP Bridge：https://github.com/yidengdengdashi/probably-stolen-mcp-releases
- 现有 Bridge 工坊页：https://steamcommunity.com/sharedfiles/filedetails/?id=3833038046
- Balatro-MCP：https://github.com/Gdnaiteab/Balatro-MCP
- rimUIMCP：https://github.com/Hcrab/rimUIMCP
- Minecraft MCP：https://github.com/yuniko-software/minecraft-mcp-server
- coop-anything：https://github.com/cranebeaker/coop-anything
- universal-modder：https://github.com/rehan-remade/universal-modder
- Voyager：https://arxiv.org/abs/2305.16291
- SIMA：https://deepmind.google/discover/blog/sima-generalist-ai-agent-for-3d-virtual-environments/
