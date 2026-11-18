# OVERNIGHT_LOG — R5 跨夜无人值守执行日志

> 规则（BRIEF §3）：每阶段结束追加一条；只记**实际运行**的命令、退出码、关键数字、结论、下一步。
> 时间一律 UTC。证据等级：`VERIFIED`（本机实际运行）/ `REPORTED`（他人报告）/ `NOT_RUN` / `BLOCKED`。
> 分支：`r5/overnight`；不 push。原始测试日志保存在 `_internal/logs/`（gitignored）。

---

## [2026-11-18T19:16Z] 阶段 0 — 启动基线与环境核验

**命令与退出码**

| # | 命令 | 退出码 | 结果 |
|---|---|---|---|
| 1 | `git rev-parse HEAD` / `git status --porcelain` | 0 | `a043449a45f2e015ea4373b4f4ad7abfc128655b`；工作树干净 |
| 2 | `DOTNET=/root/.dotnet/dotnet … REQUIRE_GAME=1 ./test.sh` | **0** | **C# passed=794 failed=0；gate passed=53 failed=0；contract checks=506 OK**（6s） |
| 3 | `.venv/bin/python -m unittest discover -s agent/tests` | **0** | **Ran 377 tests，OK**（44.8s） |
| 4 | `.venv/bin/python tools/gen_policy_catalog.py --check` | **0** | `PolicyCatalog.g.cs matches tool_policy.json (sha256 e0da5a24…)` |

**关键数字**

- HEAD `a043449a`（parent `211309dd` = HANDOFF 期望基线 + 1 doc commit，一致）。
- policy：schema 2，sha256 `e0da5a24a0c21014db57ee842413ef4d6d10ac0e1dca3545dea417490f22d1cc`，tools=116，`play_allowlist=[]`。
- 环境：Linux CT101，dotnet 9.0.321，Python 3.12.3；无游戏进程；`Assembly-CSharp.dll` sha256 `725cb999…`。
- 证据缺口：E1 成交 32 vs 约 45（CONFLICT）、E2 原始轨迹缺失、E3 `_internal/week.py` 本机不存在、E6 真机证据不可复核。

**结论**：启动基线与 BRIEF §3.5 完全一致（794/0、53/0、506、377/OK），无漂移，可以开始 R5-A。
产出 `docs/reviews/R5_BASELINE.md`。

**下一步**：R5-A1/A2（run-change prompt 语义、sleep verifier）先写失败测试；A3 交易单笔证据；A4 validator/Skill/benchmark 漂移。

---
