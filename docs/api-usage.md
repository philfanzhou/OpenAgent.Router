# Router API 调用指南

Router 地址以下记为 `http://localhost:5001`。生产环境应使用 HTTPS 域名。

## 1. 认证

生产环境使用 Bearer Token：

```http
Authorization: Bearer <access-token>
```

本文 Docker 联调实例运行在 Development 环境，可使用 Basic 账号：

```sh
curl -u admin:admin http://localhost:5001/v1/models
```

决策模型的 `X-API-Key` 是 Router 到 Intern-Decision-4B 的服务端凭据，客户端不得发送或持有它。生产部署通过 `RouterSettings__DecisionModel__ApiKeyFile` 注入。

## 2. 健康检查

```sh
curl http://localhost:5001/health
curl http://localhost:5001/ready
```

- `/health`：进程存活。
- `/ready`：Redis、Provider 等必要依赖可用。
- 正常响应为 HTTP 200，正文为 `Healthy`；部分可降级依赖异常时可能为 `Degraded`。

## 3. 获取 Agent 列表

```sh
curl -u admin:admin http://localhost:5001/v1/models
```

示例响应：

```json
{
  "object": "list",
  "data": [
    {
      "id": "auto",
      "object": "model",
      "display_name": "auto",
      "description": "Router selects the agent from the message"
    },
    {
      "id": "finance",
      "object": "model",
      "display_name": "财务助手",
      "description": "报销、发票、订单退款、账单和对账分析"
    }
  ],
  "has_more": false
}
```

`model=auto` 让 Router 自动选择 Agent；也可以直接指定列表中的 Agent ID。

## 4. OpenAI Chat Completions

### 4.1 自动选择 Agent

```sh
curl -i -u admin:admin \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "auto",
    "messages": [
      {"role": "user", "content": "请帮我核对这张报销发票"}
    ]
  }' \
  http://localhost:5001/v1/chat/completions
```

Router 返回并暴露这些响应头：

| 响应头 | 含义 |
| --- | --- |
| `X-Session-Id` | Router 生成的会话 ID |
| `X-Chat-Id` | 本次请求的聊天 ID |
| `X-OpenAgent-Selected-Agent-Id` | 最终选中的 Agent |
| `X-OpenAgent-Previous-Agent-Id` | 仅显式切换时返回原 Agent |

### 4.2 显式指定 Agent

```sh
curl -u admin:admin \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "finance",
    "messages": [
      {"role": "user", "content": "查询发票"}
    ]
  }' \
  http://localhost:5001/v1/chat/completions
```

显式 Agent 会经过权限和可用性校验，但不会调用决策模型。

### 4.3 续接会话

将首次响应中的 `X-Session-Id` 带入下一次请求：

```sh
curl -u admin:admin \
  -H 'Content-Type: application/json' \
  -H 'X-Session-Id: <session-id>' \
  -d '{
    "model": "auto",
    "messages": [
      {"role": "user", "content": "继续处理"}
    ]
  }' \
  http://localhost:5001/v1/chat/completions
```

已有会话从 Redis 绑定中取得 Agent，不重新调用决策模型。未知、过期或属于其他用户的会话返回 404。

### 4.4 流式 SSE

```sh
curl -N -u admin:admin \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "auto",
    "stream": true,
    "messages": [
      {"role": "user", "content": "分析这张发票"}
    ]
  }' \
  http://localhost:5001/v1/chat/completions
```

Router 不改写下游 SSE 事件，只在转发前完成选路并设置会话响应头。

## 5. OpenAI Responses API

首次请求：

```sh
curl -i -u admin:admin \
  -H 'Content-Type: application/json' \
  -d '{"model":"auto","input":"帮我查询报销规则"}' \
  http://localhost:5001/v1/responses
```

续接时可以使用响应中的聊天 ID：

```sh
curl -u admin:admin \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "auto",
    "input": "继续",
    "previous_response_id": "<resp-id>"
  }' \
  http://localhost:5001/v1/responses
```

也可以统一使用 `X-Session-Id`。

## 6. Anthropic Messages API

```sh
curl -u admin:admin \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "auto",
    "max_tokens": 1024,
    "messages": [
      {"role": "user", "content": "查询入职流程"}
    ]
  }' \
  http://localhost:5001/v1/messages
```

