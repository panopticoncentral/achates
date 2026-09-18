# Working Memory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make core memory load automatically into every session and add a
small agent-curated `working` tier of live threads that loads on every turn.

**Architecture:** A new `MemoryContext` transform injects memory into the
outgoing completion payload via `AgentOptions.TransformContext`, never into
persisted messages and never into the system prompt. Core memory is prepended
to the *first* user message so it sits inside the cacheable prefix; working
memory is prepended to the *latest* user message alongside the existing
temporal note, because the agent edits it mid-conversation. The `memory` tool
gains a third `working` scope backed by `~/.achates/agents/{name}/working.md`.

**Tech Stack:** .NET 10 preview, C# with nullable reference types, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-24-working-memory-design.md`

**One deviation from the spec's testing section:** the spec placed the
byte-stable-prefix invariant in `OpenRouterCacheControlTests`. This plan puts
it in `MemoryContextTests`
(`Core_block_is_byte_identical_across_turns_when_the_file_is_unchanged`)
instead — the invariant is a property of the injected block, and asserting it
there is direct rather than reconstructing a full provider request to observe
it second-hand.

## Global Constraints

- Build: `dotnet build Achates.slnx`. Test: `dotnet test Achates.slnx`.
- Solution file is `Achates.slnx` (XML format), never a legacy `.sln`.
- Nullable reference types and implicit usings are enabled.
- `sealed` on concrete classes by default; collection expressions (`[]`) over `new List<T>()`.
- Raw string literals (`"""`) for multi-line text blocks.
- Working memory default budget: **500 tokens**. Core memory default budget changes **8000 → 2000**.
- Working memory file: `~/.achates/agents/{name}/working.md` — a sibling of `memory.md`, NOT inside the archive dir `agents/{name}/memory/`.
- Working memory is never shared and is never injected on the agent-to-agent consult path.
- Shared memory (`~/.achates/memory.md`) is **not** injected by this work — it stays tool-fetched.
- Token estimate uses the existing heuristic: `content.Length / 4.0`.
- Tests live in `tests/Achates.Tests`; internals are visible to that project.

---

### Task 1: `working` scope in `MemoryTool`

Adds the third scope to the tool itself. The tool's constructor gains
optional parameters so existing call sites still compile; Task 2 wires real
values through.

**Files:**
- Modify: `src/Achates.Server/Tools/MemoryTool.cs`
- Test: `tests/Achates.Tests/MemoryToolTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `MemoryTool(string sharedPath, string agentPath, bool sharedEnabled, int coreBudgetTokens = 0, string? workingPath = null, int workingBudgetTokens = 0)`
  - `public const int MemoryTool.DefaultWorkingBudgetTokens = 500`
  - `public static int MemoryTool.ResolveWorkingBudgetTokens(int? perAgent, int? globalDefault)`
  - Scope string `"working"` accepted by `read` / `save` / `append` / `edit`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Achates.Tests/MemoryToolTests.cs`, before the closing brace.
Note `_dir` and `_agentPath` already exist in the fixture; `working.md` is
resolved as a sibling of `_agentPath`, so it lands in `_dir`.

```csharp
    // ---------------- Working scope ----------------

    [Fact]
    public void SchemaExposesWorkingScope_WhenSharedEnabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);
        Assert.Contains("\"working\"", tool.Parameters.GetRawText());
    }

    [Fact]
    public void SchemaExposesWorkingScope_WhenSharedDisabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);
        var schemaJson = tool.Parameters.GetRawText();
        Assert.Contains("\"working\"", schemaJson);
        Assert.DoesNotContain("\"shared\"", schemaJson);
    }

    [Fact]
    public async Task Save_WorkingScope_WritesSiblingFile()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("save")), ("scope", JE("working")), ("content", JE("- ask about the surgery"))));

        var workingPath = Path.Combine(_dir, "working.md");
        Assert.True(File.Exists(workingPath));
        Assert.Equal("- ask about the surgery", await File.ReadAllTextAsync(workingPath));
        Assert.Equal("", File.Exists(_agentPath) ? await File.ReadAllTextAsync(_agentPath) : "");
    }

    [Fact]
    public async Task Read_WorkingScope_ReturnsOnlyWorking()
    {
        await File.WriteAllTextAsync(_agentPath, "core note");
        await File.WriteAllTextAsync(Path.Combine(_dir, "working.md"), "- live thread");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);

        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("read")), ("scope", JE("working")))));

        Assert.Contains("- live thread", text);
        Assert.DoesNotContain("core note", text);
    }

    [Fact]
    public async Task Append_And_Edit_WorkingScope_RoundTrip()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);
        var workingPath = Path.Combine(_dir, "working.md");

        await tool.ExecuteAsync("t", Args(
            ("action", JE("save")), ("scope", JE("working")), ("content", JE("- one"))));
        await tool.ExecuteAsync("t", Args(
            ("action", JE("append")), ("scope", JE("working")), ("content", JE("- two"))));
        Assert.Equal("- one\n- two", await File.ReadAllTextAsync(workingPath));

        await tool.ExecuteAsync("t", Args(
            ("action", JE("edit")), ("scope", JE("working")), ("old", JE("- one\n")), ("new", JE(""))));
        Assert.Equal("- two", await File.ReadAllTextAsync(workingPath));
    }

    [Fact]
    public async Task Save_WorkingScope_Succeeds_WhenSharedDisabled()
    {
        // The old code force-rewrote every scope to "agent" when shared was off,
        // which would silently swallow working writes for in-character agents.
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("save")), ("scope", JE("working")), ("content", JE("- in-character thread"))));

        Assert.Equal("- in-character thread",
            await File.ReadAllTextAsync(Path.Combine(_dir, "working.md")));
    }

    [Fact]
    public async Task Save_SharedScope_DowngradesToAgent_WhenSharedDisabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("save")), ("scope", JE("shared")), ("content", JE("real world fact"))));

        Assert.False(File.Exists(_sharedPath));
        Assert.Equal("real world fact", await File.ReadAllTextAsync(_agentPath));
    }

    [Fact]
    public async Task Read_WithoutScope_IncludesWorking_WhenSharedEnabled()
    {
        await File.WriteAllTextAsync(_sharedPath, "user is Paul");
        await File.WriteAllTextAsync(_agentPath, "core note");
        await File.WriteAllTextAsync(Path.Combine(_dir, "working.md"), "- live thread");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);

        var text = Text(await tool.ExecuteAsync("t", Args(("action", JE("read")))));

        Assert.Contains("user is Paul", text);
        Assert.Contains("core note", text);
        Assert.Contains("- live thread", text);
    }

    [Fact]
    public async Task Save_WorkingScope_OverBudget_AppendsNote()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true, workingBudgetTokens: 5);

        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("save")), ("scope", JE("working")), ("content", JE(new string('x', 400))))));

        Assert.Contains("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_WorkingScope_UnderBudget_NoNote()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true, workingBudgetTokens: 100_000);

        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("save")), ("scope", JE("working")), ("content", JE("- short")))));

        Assert.DoesNotContain("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(200, 400, 200)]
    [InlineData(null, 400, 400)]
    [InlineData(null, null, MemoryTool.DefaultWorkingBudgetTokens)]
    public void ResolveWorkingBudgetTokens_FollowsResolutionOrder(int? perAgent, int? globalDefault, int expected)
    {
        Assert.Equal(expected, MemoryTool.ResolveWorkingBudgetTokens(perAgent, globalDefault));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~MemoryToolTests"`

