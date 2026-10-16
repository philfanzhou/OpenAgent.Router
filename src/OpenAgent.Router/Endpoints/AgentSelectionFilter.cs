using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Hosting.Errors;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionFilter(
    IAgentSelectionService selectionService,
    IAgentUserContext userContext) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext invocationContext,
        EndpointFilterDelegate next)
    {
        HttpContext context = invocationContext.HttpContext;
        RouterMeter.RecordRequest(context.Request.RouteValues["action"]?.ToString());
        if (!userContext.IsAuthenticated)
        {
            return await next(invocationContext).ConfigureAwait(false);
        }

        ParsedChatRequest request;
        try
        {
            request = await ChatRequestReader.ReadAsync(
                context.Request,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or BadHttpRequestException)
        {
            return TypedResults.Problem(AgentProblemDetails.Invalid(
                "The request body must contain valid JSON.", context));
        }

        string? routingConversationId;
        AgentSelection? selection;
        try
        {
            routingConversationId = RoutingRequestNormalizer.Merge("conversationId", StringComparer.Ordinal,
                new[] { request.ConversationId }
                    .Concat(context.Request.Headers["X-Conversation-Id"]));
            string? explicitAgentId = RoutingRequestNormalizer.Merge("agentId", StringComparer.OrdinalIgnoreCase,
                new[] { request.AgentId }.Concat(context.Request.Headers["X-Agent-Id"]));
            selection = await selectionService.SelectAsync(
                request.Query,
                routingConversationId,
                explicitAgentId,
                context.RequestAborted,
                context.Request.Headers.Authorization.FirstOrDefault(),
                request.LlmProfileId).ConfigureAwait(false);
        }
        catch (AgentRoutingException exception)
        {
            return TypedResults.Problem(RouterProblem.From(exception, context));
        }
        if (selection == null)
        {
            return TypedResults.Problem(RouterProblem.From(new AgentRoutingException(
                StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.NoAgentAvailable,
                "No Agent could be selected"), context));
        }

        context.Features.Set(new AgentRoutingFeature(
            selection.ConversationId ?? routingConversationId,
            selection.ProviderId,
            selection.AgentId));
        if (!string.IsNullOrWhiteSpace(selection.AgentId))
        {
            context.Request.Headers["X-Agent-Id"] = selection.AgentId;
            context.Response.Headers["X-OpenAgent-Selected-Agent-Id"] = selection.AgentId;
        }

        string? conversationId = selection.ConversationId ?? routingConversationId;
        if (conversationId != null)
        {
            context.Request.Headers["X-Conversation-Id"] = conversationId;
            context.Response.Headers["X-Conversation-Id"] = conversationId;
        }
        context.Response.OnStarting(() =>
        {
            if (conversationId != null) context.Response.Headers["X-Conversation-Id"] = conversationId;
            if (selection.AgentId != null)
                context.Response.Headers["X-OpenAgent-Selected-Agent-Id"] = selection.AgentId;
            return Task.CompletedTask;
        });
        Stream original = context.Request.Body;
        await using Stream normalized = await RoutingRequestNormalizer.RewriteAsync(
            context.Request, conversationId, selection.AgentId, context.RequestAborted).ConfigureAwait(false);
        context.Request.Body = normalized;
        context.Request.ContentLength = normalized.Length;
        try { return await next(invocationContext).ConfigureAwait(false); }
        finally { context.Request.Body = original; }
    }
}
