# PawnMind S2A：指定程序集与严格适配器契约

**结论**：仅对本机已安装的 `VERSION 100-Rev13` 程序集验证通过，**不证明拍摄时 Steam 正式稳定分支最新版**；尚未安装/运行游戏或访问存档。S2B 版本拒写仍未实施。

- [test.sh](<../PawnMind/test.sh>) 接受 `GAME_MANAGED` / `GAME_ASSEMBLY`；`REQUIRE_GAME=1` 缺 DLL 非 0 退出，默认缺 DLL 保留 SKIP 兼容；[build.sh](<../PawnMind/build.sh>) 转发 GameManaged、提供 `PAWNMIND_BUILD_ONLY=1`，真实 `dist/` 删除/打包前检查绝对 ROOT、项目标记和非链接的真实 dist 路径。[项目](<../PawnMind/src/PawnMind.csproj>) 消除写死的引用路径（含可配置默认目录）。
- [适配器契约](<../PawnMind/test/PawnMind.AdapterTests/Program.cs>) 增加方法返回/参数/静态性、字段/属性类型和版本常量检查，已安装程序集上共 **351 项**；涵盖 Rev13 的开局菜单、槽身份和首租相关成员。输出 SHA256 `725cb999d019dff1ff31bed353c7f9bb8d81a7605a15de1f80f5a3a6cf7b1d05`、`VERSION 100-Rev13`、`latest: UNVERIFIED`；若新版未满足这些静态契约必须重新调查后修复，不可直接放宽断言。
- [脚本负例测试](<../PawnMind/test/ContractGate.Tests/run.sh>) 先 Red `passed=13 failed=24`；删除路径附加测试再 Red `passed=42 failed=10`，生产最小修复后 Green `passed=53 failed=0`；临时测试仅针对自行创建且核验的唯一目录删除，构建打包正/负例仅在受控临时副本使用假 dotnet 执行。
- **主代理独立复核**：`REQUIRE_GAME=1 ./test.sh` 退出 0，纯 C# 191 项、脚本 53 项、适配器 351 项且 `adapter contract OK`；Python 12 项 OK；`REQUIRE_GAME=1 GAME_ASSEMBLY=/tmp/pawnmind-definitely-missing-managed/Assembly-CSharp.dll ./test.sh` 预期退出 **2**，报告 `FAIL adapter tests`、无适配器 SKIP；`git diff --check` 无错误。真实 `build.sh` 的打包段未执行。

**已知限制**：原有部分 162 项契约仍是成员存在性检查；新增成员签名比对严格，版本升级需按当时 DLL 复验。纯逻辑测试项目的既有 Newtonsoft 引用路径仍未随 GAME_MANAGED 切换，未来迁移时需补齐。临时清理与 dist 检查降低误删，但文件系统并发 TOCTOU 不能靠 shell 路径检查彻底消除；S7 若需运行真实打包，先再次核验精确目标。
