using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Router.Observability;
using StackExchange.Redis;

namespace OpenAgent.Router;

internal sealed class RouterReadyCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly IRouteTable _routeTable;
    private readonly IEngineReadinessProbe _probe;
    private readonly IEndpointHealthTracker _healthTracker;
    private readonly ILogger<RouterReadyCheck> _logger;
    private readonly IAgentProviderRegistry? _providers;
    private readonly IHostEnvironment? _environment;
    private readonly DecisionModelStatus? _decisionModel;

    public RouterReadyCheck(
        IRouteTable routeTable,
        IEngineReadinessProbe probe,
        IEndpointHealthTracker healthTracker,
        ILogger<RouterReadyCheck> logger,
        IConnectionMultiplexer? redis = null,
        IAgentProviderRegistry? providers = null,
        IHostEnvironment? environment = null,
        DecisionModelStatus? decisionModel = null)
    {
        _redis = redis;
        _routeTable = routeTable;
        _probe = probe;
        _healthTracker = healthTracker;
        _logger = logger;
        _providers = providers;
        _environment = environment;
        _decisionModel = decisionModel;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, object> checks = [];
        bool redisDegraded = await CheckRedisAsync(checks, cancellationToken)
            .ConfigureAwait(false);
        if (redisDegraded && _environment is { } environment && !environment.IsDevelopment())
            return HealthCheckResult.Unhealthy("Router conversation storage is unavailable", data: checks);
        if (_providers != null)
        {
            bool anyReady = false;
            foreach (IAgentProvider provider in _providers.Providers.Where(provider => provider.SupportsRouterConversationIds))
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var target = await provider.ResolveForwardingAsync(null, null, null, cancellationToken)
                        .ConfigureAwait(false);
                    if (target == null) break;
                    if (await _probe.IsReadyAsync(target.DestinationPrefix, cancellationToken).ConfigureAwait(false))
                    {
                        _healthTracker.ReportSuccess(target.DestinationPrefix);
                        anyReady = true;
                        checks[provider.Id] = "Ready";
                        break;
                    }
                    _healthTracker.ReportFailure(target.DestinationPrefix);
                    checks[provider.Id] = "Unavailable";
                }
            }
            return !anyReady ? HealthCheckResult.Unhealthy("Router has no serviceable downstream", data: checks)
                : redisDegraded || checks.Values.Contains("Unavailable") || _decisionModel?.IsDegraded == true
                    ? HealthCheckResult.Degraded("Router is serving through a degraded dependency path", data: checks)
                    : HealthCheckResult.Healthy("Router is ready", data: checks);
        }
        string? endpoint = _routeTable.GetTargetEndpoint("chat");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            checks["engine"] = "No Engine endpoint is available";
            return HealthCheckResult.Unhealthy("Router has no serviceable downstream", data: checks);
        }

        if (await _probe.IsReadyAsync(endpoint, cancellationToken).ConfigureAwait(false))
        {
            _healthTracker.ReportSuccess(endpoint);
            checks["engine"] = $"Engine is ready: {endpoint}";
            return redisDegraded
                ? HealthCheckResult.Degraded("Router is serving through a degraded discovery path", data: checks)
                : HealthCheckResult.Healthy("Router is ready", data: checks);
        }

        _healthTracker.ReportFailure(endpoint);
        RouterLog.DownstreamQuarantined(_logger, endpoint);
        string? fallbackEndpoint = _routeTable.GetTargetEndpoint("chat");
        if (!string.IsNullOrWhiteSpace(fallbackEndpoint) &&
            !string.Equals(endpoint, fallbackEndpoint, StringComparison.OrdinalIgnoreCase) &&
            await _probe.IsReadyAsync(fallbackEndpoint, cancellationToken).ConfigureAwait(false))
        {
            _healthTracker.ReportSuccess(fallbackEndpoint);
            RouterLog.ReadinessFallbackSelected(_logger, endpoint, fallbackEndpoint);
            checks["engine"] = $"Fallback Engine is ready: {fallbackEndpoint}";
            return HealthCheckResult.Degraded(
                "Router is serving through a fallback downstream", data: checks);
        }

        checks["engine"] = $"Engine is unreachable: {endpoint}";
        return HealthCheckResult.Unhealthy("Router downstream is not serviceable", data: checks);
    }

    private async Task<bool> CheckRedisAsync(
        IDictionary<string, object> checks,
        CancellationToken cancellationToken)
    {
        if (_redis == null)
        {
            checks["redis"] = "Redis is not configured; static discovery is active";
            return false;
        }

        try
        {
            await _redis.GetDatabase().PingAsync()
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            checks["redis"] = "Redis connection is healthy";
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RouterLog.RedisPingFailedDuringReadinessCheck(_logger, ex);
            checks["redis"] = "Redis connection is degraded";
            return true;
        }
    }
}
