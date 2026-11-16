# WORKSHOP_INSTALL_DRAFT — PawnMind 非破坏性安装/回滚说明（草稿）

> **状态：草稿 v0.1（2026-11-16，Nightly Batch A · 模块 G）**
> **未上架、未发布、未推送 Steam Workshop；未在真实游戏环境执行过本文任何安装步骤。**
> 本文是面向**未来 Workshop Beta** 的安装说明草稿，不是对玩家的发布承诺；所有需要实机的步骤标注 `NOT_RUN`，待用户在场、`mod-scan` 与验收完成后才能升级为正式文档。
> 配套阅读：`docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md`（兼容/发布预审矩阵）。

---

## 0. 先读：本文解决什么问题

PawnMind 的交付物有**两个物理位置完全不同**的部分：

1. **游戏内 Mod**：`PawnMind.dll` + `manifest.xml`，放进游戏的 `Mods/PawnMind/`（未来可由 Steam Workshop 分发）。
2. **外部 MCP 安装器**：主机侧的 Python venv + stdio MCP server，**不在游戏目录里**，游戏不依赖它也能运行。

本文把两者**严格分开**写，目的是避免最常见的破坏性操作：把整个仓库（含 `.venv/`、`agent/`、`tools/`、`_decomp/`）拷进 `Mods/`、覆盖玩家已有 DLL、或为了"跑通"去动存档/`ModData`。

**硬原则（全文所有步骤都不得违反）**

| # | 原则 |
|---|---|
| G1 | **不覆盖用户已有 DLL**：安装/升级前必须先备份旧文件夹并核对哈希；不做"就地覆盖单个文件"。 |
| G2 | **不触碰存档**：不读写、不复制、不删除、不重命名任何 `save_*` 文件；不做自动开局/读档/写档。 |
| G3 | **不修改游戏安装本身的文件**（`Probably Stolen_Data/`、`Managed/`、游戏根目录）。只动 `Mods/PawnMind/` 与（可选、明确告知的）`ModData/pawnmind/`。 |
| G4 | **只从 `dist/PawnMind/` 安装**：该目录应只有 `manifest.xml` + `PawnMind.dll` 两个文件。 |
| G5 | 回滚必须可行且经过演练：备份 → 安装 → 验证 → 失败即整体还原。 |
| G6 | 本文所有"预期行为"若与实机不符，**停止并记录**，不要绕过门禁（尤其 `run_mode`/token/版本门）。 |

---

## 1. 两条安装路线（不要混用）

| | 路线 A：游戏内 Mod | 路线 B：外部 MCP 安装器 |
|---|---|---|
| 装在哪 | `.../Probably Stolen/Mods/PawnMind/`（或未来 Workshop 订阅目录） | 主机任意目录（如 `D:\code\game_mods\PawnMind`），Python venv |
| 运行在 | 游戏进程内（Unity） | 独立 Python 进程（stdio MCP） |
| 必需性 | 必需（没有它就没有 HTTP bridge） | **可选**；只影响"AI 客户端能否通过 MCP 操作" |
| 网络 | 仅 `127.0.0.1:<port>` HTTP 监听 | 仅向 `127.0.0.1` 发起 HTTP 请求；stdio 与宿主通信 |
| 产物 | `manifest.xml` + `PawnMind.dll` | `.venv/` + 宿主 MCP 配置（不随游戏包分发） |
| 卸载 | 退出游戏 → 还原备份/删除该文件夹 | 删除宿主配置项 + 删除 venv；不影响游戏 |
| 绝不交叉 | ❌ 不要把 `agent/`、`tools/`、`.venv/`、`_decomp/` 放进 `Mods/` | ❌ 不要修改游戏 `Mods/` 下的 DLL 来"修 MCP" |

> 未来 Workshop Beta 的订阅者：**只需要路线 A**；路线 B 是给需要 MCP 客户端的用户的可选附加步骤。

---

## 2. 通用前置检查（两条路线都先做）

