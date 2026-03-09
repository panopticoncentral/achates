using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Events;
using Achates.Providers.Models;

namespace Achates.Agent;

/// <summary>
/// A stateful AI agent that manages conversation history, tool execution, and event streaming.
/// One instance represents one conversation thread.
/// </summary>
public sealed class AgentRuntime
{
    private readonly List<AgentMessage> _messages;
    private readonly List<Func<AgentEvent, Task>> _subscribers = [];
    private readonly Queue<UserMessage> _steeringQueue = new();
    private readonly Queue<UserMessage> _followUpQueue = new();
    private readonly object _queueLock = new();
    private readonly IReadOnlyDictionary<string, object>? _metadata;

    private Model? _model;
    private string? _systemPrompt;
    private IReadOnlyList<AgentTool> _tools;
    private CompletionOptions? _completionOptions;
    private Func<IReadOnlyList<AgentMessage>, IReadOnlyList<Providers.Completions.Messages.CompletionMessage>>? _convertToLlm;
    private Func<CompletionContext, CompletionContext>? _transformContext;
    private Func<Model, CompletionContext, CompletionOptions?, CancellationToken, CompletionEventStream>? _completionProvider;

    private CancellationTokenSource? _cts;
    private Task? _runningLoop;
    private AgentState _state = AgentState.Idle;
    private string? _lastError;

    public AgentRuntime(AgentOptions? options = null)
    {
        options ??= new AgentOptions();

        _model = options.Model;
        _systemPrompt = options.SystemPrompt;
        _tools = options.Tools ?? [];
        _messages = options.Messages is not null ? [..options.Messages] : [];
        _completionOptions = options.CompletionOptions;
        _convertToLlm = options.ConvertToLlm;
        _transformContext = options.TransformContext;
        _completionProvider = options.CompletionProvider;
        _metadata = options.Metadata;
    }

    // --- State access ---

    public IReadOnlyList<AgentMessage> Messages => _messages;
    public Model? Model => _model;
    public string? SystemPrompt => _systemPrompt;
    public IReadOnlyList<AgentTool> Tools => _tools;
    public bool IsRunning => _runningLoop is { IsCompleted: false };
    public AgentState State => _state;
    public string? LastError => _lastError;

    // --- State mutation ---

    public void SetModel(Model model) => _model = model;
    public void SetSystemPrompt(string? prompt) => _systemPrompt = prompt;
    public void SetTools(IReadOnlyList<AgentTool> tools) => _tools = tools;
    public void SetCompletionOptions(CompletionOptions? options) => _completionOptions = options;

    /// <summary>
    /// Get a metadata value by key.
    /// </summary>
    public T? GetMetadata<T>(string key) where T : class =>
        _metadata is not null && _metadata.TryGetValue(key, out var value) ? value as T : null;

    public void ClearMessages()
    {
        _messages.Clear();
    }

    // --- Prompting ---

    /// <summary>
    /// Send a text prompt and run the agent loop.
    /// Returns an event stream for observing the agent's work.
    /// </summary>
    public AgentEventStream PromptAsync(string text) =>
        PromptAsync(new UserMessage { Text = text });

    /// <summary>
    /// Send a message and run the agent loop.
    /// </summary>
    public AgentEventStream PromptAsync(UserMessage message) =>
        PromptAsync([message]);

    /// <summary>
    /// Send multiple messages and run the agent loop.
    /// </summary>
    public AgentEventStream PromptAsync(IReadOnlyList<UserMessage> messages)
    {
        if (_model is null)
        {
            throw new InvalidOperationException("Model must be set before prompting.");
        }

        _state = AgentState.Running;
        _lastError = null;

        var stream = new AgentEventStream(NotifySubscribersAsync);
        var config = BuildLoopConfig();
        _cts = new CancellationTokenSource();

        _runningLoop = Task.Run(async () =>
        {
            try
            {
                await AgentLoop.RunAsync(messages, _messages, config, stream, _cts.Token)
                    .ConfigureAwait(false);
                _state = AgentState.Idle;
            }
            catch (OperationCanceledException)
            {
                _state = AgentState.Idle;
                throw;
            }
            catch (Exception ex)
            {
                _state = AgentState.Error;
                _lastError = ex.Message;
                throw;
            }
        });

        return stream;
    }

