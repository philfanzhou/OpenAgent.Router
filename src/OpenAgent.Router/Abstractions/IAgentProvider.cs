using OpenAgent.Router.Models;

namespace OpenAgent.Router;

/// <summary>A downstream that serves agents through the standard OpenAI / Anthropic APIs; the agent ID is the <c>model</c>.</summary>
public interface IAgentProvider
{
    string Id { get; }

    bool Supports(ChatProtocol protocol);

    Task<AgentProviderCatalog> GetAgentsAsync(
        AgentProviderRequestContext requestContext,
        CancellationToken cancellationToken = default);

    Task<AgentForwardingTarget?> ResolveForwardingAsync(
        ChatProtocol protocol,
        string? tenantId,
        string? sessionId,
        CancellationToken cancellationToken = default);

    ValueTask ConfigureRequestAsync(
        HttpRequestMessage request,
        AgentForwardingTarget target,
        CancellationToken cancellationToken = default);
}