生产客户端仍通过 `Authorization: Bearer` 认证，不使用 Anthropic 默认的 `x-api-key` 认证 Router。

## 7. 会话内切换 Agent 与上下文交接

已有会话发生意图漂移时，同时发送目标 Agent 和交接摘要：

```sh
curl -i -u admin:admin \
  -H 'Content-Type: application/json' \
  -H 'X-Session-Id: <session-id>' \
  -H 'X-OpenAgent-Switch-Agent-Id: hr' \
  -H 'X-OpenAgent-Handoff-Summary: User now needs onboarding and attendance policy support' \
  -d '{
    "model": "auto",
    "messages": [
      {"role": "user", "content": "请继续处理我的入职问题"}
    ]
  }' \
  http://localhost:5001/v1/chat/completions
```

约束：

- 只允许切换调用者自己的有效会话；
- 目标 Agent 必须对调用者可见且可用；
- 目标 Agent 必须不同于当前 Agent；
- 目标 Agent 与交接摘要必须同时提供；
- 交接摘要最多 4000 字符；
- Redis 条件事务保证并发切换只有一个成功；
- 当前请求立即发给新 Agent，后续请求继续命中新 Agent；
- 下游收到可信的 `X-OpenAgent-Previous-Agent-Id` 和 `X-OpenAgent-Handoff-Summary`。

## 8. 上报误路由反馈

```sh
curl -u admin:admin \
  -H 'Content-Type: application/json' \
  -d '{
    "sessionId": "<session-id>",
    "expectedAgentId": "finance",
    "message": "这其实是财务报销问题",
    "reason": "人工确认路由错误"
  }' \
  http://localhost:5001/v1/router/feedback
```

成功返回 HTTP 202：

```json
{"sample_id":"<sample-id>"}
```

Router 会校验会话所有权和期望 Agent。默认只保留消息 SHA-256、选中/期望 Agent、原因和时间，不保存消息原文；样本按租户有界保存在 Redis。

## 9. 指标

```sh
curl http://localhost:5001/metrics
```

主要路由质量指标：

| 指标 | 含义 |
| --- | --- |
| `openagent_router_decision_confidence` | 决策置信度分布 |
| `openagent_router_decision_duration_seconds` | 决策模型延迟 |
| `openagent_router_agent_selections_total` | 最终 Agent 与选择来源分布 |
| `openagent_router_candidate_preselection_count` | embedding 粗筛后的候选数 |
| `openagent_router_agent_switches_total` | 会话内 Agent 切换数 |
| `openagent_router_misroutes_total` | 显式上报的误路由数 |

## 10. 错误响应

`/v1/*` 上 Router 自身错误统一使用 OpenAI 错误信封：

```json
{
  "error": {
    "message": "Conversation was not found",
    "type": "invalid_request_error",
    "param": null,
    "code": "conversation_not_found",
    "trace_id": "..."
  }
}
```

常见状态码：

| 状态码 | 场景 |
| --- | --- |
| 400 | 请求体、租户、切换参数或自动选路文本无效 |
| 401 | 未认证 |
| 404 | Agent 或会话不存在、不可见 |
| 409 | 会话 Agent 冲突、并发切换冲突、幂等键冲突 |
| 429 | 限流 |
| 503 | Redis、Provider 或目录不可用 |

## 11. Docker 生产部署

复制环境变量示例并准备仓库外的密钥文件：

```sh
cp deploy/.env.example deploy/.env
# 创建仓库外密钥文件，并确保容器内 uid 1654 具有只读权限
docker compose --env-file deploy/.env -f deploy/compose.yaml up --build -d
docker compose --env-file deploy/.env -f deploy/compose.yaml ps
```

生产环境必须配置：

- JWT `AUTHORITY`、`AUDIENCE`、`CLIENT_ID`；
- 可从 Router 容器访问的 `ENGINE_URL`；
- 完整决策地址 `DECISION_MODEL_ENDPOINT`；
- 仓库外密钥文件 `DECISION_MODEL_API_KEY_FILE`；
- 持久化且受保护的 Redis。

不要把决策模型 API Key 写入镜像、Git、客户端配置或普通应用日志。
