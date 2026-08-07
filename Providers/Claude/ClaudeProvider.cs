using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EasyGateway.Data.Entities;
using EasyGateway.Models;
using EasyGateway.Providers.OpenAI;

namespace EasyGateway.Providers.Claude;

/// <summary>
/// Provider for Anthropic Claude-native upstreams (api.anthropic.com /v1/messages).
/// Converts the unified ChatRequest -> Anthropic Messages request, and the
/// Anthropic response back -> unified ChatResponse. This lets a Claude-native
/// upstream participate in load balancing alongside OpenAI-compatible upstreams.
///
/// Inbound protocol conversion (Anthropic client -> OpenAI upstream) is handled
/// in the endpoint layer; THIS class handles the reverse: unified internal
/// model -> Anthropic-native upstream. So the full matrix works:
///   - OpenAI client  -> OpenAI upstream  (OpenAIProvider)
///   - OpenAI client  -> Claude upstream  (ClaudeProvider, outbound convert)
///   - Claude client  -> OpenAI upstream  (endpoint inbound convert + OpenAIProvider)
///   - Claude client  -> Claude upstream  (ClaudeProvider, no double convert needed)
/// </summary>
public class ClaudeProvider : IProvider, ISupportsTools, ISupportsVision, ISupportsReasoning, IModelListable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _providerType;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public ClaudeProvider(HttpClient http, ServiceEntity service)
    {
        _http = http;
        _baseUrl = NormalizeBaseUrl(service.ServerUrl);
        var creds = service.GetCredentials();
        creds.TryGetValue("api_key", out _apiKey);
        _providerType = service.ProviderType;
    }

    public string Type => _providerType;

    /// <summary>Fetch models from Anthropic GET /v1/models with pagination.</summary>
    public async Task<List<UpstreamModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var result = new List<UpstreamModelInfo>();
        string? afterId = null;
        // Loop pages until has_more is false (Anthropic paginates by after_id).
        for (int i = 0; i < 50; i++)
        {
            var path = "/v1/models?limit=100";
            if (!string.IsNullOrEmpty(afterId))
                path += $"&after_id={afterId}";
            var (req, _) = BuildRequest(path, new { }, stream: false);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new UpstreamException(resp.StatusCode, body);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in data.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var name = m.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
                    if (!string.IsNullOrEmpty(id))
                        result.Add(new UpstreamModelInfo(id!, name));
                }
            }
            var hasMore = doc.RootElement.TryGetProperty("has_more", out var hm) && hm.GetBoolean();
            if (!hasMore) break;
            afterId = doc.RootElement.TryGetProperty("last_id", out var lid) ? lid.GetString() : null;
            if (string.IsNullOrEmpty(afterId)) break;
        }
        return result;
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        var payload = ChatToAnthropic(request);
        var (req, _) = BuildRequest("/v1/messages", payload, stream: false);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new UpstreamException(resp.StatusCode, body);

        var ar = JsonSerializer.Deserialize<AnthropicResponse>(body, JsonOpts)
                 ?? throw new UpstreamException(resp.StatusCode, "empty response");
        var chat = AnthropicToChat(ar, request);
        return chat;
    }

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = ChatToAnthropic(request, stream: true);
        var (req, _) = BuildRequest("/v1/messages", payload, stream: true);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            throw new UpstreamException(resp.StatusCode, errBody);
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Anthropic SSE: named events. We emit OpenAI-style chunks.
        // Accumulate content_block_delta text deltas into OpenAI chunk shape.
        string chunkId = Guid.NewGuid().ToString();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string model = request.ClientModel;

        // Anthropic reports cache stats in message_start.message.usage (NOT in
        // message_delta). But the gateway keeps only the LAST chunk's Usage
        // (GatewayService overwrites per chunk), and message_delta is always
        // last — so we capture cache fields here and stamp them onto the
        // message_delta chunk, which is the one that survives into the log.
        int cacheCreationInputTokens = 0;
        int cacheReadInputTokens = 0;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (string.IsNullOrEmpty(data)) continue;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var evtType = root.TryGetProperty("type", out var t) ? t.GetString() : "";

            switch (evtType)
            {
                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("type", out var dt) &&
                        dt.GetString() == "text_delta" &&
                        delta.TryGetProperty("text", out var txt))
                    {
                        yield return new StreamChunk
                        {
                            Id = chunkId, Created = created, Model = model,
                            Choices = new List<StreamChoice> { new() { Index = 0, Delta = new() { Content = txt.GetString() } } },
                        };
                    }
                    break;
                case "message_delta":
                    // carries final usage sometimes
                    if (root.TryGetProperty("usage", out var u))
                    {
                        var usage = new Usage
                        {
                            PromptTokens = u.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0,
                            CompletionTokens = u.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0,
                            // Carried from message_start — this chunk is the last
                            // usage-bearing one, so its values survive into the log.
                            CacheCreationInputTokens = cacheCreationInputTokens,
                            CacheReadInputTokens = cacheReadInputTokens,
                        };
                        usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
                        yield return new StreamChunk { Id = chunkId, Created = created, Model = model, Usage = usage };
                    }
                    break;
                case "message_start":
                    // extract input usage + cache stats from message.usage
                    if (root.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("usage", out var mu))
                    {
                        // Cache stats arrive here (cache_creation_input_tokens /
                        // cache_read_input_tokens), not in message_delta.
                        int prompt = mu.TryGetProperty("input_tokens", out var it2) ? it2.GetInt32() : 0;
                        cacheCreationInputTokens = mu.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0;
                        cacheReadInputTokens = mu.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
                        // input tokens known here; output unknown yet — emit usage chunk
                        yield return new StreamChunk
                        {
                            Id = chunkId, Created = created, Model = model,
                            Usage = new Usage { PromptTokens = prompt },
                        };
                    }
                    break;
            }
        }
    }

    // --- outbound: unified ChatRequest -> Anthropic Messages request ---

    private static object ChatToAnthropic(ChatRequest req, bool stream = false)
    {
        // Extract system message (Anthropic uses top-level system, not a message).
        var systemText = new StringBuilder();
        var msgs = new List<object>();
        foreach (var m in req.Messages)
        {
            if (m.Role == "system")
            {
                if (systemText.Length > 0) systemText.Append('\n');
                systemText.Append(m.Content ?? "");
                continue;
            }
            msgs.Add(new { role = m.Role, content = m.Content ?? "" });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = req.Model,
            ["messages"] = msgs,
            ["max_tokens"] = req.MaxTokens ?? 1024,
            ["stream"] = stream,
        };
        if (systemText.Length > 0) payload["system"] = systemText.ToString();
        if (req.Temperature.HasValue) payload["temperature"] = req.Temperature;
        if (req.TopP.HasValue) payload["top_p"] = req.TopP;
        if (req.TopK.HasValue) payload["top_k"] = req.TopK;
        if (req.Stop is { Count: > 0 }) payload["stop_sequences"] = req.Stop;
        if (req.Tools is { Count: > 0 }) payload["tools"] = ConvertTools(req.Tools);
        if (req.ToolChoice is not null)
        {
            // OpenAI tool_choice → Anthropic tool_choice mapping.
            var tcJson = req.ToolChoice.RootElement.GetRawText();
            if (tcJson.Contains("auto", StringComparison.OrdinalIgnoreCase))
                payload["tool_choice"] = new { type = "auto" };
            else if (tcJson.Contains("none", StringComparison.OrdinalIgnoreCase))
                payload["tool_choice"] = new { type = "none" };
            else if (tcJson.Contains("required", StringComparison.OrdinalIgnoreCase))
                payload["tool_choice"] = new { type = "any" };
            else
                payload["tool_choice"] = req.ToolChoice; // pass as-is for named tool
        }
        return payload;
    }

    private static List<object> ConvertTools(List<Tool> tools)
    {
        var result = new List<object>();
        foreach (var t in tools)
        {
            if (t.Function is null) continue;
            result.Add(new
            {
                name = t.Function.Name,
                description = t.Function.Description,
                input_schema = t.Function.Parameters ?? JsonDocument.Parse("{}"),
            });
        }
        return result;
    }

    // --- inbound: Anthropic response -> unified ChatResponse ---

    private static ChatResponse AnthropicToChat(AnthropicResponse ar, ChatRequest req)
    {
        var text = new StringBuilder();
        foreach (var c in ar.Content ?? new())
        {
            if (c.Type == "text") text.Append(c.Text);
        }

        return new ChatResponse
        {
            Id = ar.Id,
            Object = "chat.completion",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = req.ClientModel.Length > 0 ? req.ClientModel : req.Model,
            Choices = new List<Choice>
            {
                new()
                {
                    Index = 0,
                    Message = new ResponseMessage { Role = "assistant", Content = text.ToString() },
                    FinishReason = AnthropicStopToOpenAI(ar.StopReason),
                }
            },
            Usage = new Usage
            {
                PromptTokens = ar.Usage?.InputTokens ?? 0,
                CompletionTokens = ar.Usage?.OutputTokens ?? 0,
                CacheCreationInputTokens = ar.Usage?.CacheCreationInputTokens ?? 0,
                CacheReadInputTokens = ar.Usage?.CacheReadInputTokens ?? 0,
            },
        };
    }

    private static string AnthropicStopToOpenAI(string? stop) => stop switch
    {
        "end_turn" => "stop",
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        _ => "stop",
    };

    // --- http ---

    private (HttpRequestMessage req, string payload) BuildRequest(string path, object payload, bool stream)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var url = $"{_baseUrl}{path}";
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(_apiKey))
            req.Headers.Add("x-api-key", _apiKey);
        // anthropic-version header required by Claude API.
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (stream)
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return (req, json);
    }

    private static string NormalizeBaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "https://api.anthropic.com";
        url = url.TrimEnd('/');
        // strip trailing /v1/messages or /v1 if present, we add path ourselves.
        // Bug fix: was url[..3] which truncated to "htt" — must use ^3.
        if (url.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/v1/messages".Length];
        else if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/v1".Length];
        return url;
    }
}

// --- Anthropic response models (for deserialization) ---

public class AnthropicResponse
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "message";
    public string Role { get; set; } = "assistant";
    public string Model { get; set; } = "";
    public List<AnthropicContent> Content { get; set; } = new();
    public string? StopReason { get; set; }
    public string? StopSequence { get; set; }
    public AnthropicUsage? Usage { get; set; }
}

public class AnthropicContent
{
    public string Type { get; set; } = "text";
    public string? Text { get; set; }
}

public class AnthropicUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheCreationInputTokens { get; set; }
    public int CacheReadInputTokens { get; set; }
}
