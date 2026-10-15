using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Models;
using StackExchange.Redis;

namespace OpenAgent.Router.Security;

internal sealed class AgentVisibilityService : IAgentVisibilityService
{
    private readonly ILogger<AgentVisibilityService> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly AgentConfigAccessor _configAccessor;

    public AgentVisibilityService(
        ILogger<AgentVisibilityService> logger,
        IConnectionMultiplexer? redis = null)
    {
        _logger = logger;
        _redis = redis;
        _configAccessor = new AgentConfigAccessor(redis, logger);
    }

    public async Task<bool> IsAgentVisibleToUserAsync(string agentId, IAgentUserContext userContext, CancellationToken cancellationToken = default)
    {
        var acl = await GetAclEntryAsync(agentId, cancellationToken);

        if (acl == null)
        {
            return true;
        }

        bool allowed = IsAllowedForUser(acl, userContext);
        if (!allowed)
        {
            RouterMeter.RecordAclDenial();
        }

        return allowed;
    }

    public async Task<List<string>> GetPublishedAgentIdsAsync(CancellationToken cancellationToken = default)
    {
        return await _configAccessor.GetPublishedAgentIdsAsync(cancellationToken);
    }

    public async Task<string?> GetAgentConfigAsync(string agentId, CancellationToken cancellationToken = default)
    {
        return await _configAccessor.GetAgentConfigAsync(agentId, cancellationToken);
    }

    private async Task<AgentAclEntry?> GetAclEntryAsync(string agentId, CancellationToken cancellationToken)
    {
        string? aclJson = null;

        if (_redis != null)
        {
            try
            {
                var db = _redis.GetDatabase();
                var value = await db.StringGetAsync($"agent:acl:{agentId}")
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                aclJson = value.HasValue ? value.ToString() : null;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                RouterLog.ReadAclEntryFailed(_logger, ex, agentId);
                throw new AgentRoutingException(
                    StatusCodes.Status503ServiceUnavailable,
                    RouterErrorCodes.AgentProviderUnavailable,
                    "Agent authorization is unavailable");
            }
        }

        if (aclJson != null)
        {
            try
            {
                return JsonSerializer.Deserialize<AgentAclEntry>(aclJson)
                    ?? throw new JsonException("Invalid Agent ACL");
            }
            catch (JsonException)
            {
                throw new AgentRoutingException(StatusCodes.Status503ServiceUnavailable,
                    RouterErrorCodes.AgentProviderUnavailable, "Agent authorization is unavailable");
            }
        }

        return null;
    }

    private static bool IsAllowedForUser(
        AgentAclEntry acl,
        IAgentUserContext userContext) =>
        acl.AllowedUserIds.Count == 0
            && acl.AllowedGroups.Count == 0
            && acl.AllowedTenantIds.Count == 0
            && acl.AllowedRoles.Count == 0
        || acl.AllowedUserIds.Contains(userContext.UserId)
        || userContext.Groups != null
            && acl.AllowedGroups.Intersect(userContext.Groups).Any()
        || userContext.TenantId != null
            && acl.AllowedTenantIds.Contains(userContext.TenantId)
        || userContext.Roles != null
            && acl.AllowedRoles.Intersect(userContext.Roles).Any();
}
