namespace OpenAgent.Router.Models;

/// <summary>The standard chat wire protocols Router accepts and forwards unchanged.</summary>
public enum ChatProtocol
{
    ChatCompletions,
    Responses,
    Messages
}

public static class ChatProtocols
{
    public static string Path(ChatProtocol protocol) => protocol switch
    {
        ChatProtocol.ChatCompletions => "/v1/chat/completions",
        ChatProtocol.Responses => "/v1/responses",
        ChatProtocol.Messages => "/v1/messages",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    /// <summary>Prefix of the Router-issued chat ID, matching each protocol's own response ID style.</summary>
    public static string ChatIdPrefix(ChatProtocol protocol) => protocol switch
    {
        ChatProtocol.ChatCompletions => "chatcmpl-",
        ChatProtocol.Responses => "resp_",
        ChatProtocol.Messages => "msg_",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    public static bool IsChatPath(PathString path) =>
        Enum.GetValues<ChatProtocol>().Any(protocol => path.Equals(Path(protocol), StringComparison.OrdinalIgnoreCase));
}
