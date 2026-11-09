using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Options;
using OpenAgent.Router.Routing;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class RoutingFeedbackEndpointHandlerTests
{
    [Fact]
    public async Task ValidFeedback_RetainsRedactedSampleAfterOwnershipAndCatalogChecks()
    {
        var user = new AgentUserContext
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            IsAuthenticated = true
        };
        var conversations = new InMemoryConversationResolver();
        await conversations.BindAsync(
            new AgentProviderRequestContext(user),
            "session-1",
            "engine",
            "finance",
            CancellationToken.None);
        var options = Microsoft.Extensions.Options.Options.Create(new RoutingQualityOptions
        {
            IncludeMessageContent = false
        });
        var samples = new RoutingQualitySampleStore(options);
        var context = new DefaultHttpContext();

        IResult result = await RoutingFeedbackEndpointHandler.HandleAsync(
            new RoutingFeedbackRequest("session-1", "hr", "I needed HR help", "wrong team"),
            context,
            user,
            conversations,
            new StubCatalog(),
            samples,
            options,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        RoutingQualitySample sample = Assert.Single(await samples.GetRecentAsync("tenant-1", 10));
        Assert.Equal("finance", sample.SelectedAgentId);
        Assert.Equal("hr", sample.ExpectedAgentId);
        Assert.Null(sample.Message);
        Assert.Equal(64, sample.MessageHash.Length);
        Assert.Equal("wrong team", sample.Reason);
    }

    private sealed class StubCatalog : IAgentCatalogService
    {
        private static readonly AgentCatalogEntry Entry = new(
            new AgentSummary { AgentId = "hr", Description = "Human resources" },
            "engine");

        public Task<IReadOnlyList<AgentCatalogEntry>> GetAuthorizedAsync(
            AgentProviderRequestContext requestContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentCatalogEntry>>([Entry]);

        public Task<AgentCatalogEntry> ResolveAsync(
            AgentProviderRequestContext requestContext,
            string agentId,
            CancellationToken cancellationToken = default,
            string? providerId = null) =>
            string.Equals(agentId, Entry.Agent.AgentId, StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(Entry)
                : Task.FromException<AgentCatalogEntry>(new AgentRoutingException(
                    StatusCodes.Status404NotFound,
                    RouterErrorCodes.AgentNotFound,
                    "Agent was not found"));
    }
}
