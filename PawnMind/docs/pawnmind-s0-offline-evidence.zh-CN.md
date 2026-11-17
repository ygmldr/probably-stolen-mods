# PawnMind S0：离线现状与待真机证据（2026-11-14）

**性质**：S0 子代理只读调查及主代理复核摘要。没有启动游戏、打开菜单、调用真实 `/action`，没有读写/复制/删除存档，没有修改 Steam 安装。仅凭本地 public 分支缓存，**不能证明拍摄当天仍是 Steam 最新版**。反编译静态结论不等于游戏运行验收。

| 主题 | 已离线核实 | 同版本静态机制 / 当前代码风险 | S8 与用户共同验证 |
|---|---|---|---|
| 三重版本身份 | Steam 本地缓存 public 分支 buildid `26525552`，appmanifest 无 BetaKey，open_beta 的 buildid 不同；安装的 `Assembly-CSharp.dll` SHA256 `725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05`、4,820,224 字节；外显 `0.46D`、内部 ``PlayerStore.VERSION``（本地反编译参考）=`VERSION 100-Rev13`。 | 反编译文件与安装程序集同代；[旧限制文档](<pawnmind-known-limitations.zh-CN.md>)的 100-REV6 记载已过时。Manifest 仍仅写 [0.46D](<../manifest.xml>#L18-L20) 且版本匹配只是 UI 警告。 | 联网确认正式分支**当时**最新 build、菜单显示的真实版本、Mod 加载指纹；不把本地缓存当最新。 |
| AI 开局 | [现有动作](<../src/Bridge/GameActions.cs>#L88-L105)硬编码学徒、跳过开头，无 AI 选择接口；[菜单快照](<../src/Bridge/StateSnapshot.cs>#L31-L46)无选项。 | Rev13 UI 顺序为场景→特质→难度→开始；原生卡片用 `WildNetworkSlot`，有分页、锁定与首局弹窗；48 个定义特质和 4 档难度是本版静态事实，不是模型目前可见项。 | 核对真实选项及锁定、分页、场景选择、难度/特质实际进入新局。 |
| 隔离环境特质 | 用户现有配置已游玩；不代表全新隔离配置继承进度。 | ``NewGamePerksUI``（本地反编译参考）在 `totalRunCount==0` 时清空选择、禁点特质；完全空白隔离环境首局无法满足“AI 选特质”。 | 先确认最终运行环境；若锁定，停下与用户讨论安全使用已玩配置或另行授权原生准备，不改游戏解锁数据。 |
| 新局存档 | `NextFreeSlotId`（本地反编译参考）从 `slotIdGenerator` 起跳过已有档，更新索引。 | 新局 `InitialSave` 会先自动分配并写盘；空白隔离配置首档静态推断为槽 0，**不是固定槽 18**。`load_game` 缺槽可能按 `-1` 绕成新局。 | 核对 OS 用户、LocalLow、云同步边界、预期槽号；用户批准一次新局原生写盘前禁止启动。 |
| 首周付租 | [桥接租期/应缴字段](<../src/Game/GameState.cs>#L46-L69)已存在。 | 房东触发看 `GetRentDayCounter()`；应缴看 `rentValue+temporaryRent`。当前[对话选择](<../src/Bridge/GameActions.cs>#L62-L76)可能把禁用的付租选项误报成功。跨日会自动保存。 | UI 房东选项 enabled、现金扣款、租期重置、已付计数和读档后状态逐一核验。 |
| 协议/版本门禁 | [ActionRouter](<../src/Bridge/ActionRouter.cs>#L71-L104)允许缺 `action_id/rev`；[客户端](<../agent/pawnmind_client.py>#L50-L76)代理禁用了但重定向未禁；[测试脚本](<../test.sh>#L12-L18) DLL 不在时会 SKIP。 | 同 ID 不同参数、旧 rev、阻塞发送、无严格版本拒写均可能越权或卡顿；S1–S4 优先加离线失败测试。 | 严格适配器 0 skip、最新版指纹、主线程帧率及断线恢复等留用户到场确认。 |

**交接决定**：S0 只读出关，允许进入 S1–S7 离线生产代码/TDD；需用户共同检查的所有 S8 项仍未出关。S0 子代理在 `/tmp` 临时产生过 8 个反编译文本（违反仓库仅允许根 `_decomp/` 的放置约定），结束前逐个核实路径并清理；后续任务禁止重演。
