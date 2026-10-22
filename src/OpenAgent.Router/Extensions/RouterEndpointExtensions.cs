using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Hosting;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Security;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router;

public static class RouterEndpointExtensions
{
    public static IEndpointRouteBuilder MapRouterEndpoints(this IEndpointRouteBuilder app)
    {
        var httpClient = new HttpMessageInvoker(HttpClientSecurity.CreateSocketsHttpHandler(
            app.ServiceProvider.GetRequiredService<IConfiguration>(),
            TimeSpan.FromSeconds(15)));
        var requestConfig = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(100) };

        foreach (ChatProtocol protocol in Enum.GetValues<ChatProtocol>())
        {
            app.MapPost(ChatProtocols.Path(protocol), (
                HttpContext context,
                IAgentSelectionService selectionService,
                ChatSessionTracker sessions,
                IAgentProviderRegistry providers,
                IAgentForwarder agentForwarder,
                IAgentUserContext userContext,
                ILogger<Program> logger,
                CancellationToken cancellationToken) =>
                ChatEndpointHandler.HandleAsync(
                    protocol, context, selectionService, sessions, providers, agentForwarder,
                    userContext, logger, cancellationToken))
                .WithName($"RouterChat{protocol}")
                .WithTags("Chat");
        }

        app.MapGet("/v1/models", (
            IAgentCatalogService catalog,
            IAgentUserContext userContext,
            HttpContext context,
            CancellationToken cancellationToken) =>
            ModelCatalogEndpointHandler.HandleAsync(catalog, userContext, context, cancellationToken))
            .WithName("RouterListModels")
            .WithTags("Models");
        app.MapPost("/v1/router/feedback", (
            RoutingFeedbackRequest request,
            HttpContext context,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            RoutingFeedbackEndpointHandler.HandleAsync(
                request,
                context,
                services.GetRequiredService<IAgentUserContext>(),
                services.GetRequiredService<IConversationProviderResolver>(),
                services.GetRequiredService<IAgentCatalogService>(),
                services.GetRequiredService<IRoutingQualitySampleStore>(),
                services.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpenAgent.Router.Options.RoutingQualityOptions>>(),
                cancellationToken))
            .WithName("RouterRoutingFeedback")
            .WithTags("Routing Quality");
        app.MapGet("/api/v1/agent/me", (
            HttpContext context,
            IHttpForwarder forwarder,
            IAgentUserContext userContext,
            IRouteTable routeTable,
            ILogger<Program> logger) =>
            GatewayProxyHandler.HandleAsync(
                context,
                forwarder,
                userContext,
                routeTable,
                logger,
                httpClient,
                requestConfig,
                requireAuthentication: true))
            .WithName("RouterCurrentUser")
            .WithTags("Agent Catalog");
        // File assets are owned by Engine, but clients use the Router as their
        // single API origin. Preserve multipart request bodies and binary
        // responses by forwarding every file method through YARP.
        app.MapMethods(
            "/api/v1/agent/files/{**path}",
            [HttpMethods.Get, HttpMethods.Post],
            (
                HttpContext context,
                IHttpForwarder forwarder,
                IAgentUserContext userContext,
                IRouteTable routeTable,
                ILogger<Program> logger) =>
                GatewayProxyHandler.HandleAsync(
                    context,
                    forwarder,
                    userContext,
                    routeTable,
                    logger,
                    httpClient,
                    requestConfig,
                    requireAuthentication: true))
            .WithName("RouterFiles")
            .WithTags("Files");
        IHostEnvironment environment = app.ServiceProvider.GetRequiredService<IHostEnvironment>();
        if (environment.IsDevelopment())
        {
            // Basic authentication is a local-development convenience and does not
            // establish a production authorization boundary. Keep both the login
            // endpoint and the management proxy unreachable outside Development.
            app.MapMethods(
                "/api/v1/admin/{**path}",
                [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete, HttpMethods.Patch],
                (
                    HttpContext context,
                    IHttpForwarder forwarder,
                    IAgentUserContext userContext,
                    IRouteTable routeTable,
                    ILogger<Program> logger) =>
                    GatewayProxyHandler.HandleAsync(
                        context,
                        forwarder,
                        userContext,
                        routeTable,
                        logger,
                        httpClient,
                        requestConfig,
                        requireAuthentication: true))
                .WithName("RouterAdminProxy")
                .WithTags("Admin");
        }
        return app;
    }
}
