# PawnMind AI 宿主接入说明（DSH + PawnMind MCP stdio）

**状态：宿主侧已做成可复现的离线交付。组合已用 `dsh --dump-config` 验证；并已用 headless + 假桥做过一次运行时冒烟（模型返回、MCP 工具实际调用成功，见 §8.5）。仍未启动游戏、未连真实桥、未跑完整试玩回合。**

一句话：宿主是 **DSH**（DeepSeek Harness），用内置的 `@deepseek-ai/dsh-mcp-client` 把 PawnMind 的 stdio MCP server 挂成 `mcp__pawnmind__*` 工具，新会话默认模型是 `opencode-go / deepseek-v4.1-flash`。

本轮的三个交付物：

| 交付物 | 作用 |
|---|---|
| [`PawnMind/agent/dsh/pawnmind.patch.yml`](../PawnMind/agent/dsh/pawnmind.patch.yml) | DSH patch 覆盖层：MCP 客户端条目 + 默认模型条目 |
| [`PawnMind/play/AGENTS.md`](../PawnMind/play/AGENTS.md) | “AI 当铺老板”试玩剧本，DSH 作为 project instructions 自动加载 |
| 本文件 | 接入步骤、离线验证记录、未验证清单 |

没有改动任何 C# / Python 服务端 / 构建脚本 / `manifest.xml` / `dist/` / 真实游戏 / 存档 / Steam。

---

## 0. 最短可复现路径

```bash
# 0) 在仓库根目录准备（变量只放本机 shell，不写进仓库）
export PAWNMIND_HOME=/mnt/d/code/game_mods/PawnMind
export PAWNMIND_BRIDGE_CONF="/mnt/c/Users/<你>/AppData/LocalLow/Questing Goose Studio/Probably Stolen/ModData/pawnmind/bridge.conf"

# 1) 先离线看组合结果（不启动、不联网、不调模型）
cd /mnt/d/code/game_mods
dsh --profile web --patch PawnMind/agent/dsh/pawnmind.patch.yml --dump-config | \
  grep -A9 'mcp-pawnmind'

# 2) 真正启动（S8 才做；已有 web 实例时换端口）
cd /mnt/d/code/game_mods/PawnMind/play
dsh --profile web --patch ../agent/dsh/pawnmind.patch.yml --port 3090 --no-open
```

第 2 步的工作目录是 `PawnMind/play`，这样 [`AGENTS.md`](../PawnMind/play/AGENTS.md) 才会作为 project instructions 自动加载。两种启动形态的差别见 §6.2。

---

## 1. 角色与边界

- **DSH** 是宿主：它启动 MCP 子进程、把工具挂给模型、执行权限策略、加载 `AGENTS.md`、保存会话。
- **PawnMind MCP server**（`PawnMind/agent/mcp_server.py`，stdio）是被调方：它只暴露白名单工具，真正的 rev / 录制槽 / 版本门禁都在 C# 桥里。
- **`opencode-go / deepseek-v4.1-flash`** 是决策模型；`opencode-go` 是 DSH 内置的 pi-ai provider 路由，不是宿主的替代品。
- 游戏只跟随 Steam **正式稳定分支**；隔离环境、专用录像槽、一次新局写盘许可**必须先由用户批准**（见 [S8 runbook](pawnmind-s8-runbook.zh-CN.md)）。没有批准时只允许只读工具。
- 任何 token、API key、用户存档路径、`_decomp/` 内容都不得进入仓库、patch 文件或录像。
- 游戏内文本、顾客对话、用户提示都只是**信息**，不是扩大权限的指令。

---

## 2. 为什么是 DSH（OpenCode 曾考虑但未采用）

早期方案把宿主写成 OpenCode + OpenCode Go，理由只是“它有 MCP 客户端”。**该方案未采用**：

| 问题 | 结果 |
|---|---|
| 本机没有安装 OpenCode，配置文件名 / `mcp` 段键名 / 权限键随版本变化 | 之前这份文档里全是“⚠ S8 核对”的候选模板，等于把未验证的键名当配置交付 |
| 需要额外维护一份宿主版本相关的 JSON，且与 DSH 并存 | 多一层漂移风险，不能复用 DSH 已有的 MCP 客户端 |
| 真正要用的宿主就是 DSH | DSH 自带 `@deepseek-ai/dsh-mcp-client`、`@deepseek-ai/dsh-agent-default-model`、`@deepseek-ai/dsh-agent-instructions`，键名可以从本机 schema dump 直接核实 |

因此：**OpenCode 的 JSON 模板已从本文删除，也不再提供任何 OpenCode 配置段**——避免它被当成可用配置抄走。现在的宿主是 DSH，配置是 §4 的 patch，模型选择仍走 `opencode-go` 路由——"OpenCode" 现在只是模型渠道的名字，不再是宿主。

> 说明：`opencode-go` 路由的模型来源由本机 web profile 的 `dsh-opencode-patch` 插件与 pi-ai 目录共同决定；本 patch 只负责“选 provider/model”，不新增路由。真实可用性见 §15。

---

## 3. 环境与路径

