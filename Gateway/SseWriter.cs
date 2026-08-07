using System.Text.Json;
using EasyGateway.Models;

namespace EasyGateway.Gateway;

/// <summary>
/// Writes OpenAI-style SSE chunks to an HttpResponse. Single place owning
/// SSE framing (headers, "data: ...\n\n", flush, [DONE] terminator),
/// replacing per-handler hand-rolled writes.
/// </summary>
public static class SseWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void StartSse(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        response.StartAsync().GetAwaiter().GetResult();
    }

    public static async Task WriteChunkAsync(HttpResponse response, StreamChunk chunk, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(chunk, JsonOpts);
        await response.WriteAsync($"data: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    public static async Task WriteErrorAsync(HttpResponse response, StreamError err, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { error = new { type = err.Type, message = err.Message } }, JsonOpts);
        await response.WriteAsync($"data: {payload}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    public static async Task WriteDoneAsync(HttpResponse response, CancellationToken ct)
    {
        await response.WriteAsync("data: [DONE]\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}

public record StreamError(string Type, string Message);
