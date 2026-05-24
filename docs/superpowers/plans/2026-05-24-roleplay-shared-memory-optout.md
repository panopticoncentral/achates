# Per-Agent Shared-Memory Opt-Out — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-agent `**Shared Memory:** false` capability that hides the shared-memory scope from `MemoryTool`'s schema, so roleplay/chatbot agents only see their own private notes.

**Architecture:** A new optional boolean capability in `AGENT.md`, defaulting to `true`. It flows through `AgentConfig.SharedMemory` (nullable) → `AgentDefinition.SharedMemoryEnabled` (resolved bool) → a new third constructor parameter on `MemoryTool` that switches between a two-scope schema (today) and a single-scope schema (no `scope` parameter at all). All four runtime-construction sites already call `UniversalTools.Build` with `AgentDefinition` in hand, so wiring is one line in `UniversalTools.Build`. Three editing surfaces (`agent.get`/`agent.update` RPCs, iOS edit sheet, `AgentManagerTool` modify action) all expose the new boolean alongside their existing capability fields.

**Tech Stack:** .NET 10 preview, ASP.NET Core, xUnit, SwiftUI (iOS app).

**Spec:** [`docs/superpowers/specs/2026-05-24-roleplay-shared-memory-optout-design.md`](../specs/2026-05-24-roleplay-shared-memory-optout-design.md).

---

## File Structure

Each touched file has one focused responsibility relative to this change.

