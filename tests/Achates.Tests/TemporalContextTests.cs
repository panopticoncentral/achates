using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;
using Achates.Server;

namespace Achates.Tests;

public sealed class TemporalContextTests
{
    // --- FormatNote ---

    [Fact]
    public void FormatNote_includes_current_time_when_no_previous_activity()
    {
        var tz = TimeZoneInfo.Local;
        var now = new DateTimeOffset(2026, 5, 23, 17, 14, 0, tz.GetUtcOffset(new DateTime(2026, 5, 23)));

        var note = TemporalContext.FormatNote(now, previousActivityUtc: null, TimeSpan.FromMinutes(30));

        Assert.Contains("2026", note);
        Assert.Contains("5:14", note);
        Assert.DoesNotContain("Previous message", note);
        Assert.DoesNotContain("ago", note);
    }

    [Fact]
    public void FormatNote_omits_gap_line_when_under_threshold()
    {
        var now = DateTimeOffset.Now;
        var prev = now.AddMinutes(-15).UtcDateTime;

        var note = TemporalContext.FormatNote(now, prev, TimeSpan.FromMinutes(30));

        Assert.DoesNotContain("Previous message", note);
    }

    [Fact]
    public void FormatNote_includes_gap_line_when_at_or_over_threshold()
    {
        var now = new DateTimeOffset(2026, 5, 23, 17, 14, 0, TimeSpan.FromHours(-7));
        var prev = now.AddHours(-8).AddMinutes(-12).UtcDateTime;

        var note = TemporalContext.FormatNote(now, prev, TimeSpan.FromMinutes(30));

        Assert.Contains("Previous message", note);
        Assert.Contains("8h 12m", note);
    }

    [Fact]
    public void FormatNote_minute_only_gap_under_one_hour()
    {
        var now = DateTimeOffset.UtcNow;
        var prev = now.AddMinutes(-45).UtcDateTime;

        var note = TemporalContext.FormatNote(now, prev, TimeSpan.FromMinutes(30));

        Assert.Contains("45m", note);
    }

    [Fact]
    public void FormatNote_day_format_for_multi_day_gap()
    {
        var now = new DateTimeOffset(2026, 5, 23, 17, 0, 0, TimeSpan.FromHours(-7));
        var prev = now.AddDays(-2).AddHours(-3).UtcDateTime;

        var note = TemporalContext.FormatNote(now, prev, TimeSpan.FromMinutes(30));

        Assert.Contains("2d 3h", note);
    }

    [Fact]
    public void FormatNote_returns_bracketed_form()
    {
        var note = TemporalContext.FormatNote(DateTimeOffset.UtcNow, null, TimeSpan.FromMinutes(30));

        Assert.StartsWith("[", note);
        Assert.EndsWith("]", note);
    }

    // --- CreateTransform ---

    [Fact]
    public void Transform_is_noop_when_no_messages()
    {
        var transform = TemporalContext.CreateTransform();
        var context = new CompletionContext { Messages = [] };

        var result = transform(context);

        Assert.Same(context, result);
    }

    [Fact]
    public void Transform_is_noop_when_no_user_message_present()
    {
        var transform = TemporalContext.CreateTransform();
        var context = new CompletionContext
        {
            Messages =
            [
                new CompletionToolResultMessage
                {
                    ToolCallId = "t1",
                    ToolName = "x",
                    Content = [new CompletionTextContent { Text = "ok" }],
                    IsError = false,
                },
            ],
        };

        var result = transform(context);

        Assert.Same(context, result);
    }

    [Fact]
    public void Transform_injects_note_into_latest_user_text_message()
    {
        var now = new DateTimeOffset(2026, 5, 23, 17, 14, 0, TimeSpan.FromHours(-7));
        var transform = TemporalContext.CreateTransform(clock: () => now);

        var context = new CompletionContext
        {
            Messages =
            [
                new CompletionUserTextMessage { Text = "hello", Timestamp = 1000 },
            ],
        };

        var result = transform(context);

        Assert.NotSame(context, result);
        var injected = (CompletionUserTextMessage)result.Messages[0];
        Assert.StartsWith("[", injected.Text);
        Assert.Contains("hello", injected.Text);
    }

