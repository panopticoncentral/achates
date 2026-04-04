using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Escalates to a thinking model for complex reasoning tasks.
/// </summary>
internal sealed class ThinkTool(Model model) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["prompt"] = StringSchema("The problem or question to reason about deeply."),
        },
        required: ["prompt"]);

    public override string Name => "think";
    public override string Description =>
        "Think deeply about a complex problem using an advanced reasoning model. " +
        "Use when you need careful analysis, multi-step reasoning, or when the stakes of getting something wrong are high.";
    public override string Label => "Think";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var prompt = GetString(arguments, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return TextResult("prompt is required.");

        var context = new CompletionContext
        {
            SystemPrompt = "You are a reasoning assistant. Think carefully and thoroughly about the problem presented. Provide your analysis and conclusions.",
            Messages =
            [
                new CompletionUserContentMessage
                {
                    Content = [new CompletionTextContent { Text = prompt }],
                },
            ],
        };

        try
        {
            var stream = model.Provider.GetCompletions(model, context, null, cancellationToken);

            await foreach (var _ in stream.WithCancellation(cancellationToken)) { }

            var result = await stream.ResultAsync;

            if (result.ErrorMessage is not null)
                return TextResult($"Thinking failed: {result.ErrorMessage}");

            var output = new StringBuilder();
            foreach (var content in result.Content)
            {
                if (content is CompletionTextContent text)
                    output.Append(text.Text);
            }

            return output.Length > 0
                ? TextResult(output.ToString())
                : TextResult("No response was generated.");
        }
        catch (Exception ex)
        {
            return TextResult($"Thinking failed: {ex.Message}");
        }
    }

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