**Server (C#):**

| File | Role in this change |
| ---- | ------------------- |
| `src/Achates.Server/AchatesConfig.cs` | Add `bool? SharedMemory` to `AgentConfig`. |
| `src/Achates.Server/AgentLoader.cs` | Parse `**Shared Memory:** true/false`; serialize the line only when `false`. |
| `src/Achates.Server/AgentDefinition.cs` | Add resolved `bool SharedMemoryEnabled`. |
| `src/Achates.Server/GatewayService.cs` | Resolve `SharedMemory ?? true` into the `AgentDefinition`. |
| `src/Achates.Server/Tools/MemoryTool.cs` | New third constructor param `sharedEnabled`; dynamic schema; single-scope routing when disabled. |
| `src/Achates.Server/Tools/UniversalTools.cs` | Pass `agentDef.SharedMemoryEnabled` into `new MemoryTool(...)`. |
| `src/Achates.Server/Mobile/MobileTransport.cs` | `agent.get` returns `shared_memory`; `agent.update` accepts it. |
| `src/Achates.Server/Tools/AgentManagerTool.cs` | `modify` action accepts `shared_memory`. |

**iOS (Swift):**

| File | Role |
| ---- | ---- |
| `apple/Achates/Models/AgentEditModel.swift` | Add `sharedMemory: Bool` field; codec in `from`/`toParams`. |
| `apple/Achates/Views/AgentEditView.swift` | Toggle row in Capabilities section + binding. |

**Tests:**

| File | Role |
| ---- | ---- |
| `tests/Achates.Tests/AgentLoaderTests.cs` | Parse + serialize + roundtrip cases. |
| `tests/Achates.Tests/MemoryToolTests.cs` | **New file.** Both modes: schema shape, read, save, defensive fallthrough. |
| `tests/Achates.Tests/UniversalToolsTests.cs` | Builds reflect `SharedMemoryEnabled`. |
| `tests/Achates.Tests/AgentManagerToolTests.cs` | Modify accepts `shared_memory`. |

**Docs:**

| File | Role |
| ---- | ---- |
| `CLAUDE.md` | Extend `MemoryTool` description; add `Shared Memory` to capabilities key list. |

---

## Task 1: Config field + AgentLoader parse/serialize

**Files:**
- Modify: `src/Achates.Server/AchatesConfig.cs`
- Modify: `src/Achates.Server/AgentLoader.cs:308-352` (parse switch) and `AgentLoader.cs:109-113` (serialize)
- Modify: `tests/Achates.Tests/AgentLoaderTests.cs`

- [ ] **Step 1: Add the failing parse + serialize + roundtrip tests**

Append the following at the bottom of `tests/Achates.Tests/AgentLoaderTests.cs`, immediately before the final closing `}` of the class.

```csharp
[Theory]
[InlineData("**Shared Memory:** false", false)]
[InlineData("**Shared Memory:** False", false)]
[InlineData("**Shared Memory:** true", true)]
[InlineData("**Shared Memory:** TRUE", true)]
public void Parse_ReadsSharedMemoryCapability(string capabilityLine, bool expected)
{
    var md = $"""
        # Test

        ## Capabilities

        {capabilityLine}
        """;

    var config = AgentLoader.Parse(md);

    Assert.NotNull(config);
    Assert.Equal(expected, config!.SharedMemory);
}

[Fact]
public void Parse_LeavesSharedMemoryNullWhenAbsent()
{
    var md = """
        # Test

        ## Capabilities

        **Tools:**
          - memory
        """;

    var config = AgentLoader.Parse(md);

    Assert.NotNull(config);
    Assert.Null(config!.SharedMemory);
}

[Fact]
public void Parse_LeavesSharedMemoryNullOnInvalidValue()
{
    var md = """
        # Test

        ## Capabilities

        **Shared Memory:** maybe
        """;

    var config = AgentLoader.Parse(md);

    Assert.NotNull(config);
    Assert.Null(config!.SharedMemory);
}

[Fact]
public void Serialize_EmitsSharedMemoryOnlyWhenFalse()
{
    var falseConfig = new AgentConfig { SharedMemory = false };
    Assert.Contains("**Shared Memory:** false", AgentLoader.Serialize("Test", falseConfig));

    var trueConfig = new AgentConfig { SharedMemory = true };
    Assert.DoesNotContain("Shared Memory", AgentLoader.Serialize("Test", trueConfig));

    var nullConfig = new AgentConfig { SharedMemory = null };
    Assert.DoesNotContain("Shared Memory", AgentLoader.Serialize("Test", nullConfig));
}

[Fact]
public void Parse_Serialize_Roundtrips_SharedMemoryFalse()
{
    var original = new AgentConfig
    {
        Title = "Test",
        Description = "desc",
        SharedMemory = false,
        Prompt = "prompt",
    };

    var md = AgentLoader.Serialize("Test", original);
    var roundtripped = AgentLoader.Parse(md);

    Assert.NotNull(roundtripped);
    Assert.False(roundtripped!.SharedMemory);
}
```

- [ ] **Step 2: Run the tests to confirm they fail to compile**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~AgentLoaderTests"`
Expected: build error `'AgentConfig' does not contain a definition for 'SharedMemory'`.

- [ ] **Step 3: Add the `SharedMemory` property to `AgentConfig`**

In `src/Achates.Server/AchatesConfig.cs`, add this property to `AgentConfig` immediately after the `Dreamtime` property (around line 77):

```csharp
/// <summary>
/// Whether the agent may access the shared memory scope (universal user facts at
/// <c>~/.achates/memory.md</c>). Null means "not specified" — resolves to the
/// default of <c>true</c>. Setting this to <c>false</c> hides the shared scope
/// from <see cref="Tools.MemoryTool"/>'s schema so the model never sees it —
/// useful for roleplay/in-character agents that should not be polluted by
/// real-world identity facts.
/// </summary>
public bool? SharedMemory { get; set; }
```

- [ ] **Step 4: Run tests; they now compile but most still fail**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~AgentLoaderTests"`
Expected: the parse/serialize tests fail with the new property still null after parse and not serialized when false.

- [ ] **Step 5: Add the parse case in `AgentLoader.ApplyCapability`**

In `src/Achates.Server/AgentLoader.cs`, inside the `switch (key)` block in `ApplyCapability` (after line 351, immediately before the closing brace of the switch), add:

```csharp
case "shared memory":
    if (value is not null && bool.TryParse(value, out var sharedMemory))
        config.SharedMemory = sharedMemory;
    break;
```

- [ ] **Step 6: Add the serialize emission**

In `src/Achates.Server/AgentLoader.cs`, immediately after the `Dreamtime` serialization block (line 109-113), add:

```csharp
if (config.SharedMemory == false)
{
    sb.AppendLine();
    sb.AppendLine("**Shared Memory:** false");
}
```

Note the explicit `== false` comparison — `null` (absent) and `true` both skip the line.

- [ ] **Step 7: Run tests to confirm they all pass**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~AgentLoaderTests"`
Expected: all `AgentLoaderTests` pass.

- [ ] **Step 8: Commit**

```bash
git add src/Achates.Server/AchatesConfig.cs \
        src/Achates.Server/AgentLoader.cs \
        tests/Achates.Tests/AgentLoaderTests.cs
git commit -m "feat(agent-config): add SharedMemory capability to AGENT.md

Per-agent boolean (null/true/false), parsed via the existing
**Shared Memory:** capability line and serialized only when
explicitly false. Existing agent files unchanged."
```

---

## Task 2: AgentDefinition field + GatewayService resolution

**Files:**
- Modify: `src/Achates.Server/AgentDefinition.cs`
- Modify: `src/Achates.Server/GatewayService.cs:535-552` (the `new AgentDefinition { ... }` block)

This task is plumbing; it's covered by Task 3/4's tests rather than its own.

- [ ] **Step 1: Add `SharedMemoryEnabled` to `AgentDefinition`**

In `src/Achates.Server/AgentDefinition.cs`, after the `Dreamtime` property (around line 44, before the closing brace), add:

```csharp
/// <summary>
/// Whether <see cref="Tools.MemoryTool"/> exposes the shared memory scope to
/// the model. Resolved from <see cref="AgentConfig.SharedMemory"/> with a
/// default of <c>true</c>. When false, the tool's schema omits the
/// <c>scope</c> parameter and reads/saves only the agent-local file.
/// </summary>
public bool SharedMemoryEnabled { get; init; } = true;
```

- [ ] **Step 2: Set it in `GatewayService.ResolveAgentAsync`**

In `src/Achates.Server/GatewayService.cs`, in the `var agentDef = new AgentDefinition { ... }` block (currently lines 535-552), add this property assignment immediately before the closing `};`:

```csharp
SharedMemoryEnabled = agentConfig.SharedMemory ?? true,
```

The final initializer ordering should be (last few lines):

```csharp
AvatarData = avatarData,
Dreamtime = agentConfig.Dreamtime,
SharedMemoryEnabled = agentConfig.SharedMemory ?? true,
```

- [ ] **Step 3: Build the solution to confirm no compilation errors**

Run: `dotnet build Achates.slnx`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Achates.Server/AgentDefinition.cs \
        src/Achates.Server/GatewayService.cs
git commit -m "feat(agent-def): resolve SharedMemory into AgentDefinition

Defaults to true when AgentConfig.SharedMemory is null."
```

---

## Task 3: MemoryTool dual-mode behavior

**Files:**
- Create: `tests/Achates.Tests/MemoryToolTests.cs`
- Modify: `src/Achates.Server/Tools/MemoryTool.cs`

- [ ] **Step 1: Create the failing test file**

Create `tests/Achates.Tests/MemoryToolTests.cs` with:

```csharp
using System.Text.Json;
using Achates.Providers.Completions.Content;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class MemoryToolTests : IDisposable
{
    private readonly string _dir;
    private readonly string _sharedPath;
    private readonly string _agentPath;

    public MemoryToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"achates-memorytool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _sharedPath = Path.Combine(_dir, "shared.md");
        _agentPath = Path.Combine(_dir, "agent.md");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static Dictionary<string, object?> Args(params (string key, object? value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    private static JsonElement JE(string s) =>
        JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement;

    private static string Text(AgentToolResult r) =>
        ((CompletionTextContent)r.Content[0]).Text;

    // ---------------- Shared-enabled mode (today's behavior) ----------------

    [Fact]
    public void SchemaExposesBothScopes_WhenSharedEnabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);
        var schemaJson = tool.Parameters.GetRawText();
        Assert.Contains("\"shared\"", schemaJson);
        Assert.Contains("\"agent\"", schemaJson);
    }

    [Fact]
    public async Task Read_WithoutScope_ReturnsBoth_WhenSharedEnabled()
    {
        await File.WriteAllTextAsync(_sharedPath, "user is Paul");
        await File.WriteAllTextAsync(_agentPath, "campaign log");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);

        var result = await tool.ExecuteAsync("t", Args(("action", JE("read"))));

        var text = Text(result);
        Assert.Contains("user is Paul", text);
        Assert.Contains("campaign log", text);
    }

    [Fact]
    public async Task Save_WithSharedScope_WritesSharedFile_WhenSharedEnabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("save")),
            ("scope", JE("shared")),
            ("content", JE("shared note"))));

        Assert.Equal("shared note", await File.ReadAllTextAsync(_sharedPath));
        Assert.False(File.Exists(_agentPath));
    }

    // ---------------- Shared-disabled mode (the new path) ----------------

    [Fact]
    public void SchemaOmitsScope_WhenSharedDisabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);
        var schemaJson = tool.Parameters.GetRawText();
        Assert.DoesNotContain("\"shared\"", schemaJson);
        // The whole 'scope' parameter is gone — schema only describes action + content.
        Assert.DoesNotContain("\"scope\"", schemaJson);
    }

    [Fact]
    public async Task Read_WithoutScope_ReturnsOnlyAgent_WhenSharedDisabled()
    {
        await File.WriteAllTextAsync(_sharedPath, "user is Paul");
        await File.WriteAllTextAsync(_agentPath, "campaign log");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        var result = await tool.ExecuteAsync("t", Args(("action", JE("read"))));

        var text = Text(result);
        Assert.Contains("campaign log", text);
        Assert.DoesNotContain("user is Paul", text);
    }

    [Fact]
    public async Task Save_WithoutScope_WritesAgentFile_WhenSharedDisabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("save")),
            ("content", JE("agent note"))));

        Assert.Equal("agent note", await File.ReadAllTextAsync(_agentPath));
        Assert.False(File.Exists(_sharedPath));
    }

    [Fact]
    public async Task Save_WithSharedScope_RoutesToAgent_WhenSharedDisabled()
    {
        // Defensive fallthrough: if a hand-crafted call slips through, it must not
        // touch the shared file.
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("save")),
            ("scope", JE("shared")),
            ("content", JE("intended for shared"))));

        Assert.False(File.Exists(_sharedPath));
        Assert.Equal("intended for shared", await File.ReadAllTextAsync(_agentPath));
    }
}
```

- [ ] **Step 2: Run the tests to confirm they fail to compile**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~MemoryToolTests"`
Expected: build error `'MemoryTool' does not contain a constructor that takes 3 arguments`.

