namespace OpenAgent.Router.Options;

internal sealed class ConversationRoutingOptions
{
    internal const string SectionName = "RouterSettings:Conversations";
    public int RetentionDays { get; set; } = 30;
    public int StorageTimeoutMs { get; set; } = 2000;
}
