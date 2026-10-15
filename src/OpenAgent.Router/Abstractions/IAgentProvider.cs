using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Models;

namespace OpenAgent.Router;

public interface IAgentProvider
{
    string Id { get; }

    // Providers must explicitly opt into the caller-issued session-ID contract.
    bool SupportsRouterConversationIds => false;

    Task<AgentForwardingTarget?> ResolveConversationForwardingAsync(
        string pathAndQuery, string? tenantId, string conversationId,
        CancellationToken cancellationToken = default) => Task.FromResult<AgentForwardingTarget?>(null);

    Task<AgentProviderCatalog> GetAgentsAsync(
        AgentProviderRequestContext requestContext,
        CancellationToken cancellationToken = default);

    Task<AgentProviderConversationStatus> ResolveConversationAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<IntentRecognitionResult?> RecognizeIntentAsync(
        AgentProviderRequestContext requestContext,
        string intentAgentId,
        IReadOnlyList<AgentSummary> agents,
        string message,
        CancellationToken cancellationToken = default);

    Task<AgentForwardingTarget?> ResolveForwardingAsync(
        string? action,
        string? tenantId,
        string? conversationId,
        CancellationToken cancellationToken = default);

    ValueTask ConfigureRequestAsync(
        HttpRequestMessage request,
        AgentForwardingTarget target,
        CancellationToken cancellationToken = default);
}
