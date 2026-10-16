using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Routing;
using OpenAgent.Router.Hosting;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Providers;

/// <summary>
/// Downstream that exposes agents as models behind the standard OpenAI / Anthropic APIs:
/// <c>GET /v1/models</c> lists agents, the chat endpoints take the agent ID as <c>model</c>.
/// </summary>
internal sealed class OpenAgentEngineProvider : IAgentProvider, IDisposable
{
    private const string ModelsPath = "/v1/models";

    private readonly string? _baseUrl;
    private readonly IReadOnlySet<ChatProtocol> _protocols;
    private readonly IReadOnlyDictionary<string, string> _serviceHeaders;
    private readonly IRouteTable _routeTable;
    private readonly HttpMessageInvoker _httpClient;
    private readonly ILogger<OpenAgentEngineProvider> _logger;

    internal OpenAgentEngineProvider(
        string id,
        IConfiguration settings,
        IRouteTable routeTable,
        HttpMessageHandler? handler = null,
        ILogger<OpenAgentEngineProvider>? logger = null,
        bool allowInsecureTls = false)
    {
        Id = id;
        _baseUrl = string.IsNullOrWhiteSpace(settings["BaseUrl"])
            ? null
            : settings["BaseUrl"]!.TrimEnd('/');
        _protocols = ParseProtocols(settings.GetSection("Protocols").GetChildren().Select(child => child.Value));
        _serviceHeaders = settings.GetSection("ServiceHeaders")
            .GetChildren()
            .Where(header => header.Value != null)
            .ToDictionary(
                header => header.Key,
                header => header.Value!,
                StringComparer.OrdinalIgnoreCase);
        _routeTable = routeTable;
        _httpClient = new HttpMessageInvoker(handler ?? CreateHandler(settings, allowInsecureTls));
        _logger = logger ?? NullLogger<OpenAgentEngineProvider>.Instance;
    }

    public string Id { get; }

    public bool Supports(ChatProtocol protocol) => _protocols.Contains(protocol);

    public async Task<AgentProviderCatalog> GetAgentsAsync(
        AgentProviderRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        string? endpoint = ResolveEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new AgentProviderCatalog([], false);
        }

        using HttpRequestMessage request = new(HttpMethod.Get, $"{endpoint.TrimEnd('/')}{ModelsPath}");
        foreach ((string name, string value) in _serviceHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (!string.IsNullOrWhiteSpace(requestContext.AuthenticationToken))
        {
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", requestContext.AuthenticationToken);
        }
        RouterLog.ProviderHttpRequest(
            _logger,
            Id,
            "get_agents",
            request.Method.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            RouterHttpLog.FormatRequestHeaders(request));
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        RouterLog.ProviderHttpResponse(
            _logger,
            Id,
            "get_agents",
            (int)response.StatusCode,
            RouterHttpLog.FormatResponseHeaders(response));
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            RouterLog.ProviderHttpResponseBody(
                _logger,
                Id,
                "get_agents",
                RouterHttpLog.FormatBody(responseBody));
        }
        if (!response.IsSuccessStatusCode)
        {
            return new AgentProviderCatalog([], false);
        }

        return new AgentProviderCatalog(ParseModels(responseBody));
    }

    public Task<AgentForwardingTarget?> ResolveForwardingAsync(
        ChatProtocol protocol,
        string? tenantId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? endpoint = _baseUrl ?? _routeTable.GetTargetEndpoint("chat", tenantId, sessionId);
        if (!Supports(protocol) || string.IsNullOrWhiteSpace(endpoint))
        {
            return Task.FromResult<AgentForwardingTarget?>(null);
        }

        AgentForwardingTarget target = new(
            endpoint.TrimEnd('/'),
            new Uri($"{endpoint.TrimEnd('/')}{ChatProtocols.Path(protocol)}"));
        RouterLog.ProviderForwardingTargetResolved(
            _logger,
            Id,
            protocol.ToString(),
            target.RequestUri.ToString(),
            tenantId,
            sessionId);
        return Task.FromResult<AgentForwardingTarget?>(target);
    }

    public ValueTask ConfigureRequestAsync(
        HttpRequestMessage request,
        AgentForwardingTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach ((string name, string value) in _serviceHeaders)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _httpClient.Dispose();

    private static SocketsHttpHandler CreateHandler(
        IConfiguration settings,
        bool allowInsecureTls) => HttpClientSecurity.CreateSocketsHttpHandler(
            settings,
            TimeSpan.FromSeconds(15),
            allowInsecureTls);

    private string? ResolveEndpoint() =>
        _baseUrl ?? _routeTable.GetTargetEndpoint("chat");

    private static IReadOnlySet<ChatProtocol> ParseProtocols(IEnumerable<string?> names)
    {
        HashSet<ChatProtocol> protocols = [];
        foreach (string? name in names)
        {
            protocols.Add(Enum.TryParse(name, ignoreCase: true, out ChatProtocol protocol)
                ? protocol
                : throw new InvalidOperationException($"Unknown chat protocol '{name}'"));
        }
        // Unconfigured means the downstream serves all three standard protocols.
        return protocols.Count > 0 ? protocols : new HashSet<ChatProtocol>(Enum.GetValues<ChatProtocol>());
    }

    /// <summary>
    /// Parses the standard <c>{"data":[{"id":...}]}</c> model list. <c>description</c> is the one
    /// non-standard field: the decision model needs it; <c>display_name</c> is the Anthropic name field.
    /// </summary>
    private static IReadOnlyList<AgentSummary> ParseModels(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The /v1/models response must contain a data array.");
        }

        List<AgentSummary> agents = [];
        foreach (JsonElement item in data.EnumerateArray())
        {
            string? id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            agents.Add(new AgentSummary
            {
                AgentId = id,
                Name = ReadString(item, "display_name") ?? id,
                Description = ReadString(item, "description") ?? string.Empty
            });
        }
        return agents;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
