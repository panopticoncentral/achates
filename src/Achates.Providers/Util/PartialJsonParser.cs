using System.Text.Json;

namespace Achates.Providers.Util;

/// <summary>
/// Parses potentially incomplete JSON during streaming.
/// Used for parsing tool call arguments as they stream in.
/// </summary>
internal static class PartialJsonParser
{
    /// <summary>
    /// Parse potentially incomplete JSON, always returning a valid dictionary.
    /// </summary>
    public static Dictionary<string, object?> ParseStreamingJson(string? partialJson)
    {
        if (string.IsNullOrWhiteSpace(partialJson))
        {
            return [];
        }

        // Try standard parsing first (fastest for complete JSON)
        try
        {
            var result = JsonSerializer.Deserialize<Dictionary<string, object?>>(partialJson);
            return result ?? [];
        }
        catch (JsonException)
        {
            // Try to repair incomplete JSON
        }

        // Try closing open braces/brackets
        try
        {
            var repaired = RepairJson(partialJson);
            var result = JsonSerializer.Deserialize<Dictionary<string, object?>>(repaired);
            return result ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Attempt to repair incomplete JSON by closing open structures.
    /// </summary>
    private static string RepairJson(string json)
    {
        var trimmed = json.TrimEnd();
        if (trimmed.Length == 0)
        {
            return "{}";
        }

        // Track open structures
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        foreach (var c in trimmed)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            switch (c)
            {
                case '\\' when inString:
                    escaped = true;
                    continue;
                case '"':
                    inString = !inString;
                    continue;
            }

            if (inString)
            {
                continue;
            }

            switch (c)
            {
                case '{':
                    stack.Push('}');
                    break;
                case '[':
                    stack.Push(']');
                    break;
                case '}':
                case ']':
                    if (stack.Count > 0)
                    {
                        stack.Pop();
                    }

                    break;
            }
        }

        // If we're in a string, close it
        if (inString)
        {
            trimmed += '"';
        }

        // Close any open structures
        while (stack.Count > 0)
        {
            trimmed += stack.Pop();
        }

        return trimmed;
    }
}
