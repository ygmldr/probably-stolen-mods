# NIGHTLY_G_MOD_COMPAT_REVIEW — G 模块（发布/兼容静态预审 + Workshop 安装草稿）

Status: **APPROVE**
（静态审计质量高：逐条可复现、预测与运行结果严格区分、Top3 风险正确登记为发布阻塞而不是“已解决”。
无 Critical。残留 3 个 Low 文档一致性问题；官方 ModScanner / dist 安装仍是 `NOT_RUN`。）
Reviewing commit(s): G 本体 `f789a92f7b347ab605ab3230f08512684629b25e`；
`task-4` 复扫增量（作者 `compat-scribe`）随文档收尾提交 `f7ae62c117995dffb43a56da70ddefd6254ccb5b`
（`docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md`、`docs/plans/WORKSHOP_INSTALL_DRAFT.md`）；**最终 HEAD `f7ae62c`**
（审阅基准：最终提交，工作树除 `docs/reviews/` 外干净）。
Change scope: `docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md`（420→约 470 行）、
`docs/plans/WORKSHOP_INSTALL_DRAFT.md`。**未改 `src/`、`test/`、`agent/`、`dist/`、`manifest.xml`**（git 可证）。

## Real evidence（本 Agent 亲自运行/复核）

```text
# 1) 反射计数（矩阵 §2.1/§12 声称 19 成员查找 + 7 MethodInfo.Invoke + 68 行 typeof）
$ grep -rc "GetField(" --include=*.cs src/ | grep -v ":0"
  NewGameMenuAdapter.cs:7  GameState.cs:2  RecordingSaveAdapter.cs:1  GameVersionAdapter.cs:1   -> 11
$ grep -rc "GetMethod(" --include=*.cs src/ | grep -v ":0"
  NewGameMenuAdapter.cs:6  RecordingSaveAdapter.cs:1                                          -> 7
$ grep -rc "GetProperty(" --include=*.cs src/ | grep -v ":0"
  NewGameMenuAdapter.cs:1                                                                    -> 1
$ grep -rn "\.Invoke(" --include=*.cs src/ | wc -l / 明细
  NewGameMenuAdapter.cs ×6 + RecordingSaveAdapter.cs ×1 = 7；另有 UiSnapshot.cs ×2 为 UnityEvent.Invoke（矩阵 §2.1 R-18 明确不计）  -> 与矩阵一致
$ grep -rn "typeof(" --include=*.cs src/ | wc -l      -> 68（NewGameMenuAdapter 61 / RecordingSaveAdapter 3 / GameVersionAdapter 2 / GameState 2）
$ grep -rl "using System.Reflection" --include=*.cs src/ | grep -v obj  -> 4 文件
# 2) 线程/协程/网络/原生
$ grep -rn "new Thread(|\bTask\.|async |await |ThreadPool|Parallel" --include=*.cs src/  -> 仅注释命中，0 代码命中
$ grep -rn "StartCoroutine" --include=*.cs src/        -> ScreenshotService.cs:65（1 处；自身 MonoBehaviour）
$ grep -rn "TcpListener" --include=*.cs src/           -> BridgeServer.cs:218（构造函数中 IPAddress.Loopback）
$ grep -rc "Socket" --include=*.cs src/Bridge/BridgeServer.cs -> 23
$ grep -rn "DllImport" --include=*.cs src/ | wc -l     -> 0
$ grep -rn "UnityEngine" --include=*.cs src/Core/ | wc -l -> 0
# 3) manifest / dist
$ cat manifest.xml | grep ModVersion                     -> 0.3.0（Capabilities: network, reflection-external）
$ sha256sum manifest.xml dist/PawnMind/manifest.xml      -> 两处均 fc14f3b7e65313c1cd9afef17480b1a4733422c3355cccf2ba2f439314ce124c
$ sha256sum dist/PawnMind/PawnMind.dll; stat -c %s ...   -> ad335987...（296,960 B，mtime 17:58:22Z）
$ find src -path 'src/bin' -prune -o -path 'src/obj' -prune -o -name '*.cs' -newer dist/PawnMind/PawnMind.dll -print | wc -l
  -> 26（与矩阵 §9.3 D-04 / §12 一致）
$ sha256sum src/bin/Release/netstandard2.1/PawnMind.dll  -> d29209a4...（338,432 B）≠ dist -> dist STALE 复核成立
# 4) 反编译规则引用的抽查（只读，_decomp 已 gitignore；仅引用行号/结论）
$ sed -n '1990,1996p' _decomp/ModScanner.cs     -> public const int VERSION = 4; REFUSE_UNSCANNABLE_CAPABILITIES=false  （矩阵一致）
$ sed -n '12,40p'  _decomp/ModCapability.cs     -> All[9] / Retiring[4]（reflection-external/native/dynamic-code/threads）  （矩阵一致）
$ sed -n '52,60p'  _decomp/ModSecurityDebug.cs  -> "refused by the game update of December 10, 2026" + files/network 验证文案  （矩阵一致）
# 5) 自跑全量/编译 gate（用于交叉核对矩阵 C8/NR-06 的“可编译”结论）
$ ./test.sh                                   -> passed=750 failed=0（最终提交；矩阵 §10.1/§12 记录其快照时点 749，见 G-2）
$ .venv/bin/python -B -m unittest discover -s agent/tests -> Ran 365 tests OK（与最终 report/test matrix 一致）
$ $DOTNET build src/PawnMind.csproj -c Release -p:GameManaged=/root/work/game_managed
Build succeeded. 0 Warning(s) 0 Error(s)      -> 与矩阵 §10.1 的编译 gate 结论一致
```

