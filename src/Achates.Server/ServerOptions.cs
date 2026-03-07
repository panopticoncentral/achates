namespace Achates.Server;

public sealed class ServerOptions
{
    public string Provider { get; set; } = "openrouter";
    public string Model { get; set; } = "";
    public string? SystemPrompt { get; set; }
}
