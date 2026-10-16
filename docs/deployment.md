# 独立部署与会话路由

Router 从本仓库独立构建，不引用或打包 Engine。运行时通过 HTTP 调用 Agent Provider 和专用决策模型，通过 Redis 保存会话绑定、限流和幂等状态。

## 请求规则

- 不带会话 ID：Router 生成 UUID，选定 Agent 后先持久化绑定，再转发业务请求。
- 显式 `context.agentId` / `X-Agent-Id`：验证权限与可用性后直接使用，不调用模型。
- 未显式指定 Agent：用 `message` 与当前授权候选调用专用模型；即使只有一个候选也调用。空白 `message` 返回 400。
- 带会话 ID：从绑定取得原 Provider、Agent 和用户，重新检查权限和可用性；不再调用模型。指定不同 Agent 返回 409。
- 未知、过期、删除、其他用户或其他租户的会话 ID 返回 404，不探测 Provider，不隐式创建。
- 请求体和请求头中空白 ID 视为未提供；多个有效来源冲突返回 400。转发时把最终 ID 和 Agent 同时规范化到请求体和请求头，保留 `fileIds`、执行模型 profile 和其他上下文。

首次请求：

```json
{"message":"查一下这份发票对应的订单"}
```

响应通过 `X-Conversation-Id` 返回会话 ID，通过 `X-OpenAgent-Selected-Agent-Id` 返回 Agent；CORS 暴露这两个头。HTTP 和 SSE 均在响应开始前确定绑定。客户端后续请求携带 `context.conversationId` 或 `X-Conversation-Id`：

```json
{"message":"再按月份汇总","context":{"conversationId":"上次返回的会话 ID"}}
```

聊天入口不使用普通查询响应缓存。相同 prompt、不同请求会创建不同会话；需要重试去重时发送 `Idempotency-Key`。JSON 非流式请求在有效期内重放成功响应及会话、Agent 响应头；重放前重新检查会话授权。重复流请求返回 409 和原会话信息，不重放 SSE 内容。幂等键与租户、用户、路径和请求摘要关联，修改请求后复用同一键返回 409；进行中的重复请求返回 `Retry-After: 1`。表单请求不提供幂等重放。

Router 不承诺跨故障的业务执行恰好一次。请求已发送或流已开始后，不自动改投其他 Agent。发生响应丢失时优先使用已返回的会话 ID；下游的业务副作用仍应有自己的幂等保护。

## 模型与候选

`RouterSettings:DecisionModel:Endpoint` 是 jev 决策服务的完整 URL，例如 `http://192.168.2.159:7860/v1/decide`；不自动拼接路径。Router 发送一个 `choice` 问题：`state` 为用户消息，`criteria` 的键为候选 Agent ID、值为其描述（无描述则用名称）。

模型只接收 `message` 和过滤后的候选 ID、名称、描述。使用 Router 专用凭据，不传用户的认证令牌或执行模型 `llmProfileId`。模型需在 `answers.agent` 返回：

```json
{"type":"choice","choice":"finance","confidence":0.95}
```

Router 校验候选成员、完整 JSON、有限置信度及阈值。模型网络错误、超时、限流、非法结果或输入超过配置上限时，走配置的默认 Agent；默认 Agent 必须仍在当前授权可用目录内，否则返回 503。取消请求不触发回退。

OpenAgentEngine 目录仅接受当前租户的 `Published` Agent（共享枚举值 2）。ACL 进一步限制用户访问，Redis 查询失败或 ACL 数据损坏返回 503；不长期缓存 ACL，因此撤销权限在下一次请求生效。未设置 ACL 的已发布 Agent 视为租户内公开。

Router 只内置 `OpenAgentEngine` 这一种 Provider 协议。第三方业务系统作为下游时，按该协议提供 Agent 目录（`AgentListPath`，返回 `AgentSummary` 数组）、会话查询（`ConversationPath`）与聊天（`ChatPath`）接口，由下游适配，不在 Router 中增加专用适配器。Router 签发会话 ID 并通过 `X-Conversation-Id` 和请求体传给下游。

同一 Provider 的多个 Engine 实例必须具有相同 Agent 目录和共享会话存储；实例发现按 `chat` 能力与会话哈希选择，不按 Agent 部署位置调度。不同 Agent 部署集合应配置为不同 Provider，并设置各自 `BaseUrl`。所有 Provider 的 Agent ID 必须唯一，新会话目录冲突返回 409。

## 配置

配置支持 ASP.NET Core 环境变量形式，例如 `RouterSettings__DecisionModel__Endpoint`。启用模型选择时，缺失 Endpoint 会使启动校验失败；可在开发或显式路由部署中设置 `RouterSettings__IntentRecognition__Enabled=false`，未显式指定 Agent 时只使用合法默认 Agent。

