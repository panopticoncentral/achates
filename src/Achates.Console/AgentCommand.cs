using Achates.Agent;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Console.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Models;

namespace Achates.Console;

internal static class AgentCommand
{
    public static async Task<int> RunAsync(
        Model model,
        string? systemPrompt,
        double? temperature)
    {
        var tools = new AgentTool[] { new TimeTool(), new CalculatorTool(), new WeatherTool() };

        var agent = new Achates.Agent.Agent(new AgentOptions
        {
            Model = model,
            SystemPrompt = systemPrompt,
            Tools = model.Parameters.HasFlag(ModelParameters.Tools) ? tools : [],
            CompletionOptions = new CompletionOptions
            {
                Temperature = temperature,
                ReasoningEffort = model.Parameters.HasFlag(ModelParameters.ReasoningEffort) ? "medium" : null,
            },
        });

        using var cts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            agent.Abort();
            cts.Cancel();
        };

        WriteHeader(model, agent.Tools);

        while (!cts.Token.IsCancellationRequested)
        {
            ChatRenderer.WritePrompt();
            var input = System.Console.ReadLine();

            if (input is null ||
                string.Equals(input.Trim(), "/exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            try
            {
                var stream = agent.PromptAsync(input);

                await foreach (var evt in stream.WithCancellation(cts.Token))
                {
                    RenderEvent(evt);
                }
            }
            catch (OperationCanceledException)
            {
                System.Console.WriteLine();
                break;
            }
        }

        return 0;
    }

    private static void RenderEvent(AgentEvent evt)
    {
        const string Reset = "\x1b[0m";
        const string Dim = "\x1b[2m";
        const string Yellow = "\x1b[33m";
        const string Green = "\x1b[32m";
        const string Red = "\x1b[31m";

        switch (evt)
        {
            case MessageStreamEvent { Inner: CompletionThinkingStartEvent }:
                System.Console.Write($"{Dim}[thinking] ");
                break;

            case MessageStreamEvent { Inner: CompletionThinkingDeltaEvent e }:
                System.Console.Write(e.Delta);
                break;

            case MessageStreamEvent { Inner: CompletionThinkingEndEvent }:
                System.Console.Write(Reset);
                System.Console.WriteLine();
                System.Console.WriteLine();
                break;

            case MessageStreamEvent { Inner: CompletionTextDeltaEvent e }:
                System.Console.Write(e.Delta);
                break;

            case MessageStreamEvent { Inner: CompletionTextEndEvent }:
                System.Console.WriteLine();
                break;

            case MessageStreamEvent { Inner: CompletionImageEvent e }:
                ChatRenderer.WriteInlineImage(e.Image);
                break;

            case MessageStreamEvent { Inner: CompletionErrorEvent e }:
                System.Console.Error.WriteLine($"{Red}Error: {e.Error.ErrorMessage}{Reset}");
                break;

            case MessageEndEvent { Message: AssistantMessage assistant }:
                if (assistant.StopReason is not CompletionStopReason.ToolUse)
                {
                    ChatRenderer.WriteUsage(assistant.Usage);
                }
                break;

            case ToolStartEvent e:
                var argsStr = string.Join(", ", e.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
                System.Console.WriteLine($"{Yellow}  {e.ToolName}({argsStr}){Reset}");
                break;

            case ToolEndEvent e:
                var text = string.Join("\n",
                    e.Result.Content.OfType<CompletionTextContent>().Select(c => c.Text));
                var color = e.IsError ? Red : Green;
                System.Console.WriteLine($"{color}  → {text}{Reset}");
                System.Console.WriteLine();
                break;

            case ToolSkippedEvent e:
                System.Console.WriteLine($"{Yellow}  [{e.ToolName} skipped: {e.Reason}]{Reset}");
                break;
        }
    }

    private static void WriteHeader(Model model, IReadOnlyList<AgentTool> tools)
    {
        const string Bold = "\x1b[1m";
        const string Dim = "\x1b[2m";
        const string Reset = "\x1b[0m";

        System.Console.WriteLine($"{Bold}Achates Agent{Reset}");
        System.Console.WriteLine($"Model: {model.Id} ({model.Name})");

        if (tools.Count > 0)
        {
            var names = string.Join(", ", tools.Select(t => t.Name));
            System.Console.WriteLine($"Tools: {names}");
        }

        System.Console.WriteLine($"Type {Dim}/exit{Reset} to quit.");
        System.Console.WriteLine();
    }
}
