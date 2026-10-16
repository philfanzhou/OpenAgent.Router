using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Hosting.Errors;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class OpenAiErrorTests
{
    [Theory]
    [InlineData(400, "invalid_request_error")]
    [InlineData(404, "invalid_request_error")]
    [InlineData(409, "invalid_request_error")]
    [InlineData(401, "authentication_error")]
    [InlineData(403, "permission_error")]
    [InlineData(429, "rate_limit_error")]
    [InlineData(500, "server_error")]
    [InlineData(503, "server_error")]
    public void TypeOf_MapsStatusToOpenAiErrorType(int status, string type) =>
        Assert.Equal(type, OpenAiError.TypeOf(status));

    [Fact]
    public async Task V1Path_IsWrittenAsOpenAiErrorEnvelope()
    {
        DefaultHttpContext context = CreateContext("/v1/chat/completions");
        var problem = AgentProblemDetails.Create(
            $"{AgentProblemDetails.TypePrefix}/conversation-not-found", "ConversationNotFound", 404,
            "Conversation was not found", "/v1/chat/completions", "trace-1");

        await RouterProblem.WriteAsync(context, problem);

        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        using JsonDocument body = JsonDocument.Parse(ReadBody(context));
        JsonElement error = body.RootElement.GetProperty("error");
        Assert.Equal("Conversation was not found", error.GetProperty("message").GetString());
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("conversation_not_found", error.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("param").ValueKind);
        Assert.Equal("trace-1", error.GetProperty("trace_id").GetString());
    }

    [Fact]
    public async Task OtherPaths_KeepProblemJson()
    {
        DefaultHttpContext context = CreateContext("/api/v1/agent/files/x");

        await RouterProblem.WriteAsync(context, AgentProblemDetails.NotFound("missing", context));

        Assert.Equal("application/problem+json", context.Response.ContentType);
    }

    [Fact]
    public async Task ProblemResult_ReportsItsStatusAndWritesOnExecute()
    {
        DefaultHttpContext context = CreateContext("/v1/models");
        IResult result = RouterProblem.Result(AgentProblemDetails.AuthenticationRequired("login", context));

        Assert.Equal(401, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        await result.ExecuteAsync(context);

        using JsonDocument body = JsonDocument.Parse(ReadBody(context));
        Assert.Equal("authentication_error", body.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    internal static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    internal static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
