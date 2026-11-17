# PawnMind S1：离线动作协议与客户端凭证门禁（2026-11-14）

**结论**：S1 Python/C# 两子卡先 Red 后 Green；主代理审查时发现两项额外安全缺陷，子代理再次先写失败测试、修复后由主代理独立重跑全量。可进入 S2；**没有启动游戏、安装 Mod 或访问真实存档**。

| 子卡 | Red/修复 | 主代理复核 |
|---|---|---|
| Python 客户端 | [pawnmind_client.py](<../agent/pawnmind_client.py>) 不再跟随 3xx，也不跨目标端口转发 Bearer；收到桥返回的 token 回显时脱敏错误体。[测试](<../agent/tests/test_client.py>)先证实 8 个跳转组合中的 6 个会漏到第二个活服务；后续 4xx/3xx 回显测试又先失败 2 项。 | 全量 Python `Ran 12 tests, OK`，0 skipped；两个 fake HTTPServer 测 GET/POST 和有效目的地，未请求真实桥。 |
| C# 请求路由 | [ActionGate.cs](<../src/Core/ActionGate.cs>) 必填 ID/rev、绑定完整原始 op/args 的 SHA-256 指纹、冲突拒绝；[ActionRouter.cs](<../src/Bridge/ActionRouter.cs>) 写前/写后刷新 revision，快照失败 fail closed，写后失败返回可缓存的 `outcome_unknown`；[ActionResult.cs](<../src/Bridge/ActionResult.cs>) 要求 raw 顶层 boolean `ok`，原始失败不记成功，返回权威 revision；[PawnMindMod.cs](<../src/PawnMindMod.cs>#L39-L47) 已接主线程刷新回调和 token 脱敏。 | 初次 Red：144 项中 5 项失败；审查补充 Red：184 项中 6 项失败，分别覆盖秘密参数参与指纹、刷新失败写动作拒绝/结果未知、缺 raw `ok`；最终全量纯逻辑 `passed=191 failed=0`。 |
| 游戏适配器契约 | 本阶段没有新增游戏成员，仍使用已安装 Rev13 DLL 的既有契约。 | `./test.sh` 的适配器 162 项 PASS、`adapter contract OK`、无 SKIP；主代理运行 C# + 适配器 + Python 全套，退出成功；`git diff --check` 无空白错误。 |

**审查边界和下阶段要求**：以上只证明协议及本地假服务行为，不能证明 Unity 真机帧率或自动存档。幂等缓存仍是进程内 1024 条，不保证跨重启 exactly-once；`outcome_unknown` 时必须先只读对账、禁止换 ID 重试未知写。主线程快照每写最多刷新两次，S8 与用户测性能。S2 负责版本/构建门禁和严格适配器，S4 负责所有真实写入口的新局/专用槽授权，S5 负责房东禁用选项与实际租金回执；这些都不能被本阶段的 353 项 PASS 冒充完成。
