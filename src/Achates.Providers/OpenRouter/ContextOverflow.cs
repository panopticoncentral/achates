using System.Text.RegularExpressions;

namespace Achates.Providers.OpenRouter;

/// <summary>
/// Detects whether an API error indicates a context-window overflow.
/// </summary>
public static class ContextOverflow
{
    private static readonly Regex[] _patterns =
    [
        Pattern(@"prompt.{0,20}(is )?too.{0,20}long"),
        Pattern(@"input.{0,20}(is )?too.{0,20}long"),
        Pattern(@"request.{0,20}too.{0,20}large"),
        Pattern(@"payload.{0,20}too.{0,20}large"),
        Pattern(@"context.{0,20}length.{0,20}exceed"),
        Pattern(@"context.{0,20}window.{0,20}(full|overflow|exceed)"),
        Pattern(@"maximum.{0,20}context.{0,20}length"),
        Pattern(@"exceeds.{0,20}(the )?max(imum)?.{0,20}(input |number of |)token"),
        Pattern(@"exceeds.{0,20}(the )?(context|available).{0,20}(window|size|length)"),
        Pattern(@"exceeds.{0,20}the.{0,20}limit.{0,20}of.{0,20}\d+"),
        Pattern(@"token.{0,20}limit.{0,20}(exceeded|reached)"),
        Pattern(@"exceeded.{0,20}model.{0,20}token.{0,20}limit"),
        Pattern(@"too.{0,20}many.{0,20}tokens"),
        Pattern(@"reduce.{0,20}(the )?(length|size|number).{0,20}(of )?(the )?(message|prompt|input|token)"),
        Pattern(@"maximum.{0,20}prompt.{0,20}length.{0,20}(is )?\d+"),
        Pattern(@"input.{0,20}token.{0,20}count.{0,20}exceeds"),
    ];

    /// <summary>
    /// Returns true if the error message text matches a known context-overflow pattern.
    /// </summary>
    public static bool IsContextOverflow(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(errorMessage))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the exception represents a context-overflow error.
    /// Checks the message text and the HTTP status code (413 Payload Too Large,
    /// or 400 with no descriptive body).
    /// </summary>
    public static bool IsContextOverflow(OpenRouterException exception)
    {
        if (IsContextOverflow(exception.Message))
        {
            return true;
        }

        return exception.Code is 413
               || (exception.Code is 400
                   && exception.Message.StartsWith("HTTP 400", StringComparison.Ordinal));
    }

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
}
