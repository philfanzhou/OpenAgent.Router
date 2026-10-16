#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net8.0
#:property PublishAot=false
#:property TreatWarningsAsErrors=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Mock downstream business system speaking the standard OpenAI / Anthropic APIs, as Router expects.
// Run: dotnet run tools/mock-business-api.cs   (listens on http://localhost:5290)
var agents = new[]
{
    new { id = "general", @object = "model", display_name = "通用助手", description = "通用问答、写作、翻译" },
    new { id = "finance", @object = "model", display_name = "财务助手", description = "报销、发票、订单退款、对账分析" },
    new { id = "hr", @object = "model", display_name = "人事助手", description = "请假、入职、薪资与考勤政策咨询" },
};

var app = WebApplication.CreateBuilder(args).Build();
app.Urls.Add("http://localhost:5290");

app.MapGet("/ready", () => Results.Ok());
app.MapGet("/v1/models", () => new { @object = "list", data = agents });

// Echo the routing facts back. Streaming answers with plain SSE chunks, enough to exercise forwarding.
foreach (string path in new[] { "/v1/chat/completions", "/v1/responses", "/v1/messages" })
{
    app.MapPost(path, async (HttpRequest r, HttpResponse res) =>
    {
        string chatId = r.Headers["X-Chat-Id"].ToString();
        string text = $"[{r.Headers["X-Session-Id"]}] echo: {(await new StreamReader(r.Body).ReadToEndAsync()).Replace("\n", " ")}";
        if (text.Contains("\"stream\":true"))
        {
            res.ContentType = "text/event-stream";
            foreach (string part in new[] { text[..text.IndexOf(']')], text[text.IndexOf(']')..] })
            {
                await res.WriteAsync($"data: {{\"id\":\"{chatId}\",\"delta\":\"{part.Replace("\"", "'")}\"}}\n\n");
                await res.Body.FlushAsync();
            }
            return;
        }
        await res.WriteAsJsonAsync(new { id = chatId, message = text });
    });
}

app.Run();
