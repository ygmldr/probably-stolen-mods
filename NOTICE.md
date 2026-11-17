# NOTICE

## 与游戏的关系

本项目是《深空当铺：可能是偷的》(*Probably Stolen*) 的非官方 Mod，与 Questing Goose Studio
没有隶属关系，也未获得其背书。游戏本体、游戏资产、商标与相关内容的版权归其权利人所有。

本仓库**不包含**：

- 游戏程序集（`Assembly-CSharp.dll` 等）、Unity 引擎库或任何游戏资源；
- 任何第三方 Mod 的源码或反编译产物；
- 游戏存档、玩家数据、token 或凭据。

## 关于分析文档

`PawnMind/docs/` 下的分析文档记录的是为**互操作**目的观察到的游戏行为，用于说明某个 Mod
接口为什么必须走某个原生入口。这些文档只引用类型名、方法名和结论；不包含可直接编译的
游戏代码，也不用于重新分发游戏内容。

## 第三方

- `PawnMind/agent/` 依赖 [MCP Python SDK](https://github.com/modelcontextprotocol/python-sdk)（MIT）。
- 构建与测试引用游戏自带的 `Newtonsoft.Json.dll`，仅在本地使用，不随仓库分发。

## 商标

"Probably Stolen" 及相关名称属于其各自权利人。本项目名称仅用于说明兼容性。
