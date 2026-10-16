using Microsoft.AspNetCore.Mvc;

namespace OpenAgent.Router.Endpoints;

/// <summary>
/// The error envelope the OpenAI SDKs parse: <c>{"error":{"message","type","param","code"}}</c>.
/// Router answers every failure on the standard <c>/v1</c> endpoints with it.
/// </summary>
internal static class OpenAiError
{
    internal static bool AppliesTo(PathString path) => path.StartsWithSegments("/v1");

    internal static object From(ProblemDetails problem)
    {
        int status = problem.Status ?? StatusCodes.Status500InternalServerError;
        return new
        {
            error = new
            {
                message = problem.Detail ?? problem.Title ?? "The request failed.",
                type = TypeOf(status),
                param = (string?)null,
                code = CodeOf(problem),
                trace_id = problem.Extensions.TryGetValue("traceId", out object? traceId) ? traceId?.ToString() : null
            }
        };
    }

    internal static string TypeOf(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "authentication_error",
        StatusCodes.Status403Forbidden => "permission_error",
        StatusCodes.Status429TooManyRequests => "rate_limit_error",
        >= 500 => "server_error",
        _ => "invalid_request_error"
    };

    /// <summary>The last segment of the problem type URI in snake_case, e.g. <c>conversation_not_found</c>.</summary>
    internal static string CodeOf(ProblemDetails problem)
    {
        string segment = (problem.Type ?? string.Empty).TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        return segment.Length > 0 ? segment.Replace('-', '_') : TypeOf(problem.Status ?? 500);
    }
}