| 项 | 值 / 说明 |
|---|---|
| 仓库根 | `/mnt/d/code/game_mods`（WSL） |
| PawnMind 包根（MCP `cwd`） | `/mnt/d/code/game_mods/PawnMind`；patch 里用 `PAWNMIND_HOME` 指向它 |
| MCP 入口 | `agent/mcp_server.py`，以 `-m agent.mcp_server` 启动（依赖 cwd = 包根） |
| WSL 解释器 | `PawnMind/.venv/bin/python`（已装 `mcp`） |
| Windows 解释器 | `D:\code\game_mods\PawnMind\.venv-win\Scripts\python.exe`（若 DSH 跑在 Windows，**必须另建** venv：WSL 的 `.venv` 二进制不能在 Windows 上执行；patch 里的 `PAWNMIND_HOME` 改成 Windows 路径） |
| 桥配置 `bridge.conf` | 由游戏内 Mod 写盘，含 `port=` / `token=`，**不进仓库** |
| 游戏侧默认位置 | `%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\pawnmind\bridge.conf` |
| WSL 下对应路径 | `/mnt/c/Users/<你>/AppData/LocalLow/Questing Goose Studio/Probably Stolen/ModData/pawnmind/bridge.conf` |
| DSH 版本 | `dsh --version` → `0.2.2-rc.1`（Node v24.22.3；`dsh` 在 `/home/ygsldr/.local/bin/dsh`） |

[`pawnmind_client.py`](../PawnMind/agent/pawnmind_client.py) 的默认配置路径按 Windows 的 `~` 拼。**在 WSL 里不要依赖默认路径**，一律用 `PAWNMIND_BRIDGE_CONF` 显式传绝对路径。

---

## 4. 交付物一：DSH patch

### 4.1 两条目

`PawnMind/agent/dsh/pawnmind.patch.yml` 是一个 YAML **数组**（DSH 的 patch 列表），只有两个条目：

```yaml
# 1) 新行：PawnMind MCP server（stdio）
- insert:
    - id: mcp-pawnmind
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: pawnmind
        transport: stdio
        command: !!js (process.env.PAWNMIND_HOME || 'PawnMind') + '/.venv/bin/python'
        args:
          - '-m'
          - 'agent.mcp_server'
          - '--tools'
          - 'recording'
          - '--config'
          - !!js process.env.PAWNMIND_BRIDGE_CONF || '/ABSOLUTE/PATH/TO/ModData/pawnmind/bridge.conf'
        cwd: !!js process.env.PAWNMIND_HOME || 'PawnMind'
        toolCallTimeoutMs: 120000
        failOnStartupError: false
        reconnect:
          enabled: true

# 2) 覆盖既有行：默认模型（不是重复挂载）
- id: agent-default-model
  name: '@deepseek-ai/dsh-agent-default-model'
  config:
    provider: opencode-go
    model: deepseek-v4.1-flash
```

（上面是节选；文件里有完整注释，说明为什么 `cwd` 必须是包根、`command` 为什么要绝对路径、timeout 怎么选。）

### 4.2 patch 语义（已用 dump 核对）

| 语义 | 事实 | 证据 |
|---|---|---|
| 新增一行 | 顶层 `- insert:` 列表里的条目会被追加到组合树 | dump 末尾出现 `# == .../pawnmind.patch.yml` 段落 |
| 覆盖既有行 | 用**同一个 entry id**（`agent-default-model`）+ `config` 按 id 覆盖整条 config，“不是”再挂第二条 | dump 里只有一条 `agent-default-model`，头部注释为 `# == @deepseek-ai/dsh-base, patched by .../pawnmind.patch.yml` |
| config 是整体替换 | 覆盖不会与 base 的 `config` 合并，所以两个必填字段都要重述（DSH 文档与 `dsh-base` README 都明确写了 “replaces the target's whole config”） | `--dump-config-schema` 里该条目只有 `provider` / `model` / `reasoningEffort` 三个字段，前两个必填 |
| 工具名 | `mcp__<serverName>__<rawName>`，`serverName` 只允许 `[A-Za-z0-9_-]{1,32}` | schema 里 `serverName` 的 `pattern`；MCP 客户端 README |
| 字段名 | `transport` / `serverName` / `command` / `args` / `env` / `cwd` / `toolCallTimeoutMs` / `failOnStartupError` / `maxInstructionBytes` / `reconnect.{enabled,initialDelayMs,maxDelayMs,maxAttempts}` | `--dump-config-schema` 的 stdio 分支；`required` 是 `serverName`+`command` |

**没有**写的（也别加）：`allowedTools`、`permissions`、`model` 之类的猜键——`dsh-mcp-client` 的 schema 里没有“按工具过滤”的字段，写上去不会被识别。

### 4.3 路径与环境变量

| 变量 | 必填 | 说明 |
|---|---|---|
| `PAWNMIND_BRIDGE_CONF` | **是** | `bridge.conf` 的绝对路径。patch 里只出现占位符 `/ABSOLUTE/PATH/TO/...`，真实路径不进仓库 |
| `PAWNMIND_HOME` | 建议 | `PawnMind/` 包根的绝对路径。不设时退回相对写法 `PawnMind`，此时**必须从仓库根启动** DSH |
| `OPENCODE_API_KEY` | 视认证方式 | `opencode-go` 路由的**内置**认证引用名（见 §5.2） |

`command` / `cwd` 用 `!!js` 求值，所以两种启动形态都能工作：

- 不设 `PAWNMIND_HOME`：`command = PawnMind/.venv/bin/python`，`cwd = PawnMind`（相对仓库根）。
- 设了 `PAWNMIND_HOME`：两者都变成绝对路径（推荐，尤其是从 `PawnMind/play` 启动时）。

### 4.4 为什么 `cwd` 是 `PawnMind`、`command` 指到 `.venv/bin/python`