- [ ] **Step 3: Rewrite `MemoryTool.cs` for dual-mode**

Replace the entire contents of `src/Achates.Server/Tools/MemoryTool.cs` with:

```csharp
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Reads and writes persistent memory files. When <paramref name="sharedEnabled"/>
/// is true, exposes both a shared memory (facts every agent should know about
/// the user) and a per-agent memory (agent-specific notes). When false, the
/// shared scope is hidden from the model entirely — the schema lists no
/// <c>scope</c> parameter and reads/saves only the agent-local file. This is
/// the roleplay/in-character configuration: it prevents real-world identity
/// facts from polluting in-character context.
/// Memory survives session resets in both modes.
/// </summary>
internal sealed class MemoryTool : AgentTool
{
    private static readonly JsonElement _bothScopesSchema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["read", "save"], "Action to perform.", "read"),
            ["scope"] = StringEnum(["shared", "agent"],
                "Which memory to target. " +
                "'shared' = facts about the user that any assistant should know (name, family, preferences, important dates). " +
                "'agent' = notes specific to this assistant's role and past conversations.",
                "agent"),
            ["content"] = StringSchema("Content to save. Required when action is 'save'. This replaces the entire memory file for the chosen scope, so include everything you want to keep."),
        },
        required: ["action"]);

    private static readonly JsonElement _agentOnlySchema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["read", "save"], "Action to perform.", "read"),
            ["content"] = StringSchema("Content to save. Required when action is 'save'. This replaces the entire memory file, so include everything you want to keep."),
        },
        required: ["action"]);

    private readonly string _sharedPath;
    private readonly string _agentPath;
    private readonly bool _sharedEnabled;

    public MemoryTool(string sharedPath, string agentPath, bool sharedEnabled)
    {
        _sharedPath = sharedPath;
        _agentPath = agentPath;
        _sharedEnabled = sharedEnabled;
    }

    public override string Name => "memory";
    public override string Description => _sharedEnabled
        ? "Read or save persistent memory. Use 'shared' scope for universal user facts, 'agent' scope for your own notes."
        : "Read or save your persistent private notes. Survives across sessions.";
    public override string Label => "Memory";
    public override JsonElement Parameters => _sharedEnabled ? _bothScopesSchema : _agentOnlySchema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "read";
        // When shared is disabled, force every request to agent scope — defensive
        // against hand-crafted calls or schema-disrespecting models. The shared
        // file is never touched in that mode.
        var scope = _sharedEnabled ? (GetString(arguments, "scope") ?? "agent") : "agent";

        return action switch
        {
            "read" => await ReadMemoryAsync(scope),
            "save" => await SaveMemoryAsync(scope, GetString(arguments, "content")),
            _ => new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = $"Unknown action: {action}" }],
            },
        };
    }

    private async Task<AgentToolResult> ReadMemoryAsync(string scope)
    {
        // Scoped reads (one file).
        if (scope is "shared" or "agent")
        {
            var path = scope == "shared" ? _sharedPath : _agentPath;
            var label = scope == "shared" ? "Shared" : "Agent";

            if (!File.Exists(path))
            {
                return new AgentToolResult
                {
                    Content = [new CompletionTextContent { Text = $"{label} memory is empty. Use save to store information." }],
                };
            }

            var content = await File.ReadAllTextAsync(path);
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = $"## {label} Memory\n\n{content}" }],
            };
        }

        // Unscoped read — only reachable in shared-enabled mode (in disabled
        // mode `scope` is forced to "agent" above).
        var parts = new List<string>();

        if (File.Exists(_sharedPath))
        {
            var shared = await File.ReadAllTextAsync(_sharedPath);
            parts.Add($"## Shared Memory\n\n{shared}");
        }
        else
        {
            parts.Add("## Shared Memory\n\n(empty)");
        }

        if (File.Exists(_agentPath))
        {
            var agent = await File.ReadAllTextAsync(_agentPath);
            parts.Add($"## Agent Memory\n\n{agent}");
        }
        else
        {
            parts.Add("## Agent Memory\n\n(empty)");
        }

        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = string.Join("\n\n---\n\n", parts) }],
        };
    }

    private async Task<AgentToolResult> SaveMemoryAsync(string scope, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "Content is required when saving." }],
            };
        }

        var path = scope == "shared" ? _sharedPath : _agentPath;

        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, content);
        var label = scope == "shared" ? "Shared" : "Agent";
        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = $"{label} memory saved." }],
        };
    }

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
```

