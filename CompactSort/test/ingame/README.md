# 游戏内自检（第一版，只读）

目标：把“进游戏看一眼”变成可重复的 `PASS/FAIL` 日志。当前版本**不移动物品**，只检查真实 Unity 场景里的容器绑定，因此可以安全地在测试存档里跑。

## 能自动检查什么

- `EmporiumEntry` 已加载；
- `global_pool_disabled` / `per_container_disabled` 为 PASS；
- 展示柜、垃圾桶、暗格、前台/后台柜台/内部柜台字段存在；
- 除暗格外，上述对象都被 `IsAutoSortBlocked` 判定为手动区域；暗格必须不被判定为手动区域；
- `CollectAutoContainers` 不包含展示柜/垃圾桶/柜台；暗格有物品时应包含；
- 收集到的每个容器：形状存在、物品数 > 0、没有同格重叠、物品都在容器边界内。

## 不能自动检查什么

- 真正执行“紧凑整理/全局池/拖拽”后的提交、撤销、回滚；
- 拖拽 UI 的实际手感；
- 跨存档的长期状态。

这些需要下一版“测试存档 + 执行/回滚”自检；当前先做只读部分。

## 跑法

1. 启动游戏，进入店内（有柜台、展示柜、垃圾桶的场景），并确保仓库/背包里至少有几件物品；
2. 按 **F8**，或者运行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File test/ingame/press_f8.ps1
   ```

3. 看日志：

   ```
   [CompactSort][TEST] PASS case=blocked_showcase ...
   [CompactSort][TEST] PASS case=not_collected_front_counter ...
   [CompactSort][TEST] PASS case=container_0_no_overlap ...
   [CompactSort][TEST] SUMMARY pass=... fail=0
   ```

4. 也可以用解析器从 WSL 读日志：

   ```bash
   python3 test/ingame/check_selftest.py
   ```

   成功返回 0，失败返回 1，找不到 `SUMMARY` 返回 2。

## 自动快照

每次游戏内睡眠/新一天开始，模组会自动写一份只读 JSON 到：

```text
%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\CompactSortSnapshots\
```

积攒几次睡眠后，可以直接跑真实数据 benchmark：

```bash
analysis/global_bench/run.sh   --snapshot "C:/Users/<user>/AppData/LocalLow/Questing Goose Studio/Probably Stolen/CompactSortSnapshots"
```

## 已知限制

- `press_f8.ps1` 只负责激活窗口和发 F8；**加载存档仍需游戏内操作**；
- 自检要求已经在店内；主菜单按 F8 会得到 `store_loaded FAIL`；
- 如果某个 `field_*` 为 null，可能只是当前场景还没初始化；先确认已经进店。