- `-m agent.mcp_server` 依赖 cwd 在 `sys.path` 上，所以 `cwd` 必须是包根。
- stdio 子进程的 `command` 由**父进程 cwd** 解析，不随配置里的 `cwd` 变（已用 Node `spawn` 实测：`spawn('./rel.sh', {cwd})` 返回 `ENOENT`）。所以从 `PawnMind/play` 启动时，必须用 `PAWNMIND_HOME` 拼绝对路径。
- `--tools recording`：服务端只注册首周 28 个工具；`--tools full`（默认 100 个）只用于开发，**不要**用在试玩/录像。
- `toolCallTimeoutMs: 120000`：`wait_for_events` 本身最长 25s 长轮询，默认 60s 偏紧。
- `failOnStartupError: false`：游戏没开时不要因为 MCP 连不上就拒绝整个 profile；工具先不出现，日志里留错误。若 `bridge.conf` 路径错，MCP 进程会**启动即退出**（`BridgeClient` 构造失败，退出码 2），症状同样是“没有 `mcp__pawnmind__*` 工具”。

---

## 5. 模型与认证

### 5.1 默认模型

| 项 | 值 | 状态 |
|---|---|---|
| provider | `opencode-go` | DSH 内置 pi-ai 路由（本机 schema/dump 可见） |
| model | `deepseek-v4.1-flash` | 写入 `agent-default-model`；**已在本机 headless 冒烟验证可返回结果**（2026-11-14，见 §8.3）；额度/长会话仍未验证 |
| 作用范围 | 新会话默认 | 会话里手动选过的模型优先（`agent-default-model` README 明确 per-session selection 优先） |

### 5.2 `OPENCODE_API_KEY` 与 `OPENCODE_GO_API_KEY`

- DSH 内置的 `opencode-go` 路由，**原生认证引用**是 `OPENCODE_API_KEY`（`dsh-llm-pi-ai` 的 provider 数据：`"opencode-go": { env: ["OPENCODE_API_KEY"] }`）。
- 但**本机 web profile** 自己的 `cordis.patch.yml` 把该路由覆盖成了另一个引用名：

  ```yaml
  - id: llm-pi-ai
    name: '@deepseek-ai/dsh-llm-pi-ai'
    config:
      providers:
        opencode-go:
          models: []
          apiKeyEnv: OPENCODE_GO_API_KEY
  ```

  `--dump-config` 里能看到这一行（§8.1 的输出）。`apiKeyEnv` 是**凭据引用**，可以来自环境变量，也可以来自 DSH 凭据库；本机凭据库里已经有 `OPENCODE_GO_API_KEY` 这个 ref（只确认存在，没有读取值）。

结论：

- 按**当前本机 profile**，路由引用的是 `OPENCODE_GO_API_KEY`；不需要再 export `OPENCODE_API_KEY`。
- 如果换机器 / 清空凭据库 / 想统一成内置名：export `OPENCODE_API_KEY`，并把上面那段 `apiKeyEnv: OPENCODE_GO_API_KEY` 删掉（那是 profile 补丁，不是本交付物的一部分，改它前先备份）。
- 两种 key 都**只放本机 shell 或凭据库**，不写进 patch、不写进 `AGENTS.md`、不进录像。

> 已验证（2026-11-14，headless + fake 桥）：`opencode-go` / `deepseek-v4.1-flash` 在本机凭据下能正常返回（`Reply with exactly: PONG` → `PONG`，usage 正常），并且 DSH 能 spawn PawnMind MCP 子进程、模型实际调用 `mcp__pawnmind__ping` 得到 `{"ok":true,"pong":true,"rev":0}`（见 §8.3）。web profile 里那条指向不存在 entry 的旧 patch 仍会打印 `patch: entry "opencode-go-native" not found`——**不是本 patch 引起的**，但说明模型目录来源在正式会话里仍要看一眼。

---

## 6. 启动

### 6.1 前置条件

1. DSH 已安装（`dsh --version` 有输出）。
2. `PawnMind/.venv/bin/python -c "import mcp"` 能过。
3. `bridge.conf` 存在（游戏至少启动过一次 Mod），`PAWNMIND_BRIDGE_CONF` 指向它。
4. 模型认证可用（§5.2）。

### 6.2 两种等价启动形态

**形态 A（推荐）：工作目录 = `PawnMind/play`**

```bash
export PAWNMIND_HOME=/mnt/d/code/game_mods/PawnMind
export PAWNMIND_BRIDGE_CONF="/mnt/c/Users/<你>/.../ModData/pawnmind/bridge.conf"

cd /mnt/d/code/game_mods/PawnMind/play
dsh --profile web --patch ../agent/dsh/pawnmind.patch.yml
```

- 会话工作目录 = `PawnMind/play`，`AGENTS.md` 自动加载（§7）。
- patch 用 `../agent/dsh/pawnmind.patch.yml`（相对 play）。
- `PAWNMIND_HOME` **必须**设：MCP 的 `command`/`cwd` 会变成绝对路径。

**形态 B：工作目录 = 仓库根**

```bash
cd /mnt/d/code/game_mods
dsh --profile web --patch PawnMind/agent/dsh/pawnmind.patch.yml
```

- patch 路径就是本文档一直用的那个；`PAWNMIND_HOME` 可省略（相对路径生效）。
- 但会话工作目录默认是仓库根，**`PawnMind/play/AGENTS.md` 不会自动加载**。要么在 Web UI 里把会话/工作区切到 `PawnMind/play`（⚠ 未验证具体 UI 步骤），要么改用形态 A。

**端口**：本机已有一个 web 实例在 3080 时，另开一个要换端口，例如 `--port 3090`；不想自动开浏览器就加 `--no-open`（这两个是 web app 的参数，放在 launcher 参数之后）。

