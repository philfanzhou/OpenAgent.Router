#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property TreatWarningsAsErrors=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Mock downstream business system speaking the standard OpenAI / Anthropic APIs, as Router expects.
// Run from the repository root: dotnet run tools/mock-business-api.cs (listens on http://localhost:5290).
using System.Text.Json;

string agentFile = Path.Combine(AppContext.BaseDirectory, "agent.json");
if (!File.Exists(agentFile))
{
    agentFile = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tools", "agent.json"));
}

JsonElement agents;
using (JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(agentFile)))
{
    agents = document.RootElement.Clone();
}

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