Three behavioral changes from the original:
1. Constructor now takes `bool sharedEnabled` and the tool stores `_sharedEnabled`.
2. `Description` and `Parameters` switch on `_sharedEnabled`.
3. `ExecuteAsync` forces `scope = "agent"` when `_sharedEnabled` is false, regardless of what the caller passed.

- [ ] **Step 4: Confirm the call site in `UniversalTools` doesn't compile yet**

Run: `dotnet build Achates.slnx`
Expected: build error in `src/Achates.Server/Tools/UniversalTools.cs` — `MemoryTool` constructor now requires three arguments.

- [ ] **Step 5: Pass the flag through `UniversalTools.Build`**

In `src/Achates.Server/Tools/UniversalTools.cs`, change the `MemoryTool` construction from:

```csharp
new MemoryTool(sharedMemoryPath, agentDef.MemoryPath),
```

to:

```csharp
new MemoryTool(sharedMemoryPath, agentDef.MemoryPath, agentDef.SharedMemoryEnabled),
```

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test Achates.slnx`
Expected: all tests pass, including the new `MemoryToolTests`.

- [ ] **Step 7: Commit**

```bash
git add src/Achates.Server/Tools/MemoryTool.cs \
        src/Achates.Server/Tools/UniversalTools.cs \
        tests/Achates.Tests/MemoryToolTests.cs