| # | 检查 | 做法 | 失败处理 |
|---|---|---|---|
| 2.1 | 游戏版本 | 确认 `Probably Stolen` 为 **0.46D**（与 `manifest.xml` 的 `<GameVersions>` 一致）；记录实际版本 | 版本不符：停止，不要安装（契约/MVID 都按 0.46D 编的） |
| 2.2 | **完全退出游戏** | 关闭游戏；确认任务管理器无 `Probably Stolen.exe` | 游戏运行中 DLL 被锁定，复制/覆盖会失败或产生半状态 |
| 2.3 | 备份现有 Mod | 若 `Mods/PawnMind/` 已存在：整个文件夹复制到游戏目录之外（如 `D:\backup\PawnMind-<日期>/`），并记录哈希 | 不备份不升级 |
| 2.4 | 记录哈希 | 对**要安装的** `dist/PawnMind/PawnMind.dll` 与 `manifest.xml` 计算 SHA256 并留存（安装后与游戏中文件对比） | 哈希不一致即停止 |
| 2.5 | 存档保护 | 找到游戏存档位置（`%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\` 及其子目录；以本机为准）→ **只定位、不操作**；不碰已知用户档（`save_11*`、`save_0/1/3/4/7`、`save_13` 等，按 AGENTS.md 既有约定） | 任何"需要动存档"的建议都属越界，直接拒绝 |
| 2.6 | 安装包来源 | 只用 `dist/PawnMind/`（应只有 2 个文件）；**不要**把仓库根、`agent/`、`tools/`、`.venv/`、`_decomp/`、`test/` 拷进 `Mods/` | 多余文件会被扫描器按 payload-file / unlisted-DLL 拒绝（见矩阵 §5 P-04） |
| 2.7 | 能力预期 | 启用时会出现能力确认。当前 manifest 声明 `network` + `reflection-external`；**静态预审预测还缺 `files`**（矩阵 §11.1，未实机确认） | 出现拒绝时记录原文，**不要**改游戏/绕过；交回开发者改 manifest 或特性 |
| 2.8 | 只读冒烟配置 | 首次跑通只用 `run_mode=bootstrap`（只读）；**不要**用 `test`，`play` 当前允许集为 0 | 非 `bootstrap` 的写操作一律不要做 |

---

## 3. 路线 A：游戏内 Mod 安装

### A-1 本地文件夹安装（Beta 阶段，用户手动）

```text
1) 退出游戏（见 2.2）。
2) 若存在旧版：备份整个 Mods\PawnMind 到游戏目录之外。
3) 把 dist\PawnMind 整个文件夹复制为：
   <Steam>\steamapps\common\Probably Stolen\Mods\PawnMind\
   目标第一层必须且只能是：
     manifest.xml
     PawnMind.dll
