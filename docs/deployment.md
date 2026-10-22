# 独立部署与会话路由

Router 从本仓库独立构建，不引用或打包 Engine。运行时通过 HTTP 调用 Agent Provider 和专用决策模型，通过 Redis 保存会话绑定、限流和幂等状态。

## 请求规则

Router 对客户端暴露标准接口：`POST /v1/chat/completions`（OpenAI Chat Completions）、`POST /v1/responses`（OpenAI Responses）、`POST /v1/messages`（Anthropic Messages）和 `GET /v1/models`。请求体与响应体保持各家官方格式，Router 只读取路由所需字段，只改写请求里的 `model`。接入细节见[下游接入接口](downstream-api.md)。

- `model` 是 Agent ID；填保留值 `auto`（或不填）时，Router 取最后一条用户消息，用专用决策模型从当前授权候选里选；即使只有一个候选也调用。没有可用的用户文本返回 400。
- `model` 为具体 Agent ID 时，验证权限与可用性后直接使用，不调用模型。
- Router 为每个会话生成会话 ID（`X-Session-Id`），为每次请求生成聊天 ID（`X-Chat-Id`，前缀 `chatcmpl-` / `resp_` / `msg_`）。两者随请求头传给下游，同时在响应头返回给客户端；CORS 暴露这两个头和 `X-OpenAgent-Selected-Agent-Id`。客户端自带的 `X-Chat-Id` 会被覆盖。
- 续接已有会话，按顺序识别：请求头 `X-Session-Id`；Responses 的 `previous_response_id`（即 Router 返回过的聊天 ID）；Chat Completions / Messages 在历史里已有 assistant 消息时，按"该用户首条用户消息"的指纹找到会话。
- 纯确认类输入（"ok"、"好的，继续"、"谢谢"等，`TrivialMessage` 的短语表）没有话题，不走意图识别：有会话时按会话续接，没有会话时直接用默认 Agent，不调用决策模型。
- 已有会话从绑定取得 Provider 和 Agent，重新检查权限和可用性，不再调用模型；`model` 指定不同 Agent 返回 409。未知、过期、其他用户或其他租户的会话 ID 返回 404，不隐式创建。

首次请求（客户端不带任何会话信息）：

```json
{"model":"auto","messages":[{"role":"user","content":"查一下这份发票对应的订单"}]}
```

响应头返回 `X-Session-Id`、`X-Chat-Id` 和 `X-OpenAgent-Selected-Agent-Id`。HTTP 和 SSE 均在响应开始前确定绑定。需要显式续接时，下一次请求带上 `X-Session-Id`。

## 错误响应

`/v1/*` 上 Router 自己产生的所有错误（认证、路由、限流、幂等、转发失败、未处理异常）使用 OpenAI 的错误信封，官方 SDK 可直接解析：

```json
{"error":{"message":"Conversation was not found","type":"invalid_request_error","param":null,"code":"conversation_not_found","trace_id":"..."}}
```

`type` 由状态码决定：401 `authentication_error`、403 `permission_error`、429 `rate_limit_error`、5xx `server_error`，其余 `invalid_request_error`；`code` 是 Router 的错误符号（snake_case）。未处理异常的 500 只返回通用文案，细节只写日志。下游自己返回的错误（含其 4xx/5xx 响应体）原样透传。`/v1/messages` 同样使用此信封，Anthropic SDK 能读到状态码和 `error.message`，但不是 Anthropic 原生的 `{"type":"error",...}` 形态。其他路径（`/api/v1/*`）仍为 problem+json。

聊天入口不使用普通查询响应缓存。相同的首条消息、不同请求会创建不同会话；需要重试去重时发送 `Idempotency-Key`。JSON 非流式请求在有效期内重放成功响应及会话、聊天 ID、Agent 响应头；重放前重新检查会话授权。重复的 `"stream": true` 请求返回 409 和原会话信息，不重放 SSE 内容。幂等键与租户、用户、路径和请求摘要关联，修改请求后复用同一键返回 409；进行中的重复请求返回 `Retry-After: 1`。表单请求不提供幂等重放。

Router 不承诺跨故障的业务执行恰好一次。请求已发送或流已开始后，不自动改投其他 Agent。发生响应丢失时优先使用已返回的会话 ID；下游的业务副作用仍应有自己的幂等保护。

## 模型与候选

`RouterSettings:DecisionModel:Endpoint` 是 jev 决策服务的完整 URL，例如 `http://192.168.2.159:7860/v1/decide`；不自动拼接路径。Router 发送一个 `choice` 问题：`state` 为用户消息，`criteria` 的键为候选 Agent ID、值为其描述（无描述则用名称）。

模型只接收最后一条用户消息和过滤后的候选 ID、名称、描述。使用 Router 专用凭据，不传用户的认证令牌。模型需在 `answers.agent` 返回：

```json
{"type":"choice","choice":"finance","confidence":0.95}
```