git commit -m "feat(memory-tool): dual-mode schema based on SharedMemoryEnabled

When sharedEnabled is false, the tool's schema omits the scope
parameter entirely, the description switches to a private-notes
framing, and reads/saves are forced to the agent-local file even
if a caller passes scope=shared."
```

---

## Task 4: UniversalTools coverage

**Files:**
- Modify: `tests/Achates.Tests/UniversalToolsTests.cs`

- [ ] **Step 1: Add a failing test for the SharedMemoryEnabled wiring**

In `tests/Achates.Tests/UniversalToolsTests.cs`, first update `MakeAgentDef` to accept the flag, then add the new test. Change the helper to:

```csharp
private static AgentDefinition MakeAgentDef(string memoryPath, bool sharedMemoryEnabled = true) => new()
{
    DisplayName = "test",
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
    CompletionOptions = null,
    SharedMemoryEnabled = sharedMemoryEnabled,
};
```

And add this test as a new `[Fact]` after `Build_returns_memory_and_cost_when_ledgers_present`:

```csharp
[Fact]
public void Build_passes_SharedMemoryEnabled_to_MemoryTool()
{
    // The schema is the contract: when shared is enabled it must list both
    // scopes; when disabled it must omit the scope parameter entirely.
    var enabledDef = MakeAgentDef("/tmp/a.md", sharedMemoryEnabled: true);
    var enabledTools = UniversalTools.Build("test", enabledDef, "/tmp/s.md",
        new Dictionary<string, CostLedger>());
    var enabledSchema = enabledTools[0].Parameters.GetRawText();
    Assert.Contains("\"shared\"", enabledSchema);
    Assert.Contains("\"scope\"", enabledSchema);

    var disabledDef = MakeAgentDef("/tmp/a.md", sharedMemoryEnabled: false);
    var disabledTools = UniversalTools.Build("test", disabledDef, "/tmp/s.md",
        new Dictionary<string, CostLedger>());
    var disabledSchema = disabledTools[0].Parameters.GetRawText();
    Assert.DoesNotContain("\"shared\"", disabledSchema);
    Assert.DoesNotContain("\"scope\"", disabledSchema);
}
```

- [ ] **Step 2: Run the test to confirm it passes**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~UniversalToolsTests"`
Expected: all `UniversalToolsTests` (including the new one and the existing two) pass. The test was designed against Task 3's implementation, which is already in place.

- [ ] **Step 3: Commit**

```bash
git add tests/Achates.Tests/UniversalToolsTests.cs
git commit -m "test(universal-tools): verify SharedMemoryEnabled flows to MemoryTool"
```

---

## Task 5: `agent.get` / `agent.update` RPC support

**Files:**
- Modify: `src/Achates.Server/Mobile/MobileTransport.cs:1007-1167`

This task wires the new field into the WebSocket protocol. No new test file — the RPC handlers don't have a dedicated unit-test surface today, and the field is purely a pass-through to `AgentConfig` which is covered by Task 1's tests. We rely on a manual build + the iOS task's first run for end-to-end verification.

- [ ] **Step 1: Add `shared_memory` to the `agent.get` response payload**

In `src/Achates.Server/Mobile/MobileTransport.cs`, inside `HandleAgentGetAsync`, in the `JsonSerializer.SerializeToElement(new { ... })` object (currently lines 1028-1044), add a new field. The full block should end like this:

```csharp
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
    dreamtime = config.Dreamtime?.ToString("HH:mm"),
    model = config.Model,
    thinking_model = config.ThinkingModel,
    default_model = DefaultModelId,
    default_thinking_model = DefaultThinkingModelId,
    shared_memory = config.SharedMemory ?? true,
}, JsonOptions);
```

The `?? true` reflects the resolved-default behavior: clients always see the effective value, even when the agent file omits the line.

- [ ] **Step 2: Accept `shared_memory` in `agent.update`**

In the same file, inside `HandleAgentUpdateAsync`, immediately after the `thinking_model` block (currently lines 1109-1113), add:

```csharp
if (p.TryGetProperty("shared_memory", out var smProp))
{
    if (smProp.ValueKind == JsonValueKind.True)
        config.SharedMemory = true;
    else if (smProp.ValueKind == JsonValueKind.False)
        config.SharedMemory = false;
    // Any other value type leaves config.SharedMemory at its default (null).
}
```

