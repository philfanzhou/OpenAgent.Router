# AGENTS.md

OpenAgent.Router 是从 OpenAgent 主仓库拆出的独立路由服务（.NET 10, ASP.NET Core）：入口认证、限流、会话亲和、新会话选 agent、HTTP/SSE 转发。

## 结构

```text
src/OpenAgent.Contracts/   共享 DTO 与配置契约（从主仓库快照拷贝，变更需与 Engine 对齐）
src/OpenAgent.Router/      服务本体；Hosting/ 为从 Engine 拷入的认证、错误信封、遥测子集
tests/OpenAgent.Router.Tests/
docs/router.md             配置与行为说明
```

- Router 不引用 Engine 项目，只通过 HTTP 与 Engine/第三方 Provider 通信。
- Router 无状态：会话亲和、限流、幂等、实例注册都在 Redis。
- 已有会话只查亲和表，不做再次选择；新会话才走选择器（后续接入 jev，失败回落默认 agent）。
- `Contracts` 与 `Hosting/` 是拷贝，与 OpenAgent 主仓库的对应代码需人工保持契约一致。

## 命令

- 构建：`dotnet build OpenAgent.Router.sln`
- 测试：`dotnet test OpenAgent.Router.sln`
- 包版本统一在 `Directory.Packages.props`。
