using OpenAgent.Contracts.Security;
using OpenAgent.Router.Hosting;
using OpenAgent.Router.Hosting.Authentication;
using OpenAgent.Router;
using OpenAgent.Router.Security;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Models;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment()
    && string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Redis")))
{
    throw new InvalidOperationException("Production Router requires ConnectionStrings:Redis for shared conversation state.");
}

// 上传请求先过 Router 再转发 Engine；Kestrel 默认 30 MB 请求体上限会在转发前拒绝
// 更大的附件，这里与 Engine 侧的 FileAssets 上限（默认 100 MB，单请求单文件）对齐
// 放开，并留出 multipart 开销余量。
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 256L * 1024 * 1024);

builder.Host.UseAgentSerilog("agent-router");

builder.Services.AddAgentHost(builder.Configuration, options =>
{
    options.ServiceName = "openagent-router";
    options.OpenTelemetrySource = "OpenAgent.Router";
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("ThirdPartyApiKeyEngine");
builder.Services.AddScoped<IThirdPartyApiKeyIdentityResolver, EngineApiKeyIdentityResolver>();
builder.Services.AddAuthorization();

// IAgentUserContext is populated by JwtUserContextMiddleware (registered below in the pipeline).
// The DI factory retrieves the context that the middleware stored in HttpContext.Items.
builder.Services.AddScoped<IAgentUserContext>(sp =>
{
    var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    if (httpContext?.Items.TryGetValue("AgentUserContext", out var ctx) == true && ctx is IAgentUserContext userContext)
    {
        return userContext;
    }

    // Fallback for requests that bypass the middleware (e.g., health checks)
    return new AgentUserContext
    {
        UserId = "anonymous",
        TenantId = null,
        Groups = new List<string>(),
        Roles = new List<string>(),
        Claims = new Dictionary<string, string>(),
        Audience = new List<string> { "router" },
        IsAuthenticated = false
    };
});

builder.Services.AddRouterRuntime(builder.Configuration);
builder.Services.AddSingleton<IAgentVisibilityService, AgentVisibilityService>();
builder.Services.AddSingleton<IAgentAccessControl, AgentAccessControl>();
builder.Services.AddAgentErrorHandling();

builder.Services.AddHttpForwarder();

builder.Services.AddHealthChecks()
    .AddCheck<RouterHealthCheck>("router", tags: new[] { "live" })
    .AddCheck<RouterReadyCheck>("router-ready", tags: new[] { "ready" });

var app = builder.Build();

app.UseAgentHost(builder.Configuration);
app.UseAgentErrorHandling();
app.UseMiddleware<OpenAiExceptionMiddleware>();
app.UseMiddleware<JwtUserContextMiddleware>();
app.UseWhen(
    context => ChatProtocols.IsChatPath(context.Request.Path),
    branch =>
    {
        branch.UseMiddleware<RateLimitingMiddleware>();
        branch.UseMiddleware<IdempotencyMiddleware>();
    });
app.MapAgentAuthenticationEndpoints();
app.MapRouterEndpoints();

app.Run();
