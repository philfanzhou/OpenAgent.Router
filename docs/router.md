# Router 服务发现、限流与就绪

独立 Docker 部署与新会话选 Agent 的需求和已确认方案，见[需求与设计](router-deployment-and-agent-selection.md)；实际配置、请求协议与部署步骤见[部署文档](deployment.md)。

Router 使用 Redis Engine 注册索引发现动态实例，并以静态 `RouterSettings:Routing` 作为明确的回退路径。实现位于 `src/OpenAgent.Router/`；Engine 注册协议见主仓库 OpenAgent 的 `docs/modules/engine/runtime/service-registration/`。

## 服务发现

- Engine 维护 `engine:registry:index` Set，并以带 TTL 的 `engine:registry:{engineId}` String 保存注册值。
- Router 通过 `SMEMBERS` 读取索引，再用一次 `MGET` 批量读取注册值，不执行 `KEYS`/`SCAN` 全库枚举。
- 注册值按 EngineId 排序；候选实例按 intent 过滤，再按负载、EngineId 确定性排序。
- 会话亲和使用 tenantId 与 conversationId 的组合键，候选集合变化时使用一致性哈希重新映射。
- TTL 已过期、心跳超时、JSON 无效或索引悬空的实例会被排除；悬空索引成员会尽力清理。

## 故障与告警策略

| 场景 | 行为 | 可观测性 |
|------|------|----------|
| Redis 不可用 | 默认清空动态快照并走静态路由；可配置短时使用 last-known 快照 | Warning 日志、`discovery_refresh_total{outcome="redis_error"}` |
| 注册过期/无匹配 intent | 排除实例并走静态路由；无静态路由时返回不可用 | Warning 日志、发现选择指标 |
| 下游转发失败 | 将目标隔离一段时间，后续选择下一动态实例或静态目标 | Warning 日志、转发失败与选择指标 |
| 下游 ready 失败 | ready 探测下一候选；回退可用时返回 Degraded，否则 Unhealthy | 探测计数与结构化日志 |

## 限流降级

固定 fail-open 无法在 Redis 故障时维持安全边界，因此默认 `FailureMode=Local`：每个 Router 进程使用并发安全的本地令牌桶继续限流。也可显式选择 `FailOpen`（可用性优先）或 `FailClosed`（安全边界优先）。所有拒绝响应均为 429，并携带秒级 `Retry-After`。

指标 `openagent_router_rate_limit_decisions_total` 按 outcome、source 与 degraded 标签区分 Redis、local、fail-open 和 fail-closed 路径。

## Provider 请求日志

Router 会在 `Information` 级别记录 Provider 的请求方法、目标 URI、请求头，以及下游响应状态码和响应头；YARP 转发会记录经过 Router 和 Provider 映射后的最终请求头。选路日志记录来源、Provider 与最终 Agent；独立决策模型客户端不记录 prompt、密钥或模型响应体。

请求/响应体只在 `Debug` 级别记录，并限制长度。`Authorization`、`Cookie`、`Set-Cookie`、API Key、Token、Secret、Password 等敏感 Header 的值会脱敏为 `[REDACTED]`。

排查 Provider 请求时，可仅将 Router 调整为 Debug（此设置不启用独立决策模型的请求体日志）：

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Override": {
        "OpenAgent.Router": "Debug"
      }
    }
  }
}
```

对应环境变量为 `Serilog__MinimumLevel__Override__OpenAgent.Router=Debug`。

## 关键配置

| 配置 | 默认值 | 说明 |
|------|--------|------|
| `RouterSettings:RateLimiting:FailureMode` | `Local` | Redis 限流失败时的降级模式 |
| `RouterSettings:ServiceDiscovery:RedisFailureMode` | `StaticOnly` | `StaticOnly` 或 `LastKnown` |
| `RouterSettings:ServiceDiscovery:SnapshotMaxAgeSeconds` | `15` | last-known 快照最长可用时间 |
| `RouterSettings:ServiceDiscovery:FailureQuarantineSeconds` | `30` | 下游失败后的隔离时间 |
| `RouterSettings:ServiceDiscovery:ReadinessPath` | `/ready` | Engine 下游就绪端点 |
| `RouterSettings:ServiceDiscovery:ReadinessTimeoutMs` | `2000` | 单次下游探测超时 |
| `RouterSettings:Registration:SharedSecret` | 空 | 下游 HTTP 注册的共享密钥；为空时不暴露注册端点 |

## HTTP 注册

下游可调用 `PUT /api/v1/router/registrations/{instanceId}` 注册并周期续约，`DELETE` 同路径下线。请求头 `X-Registration-Key` 必须等于 `SharedSecret`，否则 401；没有 Redis 时返回 503，成功返回 204。注册数据写入原有 `engine:registry:*` 键，因此与直接写 Redis 的 Engine 共用同一个池，两种方式可并存。

```json
{ "baseUrl": "http://engine-7f3a:5208", "ttlSeconds": 30, "load": 0, "intents": ["chat"] }
```

`baseUrl` 只接受不带路径的 `http` 地址；`ttlSeconds` 取 5 至 300。共享密钥是整个集群一把，泄露后需同时轮换 Router 与所有下游。

## 验证

Router 测试不依赖真实 Redis；限流失败/降级路径使用 mock，Engine readiness 使用进程内 TCP 测试下游验证。
