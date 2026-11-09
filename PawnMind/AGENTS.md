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
