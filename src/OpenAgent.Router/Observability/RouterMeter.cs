using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenAgent.Router;

/// <summary>
/// Router 路由指标仪表盘，通过 OpenTelemetry Meter 暴露 Prometheus 指标。
/// Prometheus 通过 /metrics 端点主动拉取（Pull 模式）。
/// </summary>
internal static class RouterMeter
{
    public const string MeterName = "OpenAgent.Router";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>转发失败总数，按 action/forwarder_error 分组</summary>
    private static readonly Counter<long> ForwardingFailuresTotal = Meter.CreateCounter<long>("openagent_router_forwarding_failures_total");
    private static readonly Counter<long> DiscoveryRefreshTotal = Meter.CreateCounter<long>("openagent_router_discovery_refresh_total");
    private static readonly Counter<long> DiscoverySelectionsTotal = Meter.CreateCounter<long>("openagent_router_discovery_selections_total");
    private static readonly Counter<long> RateLimitDecisionsTotal = Meter.CreateCounter<long>("openagent_router_rate_limit_decisions_total");
    private static readonly Histogram<int> DiscoveryEngineCount = Meter.CreateHistogram<int>("openagent_router_discovery_engine_count");
    private static readonly Counter<long> DownstreamProbesTotal = Meter.CreateCounter<long>("openagent_router_downstream_probes_total");
    private static readonly Counter<long> ProviderSelectionsTotal = Meter.CreateCounter<long>(
        "openagent_router_provider_selections_total");
    private static readonly Counter<long> AclDenialsTotal = Meter.CreateCounter<long>(
        "openagent_router_acl_denials_total");
    private static readonly Counter<long> CacheHitsTotal = Meter.CreateCounter<long>(
        "openagent_router_cache_hits_total");
    private static readonly Counter<long> RequestsTotal = Meter.CreateCounter<long>(
        "openagent_router_requests_total");
    private static readonly Counter<long> ForwardsTotal = Meter.CreateCounter<long>(
        "openagent_router_forwards_total");
    private static readonly Histogram<double> SseDuration = Meter.CreateHistogram<double>(
        "openagent_router_sse_duration_seconds",
        unit: "s");
    private static readonly Histogram<double> DecisionConfidence = Meter.CreateHistogram<double>(
        "openagent_router_decision_confidence");
    private static readonly Histogram<double> DecisionDuration = Meter.CreateHistogram<double>(
        "openagent_router_decision_duration_seconds",
        unit: "s");
    private static readonly Counter<long> AgentSelectionsTotal = Meter.CreateCounter<long>(
        "openagent_router_agent_selections_total");
    private static readonly Counter<long> MisroutesTotal = Meter.CreateCounter<long>(
        "openagent_router_misroutes_total");
    private static readonly Histogram<int> CandidatePreselectionCount = Meter.CreateHistogram<int>(
        "openagent_router_candidate_preselection_count");

    public static void RecordRequest(string? action)
    {
        RequestsTotal.Add(1, new TagList
        {
            { "action", NormalizeAction(action) }
        });
    }

    public static void RecordForward(string? action, bool succeeded)
    {
        ForwardsTotal.Add(1, new TagList
        {
            { "action", NormalizeAction(action) },
            { "outcome", succeeded ? "success" : "failure" }
        });
    }

    public static void RecordSseCompletion(string? action, TimeSpan duration, bool succeeded)
    {
        SseDuration.Record(duration.TotalSeconds, new TagList
        {
            { "action", NormalizeAction(action) },
            { "outcome", succeeded ? "success" : "failure" }
        });
    }

    /// <summary>
    /// 记录一次转发失败
    /// </summary>
    public static void RecordForwardingFailure(string action, string forwarderError)
    {
        ForwardingFailuresTotal.Add(1, new TagList
        {
            { "action", Normalize(action) },
            { "forwarder_error", Normalize(forwarderError) }
        });
    }

