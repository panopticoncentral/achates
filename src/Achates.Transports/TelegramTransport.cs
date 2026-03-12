using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Achates.Transports;

/// <summary>
/// A transport that sends and receives messages via a Telegram bot using long-polling.
/// </summary>
public sealed class TelegramTransport(
    string token,
    long[]? allowedChatIds,
    ILogger<TelegramTransport> logger) : ITransport
{
    private TelegramBotClient? _bot;
    private CancellationTokenSource? _cts;
    private readonly HashSet<long>? _allowedChatIds =
        allowedChatIds is { Length: > 0 } ? new HashSet<long>(allowedChatIds) : null;

    public string Id => "telegram";
    public string DisplayName => "Telegram";

    public event Func<TransportMessage, Task>? MessageReceived;

    public async Task SendAsync(TransportMessage message, CancellationToken cancellationToken = default)
    {
        if (_bot is null) return;

        var chatId = long.Parse(message.PeerId);
        var html = TelegramHtmlRenderer.Convert(message.Text);

        try
        {
            await _bot.SendMessage(chatId, html,
                parseMode: ParseMode.Html, cancellationToken: cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // HTML send failed — retry as plain text
            await _bot.SendMessage(chatId, message.Text,
                cancellationToken: cancellationToken);
        }
    }

    public async Task SendTypingAsync(string peerId, CancellationToken cancellationToken = default)
    {
        if (_bot is null) return;

        var chatId = long.Parse(peerId);
        await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: cancellationToken);
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

        logger.LogInformation("Telegram transport started");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        logger.LogInformation("Telegram transport stopped");
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
            await handler(new TransportMessage
            {
                TransportId = Id,
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
