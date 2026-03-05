using System.Globalization;
using Achates.Console;
using Achates.Providers;
using Achates.Providers.Models;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();

try
{
    return await (command switch
    {
        "models" => ListModelsAsync(args),
        "chat" => RunChatAsync(args),
        "help" or "--help" or "-h" => Task.FromResult(PrintUsage()),
        _ => Task.FromResult(PrintUnknown(command))
    });
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"HTTP error: {ex.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    return 0;
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

async Task<int> ListModelsAsync(string[] args)
{
    var filter = GetOption(args, "--filter");
    var provider = GetProvider(args);
    var models = await provider.GetModelsAsync();

    var filtered = filter is not null
        ? models.Where(m => m.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList()
        : models;

    Console.WriteLine($"Found {filtered.Count} models.");
    Console.WriteLine();
    Console.WriteLine($"{"ID",-42} {"Name",-32} {"Context",10}  $/M prompt");
    Console.WriteLine($"{new string('-', 42)} {new string('-', 32)} {new string('-', 10)}  {new string('-', 10)}");

    foreach (var model in filtered)
    {
        Console.WriteLine($"{model.Id,-42} {Truncate(model.Name, 32),-32} {model.ContextWindow,10}  {FormatCost(model.Cost.Prompt)}");
    }

    return 0;
}

async Task<int> RunChatAsync(string[] args)
{
    var modelId = GetOption(args, "--model");
    if (modelId is null)
    {
        Console.Error.WriteLine("Error: --model is required for chat.");
        return 1;
    }

    var provider = GetProvider(args);
    var models = await provider.GetModelsAsync();
    var model = models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
    if (model is null)
    {
        Console.Error.WriteLine($"Error: Model '{modelId}' not found.");
        return 1;
    }

    var systemPrompt = GetOption(args, "--system");
    var temperature = GetOption(args, "--temperature") is { } t
        ? double.Parse(t, CultureInfo.InvariantCulture)
        : (double?)null;

    return await ChatCommand.RunAsync(model, systemPrompt, temperature);
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static IModelProvider GetProvider(string[] args)
{
    var id = GetOption(args, "--provider") ?? "openrouter";

    var provider = ModelProviders.Create(id);
    if (provider is null)
    {
        Console.Error.WriteLine($"Error: Unknown provider '{id}'.");
        Environment.Exit(1);
    }

    var apiKey = GetOption(args, "--key") ?? Environment.GetEnvironmentVariable(provider.EnvironmentKey);
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.Error.WriteLine($"Error: No API key provided. Use --key or set {provider.EnvironmentKey}.");
        Environment.Exit(1);
    }

    provider.Key = apiKey;
    provider.HttpClient = new HttpClient();

    return provider;
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}

static string FormatCost(decimal perTokenRate)
{
    return perTokenRate == 0 ? "free" : $"${perTokenRate * 1_000_000m:F2}";
}

static string Truncate(string value, int maxLength) =>
    value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 1), "…");

static int PrintUsage()
{
    Console.WriteLine("""
        Usage: achates <command> [options]

        Commands:
          models [--filter <text>]                          List available models
          chat --model <id> [--system <prompt>] [--temperature <value>]
                                                            Start interactive chat

        Options:
          --provider <id>    Provider to use (default: openrouter)
          --key <key>        API key (falls back to provider environment variable)
        """);
    return 0;
}

static int PrintUnknown(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    Console.Error.WriteLine("Run 'achates help' for usage.");
    return 1;
}
