using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Achates.Agent;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Completions;
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
                "timeline.load" => await HandleTimelineLoadAsync(request, ct),
                "timeline.break.add" => await HandleTimelineBreakAddAsync(request, ct),
                "timeline.break.remove" => await HandleTimelineBreakRemoveAsync(request, ct),
                "timeline.clear" => await HandleTimelineClearAsync(request, ct),
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

    private static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(4);

    private async Task<ResponseFrame> HandleTimelineLoadAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        if (!agents.ContainsKey(agentName))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        DateTimeOffset? before = null;
        if (request.Params.TryGetProperty("before", out var beforeProp) && beforeProp.TryGetInt64(out var beforeMs))
            before = DateTimeOffset.FromUnixTimeMilliseconds(beforeMs);

        var limit = 50;
        if (request.Params.TryGetProperty("limit", out var limitProp) && limitProp.TryGetInt32(out var limitVal))
            limit = Math.Clamp(limitVal, 1, 100);

        var segments = await sessionStore.LoadTimelineAsync(agentName, before, limit, ct);
        var payload = JsonSerializer.SerializeToElement(new { segments }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleTimelineBreakAddAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        if (agentName is null || sessionId is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'session_id' parameter.");

        if (!request.Params.TryGetProperty("after_message_timestamp", out var tsProp) || !tsProp.TryGetInt64(out var afterTimestamp))
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing or invalid 'after_message_timestamp' parameter.");

        var result = await sessionStore.SplitSessionAsync(agentName, sessionId, afterTimestamp, ct);
        if (result is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Could not split session at the given timestamp.");

        // Dispose the old runtime — context has changed
        var runtimeKey = $"{agentName}:{sessionId}";
        if (_runtimes.TryRemove(runtimeKey, out var runtime))
            runtime.Abort();

        var payload = JsonSerializer.SerializeToElement(new { new_segment_id = result.Value.NewSegment.Id }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleTimelineBreakRemoveAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var segmentId = GetStringParam(request.Params, "segment_id");
        if (agentName is null || segmentId is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'segment_id' parameter.");

        // Find the predecessor before merging so we can clean up its runtime
        var segments = await sessionStore.LoadTimelineAsync(agentName, limit: int.MaxValue, ct: ct);
        string? predecessorId = null;
        for (var i = 1; i < segments.Count; i++)
        {
            if (segments[i].Id == segmentId)
            {
                predecessorId = segments[i - 1].Id;
                break;
            }
        }

        var merged = await sessionStore.MergeSessionsAsync(agentName, segmentId, ct);
        if (merged is null)
            return ResponseFrame.Failure(request.Id, "not_found", "Segment not found or has no predecessor to merge with.");

        // Dispose runtimes for both the merged segment and its predecessor
        if (predecessorId is not null && _runtimes.TryRemove($"{agentName}:{predecessorId}", out var predRuntime))
            predRuntime.Abort();
        if (_runtimes.TryRemove($"{agentName}:{segmentId}", out var segRuntime))
            segRuntime.Abort();

        var payload = JsonSerializer.SerializeToElement(new { merged_into = segmentId }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleTimelineClearAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        // Dispose all runtimes for this agent
        var prefix = $"{agentName}:";
        foreach (var key in _runtimes.Keys.Where(k => k.StartsWith(prefix)).ToList())
        {
            if (_runtimes.TryRemove(key, out var runtime))
                runtime.Abort();
        }

        // Delete all session files
        var sessions = await sessionStore.ListAsync(agentName, ct);
        foreach (var session in sessions)
            await sessionStore.DeleteAsync(agentName, session.Id, ct);

        var payload = JsonSerializer.SerializeToElement(new { cleared = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame?> HandleChatSendAsync(MobileConnection connection, RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var text = GetStringParam(request.Params, "text");
        if (agentName is null || text is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'text' parameter.");

        if (!agents.TryGetValue(agentName, out var agentDef))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        // Auto-resolve session: use latest, or create new if none exists or timed out
        var newSession = false;
        var latest = await sessionStore.GetLatestSessionAsync(agentName, ct);
        string sessionId;

        if (latest is null || DateTimeOffset.UtcNow - latest.Updated > SessionTimeout)
        {
            sessionId = Guid.NewGuid().ToString("N")[..12];
            newSession = true;
        }
        else
        {
            sessionId = latest.Id;
        }

        var runtimeKey = $"{agentName}:{sessionId}";

        // Get or create the runtime for this session
        if (!_runtimes.TryGetValue(runtimeKey, out var runtime))
        {
            if (!newSession && latest is not null && latest.Messages.Count > 0)
                runtime = CreateRuntime(agentDef, agentName, latest.Messages);
            else
                runtime = CreateRuntime(agentDef, agentName);
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
        var responsePayload = JsonSerializer.SerializeToElement(new
        {
            session_id = sessionId,
            new_session = newSession,
        }, JsonOptions);
        var response = ResponseFrame.Success(request.Id, responsePayload);
        await connection.SendResponseAsync(response, ct);

        // Stream the agent response as events to all connected clients
        _ = StreamAgentResponseAsync(runtime, agentName, sessionId, text, ct);

        return null;
    }

    private ResponseFrame HandleChatCancel(RequestFrame request)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        // Find any running runtime for this agent
        var prefix = $"{agentName}:";
        var runtime = _runtimes
            .Where(kv => kv.Key.StartsWith(prefix) && kv.Value.IsRunning)
            .Select(kv => kv.Value)
            .FirstOrDefault();

        if (runtime is null)
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
                        // Preserve Created timestamp if session already exists on disk
                        var existing = await sessionStore.LoadAsync(agentName, sessionId, ct);
                        var session = new MobileSession
                        {
                            Id = sessionId,
                            Created = existing?.Created ?? DateTimeOffset.UtcNow,
                            Messages = [.. runtime.Messages],
                        };
                        await sessionStore.SaveAsync(agentName, session, ct);

                        await BroadcastEventAsync("done", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                        }, ct);
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

    private static string? GetStringParam(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }
}