Expected: compile errors — `MemoryTool` has no `workingBudgetTokens`
parameter and no `DefaultWorkingBudgetTokens` / `ResolveWorkingBudgetTokens`
members.

- [ ] **Step 3: Add the working-scope constants, fields, and constructor parameters**

In `src/Achates.Server/Tools/MemoryTool.cs`, replace the constant block,
fields, and constructor (currently around lines 50-70):

```csharp
    private readonly string _sharedPath;
    private readonly string _agentPath;
    private readonly string _workingPath;
    private readonly bool _sharedEnabled;
    private readonly string _archiveDir;
    private readonly int _coreBudgetTokens;
    private readonly int _workingBudgetTokens;

    /// <summary>Fallback core-memory budget (tokens) when neither the agent nor config sets one.</summary>
    public const int DefaultCoreBudgetTokens = 8000;

    /// <summary>Fallback working-memory budget (tokens) when neither the agent nor config sets one.</summary>
    public const int DefaultWorkingBudgetTokens = 500;

    private const double CharsPerToken = 4.0;

    public MemoryTool(
        string sharedPath,
        string agentPath,
        bool sharedEnabled,
        int coreBudgetTokens = 0,
        string? workingPath = null,
        int workingBudgetTokens = 0)
    {
        _sharedPath = sharedPath;
        _agentPath = agentPath;
        _sharedEnabled = sharedEnabled;
        _coreBudgetTokens = coreBudgetTokens;
        _workingBudgetTokens = workingBudgetTokens;
        var agentDir = Path.GetDirectoryName(Path.GetFullPath(agentPath)) ?? ".";
        _workingPath = workingPath ?? Path.Combine(agentDir, "working.md");
        _archiveDir = Path.Combine(agentDir, "memory");
    }

    /// <summary>Resolution order: per-agent capability → global default → fallback constant.</summary>
    public static int ResolveCoreBudgetTokens(int? perAgent, int? globalDefault) =>
        perAgent ?? globalDefault ?? DefaultCoreBudgetTokens;

    /// <summary>Resolution order: per-agent capability → global default → fallback constant.</summary>
    public static int ResolveWorkingBudgetTokens(int? perAgent, int? globalDefault) =>
        perAgent ?? globalDefault ?? DefaultWorkingBudgetTokens;
```

Note `DefaultCoreBudgetTokens` stays at 8000 here — Task 2 changes it, so
that migration lands as its own reviewable commit.

- [ ] **Step 4: Replace `CoreBudgetNote` with a scope-aware budget note**

Delete the `CoreBudgetNote` method and add in its place:

```csharp
    /// <summary>
    /// Non-blocking note appended to a write when estimated tokens exceed the
    /// scope's budget. Empty string when no budget is set or the content fits.
    /// </summary>
    private static string BudgetNote(string content, int budget, string label, string remedy)
    {
        if (budget <= 0) return "";
        var tokens = (int)(content.Length / CharsPerToken);
        if (tokens <= budget) return "";
        return $"\n\n[{label} is now ~{tokens} tokens, over the ~{budget} target. {remedy}]";
    }
```

- [ ] **Step 5: Add a scope resolver and route every operation through it**

Add this private record and method next to `BudgetNote`:

```csharp
    /// <summary>A resolved memory scope: where it lives, what to call it, and its soft budget.</summary>
    private sealed record ScopeTarget(string Path, string Label, int Budget, string Remedy);

    /// <summary>
    /// Maps a scope name to its file. Anything unrecognized resolves to the agent
    /// core file, which is the historical default for a missing scope.
    /// </summary>
    private ScopeTarget Resolve(string scope) => scope switch
    {
        "shared" => new(_sharedPath, "Shared", 0, ""),
        "working" => new(_workingPath, "Working", _workingBudgetTokens,
            "Drop items you have already surfaced or resolved, and move anything durable into core memory."),
        _ => new(_agentPath, "Agent", _coreBudgetTokens,
            "Move dated/completed sections into the archive (append/save with a `file`), or summarize verbose event logs there."),
    };
```

Now rewrite the four scoped operations to use it. Replace `SaveMemoryAsync`,
`AppendMemoryAsync`, and `EditMemoryAsync` bodies' path/label/note lines:

```csharp
    private async Task<AgentToolResult> SaveMemoryAsync(string scope, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "Content is required when saving." }],
            };
        }

        var target = Resolve(scope);

        var dir = Path.GetDirectoryName(target.Path);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(target.Path, content);
        var note = BudgetNote(content, target.Budget, $"{target.Label} memory", target.Remedy);
        return Msg($"{target.Label} memory saved.{note}");
    }

    /// <summary>
    /// Appends text to the end of a scope's memory without rewriting the rest —
    /// the cheap path for adding a new learning. Keeps payloads small so a routine
    /// nightly update never has to regenerate the whole file as a single tool call.
    /// </summary>
    private async Task<AgentToolResult> AppendMemoryAsync(string scope, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Msg("Content is required when appending.");
        }

        var target = Resolve(scope);
        var dir = Path.GetDirectoryName(target.Path);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        var existing = File.Exists(target.Path) ? await File.ReadAllTextAsync(target.Path) : "";
        var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
        var combined = existing + separator + content;
        await File.WriteAllTextAsync(target.Path, combined);

        var note = BudgetNote(combined, target.Budget, $"{target.Label} memory", target.Remedy);
        return Msg($"Appended to {target.Label.ToLowerInvariant()} memory.{note}");
    }

    /// <summary>
    /// Replaces a unique substring of a scope's memory — the cheap path for
    /// correcting or removing a specific fact (an empty replacement deletes it).
    /// Refuses ambiguous or missing matches rather than guess, so the model must
    /// quote enough surrounding text to be unambiguous.
    /// </summary>
    private async Task<AgentToolResult> EditMemoryAsync(string scope, string? oldText, string? newText)
    {
        var target = Resolve(scope);
        if (!File.Exists(target.Path))
            return Msg($"{target.Label} memory is empty — nothing to edit.");

        var existing = await File.ReadAllTextAsync(target.Path);
        var (updated, error) = ReplaceUnique(existing, oldText, newText);
        if (error is not null)
            return Msg(error);

        await File.WriteAllTextAsync(target.Path, updated!);
        var note = BudgetNote(updated!, target.Budget, $"{target.Label} memory", target.Remedy);
        return Msg($"{target.Label} memory updated.{note}");
    }
```

- [ ] **Step 6: Rewrite `ReadMemoryAsync` for three scopes**

Replace the whole `ReadMemoryAsync` method:

```csharp
    private async Task<AgentToolResult> ReadMemoryAsync(string? scope)
    {
        // Scoped reads (one file).
        if (scope is "shared" or "agent" or "working")
        {
            var target = Resolve(scope);

            if (!File.Exists(target.Path))
            {
                return new AgentToolResult
                {
                    Content = [new CompletionTextContent { Text = $"{target.Label} memory is empty. Use save to store information." }],
                };
            }

            var content = await File.ReadAllTextAsync(target.Path);
            var note = BudgetNote(content, target.Budget, $"{target.Label} memory", target.Remedy);
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = $"## {target.Label} Memory\n\n{content}{note}" }],
            };
        }

        // Unscoped read — only reachable in shared-enabled mode (in disabled
        // mode `scope` is forced to "agent" by the caller).
        var parts = new List<string>();
        var trailingNote = "";

        if (File.Exists(_sharedPath))
            parts.Add($"## Shared Memory\n\n{await File.ReadAllTextAsync(_sharedPath)}");
        else
            parts.Add("## Shared Memory\n\n(empty)");

        if (File.Exists(_agentPath))
        {
            var agent = await File.ReadAllTextAsync(_agentPath);
            parts.Add($"## Agent Memory\n\n{agent}");
            trailingNote = BudgetNote(agent, _coreBudgetTokens, "Agent memory", Resolve("agent").Remedy);
        }
        else
        {
            parts.Add("## Agent Memory\n\n(empty)");
        }

        if (File.Exists(_workingPath))
            parts.Add($"## Working Memory\n\n{await File.ReadAllTextAsync(_workingPath)}");
        else
            parts.Add("## Working Memory\n\n(empty)");

        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = string.Join("\n\n---\n\n", parts) + trailingNote }],
        };
    }
```

- [ ] **Step 7: Invert the scope forcing in `ExecuteAsync`**

The current line hard-forces `"agent"` whenever shared is disabled, which
would swallow every working write. Replace it:

```csharp
        var action = GetString(arguments, "action") ?? "read";
        // Honor the requested scope, but never let a hand-crafted or
        // schema-ignoring call reach the shared file on a shared-disabled agent —
        // downgrade only that case, leaving `working` and `agent` intact.
        var scope = GetString(arguments, "scope");
        if (!_sharedEnabled && scope is "shared" or null)
            scope = "agent";
```

Note the `null` case: with shared disabled there is no unscoped "read both",
so a missing scope still means agent, exactly as before.

- [ ] **Step 8: Add `working` to both schemas**

In `_bothScopesSchema`, replace the `scope` entry:

```csharp
            ["scope"] = StringEnum(["shared", "agent", "working"],
                "Which memory to target. " +
                "'shared' = facts about the user that any assistant should know (name, family, preferences, important dates). " +
                "'agent' = your durable core notes; already loaded into every conversation. " +
                "'working' = a short list of live threads to raise next time; also already loaded. Keep it small and prune resolved items.",
                "agent"),
```

In `_agentOnlySchema`, add a `scope` entry immediately after `action` (it has
none today):

```csharp
            ["scope"] = StringEnum(["agent", "working"],
                "Which memory to target. " +
                "'agent' = your durable core notes; already loaded into every conversation. " +
                "'working' = a short list of live threads to raise next time; also already loaded. Keep it small and prune resolved items.",
                "agent"),
```

- [ ] **Step 9: Replace the existing "no scope parameter" assertion**

`tests/Achates.Tests/MemoryToolTests.cs:83` currently asserts the agent-only
schema has no `scope` parameter at all:

```csharp
        // The whole 'scope' parameter is gone — schema only describes action + content.
        Assert.DoesNotContain("\"scope\"", schemaJson);
```

That encodes the pre-working-memory design and the spec deliberately changes
it (§4: the agent-only schema gains a `scope` enum of `[agent, working]`).
Replace those two lines, keeping the rest of the enclosing test intact:

```csharp
        // Scope survives in shared-disabled mode so working memory stays reachable,
        // but the shared scope itself is never named.
        Assert.Contains("\"scope\"", schemaJson);
        Assert.Contains("\"working\"", schemaJson);
        Assert.DoesNotContain("\"shared\"", schemaJson);
```

- [ ] **Step 10: Run the full test suite**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~MemoryToolTests"`

Expected: PASS — 42 pre-existing cases (one of them amended in Step 9) plus 13
new cases (10 `[Fact]` + 3 `[Theory]` rows). The pre-existing budget tests
assert only on the substring `"over the"`, which the new `BudgetNote` still
emits, so they keep passing despite the reworded note.

- [ ] **Step 11: Run the whole suite and commit**

Run: `dotnet test Achates.slnx`

Expected: PASS.

```bash
git add src/Achates.Server/Tools/MemoryTool.cs tests/Achates.Tests/MemoryToolTests.cs
git commit -m "feat(memory): add working scope to the memory tool"
```

---

### Task 2: Budget plumbing and the core default tightening

Wires `working.md` and both budgets from config through to the tool, and
lands the 8000 → 2000 core default as its own commit.

**Files:**
- Modify: `src/Achates.Server/AchatesConfig.cs`
- Modify: `src/Achates.Server/AgentLoader.cs`
- Modify: `src/Achates.Server/AgentDefinition.cs`
- Modify: `src/Achates.Server/GatewayService.cs:594-628`
- Modify: `src/Achates.Server/Tools/UniversalTools.cs`
- Modify: `src/Achates.Server/Tools/MemoryTool.cs` (the default constant only)
- Modify: `docs/configuration.md`
- Test: `tests/Achates.Tests/AgentLoaderTests.cs`
- Test: `tests/Achates.Tests/ConfigLoaderTests.cs`

**Interfaces:**
- Consumes: `MemoryTool.ResolveWorkingBudgetTokens`, `MemoryTool.DefaultWorkingBudgetTokens`, and the 6-parameter `MemoryTool` constructor from Task 1.
- Produces:
  - `AgentConfig.WorkingBudgetTokens` (`int?`)
  - `MemoryConfig.DefaultWorkingBudgetTokens` (`int?`)
  - `AgentDefinition.WorkingMemoryPath` (`string`, required)
  - `AgentDefinition.WorkingBudgetTokens` (`int`)
  - AGENT.md capability key `**Working Budget:**`

- [ ] **Step 1: Write the failing tests**

Append to `tests/Achates.Tests/AgentLoaderTests.cs`:

```csharp
    [Fact]
    public void Parse_ReadsWorkingBudgetCapability()
    {
        var md = """
            # Test

            ## Capabilities

            **Working Budget:** 750
            """;

        var config = AgentLoader.Parse(md);

        Assert.NotNull(config);
        Assert.Equal(750, config!.WorkingBudgetTokens);
    }

    [Fact]
    public void Parse_InvalidWorkingBudget_LeavesNull()
    {
        var md = """
            # Test

            ## Capabilities

            **Working Budget:** plenty
            """;

        var config = AgentLoader.Parse(md);

        Assert.NotNull(config);
        Assert.Null(config!.WorkingBudgetTokens);
    }

    [Fact]
    public void Serialize_WritesWorkingBudget_WhenSet()
    {
        var config = new AgentConfig { WorkingBudgetTokens = 750 };
        Assert.Contains("**Working Budget:** 750", AgentLoader.Serialize("Test", config));
    }

    [Fact]
    public void Serialize_OmitsWorkingBudget_WhenNull()
    {
        var config = new AgentConfig();
        Assert.DoesNotContain("**Working Budget:**", AgentLoader.Serialize("Test", config));
    }
```

`AgentLoader.Parse` is `internal static AgentConfig? Parse(string content)`
and `Serialize` is `public static string Serialize(string name, AgentConfig
config)`; both are reachable from the test project. The `## Capabilities`
header is required — `ParseCapabilities` only runs on that section.

Also append to `tests/Achates.Tests/ConfigLoaderTests.cs`, mirroring the
existing `Memory_DefaultBudget_RoundTrips` test:

```csharp
    [Fact]
    public void Memory_DefaultWorkingBudget_RoundTrips()
    {
        var path = TempConfigPath();
        try
        {
            var config = new AchatesConfig
            {
                Provider = new ProviderConfig { Name = "openrouter" },
                Memory = new MemoryConfig { DefaultWorkingBudgetTokens = 750 },
            };
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", path);

            ConfigLoader.Save(config);
            var text = File.ReadAllText(path);
            var loaded = ConfigLoader.Load();

            Assert.Contains("default_working_budget_tokens", text);
            Assert.Equal(750, loaded.Memory?.DefaultWorkingBudgetTokens);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", null);
            File.Delete(path);
        }
    }
```

Also append to `tests/Achates.Tests/MemoryToolTests.cs`:

```csharp
    [Fact]
    public void DefaultCoreBudget_IsTightenedForAlwaysLoadedCore()
    {
        // Core is injected into every session now, so the default budget is
        // sized for context cost rather than for a browse-on-demand file.
        Assert.Equal(2000, MemoryTool.DefaultCoreBudgetTokens);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~AgentLoaderTests|FullyQualifiedName~ConfigLoaderTests|FullyQualifiedName~MemoryToolTests"`

Expected: compile errors on `AgentConfig.WorkingBudgetTokens`, plus
`DefaultCoreBudget_IsTightenedForAlwaysLoadedCore` failing with
`Assert.Equal() Failure: Expected: 2000, Actual: 8000`.

- [ ] **Step 3: Add the config properties**

In `src/Achates.Server/AchatesConfig.cs`, add to `MemoryConfig`:

```csharp
    /// <summary>
    /// Default soft budget (in tokens) for agents' working memory files
    /// (<c>working.md</c>). Per-agent <c>**Working Budget:**</c> overrides this;
    /// absent here too falls back to
    /// <see cref="Tools.MemoryTool.DefaultWorkingBudgetTokens"/>.
    /// </summary>
    public int? DefaultWorkingBudgetTokens { get; set; }
```

And to `AgentConfig`, directly after `MemoryBudgetTokens`:

```csharp
    /// <summary>
    /// Per-agent soft budget (in tokens) for the always-loaded working memory file —
    /// the rolling list of live threads. When it exceeds this, the memory tool appends
    /// a non-blocking nudge to prune. Null falls back to
    /// <c>memory.default_working_budget_tokens</c>, then to
    /// <see cref="Tools.MemoryTool.DefaultWorkingBudgetTokens"/>.
    /// </summary>
    public int? WorkingBudgetTokens { get; set; }
```

- [ ] **Step 4: Add the capability parse and serialize cases**

In `src/Achates.Server/AgentLoader.cs`, add to the capability switch right
after the `"memory budget"` case (around line 381):

```csharp
            case "working budget":
                if (int.TryParse(value, out var workBudget) && workBudget >= 0)
                    config.WorkingBudgetTokens = workBudget;
                break;
```

And in the serializer, right after the `MemoryBudgetTokens` block (around
line 122):

```csharp
        if (config.WorkingBudgetTokens is { } workBudget)
        {
            sb.AppendLine();
            sb.AppendLine($"**Working Budget:** {workBudget}");
        }
```

- [ ] **Step 5: Add the `AgentDefinition` fields**

In `src/Achates.Server/AgentDefinition.cs`, add next to `MemoryPath`:

```csharp
    /// <summary>
    /// Path to the agent's working-memory file (<c>working.md</c>) — the rolling
    /// list of live threads injected on every turn. Sibling of
    /// <see cref="MemoryPath"/>, outside the archive directory.
    /// </summary>
    public required string WorkingMemoryPath { get; init; }
```

And next to `MemoryBudgetTokens`:

```csharp
    /// <summary>
    /// Resolved working-memory soft budget in tokens (per-agent capability → global
    /// default → <see cref="Achates.Server.Tools.MemoryTool.DefaultWorkingBudgetTokens"/>).
    /// Passed to the memory tool.
    /// </summary>
    public int WorkingBudgetTokens { get; init; } = Achates.Server.Tools.MemoryTool.DefaultWorkingBudgetTokens;
```

- [ ] **Step 6: Resolve both in `GatewayService`**

In `src/Achates.Server/GatewayService.cs`, add next to the existing
`memoryPath` line (around line 594):

```csharp
        var workingMemoryPath = Path.Combine(achatesHome, "agents", name, "working.md");
```

And in the `new AgentDefinition { ... }` initializer, add after `MemoryPath`:

```csharp
            WorkingMemoryPath = workingMemoryPath,
```

and after `MemoryBudgetTokens`:

```csharp
            WorkingBudgetTokens = Tools.MemoryTool.ResolveWorkingBudgetTokens(
                agentConfig.WorkingBudgetTokens, config.Memory?.DefaultWorkingBudgetTokens),
```

- [ ] **Step 7: Pass them through `UniversalTools`**

In `src/Achates.Server/Tools/UniversalTools.cs`, replace the `MemoryTool`
construction:

```csharp
            new MemoryTool(
                sharedMemoryPath,
                agentDef.MemoryPath,
                agentDef.SharedMemoryEnabled,
                agentDef.MemoryBudgetTokens,
                agentDef.WorkingMemoryPath,
                agentDef.WorkingBudgetTokens),
```

- [ ] **Step 8: Tighten the core default**

In `src/Achates.Server/Tools/MemoryTool.cs`:

```csharp
    /// <summary>
    /// Fallback core-memory budget (tokens) when neither the agent nor config sets one.
    /// Sized for a file injected into every session's context, not for one fetched
    /// on demand — agents already over this converge via dreamtime consolidation.
    /// </summary>
    public const int DefaultCoreBudgetTokens = 2000;
```

- [ ] **Step 9: Fix any broken construction sites**

`AgentDefinition.WorkingMemoryPath` is `required`, so every object initializer
must set it. There is exactly one in the repo — `GatewayService.cs`, already
handled in Step 6 — so this step should be a no-op confirmation, not a hunt.

Run: `dotnet build Achates.slnx`

Expected: clean. If the compiler does name another `new AgentDefinition { ... }`,
set `WorkingMemoryPath` to a `working.md` sibling of whatever `MemoryPath` that
site already uses.

- [ ] **Step 10: Run the tests**

Run: `dotnet test Achates.slnx`

Expected: PASS.

- [ ] **Step 11: Update `docs/configuration.md` and check `README.md`**

Four edits, matching the file's existing table style:

1. In the `memory` section table (around line 157), add a row after
   `default_budget_tokens`:

   `| `default_working_budget_tokens` | int | `500` | Soft token budget for every agent's working memory file (`working.md`) — the rolling list of live threads injected on every turn. Exceeding it on a write appends a non-blocking pruning nudge; the write is never rejected. Per-agent `**Working Budget:**` takes precedence when set. |`

2. Change `default_budget_tokens`'s default in that same table from `8000` to
   `2000`, and update its description to say core memory is injected into
   every session.

3. In the AGENT.md capabilities table (around line 199), add a row after
   `Memory Budget`:

   `| `Working Budget` | int | _(`memory.default_working_budget_tokens`)_ | Soft token budget for this agent's working memory file. Falls back to `memory.default_working_budget_tokens`, then to the hardcoded default of 500 tokens. |`

4. In the data-paths table (around line 292), add a row after the agent
   `memory.md` row:

   `| `~/.achates/agents/{name}/working.md` | Agent memory — working tier (live threads, injected on every turn). |`

   and in the sample config block (around line 233) add
   `  default_working_budget_tokens: 500` under `memory:`, changing
   `default_budget_tokens: 8000` to `2000`.

Then grep `README.md` for memory wording:

```bash
grep -n -i "memory" README.md
```

