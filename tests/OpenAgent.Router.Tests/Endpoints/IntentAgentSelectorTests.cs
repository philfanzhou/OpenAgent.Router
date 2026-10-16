using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class IntentAgentSelectorTests
{
    private static readonly AgentSummary[] Candidates =
        [new() { AgentId = "finance", Name = "Finance", Description = "Handles invoices" }];

    [Theory]
    [InlineData("finance", 0.9, "finance")]
    [InlineData("FINANCE", 0.9, "finance")]
    [InlineData("unknown", 0.9, null)]
    [InlineData("finance", 0.1, null)]
    [InlineData("finance", double.NaN, null)]
    [InlineData("finance", double.PositiveInfinity, null)]
    public void ValidateResult_OnlyAcceptsFiniteConfidenceAndCandidate(string id, double confidence, string? expected) =>
        Assert.Equal(expected, IntentAgentSelector.ValidateResult(new(id, confidence), Candidates, 0.5));

    [Fact]
    public async Task DedicatedModel_ReceivesPromptAndCandidatesWithoutCallerCredentialsOrProfile()
    {
        var handler = new Handler();
        var selector = Create(handler);
        string? result = await selector.SelectAsync(new AgentProviderRequestContext(
            new AgentUserContext { UserId = "user", TenantId = "tenant" }, "Bearer caller-secret", "execution-profile"),
            "find invoice", Candidates, CancellationToken.None);
        Assert.Equal("finance", result);
        Assert.Equal("Bearer routing-secret", handler.Authorization);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Contains("find invoice", body.RootElement.GetProperty("state").GetString());
        JsonElement question = body.RootElement.GetProperty("questions").GetProperty("agent");
        Assert.Equal("choice", question.GetProperty("type").GetString());
        Assert.Equal("Handles invoices", question.GetProperty("criteria").GetProperty("finance").GetString());
        Assert.DoesNotContain("caller-secret", handler.RequestBody);
        Assert.DoesNotContain("execution-profile", handler.RequestBody);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"answers\":{\"agent\":{\"choice\":\"finance\"}}}")]
    [InlineData("{\"answers\":{\"agent\":{\"choice\":\"finance\",\"confidence\":null}}}")]
    [InlineData("{\"answers\":{\"agent\":{\"choice\":\"unknown\",\"confidence\":0.99}}}")]
    [InlineData("{\"answers\":{\"agent\":{\"choice\":\"finance\",\"confidence\":0.01}}}")]
    public async Task InvalidDecision_ReturnsNoSelection(string content)
    {
        var selector = Create(new Handler { Content = content });
        Assert.Null(await selector.SelectAsync(Context(), "invoice", Candidates, CancellationToken.None));
    }

    [Fact]
    public async Task GatewayFailure_ReturnsNoSelection()
    {
        var selector = Create(new Handler { Status = HttpStatusCode.TooManyRequests });
        Assert.Null(await selector.SelectAsync(Context(), "invoice", Candidates, CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_ReturnsNoSelectionButCallerCancellationPropagates()
    {
        var selector = Create(new Handler { Delay = true }, timeoutMs: 20);
        Assert.Null(await selector.SelectAsync(Context(), "invoice", Candidates, CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            selector.SelectAsync(Context(), "invoice", Candidates, canceled.Token));
    }

    [Fact]
    public async Task OversizedResponse_ReturnsNoSelection()
    {
        var selector = Create(new Handler { Content = "{\"pad\":\"" + new string('x', 20000) + "\"}" });
        Assert.Null(await selector.SelectAsync(Context(), "invoice", Candidates, CancellationToken.None));
    }

    private static AgentProviderRequestContext Context() => new(new AgentUserContext { UserId = "user", TenantId = "tenant" });
    private static IntentAgentSelector Create(Handler handler, int timeoutMs = 1000) => new(
        new ClientFactory(handler), Microsoft.Extensions.Options.Options.Create(new DecisionModelOptions
        {
            Endpoint = "http://model/v1/decisions", ApiKey = "routing-secret"
        }), Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions { TimeoutMs = timeoutMs }),
        NullLogger<IntentAgentSelector>.Instance);

    private sealed class ClientFactory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Content { get; init; } = "{\"answers\":{\"agent\":{\"choice\":\"finance\",\"confidence\":0.95}}}";
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool Delay { get; init; }
        public string? RequestBody { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Delay) await Task.Delay(Timeout.Infinite, token);
            RequestBody = await request.Content!.ReadAsStringAsync(token);
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Content)
            };
        }
    }
}
