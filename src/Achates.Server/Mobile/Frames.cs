using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Server.Mobile;

public abstract record Frame
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed record RequestFrame : Frame
{
    public override string Type => "req";
    public required string Id { get; init; }
    public required string Method { get; init; }
    public JsonElement Params { get; init; } = default;
}

public sealed record ResponseFrame : Frame
{
    public override string Type => "res";
    public required string Id { get; init; }
    public required bool Ok { get; init; }
    public JsonElement? Payload { get; init; }
    public FrameError? Error { get; init; }

    public static ResponseFrame Success(string id, JsonElement payload) => new()
    {
        Id = id, Ok = true, Payload = payload,
    };

    public static ResponseFrame Failure(string id, string code, string message) => new()
    {
        Id = id, Ok = false, Error = new FrameError { Code = code, Message = message },
    };
}

public sealed record FrameError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed record EventFrame : Frame
{
    public override string Type => "evt";
    public required string Event { get; init; }
    public JsonElement Payload { get; init; } = default;
    public required long Seq { get; init; }
}

public static class FrameParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static Frame Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();
        return type switch
        {
            "req" => JsonSerializer.Deserialize<RequestFrame>(json, Options)!,
            "res" => JsonSerializer.Deserialize<ResponseFrame>(json, Options)!,
            "evt" => JsonSerializer.Deserialize<EventFrame>(json, Options)!,
            _ => throw new JsonException($"Unknown frame type: {type}"),
        };
    }
}
