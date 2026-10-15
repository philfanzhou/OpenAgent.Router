using System.Text.Json.Nodes;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal static class RoutingRequestNormalizer
{
    internal static string? Merge(string name, StringComparer comparer, IEnumerable<string?> values)
    {
        string[] supplied = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim()).Distinct(comparer).ToArray();
        if (supplied.Length > 1)
        {
            throw new AgentRoutingException(StatusCodes.Status400BadRequest,
                RouterErrorCodes.InvalidRoutingRequest, $"Conflicting {name} values");
        }
        return supplied.FirstOrDefault();
    }

    internal static async Task<Stream> RewriteAsync(HttpRequest request, string? conversationId,
        string? agentId, CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        if (request.HasFormContentType)
        {
            IFormCollection form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, string> values = form.Where(pair => !IsRoutingKey(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            if (conversationId != null) values["conversationId"] = conversationId;
            if (agentId != null) values["agentId"] = agentId;
            if (request.ContentType!.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            {
                using var multipart = new MultipartFormDataContent();
                foreach (var pair in values) multipart.Add(new StringContent(pair.Value), pair.Key);
                foreach (IFormFile file in form.Files)
                {
                    var content = new StreamContent(file.OpenReadStream());
                    if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(file.ContentType, out var type))
                        content.Headers.ContentType = type;
                    multipart.Add(content, file.Name, file.FileName);
                }
                await multipart.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                request.ContentType = multipart.Headers.ContentType!.ToString();
            }
            else
            {
                using var content = new FormUrlEncodedContent(values);
                await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            if (request.Body.CanSeek) request.Body.Position = 0;
            JsonObject root = (await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken)
                .ConfigureAwait(false))!.AsObject();
            string? contextKey = root.Select(pair => pair.Key).FirstOrDefault(key =>
                key.Equals("context", StringComparison.OrdinalIgnoreCase));
            JsonObject context = contextKey == null || root[contextKey] == null
                ? new JsonObject() : root[contextKey]!.AsObject();
            foreach (string key in context.Select(pair => pair.Key).Where(IsRoutingKey).ToArray()) context.Remove(key);
            if (conversationId != null) context["conversationId"] = conversationId;
            if (agentId != null) context["agentId"] = agentId;
            if (contextKey != null) root.Remove(contextKey);
            root["context"] = context;
            await System.Text.Json.JsonSerializer.SerializeAsync(output, root,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        output.Position = 0;
        return output;
    }

    private static bool IsRoutingKey(string key) => key.Equals("conversationId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("agentId", StringComparison.OrdinalIgnoreCase);
}