If it describes the memory tiers in user-facing terms, add working memory
there too. If it only points at `docs/configuration.md`, leave it alone.

- [ ] **Step 12: Commit**

```bash
git add src/Achates.Server tests/Achates.Tests docs/configuration.md
git commit -m "feat(memory): plumb working-memory path and budgets, tighten core default to 2000"
```

---

### Task 3: The `MemoryContext` transform

The injection engine, with no call sites wired yet. Also lifts
`TemporalContext`'s private note-prepending helper into a shared type.

**Files:**
- Create: `src/Achates.Server/UserMessageNote.cs`
- Create: `src/Achates.Server/MemoryContext.cs`
- Modify: `src/Achates.Server/TemporalContext.cs:143-170`
- Test: `tests/Achates.Tests/MemoryContextTests.cs` (create)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `internal static CompletionContext UserMessageNote.Prepend(CompletionContext context, int targetIndex, string note)`
  - `public static Func<CompletionContext, CompletionContext> MemoryContext.CreateTransform(string corePath, string? workingPath, bool includeWorking)`
  - `public static string MemoryContext.FormatCore(string? content)`
  - `public static string MemoryContext.FormatWorking(string? content)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Achates.Tests/MemoryContextTests.cs`:

```csharp
using Achates.Providers.Completions;
using Achates.Providers.Completions.Messages;
using Achates.Server;

namespace Achates.Tests;

public sealed class MemoryContextTests : IDisposable
{
    private readonly string _dir;
    private readonly string _corePath;
    private readonly string _workingPath;

    public MemoryContextTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"achates-memctx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _corePath = Path.Combine(_dir, "memory.md");
        _workingPath = Path.Combine(_dir, "working.md");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static CompletionContext Ctx(params CompletionMessage[] messages) =>
        new() { Messages = messages };

    private static string TextAt(CompletionContext c, int i) =>
        ((CompletionUserTextMessage)c.Messages[i]).Text;

    // --- Formatting ---

    [Fact]
    public void FormatCore_is_empty_for_empty_content()
    {
        Assert.Equal("", MemoryContext.FormatCore(null));
        Assert.Equal("", MemoryContext.FormatCore("   "));
    }

    [Fact]
    public void FormatWorking_tells_the_model_not_to_recite_the_list()
    {
        var block = MemoryContext.FormatWorking("- ask about the surgery");

        Assert.Contains("- ask about the surgery", block);
        Assert.Contains("Working Memory", block);
        Assert.Contains("checklist", block);
    }

    // --- Transform ---

    [Fact]
    public void Transform_is_noop_when_no_user_message_present()
    {
        File.WriteAllText(_corePath, "core");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);
        var context = Ctx();

        Assert.Same(context, transform(context));
    }

    [Fact]
    public void Transform_is_noop_when_both_files_missing()
    {
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);
        var context = Ctx(new CompletionUserTextMessage { Text = "hi", Timestamp = 100 });

        var result = transform(context);

        Assert.Equal("hi", TextAt(result, 0));
    }

    [Fact]
    public void Transform_puts_core_on_the_first_user_message_and_working_on_the_last()
    {
        File.WriteAllText(_corePath, "core fact");
        File.WriteAllText(_workingPath, "- live thread");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        var result = transform(Ctx(
            new CompletionUserTextMessage { Text = "first", Timestamp = 100 },
            new CompletionUserTextMessage { Text = "second", Timestamp = 200 }));

        var first = TextAt(result, 0);
        var last = TextAt(result, 1);

        Assert.Contains("core fact", first);
        Assert.DoesNotContain("live thread", first);
        Assert.Contains("live thread", last);
        Assert.DoesNotContain("core fact", last);
    }

    [Fact]
    public void Transform_puts_core_ahead_of_working_on_a_single_message()
    {
        File.WriteAllText(_corePath, "core fact");
        File.WriteAllText(_workingPath, "- live thread");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        var text = TextAt(transform(Ctx(
            new CompletionUserTextMessage { Text = "hi", Timestamp = 100 })), 0);

        Assert.True(text.IndexOf("core fact", StringComparison.Ordinal)
                  < text.IndexOf("live thread", StringComparison.Ordinal));
        Assert.EndsWith("hi", text);
    }

    [Fact]
    public void Transform_omits_working_when_includeWorking_is_false()
    {
        File.WriteAllText(_corePath, "core fact");
        File.WriteAllText(_workingPath, "- live thread");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: false);

        var text = TextAt(transform(Ctx(
            new CompletionUserTextMessage { Text = "hi", Timestamp = 100 })), 0);

        Assert.Contains("core fact", text);
        Assert.DoesNotContain("live thread", text);
    }

    [Fact]
    public void Transform_does_not_reread_within_the_same_user_turn()
    {
        File.WriteAllText(_corePath, "original");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        var turn = new CompletionUserTextMessage { Text = "hi", Timestamp = 100 };
        var first = TextAt(transform(Ctx(turn)), 0);

        // Same user turn, mid-tool-loop: the file changed but the prefix must not.
        File.WriteAllText(_corePath, "changed");
        var second = TextAt(transform(Ctx(turn)), 0);

        Assert.Equal(first, second);
        Assert.Contains("original", second);
    }

    [Fact]
    public void Transform_rereads_on_a_new_user_turn()
    {
        File.WriteAllText(_corePath, "original");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        transform(Ctx(new CompletionUserTextMessage { Text = "hi", Timestamp = 100 }));

        File.WriteAllText(_corePath, "changed");
        var second = TextAt(transform(Ctx(
            new CompletionUserTextMessage { Text = "hi", Timestamp = 100 },
            new CompletionUserTextMessage { Text = "again", Timestamp = 200 })), 0);

        Assert.Contains("changed", second);
    }

    [Fact]
    public void Core_block_is_byte_identical_across_turns_when_the_file_is_unchanged()
    {
        // This is the invariant the head-injection design rests on: an unchanged
        // memory.md must leave the cacheable prefix untouched turn over turn.
        File.WriteAllText(_corePath, "core fact");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        var turn1 = TextAt(transform(Ctx(
            new CompletionUserTextMessage { Text = "first", Timestamp = 100 })), 0);

        var turn2 = TextAt(transform(Ctx(
            new CompletionUserTextMessage { Text = "first", Timestamp = 100 },
            new CompletionUserTextMessage { Text = "second", Timestamp = 200 })), 0);

        Assert.Equal(turn1, turn2);
    }

    [Fact]
    public void Composes_with_the_temporal_transform_without_either_clobbering_the_other()
    {
        File.WriteAllText(_corePath, "core fact");
        File.WriteAllText(_workingPath, "- live thread");
        var temporal = TemporalContext.CreateTransform();
        var memory = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        var text = TextAt(memory(temporal(Ctx(
            new CompletionUserTextMessage { Text = "hi", Timestamp = 100 }))), 0);

        Assert.Contains("core fact", text);
        Assert.Contains("live thread", text);
        Assert.Contains("Current time:", text);
        Assert.EndsWith("hi", text);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~MemoryContextTests"`

Expected: compile error — `MemoryContext` does not exist.

- [ ] **Step 3: Extract the shared note-prepending helper**

Create `src/Achates.Server/UserMessageNote.cs`:

