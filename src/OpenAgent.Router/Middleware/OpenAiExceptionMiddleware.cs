using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Hosting.Errors;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Router.Middleware;

/// <summary>
/// Turns unhandled exceptions on the standard <c>/v1</c> endpoints into OpenAI-style errors, so SDK clients
/// can parse them. Other paths fall through to the shared problem+json exception handler.
/// </summary>
internal sealed class OpenAiExceptionMiddleware(
    RequestDelegate next,
    AgentExceptionMapper mapper,
    ILogger<OpenAiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!OpenAiError.AppliesTo(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            string traceId = AgentProblemDetails.ResolveTraceId(context);
            var (status, problem) = mapper.Map(exception, traceId, context.Request.Path);
            if (exception is AgentException)
            {
                logger.LogDebug("Agent exception on {Path}: status {Status}, trace {TraceId}", context.Request.Path, status, traceId);
            }
            else
            {
                logger.LogError(exception, "Unhandled exception on {Path}: status {Status}, trace {TraceId}",
                    context.Request.Path, status, traceId);
            }
            // The mapper puts an unexpected exception's text into detail; do not hand that to API clients.
            if (status == StatusCodes.Status500InternalServerError)
            {
                problem.Detail = "An unexpected error occurred.";
            }
            await RouterProblem.WriteAsync(context, problem).ConfigureAwait(false);
        }
    }
}
