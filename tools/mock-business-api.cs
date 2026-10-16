#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net8.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
#:property TreatWarningsAsErrors=false
// Mock downstream business system speaking the OpenAgentEngine provider protocol.
// Run: dotnet run tools/mock-business-api.cs   (listens on http://localhost:5290)
// Status 2 = Published; TenantId must equal the caller's tenant (Development: "development").
const string Tenant = "development";

var agents = new[]
{
    new { tenantId = Tenant, agentId = "general", name = "通用助手", description = "通用问答、写作、翻译", status = 2, currentVersion = "1" },
    new { tenantId = Tenant, agentId = "finance", name = "财务助手", description = "报销、发票、订单退款、对账分析", status = 2, currentVersion = "1" },
    new { tenantId = Tenant, agentId = "hr", name = "人事助手", description = "请假、入职、薪资与考勤政策咨询", status = 2, currentVersion = "1" },
    new { tenantId = Tenant, agentId = "draft", name = "草稿助手", description = "未发布，不应出现在候选中", status = 1, currentVersion = "1" },
};

var app = WebApplication.CreateBuilder(args).Build();
app.Urls.Add("http://localhost:5290");

app.MapGet("/api/v1/agent/agents", () => agents);

// Echo; /chat/stream answers as SSE so streaming forwarding can be exercised.
app.MapPost("/api/v1/agent/chat", async (HttpRequest r) =>
    Results.Json(new { message = $"[{r.Headers["X-Agent-Id"]}/{r.Headers["X-Conversation-Id"]}] echo: {await new StreamReader(r.Body).ReadToEndAsync()}" }));

app.MapPost("/api/v1/agent/chat/stream", async (HttpRequest r, HttpResponse res) =>
{
    res.ContentType = "text/event-stream";
    string body = (await new StreamReader(r.Body).ReadToEndAsync()).Replace("\n", " ");
    foreach (string part in new[] { $"[{r.Headers["X-Agent-Id"]}/{r.Headers["X-Conversation-Id"]}] ", "echo: ", body })
    {
        await res.WriteAsync($"data: {part}\n\n");
        await res.Body.FlushAsync();
    }
});

app.Run();
