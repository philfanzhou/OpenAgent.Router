using Microsoft.Extensions.Options;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Routing;

internal sealed class ConversationProviderResolver(
    IAgentProviderRegistry providers,
    IConversationProviderStore store,
    IOptions<ConversationRoutingOptions> options) : IConversationProviderResolver
{
    public async Task<ConversationProviderAffinity?> ResolveAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken)
    {
        ConversationProviderAffinity? affinity = await WithStorageTimeoutAsync(
            token => store.GetAsync(RequireTenantId(requestContext), conversationId, token),
            cancellationToken).ConfigureAwait(false);
        // No provider probes or implicit creation. Legacy records without an Agent/owner cannot be trusted.
        if (affinity is not { Version: 1, Deleted: false }
            || string.IsNullOrWhiteSpace(affinity.AgentId)
            || !string.Equals(affinity.UserId, requestContext.UserContext.UserId, StringComparison.Ordinal)
            || affinity.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new AgentRoutingException(StatusCodes.Status404NotFound,
                RouterErrorCodes.ConversationNotFound, "Conversation was not found");
        }
        if (!providers.TryGet(affinity.ProviderId, out _))
        {
            throw new AgentRoutingException(StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.AgentProviderUnavailable, "Conversation Provider is unavailable");
        }
        return affinity;
    }

    public Task<ConversationProviderAffinity> BindAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        string providerId,
        string agentId,
        CancellationToken cancellationToken) => WithStorageTimeoutAsync(
            token => store.BindAsync(RequireTenantId(requestContext), conversationId,
                new ConversationProviderAffinity(providerId, ConversationAffinityState.Confirmed,
                    agentId, requestContext.UserContext.UserId,
                    ExpiresAt: DateTimeOffset.UtcNow.AddDays(options.Value.RetentionDays)), token),
            cancellationToken);

    private async Task<T> WithStorageTimeoutAsync<T>(Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.StorageTimeoutMs);
        try
        {
            return await action(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is not AgentRoutingException)
        {
            throw new AgentRoutingException(StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.ConversationStorageUnavailable, "Conversation storage is unavailable");
        }
    }

    private static string RequireTenantId(AgentProviderRequestContext context) =>
        !string.IsNullOrWhiteSpace(context.UserContext.TenantId)
            ? context.UserContext.TenantId
            : throw new AgentRoutingException(StatusCodes.Status400BadRequest,
                RouterErrorCodes.InvalidTenant, "Tenant ID is required");
}
