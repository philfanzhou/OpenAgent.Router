namespace OpenAgent.Router.Endpoints;

/// <summary>
/// Recognizes bare acknowledgements such as "ok", "好的，继续" or "thanks". They carry no topic, so asking the
/// decision model to pick an agent for them only yields a random one; they must follow their session instead.
/// </summary>
internal static class TrivialMessage
{
    private const int MaxTokens = 4;

    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "ok", "okay", "k", "kk", "yes", "yeah", "yep", "yup", "sure", "fine", "no", "nope", "thanks", "thank",
        "you", "thx", "continue", "go", "on", "ahead", "next", "proceed", "please", "done", "got", "it", "great",
        "good", "cool", "nice", "right", "alright", "understood"
    };

    // Longest first so "好的" is taken whole rather than as "好" + "的".
    private static readonly string[] Phrases =
    [
        "没问题", "知道了", "辛苦了", "麻烦了", "就这样", "下一步", "好的", "好吧", "好嘞", "行吧", "可以", "嗯嗯",
        "对的", "是的", "没错", "继续", "接着", "谢谢", "感谢", "多谢", "收到", "明白", "了解", "确认", "不用",
        "不是", "算了", "完成", "好", "行", "嗯", "哦", "噢", "对", "是", "不", "否", "请", "吧", "啊", "呢", "呀", "的"
    ];

    internal static bool IsTrivial(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        string[] tokens = message.Split(
            message.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length is > 0 and <= MaxTokens && tokens.All(IsAcknowledgement);
    }

    private static bool IsAcknowledgement(string token)
    {
        if (Words.Contains(token))
        {
            return true;
        }

        // Chinese has no word separators: a token is an acknowledgement when it is made of known phrases only.
        string rest = token;
        foreach (string phrase in Phrases)
        {
            rest = rest.Replace(phrase, string.Empty, StringComparison.Ordinal);
        }
        return rest.Length == 0;
    }
}
