using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IConversationProviderResolver
{
    Task<ConversationProviderAffinity?> ResolveAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<ConversationProviderAffinity> BindAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        string providerId,
        string agentId,
        CancellationToken cancellationToken = default);
}
