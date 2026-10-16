using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

/// <param name="Root">The request body; Router rewrites only its <c>model</c>.</param>
internal sealed record ChatRequestInfo(
    JsonObject Root,
    string? Model,
    bool Stream,
    string? PreviousResponseId,
    string LastUserText,
    string? FirstUserText,
    bool HasAssistantTurn);

/// <summary>Reads the routing-relevant fields of the three standard chat request bodies.</summary>
internal static class ChatRequestParser
{
    internal static ChatRequestInfo Parse(ChatProtocol protocol, byte[] body)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject
                ?? throw new AgentRoutingException(StatusCodes.Status400BadRequest,
                    RouterErrorCodes.InvalidRoutingRequest, "The request body must be a JSON object");
        }
        catch (JsonException)
        {
            throw new AgentRoutingException(StatusCodes.Status400BadRequest,
                RouterErrorCodes.InvalidRoutingRequest, "The request body must contain valid JSON");
        }

        List<(string Role, string Text)> turns = [];
        if (protocol == ChatProtocol.Responses && root["input"] is JsonValue { } plain
            && plain.TryGetValue(out string? inputText))
        {
            turns.Add(("user", inputText));
        }
        else if (root[protocol == ChatProtocol.Responses ? "input" : "messages"] is JsonArray items)
        {
            foreach (JsonObject item in items.OfType<JsonObject>())
            {
                turns.Add((ReadString(item["role"]) ?? string.Empty, TextOf(item["content"])));
            }
        }

        string? firstUser = turns.FirstOrDefault(turn => turn.Role == "user" && turn.Text.Length > 0).Text;
        return new ChatRequestInfo(
            root,
            ReadString(root["model"]),
            root["stream"] is JsonValue stream && stream.TryGetValue(out bool streaming) && streaming,
            ReadString(root["previous_response_id"]),
            turns.LastOrDefault(turn => turn.Role == "user" && turn.Text.Length > 0).Text ?? string.Empty,
            firstUser,
            turns.Any(turn => turn.Role == "assistant"));
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    /// <summary>Content is a plain string or an array of parts whose text parts carry a <c>text</c> field.</summary>
    private static string TextOf(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue(out string? text) => text,
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>()
            .Select(part => part["text"] is JsonValue t && t.TryGetValue(out string? s) ? s : null)
            .Where(text => !string.IsNullOrEmpty(text))),
        _ => string.Empty
    };
}
