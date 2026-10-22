using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Router.Models;

internal sealed record AgentCatalogEntry(
    AgentSummary Agent,
    string ProviderId);

internal sealed record AgentSelection(
    string? AgentId,
    string ProviderId,
    string? ConversationId = null,
    string? PreviousAgentId = null);
