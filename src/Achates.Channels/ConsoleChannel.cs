namespace Achates.Channels;

/// <summary>
/// A channel that reads from stdin and writes to stdout.
/// The simplest possible channel — useful for development and testing.
/// </summary>
public sealed class ConsoleChannel : IChannel
{
    private readonly string _prompt;
    private Task? _readLoop;
    private CancellationTokenSource? _cts;

    public ConsoleChannel(string prompt = "> ")
    {
        _prompt = prompt;
    }

    public string Id => "console";
    public string DisplayName => "Console";

    public event Func<ChannelMessage, Task>? MessageReceived;

    public Task SendAsync(ChannelMessage message, CancellationToken cancellationToken = default)
    {
        // Console output is handled by the gateway's event subscriber (for streaming).
        // This is called for the final assembled response — write it if no streaming renderer is attached.
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_readLoop is not null)
        {
            try { await _readLoop; }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Write the prompt indicator. Call this from your rendering code
    /// after each response completes to show the input prompt again.
    /// </summary>
    public void WritePrompt()
    {
        Console.Write(_prompt);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            WritePrompt();
            var line = await Task.Run(Console.ReadLine, cancellationToken);

            if (line is null)
                break;

            if (string.Equals(line.Trim(), "/exit", StringComparison.OrdinalIgnoreCase))
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (MessageReceived is { } handler)
            {
                await handler(new ChannelMessage
                {
                    ChannelId = Id,
                    PeerId = "local",
                    Text = line,
                });
            }
        }
    }
}
