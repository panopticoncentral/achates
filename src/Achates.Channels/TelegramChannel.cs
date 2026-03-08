using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Achates.Channels;

/// <summary>
/// A channel that sends and receives messages via a Telegram bot using long-polling.
/// </summary>
public sealed class TelegramChannel(
    string token,
    long[]? allowedChatIds,
    ILogger<TelegramChannel> logger) : IChannel
{
    private TelegramBotClient? _bot;
    private CancellationTokenSource? _cts;
    private readonly HashSet<long>? _allowedChatIds =
        allowedChatIds is { Length: > 0 } ? new HashSet<long>(allowedChatIds) : null;

    public string Id => "telegram";
    public string DisplayName => "Telegram";

    public event Func<ChannelMessage, Task>? MessageReceived;

    public async Task SendAsync(ChannelMessage message, CancellationToken cancellationToken = default)
    {
        if (_bot is null) return;

        var chatId = long.Parse(message.PeerId);

        try
        {
            await _bot.SendMessage(chatId, message.Text,
                parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Markdown parse failed — retry as plain text
            await _bot.SendMessage(chatId, message.Text,
                cancellationToken: cancellationToken);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _bot = new TelegramBotClient(token);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message],
        };

        _bot.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: _cts.Token);

        logger.LogInformation("Telegram channel started");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        logger.LogInformation("Telegram channel stopped");
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { Text: { } text } message)
            return;

        var chatId = message.Chat.Id;

        if (_allowedChatIds is not null && !_allowedChatIds.Contains(chatId))
        {
            logger.LogDebug("Ignoring message from unlisted chat {ChatId}", chatId);
            return;
        }

        logger.LogDebug("Received message from chat {ChatId}: {Text}", chatId, text);

        if (MessageReceived is { } handler)
        {
            await handler(new ChannelMessage
            {
                ChannelId = Id,
                PeerId = chatId.ToString(),
                Text = text,
            });
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception,
        HandleErrorSource source, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Telegram polling error ({Source})", source);
        return Task.CompletedTask;
    }
}