## Invariants checked（逐条 PASS/FAIL/PARTIAL）

| 不变量 | 结论 | 依据 |
|---|---|---|
| 反射/动态代码/原生 DLL/后台线程/HTTP listener/打包 Python 全覆盖扫描，且逐条给路径+行号+替代建议 | **PASS（静态）** | §2（R-01..R-19）、§3–§7、§11；本 Agent 独立复核计数：19 成员查找（11+7+1）、7 MethodInfo.Invoke（+2 UnityEvent 不计）、68 行 typeof、0 线程、1 协程、1 loopback listener、0 DllImport |
| 预测 vs 运行结果严格区分，不用网传口径代替本地证据 | **PASS** | §0.2/§1/§10/§15 反复声明：Scanner/`APIChanges`/安装 = `NOT_RUN`；规则只来自 `_decomp` 本地副本；`NR-01..NR-11` 每条给可复现命令 |
| 官方 ModScanner 未安装/未运行 → 记录 `NOT_RUN` 而不是伪造 PASS | **PASS** | §10 `NR-01`（游戏内 `mod-scan`）、§10.2 `NR-01..NR-11`；无“兼容性 PASS”声明 |
| `manifest` 静态合法性（ID/Assemblies/Capabilities） | **PASS（静态）** | §9.1 M-01..M-08；manifest 实测 `<ID>=pawnmind`、`PawnMind.dll`、`network`+`reflection-external` |
| 发现 `files` capability 缺失（发布预测拒绝）并给出决策路径 | **PASS（高，已登记）** | §11.1 F-01..F-07：`ScreenshotService` `ScreenCapture/System.IO`、`RecordingSaveAdapter` `SaveFiles.IndexLoad`；manifest 只声明 2 能力 → 预测被启用链拒绝；建议“实机 mod-scan 后声明 files 或关特性”，未擅改代码 ✓ |
| `reflection-external` 2026-12-10 retiring 风险量化 | **PASS（高，已登记）** | §2.1 R-06/R-07/R-10/R-11..R-15（14 处 computed-Type）；§11.2 + 三条替代建议（A/B/C）；未声称已解决 |
| `dist` 与源码/已安装 DLL 不一致（验收归因） | **PASS（记录）+ NOT_RUN（安装侧）** | §9.3 D-01..D-08；本 Agent 复核 dist=`ad335987…`/296,960 B、新构建 `d29209a4…`/338,432 B、26 个 `.cs` 更新 → dist STALE；安装目录不可访问 → `NOT_RUN`（未覆盖/未安装）✓ |
| 版本号一致性 | **PARTIAL（文档残留漂移，见 G-1）** | manifest `0.3.0` vs tag `v1.0.1`（真实不一致，已登记）；README/AGENTS 多数已更新，但 `PawnMind/README.md:117` 与根 `README.md:121` 仍写 `manifest 0.2.1` |
| 非破坏性 Workshop 安装说明（区分游戏内 Mod / 外部 MCP；不发布不上架） | **PASS** | `WORKSHOP_INSTALL_DRAFT.md` §1 两路线分离、§2 退出游戏/备份/哈希、§5 回滚、§7“不做”清单、§8 `NOT_RUN`；无上传/安装命令被执行 |
| 不覆盖安装/不碰存档/不推送/不发布（本批次边界） | **PASS** | G 只改 2 个 docs 文件（git 可证）；draft 明示“不覆盖 DLL/不碰 save_*/不上架”；本审阅期间也未执行任何安装 |
| 统计数字可复现、含“共享树在动，合并后需重跑”的 caveat | **PASS** | §0.1/§12/§14/§15；本 Agent 重跑 §14 关键命令，与快照一致（dist 26 个 `.cs`、计数一致）；C# 计数 final tree 为 750（矩阵记录 749，见 G-2） |
| 不把编译 gate 误记为 NOT_RUN（上一版错误已修正） | **PASS** | §10.1 `NR-06`：本机 `PAWNMIND_BUILD_ONLY=1 ./build.sh` → C# 749/0、门禁 53/0、契约 503、`Build succeeded 0/0`、EXIT=0；本 Agent 独立 build 也 succeeded（0/0） |

## Critical findings

无 Critical / High（作为“静态审计 + 草稿”交付物，G 正确地把真正的发布阻塞项登记为风险而不是假装解决）。

