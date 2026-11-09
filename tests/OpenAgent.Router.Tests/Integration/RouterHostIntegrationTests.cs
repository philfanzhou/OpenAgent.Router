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
        ["RouterSettings:DecisionModel:Endpoint"] = $"{_fixture.PrimaryEngine.Endpoint}/v1/decide"
    };

    public RouterHostIntegrationTests(RouterHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExplicitModel_ForwardsIssuedSessionAndChatIdWithUserCredentials()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateChatRequest("ordinary chat"));
        string metrics = await client.GetStringAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string session = response.Headers.GetValues("X-Session-Id").Single();
        string chatId = response.Headers.GetValues("X-Chat-Id").Single();
        Assert.True(Guid.TryParse(session, out _));
        Assert.StartsWith("chatcmpl-", chatId);
        Assert.Equal("default", response.Headers.GetValues("X-OpenAgent-Selected-Agent-Id").Single());
        Assert.Equal(session, _fixture.PrimaryEngine.LastSessionId);
        Assert.Equal(chatId, _fixture.PrimaryEngine.LastChatId);
        Assert.Equal("default", _fixture.PrimaryEngine.LastModel);
        Assert.Equal("/v1/chat/completions", _fixture.PrimaryEngine.LastChatPath);
        Assert.StartsWith("Basic ", _fixture.PrimaryEngine.LastAuthorization, StringComparison.Ordinal);
        Assert.Contains("source=\"explicit\"", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_WithoutAuthentication_ReturnsUnauthorized()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "default", messages = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("authentication_error", body.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("/v1/responses")]
    [InlineData("/v1/messages")]
    public async Task OtherProtocols_AreForwardedToTheirOwnStandardPath(string path)
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateChatRequest("hi", path));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(path, _fixture.PrimaryEngine.LastChatPath);
    }

    [Fact]
    public async Task AutoModel_SelectsAgentThenSessionHeaderKeepsItWithoutReselection()
    {
        using var factory = _fixture.CreateFactory(settings: ModelSettings());
        using var client = factory.CreateClient();
        int before = _fixture.PrimaryEngine.DecisionCount;

        using var first = await client.SendAsync(CreateChatRequest("find my invoice", model: "auto"));
        string session = first.Headers.GetValues("X-Session-Id").Single();
        Assert.Equal("finance", first.Headers.GetValues("X-OpenAgent-Selected-Agent-Id").Single());
        Assert.Equal("finance", _fixture.PrimaryEngine.LastModel);

        using var followUpRequest = CreateChatRequest("write a song", model: "auto");
        followUpRequest.Headers.Add("X-Session-Id", session);
        using var next = await client.SendAsync(followUpRequest);

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(session, next.Headers.GetValues("X-Session-Id").Single());
        Assert.Equal("finance", _fixture.PrimaryEngine.LastModel);
        Assert.Equal(before + 1, _fixture.PrimaryEngine.DecisionCount);
    }

    [Fact]
    public async Task ExplicitSwitch_HandsOffContextAndKeepsNewAgent()
    {
        using var factory = _fixture.CreateFactory(settings: ModelSettings());
        using var client = factory.CreateClient();
        int decisions = _fixture.PrimaryEngine.DecisionCount;
        using var first = await client.SendAsync(CreateChatRequest("hello", model: "default"));
        string session = first.Headers.GetValues("X-Session-Id").Single();
        using var switchRequest = CreateChatRequest("check invoice", model: "auto");
        switchRequest.Headers.Add("X-Session-Id", session);
        switchRequest.Headers.Add("X-OpenAgent-Switch-Agent-Id", "finance");
        switchRequest.Headers.Add("X-OpenAgent-Handoff-Summary", "Customer now needs invoice help");

        using var switched = await client.SendAsync(switchRequest);

        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        Assert.Equal("finance", switched.Headers.GetValues("X-OpenAgent-Selected-Agent-Id").Single());
        Assert.Equal("default", switched.Headers.GetValues("X-OpenAgent-Previous-Agent-Id").Single());
        Assert.Equal("default", _fixture.PrimaryEngine.LastPreviousAgentId);
        Assert.Equal("Customer now needs invoice help", _fixture.PrimaryEngine.LastHandoffSummary);

        using var followUp = CreateChatRequest("more", model: "auto");
        followUp.Headers.Add("X-Session-Id", session);
        using var next = await client.SendAsync(followUp);
        Assert.Equal("finance", next.Headers.GetValues("X-OpenAgent-Selected-Agent-Id").Single());
        Assert.Equal(decisions, _fixture.PrimaryEngine.DecisionCount);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("好的，继续")]
    public async Task AcknowledgementWithoutSession_SkipsModelAndFollowsSessionOtherwise(string message)
    {
        using var factory = _fixture.CreateFactory(settings: ModelSettings());
        using var client = factory.CreateClient();
        int before = _fixture.PrimaryEngine.DecisionCount;

        // The stub model always answers "finance"; the fallback is "default".
        using var alone = await client.SendAsync(CreateChatRequest(message, model: "auto"));
        Assert.Equal("default", alone.Headers.GetValues("X-OpenAgent-Selected-Agent-Id").Single());
        Assert.Equal(before, _fixture.PrimaryEngine.DecisionCount);

        using var first = await client.SendAsync(CreateChatRequest("find my invoice", model: "auto"));
        using var followUp = CreateChatRequest(message, model: "auto");
        followUp.Headers.Add("X-Session-Id", first.Headers.GetValues("X-Session-Id").Single());
        using var next = await client.SendAsync(followUp);

        Assert.Equal("finance", next.Headers.GetValues("X-OpenAgent-Selected-Agent-Id").Single());
        Assert.Equal(first.Headers.GetValues("X-Session-Id").Single(), next.Headers.GetValues("X-Session-Id").Single());
        Assert.Equal(before + 1, _fixture.PrimaryEngine.DecisionCount);
    }

    [Fact]
    public async Task ChatCompletions_ContinuationWithoutHeaderKeepsSessionByFirstMessage()
    {
        using var factory = _fixture.CreateFactory(settings: ModelSettings());
        using var client = factory.CreateClient();
        int before = _fixture.PrimaryEngine.DecisionCount;
        object[] firstTurn = [new { role = "user", content = "continuation invoice question" }];

        using var first = await client.SendAsync(CreateChatRequest(messages: firstTurn, model: "auto"));
        using var second = await client.SendAsync(CreateChatRequest(model: "auto", messages:
        [
            firstTurn[0],
            new { role = "assistant", content = "answer" },
            new { role = "user", content = "and then?" }
        ]));

        Assert.Equal(first.Headers.GetValues("X-Session-Id").Single(), second.Headers.GetValues("X-Session-Id").Single());
        Assert.NotEqual(first.Headers.GetValues("X-Chat-Id").Single(), second.Headers.GetValues("X-Chat-Id").Single());
        Assert.Equal(before + 1, _fixture.PrimaryEngine.DecisionCount);
    }

    [Fact]
    public async Task Responses_PreviousResponseIdContinuesTheSession()
    {
        using var factory = _fixture.CreateFactory(settings: ModelSettings());
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(CreateJsonRequest("/v1/responses", new { model = "auto", input = "find my invoice" }));
        string chatId = first.Headers.GetValues("X-Chat-Id").Single();
        using var second = await client.SendAsync(CreateJsonRequest("/v1/responses",
            new { model = "auto", input = "more", previous_response_id = chatId }));

        Assert.StartsWith("resp_", chatId);
        Assert.Equal(first.Headers.GetValues("X-Session-Id").Single(), second.Headers.GetValues("X-Session-Id").Single());
        Assert.Equal("finance", second.Headers.GetValues("X-OpenAgent-Selected-Agent-Id").Single());
    }

    [Fact]
    public async Task ExplicitAgent_IdempotentRetryReturnsSameSessionAndReachesDownstreamOnce()
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
        Assert.Equal(first.Headers.GetValues("X-Session-Id"), second.Headers.GetValues("X-Session-Id"));
        Assert.Equal(first.Headers.GetValues("X-Chat-Id"), second.Headers.GetValues("X-Chat-Id"));
        Assert.Equal(before + 1, _fixture.PrimaryEngine.ChatRequestCount);
        Assert.Equal(decisions, _fixture.PrimaryEngine.DecisionCount);
    }

    [Fact]
    public async Task UnknownSession_IsRejectedWithoutModelOrDownstreamCall()
    {
        using var factory = _fixture.CreateFactory(settings: ModelSettings());
        using var client = factory.CreateClient();
        int before = _fixture.PrimaryEngine.ChatRequestCount;
        int decisions = _fixture.PrimaryEngine.DecisionCount;
        using var unknown = CreateChatRequest("hello");
        unknown.Headers.Add("X-Session-Id", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(unknown);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("conversation_not_found", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(before, _fixture.PrimaryEngine.ChatRequestCount);
        Assert.Equal(decisions, _fixture.PrimaryEngine.DecisionCount);
    }

    [Fact]
    public async Task SessionAgentCannotBeChangedByModel()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        using var first = await client.SendAsync(CreateChatRequest("hello", model: "default"));
        using var change = CreateChatRequest("hello", model: "finance");
        change.Headers.Add("X-Session-Id", first.Headers.GetValues("X-Session-Id").Single());

        using var response = await client.SendAsync(change);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ModelUnavailable_UsesAuthorizedFallback()
    {
        var settings = ModelSettings();
        settings["RouterSettings:DecisionModel:Endpoint"] = "http://127.0.0.1:1/v1/decide";
        using var factory = _fixture.CreateFactory(settings: settings);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateChatRequest("find invoice", model: "auto"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("default", response.Headers.GetValues("X-OpenAgent-Selected-Agent-Id").Single());
        Assert.Equal("Degraded", await client.GetStringAsync("/ready"));
        Assert.Equal("Healthy", await client.GetStringAsync("/health"));
    }

    [Fact]
    public async Task Models_ListsAutoAndTheDownstreamAgents()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        AddAuthentication(request);

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["auto", "default", "finance"],
            body.RootElement.GetProperty("data").EnumerateArray().Select(model => model.GetProperty("id").GetString()));
    }

    [RedisFact]
    public async Task SharedRedis_ContinuesSessionAcrossRouterRestartWithoutReselection()
    {
        var settings = ModelSettings();
        settings["ConnectionStrings:Redis"] = Environment.GetEnvironmentVariable("ROUTER_TEST_REDIS");
        string session;
        int decisions = _fixture.PrimaryEngine.DecisionCount;
        using (var firstRouter = _fixture.CreateFactory(settings: settings))
        using (var client = firstRouter.CreateClient())
        {
            using var response = await client.SendAsync(CreateChatRequest("invoice", model: "auto"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            session = response.Headers.GetValues("X-Session-Id").Single();
        }
        using var restartedRouter = _fixture.CreateFactory(settings: settings);
        using var restartedClient = restartedRouter.CreateClient();
        using var followup = CreateChatRequest("write code", model: "auto");
        followup.Headers.Add("X-Session-Id", session);

        using var next = await restartedClient.SendAsync(followup);

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal("finance", _fixture.PrimaryEngine.LastModel);
        Assert.Equal(session, _fixture.PrimaryEngine.LastSessionId);
        Assert.Equal(decisions + 1, _fixture.PrimaryEngine.DecisionCount);
    }

    [Fact]
    public async Task DuplicateStream_ReturnsConflictWithOriginalSessionId()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        using var request = CreateChatRequest("stream retry", stream: true);
        request.Headers.Add("Idempotency-Key", "stream-key");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var first = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        string id = first.Headers.GetValues("X-Session-Id").Single();
        using var retry = CreateChatRequest("stream retry", stream: true);
        retry.Headers.Add("Idempotency-Key", "stream-key");

        using var second = await client.SendAsync(retry, cancel.Token);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(id, second.Headers.GetValues("X-Session-Id").Single());
        cancel.Cancel();
    }

    [Fact]
    public async Task RepeatedPrompt_CreatesIndependentSessions()
    {
        int requestCount = _fixture.PrimaryEngine.ChatRequestCount;
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(CreateChatRequest("cacheable query"));
        using var second = await client.SendAsync(CreateChatRequest("cacheable query"));

        Assert.Equal(requestCount + 2, _fixture.PrimaryEngine.ChatRequestCount);
        Assert.NotEqual(first.Headers.GetValues("X-Session-Id").Single(), second.Headers.GetValues("X-Session-Id").Single());
    }

    [Fact]
    public async Task Chat_SseClientCancellation_ReachesDownstream()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        using var request = CreateChatRequest("stream response", stream: true);
        using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
        await using Stream stream = await response.Content.ReadAsStreamAsync(requestCancellation.Token);
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
        // 用户停止生成（客户端断开 SSE）是正常取消：一次断开不得把下游计为故障并隔离 30 秒，
        // 否则紧随其后的请求被 503。断开后立即再发一次普通对话必须成功。
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        using (var aborting = CreateChatRequest("to be cancelled", stream: true))
        using (var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            using var response = await client.SendAsync(aborting, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
            await using Stream stream = await response.Content.ReadAsStreamAsync(requestCancellation.Token);
            using var reader = new StreamReader(stream);
            await reader.ReadLineAsync(requestCancellation.Token);
            await reader.ReadLineAsync(requestCancellation.Token);

            requestCancellation.Cancel();
            response.Dispose();
            using var propagationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _fixture.PrimaryEngine.WaitForSseCancellationAsync(propagationTimeout.Token);
        }

        using var followup = CreateChatRequest("after cancellation");
        using var next = await client.SendAsync(followup);

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Contains("primary-engine", await next.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
        string? message = null,
        string path = "/v1/chat/completions",
        string model = "default",
        bool stream = false,
        object[]? messages = null)
    {
        object content = path switch
        {
            "/v1/responses" => new { model, input = message ?? "hello", stream },
            _ => new { model, messages = messages ?? [new { role = "user", content = message ?? "hello" }], stream }
        };
        return CreateJsonRequest(path, content);
    }

    private static HttpRequestMessage CreateJsonRequest(string path, object content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(content) };
        AddAuthentication(request);
        return request;
    }

    private static void AddAuthentication(HttpRequestMessage request)
    {
        string credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("admin:admin"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }
}
