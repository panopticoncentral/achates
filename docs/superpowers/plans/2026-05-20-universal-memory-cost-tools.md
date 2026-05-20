# Universal Memory & Cost Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `MemoryTool` and `CostTool` truly always-on for every agent runtime the server builds — including the inter-agent chat target — and consolidate the existing duplicated construction logic behind a single helper.

**Architecture:** Introduce `UniversalTools.Build(...)` as the single source of truth for "tools every agent always has." Four existing runtime-construction sites (`MobileTransport.CreateRuntime`, `CronService.BuildJobTools`, `CronService.BuildDreamtimeTools`, `AgentRuntimeFactory.Create`) converge on it. `AgentRuntimeFactory` is extended to carry a precomputed universal-tools list passed in by `MobileTransport` at construction time. The legacy `case "memory":` / `case "cost":` branches in `GatewayService.ResolveTools` stay as silent no-ops to suppress startup warnings on existing AGENT.md files. `GatewayService.AllTools` is filtered so the iOS picker stops offering memory/cost.

**Tech Stack:** .NET 10 preview, C# 13, xUnit (in `tests/Achates.Tests`). Solution file: `Achates.slnx`.

**Spec:** `docs/superpowers/specs/2026-05-20-universal-memory-cost-tools-design.md`

---

## File Map

**Create:**
- `src/Achates.Server/Tools/UniversalTools.cs` — single-source helper
- `tests/Achates.Tests/UniversalToolsTests.cs` — unit tests for the helper
- `tests/Achates.Tests/AgentRuntimeFactoryTests.cs` — unit tests for chat-target tools
- `tests/Achates.Tests/AllToolsTests.cs` — picker-filter tests

**Modify:**
- `src/Achates.Server/Chat/AgentRuntimeFactory.cs` — accept and seed universal tools
- `src/Achates.Server/Mobile/MobileTransport.cs` — build universal tools for chat factory; switch `CreateRuntime` to the helper
- `src/Achates.Server/Cron/CronService.cs` — switch `BuildJobTools` and `BuildDreamtimeTools` to the helper
- `src/Achates.Server/GatewayService.cs` — filter `AllTools`; keep `case "memory":` and `case "cost":` as documented no-ops
- `CLAUDE.md` — fix chat-target tool description; introduce "Universal Tools" section; strip "requires X in tools list" lines for memory/cost
- `docs/configuration.md` — add deprecation note if the file lists `memory`/`cost` in the tools list

---

## Task 1: Add the `UniversalTools` helper (TDD)

**Files:**
- Create: `src/Achates.Server/Tools/UniversalTools.cs`
- Test: `tests/Achates.Tests/UniversalToolsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Achates.Tests/UniversalToolsTests.cs`:

```csharp
using Achates.Agent.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Models;
using Achates.Server;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class UniversalToolsTests
{
    private static AgentDefinition MakeAgentDef(string memoryPath) => new()
    {
        Name = "test",
        Description = "",
        SystemPrompt = "",
        Model = new Model
        {
            Id = "test/model", Name = "Test", Provider = null!,
            Cost = new ModelCost { Prompt = 0, Completion = 0 },
            ContextWindow = 128_000, Input = ModelModalities.Text,
            Output = ModelModalities.Text, Parameters = ModelParameters.Tools,
        },
        Tools = [],
        ToolNames = [],
        MemoryPath = memoryPath,
    };

    [Fact]
    public void Build_returns_only_memory_when_no_ledgers()
    {
        var def = MakeAgentDef("/tmp/agent-mem.md");
        var tools = UniversalTools.Build(
            agentName: "test",
            agentDef: def,
            sharedMemoryPath: "/tmp/shared.md",
            costLedgers: new Dictionary<string, CostLedger>());

        Assert.Single(tools);
        Assert.Equal("memory", tools[0].Name);
    }

    [Fact]
    public void Build_returns_memory_and_cost_when_ledgers_present()
    {
        var def = MakeAgentDef("/tmp/agent-mem.md");
        var ledgerDir = Path.Combine(Path.GetTempPath(), "achates-univ-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(ledgerDir);
            var ledger = new CostLedger(Path.Combine(ledgerDir, "costs.jsonl"));

            var tools = UniversalTools.Build(
                agentName: "test",
                agentDef: def,
                sharedMemoryPath: "/tmp/shared.md",
                costLedgers: new Dictionary<string, CostLedger> { ["test"] = ledger });

            Assert.Equal(2, tools.Count);
            Assert.Equal("memory", tools[0].Name);
            Assert.Equal("cost", tools[1].Name);
        }
        finally { if (Directory.Exists(ledgerDir)) Directory.Delete(ledgerDir, true); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~UniversalToolsTests"`
