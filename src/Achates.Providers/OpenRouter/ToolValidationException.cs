namespace Achates.Providers.OpenRouter;

/// <summary>
/// Thrown when a tool call fails validation against its declared JSON Schema.
/// </summary>
public sealed class ToolValidationException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);