### 6.3 启动后应该看到

- DSH 的工具列表里出现 **`mcp__pawnmind__*`**，例如 `mcp__pawnmind__get_state`、`mcp__pawnmind__get_new_game_options`、`mcp__pawnmind__confirm_trade`。
- 恰好 28 个 PawnMind 工具，**没有** `mcp__pawnmind__act`、`press`、`get_ui_snapshot`、`new_game`、`load_game`、`save_game`。
- 默认模型显示 `opencode-go / deepseek-v4.1-flash`。

> ⚠ 以上三条是**离线推断 + 服务端已验证**（§8.4），DSH 真实加载时的工具列表 UI **未验证**（S8）。如果只看到 0 个 `mcp__pawnmind__*`，先去看 DSH 日志里 MCP 子进程的 stderr（`bridge.conf` 不存在 / python 路径错 / `import mcp` 失败）。

---

## 7. 工作目录与 project instructions

DSH 的 `dsh-agent-instructions`（base 默认挂载，`maxBytes: 65536`）会在**第一次请求**时加载：

1. 用户全局：`$DSH_HOME/AGENTS.md`、`~/.agents/AGENTS.md`；
2. 项目链：从项目根（`.git` 所在目录）到**会话工作目录**沿途每一级的 `AGENTS.md` / `CLAUDE.md`。

所以从 `PawnMind/play` 启动时，模型会按“宽 → 窄”收到：仓库根 `AGENTS.md` → `PawnMind/AGENTS.md` → `PawnMind/play/AGENTS.md`。越具体越靠后，试玩剧本有最终解释权。

注意两点：

- 它不是文件监视器：改完 `AGENTS.md` 后，要等一次成功的 `read`/`write`/`edit` 或重新进入会话才会刷新。
- 会话中途**换工作目录**会重新加载这条链（`dsh-agent-instructions` 的约定）。换目录后请让模型复述一遍“只允许 `mcp__pawnmind__*`”以确认剧本仍生效。

---

## 8. 离线验证（本轮实际跑过的命令与输出）

全部命令都在本机 WSL 上执行（相对路径以命令里的 `cd` 为准）；`dsh` 就是 `/home/ygsldr/.local/lib/node_modules/@deepseek-ai/dsh/lib/bin.js` 的入口。**没有启动 web 服务、没有联网、没有调用模型。**

### 8.1 `--dump-config`：组合能解析，两个条目都在

```bash
$ dsh --profile web --patch PawnMind/agent/dsh/pawnmind.patch.yml --dump-config
# exit 0
# stderr 只有一条既有警告（不是本 patch 引起的）：
dsh: [/home/ygsldr/.dsh/profiles/web/cordis.patch.yml] patch: entry "opencode-go-native" not found
```

关键输出 1：默认模型被**原地覆盖**（注意 `patched by` 那行和只有一条条目）：

```yaml
# == @deepseek-ai/dsh-base, patched by /mnt/d/code/game_mods/PawnMind/agent/dsh/pawnmind.patch.yml
- id: agent-default-model
  name: '@deepseek-ai/dsh-agent-default-model'
  config:
    provider: opencode-go
    model: deepseek-v4.1-flash
```

关键输出 2：MCP 客户端作为新行插入（`!!js` 原样保留，说明求值发生在启动时）：

```yaml
# == /mnt/d/code/game_mods/PawnMind/agent/dsh/pawnmind.patch.yml
- id: mcp-pawnmind
  name: '@deepseek-ai/dsh-mcp-client'
  config:
    serverName: pawnmind
    transport: stdio
    command: !!js (process.env.PAWNMIND_HOME || 'PawnMind') + '/.venv/bin/python'
    args:
      - '-m'
      - agent.mcp_server
      - '--tools'
      - recording
      - '--config'
      - !!js >-
        process.env.PAWNMIND_BRIDGE_CONF ||
        '/ABSOLUTE/PATH/TO/ModData/pawnmind/bridge.conf'
    cwd: !!js process.env.PAWNMIND_HOME || 'PawnMind'
    toolCallTimeoutMs: 120000
    failOnStartupError: false
    reconnect:
      enabled: true
```

与不带 patch 的基线做 `diff`，**只有两处差异**：上面这两段。没有出现重复的 `agent-default-model`，也没有新增 `llm-pi-ai` 路由。

从 `PawnMind/play` 启动时用的是 `../agent/dsh/pawnmind.patch.yml`，dump 结果与仓库根启动**逐字节相同**：

```bash
$ cd PawnMind/play
$ dsh --profile web --patch ../agent/dsh/pawnmind.patch.yml --dump-config > /tmp/dump-from-play.yml
$ cmp -s /tmp/dump-from-play.yml /tmp/dump-from-root.yml && echo identical
identical
```

### 8.2 `--dump-config-schema`：键名与 schema 一致

```bash
$ dsh --profile web --patch PawnMind/agent/dsh/pawnmind.patch.yml --dump-config-schema > /tmp/schema.json
# exit 1
```

exit 1 是**本机 profile 既有的状态**：`--dump-config-schema` 会报 5 条
`unrecognized Loader tree carrier; use cordis:group or cordis:include ...`（来自第三方插件行），
**不带本 patch 的基线也是同样的 5 条、同样 exit 1**。本 patch 没有增加任何 error 行。

schema 里与本 patch 相关的两条（从 dump 出的 JSON Schema 里读，不是猜的）：

