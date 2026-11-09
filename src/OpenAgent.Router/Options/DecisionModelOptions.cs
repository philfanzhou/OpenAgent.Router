namespace OpenAgent.Router.Options;

internal sealed class DecisionModelOptions
{
    internal const string SectionName = "RouterSettings:DecisionModel";

    // The full jev decisions URL, e.g. http://192.168.2.159:7860/v1/decide.
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyFile { get; set; } = string.Empty;
    public int MaxPromptCharacters { get; set; } = 16000;
    // The model rejects (HTTP 400, no truncation) any request over 8192 tokens, counting the message,
    // instructions and every candidate. Treated as 1 char = 1 token, the worst case for CJK text.
    public int MaxInputCharacters { get; set; } = 7500;
    public int MaxCandidates { get; set; } = 62;
    public int MaxDescriptionCharacters { get; set; } = 2000;
    public int MaxResponseBytes { get; set; } = 16384;

    internal static bool IsValid(DecisionModelOptions options) =>
        Uri.TryCreate(options.Endpoint, UriKind.Absolute, out Uri? endpoint)
        && endpoint.Scheme is "http" or "https"
        && string.IsNullOrEmpty(endpoint.UserInfo)
        && (string.IsNullOrEmpty(options.ApiKeyFile) || File.Exists(options.ApiKeyFile))
        && options.MaxPromptCharacters is > 0 and <= 1000000 && options.MaxInputCharacters > 0 && options.MaxCandidates is > 0 and <= 62
        && options.MaxDescriptionCharacters is > 0 and <= 16000
        && options.MaxResponseBytes is > 0 and <= 1048576;
}
