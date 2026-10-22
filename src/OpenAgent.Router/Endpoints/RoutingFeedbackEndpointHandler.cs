using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Hosting.Errors;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Endpoints;

internal static class RoutingFeedbackEndpointHandler
{
    internal static async Task<IResult> HandleAsync(
        RoutingFeedbackRequest request,
        HttpContext context,
        IAgentUserContext userContext,
        IConversationProviderResolver conversations,
        IAgentCatalogService catalog,
        IRoutingQualitySampleStore samples,
        IOptions<RoutingQualityOptions> options,
        CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated)
        {
            return RouterProblem.Result(AgentProblemDetails.AuthenticationRequired(
                "Authentication is required to report routing feedback.", context));
        }

        RoutingQualityOptions settings = options.Value;
        string sessionId = request.SessionId?.Trim() ?? string.Empty;
        string expectedAgentId = request.ExpectedAgentId?.Trim() ?? string.Empty;
        string message = request.Message?.Trim() ?? string.Empty;
        string? reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (sessionId.Length == 0 || expectedAgentId.Length == 0 || message.Length == 0
            || message.Length > settings.MaxMessageCharacters || reason?.Length > 256)
        {
            return RouterProblem.Result(RouterProblem.From(new AgentRoutingException(
                StatusCodes.Status400BadRequest,
                RouterErrorCodes.InvalidRoutingRequest,
                "Session, expected Agent and a bounded message are required"), context));
        }

        string? authorization = context.Request.Headers.Authorization.FirstOrDefault();
        var requestContext = new AgentProviderRequestContext(userContext, authorization);
        try
        {
            ConversationProviderAffinity affinity = await conversations.ResolveAsync(
                requestContext, sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new AgentRoutingException(StatusCodes.Status404NotFound,
                    RouterErrorCodes.ConversationNotFound, "Conversation was not found");
            AgentCatalogEntry expected = await catalog.ResolveAsync(
                requestContext, expectedAgentId, cancellationToken).ConfigureAwait(false);
            if (string.Equals(affinity.AgentId, expected.Agent.AgentId, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentRoutingException(StatusCodes.Status400BadRequest,
                    RouterErrorCodes.InvalidRoutingRequest,
                    "Expected Agent must differ from the selected Agent");
            }

            var sample = new RoutingQualitySample(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                Hash(sessionId),
                affinity.AgentId!,
                expected.Agent.AgentId,
                Hash(message),
                settings.IncludeMessageContent ? message : null,
                reason);
            await samples.SaveAsync(userContext.TenantId!, sample, cancellationToken).ConfigureAwait(false);
            RouterMeter.RecordMisroute(affinity.AgentId!, expected.Agent.AgentId);
            return Results.Accepted(value: new { sample_id = sample.SampleId });
        }
        catch (AgentRoutingException exception)
        {
            return RouterProblem.Result(RouterProblem.From(exception, context));
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
