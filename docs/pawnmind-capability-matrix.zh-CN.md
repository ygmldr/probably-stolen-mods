# PawnMind 能力矩阵（P6–P10，2026-11-10）

> 真机统一槽 17；C# 141 项 + 适配器契约 + Python 8 项全绿。
> “真机”= 在游戏内实际调用并回读；“代码”= 已实现但只过了单测/错误路径。

## P6 通用交互 / 分析仪 / 标签 / 标记笔

| 能力 | 工具 | 状态 |
|---|---|---|
| 通用 activate/toggle/target | `list_interactions` / `activate_item` / `toggle_item` / `use_tool` | 真机（储藏区 toggle、标签机 cannot_target） |
| 水质分析仪 | `list_analyzers` / `analyze_item` | 真机（瓶装水 tested 文本） |
| 植物分析仪 | 同上 | 真机（营养果种子完整字段） |
| 化扫 | 同上 | 代码，未真机样本 |
| 基因扫描仪 | 同上 | 真机 toggle 状态 |
| 标签打印 | `list_labels` / `set_label` | 真机（瓶装水 沟渠水→纯水） |
| 鱼类标记笔 | `set_pen_mode` / `list_marks` | 真机（罗非鱼绿色） |

## P7 电话 / 彩票

| 能力 | 工具 | 状态 |
|---|---|---|
| 联系人/拨号/选项/挂断 | `list_contacts` / `call` / `phone_state` / `phone_advance` / `phone_choose` / `end_call` | 真机（Wild） |
| 供应商 Introduction | 同上 | 真机（8815，免费样品、Cooldown 4） |
| 供应商订货 | 同上 | 真机（8 份啤酒，次日送达） |
| Wild 网络 UI | — | 仅 `list_upgrades`/`buy_upgrade` 替代 |
| 彩票只读/兑奖 | `lottery_status` / `list_tickets` / `inspect_ticket` / `redeem_ticket` | 真机（未解锁、无中奖号，cannot_redeem） |
| 自动刮卡 | — | 明确不做 |

## P8 机器 / 酿酒

| 能力 | 工具 | 状态 |
|---|---|---|
| 机器枚举/槽位/状态 | `list_machines` / `machine_status` | 真机 |
| 槽位放入/取出 | `machine_insert` / `machine_collect` | 真机 |
| 熔炉 full cycle | 电池+废金属→金属锭 | 真机 |
| 水培 full cycle | 水+种子→7 果 | 真机 |
| 酿酒发酵 | `wine_add_water` / `wine_add_ingredient` / `start_fermentation` / `wine_status` | 真机（荧光莓果酿） |
| 陈酿架 | — | 未接 |

## P9 经济 / 社交 / 风险

| 能力 | 工具 | 状态 |
|---|---|---|
| 声望/派系 | `list_factions` | 真机（5 派系） |
| 健康 | `health_status` | 真机 |
| 需求 | `list_requests` / `deliver_request` | 快照真机；交付错误路径 |
| 通缉 | `wanted_status` / `arrest_wanted` | 快照空；错误路径 |
| Barter | `barter_status` / `barter_accept` | 快照空；错误路径 |
| 夜间服务/保险 | `list_services` / `toggle_service` | 真机（保险 on；锁定拒绝） |
| 安检/突袭 | — | 未接 |
| 机器模块维修 | 通过 `machine_insert` 可放模块 | 未真机 |

## P10 世界 / 农场 / 远征

| 能力 | 工具 | 状态 |
|---|---|---|
| 远征快照 | `expedition_status` | 真机 |
| 远征解锁/派遣/推进 | `dispatch_expedition` / `advance_expedition` | 真机到第一次推进；第二次 POI NRE，已安全收尾 |
| 养殖 | `husbandry_status` | 真机（老鼠 health 81.6） |
| 水培 | `machine_grow_status` | 真机 |
| 营销招牌 | `list_signs` | 真机（2 块） |
| 事件 | `events_status` | 真机（2 个活跃） |
| 保险 | `list_services` / `toggle_service` | 真机 |
| 天气 | — | 未接 |

## 明确不做 / 已知限制

- 视觉鉴定、自动刮卡。
- WildNetworkUI 本体。
- 远征 POI NRE 根因。
- 标签条件 ID 是本地化显示串。
- 彩票需第 14 天后/中奖号才能验兑奖。
