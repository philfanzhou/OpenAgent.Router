using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ModelCatalogEndpointHandlerTests
{
    [Fact]
    public async Task HandleAsync_AuthorizedCatalog_ReturnsStandardModelListStartingWithAuto()
    {
        var catalog = new StubCatalogService(
        [
            new AgentCatalogEntry(new AgentSummary { AgentId = "finance", Name = "Finance", Description = "Invoices" }, "partner")
        ]);

        IResult result = await ModelCatalogEndpointHandler.HandleAsync(
            catalog,
            AuthenticatedUser,
            new DefaultHttpContext(),
            CancellationToken.None);

        IValueHttpResult valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(valueResult.Value));
        Assert.Equal("list", body.RootElement.GetProperty("object").GetString());
        JsonElement[] data = body.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(["auto", "finance"], data.Select(model => model.GetProperty("id").GetString()));
        Assert.Equal("Invoices", data[1].GetProperty("description").GetString());
        Assert.Equal("model", data[1].GetProperty("object").GetString());
    }

    [Fact]
    public async Task HandleAsync_AnonymousUser_ReturnsUnauthorized()
    {
        IResult result = await ModelCatalogEndpointHandler.HandleAsync(
            new StubCatalogService([]),
            new AgentUserContext { UserId = "anonymous", IsAuthenticated = false },
            new DefaultHttpContext(),
            CancellationToken.None);

        IStatusCodeHttpResult status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, status.StatusCode);
    }

    private static AgentUserContext AuthenticatedUser => new()
    {
        UserId = "user-1",
        TenantId = "tenant-1",
        IsAuthenticated = true
    };

    private sealed class StubCatalogService(IReadOnlyList<AgentCatalogEntry> entries) : IAgentCatalogService
    {
        public Task<IReadOnlyList<AgentCatalogEntry>> GetAuthorizedAsync(
            AgentProviderRequestContext requestContext,
            CancellationToken cancellationToken = default) => Task.FromResult(entries);

        public Task<AgentCatalogEntry> ResolveAsync(
            AgentProviderRequestContext requestContext,
            string agentId,
            CancellationToken cancellationToken = default, string? providerId = null) =>
            Task.FromResult(entries.Single(entry => entry.Agent.AgentId == agentId));
    }
}