- [ ] **Step 3: Build to confirm the project compiles**

Run: `dotnet build Achates.slnx`
Expected: build succeeds.

- [ ] **Step 4: Confirm no existing tests broke**

Run: `dotnet test Achates.slnx`
Expected: all tests still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Achates.Server/Mobile/MobileTransport.cs
git commit -m "feat(rpc): expose shared_memory on agent.get/agent.update

agent.get always returns the effective bool (defaulting to true).
agent.update accepts a boolean; non-boolean values are ignored."
```

---

## Task 6: `AgentManagerTool` modify support

**Files:**
- Modify: `src/Achates.Server/Tools/AgentManagerTool.cs:22-38` (schema) and `:160-198` (modify handler)
- Modify: `tests/Achates.Tests/AgentManagerToolTests.cs`

- [ ] **Step 1: Add the failing test**

Append to `tests/Achates.Tests/AgentManagerToolTests.cs`, before the final `}`:

```csharp
[Fact]
public async Task Modify_SetsSharedMemoryFalse_PersistsToAgentFile()
{
    await SeedAgentAsync("Roleplay Bot", "A DM.", "You are a DM.");
    var tool = CreateTool();

    var result = await tool.ExecuteAsync("m1", Args(
        ("action", JE("modify")),
        ("agent", JE("roleplay-bot")),
        ("shared_memory", JsonDocument.Parse("false").RootElement)));

    Assert.Contains("modified", Text(result), StringComparison.OrdinalIgnoreCase);
    var content = await File.ReadAllTextAsync(
        Path.Combine(_agentsDir, "roleplay-bot", "AGENT.md"));
    Assert.Contains("**Shared Memory:** false", content);
}

[Fact]
public async Task Modify_SetsSharedMemoryTrue_OmitsLineFromAgentFile()
{
    // Start with shared_memory: false so we can verify flipping it back removes the line.
    await SeedAgentAsync("Roleplay Bot", "A DM.", "You are a DM.");
    var tool = CreateTool();
    await tool.ExecuteAsync("m1", Args(
        ("action", JE("modify")),
        ("agent", JE("roleplay-bot")),
        ("shared_memory", JsonDocument.Parse("false").RootElement)));

    await tool.ExecuteAsync("m2", Args(
        ("action", JE("modify")),
        ("agent", JE("roleplay-bot")),
        ("shared_memory", JsonDocument.Parse("true").RootElement)));

    var content = await File.ReadAllTextAsync(
        Path.Combine(_agentsDir, "roleplay-bot", "AGENT.md"));
    Assert.DoesNotContain("Shared Memory", content);
}
```

- [ ] **Step 2: Run the tests to confirm they fail**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~AgentManagerToolTests.Modify_SetsSharedMemory"`
Expected: both new tests fail — the modify action ignores the unknown `shared_memory` field, so the file isn't updated.

- [ ] **Step 3: Add `shared_memory` to the schema**

In `src/Achates.Server/Tools/AgentManagerTool.cs`, in the `_schema` dictionary (lines 22-38), add an entry after the `dreamtime` entry (currently line 37) and before `avatar`:

```csharp
["shared_memory"] = BooleanSchema("When false, the agent's memory tool only sees its own private notes (the universal user memory at ~/.achates/memory.md is hidden). Useful for roleplay or in-character agents. Optional ('modify' only)."),
```

Note: `BooleanSchema` is already available via the existing `using static Achates.Providers.Util.JsonSchemaHelpers;`.

- [ ] **Step 4: Handle the field in `ModifyAgentAsync`**

In the same file, inside `ModifyAgentAsync`:

(a) After the `var hasDreamtime = arguments.ContainsKey("dreamtime");` and `var newDreamtime = ...` lines (around line 172-173), add:

```csharp
var hasSharedMemory = arguments.ContainsKey("shared_memory");
var newSharedMemory = GetBool(arguments, "shared_memory");
```

(b) Update the `anyField` check (around line 176-180) to include `hasSharedMemory`:

```csharp
var anyField = newName is not null || newDescription is not null || newPrompt is not null
    || newTools is not null || newModel is not null || newThinkingModel is not null
    || newProvider is not null || newReasoning is not null || newTemperature is not null
    || newMaxTokens is not null || newAllowedChats is not null || hasDreamtime
    || hasSharedMemory
    || newAvatar is not null;
```

(c) After the `if (hasDreamtime) { ... }` block (around line 200-217), add:

```csharp
if (hasSharedMemory)
{
    // GetBool returns null when the value wasn't a JSON bool, which clears the
    // override (Serialize emits no line for null or true, only for false).
    config.SharedMemory = newSharedMemory;
    changed.Add("shared_memory");
}
```

(d) If `GetBool` is not already defined on the class, add it near the other helpers at the bottom of the file (look for `GetString`, `GetDouble`, `GetInt` — they live alongside each other):

