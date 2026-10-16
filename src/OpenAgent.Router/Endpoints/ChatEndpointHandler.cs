using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Hosting.Errors;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Endpoints;

/// <summary>
/// Serves the standard chat endpoints. Router picks the agent, issues the session and chat IDs,
/// rewrites <c>model</c> to the agent ID and forwards the otherwise untouched body.
/// </summary>
internal static class ChatEndpointHandler
{
    internal const string ChatHeader = "X-Chat-Id";
    private const string AutoModel = "auto";
    private const int MaxBodyBytes = 32 * 1024 * 1024;

    internal static async Task<IResult> HandleAsync(
        ChatProtocol protocol,
        HttpContext context,
        IAgentSelectionService selectionService,
        ChatSessionTracker sessions,
        IAgentProviderRegistry providers,
        IAgentForwarder agentForwarder,
        IAgentUserContext userContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (userContext == null || !userContext.IsAuthenticated)
        {
            RouterLog.UnauthenticatedRequest(
                logger, protocol.ToString(), context.Request.Method, context.Request.Path,
                Activity.Current?.Id ?? context.TraceIdentifier);
            return TypedResults.Problem(AgentProblemDetails.AuthenticationRequired(
                "Authentication is required to chat.", context));
        }

        string? authorization = context.Request.Headers.Authorization.FirstOrDefault();
        AgentProviderRequestContext requestContext = new(userContext, authorization, protocol);
        ChatRequestInfo info;
        AgentSelection? selection;
        string chatId = ChatProtocols.ChatIdPrefix(protocol) + Guid.NewGuid().ToString("N");
        try
        {
            info = ChatRequestParser.Parse(protocol, await ReadBodyAsync(context.Request, cancellationToken)
                .ConfigureAwait(false));
            string? sessionId = await sessions.FindSessionAsync(requestContext, info,
                context.Request.Headers[ChatSessionTracker.SessionHeader].FirstOrDefault(), cancellationToken)
                .ConfigureAwait(false);
            selection = await selectionService.SelectAsync(
                info.LastUserText,
                sessionId,
                string.IsNullOrWhiteSpace(info.Model) || info.Model.Equals(AutoModel, StringComparison.OrdinalIgnoreCase)
                    ? null : info.Model,
                cancellationToken,
                authorization,
                protocol).ConfigureAwait(false);
            if (selection != null)
            {
                await sessions.RememberAsync(requestContext, protocol, info, selection, chatId, cancellationToken)
                    .ConfigureAwait(false);
            }
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
        if (!providers.TryGet(selection.ProviderId, out IAgentProvider? provider) || provider == null)
        {
            return TypedResults.Problem(RouterProblem.From(new AgentRoutingException(
                StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.AgentProviderUnavailable,
                "Agent Provider is unavailable"), context));
        }

        string sessionIdOut = selection.ConversationId!;
        info.Root["model"] = selection.AgentId;
        byte[] forwarded = System.Text.Encoding.UTF8.GetBytes(info.Root.ToJsonString());
        context.Request.Headers[ChatSessionTracker.SessionHeader] = sessionIdOut;
        context.Request.Headers[ChatHeader] = chatId;
        SetResponseHeaders(context, sessionIdOut, chatId, selection.AgentId!);
        context.Response.OnStarting(() =>
        {
            SetResponseHeaders(context, sessionIdOut, chatId, selection.AgentId!);
            return Task.CompletedTask;
        });

        Stream original = context.Request.Body;
        await using MemoryStream body = new(forwarded);
        context.Request.Body = body;
        context.Request.ContentLength = forwarded.Length;
        try
        {
            await agentForwarder.ForwardAsync(
                context, provider, protocol, info.Stream, sessionIdOut, cancellationToken).ConfigureAwait(false);
        }
        finally { context.Request.Body = original; }
        return Results.Empty;
    }

    private static void SetResponseHeaders(HttpContext context, string sessionId, string chatId, string agentId)
    {
        context.Response.Headers[ChatSessionTracker.SessionHeader] = sessionId;
        context.Response.Headers[ChatHeader] = chatId;
        context.Response.Headers["X-OpenAgent-Selected-Agent-Id"] = agentId;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.Body.CanSeek) request.Body.Position = 0;
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                throw new AgentRoutingException(StatusCodes.Status413PayloadTooLarge,
                    RouterErrorCodes.InvalidRoutingRequest, "The request body is too large");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
