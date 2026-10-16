using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IConversationProviderResolver
{
    Task<ConversationProviderAffinity?> ResolveAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Like <see cref="ResolveAsync"/>, but an unknown or foreign ID yields null instead of 404.</summary>
    Task<ConversationProviderAffinity?> TryResolveAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<ConversationProviderAffinity> BindAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        string providerId,
        string agentId,
        CancellationToken cancellationToken = default,
        string? sessionId = null);

    Task OverwriteAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        string providerId,
        string agentId,
        string sessionId,
        CancellationToken cancellationToken = default);
}