```csharp
private static bool? GetBool(Dictionary<string, object?> args, string key)
{
    if (!args.TryGetValue(key, out var val)) return null;
    if (val is JsonElement je)
    {
        return je.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
    return val is bool b ? b : null;
}
```

Check first — if a `GetBool` already exists, skip this step. (Run `grep -n "GetBool" src/Achates.Server/Tools/AgentManagerTool.cs` to verify.)

- [ ] **Step 5: Run the tests to confirm they pass**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~AgentManagerToolTests"`
Expected: all `AgentManagerToolTests` pass.

- [ ] **Step 6: Run the full suite to catch regressions**

Run: `dotnet test Achates.slnx`
Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Achates.Server/Tools/AgentManagerTool.cs \
        tests/Achates.Tests/AgentManagerToolTests.cs
git commit -m "feat(agent-manager): modify action accepts shared_memory"
```

---

## Task 7: iOS edit sheet

**Files:**
- Modify: `apple/Achates/Models/AgentEditModel.swift`
- Modify: `apple/Achates/Views/AgentEditView.swift`

This task has no automated test surface in the existing Apple module (the project ships without unit tests for the iOS UI). Verification is manual: build the iOS app, edit an agent, toggle the new setting, save, reopen, confirm persistence.

- [ ] **Step 1: Add `sharedMemory` to `AgentEditModel`**

In `apple/Achates/Models/AgentEditModel.swift`, after the `var dreamtime: Date?` line (line 15) add:

```swift
var sharedMemory: Bool
```

Update the `from(_:)` factory (lines 21-38) to populate it — add this line before the closing parenthesis (so it lands after `defaultThinkingModel`):

```swift
        sharedMemory: payload["shared_memory"]?.boolValue ?? true
```

Don't forget to add a comma after the previous line. The full factory becomes:

```swift
static func from(_ payload: [String: JSONValue]) -> AgentEditModel? {
    AgentEditModel(
        displayName: payload["display_name"]?.stringValue ?? "",
        description: payload["description"]?.stringValue ?? "",
        tools: payload["tools"]?.arrayValue?.compactMap(\.stringValue) ?? [],
        reasoningEffort: payload["reasoning_effort"]?.stringValue,
        temperature: payload["temperature"]?.doubleValue,
        maxTokens: payload["max_tokens"]?.intValue,
        allowedChats: payload["allowed_chats"]?.arrayValue?.compactMap(\.stringValue) ?? [],
        prompt: payload["prompt"]?.stringValue ?? "",
        hasAvatar: payload["has_avatar"]?.boolValue ?? false,
        dreamtime: payload["dreamtime"]?.stringValue.flatMap(parseDreamtime),
        model: nonEmpty(payload["model"]?.stringValue),
        thinkingModel: nonEmpty(payload["thinking_model"]?.stringValue),
        defaultModel: nonEmpty(payload["default_model"]?.stringValue),
        defaultThinkingModel: nonEmpty(payload["default_thinking_model"]?.stringValue),
        sharedMemory: payload["shared_memory"]?.boolValue ?? true
    )
}
```

Update `toParams(agentId:)` to always send the bool. After the `if let d = dreamtime { ... }` block (line 67-69), add:

```swift
params["shared_memory"] = .bool(sharedMemory)
```

- [ ] **Step 2: Add a toggle row + binding in `AgentEditView`**

In `apple/Achates/Views/AgentEditView.swift`, find the `Section { Toggle("Dreamtime", isOn: dreamtimeEnabledBinding) ... }` block (currently lines 186-197). Immediately after that closing brace (i.e. before `Section("Tools")` around line 199), insert a new section:

```swift
            Section {
                Toggle("Access shared user memory", isOn: sharedMemoryBinding)
            } footer: {
                Text("When off, this agent only sees its own private notes — useful for roleplay or in-character chat.")
            }
```

Then add the binding alongside the other private bindings. After `dreamtimeBinding` (currently lines 342-351), add:

```swift
    private var sharedMemoryBinding: Binding<Bool> {
        Binding(
            get: { config?.sharedMemory ?? true },
            set: { newValue in
                guard var c = config else { return }
                c.sharedMemory = newValue
                config = c
            }
        )
    }
```

- [ ] **Step 3: Build the iOS app**

Use the build tool you normally use (or `mcp__xcode__BuildProject`).

Run / invoke: build the `Achates` iOS target.
Expected: build succeeds with no errors related to `sharedMemory`. (Warnings about unrelated parts of the codebase are out of scope for this task.)

- [ ] **Step 4: Manual smoke test (optional, but recommended)**

If a simulator is convenient:

1. Start the server (`dotnet run --project src/Achates.Server`).
2. Build and run the iOS app.
3. Open an agent in the edit sheet — verify the new "Access shared user memory" toggle appears, defaulting to on.
4. Toggle off and save. Re-open the agent file at `~/.achates/agents/<name>/AGENT.md` and confirm `**Shared Memory:** false` is present.
5. Re-open the edit sheet — verify the toggle is now off.
6. Toggle back on and save. Re-open the file and confirm the line is gone.

If a simulator isn't handy, skip this step — the manual checks in Task 8 cover the equivalent flow at the server level.

- [ ] **Step 5: Commit**

```bash
git add apple/Achates/Models/AgentEditModel.swift \
        apple/Achates/Views/AgentEditView.swift