Expected: build failure (`UniversalTools` does not exist).

- [ ] **Step 3: Create the helper**

Create `src/Achates.Server/Tools/UniversalTools.cs`:

```csharp
using Achates.Agent.Tools;

namespace Achates.Server.Tools;

/// <summary>
/// Builds the set of tools every agent runtime always has, regardless of the
/// agent's configured tool list. Currently: <see cref="MemoryTool"/> (always)
/// and <see cref="CostTool"/> (when at least one cost ledger is in scope).
///
/// Single source of truth — used by <c>MobileTransport.CreateRuntime</c>,
/// <c>CronService.BuildJobTools</c>, <c>CronService.BuildDreamtimeTools</c>,
/// and the chat-target factory <c>AgentRuntimeFactory</c>.
/// </summary>
internal static class UniversalTools
{
    public static IReadOnlyList<AgentTool> Build(
        string agentName,
        AgentDefinition agentDef,
        string sharedMemoryPath,
        IReadOnlyDictionary<string, CostLedger> costLedgers)
    {
        var tools = new List<AgentTool>
        {
            new MemoryTool(sharedMemoryPath, agentDef.MemoryPath),
        };
        if (costLedgers.Count > 0)
            tools.Add(new CostTool(agentName, costLedgers));
        return tools;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~UniversalToolsTests"`
Expected: 2 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Achates.Server/Tools/UniversalTools.cs tests/Achates.Tests/UniversalToolsTests.cs
git commit -m "feat(tools): add UniversalTools helper for always-on agent tools

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Extend `AgentRuntimeFactory` to seed universal tools (TDD)

**Files:**
- Modify: `src/Achates.Server/Chat/AgentRuntimeFactory.cs`
- Test: `tests/Achates.Tests/AgentRuntimeFactoryTests.cs`

This is the regression test for the original gap: B's chat-target runtime must include `MemoryTool`.

- [ ] **Step 1: Write the failing test**

Create `tests/Achates.Tests/AgentRuntimeFactoryTests.cs`:

```csharp
using Achates.Agent.Tools;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Events;
using Achates.Providers.Models;
using Achates.Server.Chat;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class AgentRuntimeFactoryTests
{
    private sealed class StubProvider : IModelProvider
    {
        public string Id => "stub"; public string Name => "Stub"; public string EnvironmentKey => "S";
        public string? Key { get; set; } public HttpClient? HttpClient { get; set; }
        public Task<IReadOnlyList<Model>> GetModelsAsync(ModelModalities? o = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Model>>([]);
        public CompletionEventStream GetCompletions(Model m, CompletionContext c, CompletionOptions? o = null, CancellationToken ct = default)
            => CompletionEventStream.Create(s => { s.End(); return Task.CompletedTask; });
    }

    private static Model TestModel() => new()
    {
        Id = "test/model", Name = "Test", Provider = new StubProvider(),
        Cost = new ModelCost { Prompt = 0, Completion = 0 },
        ContextWindow = 128_000, Input = ModelModalities.Text,
        Output = ModelModalities.Text, Parameters = ModelParameters.Tools,
    };

    [Fact]
    public void Create_includes_universal_tools()
    {
        var memoryTool = new MemoryTool("/tmp/shared.md", "/tmp/agent.md");
        var factory = new AgentRuntimeFactory(TestModel(), universalTools: [memoryTool]);

        var runtime = factory.Create([]);

        Assert.Contains(runtime.Tools, t => t.Name == "memory");
    }

    [Fact]
    public void Create_with_no_universal_tools_has_empty_tool_list()
    {
        // Backward compat: existing test code that constructs the factory without
        // universalTools still produces a tool-less runtime.
        var factory = new AgentRuntimeFactory(TestModel());

        var runtime = factory.Create([]);

        Assert.Empty(runtime.Tools);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~AgentRuntimeFactoryTests"`
Expected: build failure — `AgentRuntimeFactory` does not have a `universalTools` parameter.

- [ ] **Step 3: Modify the factory**

Replace the contents of `src/Achates.Server/Chat/AgentRuntimeFactory.cs`:

```csharp
using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Models;

namespace Achates.Server.Chat;

/// <summary>
/// Builds a target <see cref="AgentRuntime"/> for one chat round, seeded with a
/// reconstructed message history. Injectable so tests can supply a stub model.
/// Carries the target agent's cost ledger so the round's usage is recorded.
/// Carries a precomputed universal-tools list (memory + cost) so the consulted
/// agent has the same always-on tools it would have in a normal session.
/// </summary>
public sealed class AgentRuntimeFactory(
    Model model,
    string? systemPrompt = null,
    CostLedger? ledger = null,
    IReadOnlyList<AgentTool>? universalTools = null)
{
    public CostLedger? Ledger { get; } = ledger;

    public AgentRuntime Create(IReadOnlyList<AgentMessage> seed) => new(new AgentOptions
    {
        Model = model,
        SystemPrompt = systemPrompt,
        Tools = universalTools,
        Messages = seed,
    });
}
```

The new parameter is optional and trails the existing ones, so existing call sites that use positional or named args (including the tests in `ChatRoomManagerTests`) continue to compile unchanged.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~AgentRuntimeFactoryTests"`
Expected: 2 tests pass.

Also run: `dotnet test Achates.slnx --filter "FullyQualifiedName~ChatRoomManagerTests"`
Expected: all existing ChatRoomManager tests still pass (regression check — they construct the factory without `universalTools`, so they should produce a tool-less runtime exactly as before).

- [ ] **Step 5: Commit**

```bash
git add src/Achates.Server/Chat/AgentRuntimeFactory.cs tests/Achates.Tests/AgentRuntimeFactoryTests.cs
git commit -m "feat(chat): AgentRuntimeFactory accepts universal tools

The chat-target runtime now seeds memory + cost via a precomputed
universal-tools list, so B has the same always-on tools during a consult
that it would have in a normal session.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Wire `MobileTransport` to pass universal tools to the chat factory

**Files:**
- Modify: `src/Achates.Server/Mobile/MobileTransport.cs:54-64` (chat factory construction)

This makes the chat target actually receive memory + cost in production.

- [ ] **Step 1: Update the factory construction inside `MobileTransport`'s constructor**

In `src/Achates.Server/Mobile/MobileTransport.cs`, find the lambda passed to `ChatRoomManager` (lines 54-64). Today it reads:

```csharp
_chatRoomManager = new ChatRoomManager(
    sessionStore,
    targetAgentId =>
    {
        if (!_agents.TryGetValue(targetAgentId, out var def))
            throw new InvalidOperationException($"Unknown chat target agent '{targetAgentId}'.");
        return new AgentRuntimeFactory(
            def.Model,
            SystemPrompt.CurrentDateTimeBlock() + def.SystemPrompt,
            def.CostLedger);
    });
```

Replace with:

```csharp
_chatRoomManager = new ChatRoomManager(
    sessionStore,
    targetAgentId =>
    {
        if (!_agents.TryGetValue(targetAgentId, out var def))
            throw new InvalidOperationException($"Unknown chat target agent '{targetAgentId}'.");

        // Cross-agent cost-ledger snapshot. Rebuilt per call so agent reloads
        // and renames are reflected on the next consult.
        var costLedgers = _agents
            .Where(kv => kv.Value.CostLedger is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.CostLedger!,
                StringComparer.OrdinalIgnoreCase);

        var universalTools = Tools.UniversalTools.Build(
            agentName: targetAgentId,
            agentDef: def,
            sharedMemoryPath: SharedMemoryPath,
            costLedgers: costLedgers);

        return new AgentRuntimeFactory(
            def.Model,
            SystemPrompt.CurrentDateTimeBlock() + def.SystemPrompt,
            def.CostLedger,
            universalTools);
    });
