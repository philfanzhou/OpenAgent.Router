using System.Text;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ChatRequestParserTests
{
    private static ChatRequestInfo Parse(ChatProtocol protocol, string json) =>
        ChatRequestParser.Parse(protocol, Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ChatCompletions_ReadsLastAndFirstUserTextAndTurns()
    {
        ChatRequestInfo info = Parse(ChatProtocol.ChatCompletions,
            "{\"model\":\"auto\",\"stream\":true,\"messages\":[" +
            "{\"role\":\"system\",\"content\":\"be brief\"}," +
            "{\"role\":\"user\",\"content\":\"first\"}," +
            "{\"role\":\"assistant\",\"content\":\"ok\"}," +
            "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"second\"},{\"type\":\"image_url\"}]}]}");

        Assert.Equal("auto", info.Model);
        Assert.True(info.Stream);
        Assert.Equal("second", info.LastUserText);
        Assert.Equal("first", info.FirstUserText);
        Assert.True(info.HasAssistantTurn);
    }

    [Fact]
    public void Messages_ReadsContentBlocks()
    {
        ChatRequestInfo info = Parse(ChatProtocol.Messages,
            "{\"model\":\"hr\",\"system\":\"s\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"leave?\"}]}]}");

        Assert.Equal("leave?", info.LastUserText);
        Assert.False(info.HasAssistantTurn);
        Assert.False(info.Stream);
    }

    [Fact]
    public void Responses_ReadsStringInputAndPreviousResponseId()
    {
        ChatRequestInfo info = Parse(ChatProtocol.Responses,
            "{\"model\":\"auto\",\"input\":\"hello\",\"previous_response_id\":\"resp_1\"}");

        Assert.Equal("hello", info.LastUserText);
        Assert.Equal("resp_1", info.PreviousResponseId);
    }

    [Fact]
    public void Responses_ReadsItemArrayInput()
    {
        ChatRequestInfo info = Parse(ChatProtocol.Responses,
            "{\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"hi\"}]}]}");

        Assert.Equal("hi", info.LastUserText);
        Assert.Null(info.Model);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void InvalidBody_IsRejectedAsBadRequest(string body)
    {
        var exception = Assert.Throws<AgentRoutingException>(() => Parse(ChatProtocol.ChatCompletions, body));
        Assert.Equal(400, exception.StatusCode);
    }
}