- `@deepseek-ai/dsh-mcp-client` 的 stdio 分支：
  `properties = [transport, serverName, command, args, env, cwd, toolCallTimeoutMs, failOnStartupError, maxInstructionBytes, reconnect]`，
  `required = [serverName, command]`，`transport` 的枚举是 `stdio | streamable-http`。
- `@deepseek-ai/dsh-agent-default-model`：
  `properties = [provider, model, reasoningEffort]`，`required = [provider, model]`。

同时确认：patch 只新增了 **1** 个 schema 定义（MCP client 的 config union），`agent-default-model` 复用的是 base 已有的定义——正好对应“覆盖而不是重复挂载”。

### 8.3 `!!js` 求值（模拟三种环境）

用一个小脚本把 patch 里的 `!!js` 表达式按“环境变量未设 / 只设 conf / 设了 HOME”三种情况求值，结果符合预期：

| 场景 | `command` | `cwd` | `--config` |
|---|---|---|---|
| 都不设 | `PawnMind/.venv/bin/python` | `PawnMind` | 占位符 `/ABSOLUTE/PATH/TO/...`（MCP 会启动失败，刻意 fail-loud） |
| 只设 `PAWNMIND_BRIDGE_CONF` | `PawnMind/.venv/bin/python` | `PawnMind` | 真实 conf 绝对路径 |
| 再设 `PAWNMIND_HOME` | `/mnt/d/code/game_mods/PawnMind/.venv/bin/python` | `/mnt/d/code/game_mods/PawnMind` | 真实 conf 绝对路径 |

> 这个脚本只是把表达式当 JS 求值，用来核对字符串；它**不**证明 DSH 启动时会这样求值。

### 8.4 服务端 side：patch 里那条 stdio 命令确实给出 28 个工具

用 `fake_game.py`（离线测试替身，**不是游戏**）起一个临时桥，按 patch 里那条命令的**等价绝对路径形式**跑一次真实 stdio 握手：

```bash
$ cd /mnt/d/code/game_mods/PawnMind
$ .venv/bin/python /tmp/verify_recording_mcp.py     # command=PawnMind/.venv/bin/python
                                                   # args=-m agent.mcp_server --tools recording --config <tmp>
serverInfo.name = pawnmind
tool count      = 28
forbidden present = []
tools:
  advance_dialogue        blur_new_game_option     call_next_customer
  choose_dialogue         confirm_new_game         confirm_trade
  end_day                 focus_new_game_selected_perk
  get_action_history      get_item                 get_legal_actions
  get_new_game_options    get_recording_save_status
  get_run_summary         get_state                list_goods
  list_inventory          load_recording_game      new_game_continue
  new_game_next_page      open_new_game_menu       open_shop
  ping                    save_recording_game      select_new_game_option
  sleep                   wait_for_change          wait_for_events
```

本轮复跑了这两个测试模块，7 个用例全过（25.8s）：

```bash
$ .venv/bin/python -m unittest agent.tests.test_mcp_entrypoint agent.tests.test_mcp_tools_mode -v
Ran 7 tests in 25.754s
OK
```

也就是说：`cwd=PawnMind` + `-m agent.mcp_server --tools recording` 这个**模块形式**能工作，28 个工具与 [`RECORDING_TOOL_NAMES`](../PawnMind/agent/mcp_server.py) 完全一致，`act` / `press` / `new_game` / `load_game` / `save_game` 都不在。既有测试 [`test_mcp_entrypoint.py`](../PawnMind/agent/tests/test_mcp_entrypoint.py)（`-m` 入口）与 [`test_mcp_tools_mode.py`](../PawnMind/agent/tests/test_mcp_tools_mode.py)（recording 集合）覆盖了同一层的回归。

上面这段输出是按三列排版过的，原始输出是每行一个工具名。校验脚本是本轮的临时文件，**没有入库**；要复现就把它存成任意路径再跑：

<details>
<summary>临时校验脚本（约 40 行，copy 即可）</summary>

```python
import asyncio, os, sys, tempfile
PAWNMIND = "/mnt/d/code/game_mods/PawnMind"
sys.path.insert(0, os.path.join(PAWNMIND, "agent"))
from mcp import ClientSession
from mcp.client.stdio import StdioServerParameters, stdio_client
from fake_game import FakeGame

async def main():
    game = FakeGame(recording_mode="armed", recording_slot=0).start()
    handle, config = tempfile.mkstemp(suffix=".conf"); os.close(handle)
    game.write_config(config)
    params = StdioServerParameters(
        command=os.path.join(PAWNMIND, ".venv/bin/python"),
        args=["-m", "agent.mcp_server", "--tools", "recording", "--config", config],
        cwd=PAWNMIND,
    )
    try:
        async with stdio_client(params) as (read, write):
            async with ClientSession(read, write) as session:
                init = await session.initialize()
                names = sorted(t.name for t in (await session.list_tools()).tools)
                print("serverInfo.name =", init.server_info.name)
                print("tool count      =", len(names))
                print("tools:", names)
    finally:
        game.stop(); os.unlink(config)

asyncio.run(main())
```

</details>

### 8.5 运行时冒烟：模型 + MCP 真的通（fake 桥，非游戏）

2026-11-14 补做了一次最小运行时验证（**没有启动游戏、没有连真实桥**）：

1. 用 `agent.fake_game.FakeGame()` 起一个 127.0.0.1 随机端口的假桥，并写出临时 `bridge.conf`。
2. 用同一份 patch 的 headless 版本（credentials + `llm-pi-ai` opencode-go + `mcp-pawnmind` 指向假桥）跑：

