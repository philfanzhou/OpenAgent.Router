using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Router.Models;

public sealed record AgentProviderCatalog(
    IReadOnlyList<AgentSummary> Agents,
    bool IsAvailable = true);

/// <param name="Protocol">When set, only providers serving this protocol take part.</param>
public sealed record AgentProviderRequestContext(
    IAgentUserContext UserContext,
    string? AuthenticationToken = null,
    ChatProtocol? Protocol = null);
