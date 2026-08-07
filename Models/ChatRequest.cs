using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyGateway.Models;

/// <summary>
/// Unified chat completion request — the vendor-neutral internal model.
/// Carries the FULL OpenAI-compatible field set so no field is silently
/// dropped when routing to a provider that doesn't understand it (the
/// gateway down-converts instead).
/// </summary>
public class ChatRequest
{
    public string Model { get; set; } = "";

    public List<ChatMessage> Messages { get; set; } = new();

    public List<Tool>? Tools { get; set; }
    public JsonDocument? ToolChoice { get; set; }
    /// <summary>Whether to enable parallel function calling during tool use
    /// (OpenAI param). null = upstream default.</summary>
    public bool? ParallelToolCalls { get; set; }

    public ResponseFormat? ResponseFormat { get; set; }

    public ReasoningConfig? Reasoning { get; set; }

    public bool Stream { get; set; }
    public StreamOptions? StreamOptions { get; set; }

    public int? MaxTokens { get; set; }
    public float? Temperature { get; set; }
    public float? TopP { get; set; }
    public int? TopK { get; set; }
    public float? FrequencyPenalty { get; set; }
    public float? PresencePenalty { get; set; }
    public List<string>? Stop { get; set; }
    public int? Seed { get; set; }
    public int? N { get; set; }
    public bool? LogProbs { get; set; }
    public int? TopLogProbs { get; set; }
    public Dictionary<int, int>? LogitBias { get; set; }
    public string? User { get; set; }

    /// <summary>Model name as requested by the client, before redirect/mapping.</summary>
    [JsonIgnore]
    public string ClientModel { get; set; } = "";

    /// <summary>Sticky session id, taken ONLY from the X-Session-Id header.
    /// When non-empty, the gateway routes the session to the same provider
    /// (affinity), preserving multi-turn inference context. Note: a client's
    /// `user` / `metadata.user_id` field is deliberately NOT used here, since
    /// most SDKs send a stable id that would pin all traffic to one service
    /// and defeat load balancing. Sticky is opt-in via the header only.</summary>
    [JsonIgnore]
    public string? SessionId { get; set; }

    /// <summary>Provider-specific headers to forward upstream.</summary>
    [JsonIgnore]
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();
}

public class ChatMessage
{
    public string Role { get; set; } = "";
    public string? Content { get; set; }
    public List<ContentPart>? Parts { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? Name { get; set; }
}

public class ContentPart
{
    public string Type { get; set; } = "text"; // text, image_url, input_audio
    public string? Text { get; set; }
    public ImageUrl? ImageUrl { get; set; }
    public InputAudio? InputAudio { get; set; }
}

public class ImageUrl
{
    public string Url { get; set; } = "";
    public string? Detail { get; set; } // low/high/auto
}

public class InputAudio
{
    public string Data { get; set; } = ""; // base64
    public string Format { get; set; } = ""; // wav/mp3/...
}

public class Tool
{
    public string Type { get; set; } = "function";
    public FunctionDecl? Function { get; set; }
}

public class FunctionDecl
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonDocument? Parameters { get; set; } // JSON Schema
    public bool Strict { get; set; }
}

public class ToolCall
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public string Type { get; set; } = "function";
    public FunctionCall Function { get; set; } = new();
}

public class FunctionCall
{
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = ""; // JSON string
}

public class ResponseFormat
{
    public string Type { get; set; } = "text"; // text, json_object, json_schema
    public JsonSchemaSpec? JsonSchema { get; set; }
}

public class JsonSchemaSpec
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonDocument? Schema { get; set; }
    public bool Strict { get; set; }
}

public class ReasoningConfig
{
    public ReasoningEffort Effort { get; set; } = ReasoningEffort.Medium;
    public bool Enabled { get; set; }
    public int? MaxThinkingTokens { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReasoningEffort
{
    Minimal, Low, Medium, High, XHigh, Max
}

public class StreamOptions
{
    public bool IncludeUsage { get; set; }
}