### G-1（Low）版本号文档残留漂移未被矩阵完全覆盖

- `PawnMind/README.md:6` 已写 `manifest 0.3.0`，但同一文件 `:117` 仍写 “manifest 0.2.1 vs tag v1.0.1”；
  根 `README.md:121` 也仍写 “manifest.xml 仍为 0.2.1”。而实际 `manifest.xml:6` = `0.3.0`。
- 矩阵 §9.2/C4 的表述是“README/AGENTS 已被其他模块更新”，与现状不完全一致（仍有 2 处旧文）。
- **影响**：低（文档误导，非行为缺陷）；但影响发布前的“版本统一”判断。**修复计划**：收尾时把
  `PawnMind/README.md:117`、根 `README.md:121` 改成 `manifest 0.3.0 vs tag v1.0.1`（或由 Lead 在最终提交统一）。

### G-2（Low/记录）矩阵快照数字与最终提交有 1 处已知漂移（矩阵自带 caveat，仍应回填）

- 矩阵 §10.1/§12 与 `WORKSHOP_INSTALL_DRAFT.md` §8.10 记录 C# `749/0`（其 20:40Z 快照）；最终提交因 D-1
  回归测试变为 **750/0**。矩阵 §0.1/§15 已明确“合并后必须用 §14 重跑”，因此不算虚假；但截至 `f7ae62c`
  该数字未回填（最终 report/test matrix 已写 750/0，G 文档仍是 749/0）。建议 Lead 在最终审阅索引提交里
  顺手把 G 文档两处 `749/0` 改为 `750/0`（纯文档，1 行×2）。

### G-3（Low）`WORKSHOP_INSTALL_DRAFT` 对“无指纹旧桥”行为变更未同步

- A-2 修复后，新 MCP 对**不报 `policy_sha256` 的旧 Mod 构建** fail-closed（`policy_unverifiable`，拒绝写）。
  安装草稿 §8.11 只写工具数/策略 sha，未提示“DLL 与 MCP 必须同版本安装，否则写操作会被客户端拒绝”。
  这不影响静态审计结论，但用户按草稿升级外层 MCP 而没升级 DLL 时会困惑。建议草稿 §2/§8 加一句。

## Potential regressions

1. 若用户直接用当前 `dist/PawnMind.dll` 安装并加载：按矩阵预测会因未声明 `files` 能力被启用链拒绝
   （`UNDECLARED`），且 `reflection-external` 在 2026-12-10 后会被拒绝；这是**发布阻塞**，不是本批次回归，
   但用户若把“静态预测”当“已验证兼容”会踩坑（矩阵/草稿已在多处标注，仍建议最终报告醒目标注）。
2. `dist` STALE → 任何“游戏内现象”不能归因于当前 HEAD；若用户跳过重建直接验收，会把旧 DLL 行为当成
   新代码行为（矩阵 C3/D-04、NEXT_LIVE_SESSION §1 已强调）。
3. 矩阵把 `RecordingSaveAdapter` 的 `SaveFiles.IndexLoad` 预测为 `files`；若用户选择“声明 files”路线，
   2026-12-10 后 Workshop 需开发者验证（矩阵 §1.2 M-06 已记录）——发布计划风险。
4. 静态计数会随共享树继续移动（反射/typeof 计数在 C/D 合并后被复核未新增；但仍需合并后重跑 §14）。

## NOT_RUN / UNKNOWN

- **L3-real-game 全部 NOT_RUN**：官方 `mod-scan`（游戏内）、`ModScanner` 离线 harness、`/health` 实机 MVID、
  `dist` 安装一致性、启用/拒绝/能力提示的真实行为、Workshop 上传/验证。需要：游戏进程/Windows 构建机/
  用户在场授权；本批次未启动游戏、未安装、未上传。
- **NOT_RUN**：`APIChanges.md` / `ModdingGuide.md` 本地不存在（仓库内 0 命中），内容未核实。
- **UNKNOWN**：`_decomp` 规则副本（`ModScanner.VERSION=4`）与 `/root/work/game_managed` 的 MVID 是否同一构建；
  规则随游戏更新会变，实机 `mod-scan` 永远优先。
- **UNKNOWN（记录）**：`dist` DLL 的 IL 级引用/`unlisted-reference`/`native` 二进制证据未检查
  （无 ilspycmd；矩阵 `NR-10` 已登记）。

## Reviewer independence

独立审阅 Agent `nightly-reviewer`（fresh spawn 会话，未参与实现；G 实现者为 teammate `compat-scribe`，
fork 上下文，与本审阅者不同 agent/会话）。本会话未写 G 任何文件；只读矩阵/草稿、重跑 §14 关键命令与独立
grep/`sha256sum`/`find` 复核计数，并抽查 `_decomp` 规则行号引用（只读、不复制代码）。所有数字均来自本会话
亲自运行；G 的 task-4 增量已随 `f7ae62c` 提交，本文件以最终提交为准。与实现者非同一 agent，非 self-review。