```csharp
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;

namespace Achates.Server;

/// <summary>
/// Prepends an ephemeral note to a user message in an outgoing completion payload.
/// Shared by <see cref="TemporalContext"/> and <see cref="MemoryContext"/>; the
/// note is never persisted to <c>AgentRuntime.Messages</c>.
/// </summary>
internal static class UserMessageNote
{
    public static CompletionContext Prepend(
        CompletionContext context, int targetIndex, string note)
    {
        var messages = context.Messages.ToList();
        var target = messages[targetIndex];

        messages[targetIndex] = target switch
        {
            CompletionUserTextMessage text => text with { Text = $"{note}\n\n{text.Text}" },
            CompletionUserContentMessage content => content with
            {
                Content =
                [
                    new CompletionTextContent { Text = note },
                    .. content.Content,
                ],
            },
            _ => target, // Unknown user-message subtype; leave alone.
        };

        return context with { Messages = messages };
    }
}
```

- [ ] **Step 4: Point `TemporalContext` at the shared helper**

In `src/Achates.Server/TemporalContext.cs`, delete the private
`InjectIntoUserMessage` method entirely and change its one call site:

```csharp
            return UserMessageNote.Prepend(context, latestUserIdx, cachedNote);
```

- [ ] **Step 5: Run the existing temporal tests to prove the extraction was behaviour-preserving**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~TemporalContextTests"`

Expected: PASS, unchanged.

- [ ] **Step 6: Write `MemoryContext`**

Create `src/Achates.Server/MemoryContext.cs`:

```csharp
using Achates.Providers.Completions;
using Achates.Providers.Completions.Messages;

namespace Achates.Server;

/// <summary>
/// Injects the agent's always-loaded memory tiers into the outgoing completion
/// payload — never persisted to <c>AgentRuntime.Messages</c>, never baked into
/// the system prompt.
///
/// <para>
/// The two tiers inject at different points because they have different
/// volatility. <b>Core</b> goes at the HEAD (first user message): it changes
/// roughly nightly at dreamtime, so on almost every turn the bytes are
/// identical and the block sits inside the prefix that Anthropic and OpenAI
/// cache — paid once per session rather than once per turn. <b>Working</b> goes
/// at the TAIL (latest user message) alongside the temporal note: it is small,
/// and the agent edits it mid-conversation, so freshness is the point.
/// </para>
///
/// <para>
/// Both blocks are recomputed only when a new user turn begins, so within-turn
/// tool iterations keep a byte-stable prefix. An agent that writes to working
/// memory mid-turn sees it on the next user turn — acceptable, since it wrote
/// the content itself.
/// </para>
/// </summary>
public static class MemoryContext
{
    /// <summary>
    /// Builds a transform for <see cref="Agent.AgentOptions.TransformContext"/>.
    /// The returned delegate is stateful (it caches the rendered blocks) and is
    /// expected to be bound to a single <see cref="Agent.AgentRuntime"/>.
    /// </summary>
    /// <param name="corePath">Path to the agent's <c>memory.md</c>.</param>
    /// <param name="workingPath">Path to the agent's <c>working.md</c>. May be null when <paramref name="includeWorking"/> is false.</param>
    /// <param name="includeWorking">
    /// False on the agent-to-agent consult path: injecting "raise this when we
    /// next talk" into a one-round consult risks the agent raising a
    /// user-directed intention with another agent.
    /// </param>
    public static Func<CompletionContext, CompletionContext> CreateTransform(
        string corePath,
        string? workingPath,
        bool includeWorking)
    {
        long lastSeenUserTimestamp = -1;
        string coreBlock = "";
        string workingBlock = "";
        var primed = false;

        return context =>
        {
            var firstUserIdx = -1;
            var latestUserIdx = -1;
            CompletionUserMessage? latestUser = null;

            for (var i = 0; i < context.Messages.Count; i++)
            {
                if (context.Messages[i] is not CompletionUserMessage cum) continue;
                if (firstUserIdx < 0) firstUserIdx = i;
                latestUserIdx = i;
                latestUser = cum;
            }
            if (latestUser is null) return context;

            if (!primed || latestUser.Timestamp != lastSeenUserTimestamp)
            {
                coreBlock = FormatCore(ReadOrNull(corePath));
                workingBlock = includeWorking && workingPath is not null
                    ? FormatWorking(ReadOrNull(workingPath))
                    : "";
                lastSeenUserTimestamp = latestUser.Timestamp;
                primed = true;
            }

            if (coreBlock.Length == 0 && workingBlock.Length == 0) return context;

            var result = context;
            // Working first: on a single-message payload both indices are the
            // same, and prepending working before core leaves core ahead of it.
            if (workingBlock.Length > 0)
                result = UserMessageNote.Prepend(result, latestUserIdx, workingBlock);
            if (coreBlock.Length > 0)
                result = UserMessageNote.Prepend(result, firstUserIdx, coreBlock);
            return result;
        };
    }

    /// <summary>Renders the core-memory block, or an empty string when there is nothing to show.</summary>
    public static string FormatCore(string? content) =>
        string.IsNullOrWhiteSpace(content)
            ? ""
            : $"""
               ## Core Memory

               Your durable notes, already loaded. Do not call the memory tool to read this back.

               {content.Trim()}
               """;

    /// <summary>Renders the working-memory block, or an empty string when there is nothing to show.</summary>
    public static string FormatWorking(string? content) =>
        string.IsNullOrWhiteSpace(content)
            ? ""
            : $"""
               ## Working Memory

               Live threads you are tracking, already loaded. Raise one when the conversation
               makes it relevant — not all at once, and never as a checklist you recite up
               front. Once a thread has been surfaced or resolved, remove it with the memory
               tool (`scope: working`).

               {content.Trim()}
               """;

    /// <summary>
    /// Reads a memory file, treating any IO failure as "no memory" rather than
    /// failing the turn — a missing or locked file must never break a conversation.
    /// </summary>
    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~MemoryContextTests|FullyQualifiedName~TemporalContextTests"`

Expected: PASS.

- [ ] **Step 8: Run the whole suite and commit**

Run: `dotnet test Achates.slnx`

Expected: PASS.

```bash
git add src/Achates.Server/MemoryContext.cs src/Achates.Server/UserMessageNote.cs src/Achates.Server/TemporalContext.cs tests/Achates.Tests/MemoryContextTests.cs
git commit -m "feat(memory): add MemoryContext transform for always-loaded tiers"
```

---

### Task 4: Wire injection at the three runtime sites

**Files:**
- Modify: `src/Achates.Agent/Agent.cs:75` (expose `TransformContext`)
- Modify: `src/Achates.Server/Chat/AgentRuntimeFactory.cs`
- Modify: `src/Achates.Server/Mobile/MobileTransport.cs:87-95` and `:2222-2231`
- Modify: `src/Achates.Server/Cron/CronService.cs:373-380`
- Modify: `src/Achates.Server/CLAUDE.md`
- Test: `tests/Achates.Tests/AgentRuntimeFactoryTests.cs`

**Interfaces:**
- Consumes: `MemoryContext.CreateTransform(string, string?, bool)` from Task 3; `AgentDefinition.WorkingMemoryPath` from Task 2.
- Produces: `AgentRuntimeFactory(Model model, string? systemPrompt = null, CostLedger? ledger = null, IReadOnlyList<AgentTool>? universalTools = null, string? coreMemoryPath = null)`.

- [ ] **Step 1: Write the failing test**

First add the accessor the test needs. `AgentRuntime` stores
`_transformContext` but exposes no getter, so add one in
`src/Achates.Agent/Agent.cs` beside the existing
`public string? SystemPrompt => _systemPrompt;` (line 75):

