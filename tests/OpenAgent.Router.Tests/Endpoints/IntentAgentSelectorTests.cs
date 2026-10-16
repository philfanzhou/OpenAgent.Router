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
            new AgentUserContext { UserId = "user", TenantId = "tenant" }, "Bearer caller-secret"),
            "find invoice", Candidates, CancellationToken.None);
        Assert.Equal("finance", result);
        Assert.Equal("Bearer routing-secret", handler.Authorization);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Contains("find invoice", body.RootElement.GetProperty("state").GetString());
        JsonElement question = body.RootElement.GetProperty("questions").GetProperty("agent");
        Assert.Equal("choice", question.GetProperty("type").GetString());
        Assert.Equal("Handles invoices", question.GetProperty("criteria").GetProperty("finance").GetString());
        Assert.DoesNotContain("caller-secret", handler.RequestBody);
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
    public async Task InputOverModelContext_ReturnsNoSelectionWithoutCallingModel()
    {
        var handler = new Handler();
        var selector = Create(handler);
        Assert.Null(await selector.SelectAsync(Context(), new string('x', 8000), Candidates, CancellationToken.None));
        Assert.Null(handler.RequestBody);
    }

    [Fact]
    public async Task OversizedResponse_ReturnsNoSelection()
    {
        var selector = Create(new Handler { Content = "{\"pad\":\"" + new string('x', 20000) + "\"}" });
        Assert.Null(await selector.SelectAsync(Context(), "invoice", Candidates, CancellationToken.None));
    }

    [Fact]
    public async Task ConsecutiveFailures_OpenBreakerAndSkipModel_ThenProbeAfterCooldown()
    {
        var time = new Clock(DateTimeOffset.UtcNow);
        var status = new DecisionModelStatus(time);
        var handler = new Handler { Status = HttpStatusCode.BadGateway };
        var selector = Create(handler, status: status);
        for (int i = 0; i < DecisionModelStatus.BreakerThreshold; i++)
            await selector.SelectAsync(Context(), "invoice", Candidates, CancellationToken.None);
        Assert.True(status.IsBreakerOpen);

        handler.RequestBody = null;
        Assert.Null(await selector.SelectAsync(Context(), "invoice", Candidates, CancellationToken.None));
        Assert.Null(handler.RequestBody);

        time.Now += DecisionModelStatus.BreakerOpenDuration + TimeSpan.FromSeconds(1);
        Assert.False(status.IsBreakerOpen);
        await selector.SelectAsync(Context(), "invoice", Candidates, CancellationToken.None);
        Assert.True(status.IsBreakerOpen);
    }

    [Fact]
    public async Task ApiKeyFile_IsReadOncePerCacheWindow()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "key-1");
            var handler = new Handler();
            var selector = Create(handler, keyFile: file);
            await selector.SelectAsync(Context(), "invoice", Candidates, CancellationToken.None);
            File.WriteAllText(file, "key-2");
            await selector.SelectAsync(Context(), "invoice", Candidates, CancellationToken.None);
            Assert.Equal("Bearer key-1", handler.Authorization);
        }
        finally { File.Delete(file); }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AgentProviderRequestContext Context() => new(new AgentUserContext { UserId = "user", TenantId = "tenant" });
    private static IntentAgentSelector Create(
        Handler handler, int timeoutMs = 1000, DecisionModelStatus? status = null, string keyFile = "") => new(
        new ClientFactory(handler), Microsoft.Extensions.Options.Options.Create(new DecisionModelOptions
        {
            Endpoint = "http://model/v1/decisions", ApiKey = "routing-secret", ApiKeyFile = keyFile
        }), Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions { TimeoutMs = timeoutMs }),
        NullLogger<IntentAgentSelector>.Instance, status);

    private sealed class ClientFactory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Content { get; init; } = "{\"answers\":{\"agent\":{\"choice\":\"finance\",\"confidence\":0.95}}}";
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool Delay { get; init; }
        public string? RequestBody { get; set; }
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
