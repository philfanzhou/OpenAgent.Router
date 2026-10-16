using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Router.Hosting.Errors;
using OpenAgent.Router.Models;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace OpenAgent.Router.Endpoints;

/// <summary>
/// 把路由层异常转换为统一的 ProblemDetails（含 traceId/timestamp/code 扩展字段），
/// 与 Engine/Runner 的错误契约一致。标准 <c>/v1</c> 端点上的错误改用 OpenAI 的 error 信封，
/// 其余路径保持 problem+json；所有 Router 自己写出的错误都经 <see cref="Result"/> / <see cref="WriteAsync"/>。
/// </summary>
internal static class RouterProblem
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static ProblemDetails From(AgentRoutingException exception, HttpContext context) =>
        AgentProblemDetails.Create(
            $"{AgentProblemDetails.TypePrefix}/{exception.Code}",
            exception.Title,
            exception.StatusCode,
            exception.Message,
            context.Request.Path,
            AgentProblemDetails.ResolveTraceId(context));

    internal static IResult Result(ProblemDetails problem) => new ProblemResult(problem);

    internal static Task WriteAsync(HttpContext context, ProblemDetails problem, CancellationToken cancellationToken = default)
    {
        if (!OpenAiError.AppliesTo(context.Request.Path))
        {
            return AgentProblemDetails.WriteAsync(context, problem, cancellationToken);
        }

        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(OpenAiError.From(problem), JsonOptions), cancellationToken);
    }

    private sealed class ProblemResult(ProblemDetails problem) : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => problem.Status;

        public Task ExecuteAsync(HttpContext httpContext) => WriteAsync(httpContext, problem, httpContext.RequestAborted);
    }
}
