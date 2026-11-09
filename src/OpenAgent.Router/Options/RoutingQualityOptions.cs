namespace OpenAgent.Router.Options;

internal sealed class RoutingQualityOptions
{
    internal const string SectionName = "RouterSettings:RoutingQuality";

    public bool Enabled { get; set; } = true;
    public int SampleRetentionDays { get; set; } = 30;
    public int MaxSamplesPerTenant { get; set; } = 1000;
    public int MaxMessageCharacters { get; set; } = 4000;
    public bool IncludeMessageContent { get; set; }

    internal static bool IsValid(RoutingQualityOptions options) =>
        options.SampleRetentionDays is > 0 and <= 365
        && options.MaxSamplesPerTenant is > 0 and <= 100000
        && options.MaxMessageCharacters is > 0 and <= 16000;
}
