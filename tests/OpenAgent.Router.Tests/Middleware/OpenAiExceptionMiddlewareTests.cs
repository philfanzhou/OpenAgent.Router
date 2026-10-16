using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Hosting.Errors;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Tests.Endpoints;
using Xunit;

namespace OpenAgent.Router.Tests.Middleware;

public class OpenAiExceptionMiddlewareTests
{
    private static OpenAiExceptionMiddleware Create(RequestDelegate next) => new(
        next,
        new AgentExceptionMapper(Microsoft.Extensions.Options.Options.Create(new AgentExceptionHandlingOptions())),
        NullLogger<OpenAiExceptionMiddleware>.Instance);

    [Fact]
    public async Task AgentException_BecomesOpenAiErrorWithMappedStatus()
    {
        var context = OpenAiErrorTests.CreateContext("/v1/chat/completions");
        var middleware = Create(_ => throw new AgentException(AgentErrorCode.TenantNotFound, "tenant required"));

        await middleware.InvokeAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(OpenAiErrorTests.ReadBody(context));
        JsonElement error = body.RootElement.GetProperty("error");
        Assert.Equal("tenant required", error.GetProperty("message").GetString());
        Assert.Equal("tenant_not_found", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnexpectedException_DoesNotLeakItsMessage()
    {
        var context = OpenAiErrorTests.CreateContext("/v1/responses");
        var middleware = Create(_ => throw new InvalidOperationException("connection string secret"));

        await middleware.InvokeAsync(context);

        Assert.Equal(500, context.Response.StatusCode);
        string body = OpenAiErrorTests.ReadBody(context);
        Assert.DoesNotContain("secret", body);
        using JsonDocument parsed = JsonDocument.Parse(body);
        Assert.Equal("server_error", parsed.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task NonV1Path_LeavesTheExceptionToTheSharedHandler()
    {
        var context = OpenAiErrorTests.CreateContext("/api/v1/agent/files/x");
        var middleware = Create(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
    }
}