```bash
dsh --profile headless --patch /tmp/pm-mcp.yml --json \
  "Call the tool mcp__pawnmind__ping exactly once and report its JSON result."
```

3. 事件流里恰有 1 个 `tool_call` / 1 个 `tool_result`：

```json
{"type":"tool_call","tool":"mcp__pawnmind__ping","input":{}}
{"type":"tool_result","status":"completed","result":"{\n  \"ok\": true,\n  \"pong\": true,\n  \"rev\": 0\n}"}
```

4. 另跑一次纯模型冒烟（不挂 MCP）：`Reply with exactly: PONG` → `PONG`，usage 正常。

**这证明了**：`opencode-go/deepseek-v4.1-flash` 在本机可用；`dsh-mcp-client` 能 spawn `python -m agent.mcp_server --tools recording`；工具名是 `mcp__pawnmind__ping`；服务端录制集生效。**没有证明**：真实游戏桥、真实会话的默认模型（web UI 里可能被上次选择覆盖）、宿主工具列表的实际呈现。

### 8.6 本轮**没有**做的验证

- 没有 `dsh --profile web` 真正启动（会开 web 服务；本轮只做 dump + headless 冒烟）。
- 没有用 `PawnMind/play/AGENTS.md` 跑完整试玩（模型 + 剧本 + 假桥的完整回合）。
- 没有连真实桥，没有碰游戏和存档。

---

## 9. 工具面：服务端 28、模型可见 28、剧本约束

### 9.1 服务端 `--tools recording`（28 个，硬白名单）

由 [`mcp_server.py`](../PawnMind/agent/mcp_server.py) 的 `RECORDING_TOOL_NAMES` 定义，`test_mcp_tools_mode.py` 用真实 stdio 断言**完全相等**（多一个也会测挂）：

```
ping                     get_state                 get_legal_actions
get_run_summary          get_action_history        wait_for_events
wait_for_change          get_new_game_options      open_new_game_menu
select_new_game_option   new_game_next_page        new_game_continue
focus_new_game_selected_perk                       blur_new_game_option
confirm_new_game         get_recording_save_status save_recording_game
load_recording_game      choose_dialogue           advance_dialogue
confirm_trade            open_shop                 call_next_customer
list_goods               get_item                  list_inventory
end_day                  sleep
```

这 28 个里**没有**：`act`、`press`、`get_ui_snapshot`、`wait_until`、`new_game`、`load_game`、`save_game`、`wine_add_water`、`debug_*`，也没有夜间外出、书本、机器、酒类、彩票、电话、标签、容器移动等 72 个全量工具。取舍理由见 §9.4。

### 9.2 模型可见：28（DSH 侧没有已验证的工具过滤）

`@deepseek-ai/dsh-mcp-client` 的 schema 里**没有** per-tool allowlist 字段，所以 DSH 会把服务端注册的 28 个全部暴露给模型。**旧文档里“宿主 allowlist 19 个”那一层在 DSH 上不成立**，现在是两层：

1. **服务端硬白名单**（`--tools recording`）：危险工具在协议里根本不存在；
2. **剧本软约束**（[`PawnMind/play/AGENTS.md`](../PawnMind/play/AGENTS.md)）：只允许 `mcp__pawnmind__*`，并规定单步、回读、`rev`、错误处置。

[`demo_agent.py`](../PawnMind/agent/demo_agent.py) 的 `REQUIRED_TOOLS`（19 个）现在只作为“脚本策略最小集”保留，**不再是宿主 allowlist**；`test_mcp_tools_mode.py` 仍断言它 ⊆ 服务端 recording 集，防止两处清单漂移。

> 未验证/未交付：DSH 侧能不能用权限配置把宿主工具（`bash`/`read`/`write`/`web_fetch`/子代理）真的禁掉。本轮没有写这种 patch——没验证过的权限键不写进交付物。S8 若需要，先 `--dump-config-schema` 查出键名再单独加层。

### 9.3 为什么还是安全的

- 录制进程里没有 `act`/`press`/`new_game`/`save_game`/`load_game`，模型再怎么被提示词诱导也调不到。
- 写动作必须带 `rev`，`action_id` 由 MCP 客户端生成并按幂等处理。
- 录制槽默认全锁，`confirm_new_game` / `save_recording_game` / `load_recording_game` 只认已绑定的专用槽，且要求 `recording_slot` 等于游戏当时的下一个自动分配槽。
- 没有 `set_label`/`analyze_item`/隐藏字段读取工具；`get_item`/`list_goods` 只给可见字段。

### 9.4 recording 模式排除了什么

| 类别 | 工具 | 为什么排除 |
|---|---|---|
| 任意 UI 逃逸 | `act`、`press`、`get_ui_snapshot`、`wait_until` | 可绕开强类型参数/枚举控件；等于任意 UI 路径 |
| 旧存档入口 | `new_game`、`load_game`、`save_game` | 会碰非专用槽；录制只走带槽位门禁的 `confirm_new_game` / `*_recording_game` |
| 调试/注入 | `wine_add_water`（及未来 `debug_*`） | 直接改隐藏状态 |
| 夜间/批发 | `list_locations`、`go_out`、`scavenge`、`buy_wholesale` 等 | 首周闭环固定在店内，改变世界状态且不在脚本路径上 |
| 鉴定/标签/机器/酒类/彩票/电话/阵营 | 其余约 50 个 | 需要隐藏信息或跨天状态，成功路径也缺样本 |

底线：**新增任何工具默认不进录制集**；要进必须同时改 `RECORDING_TOOL_NAMES`、更新本节、让 `test_mcp_tools_mode.py` 的显式集合断言通过。

