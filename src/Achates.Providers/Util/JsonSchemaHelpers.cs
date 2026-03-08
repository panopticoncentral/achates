using System.Text.Json;

namespace Achates.Providers.Util;

/// <summary>
/// Helpers for building JSON Schema objects programmatically.
/// </summary>
public static class JsonSchemaHelpers
{
    public static JsonElement StringSchema(string? description = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        if (description is not null)
            writer.WriteString("description", description);
        writer.WriteEndObject();
        writer.Flush();
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public static JsonElement NumberSchema(string? description = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("type", "number");
        if (description is not null)
            writer.WriteString("description", description);
        writer.WriteEndObject();
        writer.Flush();
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public static JsonElement BooleanSchema(string? description = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("type", "boolean");
        if (description is not null)
            writer.WriteString("description", description);
        writer.WriteEndObject();
        writer.Flush();
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public static JsonElement StringEnum(IReadOnlyList<string> values, string? description = null, string? defaultValue = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("type", "string");

        writer.WriteStartArray("enum");
        foreach (var v in values)
            writer.WriteStringValue(v);
        writer.WriteEndArray();

        if (description is not null)
            writer.WriteString("description", description);
        if (defaultValue is not null)
            writer.WriteString("default", defaultValue);

        writer.WriteEndObject();
        writer.Flush();

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public static JsonElement ObjectSchema(
        Dictionary<string, JsonElement> properties,
        IReadOnlyList<string>? required = null,
        string? description = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("type", "object");

        if (description is not null)
            writer.WriteString("description", description);

        writer.WriteStartObject("properties");
        foreach (var (name, schema) in properties)
        {
            writer.WritePropertyName(name);
            schema.WriteTo(writer);
        }
        writer.WriteEndObject();

        if (required is { Count: > 0 })
        {
            writer.WriteStartArray("required");
            foreach (var r in required)
                writer.WriteStringValue(r);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.Flush();

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