```csharp
    /// <summary>The context transform applied to outgoing payloads, if any.</summary>
    public Func<CompletionContext, CompletionContext>? TransformContext => _transformContext;
```

That needs `using Achates.Providers.Completions;` in `Agent.cs` if it is not
already present.

Then append to `tests/Achates.Tests/AgentRuntimeFactoryTests.cs`. That file
already has a `TestModel()` helper backed by a `StubProvider`; use it. Add
`using Achates.Providers.Completions.Messages;` to the file's usings for
`CompletionUserTextMessage`.

```csharp
    [Fact]
    public void Consult_runtime_injects_core_memory_but_not_working_memory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"achates-arf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var corePath = Path.Combine(dir, "memory.md");
            File.WriteAllText(corePath, "core fact");
            File.WriteAllText(Path.Combine(dir, "working.md"), "- live thread");

            var factory = new AgentRuntimeFactory(
                TestModel(), systemPrompt: null, ledger: null, universalTools: null,
                coreMemoryPath: corePath);

            var runtime = factory.Create([]);
            var transform = runtime.TransformContext;
            Assert.NotNull(transform);

            var result = transform!(new CompletionContext
            {
                Messages = [new CompletionUserTextMessage { Text = "hi", Timestamp = 100 }],
            });

            var text = ((CompletionUserTextMessage)result.Messages[0]).Text;
            Assert.Contains("core fact", text);
            Assert.DoesNotContain("live thread", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~AgentRuntimeFactoryTests"`

Expected: compile error — `AgentRuntimeFactory` has no `coreMemoryPath` parameter.
(The `AgentRuntime.TransformContext` accessor added above should already compile.)

- [ ] **Step 3: Add core-memory injection to the consult factory**

Replace the body of `src/Achates.Server/Chat/AgentRuntimeFactory.cs`:

```csharp
using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Models;

namespace Achates.Server.Chat;

/// <summary>
/// Builds a target <see cref="AgentRuntime"/> for one chat round, seeded with a
/// reconstructed message history. Injectable so tests can supply a stub model.
/// Carries the target agent's cost ledger so the round's usage is recorded.
/// Carries a precomputed universal-tools list (memory + cost) so the consulted
/// agent has the same always-on tools it would have in a normal session.
///
/// Core memory is injected; working memory deliberately is not. A one-round
/// consult has no "next time we talk", and injecting user-directed intentions
/// risks the agent raising them with another agent instead.
/// </summary>
public sealed class AgentRuntimeFactory(
    Model model,
    string? systemPrompt = null,
    CostLedger? ledger = null,
    IReadOnlyList<AgentTool>? universalTools = null,
    string? coreMemoryPath = null)
{
    public CostLedger? Ledger { get; } = ledger;

    public AgentRuntime Create(IReadOnlyList<AgentMessage> seed)
    {
        var temporal = TemporalContext.CreateTransform();
        Func<CompletionContext, CompletionContext> transform = temporal;

        if (coreMemoryPath is not null)
        {
            var memory = MemoryContext.CreateTransform(
                coreMemoryPath, workingPath: null, includeWorking: false);
            transform = ctx => memory(temporal(ctx));
        }

        return new AgentRuntime(new AgentOptions
        {
            Model = model,
            SystemPrompt = systemPrompt,
            Tools = universalTools,
            Messages = seed,
            TransformContext = transform,
        });
    }
}
```

- [ ] **Step 4: Pass the core path in from `MobileTransport`'s factory construction**

In `src/Achates.Server/Mobile/MobileTransport.cs` (around line 91), extend the
factory construction:

```csharp
                return new AgentRuntimeFactory(
                    def.Model,
                    def.SystemPrompt,
                    def.CostLedger,
                    universalTools,
                    def.MemoryPath);
```

- [ ] **Step 5: Wire both tiers into `MobileTransport.CreateRuntime`**

In the `return new AgentRuntime(new AgentOptions { ... })` at the end of
`CreateRuntime` (around line 2222), replace the `TransformContext` line:

```csharp
        var temporal = TemporalContext.CreateTransform();
        var memory = MemoryContext.CreateTransform(
            agentDef.MemoryPath, agentDef.WorkingMemoryPath, includeWorking: true);

        return new AgentRuntime(new AgentOptions
        {
            Model = agentDef.Model,
            SystemPrompt = agentDef.SystemPrompt,
            Tools = tools,
            CompletionOptions = agentDef.CompletionOptions,
            Messages = messages,
            TransformContext = ctx => memory(temporal(ctx)),
        });
```

- [ ] **Step 6: Wire both tiers into `CronService.ExecuteJobAsync`**

In `src/Achates.Server/Cron/CronService.cs` (around line 373), replace the
runtime construction:

```csharp
        var temporal = TemporalContext.CreateTransform();
        var memory = MemoryContext.CreateTransform(
            agentDef.MemoryPath, agentDef.WorkingMemoryPath, includeWorking: true);

        var agent = new AgentRuntime(new AgentOptions
        {
            Model = agentDef.Model,
            SystemPrompt = systemPrompt,
            Tools = tools,
            CompletionOptions = agentDef.CompletionOptions,
            TransformContext = ctx => memory(temporal(ctx)),
        });
```

Also update the comment three lines above, which currently says only the
temporal context is injected at the tail:

```csharp
        // Build tool list and system prompt — dreamtime jobs get special treatment.
        // The system prompt is date-free and memory-free; temporal context and the
        // always-loaded memory tiers are injected per-turn into the outgoing payload
        // via TemporalContext / MemoryContext transforms.
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test Achates.slnx`

Expected: PASS.

- [ ] **Step 8: Update `src/Achates.Server/CLAUDE.md`**

Add `MemoryContext` and `UserMessageNote` wherever that file documents
`TemporalContext` and the server's prompt assembly, and extend the
`AgentDefinition` bullet (line 26) to list `WorkingMemoryPath` and
`WorkingBudgetTokens` alongside `MemoryPath`. State that core memory injects
at the head of the payload and working memory at the tail, and that the
consult path gets core only.

- [ ] **Step 9: Commit**

```bash
git add src/Achates.Agent src/Achates.Server tests/Achates.Tests
git commit -m "feat(memory): inject core and working memory at the three runtime sites"
```

---

### Task 5: Rewrite the prompts that describe memory

Two prompts assert things that were false before this work and are true after
it. Both are rewritten rather than extended.

**Files:**
- Modify: `src/Achates.Server/SystemPrompt.cs:60-81`
- Modify: `src/Achates.Server/Cron/CronService.cs:38-84`
- Modify: `CLAUDE.md`
- Test: `tests/Achates.Tests/SystemPromptTests.cs`

**Interfaces:**
- Consumes: nothing — prompt text only. `SystemPrompt.Build`'s signature is unchanged.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Achates.Tests/SystemPromptTests.cs`, matching the
`SystemPrompt.Build(...)` call style already used in that file:

