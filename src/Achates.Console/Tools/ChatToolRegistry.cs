using System.Data;
using System.Text.Json;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;

namespace Achates.Console.Tools;

internal sealed class ChatToolRegistry
{
    private readonly Dictionary<string, Func<Dictionary<string, object?>, string>> _handlers = new();
    private readonly List<CompletionTool> _tools = [];

    public ChatToolRegistry()
    {
        Register(
            "get_current_time",
            "Get the current date and time, optionally in a specific timezone.",
            ParseSchema("""
                {
                    "type": "object",
                    "properties": {
                        "timezone": {
                            "type": "string",
                            "description": "IANA timezone (e.g., 'America/New_York', 'Asia/Tokyo'). Defaults to local time."
                        }
                    },
                    "required": []
                }
                """),
            GetCurrentTime);

        Register(
            "calculate",
            "Evaluate a mathematical expression.",
            ParseSchema("""
                {
                    "type": "object",
                    "properties": {
                        "expression": {
                            "type": "string",
                            "description": "A mathematical expression to evaluate (e.g., '2 + 3 * 4', '100 / 7')."
                        }
                    },
                    "required": ["expression"]
                }
                """),
            Calculate);

        Register(
            "get_weather",
            "Get the current weather for a location.",
            ParseSchema("""
                {
                    "type": "object",
                    "properties": {
                        "location": {
                            "type": "string",
                            "description": "City name or location (e.g., 'San Francisco', 'London')."
                        }
                    },
                    "required": ["location"]
                }
                """),
            GetWeather);
    }

    public IReadOnlyList<CompletionTool> GetToolDefinitions() => _tools;

    public CompletionToolResultMessage Execute(CompletionToolCall toolCall)
    {
        string resultText;
        var isError = false;

        if (_handlers.TryGetValue(toolCall.Name, out var handler))
        {
            try
            {
                resultText = handler(toolCall.Arguments);
            }
            catch (Exception ex)
            {
                resultText = $"Error: {ex.Message}";
                isError = true;
            }
        }
        else
        {
            resultText = $"Unknown tool: {toolCall.Name}";
            isError = true;
        }

        return new CompletionToolResultMessage
        {
            ToolCallId = toolCall.Id,
            ToolName = toolCall.Name,
            Content = [new CompletionTextContent { Text = resultText }],
            IsError = isError,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private void Register(
        string name,
        string description,
        JsonElement parameters,
        Func<Dictionary<string, object?>, string> handler)
    {
        _tools.Add(new CompletionTool
        {
            Name = name,
            Description = description,
            Parameters = parameters,
        });
        _handlers[name] = handler;
    }

    private static JsonElement ParseSchema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static string? GetString(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val is null) return null;
        return val is JsonElement je ? je.GetString() : val.ToString();
    }

    // -------------------------------------------------------------------------
    // Tool implementations
    // -------------------------------------------------------------------------

    private static string GetCurrentTime(Dictionary<string, object?> args)
    {
        var tzName = GetString(args, "timezone");
        var tz = tzName is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(tzName)
            : TimeZoneInfo.Local;

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        return now.ToString("yyyy-MM-dd HH:mm:ss zzz");
    }

    private static string Calculate(Dictionary<string, object?> args)
    {
        var expr = GetString(args, "expression")
                   ?? throw new ArgumentException("Missing 'expression' argument.");

        var table = new DataTable();
        var result = table.Compute(expr, null);
        return result?.ToString() ?? "null";
    }

    private static string GetWeather(Dictionary<string, object?> args)
    {
        var location = GetString(args, "location")
                       ?? throw new ArgumentException("Missing 'location' argument.");

        var hash = Math.Abs(location.GetHashCode(StringComparison.OrdinalIgnoreCase));
        var temp = (hash % 35) + 5;
        var conditions = (hash % 4) switch
        {
            0 => "Sunny",
            1 => "Partly Cloudy",
            2 => "Overcast",
            _ => "Rainy",
        };
        var humidity = (hash % 60) + 30;
        var wind = (hash % 25) + 3;

        return $"Weather in {location}: {temp}°C, {conditions}, {humidity}% humidity, {wind} km/h wind";
    }
}
