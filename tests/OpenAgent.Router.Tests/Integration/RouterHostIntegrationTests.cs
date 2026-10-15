using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpenAgent.Router.Tests.Integration;

public sealed class RouterHostIntegrationTests : IClassFixture<RouterHostFixture>
{
    private readonly RouterHostFixture _fixture;

    private Dictionary<string, string?> ModelSettings() => new()
    {
        ["RouterSettings:IntentRecognition:Enabled"] = "true",
        ["RouterSettings:DecisionModel:Endpoint"] = $"{_fixture.PrimaryEngine.Endpoint}/v1/chat/completions",
        ["RouterSettings:DecisionModel:ModelId"] = "dedicated-routing-model"
    };

    [Theory]
    [InlineData("{\"message\":\"hello\",\"context\":{\"conversationId\":null}}")]
    [InlineData("{\"message\":\"hello\",\"context\":{\"conversationId\":\"   \"}}")]
    public async Task EmptyConversationValue_CreatesAndForwardsRouterId(string body)
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/chat")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        AddAuthentication(request);
        request.Headers.Add("X-Agent-Id", "default");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string id = response.Headers.GetValues("X-Conversation-Id").Single();
        using var forwarded = JsonDocument.Parse(_fixture.PrimaryEngine.LastChatBody!);
        Assert.Equal(id, forwarded.RootElement.GetProperty("context").GetProperty("conversationId").GetString());
    }

    [Fact]
    public async Task DuplicateRoutingProperties_AreRejectedBeforeForwarding()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/chat")
        {
            Content = new StringContent("{\"message\":\"hi\",\"context\":{\"agentId\":\"default\",\"AgentId\":\"finance\"}}",
                Encoding.UTF8, "application/json")
        };
        AddAuthentication(request);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [RedisFact]
    public async Task SharedRedis_ContinuesConversationAcrossRouterRestartWithoutReselection()
    {
        var settings = ModelSettings();
        settings["ConnectionStrings:Redis"] = Environment.GetEnvironmentVariable("ROUTER_TEST_REDIS");
        string id;
        int decisions = _fixture.PrimaryEngine.DecisionCount;
        using (var firstRouter = _fixture.CreateFactory(settings: settings))
        using (var client = firstRouter.CreateClient())
        {
            Assert.NotNull(firstRouter.Services.GetService(typeof(StackExchange.Redis.IConnectionMultiplexer)));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/chat")
            {
                Content = JsonContent.Create(new { message = "invoice" })
            };
            AddAuthentication(request);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            id = response.Headers.GetValues("X-Conversation-Id").Single();
        }
        using var restartedRouter = _fixture.CreateFactory(settings: settings);
        using var restartedClient = restartedRouter.CreateClient();
        using var followup = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/chat")
        {
            Content = JsonContent.Create(new { message = "write code", context = new { conversationId = id } })
        };
        AddAuthentication(followup);
        using var next = await restartedClient.SendAsync(followup);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal("finance", _fixture.PrimaryEngine.LastAgentId);
        Assert.Equal(id, _fixture.PrimaryEngine.LastConversationId);
        Assert.Equal("test-execution-profile", _fixture.PrimaryEngine.LastExecutionProfile);
        Assert.Equal(decisions + 1, _fixture.PrimaryEngine.DecisionCount);
    }

    [Fact]
    public async Task ModelSelection_ForwardedContextMatchesBindingAndFollowUpSkipsModel()
    {
        using var factory = _fixture.CreateFactory(settings: ModelSettings());
        using var client = factory.CreateClient();
        int before = _fixture.PrimaryEngine.DecisionCount;
        using var initial = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/chat")
        {
            Content = JsonContent.Create(new { message = "find my invoice" })
        };
        AddAuthentication(initial);
        using var first = await client.SendAsync(initial);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        string id = first.Headers.GetValues("X-Conversation-Id").Single();
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal("finance", first.Headers.GetValues("X-OpenAgent-Selected-Agent-Id").Single());
        Assert.Equal(id, _fixture.PrimaryEngine.LastConversationId);
        using var body = JsonDocument.Parse(_fixture.PrimaryEngine.LastChatBody!);
        Assert.Equal(id, body.RootElement.GetProperty("context").GetProperty("conversationId").GetString());
        Assert.Equal("finance", body.RootElement.GetProperty("context").GetProperty("agentId").GetString());
        Assert.DoesNotContain("Unpublished", _fixture.PrimaryEngine.LastDecisionBody!);
        Assert.DoesNotContain("Other tenant", _fixture.PrimaryEngine.LastDecisionBody!);

        using var followUp = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/chat")
        {
            Content = JsonContent.Create(new { message = "write a song", context = new { conversationId = id } })
        };
        AddAuthentication(followUp);
        using var next = await client.SendAsync(followUp);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal("finance", _fixture.PrimaryEngine.LastAgentId);
        Assert.Equal(before + 1, _fixture.PrimaryEngine.DecisionCount);
    }

    [Fact]
    public async Task ExplicitAgent_SkipsModelAndIdempotentRetryReturnsSameConversation()
    {
        using var factory = _fixture.CreateFactory(settings: ModelSettings());
        using var client = factory.CreateClient();
        int before = _fixture.PrimaryEngine.ChatRequestCount;
        int decisions = _fixture.PrimaryEngine.DecisionCount;
        using var firstRequest = CreateChatRequest("idempotent");
        firstRequest.Headers.Add("Idempotency-Key", "new-session");
        using var first = await client.SendAsync(firstRequest);
        using var secondRequest = CreateChatRequest("idempotent");
        secondRequest.Headers.Add("Idempotency-Key", "new-session");
        using var second = await client.SendAsync(secondRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(first.Headers.GetValues("X-Conversation-Id"), second.Headers.GetValues("X-Conversation-Id"));
        Assert.Equal(first.Headers.GetValues("X-OpenAgent-Selected-Agent-Id"),
            second.Headers.GetValues("X-OpenAgent-Selected-Agent-Id"));
        Assert.Equal(before + 1, _fixture.PrimaryEngine.ChatRequestCount);
        Assert.Equal(decisions, _fixture.PrimaryEngine.DecisionCount);
    }

    [Fact]
    public async Task UnknownSessionAndConflictingRoutingValues_DoNotReachModelOrChat()
    {
        using var factory = _fixture.CreateFactory(settings: ModelSettings());
        using var client = factory.CreateClient();
        int before = _fixture.PrimaryEngine.ChatRequestCount;
        int decisions = _fixture.PrimaryEngine.DecisionCount;
        using var unknown = CreateChatRequest("hello");
        unknown.Headers.Add("X-Conversation-Id", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var conflict = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/chat")
        {
            Content = JsonContent.Create(new { message = "hello", context = new { agentId = "finance" } })
        };
        AddAuthentication(conflict);
        conflict.Headers.Add("X-Agent-Id", "default");
        using var rejected = await client.SendAsync(conflict);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(before, _fixture.PrimaryEngine.ChatRequestCount);
        Assert.Equal(decisions, _fixture.PrimaryEngine.DecisionCount);
    }

    [Fact]
    public async Task ModelUnavailable_UsesAuthorizedFallback()
    {
        var settings = ModelSettings();
        settings["RouterSettings:DecisionModel:Endpoint"] = "http://127.0.0.1:1/v1/chat/completions";
        using var factory = _fixture.CreateFactory(settings: settings);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/chat")
        {
            Content = JsonContent.Create(new { message = "find invoice" })
        };
        AddAuthentication(request);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("default", response.Headers.GetValues("X-OpenAgent-Selected-Agent-Id").Single());
        Assert.Equal("Degraded", await client.GetStringAsync("/ready"));
        Assert.Equal("Healthy", await client.GetStringAsync("/health"));
    }

    [Fact]
    public async Task DuplicateStream_ReturnsConflictWithOriginalConversationId()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        using var request = CreateChatRequest("stream retry", "/api/v1/agent/chat/sse");
        request.Headers.Add("Idempotency-Key", "stream-key");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var first = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        string id = first.Headers.GetValues("X-Conversation-Id").Single();
        using var retry = CreateChatRequest("stream retry", "/api/v1/agent/chat/sse");
        retry.Headers.Add("Idempotency-Key", "stream-key");
        using var second = await client.SendAsync(retry, cancel.Token);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(id, second.Headers.GetValues("X-Conversation-Id").Single());
        cancel.Cancel();
    }

    public RouterHostIntegrationTests(RouterHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Chat_WithoutAuthentication_ReturnsUnauthorized()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/agent/chat",
            new { message = "hello" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithValidRequest_ForwardsIdentityAndRecordsSelection()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateChatRequest("ordinary chat");

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        string metrics = await client.GetStringAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("primary-engine", body, StringComparison.Ordinal);
        Assert.Equal(
            "default",
            Assert.Single(response.Headers.GetValues("X-OpenAgent-Selected-Agent-Id")));
        Assert.Null(_fixture.PrimaryEngine.LastUserId);
        Assert.Null(_fixture.PrimaryEngine.LastTenantId);
        Assert.StartsWith("Basic ", _fixture.PrimaryEngine.LastAuthorization, StringComparison.Ordinal);
        Assert.Contains("openagent_router_provider_selections_total", metrics, StringComparison.Ordinal);
        Assert.Contains("source=\"explicit\"", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_DevelopmentTenantHeader_IsForwardedWithoutRouterInterpretation()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateChatRequest("development tenant");
        request.Headers.Add("X-Tenant-Id", "development-tenant");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("development-tenant", _fixture.PrimaryEngine.LastTenantId);
        Assert.Null(_fixture.PrimaryEngine.LastCatalogTenantId);
    }

    [Fact]
    public async Task Chat_RepeatedPrompt_CreatesIndependentConversations()
    {
        int requestCount = _fixture.PrimaryEngine.ChatRequestCount;
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage first = await client.SendAsync(CreateChatRequest("cacheable query"));
        string firstBody = await first.Content.ReadAsStringAsync();
        using HttpResponseMessage second = await client.SendAsync(CreateChatRequest("cacheable query"));
        string secondBody = await second.Content.ReadAsStringAsync();
        string metrics = await client.GetStringAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, secondBody);
        Assert.Equal(requestCount + 2, _fixture.PrimaryEngine.ChatRequestCount);
        Assert.NotEqual(first.Headers.GetValues("X-Conversation-Id").Single(),
            second.Headers.GetValues("X-Conversation-Id").Single());
    }

    [Fact]
    public async Task Files_MultipartUploadAndBinaryDownload_PreservePayloads()
    {
        byte[] uploadBytes = [0x00, 0x10, 0x80, 0xff];
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage upload = new(HttpMethod.Post, "/api/v1/agent/files");
        AddAuthentication(upload);
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(uploadBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(file, "file", "payload.bin");
        upload.Content = multipart;

        using HttpResponseMessage uploadResponse = await client.SendAsync(upload);
        using HttpRequestMessage download = new(
            HttpMethod.Get,
            "/api/v1/agent/files/file-1/download");
        AddAuthentication(download);
        using HttpResponseMessage downloadResponse = await client.SendAsync(download);
        byte[] downloadBytes = await downloadResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        Assert.Equal("payload.bin", _fixture.PrimaryEngine.UploadedFileName);
        Assert.Equal("application/octet-stream", _fixture.PrimaryEngine.UploadedContentType);
        Assert.Equal(uploadBytes, _fixture.PrimaryEngine.UploadedBytes);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal("application/octet-stream", downloadResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TestEngineHost.ExpectedDownloadBytes.ToArray(), downloadBytes);
    }

    [Fact]
    public async Task ConversationCompaction_ForwardsPostToEngine()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage created = await client.SendAsync(CreateChatRequest("before compaction"));
        string id = created.Headers.GetValues("X-Conversation-Id").Single();
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"/api/v1/agent/conversations/{id}/compact");
        AddAuthentication(request);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id, _fixture.PrimaryEngine.LastCompactedConversationId);
        Assert.StartsWith("Basic ", _fixture.PrimaryEngine.LastAuthorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_SseClientCancellation_ReachesDownstream()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateChatRequest(
            "stream response",
            "/api/v1/agent/chat/sse");
        using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestCancellation.Token);
        await using Stream stream = await response.Content.ReadAsStreamAsync(
            requestCancellation.Token);
        using var reader = new StreamReader(stream);
        Assert.Equal("event: token", await reader.ReadLineAsync(requestCancellation.Token));
        Assert.Equal("data: first", await reader.ReadLineAsync(requestCancellation.Token));

        requestCancellation.Cancel();
        response.Dispose();
        using var propagationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _fixture.PrimaryEngine.WaitForSseCancellationAsync(propagationTimeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Chat_AfterSseClientCancellation_DownstreamNotQuarantined()
    {
        // 用户停止生成（客户端断开 SSE）是正常取消：一次断开不得把下游 Engine
        // 计为故障并隔离 30 秒（FailureThreshold=1），否则紧随其后的请求被 503，
        // 表现为“会话失败”。断开后立即再发一次普通对话必须成功。
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using (HttpRequestMessage aborting = CreateChatRequest(
            "to be cancelled",
            "/api/v1/agent/chat/sse"))
        using (var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            using HttpResponseMessage response = await client.SendAsync(
                aborting,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token);
            await using Stream stream = await response.Content.ReadAsStreamAsync(
                requestCancellation.Token);
            using var reader = new StreamReader(stream);
            await reader.ReadLineAsync(requestCancellation.Token);
            await reader.ReadLineAsync(requestCancellation.Token);

            requestCancellation.Cancel();
            response.Dispose();
            using var propagationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _fixture.PrimaryEngine.WaitForSseCancellationAsync(propagationTimeout.Token);
        }

        using (HttpRequestMessage followup = CreateChatRequest("after cancellation"))
        using (HttpResponseMessage next = await client.SendAsync(followup))
        {
            string body = await next.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            Assert.Contains("primary-engine", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task File_UnreachableEngine_MapsForwardingFailureMetric()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory("http://127.0.0.1:1");
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            "/api/v1/agent/files/file-1/download");
        AddAuthentication(request);

        using HttpResponseMessage response = await client.SendAsync(request);
        string metrics = await client.GetStringAsync("/metrics");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("openagent_router_forwarding_failures_total", metrics, StringComparison.Ordinal);
    }

    private static HttpRequestMessage CreateChatRequest(
        string message,
        string path = "/api/v1/agent/chat")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { message })
        };
        AddAuthentication(request);
        request.Headers.Add("X-Agent-Id", "default");
        return request;
    }

    private static void AddAuthentication(HttpRequestMessage request)
    {
        string credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("admin:admin"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }
}
