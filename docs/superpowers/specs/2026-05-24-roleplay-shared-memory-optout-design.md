# Per-Agent Shared-Memory Opt-Out — Design

Date: 2026-05-24
Status: Approved (pending spec review)

## Context

`MemoryTool` is one of the two universal tools added to every agent runtime
by `UniversalTools.Build` (`src/Achates.Server/Tools/UniversalTools.cs:16`).
It exposes two scopes:

- **Shared** (`~/.achates/memory.md`) — universal facts about the user
  (name, family, preferences, important dates) that any assistant should
  know.
- **Agent** (`~/.achates/agents/{name}/memory.md`) — notes specific to that
  agent's role and past conversations.

The current schema (`src/Achates.Server/Tools/MemoryTool.cs:15`) lets the
model pick either scope, and an unscoped `read` returns both concatenated.

This is the right behavior for an assistant whose identity continuity benefits
from knowing real-world facts about its user. It is the *wrong* behavior for a
**pure chatbot / roleplay** agent — a D&D Dungeon Master, a fiction
collaborator, an in-character companion. For those agents, the shared file's
real-world identity facts pollute the in-character context: every memory read
surfaces "user's wife is named X, kids are A and B" alongside the campaign
state, breaking immersion and seeding confusing facts into the roleplay.

The user wants a per-agent way to keep shared memory out of such an agent's
view entirely.

## Goal

Add a per-agent capability that disables the **shared scope** of `MemoryTool`
for a given agent, while preserving full access to the agent-local scope.
The model of a shared-disabled agent never sees the `shared` option in the
tool's schema — not via the `scope` parameter, not via an unscoped read.

## Non-goals

- **No second knob** for the agent-local scope. The chosen granularity is one
  toggle for shared. If a future use case needs memory-less agents we can
  grow into a `Memory: all/agent/none` enum then.
- **No automatic inference** ("if no shared tools listed, assume roleplay").
  The capability is explicit. Magic inference is harder to predict and
  reverse.
- **No changes to `MemoryTool`'s file paths, file format, or save semantics.**
  Same `sharedPath`/`agentPath` parameters, same files on disk. Only the
  schema and the routing of `read`/`save` change when shared is off.
- **No changes to other universal/per-session tools** (`CostTool`, `CronTool`,
  `SessionsTool`). They remain unconditional / opt-in respectively.
- **No changes to Dreamtime.** A roleplay agent with shared off and dreamtime
  on consolidates into agent-local memory only, which is exactly the desired
  behavior.

## Approach

