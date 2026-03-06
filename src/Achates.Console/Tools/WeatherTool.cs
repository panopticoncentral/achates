using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;

namespace Achates.Console.Tools;

internal sealed class WeatherTool : AgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
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
        """).RootElement.Clone();

    public override string Name => "get_weather";
    public override string Description => "Get the current weather for a location.";
    public override string Label => "Weather";
    public override JsonElement Parameters => Schema;

    public override Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var location = GetString(arguments, "location")
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

        return Task.FromResult(new AgentToolResult
        {
            Content = [new CompletionTextContent
            {
                Text = $"Weather in {location}: {temp}°C, {conditions}, {humidity}% humidity, {wind} km/h wind"
            }],
        });
    }

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
