# Inter-Agent Chat Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the persona/ping-pong `ChatTool` with a direct, one-round-per-call consult where the target's reply streams live as first-class attributed messages, persisted into the initiator's session and one continuing target session.

**Architecture:** A new stateless `ChatRoomManager` (owned by `MobileTransport`) runs one target reply per `ask`, seeding the target runtime from a deterministic continuing session so it "remembers". Attributed turns persist as a new `AgentSpeechMessage` (skipped from the initiator's rebuilt LLM context) and stream via new `agent_turn.*` WebSocket events. The initiator's copies are merged into its session at the end-of-turn save, anchored to the `chat` tool-call id.

**Tech Stack:** .NET 10 (C#, xUnit), Swift/SwiftUI (apple/Achates), WebSocket JSON (snake_case).

---

## Locked decisions (resolve spec's plan-time items)

1. **Continuing target session id is deterministic:** `"chat-" + Lowercase(Hex(SHA256(originSessionId + "|" + targetAgentId))[..6])` (12 hex chars). Load-or-create by this id under the target agent — no directory scan.
2. **New `MobileSession` fields:** `OriginSessionId` (initiator session id) and `PeerAgentId` (initiator agent id), both `string?`. Combined with existing `Source = SessionSource.Chat` so the existing reaper (max-age, no keep-N) bounds it.
3. **Initiator-side persistence:** the manager does **not** mutate the initiator runtime. It pushes attributed copies into a per-`(agentName,sessionId)` pending buffer keyed by the `chat` tool-call id; `MobileTransport`'s `AgentEndEvent` save splices them in immediately after the matching `ToolResultMessage`, then clears the buffer.
4. **Target runtime memory:** the manager reconstructs the target runtime's seed history from the continuing session's `AgentSpeechMessage`s — speaker==target → `AssistantMessage`, speaker==initiator → `UserMessage`. The continuing session stores `AgentSpeechMessage`s (for display/dreamtime); the runtime seed is derived, not the raw session.
5. **`AgentSpeechMessage` is skipped by default `MessageConversion`** so the initiator's reloaded runtime does not double-count the target's words (the tool call/result already carry them for the initiator).

## File Structure

**Server — create:**
- `src/Achates.Agent/Messages/AgentSpeechMessage.cs` — the attributed message record.
- `src/Achates.Server/Mobile/IChatSink.cs` — streaming + buffering bridge interface.
- `src/Achates.Server/Chat/ChatRoomManager.cs` — stateless per-`ask` orchestrator.

**Server — modify:**
- `src/Achates.Agent/Messages/AgentMessage.cs` — register the new derived type.
- `src/Achates.Agent/MessageConversion.cs` — explicit skip case.
- `src/Achates.Server/Mobile/MobileSession.cs` — `OriginSessionId`, `PeerAgentId`.
- `src/Achates.Server/Mobile/MobileSessionStore.cs` — deterministic chat-session helper.
- `src/Achates.Server/Tools/ChatTool.cs` — rewrite to `agents`/`ask` façade.
- `src/Achates.Server/Mobile/MobileTransport.cs` — manager construction, sink impl, pending-buffer merge at save, `agent_turn.*` plumbing, `CreateRuntime` wiring.
- `tests/Achates.Tests/ChatToolTests.cs` — adapt to new actions; add manager tests (new file).

**Server — create (tests):**
- `tests/Achates.Tests/ChatRoomManagerTests.cs`.

**iOS — modify:**
- `apple/Achates/Models/Message.swift` — `ContentBlock.agentTurn` + mutations.
- `apple/Achates/AppState.swift` — `startAgentTurn`/`appendAgentTurnDelta`/`collapseAgentTurn`.
- `apple/Achates/Connection/WebSocketClient.swift` — three `agent_turn.*` cases.
- `apple/Achates/Models/Session.swift` — parse persisted `agent_speech` block.
- `apple/Achates/Views/MessageBubble.swift` — route `.agentTurn`.

**iOS — create:**
- `apple/Achates/Views/AgentTurnView.swift` — attributed bubble.

**Docs — modify:** `CLAUDE.md` (ChatTool + session model + reaper paragraphs).

---

## Task 1: `AgentSpeechMessage` record + polymorphism + conversion skip

**Files:**
- Create: `src/Achates.Agent/Messages/AgentSpeechMessage.cs`
- Modify: `src/Achates.Agent/Messages/AgentMessage.cs:1-18`
- Modify: `src/Achates.Agent/MessageConversion.cs:13-84`
- Test: `tests/Achates.Tests/AgentSpeechMessageTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/Achates.Tests/AgentSpeechMessageTests.cs`:

```csharp
using System.Text.Json;
using Achates.Agent;
using Achates.Agent.Messages;

namespace Achates.Tests;

public sealed class AgentSpeechMessageTests
{
    [Fact]
    public void Roundtrips_through_polymorphic_AgentMessage_serialization()
    {
        AgentMessage msg = new AgentSpeechMessage
        {
            SpeakerAgentId = "val",
            SpeakerDisplayName = "Val",
            ToAgentId = "claire",
            Text = "hello",
        };

        var json = JsonSerializer.Serialize(msg);
        Assert.Contains("\"role\":\"speech\"", json);

        var back = JsonSerializer.Deserialize<AgentMessage>(json);
        var speech = Assert.IsType<AgentSpeechMessage>(back);
        Assert.Equal("val", speech.SpeakerAgentId);
        Assert.Equal("claire", speech.ToAgentId);
        Assert.Equal("hello", speech.Text);
    }

    [Fact]
    public void Is_excluded_from_llm_context()
    {
        IReadOnlyList<AgentMessage> history =
        [
            new UserMessage { Text = "hi" },
            new AgentSpeechMessage
            {
                SpeakerAgentId = "claire", SpeakerDisplayName = "Claire",
                ToAgentId = "val", Text = "secret side channel",
            },
        ];

        var llm = MessageConversion.DefaultConvertToLlm(history);

        Assert.DoesNotContain(llm, m => m.ToString()!.Contains("secret side channel"));
        Assert.Single(llm);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~AgentSpeechMessageTests"`
Expected: FAIL — `AgentSpeechMessage` does not exist (compile error).

- [ ] **Step 3: Create the record**

Create `src/Achates.Agent/Messages/AgentSpeechMessage.cs`:

```csharp
namespace Achates.Agent.Messages;

/// <summary>
/// One utterance in an inter-agent conversation, attributed to a speaker.
/// Persisted into both the initiator's and the target's sessions for display
/// and dreamtime review. Excluded from rebuilt LLM context by
/// <see cref="MessageConversion"/> (the tool call/result already carry the
/// exchange for the initiator).
/// </summary>
public sealed record AgentSpeechMessage : AgentMessage
{
    public required string SpeakerAgentId { get; init; }
    public required string SpeakerDisplayName { get; init; }
    public required string ToAgentId { get; init; }
    public required string Text { get; init; }
}
```

- [ ] **Step 4: Register the derived type**

In `src/Achates.Agent/Messages/AgentMessage.cs`, add after the existing `[JsonDerivedType(typeof(SummaryMessage), "summary")]` line:

```csharp
[JsonDerivedType(typeof(AgentSpeechMessage), "speech")]
```

- [ ] **Step 5: Add explicit skip in MessageConversion**

In `src/Achates.Agent/MessageConversion.cs`, inside the `switch (message)` in `DefaultConvertToLlm`, add before the final default/skip comment (after the `SummaryMessage` case):

```csharp
case AgentSpeechMessage:
    // Presentation/persistence artifact — never enters LLM context.
    break;
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~AgentSpeechMessageTests"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Achates.Agent/Messages/AgentSpeechMessage.cs src/Achates.Agent/Messages/AgentMessage.cs src/Achates.Agent/MessageConversion.cs tests/Achates.Tests/AgentSpeechMessageTests.cs
git commit -m "feat(chat): add AgentSpeechMessage, excluded from LLM context"
```

---

## Task 2: `MobileSession` origin fields + deterministic continuing-session helper

**Files:**
- Modify: `src/Achates.Server/Mobile/MobileSession.cs`
- Modify: `src/Achates.Server/Mobile/MobileSessionStore.cs`
- Test: `tests/Achates.Tests/ChatSessionStoreTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/Achates.Tests/ChatSessionStoreTests.cs`:

```csharp
using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class ChatSessionStoreTests
{
    [Fact]
    public void ChatSessionId_is_deterministic_per_origin_and_target()
    {
        var a = MobileSessionStore.ChatSessionId("sess-1", "claire");
        var b = MobileSessionStore.ChatSessionId("sess-1", "claire");
        var c = MobileSessionStore.ChatSessionId("sess-1", "val");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("chat-", a);
        Assert.Equal(5 + 12, a.Length);
    }

    [Fact]
    public async Task LoadOrCreateChatSession_creates_then_reuses_same_file()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "achates-chatsess-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new MobileSessionStore(tempBase);
            var s1 = await store.LoadOrCreateChatSessionAsync("claire", "sess-1", "val");
            Assert.Equal(SessionSource.Chat, s1.Source);
            Assert.Equal("sess-1", s1.OriginSessionId);
            Assert.Equal("val", s1.PeerAgentId);

            s1.Messages.Add(new Achates.Agent.Messages.UserMessage { Text = "x" });
            await store.SaveAsync("claire", s1);

            var s2 = await store.LoadOrCreateChatSessionAsync("claire", "sess-1", "val");
            Assert.Equal(s1.Id, s2.Id);
            Assert.Single(s2.Messages);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~ChatSessionStoreTests"`
Expected: FAIL — `ChatSessionId`/`LoadOrCreateChatSessionAsync`/`OriginSessionId` missing.

- [ ] **Step 3: Add fields to MobileSession**

In `src/Achates.Server/Mobile/MobileSession.cs`, add to `MobileSession` after the `Source` property:

```csharp
/// <summary>For a chat-origin session, the initiator's session id.</summary>
public string? OriginSessionId { get; set; }

/// <summary>For a chat-origin session, the initiator agent's id.</summary>
public string? PeerAgentId { get; set; }
```

- [ ] **Step 4: Add deterministic id + load-or-create to MobileSessionStore**

In `src/Achates.Server/Mobile/MobileSessionStore.cs`, add `using System.Security.Cryptography;` and `using System.Text;` if absent, then add these methods to the class:

```csharp
public static string ChatSessionId(string originSessionId, string targetAgentId)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(originSessionId + "|" + targetAgentId));
    return "chat-" + Convert.ToHexStringLower(bytes)[..12];
}

public async Task<MobileSession> LoadOrCreateChatSessionAsync(
    string targetAgentId, string originSessionId, string peerAgentId, CancellationToken ct = default)
{
    var id = ChatSessionId(originSessionId, targetAgentId);
    var existing = await LoadAsync(targetAgentId, id, ct);
    if (existing is not null) return existing;

    var session = new MobileSession
    {
        Id = id,
        Title = $"Chat with {peerAgentId}",
        Source = SessionSource.Chat,
        OriginSessionId = originSessionId,
        PeerAgentId = peerAgentId,
    };
    await SaveAsync(targetAgentId, session, ct);
    return session;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~ChatSessionStoreTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Achates.Server/Mobile/MobileSession.cs src/Achates.Server/Mobile/MobileSessionStore.cs tests/Achates.Tests/ChatSessionStoreTests.cs
git commit -m "feat(chat): deterministic continuing chat session per (origin, target)"
```

---

## Task 3: `IChatSink` interface + test fake

**Files:**
- Create: `src/Achates.Server/Mobile/IChatSink.cs`
- Test: used by Task 4 (no standalone test)

- [ ] **Step 1: Create the interface**

Create `src/Achates.Server/Mobile/IChatSink.cs`:

```csharp
using Achates.Agent.Messages;

namespace Achates.Server.Mobile;

/// <summary>
/// Bridge the <c>ChatRoomManager</c> uses to (a) stream attributed turns to the
/// initiator's live view and (b) buffer attributed copies for persistence into
/// the initiator's session at end-of-turn.
/// </summary>
public interface IChatSink
{
    Task EmitTurnStartAsync(string speakerAgentId, string speakerName, string toAgentId, CancellationToken ct);
    Task EmitTurnDeltaAsync(string delta, CancellationToken ct);
    Task EmitTurnEndAsync(string text, CancellationToken ct);

    /// <summary>Buffer one attributed message for the initiator session, anchored to the chat tool call.</summary>
    void BufferForInitiator(string toolCallId, AgentSpeechMessage message);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build Achates.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Achates.Server/Mobile/IChatSink.cs
git commit -m "feat(chat): add IChatSink bridge interface"
```

---

## Task 4: `ChatRoomManager.AskAsync` core

**Files:**
- Create: `src/Achates.Server/Chat/ChatRoomManager.cs`
- Test: `tests/Achates.Tests/ChatRoomManagerTests.cs` (create)

This task reuses the stub provider pattern already in `ChatToolTests.cs` (a `StubProvider` whose `GetCompletions` emits a single assistant turn). The manager builds the target runtime via an injected factory so tests can supply a stub-backed runtime.

- [ ] **Step 1: Write the failing test**

Create `tests/Achates.Tests/ChatRoomManagerTests.cs`:

```csharp
using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using Achates.Server.Chat;
using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class ChatRoomManagerTests
{
    private static Model StubModel(string reply) => new()
    {
        Id = "test/model", Name = "Test", Provider = new ReplyProvider(reply),
        Cost = new ModelCost { Prompt = 0, Completion = 0 },
        ContextWindow = 128_000, Input = ModelModalities.Text,
        Output = ModelModalities.Text, Parameters = ModelParameters.Tools,
    };

    private sealed class FakeSink : IChatSink
    {
        public List<string> Events { get; } = [];
        public List<(string ToolCallId, AgentSpeechMessage Msg)> Buffered { get; } = [];
        public Task EmitTurnStartAsync(string s, string n, string t, CancellationToken ct)
        { Events.Add($"start:{s}->{t}"); return Task.CompletedTask; }
        public Task EmitTurnDeltaAsync(string d, CancellationToken ct)
        { Events.Add($"delta:{d}"); return Task.CompletedTask; }
        public Task EmitTurnEndAsync(string text, CancellationToken ct)
        { Events.Add($"end:{text}"); return Task.CompletedTask; }
        public void BufferForInitiator(string toolCallId, AgentSpeechMessage m)
            => Buffered.Add((toolCallId, m));
    }

    private sealed class ReplyProvider(string reply) : IModelProvider
    {
        public string Id => "stub";
        public string Name => "Stub";
        public string EnvironmentKey => "STUB";
        public string? Key { get; set; }
        public HttpClient? HttpClient { get; set; }
        public Task<IReadOnlyList<Model>> GetModelsAsync(ModelModalities? o = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Model>>([]);
        public CompletionEventStream GetCompletions(Model model, CompletionContext context, CompletionOptions? options = null, CancellationToken ct = default)
            => CompletionEventStream.Create(stream =>
            {
                var msg = new CompletionAssistantMessage
                {
                    Content = [new CompletionTextContent { Text = reply }],
                    Model = model.Id,
                    CompletionUsage = new CompletionUsage { Cost = new CompletionUsageCost() },
                    CompletionStopReason = CompletionStopReason.Stop,
                };
                stream.Push(new CompletionTextDeltaEvent { ContentIndex = 0, Delta = reply, Partial = msg });
                stream.Push(new CompletionDoneEvent { Reason = CompletionStopReason.Stop, CompletionMessage = msg });
                stream.End();
                return Task.CompletedTask;
            });
    }

    private static (ChatRoomManager mgr, MobileSessionStore store, string baseDir) NewManager(string reply)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "achates-crm-" + Guid.NewGuid().ToString("N")[..8]);
        var store = new MobileSessionStore(baseDir);
        var mgr = new ChatRoomManager(
            store,
            targetAgentId => new AgentRuntimeFactory(StubModel(reply)));
        return (mgr, store, baseDir);
    }

    [Fact]
    public async Task Ask_persists_attributed_messages_to_one_continuing_target_session()
    {
        var (mgr, store, dir) = NewManager("Sure, here's my take.");
        try
        {
            var sink = new FakeSink();
            var reply1 = await mgr.AskAsync("val", "sess-1", "claire", "What do you think?", "tc-1", sink, default);
            Assert.Equal("Sure, here's my take.", reply1);

            await mgr.AskAsync("val", "sess-1", "claire", "And the risks?", "tc-2", sink, default);

            var id = MobileSessionStore.ChatSessionId("sess-1", "claire");
            var session = await store.LoadAsync("claire", id);
            Assert.NotNull(session);
            Assert.Equal(SessionSource.Chat, session!.Source);
            var speeches = session.Messages.OfType<AgentSpeechMessage>().ToList();
            Assert.Equal(4, speeches.Count); // 2 asks * (val line + claire line)
            Assert.Equal("val", speeches[0].SpeakerAgentId);
            Assert.Equal("claire", speeches[1].SpeakerAgentId);

            var (sessions, _) = await store.ListAsync("claire");
            Assert.Single(sessions);

            Assert.Contains("start:val->claire", sink.Events);
            Assert.Contains("delta:Sure, here's my take.", sink.Events);
            Assert.Equal(4, sink.Buffered.Count);
            Assert.Equal("tc-1", sink.Buffered[0].ToolCallId);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Ask_seeds_target_runtime_from_prior_session()
    {
        var (mgr, store, dir) = NewManager("ack");
        try
        {
            var sink = new FakeSink();
            await mgr.AskAsync("val", "s", "claire", "round one", "t1", sink, default);
            await mgr.AskAsync("val", "s", "claire", "round two", "t2", sink, default);

            var id = MobileSessionStore.ChatSessionId("s", "claire");
            var session = await store.LoadAsync("claire", id);
            var texts = session!.Messages.OfType<AgentSpeechMessage>().Select(m => m.Text).ToList();
            Assert.Equal(["round one", "ack", "round two", "ack"], texts);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~ChatRoomManagerTests"`
Expected: FAIL — `ChatRoomManager`, `AgentRuntimeFactory` do not exist.

- [ ] **Step 3: Create the runtime factory abstraction**

Create `src/Achates.Server/Chat/AgentRuntimeFactory.cs`:

```csharp
using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Providers.Models;

namespace Achates.Server.Chat;

/// <summary>
/// Builds a target <see cref="AgentRuntime"/> for one chat round, seeded with a
/// reconstructed message history. Kept injectable so tests can supply a stub model.
/// </summary>
public sealed class AgentRuntimeFactory(Model model, string? systemPrompt = null)
{
    public AgentRuntime Create(IReadOnlyList<AgentMessage> seed) => new(new AgentOptions
    {
        Model = model,
        SystemPrompt = systemPrompt,
        Messages = seed,
    });
}
```

- [ ] **Step 4: Create ChatRoomManager**

Create `src/Achates.Server/Chat/ChatRoomManager.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text;
using Achates.Agent;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Server.Mobile;

namespace Achates.Server.Chat;

/// <summary>
/// Stateless (except per-pairing locking) orchestrator for one inter-agent
/// consult round. See docs/superpowers/specs/2026-05-17-inter-agent-chat-redesign-design.md.
/// </summary>
public sealed class ChatRoomManager(
    MobileSessionStore sessionStore,
    Func<string, AgentRuntimeFactory> runtimeFactoryFor)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private SemaphoreSlim LockFor(string key)
        => _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    public async Task<string> AskAsync(
        string initiatorAgentId, string initiatorSessionId, string targetAgentId,
        string message, string toolCallId, IChatSink sink, CancellationToken ct)
    {
        var key = initiatorSessionId + "|" + targetAgentId;
        var gate = LockFor(key);
        await gate.WaitAsync(ct);
        try
        {
            var session = await sessionStore.LoadOrCreateChatSessionAsync(
                targetAgentId, initiatorSessionId, initiatorAgentId, ct);

            // Initiator's outgoing line: a completed attributed turn.
            var outgoing = new AgentSpeechMessage
            {
                SpeakerAgentId = initiatorAgentId,
                SpeakerDisplayName = initiatorAgentId,
                ToAgentId = targetAgentId,
                Text = message,
            };
            await sink.EmitTurnStartAsync(initiatorAgentId, initiatorAgentId, targetAgentId, ct);
            await sink.EmitTurnEndAsync(message, ct);
            sink.BufferForInitiator(toolCallId, outgoing);
            session.Messages.Add(outgoing);

            // Reconstruct the target runtime's memory from prior attributed turns.
            var seed = new List<AgentMessage>();
            foreach (var m in session.Messages.OfType<AgentSpeechMessage>())
            {
                if (m == outgoing) break;
                seed.Add(m.SpeakerAgentId == targetAgentId
                    ? new AssistantMessage
                    {
                        Content = [new CompletionTextContent { Text = m.Text }],
                        Model = "", Usage = Achates.Providers.Completions.CompletionUsage.Empty,
                        StopReason = Achates.Providers.Completions.CompletionStopReason.Stop,
                    }
                    : new UserMessage { Text = $"[From {m.SpeakerAgentId}]: {m.Text}" });
            }

            var runtime = runtimeFactoryFor(targetAgentId).Create(seed);

            await sink.EmitTurnStartAsync(targetAgentId, targetAgentId, initiatorAgentId, ct);
            var reply = new StringBuilder();
            string error = "";
            try
            {
                await foreach (var evt in runtime.PromptAsync($"[From {initiatorAgentId}]: {message}")
                                   .WithCancellation(ct))
                {
                    switch (evt)
                    {
                        case MessageStreamEvent { Inner: CompletionTextDeltaEvent d }:
                            reply.Append(d.Delta);
                            await sink.EmitTurnDeltaAsync(d.Delta, ct);
                            break;
                        case MessageEndEvent { Message: AssistantMessage a } when a.Error is { } e:
                            error = e;
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { error = ex.Message; }

            var replyText = reply.ToString().Trim();
            if (error.Length > 0 && replyText.Length == 0)
                replyText = $"(consult failed: {error})";

            await sink.EmitTurnEndAsync(replyText, ct);

            var incoming = new AgentSpeechMessage
            {
                SpeakerAgentId = targetAgentId,
                SpeakerDisplayName = targetAgentId,
                ToAgentId = initiatorAgentId,
                Text = replyText,
            };
            sink.BufferForInitiator(toolCallId, incoming);
            session.Messages.Add(incoming);

            await sessionStore.SaveAsync(targetAgentId, session, ct);

            return replyText;
        }
        finally
        {
            gate.Release();
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~ChatRoomManagerTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Add cancellation + error tests**

Append to `ChatRoomManagerTests.cs`:

```csharp
    private sealed class ThrowingProvider : IModelProvider
    {
        public string Id => "stub"; public string Name => "Stub"; public string EnvironmentKey => "S";
        public string? Key { get; set; } public HttpClient? HttpClient { get; set; }
        public Task<IReadOnlyList<Model>> GetModelsAsync(ModelModalities? o = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Model>>([]);
        public CompletionEventStream GetCompletions(Model m, CompletionContext c, CompletionOptions? o = null, CancellationToken ct = default)
            => throw new InvalidOperationException("network down");
    }

    [Fact]
    public async Task Ask_records_failure_text_when_target_throws()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "achates-crm-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new MobileSessionStore(baseDir);
            var model = new Model
            {
                Id = "t/m", Name = "t", Provider = new ThrowingProvider(),
                Cost = new ModelCost { Prompt = 0, Completion = 0 }, ContextWindow = 1000,
                Input = ModelModalities.Text, Output = ModelModalities.Text, Parameters = ModelParameters.Tools,
            };
            var mgr = new ChatRoomManager(store, _ => new AgentRuntimeFactory(model));
            var sink = new FakeSink();

            var reply = await mgr.AskAsync("val", "s", "claire", "hi", "t1", sink, default);

            Assert.Contains("consult failed", reply);
            Assert.Contains("network down", reply);
        }
        finally { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true); }
    }
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~ChatRoomManagerTests"`
Expected: PASS (3 tests).

