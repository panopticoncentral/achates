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
using Achates.Providers.Models;
using Achates.Server.Cron;
using Achates.Server.Tools;

namespace Achates.Server.Mobile;

/// <summary>
/// WebSocket handler for /ws connections. Supports multiple concurrent clients.
/// All clients share the same session namespace — sessions are per-agent, not per-client.
/// Manages the read loop, RPC dispatch, agent event streaming, and session persistence.
/// </summary>
public sealed class MobileTransport(
    IReadOnlyDictionary<string, AgentDefinition> initialAgents,
    MobileSessionStore sessionStore,
    AgentStateCache stateCache,
    ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ILogger _logger = loggerFactory.CreateLogger<MobileTransport>();
    private readonly ConcurrentDictionary<string, AgentDefinition> _agents = new(initialAgents);
    private readonly ConcurrentDictionary<string, MobileConnection> _connections = new();
    private readonly ConcurrentDictionary<string, AgentRuntime> _runtimes = new();
    private IReadOnlyList<object>? _modelsCache;

    public CronService? CronService { get; set; }

    /// <summary>
    /// Model to use for auto-generating session titles. Falls back to the agent's model if not set.
    /// </summary>
    public Providers.Models.Model? TitleModel { get; set; }

    /// <summary>
    /// Delegate to reload an agent definition from disk. Set by GatewayService after construction.
    /// </summary>
    public Func<string, CancellationToken, Task<AgentDefinition>>? AgentReloadFunc { get; set; }

    /// <summary>
    /// Delegate to list all available models from the provider. Set by GatewayService after construction.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<Achates.Providers.Models.Model>>>? ModelsListFunc { get; set; }

    /// <summary>
    /// Delegate to generate an avatar image from a prompt. Set by GatewayService after construction.
    /// </summary>
    public Func<string, byte[]?, CancellationToken, Task<byte[]?>>? GenerateAvatarFunc { get; set; }

    /// <summary>
    /// Delegate to rename an agent (old name, new name, display name). Set by GatewayService after construction.
    /// </summary>
    public Func<string, string, string, CancellationToken, Task>? AgentRenameFunc { get; set; }

    /// <summary>
    /// Replace an agent definition and evict any cached runtimes for that agent.
    /// </summary>
    public void UpdateAgent(string name, AgentDefinition definition)
    {
        _agents[name] = definition;

        var prefix = $"{name}:";
        foreach (var key in _runtimes.Keys.Where(k => k.StartsWith(prefix)).ToList())
        {
            if (_runtimes.TryRemove(key, out var runtime))
                runtime.Abort();
        }

        stateCache.Invalidate(name);

        _ = BroadcastEventAsync("agents.changed", new
        {
            agent = name,
            reason = "agent_reloaded",
        }, CancellationToken.None);
    }

    /// <summary>
    /// Abort and remove all cached runtimes for the given agent.
    /// </summary>
    public void EvictRuntimes(string agentName)
    {
        var prefix = $"{agentName}:";
        foreach (var key in _runtimes.Keys.Where(k => k.StartsWith(prefix)).ToList())
        {
            if (_runtimes.TryRemove(key, out var runtime))
                runtime.Abort();
        }
    }

    /// <summary>
    /// Re-key an agent after rename: remove old key, insert new.
    /// Call EvictRuntimes first if runtimes need to be aborted before disk operations.
    /// </summary>
    public void RenameAgent(string oldName, string newName, AgentDefinition definition)
    {
        _agents.TryRemove(oldName, out _);
        _agents[newName] = definition;
        stateCache.Invalidate(oldName);
        stateCache.Invalidate(newName);
    }

    /// <summary>
    /// Invalidate the cached preview for an agent, forcing recomputation on next access.
    /// </summary>
    public void InvalidateAgentCache(string agentName) =>
        stateCache.Invalidate(agentName);

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
                    if (IsLongRunning(request.Method))
                        _ = DispatchLongRunningAsync(connection, request);
                    else
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
                "agents.list" => await HandleAgentsListAsync(request, ct),
                "sessions.list" => await HandleSessionsListAsync(request, ct),
                "sessions.create" => await HandleSessionsCreateAsync(request, ct),
                "sessions.get" => await HandleSessionsGetAsync(request, ct),
                "sessions.delete" => await HandleSessionsDeleteAsync(request, ct),
                "sessions.rename" => await HandleSessionsRenameAsync(request, ct),
                "sessions.delete_all" => await HandleSessionsDeleteAllAsync(request, ct),
                "chat.send" => await HandleChatSendAsync(connection, request, ct),
                "chat.cancel" => HandleChatCancel(request),
                "chat.read" => await HandleChatReadAsync(request, ct),
                "agent.get" => await HandleAgentGetAsync(request),
                "agent.update" => await HandleAgentUpdateAsync(request, ct),
                "agent.rename" => await HandleAgentRenameAsync(request, ct),
                "agent.generate_avatar" => await HandleAgentGenerateAvatarAsync(request, ct),
                "tools.list" => HandleToolsList(request),
                "models.list" => await HandleModelsListAsync(request, ct),
                "costs.summary" => await HandleCostsSummaryAsync(request, ct),
                "memory.list" => HandleMemoryList(request),
                "memory.get" => await HandleMemoryGetAsync(request, ct),
                "memory.set" => await HandleMemorySetAsync(request, ct),
                "jobs.list" => await HandleJobsListAsync(request, ct),
                "jobs.update" => await HandleJobsUpdateAsync(request, ct),
                "jobs.delete" => await HandleJobsDeleteAsync(request, ct),
                _ => ResponseFrame.Failure(request.Id, "unknown_method", $"Unknown method: {request.Method}"),
            };

            if (response is not null)
                await connection.SendResponseAsync(response, ct);
        }
        catch (Exception) when (connection.Socket.State != WebSocketState.Open)
        {
            _logger.LogDebug("Response for {Method} could not be sent (connection {Id} closed)",
                request.Method, connection.ConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching {Method} from connection {Id}", request.Method, connection.ConnectionId);
            try
            {
                var errorResponse = ResponseFrame.Failure(request.Id, "internal_error", ex.Message);
                await connection.SendResponseAsync(errorResponse, ct);
            }
            catch { /* connection may be dead */ }
        }
    }

    private static bool IsLongRunning(string method) => method is "agent.generate_avatar";

    /// <summary>
    /// Resize and compress an image to JPEG for avatar use.
    /// </summary>
    private static byte[] CompressAvatar(byte[] imageBytes, int maxSize, int quality)
    {
        using var original = SkiaSharp.SKBitmap.Decode(imageBytes);
        if (original is null)
            return imageBytes;

        var scale = Math.Min((float)maxSize / original.Width, (float)maxSize / original.Height);
        if (scale >= 1f)
        {
            // Already small enough, just re-encode as JPEG
            using var img = SkiaSharp.SKImage.FromBitmap(original);
            return img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, quality).ToArray();
        }

        var newWidth = (int)(original.Width * scale);
        var newHeight = (int)(original.Height * scale);
        using var resized = original.Resize(new SkiaSharp.SKImageInfo(newWidth, newHeight), SkiaSharp.SKSamplingOptions.Default);
        using var image = SkiaSharp.SKImage.FromBitmap(resized ?? original);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, quality).ToArray();
    }

    /// <summary>
    /// Dispatches a long-running request independently of the read loop.
    /// Uses its own cancellation and handles send failures gracefully
    /// (the connection may have died while the work was in progress).
    /// </summary>
    private async Task DispatchLongRunningAsync(MobileConnection connection, RequestFrame request)
    {
        try
        {
            await DispatchRequestAsync(connection, request, CancellationToken.None);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            _logger.LogDebug("Long-running {Method} response could not be sent (connection gone)",
                request.Method);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Long-running {Method} failed", request.Method);
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
            protocol_version = 3,
            connection_id = connection.ConnectionId,
        }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private static ResponseFrame HandlePing(RequestFrame request)
    {
        var payload = JsonSerializer.SerializeToElement(new { ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleAgentsListAsync(RequestFrame request, CancellationToken ct)
    {
        var agentList = new List<object>();

        foreach (var (name, def) in _agents)
        {
            var preview = stateCache.Get(name);
            if (preview is null)
            {
                // Cache miss — compute from disk and populate
                var (lastMessage, lastActivity) = await GetLastMessagePreviewAsync(name, ct);
                var unreadCount = await GetUnreadCountAsync(name, ct);
                preview = new AgentPreviewState(lastMessage, lastActivity, unreadCount);
                stateCache.Set(name, preview);
            }

            agentList.Add(new
            {
                name,
                display_name = def.DisplayName ?? name,
                description = def.Description ?? "",
                model = def.Model.Id,
                tools = def.Tools.Select(t => t.Name).ToArray(),
                last_message = preview.LastMessage,
                last_activity = preview.LastActivity,
                unread_count = preview.UnreadCount,
                avatar = def.AvatarData is not null ? Convert.ToBase64String(def.AvatarData) : null,
            });
        }

        var payload = JsonSerializer.SerializeToElement(new { agents = agentList }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    // --- Sessions ---

    private async Task<ResponseFrame> HandleSessionsListAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        if (!_agents.ContainsKey(agentName))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        DateTimeOffset? before = null;
        if (request.Params.TryGetProperty("before", out var beforeProp) && beforeProp.TryGetInt64(out var beforeMs))
            before = DateTimeOffset.FromUnixTimeMilliseconds(beforeMs);

        var limit = 50;
        if (request.Params.TryGetProperty("limit", out var limitProp) && limitProp.TryGetInt32(out var limitVal))
            limit = Math.Clamp(limitVal, 1, 100);

        var (sessions, hasMore) = await sessionStore.ListAsync(agentName, before, limit, ct);
        var payload = JsonSerializer.SerializeToElement(new
        {
            sessions = sessions.Select(s => new
            {
                id = s.Id,
                title = s.Title,
                preview = s.Preview,
                created = s.Created.ToUnixTimeMilliseconds(),
                updated = s.Updated.ToUnixTimeMilliseconds(),
                job_id = s.JobId,
            }),
            has_more = hasMore,
        }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsCreateAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        if (!_agents.ContainsKey(agentName))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        var session = await sessionStore.CreateAsync(agentName, ct);
        stateCache.Invalidate(agentName);

        var payload = JsonSerializer.SerializeToElement(new { id = session.Id }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsGetAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        if (agentName is null || sessionId is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'session_id' parameter.");

        if (!_agents.ContainsKey(agentName))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        var session = await sessionStore.LoadAsync(agentName, sessionId, ct);
        if (session is null)
            return ResponseFrame.Failure(request.Id, "not_found", $"Session '{sessionId}' not found.");

        var payload = JsonSerializer.SerializeToElement(new
        {
            id = session.Id,
            title = session.Title,
            created = session.Created.ToUnixTimeMilliseconds(),
            updated = session.Updated.ToUnixTimeMilliseconds(),
            messages = session.Messages,
        }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsDeleteAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        if (agentName is null || sessionId is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'session_id' parameter.");

        await sessionStore.DeleteAsync(agentName, sessionId, ct);

        var runtimeKey = $"{agentName}:{sessionId}";
        if (_runtimes.TryRemove(runtimeKey, out var runtime))
            runtime.Abort();

        stateCache.Invalidate(agentName);

        var payload = JsonSerializer.SerializeToElement(new { deleted = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsRenameAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        var title = GetStringParam(request.Params, "title");
        if (agentName is null || sessionId is null || title is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent', 'session_id', or 'title' parameter.");

        await sessionStore.UpdateMetadataAsync(agentName, sessionId, title, ct);

        _ = BroadcastEventAsync("session.updated", new
        {
            agent = agentName,
            session_id = sessionId,
            title,
        }, CancellationToken.None);

        var payload = JsonSerializer.SerializeToElement(new { renamed = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleSessionsDeleteAllAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        stateCache.Invalidate(agentName);

        // Dispose all runtimes for this agent
        var prefix = $"{agentName}:";
        foreach (var key in _runtimes.Keys.Where(k => k.StartsWith(prefix)).ToList())
        {
            if (_runtimes.TryRemove(key, out var runtime))
                runtime.Abort();
        }

        await sessionStore.DeleteAllAsync(agentName, ct);

        // Delete generated images
        var imagesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".achates", "agents", agentName, "images");
        if (Directory.Exists(imagesDir))
            Directory.Delete(imagesDir, recursive: true);

        // Notify clients so agent list preview clears
        _ = BroadcastEventAsync("agents.changed", new
        {
            agent = agentName,
            reason = "sessions_cleared",
        }, CancellationToken.None);

        var payload = JsonSerializer.SerializeToElement(new { cleared = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    // --- Chat ---

    private async Task<(string? message, string? activity)> GetLastMessagePreviewAsync(
        string agentName, CancellationToken ct)
    {
        var (sessions, _) = await sessionStore.ListAsync(agentName, limit: 1, ct: ct);
        if (sessions.Count == 0) return (null, null);

        var info = sessions[0];
        var session = await sessionStore.LoadAsync(agentName, info.Id, ct);
        if (session is null) return (null, null);

        // Walk messages in reverse to find the last user or assistant message
        for (var i = session.Messages.Count - 1; i >= 0; i--)
        {
            var msg = session.Messages[i];
            string? text = null;

            switch (msg)
            {
                case UserMessage { Hidden: false } user:
                    text = user.Text;
                    break;

                case AssistantMessage assistant:
                    text = string.Join(" ", assistant.Content
                        .OfType<CompletionTextContent>()
                        .Select(c => c.Text)
                        .Where(t => !System.Text.RegularExpressions.Regex.IsMatch(t.Trim(), @"^!\[.*?\]\(.*?\)\s*$")));
                    break;

                default:
                    continue;
            }

            if (string.IsNullOrWhiteSpace(text))
                continue;

            // Clean up: replace newlines with spaces, collapse whitespace
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

            // Truncate at word boundary
            if (text.Length > 100)
            {
                var truncated = text[..100];
                var lastSpace = truncated.LastIndexOf(' ');
                text = (lastSpace > 50 ? truncated[..lastSpace] : truncated) + "...";
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(msg.Timestamp)
                .ToString("o");

            return (text, timestamp);
        }

        return (null, null);
    }

    private string GetAgentReadStatePath(string agentName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".achates", "agents", agentName, "read-state.json");

    private async Task<long> LoadLastReadTimestampAsync(string agentName)
    {
        var path = GetAgentReadStatePath(agentName);
        if (!File.Exists(path)) return 0;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("last_read_timestamp", out var ts) && ts.TryGetInt64(out var value))
                return value;
        }
        catch { /* corrupted or invalid — treat as never read */ }
        return 0;
    }

    private async Task SaveLastReadTimestampAsync(string agentName, long timestamp)
    {
        var path = GetAgentReadStatePath(agentName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(new { last_read_timestamp = timestamp }, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private async Task<int> GetUnreadCountAsync(string agentName, CancellationToken ct)
    {
        var lastRead = await LoadLastReadTimestampAsync(agentName);
        var (sessions, _) = await sessionStore.ListAsync(agentName, limit: int.MaxValue, ct: ct);
        var count = 0;

        foreach (var info in sessions)
        {
            // Sessions are sorted by Updated desc; if this session is entirely before lastRead, stop
            if (info.Updated.ToUnixTimeMilliseconds() <= lastRead) break;

            var session = await sessionStore.LoadAsync(agentName, info.Id, ct);
            if (session is null) continue;

            for (var j = session.Messages.Count - 1; j >= 0; j--)
            {
                if (session.Messages[j].Timestamp <= lastRead) break;
                if (session.Messages[j] is AssistantMessage) count++;
            }
        }

        return count;
    }

    private async Task<ResponseFrame?> HandleChatSendAsync(MobileConnection connection, RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var text = GetStringParam(request.Params, "text");
        var sessionId = GetStringParam(request.Params, "session_id");
        if (agentName is null || text is null || sessionId is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent', 'text', or 'session_id' parameter.");

        if (!_agents.TryGetValue(agentName, out var agentDef))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        var runtimeKey = $"{agentName}:{sessionId}";

        // Get or create the runtime for this session
        if (!_runtimes.TryGetValue(runtimeKey, out var runtime))
        {
            var existing = await sessionStore.LoadAsync(agentName, sessionId, ct);
            if (existing is not null && existing.Messages.Count > 0)
                runtime = CreateRuntime(agentDef, agentName, existing.Messages);
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

    private async Task<ResponseFrame> HandleChatReadAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        if (!_agents.ContainsKey(agentName))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        if (!request.Params.TryGetProperty("timestamp", out var tsProp) || !tsProp.TryGetInt64(out var timestamp))
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing or invalid 'timestamp' parameter.");

        // Only advance forward, never backward
        var current = await LoadLastReadTimestampAsync(agentName);
        if (timestamp > current)
            await SaveLastReadTimestampAsync(agentName, timestamp);

        stateCache.MarkRead(agentName);

        var payload = JsonSerializer.SerializeToElement(new { ok = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    // --- Agent Management ---

    private async Task<ResponseFrame> HandleAgentGetAsync(RequestFrame request)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        if (!_agents.ContainsKey(agentName))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        var agentFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".achates", "agents", agentName, "AGENT.md");

        if (!File.Exists(agentFile))
            return ResponseFrame.Failure(request.Id, "not_found", "AGENT.md file not found.");

        var content = await File.ReadAllTextAsync(agentFile);
        var config = AgentLoader.Parse(content);
        if (config is null)
            return ResponseFrame.Failure(request.Id, "parse_error", "Failed to parse AGENT.md.");

        var payload = JsonSerializer.SerializeToElement(new
        {
            display_name = config.Title ?? agentName,
            description = config.Description ?? "",
            tools = config.Tools ?? [],
            reasoning_effort = config.Completion?.ReasoningEffort,
            temperature = config.Completion?.Temperature,
            max_tokens = config.Completion?.MaxTokens,
            allowed_chats = config.AllowChat ?? [],
            prompt = config.Prompt ?? "",
            has_avatar = _agents[agentName].AvatarData is not null,
        }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleAgentUpdateAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        if (!_agents.ContainsKey(agentName))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        var p = request.Params;
        var config = new AgentConfig
        {
            Description = GetStringParam(p, "description"),
            Prompt = GetStringParam(p, "prompt"),
        };

        if (p.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
            config.Tools = toolsEl.EnumerateArray()
                .Select(t => t.GetString()!)
                .Where(t => t is not null)
                .ToList();

        if (p.TryGetProperty("allowed_chats", out var chatsEl) && chatsEl.ValueKind == JsonValueKind.Array)
            config.AllowChat = chatsEl.EnumerateArray()
                .Select(c => c.GetString()!)
                .Where(c => c is not null)
                .ToList();

        if (p.TryGetProperty("reasoning_effort", out var reProp) && reProp.ValueKind == JsonValueKind.String)
        {
            config.Completion ??= new CompletionConfig();
            config.Completion.ReasoningEffort = reProp.GetString();
        }

        if (p.TryGetProperty("temperature", out var tempProp) && tempProp.ValueKind == JsonValueKind.Number)
        {
            config.Completion ??= new CompletionConfig();
            config.Completion.Temperature = tempProp.GetDouble();
        }

        if (p.TryGetProperty("max_tokens", out var mtProp) && mtProp.ValueKind == JsonValueKind.Number)
        {
            config.Completion ??= new CompletionConfig();
            config.Completion.MaxTokens = mtProp.GetInt32();
        }

        var displayName = char.ToUpper(agentName[0]) + agentName[1..];
        var markdown = AgentLoader.Serialize(displayName, config);

        var agentFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".achates", "agents", agentName, "AGENT.md");

        var tempPath = agentFile + ".tmp";
        await File.WriteAllTextAsync(tempPath, markdown, ct);
        File.Move(tempPath, agentFile, overwrite: true);

        // Handle avatar upload or removal
        var agentDir = Path.GetDirectoryName(agentFile)!;
        if (p.TryGetProperty("avatar", out var avatarProp) && avatarProp.ValueKind == JsonValueKind.String)
        {
            var avatarBytes = Convert.FromBase64String(avatarProp.GetString()!);
            await File.WriteAllBytesAsync(Path.Combine(agentDir, "avatar.jpg"), avatarBytes, ct);
            var pngPath = Path.Combine(agentDir, "avatar.png");
            if (File.Exists(pngPath)) File.Delete(pngPath);
        }
        else if (p.TryGetProperty("avatar_remove", out var removeProp) && removeProp.ValueKind == JsonValueKind.True)
        {
            var jpgPath = Path.Combine(agentDir, "avatar.jpg");
            var pngPath = Path.Combine(agentDir, "avatar.png");
            if (File.Exists(jpgPath)) File.Delete(jpgPath);
            if (File.Exists(pngPath)) File.Delete(pngPath);
        }

        string? warning = null;
        if (AgentReloadFunc is not null)
        {
            try
            {
                await AgentReloadFunc(agentName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent '{Name}' saved but reload failed", agentName);
                warning = $"Saved, but reload failed: {ex.Message}";
            }
        }

        // Notify all clients that the agent list has changed
        stateCache.Invalidate(agentName);
        _ = BroadcastEventAsync("agents.changed", new
        {
            agent = agentName,
            reason = "profile_updated",
        }, CancellationToken.None);

        var payload = JsonSerializer.SerializeToElement(new { ok = true, warning }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleAgentRenameAsync(RequestFrame request, CancellationToken ct)
    {
        if (AgentRenameFunc is null)
            return ResponseFrame.Failure(request.Id, "not_available", "Agent rename not available.");

        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        var displayName = GetStringParam(request.Params, "name");
        if (string.IsNullOrWhiteSpace(displayName))
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'name' parameter.");

        if (!_agents.ContainsKey(agentName))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        var newId = AgentLoader.NormalizeId(displayName);
        if (newId is null)
            return ResponseFrame.Failure(request.Id, "invalid_name", "Name produces an invalid or empty ID.");

        if (newId != agentName && _agents.ContainsKey(newId))
            return ResponseFrame.Failure(request.Id, "conflict", $"An agent with ID '{newId}' already exists.");

        try
        {
            await AgentRenameFunc(agentName, newId, displayName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename agent '{Old}' to '{New}'", agentName, newId);
            return ResponseFrame.Failure(request.Id, "error", $"Rename failed: {ex.Message}");
        }

        // Broadcast event
        await BroadcastEventAsync("agent.renamed", new
        {
            old_id = agentName,
            new_id = newId,
            display_name = displayName,
        }, ct);

        var payload = JsonSerializer.SerializeToElement(new { ok = true, id = newId, name = displayName }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleAgentGenerateAvatarAsync(RequestFrame request, CancellationToken ct)
    {
        if (GenerateAvatarFunc is null)
            return ResponseFrame.Failure(request.Id, "not_available", "Avatar generation not available.");

        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        if (!_agents.TryGetValue(agentName, out var def))
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found.");

        var prompt = GetStringParam(request.Params, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            var displayName = char.ToUpper(agentName[0]) + agentName[1..];
            var desc = def.Description ?? "a helpful assistant";
            prompt = $"A profile avatar for an AI assistant named {displayName}. {desc}. Clean, modern, circular icon style.";
        }

        var imageBase64 = GetStringParam(request.Params, "image");
        byte[]? referenceImage = imageBase64 is not null ? Convert.FromBase64String(imageBase64) : null;

        try
        {
            var imageBytes = await GenerateAvatarFunc(prompt, referenceImage, ct);
            if (imageBytes is null)
                return ResponseFrame.Failure(request.Id, "generation_failed", "No image was returned.");

            imageBytes = CompressAvatar(imageBytes, 512, 80);

            var payload = JsonSerializer.SerializeToElement(
                new { image = Convert.ToBase64String(imageBytes) }, JsonOptions);
            return ResponseFrame.Success(request.Id, payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Avatar generation failed");
            return ResponseFrame.Failure(request.Id, "generation_failed", ex.Message);
        }
    }

    private static ResponseFrame HandleToolsList(RequestFrame request)
    {
        var payload = JsonSerializer.SerializeToElement(
            new { tools = GatewayService.AllTools.Select(t => new { name = t.Name, label = t.Label }) },
            JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleModelsListAsync(RequestFrame request, CancellationToken ct)
    {
        if (_modelsCache is null)
        {
            if (ModelsListFunc is null)
                return ResponseFrame.Failure(request.Id, "not_available", "Model listing not available.");

            var models = await ModelsListFunc(ct);
            _modelsCache = models.Select(m => (object)new
            {
                id = m.Id,
                name = m.Name,
                context_window = m.ContextWindow,
                input = FormatModalities(m.Input),
                output = FormatModalities(m.Output),
            }).ToList();
        }

        var payload = JsonSerializer.SerializeToElement(new { models = _modelsCache }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleCostsSummaryAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        if (agentName is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' parameter.");

        if (!_agents.TryGetValue(agentName, out var agentDef) || agentDef.CostLedger is not { } ledger)
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' not found or has no cost ledger.");

        var period = GetStringParam(request.Params, "period") ?? "month";
        var now = DateTimeOffset.Now;
        DateTimeOffset? from = period switch
        {
            "today" => new DateTimeOffset(now.Date, now.Offset),
            "week" => now.AddDays(-7),
            "month" => now.AddDays(-30),
            "all" => null,
            _ => new DateTimeOffset(now.Date, now.Offset),
        };

        var entries = await ledger.QueryAsync(from);

        var byDay = entries
            .GroupBy(e => e.Timestamp.LocalDateTime.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), cost = g.Sum(e => e.CostTotal), completions = g.Count() })
            .ToList();

        var byModel = entries
            .GroupBy(e => e.Model)
            .OrderByDescending(g => g.Sum(e => e.CostTotal))
            .Select(g => new { model = g.Key, cost = g.Sum(e => e.CostTotal), completions = g.Count() })
            .ToList();

        var payload = JsonSerializer.SerializeToElement(new
        {
            total_cost = entries.Sum(e => e.CostTotal),
            completions = entries.Count,
            input_tokens = entries.Sum(e => e.InputTokens),
            output_tokens = entries.Sum(e => e.OutputTokens),
            by_day = byDay,
            by_model = byModel,
        }, JsonOptions);

        return ResponseFrame.Success(request.Id, payload);
    }

    // --- Memory ---

    private ResponseFrame HandleMemoryList(RequestFrame request)
    {
        var memories = new List<object>();

        long sharedSize = 0;
        long sharedUpdated = 0;
        if (File.Exists(SharedMemoryPath))
        {
            var fi = new FileInfo(SharedMemoryPath);
            sharedSize = fi.Length;
            sharedUpdated = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        }
        memories.Add(new { scope = "shared", size = sharedSize, updated = sharedUpdated });

        foreach (var agentName in _agents.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(AchatesHome, "agents", agentName, "memory.md");
            if (!File.Exists(path))
                continue;

            var fi = new FileInfo(path);
            memories.Add(new
            {
                scope = agentName,
                size = fi.Length,
                updated = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            });
        }

        var payload = JsonSerializer.SerializeToElement(new { memories }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private static async Task<ResponseFrame> HandleMemoryGetAsync(RequestFrame request, CancellationToken ct)
    {
        var scope = GetStringParam(request.Params, "scope");
        if (scope is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'scope' parameter.");

        var path = ResolveMemoryPath(scope);
        var content = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";

        var payload = JsonSerializer.SerializeToElement(new { content }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleMemorySetAsync(RequestFrame request, CancellationToken ct)
    {
        var scope = GetStringParam(request.Params, "scope");
        if (scope is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'scope' parameter.");

        if (!request.Params.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.String)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'content' parameter.");

        var content = contentProp.GetString() ?? "";
        var path = ResolveMemoryPath(scope);

        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content, ct);

        _ = BroadcastEventAsync("memory.updated", new { scope }, CancellationToken.None);

        var payload = JsonSerializer.SerializeToElement(new { ok = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    // --- Jobs ---

    private async Task<ResponseFrame> HandleJobsListAsync(RequestFrame request, CancellationToken ct)
    {
        var jobs = new List<object>();

        foreach (var (agentName, agentDef) in _agents)
        {
            if (agentDef.CronStore is not { } store)
                continue;

            var loaded = await store.LoadAsync(ct);
            foreach (var job in loaded)
            {
                jobs.Add(new
                {
                    agent = agentName,
                    id = job.Id,
                    name = job.Name,
                    kind = job.Kind == CronJobKind.Dreamtime ? "dreamtime" : "user",
                    schedule = ScheduleToDto(job.Schedule),
                    enabled = job.Enabled,
                    message = job.Message,
                    state = new
                    {
                        last_status = job.State.LastStatus,
                        last_run_at = job.State.LastRunAt?.ToUnixTimeMilliseconds(),
                        next_run_at = job.State.NextRunAt?.ToUnixTimeMilliseconds(),
                        last_error = job.State.LastError,
                        consecutive_errors = job.State.ConsecutiveErrors,
                    },
                });
            }
        }

        var payload = JsonSerializer.SerializeToElement(new { jobs }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleJobsUpdateAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var jobId = GetStringParam(request.Params, "id");
        if (agentName is null || jobId is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'id' parameter.");

        if (!_agents.TryGetValue(agentName, out var agentDef) || agentDef.CronStore is not { } store)
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' has no cron store.");

        if (request.Params.TryGetProperty("enabled", out var enabledProp)
            && enabledProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var enabled = enabledProp.GetBoolean();
            var updated = await store.UpdateAsync(jobId, j => j.Enabled = enabled, ct);
            if (updated is null)
                return ResponseFrame.Failure(request.Id, "not_found", $"Job '{jobId}' not found.");
        }

        _ = BroadcastEventAsync("jobs.updated", new { agent = agentName, id = jobId }, CancellationToken.None);

        var payload = JsonSerializer.SerializeToElement(new { ok = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleJobsDeleteAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var jobId = GetStringParam(request.Params, "id");
        if (agentName is null || jobId is null)
            return ResponseFrame.Failure(request.Id, "invalid_params", "Missing 'agent' or 'id' parameter.");

        if (!_agents.TryGetValue(agentName, out var agentDef) || agentDef.CronStore is not { } store)
            return ResponseFrame.Failure(request.Id, "not_found", $"Agent '{agentName}' has no cron store.");

        var existing = await store.LoadAsync(ct);
        var job = existing.FirstOrDefault(j => j.Id == jobId);
        if (job is null)
            return ResponseFrame.Failure(request.Id, "not_found", $"Job '{jobId}' not found.");

        if (job.Kind == CronJobKind.Dreamtime)
            return ResponseFrame.Failure(request.Id, "forbidden",
                "Dreamtime jobs are system-managed and cannot be deleted.");

        await store.RemoveAsync(jobId, ct);

        _ = BroadcastEventAsync("jobs.updated", new { agent = agentName, id = jobId, deleted = true }, CancellationToken.None);

        var payload = JsonSerializer.SerializeToElement(new { ok = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private static object ScheduleToDto(CronSchedule schedule) => schedule switch
    {
        CronSchedule.At at => new { type = "at", time = at.Time.ToUnixTimeMilliseconds() },
        CronSchedule.Every every => new { type = "every", minutes = every.Interval.TotalMinutes },
        CronSchedule.Cron cron => new { type = "cron", expression = cron.Expression, timezone = cron.Timezone },
        _ => new { type = "unknown" },
    };

    private static string ResolveMemoryPath(string scope) =>
        scope == "shared"
            ? SharedMemoryPath
            : Path.Combine(AchatesHome, "agents", scope, "memory.md");

    private static string[] FormatModalities(ModelModalities m)
    {
        var list = new List<string> { "text" };
        if (m.HasFlag(ModelModalities.Image)) list.Add("image");
        if (m.HasFlag(ModelModalities.File)) list.Add("file");
        if (m.HasFlag(ModelModalities.Audio)) list.Add("audio");
        if (m.HasFlag(ModelModalities.Video)) list.Add("video");
        if (m.HasFlag(ModelModalities.Embeddings)) list.Add("embeddings");
        return [.. list];
    }

    // --- Streaming ---

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

                    case MessageStreamEvent { Inner: CompletionImageEvent imageEvt }:
                        await BroadcastEventAsync("image.block", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            data = imageEvt.Image.Data,
                            mime_type = imageEvt.Image.MimeType,
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
                        // Emit image.block from tool details (e.g. ImageTool)
                        var imageUrl = (string?)null;
                        if (toolEnd.Result.Details is ImageDetails imgDetails)
                        {
                            imageUrl = imgDetails.Url;
                            await BroadcastEventAsync("image.block", new
                            {
                                agent = agentName,
                                session_id = sessionId,
                                url = imgDetails.Url,
                                data = imgDetails.Data,
                                mime_type = imgDetails.MimeType,
                            }, ct);
                        }

                        var resultText = string.Join("\n", toolEnd.Result.Content
                            .OfType<CompletionTextContent>()
                            .Select(c => c.Text));

                        await BroadcastEventAsync("tool.end", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            tool_call_id = toolEnd.ToolCallId,
                            tool_name = toolEnd.ToolName,
                            is_error = toolEnd.IsError,
                            result = string.IsNullOrEmpty(resultText) ? null : resultText,
                            image_url = imageUrl,
                        }, ct);
                        break;

                    case MessageEndEvent { Message: AssistantMessage assistantMsg }:
                        if (_agents.TryGetValue(agentName, out var agentDef) && agentDef.CostLedger is { } costLedger)
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
                        // Preserve Created timestamp and title if session already exists on disk
                        var existing = await sessionStore.LoadAsync(agentName, sessionId, ct);
                        var session = new MobileSession
                        {
                            Id = sessionId,
                            Title = existing?.Title,
                            Created = existing?.Created ?? DateTimeOffset.UtcNow,
                            Messages = [.. runtime.Messages],
                        };
                        await sessionStore.SaveAsync(agentName, session, ct);

                        // Auto-title after a few exchanges so the title reflects the conversation
                        if (session.Title is null && _agents.TryGetValue(agentName, out var titleAgentDef))
                        {
                            var assistantCount = runtime.Messages.Count(m => m is AssistantMessage);
                            if (assistantCount >= 2)
                                _ = TryGenerateTitleAsync(agentName, sessionId, titleAgentDef, runtime.Messages);
                        }

                        var preview = await ComputeAndCachePreviewAsync(agentName, sessionId, ct);
                        await BroadcastEventAsync("done", new
                        {
                            agent = agentName,
                            session_id = sessionId,
                            last_message = preview.LastMessage,
                            last_activity = preview.LastActivity,
                            unread_count = preview.UnreadCount,
                        }, ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Agent stream cancelled for {Agent}/{Session}", agentName, sessionId);
            stateCache.Invalidate(agentName);
            try
            {
                await BroadcastEventAsync("done", new
                {
                    agent = agentName,
                    session_id = sessionId,
                }, CancellationToken.None);
            }
            catch { /* best effort */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming agent response for {Agent}/{Session}", agentName, sessionId);
            stateCache.Invalidate(agentName);
            try
            {
                await BroadcastEventAsync("error", new
                {
                    agent = agentName,
                    session_id = sessionId,
                    error = ex.Message,
                }, CancellationToken.None);

                // Always send done so the client exits the typing/streaming state
                await BroadcastEventAsync("done", new
                {
                    agent = agentName,
                    session_id = sessionId,
                }, CancellationToken.None);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Generate a short title for a session using the agent's model. Best-effort, non-blocking.
    /// </summary>
    private async Task TryGenerateTitleAsync(
        string agentName, string sessionId, AgentDefinition agentDef, IReadOnlyList<AgentMessage> messages)
    {
        try
        {
            var titleRuntime = new AgentRuntime(new AgentOptions
            {
                Model = TitleModel ?? agentDef.Model,
                SystemPrompt = "Generate a short title (3-8 words) for this conversation. Respond with only the title, no quotes or punctuation.",
                Tools = [],
                CompletionOptions = new CompletionOptions { MaxTokens = 30 },
            });

            // Build a brief summary of the conversation for titling
            var summary = new StringBuilder();
            foreach (var msg in messages.Take(6))
            {
                switch (msg)
                {
                    case UserMessage user:
                        summary.AppendLine($"User: {user.Text}");
                        break;
                    case AssistantMessage assistant:
                        var text = string.Join(" ", assistant.Content.OfType<CompletionTextContent>().Select(c => c.Text));
                        if (text.Length > 200) text = text[..200] + "...";
                        summary.AppendLine($"Assistant: {text}");
                        break;
                }
            }

            var stream = titleRuntime.PromptAsync(summary.ToString());
            var titleBuilder = new StringBuilder();

            await foreach (var evt in stream)
            {
                if (evt is MessageStreamEvent { Inner: CompletionTextDeltaEvent delta })
                    titleBuilder.Append(delta.Delta);
            }

            var title = titleBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title)) return;

            // Truncate if too long
            if (title.Length > 60) title = title[..60].TrimEnd();

            await sessionStore.UpdateMetadataAsync(agentName, sessionId, title);

            await BroadcastEventAsync("session.updated", new
            {
                agent = agentName,
                session_id = sessionId,
                title,
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate title for {Agent}/{Session}", agentName, sessionId);
        }
    }

    /// <summary>
    /// Compute the preview for an agent from a just-saved session, update the cache,
    /// and return the preview for inclusion in the done event.
    /// </summary>
    private async Task<AgentPreviewState> ComputeAndCachePreviewAsync(
        string agentName, string sessionId, CancellationToken ct)
    {
        var (lastMessage, lastActivity) = await GetLastMessagePreviewAsync(agentName, ct);
        var unreadCount = await GetUnreadCountAsync(agentName, ct);
        var preview = new AgentPreviewState(lastMessage, lastActivity, unreadCount);
        stateCache.Set(agentName, preview);
        return preview;
    }

    private static readonly string AchatesHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates");

    private static readonly string SharedMemoryPath = Path.Combine(AchatesHome, "memory.md");

    private AgentRuntime CreateRuntime(AgentDefinition agentDef, string agentName,
        IReadOnlyList<AgentMessage>? messages = null)
    {
        var tools = new List<AgentTool>(agentDef.Tools);

        tools.Add(new MemoryTool(SharedMemoryPath, agentDef.MemoryPath));
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
