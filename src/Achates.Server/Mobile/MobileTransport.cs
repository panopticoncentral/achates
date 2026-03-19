using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Achates.Agent;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Server.Cron;
using Achates.Server.Tools;

namespace Achates.Server.Mobile;

/// <summary>
/// WebSocket handler for /ws connections. Supports multiple concurrent clients.
/// All clients share the same session namespace — sessions are per-agent, not per-client.
/// Manages the read loop, RPC dispatch, agent event streaming, and session persistence.
/// </summary>
public sealed class MobileTransport(
    IReadOnlyDictionary<string, AgentDefinition> agents,
    MobileSessionStore sessionStore,
    ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ILogger _logger = loggerFactory.CreateLogger<MobileTransport>();
    private readonly ConcurrentDictionary<string, MobileConnection> _connections = new();
    private readonly ConcurrentDictionary<string, AgentRuntime> _runtimes = new();

    public CronService? CronService { get; set; }

    /// <summary>
    /// All currently connected clients.
    /// </summary>
    public ICollection<MobileConnection> Connections => _connections.Values;

    /// <summary>
    /// Handle a WebSocket connection. Runs the read loop until the socket closes or is cancelled.
    /// </summary>
    public async Task HandleConnectionAsync(WebSocket socket, CancellationToken ct)
    {
        var connectionId = Guid.NewGuid().ToString("N")[..8];
        var connection = new MobileConnection(socket, connectionId, loggerFactory);
        _connections[connectionId] = connection;
        _logger.LogInformation("Connection {Id} opened", connectionId);

        try
        {
            await ReadLoopAsync(connection, ct);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogDebug("Connection {Id} closed prematurely", connectionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Connection {Id} cancelled", connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection {Id} error", connectionId);
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            connection.Dispose();
            _logger.LogInformation("Connection {Id} closed", connectionId);
        }
    }

    /// <summary>
    /// Broadcast an event to all connected clients.
    /// </summary>
    public async Task BroadcastEventAsync(string eventName, object? payload, CancellationToken ct = default)
    {
        foreach (var conn in _connections.Values)
        {
            try
            {
                await conn.SendEventAsync(eventName, payload, ct);
            }
            catch { /* best effort */ }
        }
    }

    private async Task ReadLoopAsync(MobileConnection connection, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream();

        while (connection.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            messageBuffer.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                result = await connection.Socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                messageBuffer.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);

            Frame frame;
            try
            {
                frame = FrameParser.Parse(json);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse frame from connection {Id}", connection.ConnectionId);
                continue;
            }

            switch (frame)
            {
                case RequestFrame request:
                    await DispatchRequestAsync(connection, request, ct);
                    break;

                case ResponseFrame response:
                    if (!connection.CompleteRequest(response.Id, response))
                    {
                        _logger.LogWarning("No pending request for response {Id} from connection {ConnId}",
                            response.Id, connection.ConnectionId);
                    }
                    break;

                default:
                    _logger.LogWarning("Unexpected frame type from connection {Id}: {Type}",
                        connection.ConnectionId, frame.Type);
                    break;
            }
        }
    }

    private async Task DispatchRequestAsync(MobileConnection connection, RequestFrame request, CancellationToken ct)
    {
        try
        {
            ResponseFrame? response = request.Method switch
            {
                "connect" => HandleConnect(connection, request),
                "ping" => HandlePing(request),
                "agents.list" => HandleAgentsList(request),
                "sessions.list" => await HandleSessionsListAsync(request, ct),
                "sessions.new" => await HandleSessionsNewAsync(request, ct),
                "sessions.get" or "sessions.switch" => await HandleSessionsGetAsync(request, ct),
                "sessions.delete" => await HandleSessionsDeleteAsync(request, ct),
                "sessions.update" or "sessions.rename" => await HandleSessionsUpdateAsync(request, ct),
                "chat.send" => await HandleChatSendAsync(connection, request, ct),
                "chat.cancel" => HandleChatCancel(request),
                _ => ResponseFrame.Failure(request.Id, "unknown_method", $"Unknown method: {request.Method}"),
            };

            if (response is not null)
                await connection.SendResponseAsync(response, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching {Method} from connection {Id}", request.Method, connection.ConnectionId);
            var errorResponse = ResponseFrame.Failure(request.Id, "internal_error", ex.Message);
            await connection.SendResponseAsync(errorResponse, ct);
        }
    }

    private ResponseFrame HandleConnect(MobileConnection connection, RequestFrame request)
    {
        if (request.Params.ValueKind == JsonValueKind.Object
            && request.Params.TryGetProperty("capabilities", out var caps)
            && caps.ValueKind == JsonValueKind.Array)
        {
            connection.Capabilities.Clear();
            foreach (var cap in caps.EnumerateArray())
            {
                if (cap.GetString() is { } c)
                    connection.Capabilities.Add(c);
            }
            _logger.LogInformation("Connection {Id} capabilities: {Caps}",
                connection.ConnectionId, string.Join(", ", connection.Capabilities));
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            protocol_version = 2,
            connection_id = connection.ConnectionId,
        }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private static ResponseFrame HandlePing(RequestFrame request)
    {
        var payload = JsonSerializer.SerializeToElement(new { ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private ResponseFrame HandleAgentsList(RequestFrame request)
    {
        var agentList = agents.Select(a => new
        {
            name = a.Key,
            model = a.Value.Model.Id,
            tools = a.Value.Tools.Select(t => t.Name).ToArray(),
        }).ToList();

        var payload = JsonSerializer.SerializeToElement(new { agents = agentList }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsListAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        if (!agents.ContainsKey(agentName))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        var sessions = await sessionStore.ListAsync(agentName, ct);
        var payload = JsonSerializer.SerializeToElement(new { sessions }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsNewAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        if (!agents.TryGetValue(agentName, out var agentDef))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var session = new MobileSession { Id = sessionId };
        await sessionStore.SaveAsync(agentName, session, ct);

        var runtimeKey = $"{agentName}:{sessionId}";
        var runtime = CreateRuntime(agentDef, agentName);
        _runtimes[runtimeKey] = runtime;

        var payload = JsonSerializer.SerializeToElement(new
        {
            id = sessionId,
            title = (string?)null,
            message_count = 0,
            preview = "",
        }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsGetAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        if (agentName is null || sessionId is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'session_id' parameter.");

        var session = await sessionStore.LoadAsync(agentName, sessionId, ct);
        if (session is null)
            return ResponseFrame.Failure(request.Id, "not_found", $"Session '{sessionId}' not found.");

        var payload = JsonSerializer.SerializeToElement(session, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsDeleteAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        if (agentName is null || sessionId is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'session_id' parameter.");

        var runtimeKey = $"{agentName}:{sessionId}";
        if (_runtimes.TryRemove(runtimeKey, out var runtime))
            runtime.Abort();

        await sessionStore.DeleteAsync(agentName, sessionId, ct);
        var payload = JsonSerializer.SerializeToElement(new { deleted = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsUpdateAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        var title = GetStringParam(request.Params, "title");
        if (agentName is null || sessionId is null || title is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent', 'session_id', or 'title' parameter.");

        await sessionStore.UpdateMetadataAsync(agentName, sessionId, title, ct);
        var payload = JsonSerializer.SerializeToElement(new { updated = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame?> HandleChatSendAsync(MobileConnection connection, RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var text = GetStringParam(request.Params, "text");
        var sessionId = GetStringParam(request.Params, "session_id");
        if (agentName is null || text is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'text' parameter.");

        if (!agents.TryGetValue(agentName, out var agentDef))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        sessionId ??= Guid.NewGuid().ToString("N")[..12];
        var runtimeKey = $"{agentName}:{sessionId}";

        // Get or create the runtime for this session
        if (!_runtimes.TryGetValue(runtimeKey, out var runtime))
        {
            // Try to load existing session messages
            var session = await sessionStore.LoadAsync(agentName, sessionId, ct);
            runtime = session is not null && session.Messages.Count > 0
                ? CreateRuntime(agentDef, agentName, session.Messages)
                : CreateRuntime(agentDef, agentName);
            _runtimes[runtimeKey] = runtime;
        }

        // If already running, queue as follow-up
        if (runtime.IsRunning)
        {
            runtime.FollowUp(new UserMessage { Text = text });
            var payload = JsonSerializer.SerializeToElement(new { session_id = sessionId, queued = true }, JsonOptions);
            return ResponseFrame.Success(request.Id, payload);
        }

        // Send immediate response to the requesting client
        var responsePayload = JsonSerializer.SerializeToElement(new { session_id = sessionId }, JsonOptions);
        var response = ResponseFrame.Success(request.Id, responsePayload);
        await connection.SendResponseAsync(response, ct);

        // Stream the agent response as events to all connected clients
        _ = StreamAgentResponseAsync(runtime, agentName, sessionId, text, ct);

        return null;
    }

    private ResponseFrame HandleChatCancel(RequestFrame request)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        var runtimeKey = sessionId is not null ? $"{agentName}:{sessionId}" : null;
        AgentRuntime? runtime = null;

        if (runtimeKey is not null)
            _runtimes.TryGetValue(runtimeKey, out runtime);

        if (runtime is null || !runtime.IsRunning)
        {
            var payload = JsonSerializer.SerializeToElement(new { cancelled = false }, JsonOptions);
            return ResponseFrame.Success(request.Id, payload);
        }

        runtime.Abort();
        var result = JsonSerializer.SerializeToElement(new { cancelled = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, result);
    }

    private async Task StreamAgentResponseAsync(
        AgentRuntime runtime, string agentName, string sessionId, string text, CancellationToken ct)
    {
        try
        {
            var stream = runtime.PromptAsync(text);

            await foreach (var evt in stream.WithCancellation(ct))
            {
                switch (evt)
                {
                    case MessageStreamEvent { Inner: CompletionTextDeltaEvent delta }:
                        await BroadcastEventAsync("text.delta", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            delta = delta.Delta,
                        }, ct);
                        break;

                    case MessageStreamEvent { Inner: CompletionThinkingDeltaEvent thinking }:
                        await BroadcastEventAsync("thinking.delta", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            delta = thinking.Delta,
                        }, ct);
                        break;

                    case MessageStreamEvent { Inner: CompletionThinkingEndEvent }:
                        await BroadcastEventAsync("thinking.end", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                        }, ct);
                        break;

                    case ToolStartEvent toolStart:
                        await BroadcastEventAsync("tool.start", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            tool_call_id = toolStart.ToolCallId,
                            tool_name = toolStart.ToolName,
                        }, ct);
                        break;

                    case ToolEndEvent toolEnd:
                        await BroadcastEventAsync("tool.end", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            tool_call_id = toolEnd.ToolCallId,
                            tool_name = toolEnd.ToolName,
                            is_error = toolEnd.IsError,
                        }, ct);
                        break;

                    case MessageEndEvent { Message: AssistantMessage assistantMsg }:
                        if (agents.TryGetValue(agentName, out var agentDef) && agentDef.CostLedger is { } costLedger)
                        {
                            _ = costLedger.AppendAsync(new CostEntry
                            {
                                Timestamp = DateTimeOffset.UtcNow,
                                Model = assistantMsg.Model,
                                Channel = agentName,
                                Peer = "shared",
                                InputTokens = assistantMsg.Usage.Input,
                                OutputTokens = assistantMsg.Usage.Output,
                                CacheReadTokens = assistantMsg.Usage.CacheRead,
                                CacheWriteTokens = assistantMsg.Usage.CacheWrite,
                                CostTotal = assistantMsg.Usage.Cost.Total,
                                CostInput = assistantMsg.Usage.Cost.Input,
                                CostOutput = assistantMsg.Usage.Cost.Output,
                                CostCacheRead = assistantMsg.Usage.Cost.CacheRead,
                                CostCacheWrite = assistantMsg.Usage.Cost.CacheWrite,
                            });
                        }

                        await BroadcastEventAsync("message.end", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            usage = new
                            {
                                input = assistantMsg.Usage.Input,
                                output = assistantMsg.Usage.Output,
                                cost = assistantMsg.Usage.Cost.Input + assistantMsg.Usage.Cost.Output,
                            },
                        }, ct);

                        if (assistantMsg.StopReason is not CompletionStopReason.ToolUse)
                        {
                            await BroadcastEventAsync("text.end", new
                            {
                                agent = agentName,
                                session_id = sessionId,
                            }, ct);
                        }
                        break;

                    case AgentEndEvent:
                        var session = new MobileSession
                        {
                            Id = sessionId,
                            Messages = [.. runtime.Messages],
                        };
                        await sessionStore.SaveAsync(agentName, session, ct);

                        await BroadcastEventAsync("done", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                        }, ct);

                        if (session.Title is null && session.Messages.Count >= 2)
                        {
                            _ = Task.Run(() => AutoNameSessionAsync(agentName, session, ct), ct);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Agent stream cancelled for {Agent}/{Session}", agentName, sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming agent response for {Agent}/{Session}", agentName, sessionId);
            try
            {
                await BroadcastEventAsync("error", new
                {
                    agent = agentName,
                    session_id = sessionId,
                    error = ex.Message,
                }, ct);
            }
            catch { /* best effort */ }
        }
    }

    private static readonly string SharedMemoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates", "memory.md");

    private AgentRuntime CreateRuntime(AgentDefinition agentDef, string agentName,
        IReadOnlyList<AgentMessage>? messages = null)
    {
        var tools = new List<AgentTool>(agentDef.Tools);

        tools.Add(new MemoryTool(SharedMemoryPath, agentDef.MemoryPath));
        if (agentDef.TodoPath is { } todoPath)
            tools.Add(new TodoTool(todoPath));
        if (agentDef.CostLedger is { } costLedger)
            tools.Add(new CostTool(costLedger));
        if (agentDef.CronStore is { } cronStore && CronService is { } cron)
            tools.Add(new CronTool(cronStore, agentName, cron));

        return new AgentRuntime(new AgentOptions
        {
            Model = agentDef.Model,
            SystemPrompt = agentDef.SystemPrompt,
            Tools = tools,
            CompletionOptions = agentDef.CompletionOptions,
            Messages = messages,
        });
    }

    private async Task AutoNameSessionAsync(string agentName, MobileSession session, CancellationToken ct)
    {
        try
        {
            if (!agents.TryGetValue(agentName, out var agentDef))
                return;

            var userText = session.Messages.OfType<UserMessage>().FirstOrDefault()?.Text;
            var assistantText = session.Messages.OfType<AssistantMessage>().FirstOrDefault()
                ?.Content.OfType<CompletionTextContent>().FirstOrDefault()?.Text;

            if (userText is null)
                return;

            var snippet = $"User: {Truncate(userText, 200)}";
            if (assistantText is not null)
                snippet += $"\nAssistant: {Truncate(assistantText, 200)}";

            var namingRuntime = new AgentRuntime(new AgentOptions
            {
                Model = agentDef.Model,
                SystemPrompt = "Generate a short title (3-6 words) for the following conversation. "
                    + "Return ONLY the title text, nothing else. No quotes, no punctuation at the end.",
                Tools = [],
                CompletionOptions = agentDef.CompletionOptions,
            });

            var title = "";
            var stream = namingRuntime.PromptAsync(snippet);
            await foreach (var evt in stream.WithCancellation(ct))
            {
                if (evt is MessageStreamEvent { Inner: CompletionTextDeltaEvent delta })
                    title += delta.Delta;
            }

            title = title.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(title))
                return;

            await sessionStore.UpdateMetadataAsync(agentName, session.Id, title, ct);

            await BroadcastEventAsync("session.renamed", new
            {
                agent = agentName,
                session_id = session.Id,
                title,
            }, ct);

            _logger.LogDebug("Auto-named session {Agent}/{Session}: {Title}", agentName, session.Id, title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-name session {Agent}/{Session}", agentName, session.Id);
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static string? GetStringParam(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }
}
