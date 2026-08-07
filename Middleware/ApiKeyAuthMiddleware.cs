using System.Text.Json;
using EasyGateway.Models;
using EasyGateway.Services;

namespace EasyGateway.Middleware;

/// <summary>
/// API key authentication middleware. Validates the inbound key against
/// configured ApiKeys (or a single global key). Falls through to Blazor/UI
/// routes (which use cookie auth separately). Replaces the legacy
/// validateAPIKey + ValidateAPIKeyAndModel.
/// </summary>
public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ConfigService _config;
    private readonly ILogger<ApiKeyAuthMiddleware> _log;

    // snake_case to match OpenAI error envelope: {"error":{"message":...,"type":...}}
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ApiKeyAuthMiddleware(RequestDelegate next, ConfigService config, ILogger<ApiKeyAuthMiddleware> log)
    {
        _next = next; _config = config; _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        // Only gate the gateway API paths; UI/static/admin handled elsewhere.
        if (!IsGatewayPath(path))
        {
            await _next(ctx);
            return;
        }

        var apiKey = ExtractApiKey(ctx.Request);
        var (valid, name) = ValidateKey(apiKey, modelHint: null);
        if (!valid)
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "application/json";
            // Return the error in the protocol matching the inbound path:
            // Anthropic uses {"type":"error","error":{...}}, OpenAI uses {"error":{...}}.
            var body = path.StartsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Serialize(new { type = "error", error = new { type = "authentication_error", message = "invalid x-api-key" } }, JsonOpts)
                : JsonSerializer.Serialize(new ErrorResponse
                {
                    Error = new ErrorDetail { Message = "invalid api key", Type = "invalid_request_error", Code = "invalid_api_key" }
                }, JsonOpts);
            await ctx.Response.WriteAsync(body);
            return;
        }

        // model-level permission checked in the endpoint once body is parsed.
        ctx.Items["ApiKey"] = apiKey;
        ctx.Items["ApiKeyName"] = name ?? "";
        await _next(ctx);
    }

    private static bool IsGatewayPath(string path) =>
        path.StartsWith("/v1/") || path.StartsWith("/v1beta/") ||
        path == "/chat/completions" || path == "/messages" || path == "/responses";

    private static string? ExtractApiKey(HttpRequest req)
    {
        var auth = req.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        return req.Headers.TryGetValue("x-api-key", out var k) ? k.ToString()
             : req.Headers.TryGetValue("x-goog-api-key", out var g) ? g.ToString()
             : null;
    }

    private (bool valid, string? name) ValidateKey(string? apiKey, string? modelHint)
    {
        var snap = _config.Snapshot;
        // No keys configured at all → open (matches legacy behavior).
        if (snap.ApiKeys.Count == 0) return (true, "open");

        if (string.IsNullOrEmpty(apiKey)) return (false, null);
        var key = snap.ApiKeys.FirstOrDefault(k => k.Enabled && k.KeyValue == apiKey);
        if (key is null) return (false, null);
        return (true, key.Name);
    }
}