4) 对比 SHA256（dist 里记录的值 vs 安装后文件）。
5) 启动游戏 → 主菜单「模组 / Mods」→ 找到 "PawnMind · AI Bridge" → 启用。
6) 阅读能力确认弹窗（见 §3.2），确认后按提示重启游戏。
```

> 路径 `.../Probably Stolen/Mods/PawnMind/` 与"第一层只有 manifest.xml + DLL"来自 `README.md:401-410`；当前 `dist` 实测为这两个文件（矩阵 §9.3 D-01）。

### A-2 能力确认弹窗（预期文案与含义）

游戏会按 manifest 声明 + 扫描结果展示能力（来源：`_decomp/ModCapability.cs:62-77`）：

| 能力 | 玩家看到的含义（原文意译） | PawnMind 的实际用途 | 备注 |
|---|---|---|---|
| `network`（已声明） | "Access the internet" | **仅**绑定 `127.0.0.1` 的 HTTP 桥；不访问外网 | 矩阵 N-01；只监听回环 |
| `reflection-external`（已声明，retiring） | "Reach into system and engine internals through reflection" | 读取游戏状态/UI 的受控适配层 | **2026-12-10 更新起将被拒绝**，矩阵 §11.2 |
| `files`（**预测未声明**） | "Read and write files anywhere on this computer" | 截图证据写入 ModData；读取存档索引做录制策略；读取截图目录用量 | 矩阵 §11.1 F-01~F-07；`NOT_RUN` 待 `mod-scan` |

- 若出现 **"uses capabilities its manifest does not declare"** 拒绝：说明预测的 `files` 命中成立 → **停止安装**，这不是玩家能修的问题，交回开发者更新 manifest/特性（矩阵 §13 Top1）。
- 若出现 **DEPRECATED** 提示（`reflection-external`）：属预期（retiring），可确认启用，但发布到 Workshop 前必须整改（矩阵 C2）。
- 不要通过手动改 `ModData`、改 manifest、或"关掉扫描器"等方式绕过。

### A-3 首次启动与 `bridge.conf`

首次成功启用后，Mod 自动写连接配置（不包含用户存档数据）：

```text
%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\pawnmind\bridge.conf
```

格式（`src/Core/BridgeConfig.cs:136-146`；README:418-424）：

```ini
# pawnmind bridge config v1
port=15528
token=<64 hex chars，自动生成，勿外泄>
# 以下安全门字段默认留空 → 写操作全锁（fail-closed）
run_mode=
allow_test_mode=false
allow_test_items=false
expected_game_version=
expected_assembly_mvid=
```

**只读冒烟**才允许改 `run_mode`（改完必须重启游戏）：

```ini
run_mode=bootstrap
allow_test_mode=false
```

- 不写 `run_mode`（或非法值/解析失败）= **Locked**：除 `/health` 外全部拒绝（fail-closed）。
- 主菜单阶段可用 `bootstrap`；进入游戏后若确需 `play`，修改并重启（`play` 当前允许集为 0，属预期）。
- **`test` 仅限受控开发配置**（还需 `allow_test_mode=true`）；玩家/录制验收不得使用。
- `expected_game_version` / `expected_assembly_mvid`：只有确认实机版本/MVID 后才填；**不要**从 dist 或旧文档抄值。未填 → 写操作保持锁定（安全侧）。

### A-4 安装后健康检查（无游戏内操作）

游戏运行、Mod 已启用后，在**本机**执行（端口以 `bridge.conf` 的 `port=` 为准，默认 15528）：

```bash
# /health 不需要 token
curl -s http://127.0.0.1:15528/health
```

预期字段（`src/Bridge/BridgeServer.cs:696-760`；preflight 同源字段 `agent/preflight.py:34-42`）：

| 字段 | 预期 | 不符时 |
|---|---|---|
| `ok` | `true` | false/无响应：看游戏日志 `Player.log` 的 `[PawnMind]` 行 |
| `name` | `pawnmind` | — |
| `version` / `mod_version` | 与安装的 `manifest.xml` 的 `<ModVersion>` 一致（当前 `0.3.0`） | 版本不一致 = 装错/半覆盖，回滚重装 |
| `game_version` | `0.46D`（以游戏实际为准） | 与 manifest GameVersions 不符要停下 |
| `game_assembly_mvid` | 一个 GUID（记录备用，不公开外传） | 与 `expected_assembly_mvid` 不符 → 写门锁死（预期 fail-closed） |
| `write_gate` | 未配置 expected 时为 `locked`；这是**安全默认** | 不要为了"看见 open"去瞎填 MVID |
| `latest` / `latest_verified` | `UNVERIFIED` / `false` | 属预期：本地代码不访问 Steam，不能宣称是最新稳定版 |

> 进一步只读探测（可选）：`python -m agent.preflight --config "<bridge.conf>" --json`（`agent/preflight.py:1-17`；只做 3 次只读请求、从不打印 token，退出码 0=ready / 1=connected-unready / 2=fatal）。
> 不要让任何一条命令把 token 打印到终端/日志/截图。

### A-5 验收通过标准（本步 `NOT_RUN`）

- [ ] 游戏正常启动，Mods 菜单无红色/警告三角（若出现，记录原文）。
- [ ] 能力确认后能启用并重启，`/health` 返回 `ok:true`，版本/MVID 与记录一致。
- [ ] `bridge.conf` 生成且 `run_mode` 未被误设为 `test`。
- [ ] 未触碰任何存档；未出现 `new_game`/自动开局。
- [ ] 首轮只做只读（不派发写动作）。

### A-6 未来 Workshop Beta 的差别（**未上架，仅草案**）

- 订阅者通过 Steam 订阅拿到 Mod 文件夹；安装步骤变为"订阅 → 模组菜单启用 → 能力确认 → 重启"。
- Mod 更新后文件哈希变化 → 游戏信任状态变为 `Updated`，玩家需重新确认（来源：`docs/workshop-mod-security-report.zh-CN.md:85-90`）。**不要**让玩家手动替换文件来"跳过"重新确认。
- 2026-12-10 起，Workshop Mod 若声明 `files`/`network`，需开发者验证后才能加载（`_decomp/ModSecurityDebug.cs:67-71`）；本地文件夹不受此限（`_decomp/ModTrustList.cs:117-119`）。PawnMind 至少声明了 `network`，**上架前必须确认验证路径**。
- 未订阅/退订由 Steam 客户端处理；**不要**手动删除 `steamapps/workshop/content/4348910/` 下不属于 PawnMind 的目录。
- 上架本身 `NOT_RUN`：本文不包含任何上传命令/素材要求（预览图等未核实）。

---

## 4. 路线 B：外部 MCP 安装器（可选，与游戏目录完全分离）

> 目标：让宿主（如 DSH）通过 **stdio MCP** 连接本机 bridge。MCP server 是一个普通 Python 进程，**不写入游戏目录**。

### B-1 前置

- Python **≥ 3.11**（`agent/pyproject.toml:5`）。
- 游戏已按路线 A 安装并至少成功启动过一次（存在 `bridge.conf`）。
- 宿主支持 stdio MCP（例如 DSH 的 `@deepseek-ai/dsh-mcp-client`；见 `docs/pawnmind-ai-host-setup.zh-CN.md`）。

### B-2 创建独立 venv 并安装依赖

```bash
# 在 PawnMind 仓库目录（或任一独立目录）执行；不要装进系统 Python
cd /path/to/PawnMind            # Windows: cd /d D:\code\game_mods\PawnMind
python -m venv .venv
# Windows:
.venv\Scripts\python.exe -m pip install -U pip "mcp>=1.2.0"
# WSL/Linux:
.venv/bin/python -m pip install -U pip "mcp>=1.2.0"
```

- 依赖仅 `mcp>=1.2.0`（`agent/pyproject.toml:6`）。
- venv 与游戏 Mod 无关：**不要**把 `.venv` 复制进 `Mods/`（会被扫描器当 payload 拒绝）。

### B-3 用 `--probe` / preflight 自检（只读）

```bash
# 端口/token 全部来自 bridge.conf；不手抄 token
# Windows:
.venv\Scripts\python.exe agent\mcp_server.py --config "<...>\ModData\pawnmind\bridge.conf" --probe
# WSL:
.venv/bin/python agent/mcp_server.py --config "/mnt/c/Users/<你>/AppData/LocalLow/Questing Goose Studio/Probably Stolen/ModData/pawnmind/bridge.conf" --probe
```

- `--probe` 打印 `health:`/`state:`/`ping:` 且**不应**出现 token（`docs/pawnmind-ai-host-setup.zh-CN.md:524-542`）。
- 或使用 `python -m agent.preflight --config "<conf>" --json`（退出码语义见 A-4）。
- 失败对照：`no_client`/连接失败 = 游戏没开或 Mod 未启用；`unauthorized` = token/端口不匹配（重新读取 `bridge.conf`，或停游戏后让 Mod 重新生成配置）。

### B-4 宿主 stdio 配置（示例骨架，占位符不得提交）

```yaml
# 仅示例字段；真实值用本机绝对路径/环境变量，不把 token/用户路径写进仓库
- name: pawnmind
  transport: stdio
  command: <PAWNMIND_HOME>/.venv/bin/python      # Windows: <PAWNMIND_HOME>\.venv\Scripts\python.exe
  args:
    - <PAWNMIND_HOME>/agent/mcp_server.py
    - --config
    - <path-to>/ModData/pawnmind/bridge.conf
  env:
    PAWNMIND_HOME: <PAWNMIND_HOME>
    PAWNMIND_BRIDGE_CONF: <path-to>/ModData/pawnmind/bridge.conf
  cwd: <PAWNMIND_HOME>
  toolCallTimeoutMs: 180000
  failOnStartupError: false
