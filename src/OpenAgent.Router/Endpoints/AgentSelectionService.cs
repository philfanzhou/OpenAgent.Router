using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionService(
    IAgentCatalogService catalog,
    IConversationProviderResolver conversations,
    IIntentAgentSelector intentAgentSelector,
    IAgentUserContext userContext,
    IOptions<IntentRecognitionOptions> options,
    ILogger<AgentSelectionService>? logger = null) : IAgentSelectionService
{
    public async Task<AgentSelection?> SelectAsync(
        string message,
        string? conversationId,
        string? explicitAgentId,
        CancellationToken cancellationToken,
        string? authenticationToken = null,
        ChatProtocol? protocol = null)
    {
        if (string.IsNullOrWhiteSpace(userContext.TenantId))
        {
            throw new AgentRoutingException(StatusCodes.Status400BadRequest,
                RouterErrorCodes.InvalidTenant, "Tenant ID is required");
        }
        AgentProviderRequestContext requestContext = new(userContext, authenticationToken, protocol);
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            ConversationProviderAffinity affinity = await conversations.ResolveAsync(
                requestContext, conversationId, cancellationToken).ConfigureAwait(false)
                ?? throw new AgentRoutingException(StatusCodes.Status404NotFound,
                    RouterErrorCodes.ConversationNotFound, "Conversation was not found");
            if (!string.IsNullOrWhiteSpace(explicitAgentId) && !string.Equals(
                explicitAgentId, affinity.AgentId, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentRoutingException(StatusCodes.Status409Conflict,
                    RouterErrorCodes.ConversationProviderMismatch, "Conversation Agent cannot be changed");
            }
            // Recheck availability and access; never reselect an Agent for an existing conversation.
            AgentCatalogEntry entry = await ResolveAuthorizedAsync(requestContext, affinity.AgentId!,
                cancellationToken, affinity.ProviderId).ConfigureAwait(false);
            if (!string.Equals(entry.ProviderId, affinity.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentRoutingException(StatusCodes.Status503ServiceUnavailable,
                    RouterErrorCodes.AgentProviderUnavailable, "Conversation Agent is unavailable on its original Provider");
            }
            RouterMeter.RecordProviderSelection("conversation");
            logger?.LogInformation("Agent route selected: {SelectionSource} {ProviderId} {AgentId}",
                "conversation", affinity.ProviderId, entry.Agent.AgentId);
            return new AgentSelection(entry.Agent.AgentId, affinity.ProviderId, conversationId);
        }

        AgentCatalogEntry? selected;
        string source;
        if (!string.IsNullOrWhiteSpace(explicitAgentId))
        {
            selected = await ResolveAuthorizedAsync(requestContext, explicitAgentId,
                cancellationToken).ConfigureAwait(false);
            source = "explicit";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new AgentRoutingException(StatusCodes.Status400BadRequest,
                    RouterErrorCodes.InvalidRoutingRequest, "A message is required for automatic Agent selection");
            }
            IReadOnlyList<AgentCatalogEntry> entries = await GetCatalogAsync(requestContext,
                cancellationToken).ConfigureAwait(false);
            // A bare "ok / 好的，继续" has no topic: skip the model and take the fallback agent.
            bool trivial = TrivialMessage.IsTrivial(message);
            string? agentId = options.Value.Enabled && !trivial
                ? await intentAgentSelector.SelectAsync(requestContext, message,
                    entries.Select(entry => entry.Agent).ToArray(), cancellationToken).ConfigureAwait(false)
                : null;
            selected = entries.FirstOrDefault(entry => string.Equals(
                entry.Agent.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            source = "model";
            if (selected == null)
            {
                selected = entries.FirstOrDefault(entry => string.Equals(
                    entry.Agent.AgentId, options.Value.FallbackAgentId, StringComparison.OrdinalIgnoreCase));
                source = trivial ? "trivial" : "fallback";
            }
        }
        if (selected == null)
        {
            RouterMeter.RecordProviderSelection("unavailable");
            return null;
        }

        string newConversationId = Guid.NewGuid().ToString();
        ConversationProviderAffinity bound = await conversations.BindAsync(requestContext,
            newConversationId, selected.ProviderId, selected.Agent.AgentId, cancellationToken).ConfigureAwait(false);
        if (bound.ProviderId != selected.ProviderId || bound.AgentId != selected.Agent.AgentId
            || bound.UserId != userContext.UserId)
        {
            throw new AgentRoutingException(StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.ConversationStorageUnavailable, "Conversation could not be created");
        }
        RouterMeter.RecordProviderSelection(source);
        logger?.LogInformation("Agent route selected: {SelectionSource} {ProviderId} {AgentId}",
            source, selected.ProviderId, selected.Agent.AgentId);
        return new AgentSelection(selected.Agent.AgentId, selected.ProviderId, newConversationId);
    }

    private async Task<AgentCatalogEntry> ResolveAuthorizedAsync(AgentProviderRequestContext context,
        string agentId, CancellationToken cancellationToken, string? providerId = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.CatalogTimeoutMs);
        try
        {
            return await catalog.ResolveAsync(context, agentId, timeout.Token, providerId)
                .WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentRoutingException(StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.AgentProviderUnavailable, "Agent catalog timed out");
        }
    }

    private async Task<IReadOnlyList<AgentCatalogEntry>> GetCatalogAsync(AgentProviderRequestContext context,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.CatalogTimeoutMs);
        try
        {
            return await catalog.GetAuthorizedAsync(context, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentRoutingException(StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.AgentProviderUnavailable, "Agent catalog timed out");
        }
    }
}
