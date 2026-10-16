using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Routing;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Providers;
using Xunit;

namespace OpenAgent.Router.Tests.Providers;

public class OpenAgentEngineProviderTests
{
    [Fact]
    public async Task GetAgentsAsync_ReadsStandardModelListWithUserTokenAndServiceHeaders()
    {
        var handler = new RecordingHandler();
        using var provider = Create(handler, new Dictionary<string, string?>
        {
            ["BaseUrl"] = "http://engine/",
            ["ServiceHeaders:X-Api-Key"] = "service-key"
        });

        AgentProviderCatalog catalog = await provider.GetAgentsAsync(
            new AgentProviderRequestContext(
                new AgentUserContext { UserId = "user-1", TenantId = "tenant-1", IsAuthenticated = true },
                "Bearer user-token"),
            CancellationToken.None);

        Assert.Equal("http://engine/v1/models", handler.RequestUri);
        Assert.Equal("Bearer user-token", handler.Authorization);
        Assert.Equal("service-key", handler.ServiceKey);
        Assert.Equal(["finance", "hr"], catalog.Agents.Select(agent => agent.AgentId));
        Assert.Equal("Invoices and refunds", catalog.Agents[0].Description);
        Assert.Equal("Finance", catalog.Agents[0].Name);
        Assert.Equal("hr", catalog.Agents[1].Name);
        Assert.Equal(string.Empty, catalog.Agents[1].Description);
    }

    [Fact]
    public async Task GetAgentsAsync_NonSuccess_MarksProviderUnavailable()
    {
        using var provider = Create(new RecordingHandler { Status = HttpStatusCode.BadGateway },
            new Dictionary<string, string?> { ["BaseUrl"] = "http://engine" });

        AgentProviderCatalog catalog = await provider.GetAgentsAsync(
            new AgentProviderRequestContext(new AgentUserContext { UserId = "u", TenantId = "t" }),
            CancellationToken.None);

        Assert.False(catalog.IsAvailable);
    }

    [Theory]
    [InlineData(ChatProtocol.ChatCompletions, "/v1/chat/completions")]
    [InlineData(ChatProtocol.Responses, "/v1/responses")]
    [InlineData(ChatProtocol.Messages, "/v1/messages")]
    public async Task ResolveForwardingAsync_UsesTheStandardPathOfTheProtocol(ChatProtocol protocol, string path)
    {
        using var provider = Create(new RecordingHandler(),
            new Dictionary<string, string?> { ["BaseUrl"] = "http://engine" });

        AgentForwardingTarget? target = await provider.ResolveForwardingAsync(
            protocol, "tenant-1", "session-1", CancellationToken.None);

        Assert.Equal("http://engine" + path, target?.RequestUri.ToString());
        Assert.Equal("http://engine", target?.DestinationPrefix);
    }

    [Fact]
    public async Task ConfiguredProtocols_LimitWhatTheProviderServes()
    {
        using var provider = Create(new RecordingHandler(), new Dictionary<string, string?>
        {
            ["BaseUrl"] = "http://engine",
            ["Protocols:0"] = "Messages"
        });

        Assert.True(provider.Supports(ChatProtocol.Messages));
        Assert.False(provider.Supports(ChatProtocol.Responses));
        Assert.Null(await provider.ResolveForwardingAsync(
            ChatProtocol.Responses, "tenant-1", null, CancellationToken.None));
    }

    [Fact]
    public void NoConfiguredProtocols_MeansAllThree()
    {
        using var provider = Create(new RecordingHandler(), new Dictionary<string, string?>());

        Assert.All(Enum.GetValues<ChatProtocol>(), protocol => Assert.True(provider.Supports(protocol)));
    }

    [Fact]
    public void UnknownProtocolName_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Create(new RecordingHandler(),
            new Dictionary<string, string?> { ["Protocols:0"] = "Grpc" }));
    }

    [Fact]
    public async Task WithoutBaseUrl_UsesRouteTableWithTenantAndSession()
    {
        var routeTable = new StubRouteTable();
        using var provider = new OpenAgentEngineProvider("engine",
            new ConfigurationBuilder().Build(), routeTable, new RecordingHandler());

        await provider.ResolveForwardingAsync(ChatProtocol.Responses, "tenant-1", "session-1", CancellationToken.None);

        Assert.Equal("tenant-1", routeTable.TenantId);
        Assert.Equal("session-1", routeTable.SessionId);
    }

    private static OpenAgentEngineProvider Create(HttpMessageHandler handler, Dictionary<string, string?> settings) =>
        new("engine", new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new StubRouteTable(), handler);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? ServiceKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null;
            ServiceKey = request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = JsonContent.Create(new
                {
                    @object = "list",
                    data = new object[]
                    {
                        new { id = "finance", @object = "model", display_name = "Finance", description = "Invoices and refunds" },
                        new { id = "hr", @object = "model" }
                    }
                })
            });
        }
    }

    private sealed class StubRouteTable : IRouteTable
    {
        public string? TenantId { get; private set; }
        public string? SessionId { get; private set; }

        public string? GetTargetEndpoint(string intent) => "http://engine";

        public string? GetTargetEndpoint(string intent, string? tenantId, string? conversationId)
        {
            TenantId = tenantId;
            SessionId = conversationId;
            return "http://engine";
        }
    }
}
