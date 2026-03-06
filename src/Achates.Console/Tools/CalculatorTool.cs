using System.Data;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;

namespace Achates.Console.Tools;

internal sealed class CalculatorTool : AgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
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
        """).RootElement.Clone();

    public override string Name => "calculate";
    public override string Description => "Evaluate a mathematical expression.";
    public override string Label => "Calculator";
    public override JsonElement Parameters => Schema;

    public override Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var expr = GetString(arguments, "expression")
                   ?? throw new ArgumentException("Missing 'expression' argument.");

        var table = new DataTable();
        var result = table.Compute(expr, null);

        return Task.FromResult(new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = result?.ToString() ?? "null" }],
        });
    }

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
