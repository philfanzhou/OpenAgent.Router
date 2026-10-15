using System.Diagnostics;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Hosting.Errors;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Models;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal static class GatewayProxyHandler
{
    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        IHttpForwarder forwarder,
        IAgentUserContext userContext,
        IRouteTable routeTable,
        ILogger logger,
        HttpMessageInvoker httpClient,
        ForwarderRequestConfig requestConfig,
        bool requireAuthentication)
    {
        if (requireAuthentication && !userContext.IsAuthenticated)
        {
            return TypedResults.Problem(AgentProblemDetails.AuthenticationRequired(
                "Authentication is required.", context));
        }

        string? tenantId = userContext.IsAuthenticated
            ? userContext.TenantId
            : null;
        string? conversationId = userContext.IsAuthenticated
            ? context.Request.RouteValues["conversationId"]?.ToString()
                ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault()
            : null;
        string? targetEndpoint = routeTable.GetTargetEndpoint(
            "chat",
            tenantId,
            conversationId);
        IAgentProvider? conversationProvider = null;
        Uri? conversationTarget = null;
        if (context.Request.RouteValues.ContainsKey("conversationId")
            && context.RequestServices.GetService<IAgentSelectionService>() is { } selectionService)
        {
            try
            {
                AgentSelection? selection = await selectionService.SelectAsync(string.Empty, conversationId,
                    null, context.RequestAborted, context.Request.Headers.Authorization.FirstOrDefault())
                    .ConfigureAwait(false);
                var registry = context.RequestServices.GetRequiredService<IAgentProviderRegistry>();
                if (selection == null || !registry.TryGet(selection.ProviderId, out conversationProvider)
                    || conversationProvider == null) throw new AgentRoutingException(
                        StatusCodes.Status503ServiceUnavailable, RouterErrorCodes.AgentProviderUnavailable,
                        "Conversation Provider is unavailable");
                AgentForwardingTarget? target = await conversationProvider.ResolveConversationForwardingAsync(
                    $"{context.Request.Path}{context.Request.QueryString}", tenantId, conversationId!,
                    context.RequestAborted).ConfigureAwait(false);
                if (target == null) throw new AgentRoutingException(StatusCodes.Status501NotImplemented,
                    RouterErrorCodes.AgentProviderUnavailable, "Provider does not support this conversation operation");
                targetEndpoint = target.DestinationPrefix;
                conversationTarget = target.RequestUri;
                if (HttpMethods.IsDelete(context.Request.Method))
                {
                    var store = context.RequestServices.GetRequiredService<IConversationProviderStore>();
                    ConversationProviderAffinity affinity = await context.RequestServices
                        .GetRequiredService<IConversationProviderResolver>().ResolveAsync(
                            new AgentProviderRequestContext(userContext), conversationId!, context.RequestAborted)
                        .ConfigureAwait(false) ?? throw new AgentRoutingException(StatusCodes.Status404NotFound,
                            RouterErrorCodes.ConversationNotFound, "Conversation was not found");
                    // Tombstone before forwarding: a failed DELETE must never allow the ID to create a new conversation.
                    await store.SetAsync(tenantId!, conversationId!, affinity with { Deleted = true },
                        context.RequestAborted).WaitAsync(TimeSpan.FromSeconds(2), context.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (AgentRoutingException exception)
            {
                return TypedResults.Problem(RouterProblem.From(exception, context));
            }
            catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested
                && exception is StackExchange.Redis.RedisException or TimeoutException)
            {
                return TypedResults.Problem(RouterProblem.From(new AgentRoutingException(
                    StatusCodes.Status503ServiceUnavailable, RouterErrorCodes.ConversationStorageUnavailable,
                    "Conversation storage is unavailable"), context));
            }
        }
        if (string.IsNullOrWhiteSpace(targetEndpoint))
        {
            return TypedResults.Problem(AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/no-engine-available",
                "NoEngineAvailable",
                StatusCodes.Status503ServiceUnavailable,
                "No Engine is available to serve this request.",
                context.Request.Path,
                AgentProblemDetails.ResolveTraceId(context),
                ("errorCode", (int)AgentErrorCode.DependencyUnavailable)));
        }

        string targetUrl = conversationTarget?.ToString()
            ?? $"{targetEndpoint.TrimEnd('/')}{context.Request.Path}{context.Request.QueryString}";
        string traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        ForwarderError error;
        try
        {
            error = await forwarder.SendAsync(
                context,
                targetEndpoint,
                httpClient,
                requestConfig,
                (_, proxyRequest) => userContext.IsAuthenticated
                    ? ApplyAuthenticatedAsync(
                        proxyRequest,
                        new Uri(targetUrl),
                        traceId)
                    : ApplyAnonymousAsync(proxyRequest, new Uri(targetUrl), traceId)).ConfigureAwait(false);
        }
        catch
        {
            RouterMeter.RecordForward("other", succeeded: false);
            throw;
        }
        RouterMeter.RecordForward("other", error == ForwarderError.None);
        if (error == ForwarderError.None)
        {
            context.RequestServices.GetService<IEndpointHealthTracker>()?.ReportSuccess(targetEndpoint);
            return Results.Empty;
        }

        context.RequestServices.GetService<IEndpointHealthTracker>()?.ReportFailure(targetEndpoint);
        RouterLog.DownstreamQuarantined(logger, targetEndpoint);

        RouterLog.ForwardingFailed(
            logger,
            context.GetForwarderErrorFeature()?.Exception,
            error,
            context.Request.Path,
            targetEndpoint,
            targetUrl,
            userContext.UserId,
            tenantId,
            traceId);
        RouterMeter.RecordForwardingFailure("other", error.ToString());
        return TypedResults.Problem(AgentProblemDetails.Create(
            $"{AgentProblemDetails.TypePrefix}/forwarding-failed",
            "ForwardingFailed",
            StatusCodes.Status503ServiceUnavailable,
            $"Forwarding to the Engine failed ({error}).",
            context.Request.Path,
            AgentProblemDetails.ResolveTraceId(context),
            ("errorCode", (int)AgentErrorCode.DependencyUnavailable)));
    }

    private static ValueTask ApplyAuthenticatedAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        string traceId)
    {
        return ForwardingContextBuilder.ApplyAsync(
            proxyRequest,
            targetUri,
            traceId);
    }

    private static ValueTask ApplyAnonymousAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        string traceId)
    {
        proxyRequest.RequestUri = targetUri;
        if (!proxyRequest.Headers.Contains("X-Trace-Id"))
        {
            proxyRequest.Headers.TryAddWithoutValidation("X-Trace-Id", traceId);
        }
        return ValueTask.CompletedTask;
    }
}
