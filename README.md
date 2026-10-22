# OpenAgent.Router

独立部署的 .NET 10 路由服务，负责认证、限流、会话亲和、基于消息的 Agent 选择，并以标准 OpenAI（Chat Completions / Responses）和 Anthropic（Messages）接口转发 HTTP/SSE。

会话 ID 与聊天 ID 由 Router 生成并传给下游；`model` 指定 Agent 时跳过模型，否则由专用决策模型从授权候选中选择。后续请求保持原 Agent，未知会话 ID 返回 404。共享状态保存在 Redis。

- [部署、配置与请求协议](docs/deployment.md)
- [API 调用指南](docs/api-usage.md)
- [需求、架构分析与已确认决策](docs/router-deployment-and-agent-selection.md)
- [服务发现、限流与运维](docs/router.md)

```sh
dotnet build OpenAgent.Router.sln
dotnet test OpenAgent.Router.sln
```

Docker 部署样例位于 `deploy/compose.yaml`，依赖独立运行的 Engine、模型网关及身份服务。使用前按部署文档配置地址和凭据。

## 本地联调

1. 启动 mock 下游：`dotnet run tools/mock-business-api.cs`（`http://localhost:5290`）。
2. 启动 Router：`dotnet run --project src/OpenAgent.Router --launch-profile mock-business`（`http://localhost:5001`，Development，Basic 账号 `admin/admin`，决策模型指向 `RouterSettings__DecisionModel__Endpoint`）。
3. Postman 导入 `tools/postman/OpenAgent.Router.postman_collection.json`，按文件夹顺序运行；命令行：`npx newman run tools/postman/OpenAgent.Router.postman_collection.json`。
