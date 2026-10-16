using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Hosting.Errors;
using OpenAgent.Router.Observability;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal static class ForwardingErrorHandler
{
    /// <summary>
    /// Standard SDKs only understand the downstream's own error shape, so a failure that happens
    /// after the response has started is left as a cut stream; before that it is a plain problem response.
    /// </summary>
    internal static async Task<IResult> HandleChatAsync(
        HttpContext context,
        string? action,
        ForwarderError error,
        string targetEndpoint,
        string targetUrl,
        IAgentUserContext userContext,
        string? tenantId,
        string traceId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var exception = context.GetForwarderErrorFeature()?.Exception;
        RouterLog.ForwardingFailed(
            logger, exception, error, context.Request.Path, targetEndpoint,
            targetUrl, userContext.UserId, tenantId, traceId);
        RouterMeter.RecordForwardingFailure(action ?? "chat", error.ToString());
        if (context.Response.HasStarted)
        {
            return Results.Empty;
        }

        bool timedOut = error == ForwarderError.RequestTimedOut;
        return RouterProblem.Result(AgentProblemDetails.Create(
            $"{AgentProblemDetails.TypePrefix}/{(timedOut ? "gateway-timeout" : "service-unavailable")}",
            timedOut ? "GatewayTimeout" : "ServiceUnavailable",
            timedOut ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status503ServiceUnavailable,
            timedOut ? "The request to the agent timed out. Please try again later."
                : "The agent is temporarily unavailable.",
            context.Request.Path,
            traceId,
            ("errorCode", (int)AgentErrorCode.DependencyUnavailable)));
    }
}