    [Fact]
    public void Transform_caches_note_within_same_user_turn()
    {
        var now = DateTimeOffset.UtcNow;
        var clockCalls = 0;
        var transform = TemporalContext.CreateTransform(clock: () =>
        {
            clockCalls++;
            return now;
        });

        var context = new CompletionContext
        {
            Messages = [new CompletionUserTextMessage { Text = "hi", Timestamp = 100 }],
        };

        var firstCall = transform(context);
        var firstText = ((CompletionUserTextMessage)firstCall.Messages[0]).Text;

        // Second iteration in same turn: added an assistant + tool result, but
        // the latest user message (timestamp 100) is unchanged.
        var contextIter2 = new CompletionContext
        {
            Messages =
            [
                new CompletionUserTextMessage { Text = "hi", Timestamp = 100 },
                new CompletionToolResultMessage
                {
                    ToolCallId = "t1", ToolName = "x",
                    Content = [new CompletionTextContent { Text = "result" }],
                    IsError = false, Timestamp = 200,
                },
            ],
        };
        var secondCall = transform(contextIter2);
        var secondText = ((CompletionUserTextMessage)secondCall.Messages[0]).Text;

        Assert.Equal(firstText, secondText);
        Assert.Equal(1, clockCalls); // clock not consulted on the second iteration
    }

    [Fact]
    public void Transform_recomputes_note_on_new_user_turn()
    {
        var clockValues = new Queue<DateTimeOffset>(new[]
        {
            new DateTimeOffset(2026, 5, 23, 9, 0, 0, TimeSpan.FromHours(-7)),
            new DateTimeOffset(2026, 5, 23, 17, 14, 0, TimeSpan.FromHours(-7)),
        });
        var transform = TemporalContext.CreateTransform(clock: () => clockValues.Dequeue());

        var first = transform(new CompletionContext
        {
            Messages = [new CompletionUserTextMessage { Text = "morning", Timestamp = 100 }],
        });

        // New user turn: a second user message has been added with a different timestamp.
        var second = transform(new CompletionContext
        {
            Messages =
            [
                new CompletionUserTextMessage { Text = "morning", Timestamp = 100 },
                new CompletionUserTextMessage { Text = "evening", Timestamp = 200 },
            ],
        });

        var firstText = ((CompletionUserTextMessage)first.Messages[0]).Text;
        var secondText = ((CompletionUserTextMessage)second.Messages[1]).Text;

        Assert.Contains("9:00", firstText);
        Assert.Contains("5:14", secondText);
        // First user message should be untouched in the second context.
        var unchangedFirst = ((CompletionUserTextMessage)second.Messages[0]).Text;
        Assert.Equal("morning", unchangedFirst);
    }

    [Fact]
    public void Transform_includes_gap_when_long_pause_between_turns()
    {
        // Morning user turn at 9am (Unix ms).
        var morningTs = new DateTimeOffset(2026, 5, 23, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        // Evening: 5:14pm UTC, ~8h 14m later.
        var eveningNow = new DateTimeOffset(2026, 5, 23, 17, 14, 0, TimeSpan.Zero);
        var eveningTs = eveningNow.AddSeconds(-1).ToUnixTimeMilliseconds(); // just before "now"

        var transform = TemporalContext.CreateTransform(clock: () => eveningNow);

        var context = new CompletionContext
        {
            Messages =
            [
                new CompletionUserTextMessage { Text = "good morning", Timestamp = morningTs },
                new CompletionAssistantMessage
                {
                    Content = [new CompletionTextContent { Text = "morning!" }],
                    Model = "stub",
                    CompletionUsage = CompletionUsage.Empty,
                    CompletionStopReason = CompletionStopReason.Stop,
                    Timestamp = morningTs + 60_000,
                },
                new CompletionUserTextMessage { Text = "how was today?", Timestamp = eveningTs },
            ],
        };

        var result = transform(context);
        var latest = ((CompletionUserTextMessage)result.Messages[^1]).Text;

        Assert.Contains("Previous message", latest);
        Assert.Contains("8h", latest);
        Assert.Contains("how was today?", latest);
    }

    [Fact]
    public void Transform_injects_into_content_message_by_prepending_text_block()
    {
        var transform = TemporalContext.CreateTransform(clock: () => DateTimeOffset.UtcNow);

        var context = new CompletionContext
        {
            Messages =
            [
                new CompletionUserContentMessage
                {
                    Content =
                    [
                        new CompletionTextContent { Text = "describe this" },
                    ],
                    Timestamp = 100,
                },
            ],
        };

        var result = transform(context);
        var injected = (CompletionUserContentMessage)result.Messages[0];

        Assert.Equal(2, injected.Content.Count);
        var firstBlock = (CompletionTextContent)injected.Content[0];
        Assert.StartsWith("[", firstBlock.Text);
        var secondBlock = (CompletionTextContent)injected.Content[1];
        Assert.Equal("describe this", secondBlock.Text);
    }

    [Fact]
    public void Transform_does_not_mutate_original_context()
    {
        var transform = TemporalContext.CreateTransform();
        var originalMessage = new CompletionUserTextMessage { Text = "original", Timestamp = 100 };
        var context = new CompletionContext { Messages = [originalMessage] };

        _ = transform(context);

        // The original message reference is unchanged.
        Assert.Equal("original", originalMessage.Text);
        Assert.Equal("original", ((CompletionUserTextMessage)context.Messages[0]).Text);
    }
}
