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
