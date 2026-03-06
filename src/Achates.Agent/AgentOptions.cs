using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;

namespace Achates.Agent;

public sealed record AgentOptions
{
    /// <summary>
    /// The model to use for completions.
    /// </summary>
    public Model? Model { get; init; }

    /// <summary>
    /// System prompt for the conversation.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Tools available to the agent.
    /// </summary>
    public IReadOnlyList<AgentTool>? Tools { get; init; }

    /// <summary>
    /// Initial messages to seed the conversation with.
    /// </summary>
    public IReadOnlyList<AgentMessage>? Messages { get; init; }

    /// <summary>
    /// Options passed to the provider for each completion request.
    /// </summary>
    public CompletionOptions? CompletionOptions { get; init; }

    /// <summary>
    /// Converts agent messages to provider-level completion messages.
    /// Override this to filter, transform, or inject messages before they reach the model.
    /// Default: straightforward mapping of User/Assistant/ToolResult messages.
    /// </summary>
    public Func<IReadOnlyList<AgentMessage>, IReadOnlyList<CompletionMessage>>? ConvertToLlm { get; init; }

    /// <summary>
    /// Transforms the completion context just before it is sent to the provider.
    /// Use for context window pruning, RAG injection, cache hints, etc.
    /// </summary>
    public Func<CompletionContext, CompletionContext>? TransformContext { get; init; }

    /// <summary>
    /// Override how completions are obtained. Defaults to using the model's provider.
    /// Useful for testing, proxying, or custom routing.
    /// </summary>
    public Func<Model, CompletionContext, CompletionOptions?, CancellationToken,
        CompletionEventStream>? CompletionProvider { get; init; }
}