One main approach, since the granularity (shared toggle, not whole-tool) and
the schema mode (omit shared from the model's view when off) were settled in
brainstorming.

The alternative considered and rejected: split `MemoryTool` into two tool
classes (`SharedMemoryTool` + `AgentMemoryTool`). Slightly cleaner
conceptually but duplicates file-I/O and schema boilerplate and changes the
model's mental model of memory from one tool with scopes to two separate
tools. Not worth the marginal cleanliness.

## Architecture

### Capability

A new optional capability in `AGENT.md`:

```markdown
## Capabilities

**Shared Memory:** false
```

- **Default `true`** when the line is omitted. Existing AGENT.md files do not
  need to change.
- `AgentLoader.Serialize` writes the line **only when explicitly `false`**, so
  ordinary assistant agents stay clean.

### Data flow

```
AGENT.md  →  AgentConfig.SharedMemory (bool?, null = default true)
          →  AgentDefinition.SharedMemoryEnabled (bool)
          →  UniversalTools.Build(..., sharedMemoryEnabled)
          →  MemoryTool(sharedPath, agentPath, sharedEnabled)
```

### `MemoryTool` dual-mode behavior

The tool's constructor gains a third parameter:

```csharp
internal sealed class MemoryTool(string sharedPath, string agentPath, bool sharedEnabled) : AgentTool
```

The schema is built dynamically rather than as a `static readonly` literal,
because the available `scope` values depend on `sharedEnabled`:

- **`sharedEnabled == true`** (today's behavior): schema's `scope` enum lists
  both `shared` and `agent`. Unscoped `read` returns both files
  concatenated. Description text unchanged.
- **`sharedEnabled == false`**: schema **omits the `scope` parameter
  entirely**. The tool is single-scope from the model's perspective:
  - `read` reads `agentPath` only.
  - `save` writes to `agentPath` only.
  - `Description` is reworded to drop the "shared/agent" framing
    (e.g. "Read or save your persistent private notes. Survives across
    sessions.").

A defensive fallthrough: if a hand-crafted call or a schema-disrespecting
model passes `scope: shared` to a shared-disabled tool, the parameter is
ignored and the action proceeds against `agentPath` (no error). This keeps
the tool quiet rather than failing loudly on something the model isn't even
supposed to see.

### Wiring

`UniversalTools.Build`'s signature does not change — the flag rides on the
`AgentDefinition` it already receives. The body becomes:

```csharp
public static IReadOnlyList<AgentTool> Build(
    string agentName,
    AgentDefinition agentDef,
    string sharedMemoryPath,
    IReadOnlyDictionary<string, CostLedger> costLedgers)
{
    var tools = new List<AgentTool>
    {
        new MemoryTool(sharedMemoryPath, agentDef.MemoryPath, agentDef.SharedMemoryEnabled),
    };
    if (costLedgers.Count > 0)
        tools.Add(new CostTool(agentName, costLedgers));
    return tools;
}
```

The four call sites that invoke `UniversalTools.Build` need no changes:

- `MobileTransport.CreateRuntime`
- `CronService.BuildJobTools`
- `CronService.BuildDreamtimeTools`
- `AgentRuntimeFactory` chat-target factory

### `AgentConfig` / `AgentDefinition`

- `AgentConfig`: add `bool? SharedMemory { get; set; }` (nullable to
  distinguish "absent" from "explicitly false").
- `AgentDefinition`: add `bool SharedMemoryEnabled { get; init; }` (resolved,
  non-nullable; the resolver applies the default).
- The default resolution lives wherever `AgentDefinition` is built from
  `AgentConfig` today (`GatewayService` agent resolution path): `SharedMemoryEnabled =
  config.SharedMemory ?? true`.

### `AgentLoader` parse / serialize

- **Parse**: extend the `switch (key)` in `ApplyCapability`
  (`src/Achates.Server/AgentLoader.cs:308`) with a `"shared memory"` case
  that parses `value` as `true`/`false` (case-insensitive). Invalid or
  missing → leave `config.SharedMemory` as `null` (treated as default true).
- **Serialize**: in `Serialize`, after the `Dreamtime` block
  (around line 109), emit `**Shared Memory:** false` only when
  `config.SharedMemory == false`. Do not emit the line when `null` or `true`.

## RPC and UI exposure

The system's existing pattern: every `AGENT.md` capability is editable through
three surfaces, all driven from `AgentConfig`. We follow it consistently.

### `agent.get` / `agent.update` RPCs

(`src/Achates.Server/Mobile/MobileTransport.cs`, the existing handlers.)

- `agent.get` response: add a `shared_memory` boolean field. Always present;
  reflects the effective resolved value (so iOS sees `true` for agents that
  never set it).
- `agent.update` request: accept an optional `shared_memory` boolean. Omit =
  no change. The handler writes it onto `AgentConfig.SharedMemory` and
  re-serializes via `AgentLoader.Serialize`, then triggers the existing
  `ReloadAgentAsync` path.
- The existing `agents.changed` broadcast event already fires on
  `agent.update`, so the iOS app picks up the toggled state through the
  normal reload path. No new event.

### iOS agent edit sheet

Add a toggle in the capabilities section:

- **Label**: "Access shared user memory"
- **Subtitle**: "When off, this agent only sees its own private notes —
  useful for roleplay or in-character chat."
- **Default**: on (matches the resolved `shared_memory` value from
  `agent.get`).
- Persisted through the existing `agent.update` flow.

### `AgentManagerTool`

(`src/Achates.Server/Tools/AgentManagerTool.cs`, the `modify` action.)

Accept an optional `shared_memory` boolean in the modify-action schema,
mirroring the existing fields like `temperature` and `max_tokens`. Same
plumbing as a `agent.update` invocation: write to `AgentConfig.SharedMemory`,
re-serialize, reload.

## Error handling

- **Missing capability line** → `AgentConfig.SharedMemory = null` →
  resolved `SharedMemoryEnabled = true`. No warning.
- **Non-boolean value** in AGENT.md (e.g. `**Shared Memory:** maybe`) →
  treat as missing (null), default `true`. No warning — matches how
  `temperature` / `max tokens` parse failures are silently ignored today
  (`AgentLoader.cs:330–341`).
- **Model passes `scope: shared` when disabled** → cannot happen by
  construction (schema doesn't list it). Defensive fallthrough routes to
  `agentPath` quietly.
- **`agent.update` with `shared_memory: null`** → treated as omitted (no
  change), consistent with other optional update fields.

## Testing

All tests in `tests/Achates.Tests/`.

### `AgentLoaderTests`

- `Parses **Shared Memory:** false` → `config.SharedMemory == false`.
- `Parses **Shared Memory:** true` → `config.SharedMemory == true`.
- Missing line → `config.SharedMemory == null`.
- Non-boolean value (`maybe`) → `config.SharedMemory == null`.
- `Serialize` with `SharedMemory == false` → output contains
  `**Shared Memory:** false`.
- `Serialize` with `SharedMemory == true` → output does **not** contain a
  `Shared Memory` line.
- `Serialize` with `SharedMemory == null` → output does **not** contain a
  `Shared Memory` line.
- Round-trip parse → serialize → parse preserves the value.

### `MemoryToolTests`

(New file; no existing one.) Each case writes the file(s) under a temp
directory, constructs `MemoryTool`, invokes `ExecuteAsync`, and asserts the
result text and on-disk state.

- **Shared-enabled mode**:
  - Schema's `scope` enum includes both `shared` and `agent`.
  - `read` with `scope=shared` reads `sharedPath`.
  - `read` with `scope=agent` reads `agentPath`.
  - `read` with no scope returns both, in a single text block.
  - `save` with `scope=shared` writes `sharedPath`.
  - `save` with `scope=agent` writes `agentPath`.
- **Shared-disabled mode**:
  - Schema has no `scope` property (or, if simpler to assert: the
    serialized JSON Schema does not mention the string `"shared"`).
  - `read` with no scope reads `agentPath` only — the `sharedPath` file's
    contents do not appear in the result, even when the file exists.
  - `save` with no scope writes `agentPath`.
  - Defensive: `read`/`save` with `scope=shared` quietly routes to
    `agentPath` (does not touch `sharedPath`).

### `UniversalToolsTests`

(Extend the existing file if present; otherwise new.)

- `Build` returns a `MemoryTool` whose schema reflects the
  `AgentDefinition.SharedMemoryEnabled` flag. Two cases: enabled and
  disabled. Inspecting the built tool's `Parameters` for the presence/
  absence of the `scope` field is sufficient — no need to integration-test
  the four call sites; the contract is on `Build` and the call sites are
  mechanical pass-throughs.

## Out of scope (deferred)

- Splitting `MemoryTool` into separate shared/agent tool classes.
- A `Memory: all/agent/none` enum to also cover a "no memory at all" mode.
- Restricting other "real-world" tools (mail, calendar, contacts, etc.) on
  roleplay agents. Those are already opt-in via `Tools:` and a roleplay
  agent simply won't list them. This spec is about closing the one universal
  leak.
- Migrating existing agents. The new capability is purely additive; nothing
  to migrate.

## Touched files (preview, for plan-time scoping)

Server (C#):

- `src/Achates.Server/Tools/MemoryTool.cs` — constructor param, dynamic
  schema, unscoped-read branch.
- `src/Achates.Server/Tools/UniversalTools.cs` — pass the flag from
  `AgentDefinition` into `MemoryTool`.
- `src/Achates.Server/AgentConfig.cs` — add `bool? SharedMemory`.
- `src/Achates.Server/AgentDefinition.cs` — add `bool SharedMemoryEnabled`.
- `src/Achates.Server/AgentLoader.cs` — parse + serialize the capability.
- `src/Achates.Server/GatewayService.cs` (or wherever `AgentDefinition` is
  built from `AgentConfig`) — apply the default during resolution.
- `src/Achates.Server/Mobile/MobileTransport.cs` — `agent.get` /
  `agent.update` RPC handlers.
- `src/Achates.Server/Tools/AgentManagerTool.cs` — modify-action schema
  + handler.

iOS (Swift):

- Agent edit sheet view — new toggle row.
- Agent model + the `agent.get` / `agent.update` codec — new boolean field.

Tests:

- `tests/Achates.Tests/AgentLoaderTests.cs` — new cases (or extend
  existing).
- `tests/Achates.Tests/MemoryToolTests.cs` — new file.
- `tests/Achates.Tests/UniversalToolsTests.cs` — new/extended.

Docs:

- `CLAUDE.md` — extend the `MemoryTool` description in the Universal Tools
  section, and add `Shared Memory` to the capabilities list under "Agent
  definitions".
- `README.md` — if it documents the AGENT.md format with a capabilities
  example, add the new key. (Confirm during implementation.)
