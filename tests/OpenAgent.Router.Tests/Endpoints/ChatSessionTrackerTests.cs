using System.Text;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ChatSessionTrackerTests
{
    private static readonly AgentProviderRequestContext Context =
        new(new AgentUserContext { UserId = "user-1", TenantId = "tenant-1", IsAuthenticated = true });

    private readonly InMemoryConversationResolver _resolver = new();
    private readonly ChatSessionTracker _tracker;

    public ChatSessionTrackerTests() => _tracker = new ChatSessionTracker(_resolver);

    private static ChatRequestInfo Info(ChatProtocol protocol, string json) =>
        ChatRequestParser.Parse(protocol, Encoding.UTF8.GetBytes(json));

    private const string FirstTurn = "{\"messages\":[{\"role\":\"user\",\"content\":\"refund\"}]}";
    private const string SecondTurn =
        "{\"messages\":[{\"role\":\"user\",\"content\":\"refund\"},{\"role\":\"assistant\",\"content\":\"ok\"},{\"role\":\"user\",\"content\":\"and?\"}]}";

    [Fact]
    public async Task Header_WinsOverEverythingElse()
    {
        var info = Info(ChatProtocol.Responses, "{\"input\":\"x\",\"previous_response_id\":\"resp_1\"}");
        Assert.Equal("s-header", await _tracker.FindSessionAsync(Context, info, " s-header ", CancellationToken.None));
    }

    [Fact]
    public async Task NewConversation_HasNoSession()
    {
        Assert.Null(await _tracker.FindSessionAsync(Context, Info(ChatProtocol.ChatCompletions, FirstTurn), null, CancellationToken.None));
    }

    [Fact]
    public async Task ChatCompletions_ContinuationFindsSessionByFirstMessageFingerprint()
    {
        var first = Info(ChatProtocol.ChatCompletions, FirstTurn);
        await _tracker.RememberAsync(Context, ChatProtocol.ChatCompletions, first,
            new AgentSelection("finance", "partner", "session-1"), "chatcmpl-1", CancellationToken.None);

        string? session = await _tracker.FindSessionAsync(
            Context, Info(ChatProtocol.ChatCompletions, SecondTurn), null, CancellationToken.None);

        Assert.Equal("session-1", session);
    }

    [Fact]
    public async Task Fingerprint_IsPerUser()
    {
        await _tracker.RememberAsync(Context, ChatProtocol.ChatCompletions, Info(ChatProtocol.ChatCompletions, FirstTurn),
            new AgentSelection("finance", "partner", "session-1"), "c", CancellationToken.None);
        var other = new AgentProviderRequestContext(new AgentUserContext { UserId = "user-2", TenantId = "tenant-1", IsAuthenticated = true });

        Assert.Null(await _tracker.FindSessionAsync(other, Info(ChatProtocol.ChatCompletions, SecondTurn), null, CancellationToken.None));
    }

    [Fact]
    public async Task Responses_PreviousResponseIdFindsSessionOfIssuedChatId()
    {
        var first = Info(ChatProtocol.Responses, "{\"input\":\"hi\"}");
        await _tracker.RememberAsync(Context, ChatProtocol.Responses, first,
            new AgentSelection("finance", "partner", "session-9"), "resp_abc", CancellationToken.None);

        string? session = await _tracker.FindSessionAsync(Context,
            Info(ChatProtocol.Responses, "{\"input\":\"more\",\"previous_response_id\":\"resp_abc\"}"), null, CancellationToken.None);

        Assert.Equal("session-9", session);
    }

    [Fact]
    public async Task Responses_UnknownPreviousResponseId_IsNotFound()
    {
        var exception = await Assert.ThrowsAsync<AgentRoutingException>(() => _tracker.FindSessionAsync(Context,
            Info(ChatProtocol.Responses, "{\"input\":\"more\",\"previous_response_id\":\"resp_unknown\"}"), null, CancellationToken.None));
        Assert.Equal(404, exception.StatusCode);
    }
}