```

- 路径/环境变量语义、`failOnStartupError` 的原因（游戏未开时不要让整个宿主 profile 启动失败）见 `docs/pawnmind-ai-host-setup.zh-CN.md:92-154`。
- 工具集：默认 full（114 个 MCP 工具，AGENTS.md）；`--tools recording` 只注册录制用 104 个（用于受控录制，不要给普通用户开）。**具体数量以 `--probe`/宿主工具列表实测为准（`NOT_RUN`）。**
- `PAWNMIND_BRIDGE_CONF` 必须指向真实 `bridge.conf`；不要写死占位符后"假装能连"——MCP 会启动即失败（刻意 fail-loud）。

### B-5 安全约束

- bridge **只监听 `127.0.0.1`**；不要做端口转发、反向代理、公网暴露。
- `bridge.conf` 含 token：**不进仓库、不进截图/录像、不发工单**；日志/错误响应也不得包含 token（`agent/preflight.py:14,77-80`）。
- 宿主以普通用户权限运行；MCP server 不提供任意代码执行/任意文件读写（工具白名单在 `agent/mcp_server.py`；写动作需 `rev`/`action_id` 且受 C# 门禁）。
- 不要让 MCP 进程写游戏目录；不要用 MCP 触发 `new_game` / 写存档（录制策略默认全锁）。
- 卸载：删除宿主配置项 → 删除 venv 即可；游戏与存档不受影响。

---

## 5. 回滚步骤

### 5.1 路线 A 回滚（游戏内 Mod）

```text
1) 完全退出游戏（任务管理器确认无 Probably Stolen.exe）。
2) 备份当前文件夹（可选但推荐）：
   Mods\PawnMind  →  游戏目录之外  PawnMind-rollback-<日期>\