- [ ] **Step 8: Commit**

```bash
git add src/Achates.Server/Chat/ tests/Achates.Tests/ChatRoomManagerTests.cs
git commit -m "feat(chat): ChatRoomManager runs one consult round, one continuing target session"
```

---

## Task 5: Rewrite `ChatTool` to `agents`/`ask` façade

**Files:**
- Modify: `src/Achates.Server/Tools/ChatTool.cs`
- Modify: `tests/Achates.Tests/ChatToolTests.cs`

- [ ] **Step 1: Adapt the existing tests first (write failing)**

In `tests/Achates.Tests/ChatToolTests.cs`: the constructor changes to `new ChatTool(selfAgentName, registry, allowList, manager, sessionId)`. Replace the `Chat_persists_target_agent_session_with_chat_source` test with an `ask`-action test, and update every `new ChatTool("self", registry, null)` call to `new ChatTool("self", registry, null, manager: null, initiatorSessionId: "s")`. Update the `Args(...)` helper calls that used `action: "chat"` to `action: "ask"`. Replace the persistence test body with:

```csharp
    [Fact]
    public async Task Ask_action_returns_target_reply_via_manager()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "achates-ct-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new Achates.Server.Mobile.MobileSessionStore(baseDir);
            var model = new Model
            {
                Id = "t/m", Name = "t", Provider = new ReplyOnce("the answer"),
                Cost = new ModelCost { Prompt = 0, Completion = 0 }, ContextWindow = 1000,
                Input = ModelModalities.Text, Output = ModelModalities.Text, Parameters = ModelParameters.Tools,
            };
            var mgr = new Achates.Server.Chat.ChatRoomManager(
                store, _ => new Achates.Server.Chat.AgentRuntimeFactory(model));
            var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
            var tool = new ChatTool("self", registry, null, mgr, "sess-1");

            var result = await tool.ExecuteAsync("tc-1",
                Args(("agent", "bob"), ("message", "hello")));

            Assert.Contains("the answer", GetText(result));
        }
        finally { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true); }
    }
```

