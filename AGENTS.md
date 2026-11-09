# AGENTS.md

This workspace contains the **Compact Inventory Sort** mod for *Probably Stolen*.

All code changes in this workspace must follow the TDD rules in
[`CompactSort/AGENTS.md`](CompactSort/AGENTS.md). The short version:

1. Write a failing test first.
2. Run it and confirm it fails for the expected reason.
3. Make the smallest production change that passes.
4. Run the full suite before building or packaging anything.

The test command is:

```bash
cd CompactSort
./test.sh
```

## 反编译 / 逆向产物

游戏程序集、第三方 Mod 以及任何二进制目标的反编译、反汇编、dump、解包产物，一律放在仓库根目录的 `_decomp/` 下（该目录已在 `.gitignore` 中）：

- 不提交到 git，不随 `dist/` 或工坊包分发；
- 只用于本地互操作、排查和自用分析；
- 需要长期保留的结论写进 `CompactSort/analysis/` 或 `docs/`，引用时只写类型名、方法名和结论，不要复制大段原始反编译代码；
- `CompactSort/analysis/sia_decompiled/` 是历史遗留的已跟踪内容，新增产物不要再放进 `analysis/`。