3) 还原：
   a) 若安装前有备份：删除/移走当前 Mods\PawnMind，把旧备份整个复制回来；
   b) 若没有备份（首次安装）：只删除 Mods\PawnMind 这一个文件夹。
4) （可选）处理 Mod 自己的运行数据（不是存档！）：
   把 ModData\pawnmind 整个移走/改名（如 pawnmind.rollback-<日期>），
   不要只删 bridge.conf 之外的用户文件；下次启用会自动重建 bridge.conf（token 轮换）。
5) 启动游戏：模组列表不再有 PawnMind（或回到旧版）；游戏可正常读取原有存档。
6) 端口检查（应连接失败）：curl -s http://127.0.0.1:15528/health
```

**存档安全**：以上步骤不涉及任何 `save_*` 文件；若用户要求"备份存档"，只做只读复制到游戏目录之外，并在文档中登记——不要删除/改名存档。

### 5.2 路线 B 回滚（外部 MCP）

```text
1) 在宿主配置里禁用/删除 pawnmind 的 stdio 条目 → 重启宿主（或让宿主重载配置）。
2) 结束残留的 MCP Python 进程（宿主退出一般会带走子进程；若没有，手动结束，不要 kill 游戏进程）。
3) 删除 .venv（可选；不影响游戏）。
4) 如需轮换 token：完全退出游戏 → 把 ModData\pawnmind\bridge.conf 改名/移走 → 下次启用自动生成新 token → 更新宿主配置。
```

### 5.3 组合回滚顺序

先 B（外部进程）再 A（游戏内 Mod），最后处理 ModData 备份；全程不要触碰存档。

### 5.4 回滚验收（`NOT_RUN`，需实机）

- [ ] 游戏启动正常，Mods 列表状态符合预期（无 PawnMind 或为旧版）。
- [ ] 原有存档可正常读取（只打开确认，不做写操作）。
- [ ] HTTP 端口无监听（`curl` 连接失败）。
- [ ] 宿主工具列表无 `mcp__pawnmind__*`。
- [ ] 所有备份文件都在游戏目录之外，且哈希已记录。

---

## 6. 升级 / 降级规则（不覆盖用户 DLL 的具体做法）

1. **永远不在游戏运行时替换 DLL**（文件被锁定；半覆盖可能导致 manifest 与 DLL 版本不一致）。
2. 升级 = 备份旧文件夹 → 用新 `dist/PawnMind` **整个文件夹**替换 → 核对 `manifest.xml` 与 `PawnMind.dll` 是**同一构建**（SHA256 对登记表）→ 启动 → `/health` 对比版本/MVID。
3. 降级 = 用备份文件夹整体还原；不要只回退 DLL 而留下新 manifest。
4. 不要本地编辑 `manifest.xml`（ID/能力/版本必须来自构建产物；扫描器指纹含 manifest 哈希）。
5. 不要把不同来源的 DLL/manifest 混装；不要从 `_decomp/`、`test/`、`backup/` 里拿 DLL。
6. 升级后能力声明若变化（例如新增 `files`），玩家会看到新的能力确认/验证要求——这是预期，不要引导玩家绕过。

---

## 7. 明确"不做"的事（非破坏性保证）

- ❌ 不覆盖安装：不会向 `Mods/PawnMind/` 直接覆盖文件；全部通过"备份+整体替换"。
- ❌ 不覆盖用户 DLL：不修改/不删除他人 Mod 的 DLL；不写 `Managed/`。
- ❌ 不触碰存档：不读、不写、不复制、不删除 `save_*`；不自动 `new_game`/`load_game`/`save_game`。
- ❌ 不修改 `bridge.conf` 的录制/写门字段去解锁；除 `run_mode=bootstrap` 只读冒烟外不改配置。
- ❌ 不安装/运行游戏自动化脚本；不把 Python/脚本放进 Mods。
- ❌ 不上架、不推送、不修改 Steam Workshop（本批次边界）。
- ❌ 不执行任何需要管理员权限的操作；Firewall 不做入站放行。

---

## 8. 已知限制与 `NOT_RUN`（必须随本文一起看）

| # | 项 | 状态/说明 |
|---|---|---|
| 8.1 | 官方扫描器 `mod-scan` | **`NOT_RUN`**：需要在真实游戏内执行（见矩阵 §10 `NR-01`）；本文"能力确认/拒绝"的预测未实机确认 |
| 8.2 | 预测 `files` 能力未声明 | 矩阵 §11.1（`F-01~F-07`）；**可能是启用阻塞**；本文只记录处理原则，不在本批次改 manifest |
| 8.3 | `reflection-external` retiring | **2026-12-10 起拒绝**（矩阵 C2）；上架前必须整改并移除该能力 |
| 8.4 | dist 与源码/已安装 DLL 不一致 | 矩阵 §9.3（D-04/D-07）：**当前安装不能用来证明仓库代码**；安装前必须重建 dist 并记录 SHA256 |
| 8.5 | 版本号不一致 | `manifest 0.3.0` vs tag `v1.0.1` vs 文档 `0.2.1`（矩阵 §9.2）：上架前统一 |
| 8.6 | `/health` 实机值、MVID、端口 | `NOT_RUN`（无游戏）；A-4 字段是源码契约，不是实测值 |
| 8.7 | Workshop 上传/验证/预览素材 | `NOT_RUN`；**未上架**，本文不构成发布流程 |
| 8.8 | Windows/WSL/宿主组合 | 宿主接入的离线验证见 `docs/pawnmind-ai-host-setup.zh-CN.md` §8；Windows 宿主未验证（该文档 §15） |
| 8.9 | 存档/ModData 精确路径 | 本文只给既定文档路径；实际路径以本机为准，`NOT_RUN` 未逐项核实 |

---

## 9. 未来 Workshop Beta 发布前检查清单（草稿 gate）

- [ ] 在真实 0.46D 上跑 `mod-scan`：硬旗 0、`UNDECLARED` 0（或已按结论更新 manifest）、DEPRECATED 有明确处置。
- [ ] manifest 能力与代码一致（`network` / `reflection-external` / 预测的 `files` 决策完成）。
- [ ] `reflection-external` 移除或确认官方宽限期（2026-12-10 前必须完成整改）。
- [ ] 版本号统一：manifest / git tag / README / 工坊条目一致。
- [ ] clean build → dist 仅 2 文件 → 记录 DLL/manifest SHA256 与 MVID。
- [ ] 安装/回滚演练（干净 Windows 环境；含"旧版备份→升级→回滚"）。
- [ ] 能力提示文案与 README/工坊描述一致（声明 loopback-only、ModData-only 文件用途）。
- [ ] Workshop 验证路径确认（若声明 `files`/`network`）。
- [ ] 确认发布包不含 `agent/`、`tools/`、`.venv/`、`_decomp/`、`test/`。
- [ ] 存档零触碰声明与回滚说明随发布文档提供。
- [ ] 本文草稿去掉"草稿/未上架"标记，换成实测证据与版本号后才能对外。

---

## 10. 路径 / 命令速查

| 项 | 路径/命令 |
|---|---|
| 游戏 Mod 目录 | `<Steam>\steamapps\common\Probably Stolen\Mods\PawnMind\` |
| 安装包 | `PawnMind\dist\PawnMind\{manifest.xml, PawnMind.dll}` |
| 连接配置 | `%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\pawnmind\bridge.conf` |
| 健康检查 | `curl -s http://127.0.0.1:<port>/health` |
| 只读预检 | `python -m agent.preflight --config "<bridge.conf>" --json` |
| MCP 冒烟 | `<venv>\python agent\mcp_server.py --config "<bridge.conf>" --probe` |
| 构建（构建机；本批次未执行） | `cd PawnMind && PAWNMIND_BUILD_ONLY=1 ./build.sh`（只编译不打包）；打包用 `./build.sh` |
| 参考文档 | `README.md` §安装/连接；`docs/plans/NEXT_LIVE_SESSION.md`；`docs/pawnmind-s8-runbook.zh-CN.md` §2-3；`docs/pawnmind-ai-host-setup.zh-CN.md` |

---

> **再次声明：本文是草稿，未上架。** 其中任何"预期行为"都以 `docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md` 的静态证据与后续实机 `mod-scan`/`/health` 结果为准；未实机验证前不得对用户宣称"已验证/兼容/可发布"。
