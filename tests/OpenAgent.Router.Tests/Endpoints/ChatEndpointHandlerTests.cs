using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ChatEndpointHandlerTests
{
    private const string Body =
        "{\"model\":\"auto\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"refund my order\"}]}";

    [Fact]
    public async Task AnonymousRequest_ReturnsUnauthorizedWithoutForwarding()
    {
        var forwarder = new RecordingForwarder();
        DefaultHttpContext context = CreateContext(Body);

        IResult result = await HandleAsync(ChatProtocol.ChatCompletions, context, new StubSelection(null), forwarder, Anonymous);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(0, forwarder.CallCount);
    }

    [Fact]
    public async Task AutoModel_SelectsByLastUserMessageAndForwardsRewrittenBodyWithIssuedIds()
    {
        var selection = new StubSelection(new AgentSelection("finance", "partner", "session-1"));
        var forwarder = new RecordingForwarder();
        DefaultHttpContext context = CreateContext(Body);

        await HandleAsync(ChatProtocol.ChatCompletions, context, selection, forwarder, Authenticated);

        Assert.Equal("refund my order", selection.Message);
        Assert.Null(selection.ExplicitAgentId);
        Assert.Equal(ChatProtocol.ChatCompletions, selection.Protocol);
        Assert.Equal("finance", JsonNode.Parse(forwarder.ForwardedBody!)!["model"]!.GetValue<string>());
        Assert.True(forwarder.Streaming);
        Assert.Equal("session-1", forwarder.SessionId);
        Assert.Equal("session-1", context.Request.Headers["X-Session-Id"].ToString());
        string chatId = context.Request.Headers["X-Chat-Id"].ToString();
        Assert.StartsWith("chatcmpl-", chatId);
        Assert.Equal(chatId, context.Response.Headers["X-Chat-Id"].ToString());
        Assert.Equal("session-1", context.Response.Headers["X-Session-Id"].ToString());
        Assert.Equal("finance", context.Response.Headers["X-OpenAgent-Selected-Agent-Id"].ToString());
    }

    [Fact]
    public async Task ExplicitModel_IsPassedAsAgentAndResponsesChatIdUsesRespPrefix()
    {
        var selection = new StubSelection(new AgentSelection("hr", "partner", "session-2"));
        DefaultHttpContext context = CreateContext("{\"model\":\"hr\",\"input\":\"leave policy\"}");

        await HandleAsync(ChatProtocol.Responses, context, selection, new RecordingForwarder(), Authenticated);

        Assert.Equal("hr", selection.ExplicitAgentId);
        Assert.StartsWith("resp_", context.Request.Headers["X-Chat-Id"].ToString());
    }

    [Fact]
    public async Task ClientSuppliedChatId_IsReplacedByIssuedId()
    {
        DefaultHttpContext context = CreateContext(Body);
        context.Request.Headers["X-Chat-Id"] = "forged";

        await HandleAsync(ChatProtocol.ChatCompletions, context,
            new StubSelection(new AgentSelection("finance", "partner", "s")), new RecordingForwarder(), Authenticated);

        Assert.NotEqual("forged", context.Request.Headers["X-Chat-Id"].ToString());
    }

    [Fact]
    public async Task RoutingFailure_ReturnsItsStatusWithoutForwarding()
    {
        var forwarder = new RecordingForwarder();
        DefaultHttpContext context = CreateContext(Body);

        IResult result = await HandleAsync(ChatProtocol.ChatCompletions, context,
            new StubSelection(new AgentRoutingException(StatusCodes.Status409Conflict, "conflict", "conflict")),
            forwarder, Authenticated);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal(0, forwarder.CallCount);
    }

    [Fact]
    public async Task UnknownProvider_ReturnsServiceUnavailable()
    {
        DefaultHttpContext context = CreateContext(Body);

        IResult result = await HandleAsync(ChatProtocol.ChatCompletions, context,
            new StubSelection(new AgentSelection("finance", "missing", "s")), new RecordingForwarder(), Authenticated);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    private static Task<IResult> HandleAsync(
        ChatProtocol protocol, HttpContext context, IAgentSelectionService selection,
        IAgentForwarder forwarder, IAgentUserContext user) =>
        ChatEndpointHandler.HandleAsync(
            protocol, context, selection, new ChatSessionTracker(new InMemoryConversationResolver()),
            new StubRegistry(new StubProvider("partner")), forwarder, user,
            NullLogger.Instance, CancellationToken.None);

    private static DefaultHttpContext CreateContext(string body)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static AgentUserContext Authenticated => new() { UserId = "user-1", TenantId = "tenant-1", IsAuthenticated = true };

    private static AgentUserContext Anonymous => new() { UserId = string.Empty, IsAuthenticated = false };

    private sealed class StubSelection(object? outcome) : IAgentSelectionService
    {
        public string? Message { get; private set; }
        public string? ExplicitAgentId { get; private set; }
        public ChatProtocol? Protocol { get; private set; }

        public Task<AgentSelection?> SelectAsync(string message, string? conversationId, string? explicitAgentId,
            CancellationToken cancellationToken, string? authenticationToken = null, ChatProtocol? protocol = null)
        {
            Message = message;
            ExplicitAgentId = explicitAgentId;
            Protocol = protocol;
            return outcome is AgentRoutingException exception
                ? Task.FromException<AgentSelection?>(exception)
                : Task.FromResult((AgentSelection?)outcome);
        }
    }

    private sealed class StubRegistry(IAgentProvider provider) : IAgentProviderRegistry
    {
        public IReadOnlyList<IAgentProvider> Providers => [provider];
        public IAgentProvider DefaultProvider => provider;

        public bool TryGet(string providerId, out IAgentProvider? resolved)
        {
            resolved = provider.Id == providerId ? provider : null;
            return resolved is not null;
        }
    }

    private sealed class RecordingForwarder : IAgentForwarder
    {
        public int CallCount { get; private set; }
        public bool Streaming { get; private set; }
        public string? SessionId { get; private set; }
        public string? ForwardedBody { get; private set; }

        public async Task ForwardAsync(HttpContext context, IAgentProvider provider, ChatProtocol protocol,
            bool streaming, string? sessionId, CancellationToken cancellationToken)
        {
            CallCount++;
            Streaming = streaming;
            SessionId = sessionId;
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            ForwardedBody = await reader.ReadToEndAsync(cancellationToken);
        }
    }

    private sealed class StubProvider(string id) : IAgentProvider
    {
        public string Id => id;
        public bool Supports(ChatProtocol protocol) => true;

        public Task<AgentProviderCatalog> GetAgentsAsync(AgentProviderRequestContext requestContext,
            CancellationToken cancellationToken) => Task.FromResult(new AgentProviderCatalog([]));

        public Task<AgentForwardingTarget?> ResolveForwardingAsync(ChatProtocol protocol, string? tenantId,
            string? sessionId, CancellationToken cancellationToken) => Task.FromResult<AgentForwardingTarget?>(null);

        public ValueTask ConfigureRequestAsync(HttpRequestMessage request, AgentForwardingTarget target,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
