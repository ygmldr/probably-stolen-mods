# Security

## 设计前提

PawnMind 是一个**本地**桥接：HTTP 只监听 `127.0.0.1`，除 `/health` 外都需要 Bearer token，
token 由 Mod 首次启动时随机生成并写在 `bridge.conf`。它不联网、不上传状态、不包含遥测。

## 报告问题

请通过 GitHub Security Advisory（仓库 → Security → Report a vulnerability）私下报告，
不要先开公开 issue。涉及以下内容时请务必描述复现步骤与影响：

- token 泄漏（日志、错误响应、异常栈）
- 绕过 `run_mode` / 端点门禁
- 绕过 `rev` 或 `action_id` 幂等
- 在 HTTP 线程上直接访问 Unity 对象（主线程违规）
- 读取隐藏字段（鉴定答案、隐藏身份等）
- 写操作留下半状态

## 不在范围内

- 游戏本身的漏洞（请报告给发行商）
- 需要先拿到用户 token 才能利用的本地提权
- 你自己改过的 `bridge.conf` 导致的配置问题

## 使用建议

- 不要把 `bridge.conf`、日志或截图里的 token 发到公开渠道。
- 录制/自动操作只在你自己的专用存档槽上进行，先备份。
- `run_mode` 保持 `bootstrap` / `play`；`test` 只用于测试档。
