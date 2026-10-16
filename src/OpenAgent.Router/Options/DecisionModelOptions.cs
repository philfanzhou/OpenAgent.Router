namespace OpenAgent.Router.Options;

internal sealed class DecisionModelOptions
{
    internal const string SectionName = "RouterSettings:DecisionModel";

    // The full jev decisions URL, e.g. http://192.168.20.31:7860/v1/decisions.
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyFile { get; set; } = string.Empty;
    public int MaxPromptCharacters { get; set; } = 16000;
    public int MaxCandidates { get; set; } = 100;
    public int MaxDescriptionCharacters { get; set; } = 2000;
    public int MaxResponseBytes { get; set; } = 16384;

    internal static bool IsValid(DecisionModelOptions options) =>
        Uri.TryCreate(options.Endpoint, UriKind.Absolute, out Uri? endpoint)
        && endpoint.Scheme is "http" or "https"
        && string.IsNullOrEmpty(endpoint.UserInfo)
        && (string.IsNullOrEmpty(options.ApiKeyFile) || File.Exists(options.ApiKeyFile))
        && options.MaxPromptCharacters is > 0 and <= 1000000 && options.MaxCandidates is > 0 and <= 1000
        && options.MaxDescriptionCharacters is > 0 and <= 16000
        && options.MaxResponseBytes is > 0 and <= 1048576;
}
