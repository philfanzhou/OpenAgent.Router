# OpenAgent.Router

独立部署的 .NET 10 路由服务，负责认证、限流、会话亲和、基于 prompt 的 Agent 选择和 HTTP/SSE 转发。

新会话由 Router 生成 ID；显式指定 Agent 跳过模型，否则由专用决策模型从授权候选中选择。后续请求保持原 Agent，未知会话 ID 返回 404。共享状态保存在 Redis。

- [部署、配置与请求协议](docs/deployment.md)
- [需求、架构分析与已确认决策](docs/router-deployment-and-agent-selection.md)
- [服务发现、限流与运维](docs/router.md)

```sh
dotnet build OpenAgent.Router.sln
dotnet test OpenAgent.Router.sln
```

Docker 部署样例位于 `deploy/compose.yaml`，依赖独立运行的 Engine、模型网关及身份服务。使用前按部署文档配置地址和凭据。