Add a `ReplyOnce` provider helper to the test class identical to `ChatRoomManagerTests.ReplyProvider` (rename to `ReplyOnce`). Update the existing stub `StubProvider.GetCompletions` (it already emits `<<DONE>>`) — it can stay for the validation tests; only the persistence test changes.

- [ ] **Step 2: Run to verify failing**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~ChatToolTests"`
Expected: FAIL — `ChatTool` constructor signature mismatch / `ask` action unknown.

- [ ] **Step 3: Rewrite ChatTool**

Replace `src/Achates.Server/Tools/ChatTool.cs` body. Keep `AgentInfo` record, `agents` listing, allowlist (`IsAllowed`), validation. Remove the persona runtimes, the ping-pong loop, `RunAgentTurnAsync`, and the end-of-call session write. New shape:

```csharp
internal sealed class ChatTool(
    string selfAgentName,
    IReadOnlyDictionary<string, AgentInfo> agents,
    IReadOnlyList<string>? allowList,
    ChatRoomManager? manager,
    string initiatorSessionId) : AgentTool
{
    private const string ActionAgents = "agents";
    private const string ActionAsk = "ask";

    public override string Name => "chat";
    public override string Label => "Agent Chat";
    public override string Description =>
        "Talk to another agent. 'agents' lists who's available; 'ask' sends one message to an agent and returns its reply. Call 'ask' again to continue — the other agent remembers this conversation.";

    // _schema: action enum ["agents","ask"], agent (string), message (string). required ["action"].
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId, Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? ActionAgents;
        return action switch
        {
            ActionAgents => ListAgents(),
            ActionAsk => await AskAsync(toolCallId, arguments, cancellationToken),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    private async Task<AgentToolResult> AskAsync(
        string toolCallId, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        if (manager is null)
            return TextResult("Error: inter-agent chat is not available.");
        var targetName = GetString(arguments, "agent");
        if (string.IsNullOrWhiteSpace(targetName))
            return TextResult("Error: 'agent' is required.");
        if (targetName.Equals(selfAgentName, StringComparison.OrdinalIgnoreCase))
            return TextResult("Error: you cannot chat with yourself.");
        if (!agents.ContainsKey(targetName))
            return TextResult($"Error: agent '{targetName}' not found. Use action 'agents'.");
        if (!IsAllowed(targetName))
            return TextResult($"Error: you are not allowed to chat with '{targetName}'.");
        var message = GetString(arguments, "message");
        if (string.IsNullOrWhiteSpace(message))
            return TextResult("Error: 'message' is required.");

        var sink = ChatSinkAccessor.Current
            ?? throw new InvalidOperationException("No chat sink bound for this turn.");
        var reply = await manager.AskAsync(
            selfAgentName, initiatorSessionId, targetName, message, toolCallId, sink, ct);
        return TextResult(reply);
    }
}
```