    public static void RecordDiscoveryRefresh(string outcome, int engineCount)
    {
        DiscoveryRefreshTotal.Add(1, new TagList { { "outcome", Normalize(outcome) } });
        DiscoveryEngineCount.Record(engineCount, new TagList { { "outcome", Normalize(outcome) } });
    }

    public static void RecordDiscoverySelection(string intent, string source)
    {
        DiscoverySelectionsTotal.Add(1, new TagList
        {
            { "intent", NormalizeIntent(intent) },
            { "source", NormalizeDiscoverySource(source) }
        });
    }

    public static void RecordRateLimitDecision(RateLimitDecision decision)
    {
        RateLimitDecisionsTotal.Add(1, new TagList
        {
            { "outcome", decision.IsAllowed ? "allowed" : "denied" },
            { "source", Normalize(decision.Source) },
            { "degraded", decision.IsDegraded }
        });
    }

    public static void RecordDownstreamProbe(string outcome)
    {
        DownstreamProbesTotal.Add(1, new TagList { { "outcome", Normalize(outcome) } });
    }

    public static void RecordProviderSelection(string source)
    {
        ProviderSelectionsTotal.Add(1, new TagList
        {
            { "source", NormalizeSelectionSource(source) }
        });
    }

    public static void RecordDecision(double? confidence, TimeSpan duration, string outcome)
    {
        var tags = new TagList { { "outcome", Normalize(outcome) } };
        DecisionDuration.Record(duration.TotalSeconds, tags);
        if (confidence is { } value && double.IsFinite(value) && value is >= 0 and <= 1)
        {
            DecisionConfidence.Record(value, tags);
        }
    }

    public static void RecordAgentSelection(string agentId, string source)
    {
        AgentSelectionsTotal.Add(1, new TagList
        {
            { "agent_id", NormalizeAgentId(agentId) },
            { "source", NormalizeSelectionSource(source) }
        });
    }

    public static void RecordMisroute(string selectedAgentId, string expectedAgentId)
    {
        MisroutesTotal.Add(1, new TagList
        {
            { "selected_agent_id", NormalizeAgentId(selectedAgentId) },
            { "expected_agent_id", NormalizeAgentId(expectedAgentId) }
        });
    }

    public static void RecordCandidatePreselection(int originalCount, int selectedCount)
    {
        CandidatePreselectionCount.Record(selectedCount, new TagList
        {
            { "catalog_size", BucketCatalogSize(originalCount) }
        });
    }

    public static void RecordAclDenial() =>
        AclDenialsTotal.Add(1, new TagList { { "reason", "agent_acl" } });

    public static void RecordCacheHit(string cache)
    {
        CacheHitsTotal.Add(1, new TagList
        {
            { "cache", NormalizeCache(cache) }
        });
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().ToLowerInvariant();

    private static string NormalizeAgentId(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized.Length <= 128 ? normalized : normalized[..128];
    }

    private static string BucketCatalogSize(int count) => count switch
    {
        <= 62 => "le_62",
        <= 100 => "le_100",
        <= 500 => "le_500",
        _ => "gt_500"
    };

    private static string NormalizeSelectionSource(string value) => value.Trim().ToLowerInvariant() switch
    {
        "explicit" => "explicit",
        "conversation" => "conversation",
        "intent" => "intent",
        "model" => "model",
        "fallback" => "fallback",
        "unavailable" => "unavailable",
        _ => "other"
    };

    private static string NormalizeDiscoverySource(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dynamic" => "dynamic",
        "static_fallback" => "static_fallback",
        "error" => "error",
        _ => "other"
    };

    private static string NormalizeAction(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => "chat",
        "chat" => "chat",
        "stream" => "stream",
        "sse" => "sse",
        _ => "other"
    };

    private static string NormalizeIntent(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "chat" => "chat",
        "workflow" => "workflow",
        _ => "other"
    };

    private static string NormalizeCache(string value) => value.Trim().ToLowerInvariant() switch
    {
        "idempotency" => "idempotency",
        "query" => "query",
        _ => "other"
    };

}
