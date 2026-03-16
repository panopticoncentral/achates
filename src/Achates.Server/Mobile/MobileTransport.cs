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
/// WebSocket handler for /ws/v2 mobile connections.
/// Manages the read loop, RPC dispatch, agent event streaming, and session persistence.
/// Operates independently of Gateway — not an ITransport implementation.
/// </summary>
public sealed class MobileTransport(
    IReadOnlyDictionary<string, AgentDefinition> agents,
    MobileSessionStore sessionStore,
    CronService? cronService,
    ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ILogger _logger = loggerFactory.CreateLogger<MobileTransport>();
    private volatile MobileConnection? _activeConnection;

    /// <summary>
    /// The currently connected mobile client, if any.
    /// </summary>
    public MobileConnection? ActiveConnection => _activeConnection;

    /// <summary>
    /// Handle a WebSocket connection for the given peer.
    /// Runs the read loop until the socket closes or is cancelled.
    /// </summary>
    public async Task HandleConnectionAsync(WebSocket socket, string peerId, CancellationToken ct)
    {
        var connection = new MobileConnection(socket, peerId, loggerFactory);
        _activeConnection = connection;
        _logger.LogInformation("Mobile connection opened for peer {PeerId}", peerId);

        try
        {
            await ReadLoopAsync(connection, ct);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogDebug("Mobile connection closed prematurely for peer {PeerId}", peerId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Mobile connection cancelled for peer {PeerId}", peerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mobile connection error for peer {PeerId}", peerId);
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeConnection, null, connection);
            connection.Dispose();
            _logger.LogInformation("Mobile connection closed for peer {PeerId}", peerId);
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
                _logger.LogWarning(ex, "Failed to parse frame from peer {PeerId}", connection.PeerId);
                continue;
            }

            switch (frame)
            {
                case RequestFrame request:
                    await DispatchRequestAsync(connection, request, ct);
                    break;

                case ResponseFrame response:
                    // Client responding to a server-initiated request (device commands)
                    if (!connection.CompleteRequest(response.Id, response))
                    {
                        _logger.LogWarning("No pending request for response {Id} from peer {PeerId}",
                            response.Id, connection.PeerId);
                    }
                    break;

                default:
                    _logger.LogWarning("Unexpected frame type from peer {PeerId}: {Type}",
                        connection.PeerId, frame.Type);
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
                "sessions.list" => await HandleSessionsListAsync(connection, request, ct),
                "sessions.get" => await HandleSessionsGetAsync(connection, request, ct),
                "sessions.delete" => await HandleSessionsDeleteAsync(connection, request, ct),
                "sessions.update" => await HandleSessionsUpdateAsync(connection, request, ct),
                "chat.send" => await HandleChatSendAsync(connection, request, ct),
                "chat.cancel" => HandleChatCancel(connection, request),
                _ => ResponseFrame.Failure(request.Id, "unknown_method", $"Unknown method: {request.Method}"),
            };

            // chat.send sends its own response before streaming, so it returns null
            if (response is not null)
                await connection.SendResponseAsync(response, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching {Method} from peer {PeerId}", request.Method, connection.PeerId);
            var errorResponse = ResponseFrame.Failure(request.Id, "internal_error", ex.Message);
            await connection.SendResponseAsync(errorResponse, ct);
        }
    }

    private ResponseFrame HandleConnect(MobileConnection connection, RequestFrame request)
    {
        // Parse capabilities from connect params (e.g. { "capabilities": ["location", "camera"] })
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
            _logger.LogInformation("Peer {PeerId} capabilities: {Caps}",
                connection.PeerId, string.Join(", ", connection.Capabilities));
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            protocol_version = 1,
            peer_id = connection.PeerId,
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

    private async Task<ResponseFrame> HandleSessionsListAsync(MobileConnection connection, RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        if (!agents.ContainsKey(agentName))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        var sessions = await sessionStore.ListAsync(agentName, connection.PeerId, ct);
        var payload = JsonSerializer.SerializeToElement(new { sessions }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsGetAsync(MobileConnection connection, RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        if (agentName is null || sessionId is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'session_id' parameter.");

        var session = await sessionStore.LoadAsync(agentName, connection.PeerId, sessionId, ct);
        if (session is null)
            return ResponseFrame.Failure(request.Id, "not_found", $"Session '{sessionId}' not found.");

        var payload = JsonSerializer.SerializeToElement(session, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsDeleteAsync(MobileConnection connection, RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        if (agentName is null || sessionId is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'session_id' parameter.");

        // Abort and remove the runtime if it's active
        var runtime = connection.GetRuntime(agentName);
        if (runtime is not null)
        {
            runtime.Abort();
            connection.RemoveRuntime(agentName);
        }

        await sessionStore.DeleteAsync(agentName, connection.PeerId, sessionId, ct);
        var payload = JsonSerializer.SerializeToElement(new { deleted = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsUpdateAsync(MobileConnection connection, RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        var title = GetStringParam(request.Params, "title");
        if (agentName is null || sessionId is null || title is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent', 'session_id', or 'title' parameter.");

        await sessionStore.UpdateMetadataAsync(agentName, connection.PeerId, sessionId, title, ct);
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

        // Create or get session ID
        sessionId ??= Guid.NewGuid().ToString("N")[..12];

        // Get or create the runtime for this agent
        var runtime = connection.GetRuntime(agentName);
        if (runtime is null)
        {
            runtime = CreateRuntime(agentDef, agentName, connection.PeerId, sessionId);

            // Try to load existing session messages
            var session = await sessionStore.LoadAsync(agentName, connection.PeerId, sessionId, ct);
            if (session is not null && session.Messages.Count > 0)
            {
                runtime = CreateRuntime(agentDef, agentName, connection.PeerId, sessionId, session.Messages);
            }

            connection.SetRuntime(agentName, runtime);
        }

        // If already running, queue as follow-up
        if (runtime.IsRunning)
        {
            runtime.FollowUp(new UserMessage { Text = text });
            var payload = JsonSerializer.SerializeToElement(new { session_id = sessionId, queued = true }, JsonOptions);
            return ResponseFrame.Success(request.Id, payload);
        }

        // Send immediate response with session ID, then stream events
        var responsePayload = JsonSerializer.SerializeToElement(new { session_id = sessionId }, JsonOptions);
        var response = ResponseFrame.Success(request.Id, responsePayload);
        await connection.SendResponseAsync(response, ct);

        // Stream the agent response as events (fire and forget — errors logged internally)
        _ = StreamAgentResponseAsync(connection, runtime, agentName, sessionId, text, ct);

        // Response already sent above; return null so DispatchRequestAsync skips sending
        return null;
    }

    private ResponseFrame HandleChatCancel(MobileConnection connection, RequestFrame request)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        var runtime = connection.GetRuntime(agentName);
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
        MobileConnection connection, AgentRuntime runtime, string agentName,
        string sessionId, string text, CancellationToken ct)
    {
        try
        {
            var stream = runtime.PromptAsync(text);

            await foreach (var evt in stream.WithCancellation(ct))
            {
                switch (evt)
                {
                    case MessageStreamEvent { Inner: CompletionTextDeltaEvent delta }:
                        await connection.SendEventAsync("text.delta", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            delta = delta.Delta,
                        }, ct);
                        break;

                    case MessageStreamEvent { Inner: CompletionThinkingDeltaEvent thinking }:
                        await connection.SendEventAsync("thinking.delta", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            delta = thinking.Delta,
                        }, ct);
                        break;

                    case MessageStreamEvent { Inner: CompletionThinkingEndEvent }:
                        await connection.SendEventAsync("thinking.end", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                        }, ct);
                        break;

                    case ToolStartEvent toolStart:
                        await connection.SendEventAsync("tool.start", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            tool_call_id = toolStart.ToolCallId,
                            tool_name = toolStart.ToolName,
                        }, ct);
                        break;

                    case ToolEndEvent toolEnd:
                        await connection.SendEventAsync("tool.end", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            tool_call_id = toolEnd.ToolCallId,
                            tool_name = toolEnd.ToolName,
                            is_error = toolEnd.IsError,
                        }, ct);
                        break;

                    case MessageEndEvent { Message: AssistantMessage assistantMsg }:
                        // Record cost
                        if (agents.TryGetValue(agentName, out var agentDef) && agentDef.CostLedger is { } costLedger)
                        {
                            _ = costLedger.AppendAsync(new CostEntry
                            {
                                Timestamp = DateTimeOffset.UtcNow,
                                Model = assistantMsg.Model,
                                Channel = $"{agentName}/mobile",
                                Peer = connection.PeerId,
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

                        await connection.SendEventAsync("message.end", new
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

                        // Notify text end when not continuing with tools
                        if (assistantMsg.StopReason is not CompletionStopReason.ToolUse)
                        {
                            await connection.SendEventAsync("text.end", new
                            {
                                agent = agentName,
                                session_id = sessionId,
                            }, ct);
                        }
                        break;

                    case AgentEndEvent:
                        // Persist session
                        var session = new MobileSession
                        {
                            Id = sessionId,
                            Messages = [.. runtime.Messages],
                        };
                        await sessionStore.SaveAsync(agentName, connection.PeerId, session, ct);

                        await connection.SendEventAsync("done", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                        }, ct);

                        // Auto-name session after first exchange
                        if (session.Title is null && session.Messages.Count >= 2)
                        {
                            _ = Task.Run(() => AutoNameSessionAsync(
                                connection, agentName, session, ct), ct);
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
                await connection.SendEventAsync("error", new
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

    private AgentRuntime CreateRuntime(AgentDefinition agentDef, string agentName, string peerId,
        string sessionId, IReadOnlyList<AgentMessage>? messages = null)
    {
        var tools = new List<AgentTool>(agentDef.Tools);

        // Per-session tools (mirrors Gateway.BuildSessionTools)
        tools.Add(new MemoryTool(SharedMemoryPath, agentDef.MemoryPath));
        if (agentDef.TodoPath is { } todoPath)
            tools.Add(new TodoTool(todoPath));
        if (agentDef.CostLedger is { } costLedger)
            tools.Add(new CostTool(costLedger));
        if (agentDef.CronStore is { } cronStore && cronService is { } cron)
            tools.Add(new CronTool(cronStore, agentName, $"{agentName}/mobile", peerId, cron));

        return new AgentRuntime(new AgentOptions
        {
            Model = agentDef.Model,
            SystemPrompt = agentDef.SystemPrompt,
            Tools = tools,
            CompletionOptions = agentDef.CompletionOptions,
            Messages = messages,
        });
    }

    private async Task AutoNameSessionAsync(MobileConnection connection, string agentName,
        MobileSession session, CancellationToken ct)
    {
        try
        {
            if (!agents.TryGetValue(agentName, out var agentDef))
                return;

            // Extract first user text and first assistant text
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

            // Update session metadata
            await sessionStore.UpdateMetadataAsync(agentName, connection.PeerId, session.Id, title, ct);

            // Notify client
            await connection.SendEventAsync("session.renamed", new
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