Keep the existing `_schema`, `ListAgents`, `IsAllowed`, `AgentInfo`, and `TextResult`/`GetString` helpers (trim `_schema` to the two actions). Add a tiny ambient accessor (Task 6 sets it):

Create nothing new here; add to the same file:

```csharp
internal static class ChatSinkAccessor
{
    private static readonly AsyncLocal<IChatSink?> _current = new();
    public static IChatSink? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
```

(Rationale: the sink must be bound per chat.send turn — `AsyncLocal` mirrors the existing device-code-notifier pattern in `GraphClient`. Task 6 sets it around the runtime turn.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~ChatToolTests"`
Expected: PASS (validation tests + the new `ask` test). For the `ask` test, set `ChatSinkAccessor.Current` to a `FakeSink` at the top of that test (add: `ChatSinkAccessor.Current = new FakeSink();` — copy the FakeSink class into the test file or make it shared `internal`).

- [ ] **Step 5: Commit**

```bash
git add src/Achates.Server/Tools/ChatTool.cs tests/Achates.Tests/ChatToolTests.cs
git commit -m "feat(chat): ChatTool becomes agents/ask facade over ChatRoomManager"
```

---

## Task 6: Wire MobileTransport — manager, sink, pending-buffer merge, agent_turn events

**Files:**
- Modify: `src/Achates.Server/Mobile/MobileTransport.cs`
- Test: `tests/Achates.Tests/ChatTranscriptBufferTests.cs` (create)

- [ ] **Step 1: Write the failing test for the merge helper**

Create `tests/Achates.Tests/ChatTranscriptBufferTests.cs`:

```csharp
using Achates.Agent.Messages;
using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class ChatTranscriptBufferTests
{
    [Fact]
    public void Splices_buffered_speech_after_matching_tool_result()
    {
        var buffer = new ChatTranscriptBuffer();
        buffer.Add("tc-1", new AgentSpeechMessage
        { SpeakerAgentId = "val", SpeakerDisplayName = "Val", ToAgentId = "claire", Text = "q" });
        buffer.Add("tc-1", new AgentSpeechMessage
        { SpeakerAgentId = "claire", SpeakerDisplayName = "Claire", ToAgentId = "val", Text = "a" });

        var runtimeMsgs = new List<AgentMessage>
        {
            new UserMessage { Text = "user asked" },
            new AssistantMessage { Content = [], Model = "m",
                Usage = Achates.Providers.Completions.CompletionUsage.Empty,
                StopReason = Achates.Providers.Completions.CompletionStopReason.ToolUse },
            new ToolResultMessage { ToolCallId = "tc-1", ToolName = "chat", Content = [] },
            new AssistantMessage { Content = [], Model = "m",
                Usage = Achates.Providers.Completions.CompletionUsage.Empty,
                StopReason = Achates.Providers.Completions.CompletionStopReason.Stop },
        };

        var merged = buffer.Merge(runtimeMsgs);

        Assert.Equal(6, merged.Count);
        Assert.IsType<ToolResultMessage>(merged[2]);
        Assert.IsType<AgentSpeechMessage>(merged[3]);
        Assert.IsType<AgentSpeechMessage>(merged[4]);
        Assert.Equal("q", ((AgentSpeechMessage)merged[3]).Text);
        Assert.IsType<AssistantMessage>(merged[5]);
    }

    [Fact]
    public void Merge_is_noop_when_empty()
    {
        var buffer = new ChatTranscriptBuffer();
        var msgs = new List<AgentMessage> { new UserMessage { Text = "x" } };
        Assert.Single(buffer.Merge(msgs));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~ChatTranscriptBufferTests"`
Expected: FAIL — `ChatTranscriptBuffer` missing.

- [ ] **Step 3: Create ChatTranscriptBuffer**

Create `src/Achates.Server/Mobile/ChatTranscriptBuffer.cs`:

```csharp
using Achates.Agent.Messages;

namespace Achates.Server.Mobile;

/// <summary>
/// Per-session pending attributed messages, keyed by the chat tool-call id,
/// spliced into the session's saved message list right after the matching
/// <see cref="ToolResultMessage"/> at end-of-turn.
/// </summary>
public sealed class ChatTranscriptBuffer
{
    private readonly Dictionary<string, List<AgentSpeechMessage>> _byToolCall = [];

    public void Add(string toolCallId, AgentSpeechMessage message)
    {
        if (!_byToolCall.TryGetValue(toolCallId, out var list))
            _byToolCall[toolCallId] = list = [];
        list.Add(message);
    }

    public bool IsEmpty => _byToolCall.Count == 0;

    public IReadOnlyList<AgentMessage> Merge(IReadOnlyList<AgentMessage> messages)
    {
        if (_byToolCall.Count == 0) return messages;
        var result = new List<AgentMessage>(messages.Count + _byToolCall.Count * 2);
        foreach (var m in messages)
        {
            result.Add(m);
            if (m is ToolResultMessage tr && _byToolCall.TryGetValue(tr.ToolCallId, out var pending))
                result.AddRange(pending);
        }
        return result;
    }

    public void Clear() => _byToolCall.Clear();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~ChatTranscriptBufferTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Implement the sink + wire into MobileTransport**

In `src/Achates.Server/Mobile/MobileTransport.cs`:

(a) Add a field and construct the manager. Near the other manager/service fields, add:

```csharp
private readonly ChatRoomManager _chatRoomManager;
private readonly ConcurrentDictionary<string, ChatTranscriptBuffer> _chatBuffers = new();
```

In the constructor body (where other services are built), add:

```csharp
_chatRoomManager = new ChatRoomManager(
    sessionStore,
    targetAgentId => new AgentRuntimeFactory(
        _agents.TryGetValue(targetAgentId, out var d) ? d.Model : throw new InvalidOperationException($"unknown agent {targetAgentId}"),
        _agents.TryGetValue(targetAgentId, out var d2)
            ? SystemPrompt.CurrentDateTimeBlock() + d2.SystemPrompt
            : null));
```

(b) Add the sink implementation as a private nested class:

```csharp
private sealed class TransportChatSink(
    MobileTransport transport, string agentName, string sessionId, ChatTranscriptBuffer buffer) : IChatSink
{
    public Task EmitTurnStartAsync(string speakerAgentId, string speakerName, string toAgentId, CancellationToken ct)
        => transport.BroadcastEventAsync("agent_turn.start", new
        { agent = agentName, session_id = sessionId, id = speakerAgentId,
          speaker_id = speakerAgentId, agent_name = speakerName, to_id = toAgentId }, ct);

    public Task EmitTurnDeltaAsync(string delta, CancellationToken ct)
        => transport.BroadcastEventAsync("agent_turn.delta", new
        { agent = agentName, session_id = sessionId, delta }, ct);

    public Task EmitTurnEndAsync(string text, CancellationToken ct)
        => transport.BroadcastEventAsync("agent_turn.end", new
        { agent = agentName, session_id = sessionId, text }, ct);

    public void BufferForInitiator(string toolCallId, AgentSpeechMessage message)
        => buffer.Add(toolCallId, message);
}
```

(c) In `CreateRuntime`, replace the existing `ChatTool` construction block (the `if (agentDef.ToolNames.Contains("chat"))` block) with one that passes the manager + sessionId. `CreateRuntime` must receive the sessionId — add a `string sessionId` parameter to `CreateRuntime` and pass it from every call site (the call sites at ~764, 766, 839 already have `sessionId` in scope). New block:

```csharp
if (agentDef.ToolNames.Contains("chat"))
{
    var registry = _agents.ToDictionary(
        kv => kv.Key,
        kv => new AgentInfo
        {
            AgentDef = kv.Value,
            Description = kv.Value.Description,
            ToolNames = kv.Value.ToolNames,
            AllowChat = kv.Value.AllowChat,
        },
        StringComparer.OrdinalIgnoreCase);
    tools.Add(new ChatTool(agentName, registry, agentDef.AllowChat, _chatRoomManager, sessionId));
}
```

(d) In `StreamAgentResponseAsync`, bind the sink for the turn. Immediately before the `await foreach` over the runtime event stream, add:

```csharp
var chatBuffer = _chatBuffers.GetOrAdd($"{agentName}:{sessionId}", _ => new ChatTranscriptBuffer());
ChatSinkAccessor.Current = new TransportChatSink(this, agentName, sessionId, chatBuffer);
```

(e) In the `AgentEndEvent` case, change the `Messages = [.. runtime.Messages]` assignment to merge the buffer, then clear it:

```csharp
var mergedMessages = chatBuffer.Merge([.. runtime.Messages]);
var session = new MobileSession
{
    Id = sessionId,
    Title = existing?.Title,
    Created = existing?.Created ?? DateTimeOffset.UtcNow,
    JobId = existing?.JobId,
    Source = existing?.Source,
    OriginSessionId = existing?.OriginSessionId,
    PeerAgentId = existing?.PeerAgentId,
    Messages = [.. mergedMessages],
};
await sessionStore.SaveAsync(agentName, session, ct);
await BroadcastSessionUpdatedAsync(agentName, session, ct);
chatBuffer.Clear();
```

(f) Add the necessary `using Achates.Server.Chat;` to the file's usings if absent.

- [ ] **Step 6: Build + full test suite**

Run: `dotnet build Achates.slnx`
Expected: Build succeeded, 0 errors.

Run: `dotnet test Achates.slnx`
Expected: PASS — all tests green (adapted ChatTool tests, manager, buffer, session store, plus existing suite).

- [ ] **Step 7: Commit**

```bash
git add src/Achates.Server/Mobile/MobileTransport.cs src/Achates.Server/Mobile/ChatTranscriptBuffer.cs tests/Achates.Tests/ChatTranscriptBufferTests.cs
git commit -m "feat(chat): wire ChatRoomManager + sink + transcript merge into MobileTransport"
```

---

## Task 7: iOS — `ContentBlock.agentTurn` + mutations + AppState

**Files:**
- Modify: `apple/Achates/Models/Message.swift`
- Modify: `apple/Achates/AppState.swift`

- [ ] **Step 1: Add the ContentBlock case**

In `apple/Achates/Models/Message.swift`, in `enum ContentBlock`, add before `case remoteImage`:

```swift
case agentTurn(id: String, agentName: String, text: String, collapsed: Bool)
```

In the `var id` computed property switch, add:

```swift
case .agentTurn(let id, _, _, _): return "agent-\(id)"
```

- [ ] **Step 2: Add ChatMessage mutations**

In `struct ChatMessage`, add:

```swift
mutating func appendAgentTurn(_ delta: String, agentTurnId: String, agentName: String) {
    if let index = blocks.firstIndex(where: {
        if case .agentTurn(let id, _, _, _) = $0 { return id == agentTurnId }
        return false
    }), case .agentTurn(let id, let name, let existing, let collapsed) = blocks[index] {
        blocks[index] = .agentTurn(id: id, agentName: name, text: existing + delta, collapsed: collapsed)
    } else {
        blocks.append(.agentTurn(id: agentTurnId, agentName: agentName, text: delta, collapsed: false))
    }
}

mutating func appendAgentTurnDelta(_ delta: String) {
    if let index = blocks.lastIndex(where: {
        if case .agentTurn = $0 { return true }
        return false
    }), case .agentTurn(let id, let name, let existing, let collapsed) = blocks[index] {
        blocks[index] = .agentTurn(id: id, agentName: name, text: existing + delta, collapsed: collapsed)
    }
}

mutating func collapseAgentTurn() {
    if let index = blocks.lastIndex(where: {
        if case .agentTurn = $0 { return true }
        return false
    }), case .agentTurn(let id, let name, let text, _) = blocks[index] {
        blocks[index] = .agentTurn(id: id, agentName: name, text: text, collapsed: true)
    }
}
```

- [ ] **Step 3: Add AppState methods**

In `apple/Achates/AppState.swift`, after `completeToolCall(...)`:

```swift
func startAgentTurn(agentTurnId: String, agentName: String) {
    guard let id = streamingMessageId, let index = lastMessageIndex(id: id) else { return }
    messages[index].appendAgentTurn("", agentTurnId: agentTurnId, agentName: agentName)
}

func appendAgentTurnDelta(_ delta: String) {
    guard let id = streamingMessageId, let index = lastMessageIndex(id: id) else { return }
    messages[index].appendAgentTurnDelta(delta)
}

func collapseAgentTurn() {
    guard let id = streamingMessageId, let index = lastMessageIndex(id: id) else { return }
    messages[index].collapseAgentTurn()
}
```

- [ ] **Step 4: Build the iOS app**

Use the `xcode` MCP `BuildProject` tool (scheme `Achates`).
Expected: build succeeds (the new enum case will cause a warning in `MessageBubble.swift` switch until Task 10; that is expected and resolved there — if the build *fails* due to a non-exhaustive switch, proceed to Task 10's MessageBubble change before building, then return).

- [ ] **Step 5: Commit**

```bash
git add apple/Achates/Models/Message.swift apple/Achates/AppState.swift
git commit -m "feat(ios): add agentTurn content block + streaming mutations"
```

---

## Task 8: iOS — WebSocket `agent_turn.*` events

**Files:**
- Modify: `apple/Achates/Connection/WebSocketClient.swift`

- [ ] **Step 1: Add the three event cases**

In `handleEvent(_:)`, after the `case "tool.end":` block and before `case "message.end":`, add:

```swift
case "agent_turn.start":
    guard matchesCurrentSession else { break }
    let turnId = payload["id"]?.stringValue ?? UUID().uuidString
    let name = payload["agent_name"]?.stringValue ?? "agent"
    appState.startAgentTurn(agentTurnId: turnId, agentName: name)

case "agent_turn.delta":
    guard matchesCurrentSession else { break }
    appState.appendAgentTurnDelta(payload["delta"]?.stringValue ?? "")

case "agent_turn.end":
    guard matchesCurrentSession else { break }
    appState.collapseAgentTurn()
```

- [ ] **Step 2: Build**

`xcode` MCP `BuildProject` (scheme `Achates`). Expected: success.

- [ ] **Step 3: Commit**

```bash
git add apple/Achates/Connection/WebSocketClient.swift
git commit -m "feat(ios): handle agent_turn.start/delta/end streaming events"
```

---

## Task 9: iOS — parse persisted `agent_speech` on reload

**Files:**
- Modify: `apple/Achates/Models/Session.swift`

The server persists `AgentSpeechMessage` with `"role":"speech"` and fields `speaker_agent_id`, `speaker_display_name`, `to_agent_id`, `text` (snake_case via the session store's naming policy).

- [ ] **Step 1: Add a top-level message case**

In `Session.swift`, in the `switch typeStr` of `parseMessage`, add before `default:`:

```swift
case "speech":
    let text = dict["text"]?.stringValue ?? ""
    let speaker = dict["speaker_display_name"]?.stringValue
        ?? dict["speaker_agent_id"]?.stringValue ?? "agent"
    return ChatMessage(
        id: id,
        role: .assistant,
        blocks: [.agentTurn(id: id, agentName: speaker, text: text, collapsed: true)],
        timestamp: timestamp)
```

- [ ] **Step 2: Build**

`xcode` MCP `BuildProject` (scheme `Achates`). Expected: success.

- [ ] **Step 3: Commit**

```bash
git add apple/Achates/Models/Session.swift
git commit -m "feat(ios): render persisted agent_speech messages on session reload"
```

---

## Task 10: iOS — `AgentTurnView` + bubble routing

**Files:**
- Create: `apple/Achates/Views/AgentTurnView.swift`
- Modify: `apple/Achates/Views/MessageBubble.swift`

- [ ] **Step 1: Create AgentTurnView**

Create `apple/Achates/Views/AgentTurnView.swift` (mirror `ThinkingView`'s style; distinct label + tint):

```swift
import SwiftUI

struct AgentTurnView: View {
    let agentTurnId: String
    let agentName: String
    let text: String
    let collapsed: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(agentName)
                .font(.caption2.weight(.semibold))
                .foregroundStyle(.tint)
            Text(text)
                .font(.callout)
                .foregroundStyle(.primary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(Color.accentColor.opacity(0.08))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(Color.accentColor.opacity(0.25), lineWidth: 1)
        )
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
```

- [ ] **Step 2: Route the block**

In `apple/Achates/Views/MessageBubble.swift`, in `blockView(_:)`, add before `case .image`:

```swift
case .agentTurn(let id, let agentName, let text, let collapsed):
    AgentTurnView(agentTurnId: id, agentName: agentName, text: text, collapsed: collapsed)
```

- [ ] **Step 3: Confirm the chat tool chip already collapses**

No code change: `ToolCallView` already collapses tool calls by default and `chat` already maps to "Talked to another agent" labels. Verify by reading `apple/Achates/Views/ToolCallView.swift` `canExpand`/label logic — the `ask` reply is no longer carried in the tool result body for display (it's in `agentTurn` blocks), so the collapsed chip is correct as-is.

- [ ] **Step 4: Build**

`xcode` MCP `BuildProject` (scheme `Achates`). Expected: success, no non-exhaustive-switch warnings.

- [ ] **Step 5: Commit**

```bash
git add apple/Achates/Views/AgentTurnView.swift apple/Achates/Views/MessageBubble.swift
git commit -m "feat(ios): render attributed agent-turn bubbles"
```

---

## Task 11: Docs + final verification

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update CLAUDE.md**

In `CLAUDE.md`:
- Replace the `ChatTool` bullet with: actions `agents`/`ask`; one round per `ask`; target rebuilt from one continuing session per `(initiator session, target agent)` so it remembers; replies stream live as first-class `AgentSpeechMessage`s persisted into both sessions; no persona/ping-pong; orchestrated by `ChatRoomManager` (in `src/Achates.Server/Chat/`), thin `ChatTool` façade; sink bound per turn via `ChatSinkAccessor` (AsyncLocal); initiator copies merged into its session at end-of-turn via `ChatTranscriptBuffer`.
- In the `MobileSession` bullet, add `OriginSessionId` and `PeerAgentId`.
- In the `CronSessionReaper` bullet, confirm chat-origin sessions are still `Source = Chat` (one continuing session, still max-age pruned) — no rule change.
- Add `agent_turn.start/delta/end` to the broadcast-events list.

- [ ] **Step 2: Full build + test**

Run: `dotnet build Achates.slnx && dotnet test Achates.slnx`
Expected: Build succeeded; all tests pass.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md for inter-agent chat redesign"
```

- [ ] **Step 4: Manual E2E (record results, do not auto-pass)**

Run `dotnet run --project src/Achates.Server` with two agents: A has `chat` + `allow_chats` includes B; B has `dreamtime` + `memory`. From an A session, ask A to consult B.

Verify:
1. B's reply streams live as a labeled attributed bubble in A's transcript (not a collapsed tool chip).
2. The `chat` tool call shows only as a thin collapsed chip.
3. Reload the A session — the attributed bubbles persist (bug #1 fixed).
4. Under `~/.achates/agents/B/sessions/` exactly one `chat-*.json` exists; ask A to consult B again in the same session → the **same** file grows (bug #2 fixed), and B's reply shows it remembered the prior round.
5. B's dreamtime can read that one continuing session.
6. Kill the server mid-nothing; restart; ask A to consult B again → continues the same B session (rehydrated from disk).

---

## Self-Review

**1. Spec coverage:**
- Bounded round-per-`ask`, no persona/burst/end → Tasks 4, 5. ✓
- First-class `AgentSpeechMessage`, excluded from LLM context → Task 1. ✓
- Live token streaming (`agent_turn.*`) → Tasks 6, 8, 10. ✓
- One continuing target session per pairing, appended → Tasks 2, 4. ✓
- Initiator-side persisted + reload-visible → Tasks 6 (buffer/merge), 9 (iOS parse). ✓
- `ChatRoomManager` owned by transport, stateless, per-pairing lock → Tasks 4, 6. ✓
- Errors surface as descriptive text; cost recording — error path Task 4; **cost recording gap**: the spec keeps target-side cost recording (channel `chat`). The stub-based manager does not yet append `CostEntry`. **Resolution:** add a `CostLedger?` to `AgentRuntimeFactory` for the target agent and append a `CostEntry { Channel = "chat", Peer = initiatorAgentId }` on `MessageEndEvent { AssistantMessage }` inside `ChatRoomManager.AskAsync` (mirror `ChatTool.RunAgentTurnAsync` lines 242-261). Add this as Step in Task 4 and a test asserting a ledger entry. *(Added below as Task 4 Step 9.)*
- Reaper unchanged (Source=Chat) → Task 2 reuses `SessionSource.Chat`. ✓
- iOS reload/rebuild double-count avoided → Task 1 Step 5 + test. ✓

**2. Placeholder scan:** No TBD/TODO; all steps contain concrete code or exact edits. ✓

**3. Type consistency:** `ChatRoomManager.AskAsync(initiatorAgentId, initiatorSessionId, targetAgentId, message, toolCallId, sink, ct)` used identically in Tasks 4/5/6. `IChatSink` members identical across Tasks 3/4/6. `ChatTranscriptBuffer.{Add,Merge,Clear,IsEmpty}` consistent Tasks 6. `MobileSessionStore.{ChatSessionId,LoadOrCreateChatSessionAsync}` consistent Tasks 2/4. ✓

### Task 4 Step 9 (added by self-review): target-side cost recording

- [ ] **Step 9: Add target cost recording**

Extend `AgentRuntimeFactory` with an optional `CostLedger? ledger` and expose it; in `ChatRoomManager.AskAsync`, in the event loop, add:

```csharp
case MessageEndEvent { Message: AssistantMessage am }:
    factory.Ledger?.AppendAsync(new Achates.Server.CostEntry
    {
        Timestamp = DateTimeOffset.UtcNow, Model = am.Model,
        Channel = "chat", Peer = initiatorAgentId,
        InputTokens = am.Usage.Input, OutputTokens = am.Usage.Output,
        CacheReadTokens = am.Usage.CacheRead, CacheWriteTokens = am.Usage.CacheWrite,
        CostTotal = am.Usage.Cost.Total, CostInput = am.Usage.Cost.Input,
        CostOutput = am.Usage.Cost.Output, CostCacheRead = am.Usage.Cost.CacheRead,
        CostCacheWrite = am.Usage.Cost.CacheWrite,
    });
    break;
```

In `MobileTransport`'s factory lambda, pass `_agents[targetAgentId].CostLedger`. Add a test asserting the target's ledger file gets one entry after an `ask` (use a real `CostLedger` over a temp path; assert the `.jsonl` has a line with `"channel":"chat"`). Commit with Task 4 Step 8.
```