    /// <summary>
    /// Continue from the current state (retry after error, or process queued messages).
    /// </summary>
    public AgentEventStream ContinueAsync()
    {
        if (_model is null)
        {
            throw new InvalidOperationException("Model must be set before continuing.");
        }

        _state = AgentState.Running;
        _lastError = null;

        var stream = new AgentEventStream(NotifySubscribersAsync);
        var config = BuildLoopConfig();
        _cts = new CancellationTokenSource();

        _runningLoop = Task.Run(async () =>
        {
            try
            {
                await AgentLoop.ContinueAsync(_messages, config, stream, _cts.Token)
                    .ConfigureAwait(false);
                _state = AgentState.Idle;
            }
            catch (OperationCanceledException)
            {
                _state = AgentState.Idle;
                throw;
            }
            catch (Exception ex)
            {
                _state = AgentState.Error;
                _lastError = ex.Message;
                throw;
            }
        });

        return stream;
    }

    // --- Mid-conversation injection ---

    /// <summary>
    /// Queue a steering message that interrupts tool execution.
    /// Delivered after the current tool completes; remaining tools are skipped.
    /// </summary>
    public void Steer(UserMessage message)
    {
        lock (_queueLock)
        {
            _steeringQueue.Enqueue(message);
        }
    }

    /// <summary>
    /// Queue a follow-up message processed after the agent would normally stop.
    /// </summary>
    public void FollowUp(UserMessage message)
    {
        lock (_queueLock)
        {
            _followUpQueue.Enqueue(message);
        }
    }

    public bool HasQueuedMessages
    {
        get { lock (_queueLock)
            {
                return _steeringQueue.Count > 0 || _followUpQueue.Count > 0;
            }
        }
    }

    public void ClearQueues()
    {
        lock (_queueLock)
        {
            _steeringQueue.Clear();
            _followUpQueue.Clear();
        }
    }

    // --- Cancellation ---

    /// <summary>
    /// Cancel the currently running agent loop.
    /// </summary>
    public void Abort() => _cts?.Cancel();

    /// <summary>
    /// Wait for the agent to finish its current run.
    /// </summary>
    public async Task WaitForIdleAsync()
    {
        if (_runningLoop is not null)
        {
            await _runningLoop.ConfigureAwait(false);
        }
    }

    // --- Event subscription ---

    /// <summary>
    /// Subscribe to agent events. Returns an action that unsubscribes when called.
    /// </summary>
    public Action Subscribe(Func<AgentEvent, Task> handler)
    {
        _subscribers.Add(handler);
        return () => _subscribers.Remove(handler);
    }

    /// <summary>
    /// Subscribe to agent events with a synchronous handler.
    /// </summary>
    public Action Subscribe(Action<AgentEvent> handler)
    {
        return Subscribe(evt =>
        {
            handler(evt);
            return Task.CompletedTask;
        });
    }

    // --- Internals ---

    private AgentLoop.LoopConfig BuildLoopConfig()
    {
        var getCompletions = _completionProvider
            ?? ((model, context, options, ct) => model.Provider.GetCompletions(model, context, options, ct));

        return new AgentLoop.LoopConfig
        {
            Model = _model!,
            ConvertToLlm = _convertToLlm ?? MessageConversion.DefaultConvertToLlm,
            TransformContext = _transformContext,
            GetCompletions = getCompletions,
            CompletionOptions = _completionOptions,
            Tools = _tools,
            SystemPrompt = _systemPrompt,
            DequeueSteeringMessages = DequeueSteeringMessages,
            DequeueFollowUpMessages = DequeueFollowUpMessages,
        };
    }

    private IReadOnlyList<UserMessage> DequeueSteeringMessages()
    {
        lock (_queueLock)
        {
            if (_steeringQueue.Count == 0)
            {
                return [];
            }

            var result = _steeringQueue.ToList();
            _steeringQueue.Clear();
            return result;
        }
    }

    private IReadOnlyList<UserMessage> DequeueFollowUpMessages()
    {
        lock (_queueLock)
        {
            if (_followUpQueue.Count == 0)
            {
                return [];
            }

            var result = _followUpQueue.ToList();
            _followUpQueue.Clear();
            return result;
        }
    }

    private async Task NotifySubscribersAsync(AgentEvent evt)
    {
        foreach (var subscriber in _subscribers)
        {
            await subscriber(evt).ConfigureAwait(false);
        }
    }
}