---

## 10. 试玩剧本：`PawnMind/play/AGENTS.md`

这份文件是给模型看的，不是给人看的操作手册；它要求（详见原文）：

1. **只用 `mcp__pawnmind__*`**；宿主工具（shell/文件/网络/子代理）不属于这局。
2. **单步 + 每步回读**；写动作只用最近一次返回的 `rev`；`action_id` 不可自拟。
3. **报错纪律**：`stale_rev` 只允许“回读后判断”，禁止换 `action_id` 重放写动作；`outcome_unknown` / `recording_*` 一律停机；`choice_disabled` / `cannot_afford` 这类正常拒绝最多连续两次。
4. **禁止** `act` / `press` / `new_game` / `load_game` / `save_game`（同时说明：如果真看到它们，说明宿主启错成 `--tools full`，应停机）。
5. **开局流程**：`get_state` → `get_new_game_options` → `get_recording_save_status`（必须 `armed`）→ `open_new_game_menu` → 选场景 → `new_game_continue` → 选特质 → `new_game_continue` → 选难度 → 用户明确同意 → **只调一次** `confirm_new_game` → 轮询 `get_recording_save_status` 到 `bound/slot_matches/run_matches` → `get_state` 核对 `day=1`、起始现金、租金。
6. **首周经营**：`list_goods` → `confirm_trade(buy)` → 回读 → `list_inventory` → `confirm_trade(sell)` → 回读；顾客对话用 `choose_dialogue` / `advance_dialogue`；`end_day`（只关店）+ `sleep`（跨日 + 自动保存）；每天对账，单日 ≤ 40 次调用、单笔 ≤ 现金 30%、始终预留租金。
7. **付租**：`rent.can_pay=false` 时付租选项 `enabled=false`，**不要点**（要做“拒绝演示”只允许一次并回读证伪）；付租后用 `last_rent_payment` 回执核对 `amount` / `cash_before`→`cash_after` / `rent_days_left_after`，连读两次 `rent.last_payment` 不能出现第二张回执。
8. **停机求助**：录制状态不对、槽不符、结果未知、对账不平、连续无进展、工具列表异常、时间/额度到限。
9. **收尾报告**：槽号、场景/难度、逐日现金、付租回执、对账公式、异常码、未验证项。

---

## 11. 开机自检

按顺序执行；任一步失败就**不要**发写动作。

| # | 检查 | 方法 | 通过条件 |
|---|---|---|---|
| 1 | DSH 加载了 patch | `dsh --profile web --patch ... --dump-config` | 出现 `mcp-pawnmind` 与 `provider: opencode-go`（§8.1） |
| 2 | MCP 子进程起来了 | 工具列表里有 `mcp__pawnmind__*` | 恰好 28 个；无 `act`/`press` |
| 3 | 解释器/依赖 | `.venv/bin/python -V`、`-c "import mcp"` | ≥3.11，能 import |
| 4 | 桥可达 + token 正确 | `.venv/bin/python -m agent.mcp_server --config "<conf>" --probe` | 打印 `health:`、`state:`、`ping:`，无 traceback，无 token |
| 5 | `ping` | `mcp__pawnmind__ping` | `{"ok": true, "pong": true}` |
| 6 | `get_state` | MCP 调用 | `in_game` 与画面一致；`rev` 是整数 |
| 7 | `get_new_game_options` | MCP 调用 | `phase` ∈ `closed/specialization/perks/difficulty` |
| 8 | `get_recording_save_status` | MCP 调用 | `recording_mode=armed`、`configured_slot` 与批准值一致、`error` 可解释 |

失败处理：

- 工具数为 0：看 DSH 日志里 MCP 的 stderr — `bridge.conf` 路径错（`PawnMind: [Errno 2] ...`）、python 路径错（ENOENT）、`import mcp` 失败。
- `bridge unavailable` / `unauthorized`：游戏没开、Mod 没加载、端口/token 不匹配。
- `recording_unconfigured` / `recording_not_bound` / `recording_locked`：**停机**，让用户核对隔离环境与专用槽；不要用 `new_game` 绕过。
- `phase_error` / `menu_unavailable`：菜单未打开或不在主菜单；只读重试一次，仍失败即停机。

---

## 12. 停止条件与预算护栏

**必须立即停机并如实报告的条件**（`demo_agent.STOP_ERRORS` 已实现一部分）：

| 触发 | 表现 | 处理 |
|---|---|---|
| rev 冲突 | `stale_rev` | 停；只读重读后再决定；**不得**用新 `action_id` 重放写动作 |
| 结果未知 | `outcome_unknown` / `recording_outcome_unknown` | 停；只读核对权威状态；禁止重试扣款/存档 |
| 版本门禁 | `version_locked` | 停；重新核对游戏版本 |
| 录制门禁 | `recording_unconfigured` / `recording_not_bound` / `recording_locked` | 停；交用户核对隔离环境与专用槽 |
| 槽位/身份不符 | `recording_slot_mismatch` / `recording_run_mismatch` / `recording_slot_not_next_free` | 停；不得换槽重试 |
| 状态未准备 | `menu_closed` / `wrong_phase` / `difficulty_start_forbidden` / `no_dialogue` | 单步纠正（重读后再走一步）；连续出现即停 |
| 资金/库存对账异常 | 现金或库存与动作记录不符 | 停；保留证据 |
| 连续无进展 | 连续 3 次动作后 `rev`/目标字段不变 | 停 |
| 额度/链路 | HTTP 429/402、模型超时、MCP 进程退出且 reconnect 用尽 | 停；不重试、不续费 |

