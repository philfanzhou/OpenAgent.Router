using Microsoft.AspNetCore.Http;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Tests.Endpoints;

/// <summary>Dictionary-backed resolver for tests that only need bind / find / overwrite.</summary>
internal sealed class InMemoryConversationResolver : IConversationProviderResolver
{
    public Dictionary<string, ConversationProviderAffinity> Records { get; } = new();

    public Task<ConversationProviderAffinity?> ResolveAsync(AgentProviderRequestContext requestContext,
        string conversationId, CancellationToken cancellationToken = default) => TryResolveAsync(requestContext, conversationId, cancellationToken);

    public Task<ConversationProviderAffinity?> TryResolveAsync(AgentProviderRequestContext requestContext,
        string conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Records.TryGetValue(conversationId, out var record)
            && record.UserId == requestContext.UserContext.UserId ? record : null);

    public Task<ConversationProviderAffinity> BindAsync(AgentProviderRequestContext requestContext,
        string conversationId, string providerId, string agentId, CancellationToken cancellationToken = default,
        string? sessionId = null) =>
        Task.FromResult(Records[conversationId] = new ConversationProviderAffinity(providerId,
            ConversationAffinityState.Confirmed, agentId, requestContext.UserContext.UserId, SessionId: sessionId));

    public Task OverwriteAsync(AgentProviderRequestContext requestContext, string conversationId,
        string providerId, string agentId, string sessionId, CancellationToken cancellationToken = default) =>
        BindAsync(requestContext, conversationId, providerId, agentId, cancellationToken, sessionId);
    public Task<ConversationProviderAffinity> SwitchAsync(AgentProviderRequestContext requestContext,
        string conversationId, ConversationProviderAffinity expected, string providerId, string agentId,
        CancellationToken cancellationToken = default)
    {
        if (!Records.TryGetValue(conversationId, out ConversationProviderAffinity? current) || current != expected)
        {
            throw new AgentRoutingException(StatusCodes.Status409Conflict,
                RouterErrorCodes.ConversationSwitchConflict, "Conversation changed");
        }
        return Task.FromResult(Records[conversationId] = expected with
        {
            ProviderId = providerId,
            AgentId = agentId
        });
    }
}
