using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using EasyGateway.Gateway;
using EasyGateway.Models;
using EasyGateway.Providers.OpenAI;
using EasyGateway.Services;

namespace EasyGateway.Endpoints;

/// <summary>
/// OpenAI-compatible inbound endpoints: /v1/chat/completions,
/// /v1/models, /v1/embeddings. This is the primary client-facing surface.
/// </summary>
public static class OpenAiEndpoints
{
    public static IEndpointRouteBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1");

        g.MapPost("/chat/completions", HandleChatCompletion);

        g.MapGet("/models", HandleListModels);
        g.MapGet("/models/{model}", HandleGetModel);
        g.MapPost("/embeddings", HandleEmbeddings);

        return app;
    }

    private static async Task<IResult> HandleChatCompletion(
        HttpContext ctx, [FromBody] JsonElement body,
        GatewayService gw, ConfigService config, CancellationToken ct)
    {
        var req = body.Deserialize<ChatRequest>(JsonOpts) ?? new ChatRequest();
        req.ClientModel = req.Model;
        // Sticky session engages ONLY on an explicit X-Session-Id header — a
        // client's `user` field no longer pins affinity (it defeated load
        // balancing because most SDKs send a stable user id).
        req.SessionId = ctx.Request.Headers["X-Session-Id"].FirstOrDefault();
        // Forward client headers that upstreams need (anthropic-version, beta, etc.)
        req.ExtraHeaders = ExtractForwardableHeaders(ctx.Request.Headers);
        var apiKeyName = ctx.Items["ApiKeyName"] as string ?? "";

        if (!CheckModelPermission(ctx, config, req.Model))
            return Error(403, "model not allowed for this api key", "invalid_request_error");

        if (req.Stream)
            return await StreamResponse(ctx, gw, req, apiKeyName, ct);
        else
            return await NonStreamResponse(gw, req, apiKeyName, ct);
    }

    /// <summary>Headers to forward to upstream (whitelist, like sub2api).</summary>
    private static Dictionary<string, string> ExtractForwardableHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] forward = { "anthropic-version", "anthropic-beta", "user-agent",
            "x-stainless-arch", "x-stainless-os", "x-stainless-package-version",
            "x-stainless-runtime", "x-stainless-runtime-version", "x-stainless-lang",
            "x-client-request-id", "accept-language", "openai-beta", "originator" };
        foreach (var key in forward)
        {
            var v = headers[key].ToString();
            if (!string.IsNullOrEmpty(v)) result[key] = v;
        }
        return result;
    }

    private static async Task<IResult> NonStreamResponse(
        GatewayService gw, ChatRequest req, string apiKeyName, CancellationToken ct)
    {
        try
        {
            var resp = await gw.ChatAsync(req, apiKeyName, ct);
            return Results.Json(resp, JsonOpts);
        }
        catch (ModelNotFoundException ex)
        {
            return Error(404, ex.Message, "invalid_request_error");
        }
        catch (UpstreamException ex)
        {
            return Error((int)ex.StatusCode, ex.Message, "upstream_error");
        }
    }

    private static async Task<IResult> StreamResponse(
        HttpContext ctx, GatewayService gw, ChatRequest req, string apiKeyName, CancellationToken ct)
    {
        SseWriter.StartSse(ctx.Response);
        try
        {
            await foreach (var chunk in gw.StreamAsync(req, apiKeyName, ct))
                await SseWriter.WriteChunkAsync(ctx.Response, chunk, ct);
            await SseWriter.WriteDoneAsync(ctx.Response, ct);
            return Results.Empty;
        }
        catch (ModelNotFoundException ex)
        {
            await SseWriter.WriteErrorAsync(ctx.Response, new StreamError("invalid_request_error", ex.Message), ct);
            return Results.Empty;
        }
        catch (Exception ex)
        {
            await SseWriter.WriteErrorAsync(ctx.Response, new StreamError("upstream_error", ex.Message), ct);
            return Results.Empty;
        }
    }

    private static IResult HandleListModels(ConfigService config)
    {
        var models = config.GetEnabledModelNames().Select(m => new
        {
            id = m,
            @object = "model",
            created = 0,
            owned_by = "easy-gateway",
        });
        return Results.Json(new { @object = "list", data = models });
    }

    private static IResult HandleGetModel(string model, ConfigService config)
    {
        var names = config.GetEnabledModelNames();
        if (!names.Any(n => n.Equals(model, StringComparison.OrdinalIgnoreCase)))
            return Error(404, $"model '{model}' not found", "invalid_request_error");
        return Results.Json(new { id = model, @object = "model", created = 0, owned_by = "easy-gateway" });
    }

    private static async Task<IResult> HandleEmbeddings(
        [FromBody] EmbeddingRequest req, GatewayService gw, CancellationToken ct)
    {
        // Embeddings routing deferred; return not implemented for now.
        return Results.Json(new { error = "embeddings routing not yet wired" });
    }

    private static bool CheckModelPermission(HttpContext ctx, ConfigService config, string model)
    {
        var snap = config.Snapshot;
        if (snap.ApiKeys.Count == 0) return true; // open mode
        var keyValue = ctx.Items["ApiKey"] as string;
        var key = snap.ApiKeys.FirstOrDefault(k => k.KeyValue == keyValue);
        return key?.AllowsModel(model) ?? false;
    }

    private static IResult Error(int status, string message, string type) =>
        Results.Json(new ErrorResponse { Error = new ErrorDetail { Message = message, Type = type } },
            JsonSerializerOptions, statusCode: status);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonSerializerOptions JsonSerializerOptions = JsonOpts;
}
