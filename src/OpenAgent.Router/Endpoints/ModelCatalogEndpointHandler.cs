using OpenAgent.Contracts.Security;
using OpenAgent.Router.Hosting.Errors;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

/// <summary>
/// <c>GET /v1/models</c>: the agents the caller may use, as standard model objects.
/// <c>auto</c> asks Router to pick the agent from the message.
/// </summary>
internal static class ModelCatalogEndpointHandler
{
    internal static async Task<IResult> HandleAsync(
        IAgentCatalogService catalog,
        IAgentUserContext userContext,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated)
        {
            return RouterProblem.Result(AgentProblemDetails.AuthenticationRequired(
                "Authentication is required to list models.", context));
        }
        if (string.IsNullOrWhiteSpace(userContext.TenantId))
        {
            return RouterProblem.Result(RouterProblem.From(new AgentRoutingException(
                StatusCodes.Status400BadRequest, RouterErrorCodes.InvalidTenant, "Tenant ID is required"), context));
        }

        try
        {
            IReadOnlyList<AgentCatalogEntry> entries = await catalog.GetAuthorizedAsync(
                new AgentProviderRequestContext(userContext, context.Request.Headers.Authorization.FirstOrDefault()),
                cancellationToken).ConfigureAwait(false);
            // "type"/"display_name" and "owned_by"/"created" make the same object valid for both vendors' SDKs.
            var data = entries.Select(entry => Model(entry.Agent.AgentId, entry.Agent.Name, entry.Agent.Description, entry.ProviderId))
                .Prepend(Model("auto", "auto", "Router selects the agent from the message", "router"));
            return TypedResults.Ok(new { @object = "list", data, has_more = false });
        }
        catch (AgentRoutingException exception)
        {
            return RouterProblem.Result(RouterProblem.From(exception, context));
        }
    }

    private static object Model(string id, string name, string description, string owner) => new
    {
        id,
        @object = "model",
        type = "model",
        display_name = name,
        description,
        created = 0,
        owned_by = owner
    };
}
