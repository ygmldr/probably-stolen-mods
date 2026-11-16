# PawnMind 测试档

> 用户要求：以后测试直接读档，不要再 `new_game` 造新档。

## 指定测试档

- **存档槽：17**
- 文件：`%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\save_17.es3`
- 约 163 KB，2026-11-10 03:02 创建
- 状态：第 1 天、现金 550、背包 14 件、店态 MORNING、无顾客
- 用途：PawnMind 的所有真机回归

## 怎么读

菜单状态下：

```json
{"op":"act","action":"load_game","args":{"slot":17}}
```

或 MCP：先回主菜单，再 `load_game(17)`。

读档后先 `get_state` 确认 `day=1, cash=550, inventory_count=14`。

## 不要碰

- `save_11.es3` / `save_11_day*.es3`：用户主档（第 135–140 天）
- `save_0/1/3/4/7`：用户旧档
- `save_13.es3`：更早的档，来源未确认

## 其他测试档

`save_14/15/16.es3` 是 2026-11-09 晚间的早期测试档，保留但不再使用；不要删除，除非用户明确同意。

## 约定

- 不再调用 `new_game` 做测试；要新档先问用户。
- 所有测试动作只发生在槽 17。
- 测试结束不主动覆盖用户主档。
