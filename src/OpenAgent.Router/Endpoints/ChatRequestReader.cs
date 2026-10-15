using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Router.Hosting;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal static class ChatRequestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<ParsedChatRequest> ReadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        try
        {
            if (request.HasFormContentType)
            {
                IFormCollection form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
                return new ParsedChatRequest(
                    ReadFormValue(form, "message")
                        ?? ReadFormValue(form, "query")
                        ?? string.Empty,
                    ReadFormValue(form, ChatRequestContext.ConversationIdKey),
                    ReadFormValue(form, ChatRequestContext.AgentIdKey),
                    ReadFormValue(form, ChatRequestContext.LlmProfileIdKey));
            }

            if (RequestBodySnapshot.TryGet(request.HttpContext, out Task<RequestBodySnapshot> snapshotTask))
            {
                RequestBodySnapshot snapshot = await snapshotTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Digest.Length > 0)
                {
                    using JsonDocument cached = JsonDocument.Parse(snapshot.CanonicalBody);
                    return ParseDocument(cached);
                }
            }

            using JsonDocument document = await JsonDocument.ParseAsync(request.Body,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseDocument(document);
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    private static string? ReadFormValue(IFormCollection form, string key)
    {
        if (form[key].Count > 1) throw new InvalidDataException($"Duplicate form field: {key}");
        return form[key].FirstOrDefault();
    }

    private static ParsedChatRequest ParseDocument(JsonDocument document)
    {
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A chat object is required");
        EnsureUniqueKeys(root);
        foreach (JsonProperty property in root.EnumerateObject())
            if (property.Name.Equals("context", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Object) EnsureUniqueKeys(property.Value);
        return ToParsedRequest(root.Deserialize<ChatRequest>(JsonOptions)
            ?? throw new JsonException("The chat request body is required"));
    }

    private static void EnsureUniqueKeys(JsonElement value)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new JsonException("Duplicate chat request property");
    }

    private static ParsedChatRequest ToParsedRequest(ChatRequest body)
    {
        return new ParsedChatRequest(
            body.Message,
            ChatRequestContext.ReadString(body.Context, ChatRequestContext.ConversationIdKey),
            ChatRequestContext.ReadString(body.Context, ChatRequestContext.AgentIdKey),
            ChatRequestContext.ReadString(body.Context, ChatRequestContext.LlmProfileIdKey));
    }
}