Router 校验候选成员、完整 JSON、有限置信度及阈值。模型网络错误、超时、限流、非法结果或输入超过配置上限时，走配置的默认 Agent；默认 Agent 必须仍在当前授权可用目录内，否则返回 503。取消请求不触发回退。

OpenAgentEngine 目录仅接受当前租户的 `Published` Agent（共享枚举值 2）。ACL 进一步限制用户访问，Redis 查询失败或 ACL 数据损坏返回 503；不长期缓存 ACL，因此撤销权限在下一次请求生效。未设置 ACL 的已发布 Agent 视为租户内公开。

Router 只内置 `OpenAgentEngine` 这一种 Provider 类型：下游用标准 OpenAI / Anthropic 接口提供 Agent，Agent ID 即 `model`；`GET /v1/models` 返回候选。第三方业务系统按[下游接入接口](downstream-api.md)适配，Router 不增加专用适配器。

同一 Provider 的多个 Engine 实例必须具有相同 Agent 目录和共享会话存储；实例发现按 `chat` 能力与会话哈希选择，不按 Agent 部署位置调度。不同 Agent 部署集合应配置为不同 Provider，并设置各自 `BaseUrl`。所有 Provider 的 Agent ID 必须唯一，新会话目录冲突返回 409。

## 配置

配置支持 ASP.NET Core 环境变量形式，例如 `RouterSettings__DecisionModel__Endpoint`。启用模型选择时，缺失 Endpoint 会使启动校验失败；可在开发或显式路由部署中设置 `RouterSettings__IntentRecognition__Enabled=false`，未显式指定 Agent 时只使用合法默认 Agent。

| 配置 | 默认值 | 作用 |
| --- | --- | --- |
| `ConnectionStrings:Redis` | 无 | 非 Development 环境必填；开发环境可使用进程内存储 |
| `RouterSettings:DecisionModel:Endpoint` | 空 | 完整模型请求 URL |
| `RouterSettings:DecisionModel:ApiKey` | 空 | 专用密钥；配置后通过 `X-API-Key` 请求头发送，本地无鉴权网关可留空 |
| `RouterSettings:DecisionModel:ApiKeyFile` | 空 | 可选 Secret 文件；设置后优先从文件读取 |
| `RouterSettings:DecisionModel:MaxPromptCharacters` | 16000 | 自动选择的 prompt 上限 |
| `RouterSettings:DecisionModel:MaxInputCharacters` | 7500 | 消息、指令与全部候选的估算总长（1 字符按 1 token）；决策模型上限 8192 token 且不截断，超出则不调用模型，直接走默认 Agent |
| `RouterSettings:DecisionModel:MaxCandidates` | 62 | 发送给决策模型的候选上限；目录或输入超限时先以本地 feature-hashing embedding 粗排，再由决策模型精选 |
| `RouterSettings:DecisionModel:MaxDescriptionCharacters` | 2000 | 单条候选描述上限；名称上限 256 字符 |
| `RouterSettings:DecisionModel:MaxResponseBytes` | 16384 | 模型响应体上限 |
| `RouterSettings:IntentRecognition:TimeoutMs` | 5000 | 模型请求预算 |
| `RouterSettings:IntentRecognition:CatalogTimeoutMs` | 3000 | 目录与 ACL 查询预算 |
| `RouterSettings:IntentRecognition:MinimumConfidence` | 0.5 | 接受模型结果的阈值，不代表校准后的正确率 |
| `RouterSettings:IntentRecognition:FallbackAgentId` | default | 模型失败时的受控回退 |
| `RouterSettings:Conversations:StorageTimeoutMs` | 2000 | 会话存储操作预算 |
| `RouterSettings:Conversations:RetentionDays` | 30 | 绑定的固定保留时间，从创建时开始计算 |

原 `IntentRecognition:ProviderId/AgentId` 不再用于选择链路；不需要配置 `intent-router`。Router 不处理执行模型，`model` 之外的请求字段（`temperature`、工具等）原样交给下游。Engine 需接受透传的用户令牌并采用一致的租户解析。

绑定通过 Redis `SET NX` 原子创建，包含版本、用户、Provider、Agent 和到期时间，转发失败也不换绑。过期会话返回 404。Router 不再提供会话列表、删除、压缩等接口，历史由客户端持有（标准协议每次带全量消息，或用 Responses 的 `previous_response_id`），下游数据清理由运维处理。旧版本仅包含 Provider、没有用户与 Agent 的绑定会被拒绝，升级后客户端应创建新会话。

生产 Redis 应持久化、备份并使用适合环境的访问控制。示例开启 AOF、`noeviction` 且不暴露 Redis 宿主机端口；它不是高可用集群模板。Router 可以配置静态实例地址作为服务发现回退，但 Redis 会话状态或授权不可用时不能绕过。

## 使用 Docker Compose

1. 复制 `deploy/.env.example` 到 `deploy/.env`，填写身份服务 Authority、Audience、ClientId，下游的容器可达地址，以及专用决策模型 Endpoint。
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