| 配置 | 默认值 | 作用 |
| --- | --- | --- |
| `ConnectionStrings:Redis` | 无 | 非 Development 环境必填；开发环境可使用进程内存储 |
| `RouterSettings:DecisionModel:Endpoint` | 空 | 完整模型请求 URL |
| `RouterSettings:DecisionModel:ApiKey` | 空 | 专用密钥；本地无鉴权网关可留空 |
| `RouterSettings:DecisionModel:ApiKeyFile` | 空 | 可选 Secret 文件；设置后优先从文件读取 |
| `RouterSettings:DecisionModel:MaxPromptCharacters` | 16000 | 自动选择的 prompt 上限 |
| `RouterSettings:DecisionModel:MaxCandidates` | 100 | 候选总数上限，不静默截断 |
| `RouterSettings:DecisionModel:MaxDescriptionCharacters` | 2000 | 单条候选描述上限；名称上限 256 字符 |
| `RouterSettings:DecisionModel:MaxResponseBytes` | 16384 | 模型响应体上限 |
| `RouterSettings:IntentRecognition:TimeoutMs` | 5000 | 模型请求预算 |
| `RouterSettings:IntentRecognition:CatalogTimeoutMs` | 3000 | 目录与 ACL 查询预算 |
| `RouterSettings:IntentRecognition:MinimumConfidence` | 0.5 | 接受模型结果的阈值，不代表校准后的正确率 |
| `RouterSettings:IntentRecognition:FallbackAgentId` | default | 模型失败时的受控回退 |
| `RouterSettings:Conversations:StorageTimeoutMs` | 2000 | 会话存储操作预算 |
| `RouterSettings:Conversations:RetentionDays` | 30 | 绑定的固定保留时间，从创建时开始计算 |

原 `IntentRecognition:ProviderId/AgentId` 不再用于选择链路；不需要配置 `intent-router`。客户端 `llmProfileId` 原样保留给任务执行，不决定 Router 使用哪个模型。

当前 Engine 的任务执行仍要求 LLM profile。可在对应 Provider 的 Settings 设置 `DefaultLlmProfileId`（Compose 中为 `ENGINE_LLM_PROFILE_ID`），Router 会通过 `X-OpenAgent-Llm-Profile-Id` 提供默认执行模型；客户端显式传入的执行 profile 优先。没有配置默认值时，客户端仍需携带执行 profile。上面的仅 `message` 示例以已配置服务端默认值为前提；此配置与 Router 的决策模型相互独立。Engine 还需接受透传的用户令牌并采用一致的租户解析。

绑定通过 Redis `SET NX` 原子创建，包含版本、用户、Provider、Agent 和到期时间，转发失败也不换绑。过期会话返回 404。会话删除先保存墓碑，再转发 DELETE；即使下游删除失败，该 ID 也不能再次进入 chat 创建对话。清理下游残留数据由运维处理。旧版本仅包含 Provider、没有用户与 Agent 的绑定会被拒绝，升级后客户端应创建新会话。

生产 Redis 应持久化、备份并使用适合环境的访问控制。示例开启 AOF、`noeviction` 且不暴露 Redis 宿主机端口；它不是高可用集群模板。Router 可以配置静态实例地址作为服务发现回退，但 Redis 会话状态或授权不可用时不能绕过。

## 使用 Docker Compose

1. 复制 `deploy/.env.example` 到 `deploy/.env`，填写身份服务 Authority、Audience、ClientId，Engine 的容器可达地址、默认执行 profile，以及专用决策模型 Endpoint。
2. 将专用模型密钥写入仓库之外的文件，设置 `DECISION_MODEL_API_KEY_FILE` 指向该文件；文件必须允许容器的非 root 用户读取。无鉴权网关可使用空文件。
3. 从仓库根目录执行：

```sh
docker compose --env-file deploy/.env -f deploy/compose.yaml up --build -d
```

示例只有 Router 和 Redis。Engine、模型网关与 JWT 身份服务独立运行；示例中的 `engine` 主机名需要按实际环境替换，不能直接使用宿主机开发服务的 `localhost`。容器服务名仅在共享 Docker 网络中可解析。

Router 监听 5001，输出日志到标准输出，以非 root 用户运行。只读密钥挂载不进入镜像。反向代理接入 SSE 时需要关闭响应缓冲，并设置与最长会话流时长匹配的超时；停机宽限期可按实际流时长调整。

镜像也可独立构建：

```sh
docker build -t openagent-router:local .
```

CI 在 PR 中构建、测试并验证生产镜像的非 root 用户、存活与就绪探针。合并到 `main` 后发布 `ghcr.io/philfanzhou/openagent.router:sha-<完整提交 SHA>`；部署可使用该不可变版本标签。拉取现有镜像时设置 `ROUTER_IMAGE`，执行 `docker compose ... pull router` 后用 `up -d --no-build`。

## 健康检查与验证

- `/health`（也有 `/health/live`）：进程存活，Docker 镜像内置此检查。
- `/ready`（也有 `/health/ready`）：检查 Redis 与配置中支持会话协议的 Provider。生产 Redis 不可用返回 503；至少一个 Provider 可服务时可接流量，部分 Provider 失败或最近模型调用失败标记 Degraded。
- 模型健康来自真实调用的最近结果，不在健康探针中进行付费推理；恢复成功后清除降级。模型故障不会使已有会话重新选择 Agent。
- `openagent_router_provider_selections_total{source="model|fallback|explicit|conversation|unavailable"}` 区分路由来源；选路日志不记录 prompt、模型密钥或完整模型响应。

常规验证：

```sh
dotnet build OpenAgent.Router.sln
dotnet test OpenAgent.Router.sln
```

真实 Redis 测试在 CI 默认运行；本地准备专用测试 Redis 后可运行：

```sh
ROUTER_TEST_REDIS=localhost:6379 dotnet test OpenAgent.Router.sln
```

测试包括并发绑定竞争、租户隔离、Router 重启后续聊与模型不重选。未设置该变量时显式跳过这些测试。Linux 有 Docker 和 Redis 时可执行 `python3 tests/container_smoke.py openagent-router:local` 验证生产镜像。
