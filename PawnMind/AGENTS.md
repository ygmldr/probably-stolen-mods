# AGENTS.md — PawnMind

## 铁律：TDD

任何生产代码变更（`src/`、`manifest.xml` 行为、构建/安装脚本逻辑）都必须 Red → Green → Refactor：

1. 先写失败测试，运行 `./test.sh`，确认失败原因符合预期；
2. 只写让测试通过的最小实现；
3. 全绿后再整理；提交前跑完整 `./test.sh`。

禁止：先改生产代码再补测试；为过测试放宽/删除断言；只测 happy path。

纯文档、注释、日志文案可以例外。

## 测试

```bash
./test.sh                 # 纯逻辑 + 适配器契约
./test.sh Bailout         # 只跑名字匹配的用例
```

- `test/PawnMind.Tests/`：不依赖 Unity/游戏进程，直接编译 `src/Core/`。
- `test/PawnMind.AdapterTests/`：引用 `Assembly-CSharp.dll`，断言 bridge 依赖的类型/成员仍存在；游戏 DLL 缺失时 SKIP。

## 分层规则

- `src/Core/` 不得 `using UnityEngine` 或引用游戏类型。
- 所有游戏对象访问必须在 Unity 主线程；HTTP 线程只能入队（`MainThreadPump`）。
- `src/Game/` 是唯一允许反射/直连游戏类型的层；新增字段先加适配器契约测试。
- 不读隐藏字段：`AppraisalHelper.GetCorrectBaseValue`、顾客隐藏身份、水质组成等一律不接给 AI。
- 不调用 `ModHelper.AddCredit`，不碰 ModLoader/信任/Steam 写接口。

## 构建与安装

```bash
./build.sh
# 产物：dist/PawnMind/{manifest.xml,PawnMind.dll}
# 安装：...\Probably Stolen\Mods\PawnMind\
```

游戏运行时 DLL 被锁定；先退出游戏再覆盖。

## 安全不变量

- 只监听 `127.0.0.1`；除 `/health` 外都要 Bearer token
- revision 不匹配的动作一律拒绝
- 写操作失败不得留下半状态
- 日志与错误响应不得包含 token

## 当前版本与架构（2026-11-11）

- Git tag：`v1.0.1`；`manifest.xml` 仍是 0.2.1（发版前同步）。
- 测试：以 `./test.sh` 与 `agent/tests` 实际输出为准（2026-11-15：C# 627 + 脚本门禁 53 + 适配器契约 453、Python 143，均 0 skip）。
- 114 个 MCP 工具（`--tools recording` 只注册首周录制用 104 个）；入口 `agent/mcp_server.py`，HTTP 客户端 `agent/pawnmind_client.py`。
- `agent/orchestrator.py` 只有确定性 `RulePolicy`，还不是 LLM agent。

```
src/Core/   纯逻辑，禁止 UnityEngine
src/Bridge/ HTTP + 鉴权 + rev + 快照 + 动作
src/Game/   唯一允许反射/直连游戏类型；主线程
test/PawnMind.Tests/        纯逻辑
test/PawnMind.AdapterTests/ 对 Assembly-CSharp.dll 的契约
agent/mcp_server.py         stdio MCP
```

## MCP / Agent 层约定

- MCP 工具必须**单步**：做一件事，回读状态；不要在一个工具里偷偷做多步。
- 所有写动作要 `rev`；HTTP 层同时支持 `action_id` 幂等。
- 错误码要稳定、可判断：`stale_rev`、`no_client`、`cannot_report`、`not_fillable` 等。
- 不把隐藏字段（鉴定答案、隐藏身份、真实水质）暴露给 MCP。
- 不提供任意代码执行、任意文件读写、`AddCredit`、生成物品。
- 新增 MCP 工具时，先在 `agent/tests/` 加工具注册/参数测试；C# 侧若新增游戏成员，先补适配器契约。
- `new_game` 在录制模式下恒被拒绝；受控开局只能走 `confirm_new_game`（S3B 菜单 + S4 专用槽授权，默认全锁）。普通离线回归禁止创建任何存档；历史开发测试的槽 17 约定不再用于录制。

## 审查入口

1. 先读仓库根 `README.md` 和本文件。
2. 对照 `docs/pawnmind-capability-matrix.zh-CN.md` 检查每个“真机/代码/未接”的声明。
3. 重点看 `src/Bridge/`（rev/token/主线程/回滚）和 `src/Game/`（隐藏字段/反射边界）。
4. 跑 `./test.sh` 和 `.venv/bin/python -m unittest discover -s agent/tests`。
5. 不要动用户存档；录制只允许已绑定的专用槽（`recording_slot`），默认全锁。

## 已知“不是 bug 但别误判”

- 远征（Expedition）是 debug-only/未上线内容；正常 Perk 不启用，`debug_unlock_expedition` 只测试。
- 突袭（RaidScene）是独立飞船/站点战斗脚手架，正常开店流程不可达。
- 需求/通缉/Barter 只验了错误路径，成功路径缺样本。
- WildNetworkUI 未接，用 `list_upgrades`/`buy_upgrade` 替代。
- 陈酿只验了入槽；1 天品质未变化，需多日验证。
- 标签条件 ID 是本地化显示串（如“纯水”）。
- 彩票兑奖成功路径未验（测试档未解锁/无中奖号）。
- 没有视觉鉴定，没有 LLM agent loop。
