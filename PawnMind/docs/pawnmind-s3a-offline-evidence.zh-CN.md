# PawnMind S3A：原生新局菜单的离线观察和单步选择

**边界**：只增加 Core 假菜单规则与 Game 适配器；尚未接 HTTP/MCP 路由，不打开游戏、不新建存档、不改 Steam。候选卡来自已安装 Rev13 的当前原生 UI 列表而非全局百科；用户实际屏幕及最新正式版仍待 S8 共同验证。

- [菜单 DTO](<../src/Core/NewGameMenu.cs>) 的公开视图只含当前页可见选项、显示文案、锁定/选中/分页状态，不含原生回调槽位参数；[规则](<../src/Core/NewGameMenuRules.cs>) 实施阶段、锁定、互斥、点数、特质数量、重复 ID 和 readback 校验；[会话](<../src/Core/NewGameMenuSession.cs>) 单次选择回调后重新观察，状态不变不报成功。
- [游戏适配器](<../src/Game/NewGameMenuAdapter.cs>) 在主线程只读取三类已打开菜单及其当页候选；场景/难度语义 ID 映射原生索引、特质映射其 ID。只允许一次原生选项回调；不调用 Continue/Start/Save/Load。新增 **18 项**已安装程序集成员/签名检查于[适配器契约](<../test/PawnMind.AdapterTests/Program.cs>)，总计 369。
- 先写失败测试后跑 `./test.sh` Red（缺新 Core API 编译失败）；主审追补的过期页可见标志与重复特质删除两类安全边界再次 Red：`passed=46 failed=6`；修复后 `./test.sh NewGameMenu` 得 `passed=52 failed=0`。主代理独立复跑 `REQUIRE_GAME=1 ./test.sh`：纯逻辑 **272** 项、脚本 **53** 项、适配器 **369** 项全绿无 SKIP；Python **12** 项 OK；Release 构建 0 警告/错误。

**S3B 必须解决**：当前没有 MCP、分页、Continue/开局、复选框动作。原生特质回调优先匹配 hover，未 hover 时优先匹配 available 卡；同 ID 候选卡与 selected 卡同屏时单次回调无法撤销，S3A 正确返回 `ambiguous_target`。在已安装版 2 页场景可另翻候选页；但未来候选仅 1 页时翻页也无解。S3B 不得宣称“全部合法选择可撤销”除非提出经契约和假 UI 证明的原生单步目标机制（例如显式单步原生 hover 后另一次单步 click，需保留模型可观察两步与 readback），或者明确标为真机待验证缺口。不许直接改内部选择列表/作弊或在一个工具里偷偷多回调。开启新局、写盘、槽授权仍归 S4。首局空白隔离配置中 `firstRun` 锁特质，不要伪造解锁。
