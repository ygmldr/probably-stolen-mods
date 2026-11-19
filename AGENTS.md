# AGENTS.md — game_mods workspace

This workspace contains two mods for **Probably Stolen 0.46D**:

| 目录 | Mod | 测试 |
|---|---|---|
| `CompactSort/` | 紧凑整理（背包/货架重排） | `cd CompactSort && ./test.sh` |
| `PawnMind/` | AI Bridge（HTTP + MCP） | `cd PawnMind && ./test.sh` |

先说结论：**任何生产代码改动都必须 TDD**。纯文档/注释/日志文案例外。

## 0. 铁律：Red → Green → Refactor

1. **Red**：先写失败测试，运行对应 `./test.sh`，确认失败原因符合预期。
2. **Green**：只写让测试通过的最小实现。
3. **Refactor**：全绿后整理；每一步重跑。
4. **Full suite**：提交/打包前跑完整测试，不能只跑单个用例。

禁止：

- 先改生产代码再补测试；
- 为过测试放宽/删除断言；
- 只测 happy path；
- 用“游戏本身的问题”解释 bug 而不留回归测试；
- 未测试的适配器改动直接进 `dist/`。

## 1. 项目地图

### CompactSort

- `CompactSort/src/Packing/`：纯算法，不依赖 Unity
- `CompactSort/test/CompactSort.Tests/`：纯逻辑测试
- `CompactSort/build.sh`：先跑测试再打包
- 详细规则见 [`CompactSort/AGENTS.md`](CompactSort/AGENTS.md)

### PawnMind

- `PawnMind/src/Core/`：纯逻辑，**禁止** `using UnityEngine` 或游戏类型
- `PawnMind/src/Bridge/`：HTTP、鉴权、revision、快照、动作路由
- `PawnMind/src/Game/`：唯一允许反射/直连游戏类型的层
- `PawnMind/test/PawnMind.Tests/`：纯逻辑测试
- `PawnMind/test/PawnMind.AdapterTests/`：对 `Assembly-CSharp.dll` 的契约测试（DLL 缺失时 SKIP）
- `PawnMind/agent/mcp_server.py`：stdio MCP，117 个工具（`--tools recording` 104 个；`--tools pilot` 75 个 = 55 个纯读 + 20 个 Day-1 写。R5.3 起 `buy_wholesale`/`buy_upgrade`/`set_label` 不再对 AI 暴露）
- `PawnMind/agent/pawnmind_client.py`：HTTP 客户端 + 长轮询
- `PawnMind/agent/orchestrator.py`：observe→decide→act 基线，只有 `RulePolicy`
- 详细规则见 [`PawnMind/AGENTS.md`](PawnMind/AGENTS.md)

## 2. 测试命令

```bash
cd CompactSort && ./test.sh
cd PawnMind && ./test.sh
cd PawnMind && ./test.sh Bailout   # 只跑名字匹配的用例
```

PawnMind Python 测试：

```bash
cd PawnMind
.venv/bin/python -m unittest discover -s agent/tests
```

构建：

```bash
cd CompactSort && ./build.sh
cd PawnMind && ./build.sh
```

## 3. PawnMind 架构约束

1. **主线程**：所有游戏对象访问必须排进 Unity 主线程（`MainThreadPump`）。HTTP 线程只入队。
2. **分层**：`Core` 纯逻辑；`Game` 适配；`Bridge` 编排。不要把游戏类型泄漏进 `Core`。
3. **契约先行**：新增游戏字段/方法，先在 `test/PawnMind.AdapterTests/Program.cs` 加契约检查。
4. **不读隐藏字段**：`AppraisalHelper.GetCorrectBaseValue`、顾客隐藏身份、水质真实组成等一律不接给 AI。
5. **不改钱/不造物**：不调用 `ModHelper.AddCredit`，不碰 ModLoader/信任/Steam 写接口。
6. **不执行任意代码**：MCP/HTTP 只暴露白名单动作。
7. **幂等**：写动作必须带 `action_id`；`rev` 不匹配一律拒绝。

## 4. 安全不变量

- 只监听 `127.0.0.1`；除 `/health` 外都要 Bearer token。
- 日志/错误响应不得包含 token。
- 写操作失败不得留下半状态；能回滚的回滚。
- 不读用户存档；普通回归禁止 `new_game`。录制流程只允许用户批准的专用槽（`recording_slot`，且必须等于游戏当时的下一个自动分配槽），全部经 S4 策略，默认全锁。历史开发测试的槽 17 约定不再用于录制。
- 禁止触碰：`save_11*`、`save_0/1/3/4/7`、`save_13` 等用户存档。
- 发布包不要带 `allow_test_items=true`；debug 注入仅测试档使用。

## 5. 反编译 / 逆向产物

游戏程序集、第三方 Mod、任何二进制目标的反编译/反汇编/dump/解包产物，一律放在根目录 `_decomp/`（已 gitignore）：

- 不提交、不随 `dist/` 或工坊包分发；
- 只用于本地互操作、排查、自用分析；
- 长期结论写进 `CompactSort/analysis/` 或 `PawnMind/docs/`；
- 引用只写类型名/方法名/结论，不复制大段反编译代码；
- 第三方反编译源码**不得进入仓库**：`sia_decompiled` 已从历史移除，本地放在 `_decomp/sia/`（已 gitignore）；
  `CompactSort/analysis/competitor_bench/` 通过 `$(SiaSourceRoot)` 引用它，缺源码时该项目不参与构建。

## 6. 文档与审查

- 仓库总览：[`README.md`](README.md)
- PawnMind 能力矩阵：[`PawnMind/docs/pawnmind-capability-matrix.zh-CN.md`](PawnMind/docs/pawnmind-capability-matrix.zh-CN.md)
- 已知限制：[`PawnMind/docs/pawnmind-known-limitations.zh-CN.md`](PawnMind/docs/pawnmind-known-limitations.zh-CN.md)
- 迭代问题：[`PawnMind/docs/pawnmind-iteration-issues.md`](PawnMind/docs/pawnmind-iteration-issues.md)

审查时优先验证：

1. README 的“已接/未接”是否与代码一致；
2. `src/Bridge/` 的 rev/token/主线程/回滚；
3. `src/Game/` 是否读取隐藏字段；
4. MCP 工具参数、错误码、成功路径是否有测试；
5. 失败/边界/回滚是否覆盖，而不只是 happy path；
6. 文档里的“真机验证”是否有可复现步骤。

## 7. 当前已知坑

- PawnMind `manifest.xml` 是 0.3.0，git tag 是 v1.0.1（发布前需统一；见 `PawnMind/docs/audit/NIGHTLY_MOD_COMPAT_MATRIX.md`）。
- 远征（Expedition）是 debug-only/未上线内容；正常 Perk 不启用。
- 突袭（RaidScene）正常流程不可达，属于独立飞船/站点战斗脚手架。
- 需求/通缉/Barter 只验了错误路径，成功路径缺样本。
- WildNetworkUI 未接，用 `list_upgrades`/`buy_upgrade` 替代。
- 陈酿只验了入槽，1 天品质未变化。
- 标签条件 ID 是本地化显示串（如“纯水”）。
- 彩票兑奖成功路径未验。
- 没有视觉鉴定、没有 LLM agent loop。
