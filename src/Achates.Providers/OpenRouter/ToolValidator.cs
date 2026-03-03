using System.Text.Json;
using Achates.Providers.OpenRouter.Chat;
using Json.Schema;

namespace Achates.Providers.OpenRouter;

/// <summary>
/// Validates tool call arguments against the JSON Schema declared in the tool definition.
/// </summary>
public static class ToolValidator
{
    /// <summary>
    /// Validates a tool call's arguments against the schema of the matching tool.
    /// Returns the parsed arguments on success.
    /// Throws <see cref="ToolValidationException"/> if the function name is unknown,
    /// the arguments are not valid JSON, or the arguments fail schema validation.
    /// </summary>
    public static Dictionary<string, JsonElement> ValidateToolCall(
        IReadOnlyList<ChatTool> tools,
        ChatToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolCall);

        var functionName = toolCall.Function.Name;

        ChatFunction? matchedFunction = null;
        foreach (var tool in tools)
        {
            if (string.Equals(tool.Function.Name, functionName, StringComparison.Ordinal))
            {
                matchedFunction = tool.Function;
                break;
            }
        }

        if (matchedFunction is null)
        {
            throw new ToolValidationException(
                $"Tool call references unknown function '{functionName}'.");
        }

        Dictionary<string, JsonElement> parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                toolCall.Function.Arguments) ?? [];
        }
        catch (JsonException ex)
        {
            throw new ToolValidationException(
                $"Tool call arguments for '{functionName}' are not valid JSON: {ex.Message}",
                ex);
        }

        if (matchedFunction.Parameters is not { } schemaElement)
        {
            return parsed;
        }

        try
        {
            var schema = JsonSchema.FromText(schemaElement.GetRawText());
            var result = schema.Evaluate(
                JsonDocument.Parse(toolCall.Function.Arguments).RootElement);

            if (!result.IsValid)
            {
                var errors = CollectErrors(result);
                throw new ToolValidationException(
                    $"Tool call arguments for '{functionName}' failed schema validation: " +
                    string.Join("; ", errors));
            }
        }
        catch (ToolValidationException)
        {
            throw;
        }
        catch (Exception)
        {
            // If schema validation infrastructure fails (e.g. unsupported schema feature),
            // trust the LLM output rather than blocking the call.
            return parsed;
        }

        return parsed;
    }

    private static List<string> CollectErrors(EvaluationResults results)
    {
        var errors = new List<string>();
        CollectErrorsRecursive(results, errors);
        return errors;
    }

    private static void CollectErrorsRecursive(EvaluationResults results, List<string> errors)
    {
        if (results.HasErrors)
        {
            foreach (var kvp in results.Errors!)
            {
                var path = results.InstanceLocation?.ToString();
                errors.Add(string.IsNullOrEmpty(path)
                    ? $"{kvp.Key}: {kvp.Value}"
                    : $"at '{path}': {kvp.Key}: {kvp.Value}");
            }
        }

        if (results.HasDetails)
        {
            foreach (var detail in results.Details)
            {
                CollectErrorsRecursive(detail, errors);
            }
        }
    }
}