git commit -m "feat(ios): toggle for shared user memory in agent edit sheet"
```

---

## Task 8: Docs + final verification

**Files:**
- Modify: `CLAUDE.md`
- Verify: full `dotnet test Achates.slnx`

- [ ] **Step 1: Extend `MemoryTool`'s description in `CLAUDE.md`**

In `CLAUDE.md`, find the bullet that begins with `` - `MemoryTool` — layered persistent memory `` (in the "Universal Tools" subsection of "Tool System"). Replace it with:

```
- `MemoryTool` — layered persistent memory with two scopes. **Shared memory** at `~/.achates/memory.md` stores universal user facts (name, family, preferences) accessible to all agents. **Agent memory** at `~/.achates/agents/{agentName}/memory.md` stores agent-specific notes. `scope` parameter (`shared` or `agent`) controls which file to target; `read` without a scope returns both. Survives session boundaries. Per-agent opt-out via the `**Shared Memory:** false` capability in AGENT.md (default `true`) — when off, the tool's schema omits the `scope` parameter entirely and the model only sees the agent-local file. This is the roleplay-friendly mode: it prevents real-world identity facts from polluting in-character context.
```

- [ ] **Step 2: Add `Shared Memory` to the AGENT.md capabilities key list**

In `CLAUDE.md`, find the line that lists capability keys (search for `Capabilities keys: `Provider`, `Model`, `Thinking Model`,`). Replace the whole line with:

```
Capabilities keys: `Provider`, `Model`, `Thinking Model`, `Tools`, `Allowed Chats`, `Reasoning Effort`, `Temperature`, `Max Tokens`, `Dreamtime`, `Shared Memory`. List values use sub-bullets; scalar values go inline after the key. `Model` and `Thinking Model` are optional — when omitted, the agent uses `models.base` / `models.thinking` from `config.yaml` as a fallback. `Shared Memory` defaults to `true` when omitted; set to `false` for agents (typically roleplay/in-character) that should not see the shared user memory file.
```

- [ ] **Step 3: Run the full test suite one last time**

Run: `dotnet test Achates.slnx`
Expected: all tests pass.

- [ ] **Step 4: Manual sanity check against a real AGENT.md**

This catches anything the unit tests miss (path joining, file write permissions, hot reload).

1. Start the server: `dotnet run --project src/Achates.Server`. Wait for it to log "Agent '...' resolved with model ...".
2. Pick an existing agent dir under `~/.achates/agents/` and edit its `AGENT.md` to add `**Shared Memory:** false` under `## Capabilities`.
3. Touch the file (or restart the server) to trigger a reload — confirm the log shows the agent resolved without warnings.
4. From the iOS app (or any WebSocket client) call `tools.list` for that agent and confirm the `memory` tool's parameter schema no longer contains `"shared"` or a `scope` parameter.
5. Send a `memory` `read` with no scope and confirm only the agent file's contents come back (it's fine if the agent file is empty — the "Agent memory is empty" placeholder is enough).
6. Revert the AGENT.md change.

- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md
git commit -m "docs(claude-md): document Shared Memory capability"
```

---

## Self-Review Notes

**Spec coverage:**
- Capability parse/serialize → Task 1 ✓
- `AgentConfig.SharedMemory` + `AgentDefinition.SharedMemoryEnabled` + resolution → Tasks 1, 2 ✓
- `MemoryTool` dual-mode (schema + read + save + defensive fallthrough) → Task 3 ✓
- `UniversalTools.Build` passes the flag (one of the four wiring sites; the other three need no changes per spec) → Task 3 + 4 ✓
- `agent.get` / `agent.update` RPC fields → Task 5 ✓
- `AgentManagerTool` modify action → Task 6 ✓
- iOS edit sheet → Task 7 ✓
- `CLAUDE.md` updates → Task 8 ✓
- All test files called out in the spec ("AgentLoaderTests", "MemoryToolTests", "UniversalToolsTests") → Tasks 1, 3, 4 ✓
- Error-handling cases (missing → default true, non-boolean → null, model passes `scope: shared` when disabled → routes to agent) → Tasks 1, 3 ✓

**Type consistency check:**
- `bool? SharedMemory` (AgentConfig) vs `bool SharedMemoryEnabled` (AgentDefinition): different names by design — nullable input vs. resolved output. Consistent across every reference.
- `MemoryTool` constructor: `(string sharedPath, string agentPath, bool sharedEnabled)` — used identically in Task 3 (definition), Task 3 step 5 (UniversalTools), and Task 3 tests.
- iOS field `sharedMemory: Bool` everywhere; JSON key `shared_memory` everywhere.

**No placeholders:** confirmed — every code step includes the actual code, every command step includes the actual command and expected output.