```csharp
    [Fact]
    public void Memory_section_does_not_tell_the_model_to_read_memory_at_conversation_start()
    {
        // Core and working memory are injected into the payload now; instructing
        // the model to read them back burns a tool call on content already present.
        var prompt = SystemPrompt.Build(sharedMemoryEnabled: true);

        Assert.DoesNotContain("Read memory at the start of new conversations", prompt);
        Assert.DoesNotContain("Read your memory at the start of new conversations", prompt);
    }

    [Fact]
    public void Memory_section_describes_working_memory_when_shared_enabled()
    {
        var prompt = SystemPrompt.Build(sharedMemoryEnabled: true);

        Assert.Contains("Working memory", prompt);
        Assert.Contains("scope: working", prompt);
        Assert.Contains("Shared memory", prompt);
    }

    [Fact]
    public void Memory_section_describes_working_memory_but_never_shared_when_shared_disabled()
    {
        var prompt = SystemPrompt.Build(sharedMemoryEnabled: false);

        Assert.Contains("Working memory", prompt);
        Assert.Contains("scope: working", prompt);
        Assert.DoesNotContain("Shared memory", prompt);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~SystemPromptTests"`

Expected: FAIL — the prompt still contains "Read memory at the start of new
conversations" and has no working-memory text.

- [ ] **Step 3: Rewrite the memory section of the system prompt**

In `src/Achates.Server/SystemPrompt.cs`, replace the entire `## Memory` block
(the `if (sharedMemoryEnabled) { ... } else { ... }` and its surrounding
`lines.Add("## Memory")`):

```csharp
        // Memory section — always included since memory tool is added per-session.
        // Core and working memory are injected into the outgoing payload by
        // MemoryContext, so this section tells the model they are already present
        // rather than telling it to go fetch them.
        // Roleplay/in-character agents (sharedMemoryEnabled == false) get a
        // variant that never names the shared scope.
        lines.Add("## Memory");
        lines.Add("You have persistent memory that survives session resets.");
        lines.Add("- **Core memory** (`scope: agent`): your durable notes — facts, preferences, recurring patterns, current state. Already loaded into this conversation; don't call the memory tool to read it back.");
        lines.Add("- **Working memory** (`scope: working`): a short list of live threads to raise when they become relevant. Also already loaded. Keep it small — add a thread when something is worth returning to, and remove it once you've surfaced or resolved it.");
        if (sharedMemoryEnabled)
        {
            lines.Add("- **Shared memory** (`scope: shared`): facts about the user that any assistant should know — name, family, preferences, important dates. All agents read and write this same file. This one is not preloaded; read it when you need it.");
        }
        lines.Add("- **Memory archive**: topical files retrieved on demand. Use `action: list` to see them, `action: search` to find content, and read/save/append/edit with a `file` to work with one.");
        lines.Add("Keep core focused: dated logs, completed items, and long histories belong in the archive.");
        lines.Add("When saving, include everything you want to keep — `save` replaces the file for that scope. Prefer `append`/`edit` for small updates.");
        lines.Add("");
```

- [ ] **Step 4: Run the system prompt tests**

Run: `dotnet test Achates.slnx --filter "FullyQualifiedName~SystemPromptTests"`

Expected: three PRE-EXISTING tests fail, because they assert on the bullet
label this task renames from "Agent memory" to "Core memory":

- `Includes_both_memory_scopes_when_shared_enabled` (line ~61)
- the test at line ~77 asserting `Contains("Agent memory", result)`
- `Omits_shared_memory_when_shared_disabled` (line ~83, its line ~93 assertion)

In each, change `Assert.Contains("Agent memory", result)` to
`Assert.Contains("Core memory", result)`. Leave every other assertion in those
tests alone — in particular `Assert.DoesNotContain("Shared memory", result)`
must keep passing, and it does. The rename is the point of this task, not a
regression.

Re-run until green.

- [ ] **Step 5: Rewrite the dreamtime instructions**

In `src/Achates.Server/Cron/CronService.cs`, replace the `DreamtimeInstructions`
constant. The tier list becomes three, step 2's "read core once up front"
becomes "core is already in context", and a working-memory step is added:

```csharp
    private const string DreamtimeInstructions = """

        --- Dreamtime Mode ---

        You are performing your nightly memory review and consolidation.

        You keep memory in three tiers:
        - **Core memory** (your main file): durable facts, relationships, recurring
          patterns, and current state. It is already loaded into this session — you do
          not need to read it, and re-reading it bloats this session for no benefit.
        - **Working memory** (`scope: working`): a short, live list of threads to raise
          in your next conversation. Also already loaded. This is where "I noticed X,
          mention it next time" belongs.
        - **Memory archive** (topical files): retrieved on demand via the memory tool's
          `list` / `search` / read with a `file`. This is where dated logs, completed
          items, and long historical accounts belong.

        Your job:

        1. Use the sessions tool to list recent sessions; decide which contain anything worth
           remembering; read those in full.
        2. Core and working memory are already in your context above. Edit them with
           INCREMENTAL `edit`/`append` using the text you can already see — do not read
           either file back first, and reserve a full `save` for real restructuring.
        3. Record tonight's observations by APPENDING to an archive journal file
           (`append` with `file: journal`) — and ONLY there. A dated nightly note must
           NEVER also be written into core memory; dated notes live in the archive.
        4. Update core memory with genuinely durable new learnings (preferences, facts,
           corrections, patterns).
        5. Curate working memory (`scope: working`):
           - REMOVE threads you can see were surfaced or resolved in tonight's sessions.
           - PROMOTE anything that turned out to be durable into core memory, or
             relocate the detail to an archive file.
           - ADD a thread for anything you noticed tonight that is worth raising in your
             next conversation. Keep each one to a line or two.
           - Working memory is small on purpose. If it is over budget, prune before you
             add.
        6. Keep core lean — consolidate, don't just accumulate:
           - Move dated/completed sections OUT of core (`edit` to remove) into a topical
             archive file (`append` with a `file`). Old dated `Dreamtime Note —` entries and
             old dated metric/score logs do NOT belong in core — relocate them.
           - SUMMARIZE, don't just relocate: collapse verbose dated event reconstructions to
             a few summary lines in core, moving the full account to an archive file. Core
             should get denser, not just shorter.
           - Prune completed items from any pending/todo list in core.
        7. Every core write tells you whether core is over its size budget. If it says you
           are OVER budget, you MUST consolidate this run before finishing: move at least
           the oldest one or two dated sections and any completed items out of core into
           the archive. You need not clear the whole backlog in one night, but every
           over-budget run must leave core SMALLER than it started — never larger.

        Focus on durable knowledge that helps you serve the user better. Do NOT memorize
        transient details (specific appointment times, one-off questions) in core — those go
        to the archive journal if anywhere.

        When you're done, briefly summarize what you changed and why.
        """;
```

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test Achates.slnx`

Expected: PASS. Any cron test asserting on the old dreamtime wording needs
updating to the new text.

- [ ] **Step 7: Update the root `CLAUDE.md`**

The "Core Concepts" section describes an Agent as having "persistent memory".
Extend that to name the tiers and say which are preloaded: shared, core
(preloaded at the head of the payload), working (preloaded at the tail), and
archive (on demand). Keep it to a sentence or two — `docs/configuration.md`
holds the detail, and the root file explicitly defers config format to it.

- [ ] **Step 8: Commit**

```bash
git add src/Achates.Server tests/Achates.Tests CLAUDE.md
git commit -m "docs(memory): rewrite system and dreamtime prompts for the three tiers"
```

---

## Deferred (explicitly not in this plan)

The mobile memory browser. `HandleMemoryList` / `HandleMemoryGetAsync` /
`HandleMemorySetAsync` key scope as `"shared"` or a bare agent name
(`src/Achates.Server/Mobile/MobileTransport.cs:1759`), a namespace with no
room for a second file per agent. Exposing `working.md` there needs a key
convention plus a matching iOS client change. Working memory is agent-curated
and dreamtime-maintained without it.
