namespace OpenAgent.Router.Models;

internal enum ConversationAffinityState
{
    Pending,
    Confirmed
}

internal sealed record ConversationProviderAffinity(
    string ProviderId,
    ConversationAffinityState State,
    string? AgentId = null,
    string? UserId = null,
    int Version = 1,
    DateTimeOffset? ExpiresAt = null,
    bool Deleted = false);