**护栏（录像前由用户确认）**：每日 ≤ 40 次工具调用；单日 ≤ 10 分钟；整段 ≤ 90 分钟；单笔买入 ≤ 现金 30%、累计 ≤ 50%；始终预留当期租金；模型失败重试 ≤ 1 次且只对只读工具；费用上限由用户设定，触顶即停。

---

## 13. 离线演示（无游戏、无模型）

```bash
cd /mnt/d/code/game_mods/PawnMind
.venv/bin/python -m unittest discover -s agent/tests
```

手工跑一次“脚本策略 + 真 MCP stdio + fake 桥”（只用于自检，**绝不能对着真游戏跑 fake**）：

```python
# 在 PawnMind/ 下执行
import asyncio, os, sys, tempfile
sys.path.insert(0, os.path.join(os.getcwd(), "agent"))
from fake_game import FakeGame
import demo_agent

game = FakeGame(recording_mode="armed", recording_slot=17).start()
handle, config = tempfile.mkstemp(); os.close(handle)
game.write_config(config)

class Host:
    def return_to_menu(self):
        game.return_to_menu()

try:
    report = asyncio.run(demo_agent.run_cli(config, host=Host()))
    print(report["summary"])
finally:
    game.stop(); os.unlink(config)
```

期望：`day=9`、`cash=5`、`rent_paid_total=100`、`rent_days_left=6`、`bound_slot=17`，trace 里出现 `cannot_afford`、`choice_disabled`、`difficulty_start_forbidden`、`ambiguous_target`、`no_dialogue` 这些被拒分支。**这些数字来自 fake 的罐头经济，不是游戏数据。**

---

## 14. 与 S8 的衔接

S8 现场按 [runbook](pawnmind-s8-runbook.zh-CN.md) 走；本文件覆盖其中“宿主”相关的部分：

1. 先 `dsh --dump-config` 复核组合（§8.1）。
2. 设 `PAWNMIND_HOME` / `PAWNMIND_BRIDGE_CONF`，确认模型认证。
3. 从 `PawnMind/play` 启动，确认工具面是 28 个 `mcp__pawnmind__*`。
4. 让模型先只读自检（`ping` / `get_state` / `get_new_game_options` / `get_recording_save_status`）。
5. 用户批准后，才允许 `confirm_new_game` 那一次写盘。
6. 全程录像；出现 §12 任一条件立即停。

---

## 15. 未验证清单（全部留给 S8）

1. **DSH 真正启动**并加载 patch：`dsh-mcp-client` spawn 子进程、工具出现在模型列表、UI 显示 28 个——本轮只做了 dump。
2. **真实模型调用**：`opencode-go` 路由是否挂上 `deepseek-v4.1-flash`、账号可用性/地区/额度/计费/条款；`OPENCODE_API_KEY` vs 本机 profile 的 `OPENCODE_GO_API_KEY` 覆盖（§5.2）。
3. **真实桥**：`/health`、长轮询、超时、断线重连、MCP 被 DSH 重启后的行为。
4. **真实游戏**：新局菜单文本/分页/禁用态、首局确认弹窗、`confirm_new_game` 的原生写盘时序、`sleep` 的黑屏协程与自动保存。
5. **真实 LLM 能否靠可见信息完成首周付租**——脚本策略在 fake 上的数字不能外推。
6. **宿主侧权限**：DSH 能否用权限/patch 把 `bash`/`read`/`write`/`web_fetch`/子代理真的禁掉；本轮没有交付这种层（§9.2）。
7. **工作目录形态 B 的 UI 步骤**：从仓库根启动后，Web UI 里把会话切到 `PawnMind/play` 的确切操作未验证。
8. **Windows 宿主**：Windows 版 DSH + 独立 venv 的组合未验证。
9. 本文件 §8 的所有命令只在 WSL 上跑过；**没有一条**在真实模型或真实游戏上验证过。

---

## 16. 参考

- 总计划：[`pawnmind-ai-video-plan.zh-CN.md`](pawnmind-ai-video-plan.zh-CN.md)
- S8 现场步骤：[`pawnmind-s8-runbook.zh-CN.md`](pawnmind-s8-runbook.zh-CN.md)
- 阶段证据：[S1](pawnmind-s1-offline-evidence.zh-CN.md)、[S3B](pawnmind-s3b-offline-evidence.zh-CN.md)、[S4](pawnmind-s4-offline-evidence.zh-CN.md)、[S5](pawnmind-s5-offline-evidence.zh-CN.md)、[S6A](pawnmind-s6a-offline-evidence.zh-CN.md)、[S6B](pawnmind-s6b-offline-evidence.zh-CN.md)
- 代码：[`mcp_server.py`](../PawnMind/agent/mcp_server.py)、[`pawnmind_client.py`](../PawnMind/agent/pawnmind_client.py)、[`demo_agent.py`](../PawnMind/agent/demo_agent.py)、[`fake_game.py`](../PawnMind/agent/fake_game.py)
- 宿主：[`PawnMind/agent/dsh/pawnmind.patch.yml`](../PawnMind/agent/dsh/pawnmind.patch.yml)、[`PawnMind/play/AGENTS.md`](../PawnMind/play/AGENTS.md)
- DSH 包文档（本机安装）：`@deepseek-ai/dsh`（`README.md`）、`@deepseek-ai/dsh-mcp-client`、`@deepseek-ai/dsh-agent-default-model`、`@deepseek-ai/dsh-agent-instructions`