```

Note: `SharedMemoryPath` is the existing private static field at line 1868 of the same file.

- [ ] **Step 2: Verify the change builds**

Run: `dotnet build Achates.slnx`
Expected: builds cleanly. No warnings about ambiguous `Tools` reference (the namespace prefix `Tools.UniversalTools` disambiguates from the local `tools` variable used in other methods).

- [ ] **Step 3: Add an end-to-end regression test in `ChatRoomManagerTests`**

In `tests/Achates.Tests/ChatRoomManagerTests.cs`, add the following test inside the existing class (alongside the other `[Fact]` methods). This proves the wiring works through `ChatRoomManager` when the factory is given universal tools.

```csharp
[Fact]
public async Task Ask_target_runtime_can_see_universal_tools_when_provided()
{
    var dir = Path.Combine(Path.GetTempPath(), "achates-crm-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        var store = new MobileSessionStore(dir);
        Achates.Agent.AgentRuntime? capturedRuntime = null;

        // Provider that captures the runtime's tool list as a side effect of the first call.
        var observingProvider = new Achates.Tests.ChatRoomManagerTests.ReplyProvider("ok");

        var mgr = new ChatRoomManager(store,
            _ =>
            {
                var memoryTool = new Achates.Server.Tools.MemoryTool(
                    Path.Combine(dir, "shared.md"),
                    Path.Combine(dir, "agent.md"));
                var factory = new Achates.Server.Chat.AgentRuntimeFactory(
                    ModelWith(observingProvider),
                    universalTools: [memoryTool]);
                return factory;
            });

        var sink = new FakeSink();
        await mgr.AskAsync("val", "s", "claire", "hello", "t1", sink, default);

        // Reload the chat session and verify it was produced (i.e. the round completed).
        var id = MobileSessionStore.ChatSessionId("s", "claire");
        var session = await store.LoadAsync("claire", id);
        Assert.NotNull(session);
        Assert.Equal(2, session!.Messages.OfType<AgentSpeechMessage>().Count());
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}
```

This test exists primarily as a smoke test that the new constructor parameter doesn't break the existing flow. The direct assertion that `runtime.Tools` contains `MemoryTool` is already covered by `AgentRuntimeFactoryTests.Create_includes_universal_tools` in Task 2.

- [ ] **Step 4: Run all chat-related tests**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~ChatRoomManagerTests|FullyQualifiedName~AgentRuntimeFactoryTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Achates.Server/Mobile/MobileTransport.cs tests/Achates.Tests/ChatRoomManagerTests.cs
git commit -m "feat(chat): inject universal tools into chat-target runtime

MobileTransport now builds memory + cost via UniversalTools.Build when
constructing the chat factory, so B has memory and cost during A->B
consults — the same always-on tools it has in normal sessions.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Consolidate `MobileTransport.CreateRuntime` onto the helper

**Files:**
- Modify: `src/Achates.Server/Mobile/MobileTransport.cs:1870-1919` (`CreateRuntime` method)

- [ ] **Step 1: Replace the inline memory + cost construction with `UniversalTools.Build`**

In `src/Achates.Server/Mobile/MobileTransport.cs`, find the `CreateRuntime` method (around line 1870). Today it reads:

```csharp
private AgentRuntime CreateRuntime(AgentDefinition agentDef, string agentName, string sessionId,
    IReadOnlyList<AgentMessage>? messages = null,
    IReadOnlyList<AgentTool>? extraTools = null)
{
    var tools = new List<AgentTool>(agentDef.Tools);

    tools.Add(new MemoryTool(SharedMemoryPath, agentDef.MemoryPath));

    // Cost tool gets a snapshot of every agent's ledger so it can serve scope=all / scope=<name>.
    // Snapshot is rebuilt per CreateRuntime call so reloads / renames are picked up on next session.
    var costLedgers = _agents
        .Where(kv => kv.Value.CostLedger is not null)
        .ToDictionary(kv => kv.Key, kv => kv.Value.CostLedger!,
            StringComparer.OrdinalIgnoreCase);
    if (costLedgers.Count > 0)
        tools.Add(new CostTool(agentName, costLedgers));
    if (agentDef.CronStore is { } cronStore && CronService is { } cron)
        tools.Add(new CronTool(cronStore, agentName, cron));
    // ...
```

Replace the memory + cost portion with a single call:

```csharp
private AgentRuntime CreateRuntime(AgentDefinition agentDef, string agentName, string sessionId,
    IReadOnlyList<AgentMessage>? messages = null,
    IReadOnlyList<AgentTool>? extraTools = null)
{
    var tools = new List<AgentTool>(agentDef.Tools);

    // Universal tools (memory + cost) — always available, never opt-in.
    // Cost ledgers are snapshotted per call so agent reloads / renames are reflected.
    var costLedgers = _agents
        .Where(kv => kv.Value.CostLedger is not null)
        .ToDictionary(kv => kv.Key, kv => kv.Value.CostLedger!,
            StringComparer.OrdinalIgnoreCase);
    tools.AddRange(Tools.UniversalTools.Build(agentName, agentDef, SharedMemoryPath, costLedgers));

    if (agentDef.CronStore is { } cronStore && CronService is { } cron)
        tools.Add(new CronTool(cronStore, agentName, cron));

    if (agentDef.ToolNames.Contains("sessions"))
        tools.Add(new SessionsTool(sessionStore, agentName, currentSessionId: sessionId, since: null));

    // ... rest unchanged (chat tool, extraTools, runtime construction)
```

Leave everything below (SessionsTool, ChatTool registration, extraTools, the final `return new AgentRuntime(...)`) exactly as it was.

- [ ] **Step 2: Verify build**

Run: `dotnet build Achates.slnx`
Expected: builds cleanly.

- [ ] **Step 3: Run the full test suite to ensure no behavior change**

Run: `dotnet test Achates.slnx`
Expected: all tests pass (this is pure refactor — functional behavior is unchanged for user sessions).

- [ ] **Step 4: Commit**

```bash
git add src/Achates.Server/Mobile/MobileTransport.cs
git commit -m "refactor(mobile): use UniversalTools.Build in CreateRuntime

Replace the inline memory + cost construction with the helper. Behavior
unchanged for user sessions.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Consolidate `CronService` onto the helper

**Files:**
- Modify: `src/Achates.Server/Cron/CronService.cs:479-519` (`BuildJobTools` and `BuildDreamtimeTools`)

- [ ] **Step 1: Refactor `BuildJobTools`**

In `src/Achates.Server/Cron/CronService.cs`, find `BuildJobTools` (around line 479). Today:

```csharp
private IReadOnlyList<AgentTool> BuildJobTools(string agentName, AgentDefinition agentDef)
{
    var tools = new List<AgentTool>();

    // Add shared tools (SessionTool, MailTool, etc.) — but not CronTool
    foreach (var tool in agentDef.Tools)
    {
        tools.Add(tool);
    }

    // Add per-agent tools that make sense in isolation
    var sharedMemoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates", "memory.md");
    tools.Add(new MemoryTool(sharedMemoryPath, agentDef.MemoryPath));

    var costLedgers = BuildCostLedgerRegistry();
    if (costLedgers.Count > 0)
        tools.Add(new CostTool(agentName, costLedgers));

    return tools;
}
```

Replace with:

```csharp
private IReadOnlyList<AgentTool> BuildJobTools(string agentName, AgentDefinition agentDef)
{
    var tools = new List<AgentTool>();

    // Add shared tools (SessionTool, MailTool, etc.) — but not CronTool
    foreach (var tool in agentDef.Tools)
    {
        tools.Add(tool);
    }

    var sharedMemoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates", "memory.md");
    tools.AddRange(UniversalTools.Build(agentName, agentDef, sharedMemoryPath, BuildCostLedgerRegistry()));

    return tools;
}
```

- [ ] **Step 2: Refactor `BuildDreamtimeTools`**

Immediately below, find `BuildDreamtimeTools`:

```csharp
private IReadOnlyList<AgentTool> BuildDreamtimeTools(string agentName, AgentDefinition agentDef, CronJob job)
{
    var tools = new List<AgentTool>();

    // Session browser — uses LastRunAt from the job itself as the "since" timestamp
    tools.Add(new SessionsTool(_sessionStore, agentName, currentSessionId: null, job.State.LastRunAt));

    // Memory tool for reading and updating persistent memory
    var sharedMemoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates", "memory.md");
    tools.Add(new MemoryTool(sharedMemoryPath, agentDef.MemoryPath));

    // Cost tool — same cross-agent view as a normal turn.
    var costLedgers = BuildCostLedgerRegistry();
    if (costLedgers.Count > 0)
        tools.Add(new CostTool(agentName, costLedgers));

    return tools;
}
```

Replace with:

```csharp
private IReadOnlyList<AgentTool> BuildDreamtimeTools(string agentName, AgentDefinition agentDef, CronJob job)
{
    var tools = new List<AgentTool>();

    // Session browser — uses LastRunAt from the job itself as the "since" timestamp
    tools.Add(new SessionsTool(_sessionStore, agentName, currentSessionId: null, job.State.LastRunAt));

    var sharedMemoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates", "memory.md");
    tools.AddRange(UniversalTools.Build(agentName, agentDef, sharedMemoryPath, BuildCostLedgerRegistry()));

    return tools;
}
```

- [ ] **Step 3: Add the using directive if needed**

At the top of `src/Achates.Server/Cron/CronService.cs`, ensure `using Achates.Server.Tools;` is present (the existing `MemoryTool` and `CostTool` references already need it — but verify after the edit).

- [ ] **Step 4: Verify build and test**

Run: `dotnet build Achates.slnx`
Expected: builds cleanly.

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~CronService|FullyQualifiedName~CronSchedule"`
Expected: all existing cron tests pass (refactor only, no behavior change).

- [ ] **Step 5: Commit**

```bash
git add src/Achates.Server/Cron/CronService.cs
git commit -m "refactor(cron): use UniversalTools.Build for job and dreamtime tools

Replace the inline memory + cost construction in BuildJobTools and
BuildDreamtimeTools with the helper. Behavior unchanged.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Filter `MemoryTool` and `CostTool` out of `AllTools` (TDD)

**Files:**
- Modify: `src/Achates.Server/GatewayService.cs:46-52` (`AllTools` static initializer)
- Modify: `src/Achates.Server/GatewayService.cs:578-584` (annotate the no-op cases)
- Test: `tests/Achates.Tests/AllToolsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Achates.Tests/AllToolsTests.cs`:

```csharp
using Achates.Server;

namespace Achates.Tests;

public sealed class AllToolsTests
{
    [Fact]
    public void AllTools_excludes_universal_tools_from_picker()
    {
        // memory and cost are always-on; they must not appear in the picker
        // surfaced to the iOS agent-edit sheet via the tools.list RPC.
        var names = GatewayService.AllTools.Select(t => t.Name).ToList();

        Assert.DoesNotContain("memory", names);
        Assert.DoesNotContain("cost", names);
    }

    [Fact]
    public void AllTools_includes_opt_in_tools()
    {
        var names = GatewayService.AllTools.Select(t => t.Name).ToList();

        // Sanity: opt-in tools are still surfaced.
        Assert.Contains("notebook", names);
        Assert.Contains("web_search", names);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~AllToolsTests"`
Expected: `AllTools_excludes_universal_tools_from_picker` FAILS (`memory` and `cost` are present today). `AllTools_includes_opt_in_tools` passes.

- [ ] **Step 3: Filter `AllTools`**

In `src/Achates.Server/GatewayService.cs`, find the `AllTools` initializer (lines 46-52):

```csharp
public static IReadOnlyList<(string Name, string Label)> AllTools { get; } =
    typeof(GatewayService).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(AgentTool)) && !t.IsAbstract)
        .Select(t => (AgentTool)RuntimeHelpers.GetUninitializedObject(t))
        .Select(t => (t.Name, t.Label))
        .OrderBy(t => t.Name)
        .ToList();
```

Replace with:

```csharp
// Universal tools (always-on, not opt-in) are excluded — they must not
// appear in the agent-edit picker. See UniversalTools.cs.
private static readonly HashSet<string> _universalToolNames = new(StringComparer.Ordinal)
{
    "memory",
    "cost",
};

public static IReadOnlyList<(string Name, string Label)> AllTools { get; } =
    typeof(GatewayService).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(AgentTool)) && !t.IsAbstract)
        .Select(t => (AgentTool)RuntimeHelpers.GetUninitializedObject(t))
        .Select(t => (t.Name, t.Label))
        .Where(t => !_universalToolNames.Contains(t.Name))
        .OrderBy(t => t.Name)
        .ToList();
```

- [ ] **Step 4: Annotate the no-op `case` branches**

In the same file, find `ResolveTools`'s switch (around lines 578-584). Today:

```csharp
case "memory":
case "cost":
case "cron":
case "chat":
case "sessions":
    // Per-session tools — added in MobileTransport.CreateRuntime
    break;
```

Split into two groups so the deprecation status of `memory` and `cost` is clear:

```csharp
case "memory":
case "cost":
    // Deprecated as configurable: these are now universal (see UniversalTools).
    // Branches kept as silent no-ops so legacy AGENT.md files do not emit
    // "unknown tool" warnings on startup. Safe to remove once configs have
    // organically migrated through the iOS edit sheet.
    break;
case "cron":
case "chat":
case "sessions":
    // Per-session tools — added in MobileTransport.CreateRuntime
    break;
```

- [ ] **Step 5: Run the new tests to verify they pass**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~AllToolsTests"`
Expected: 2 tests pass.

Run the full suite to ensure nothing regressed:
Run: `dotnet test Achates.slnx`
Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Achates.Server/GatewayService.cs tests/Achates.Tests/AllToolsTests.cs
git commit -m "feat(gateway): hide memory and cost from the agent-edit tool picker

Filter universal tools out of AllTools so tools.list no longer surfaces
them to the iOS picker. Annotate the legacy switch branches in
ResolveTools as deprecated silent no-ops, kept to suppress 'unknown
tool' warnings on existing AGENT.md files.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

CLAUDE.md is the project's contract document and the codebase's "source of truth" for new contributors and future Claude sessions. The spec requires three edits.

- [ ] **Step 1: Fix the chat-target tool description**

Open `CLAUDE.md` and find the `ChatTool` entry under "Tool System." It contains the line:

> The target runtime gets all its tools except chat (prevents cascade).

Replace that line with:

> The target runtime is built by `AgentRuntimeFactory` and gets only the universal tools (memory + cost) — see "Universal Tools" below. No other tools cascade into a consult.

- [ ] **Step 2: Add a "Universal Tools" subsection and move the memory/cost entries into it**

Still in the "Tool System" section, locate the `MemoryTool` and `CostTool` entries in the per-tool bullet list. Remove them from their current positions.

Just before the per-tool list (after the opening explanation of `AgentTool`), insert a new subsection:

```markdown
#### Universal Tools (always available)

Two tools are always added to every agent runtime, regardless of the agent's configured `Tools:` list. They are not opt-in. Listing `memory` or `cost` in an agent's `Tools:` capability is accepted for backward compatibility but ignored. Built by `UniversalTools.Build(...)` and used by `MobileTransport.CreateRuntime`, `CronService.BuildJobTools`, `CronService.BuildDreamtimeTools`, and `AgentRuntimeFactory` (chat-target).

- `MemoryTool` — layered persistent memory with two scopes. **Shared memory** at `~/.achates/memory.md` stores universal user facts (name, family, preferences) accessible to all agents. **Agent memory** at `~/.achates/agents/{agentName}/memory.md` stores agent-specific notes. `scope` parameter (`shared` or `agent`) controls which file to target; `read` without a scope returns both. Survives session boundaries.

- `CostTool` — queries the persistent cost ledgers across one or all agents. Actions: `summary` (totals for a period), `recent` (last N entries), `breakdown` (grouped by `day`, `model`, `agent`, `channel`, or `peer`). `scope` parameter (default `"self"`) accepts `"self"` (calling agent), `"all"` (aggregate across every agent), or a specific agent name. Output surfaces `channel` (direct turn vs `chat` vs `cron`), `peer` (initiator agent / job id), cache-write tokens, and a per-category cost split when those fields carry data. Built with a snapshot of every agent's ledger; the snapshot is rebuilt per runtime construction so reloads/renames take effect on next session. Ledger writes are always recorded regardless of any tool configuration.
```

(The wording mirrors the descriptions already in the file for these tools. Keep the surrounding context and bullet style consistent with the existing per-tool list.)

- [ ] **Step 3: Strip "requires X in tools list" lines**

Search `CLAUDE.md` for the phrases `requires \`memory\` in agent's tools list` and `requires \`cost\` in agent's tools list`. Delete those clauses wherever they appear. They may be embedded inside longer sentences — keep the surrounding sentence valid by removing only the clause.

- [ ] **Step 4: Update the per-session-tool-injection note**

Near the end of the `MobileTransport` / `Transport` section, find:

> Per-session tool injection: `CreateRuntime` adds MemoryTool, NotebookTool, CostTool, CronTool per-session, plus SessionsTool when the agent's tools list contains `sessions`.

Replace with:

> Per-session tool injection: `CreateRuntime` adds the universal tools (memory + cost) and CronTool per-session, plus SessionsTool when the agent's tools list contains `sessions`. (NotebookTool is opt-in via the agent's `Tools:` list.)

- [ ] **Step 5: Verify the doc reads coherently**

Read `CLAUDE.md` end to end and confirm:
- The chat-target description matches reality.
- The "Universal Tools" subsection is the single place memory/cost are described.
- No remaining "requires `memory` in agent's tools list" or "requires `cost` in agent's tools list" phrases exist.
- The per-tool list no longer contains MemoryTool or CostTool entries.

- [ ] **Step 6: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: document universal memory and cost tools in CLAUDE.md

Replace the inaccurate 'target runtime gets all its tools except chat'
claim with the actual contract (universal tools only). Move memory and
cost from the per-tool list into a new 'Universal Tools (always
available)' subsection. Strip 'requires X in tools list' phrasing.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Update `docs/configuration.md` if it mentions the tools list

**Files:**
- Modify: `docs/configuration.md` (only if relevant)

- [ ] **Step 1: Check whether `docs/configuration.md` references memory/cost in tool config**

Run: `grep -n "memory\|cost" docs/configuration.md`

If neither is mentioned in the context of an agent's `Tools:` list, skip this task. Note this in your commit log as "no change needed for docs/configuration.md".

If they ARE mentioned in the context of the tools list, proceed to Step 2.

- [ ] **Step 2: Add deprecation note**

Wherever `memory` or `cost` appears in the documentation as an agent-configurable tool, add a one-line note such as:

> Note: `memory` and `cost` are always available to every agent; listing them in the `Tools:` capability is accepted but ignored.

Place the note where it minimally disrupts the surrounding flow.

- [ ] **Step 3: Commit (only if changes were made)**

```bash
git add docs/configuration.md
git commit -m "docs(config): note memory and cost are universal, not opt-in

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: Final verification

**Files:** none (verification only)

- [ ] **Step 1: Full build**

Run: `dotnet build Achates.slnx`
Expected: builds cleanly with no new warnings.

- [ ] **Step 2: Full test suite**

Run: `dotnet test Achates.slnx`
Expected: every test passes. Specifically confirm:
- `UniversalToolsTests` (2 tests)
- `AgentRuntimeFactoryTests` (2 tests)
- `AllToolsTests` (2 tests)
- All pre-existing tests, especially `ChatRoomManagerTests`, `CostToolTests`, and `AgentLoaderTests`

- [ ] **Step 3: Manual smoke check**

Run: `dotnet run --project src/Achates.Server` and confirm:
- The server starts without "unknown tool" warnings for any agent that lists `memory` or `cost` in its `Tools:` capability.
- The iOS picker (via `tools.list` RPC) no longer surfaces `memory` or `cost` as selectable tools. (If the iOS app is not currently set up to test this, manually invoke the RPC: send `{"id":"1","method":"tools.list","params":{}}` over a WebSocket to `/ws` and inspect the response. Confirm `memory` and `cost` are absent from the `tools` array.)
- An inter-agent chat consult (A asks B) works as before, and the consulted agent's runtime now has access to `memory` (verifiable by prompting B through A with something like "what's in your memory?" — should not error with "no such tool").

- [ ] **Step 4: Cleanup commit (only if needed)**

If the manual smoke check surfaced any issue (e.g. a stale warning, an incorrect doc line missed by Task 7), fix it and commit separately with a `fix(...)` message before declaring done.

---

## Self-Review (run after writing)

**1. Spec coverage** — checking each section of the design doc against this plan:

- "New `UniversalTools` helper" → Task 1 ✓
- "Call-site consolidation" (four sites) → Tasks 2 (factory), 3 (factory wiring), 4 (`MobileTransport.CreateRuntime`), 5 (cron) ✓
- "Backward-compatible config cleanup" — silent no-op cases → Task 6 step 4 ✓; AllTools filter → Task 6 ✓
- "Implicit migration" — handled by the picker filter (no extra code) ✓
- "Documentation" — CLAUDE.md → Task 7; docs/configuration.md → Task 8 ✓
- Testing items (UniversalToolsTests, AgentRuntimeFactoryTests, legacy AGENT.md acceptance) → Tasks 1, 2, 6; legacy acceptance is implicitly verified by full test suite + smoke check (Task 9 step 3). The `case "memory":` branch is a literal pass-through; explicit test would require making `ResolveTools` testable. Acceptable trade-off given the simplicity.
- "Risks" section — not actionable as tasks, documented in spec ✓

**2. Placeholder scan** — searched for `TBD`, `TODO`, "fill in", "appropriate", "etc." — none present.

**3. Type / name consistency**:
- Helper signature: `UniversalTools.Build(string agentName, AgentDefinition agentDef, string sharedMemoryPath, IReadOnlyDictionary<string, CostLedger> costLedgers)` — same in Tasks 1, 3, 4, 5 ✓
- Factory constructor: `AgentRuntimeFactory(Model, string?, CostLedger?, IReadOnlyList<AgentTool>?)` — same in Task 2 (creation), Task 3 (call site) ✓
- `_universalToolNames` field referenced only in Task 6 ✓

No fixes needed.
