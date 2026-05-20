# Universal Memory & Cost Tools — Design

Date: 2026-05-20
Status: Approved (pending spec review)

## Context

`MemoryTool` and `CostTool` are configurable per agent (listed in
`AGENT.md`'s `Tools:` capability), but the configuration is already a lie:

- `MobileTransport.CreateRuntime` adds `MemoryTool` unconditionally on every
  user session (line 1876). It also adds `CostTool` unconditionally when any
  agent has a cost ledger (line 1885). The `case "memory":` and `case "cost":`
  branches in `GatewayService.ResolveTools` are no-op pass-throughs whose
  only function is suppressing the "unknown tool" warning that the `default:`
  branch would emit.
- `CronService.BuildJobTools` and `CronService.BuildDreamtimeTools` add both
  tools unconditionally too.
- So whether an agent lists `memory` or `cost` in its tools is irrelevant for
  user sessions, cron jobs, and dreamtime: it always has them.

The one place this is **not** true is the **inter-agent chat target**. When
agent A consults agent B via the `chat` tool, B's runtime is built by
`AgentRuntimeFactory.Create()` with `AgentOptions` that set only `Model`,
`SystemPrompt`, and `Messages` — **no tools at all**. B has neither memory
nor cost during the consult, even though A does throughout its session.

CLAUDE.md compounds this by claiming *"The target runtime gets all its tools
except chat (prevents cascade)"* — a description that has not matched the
code since the inter-agent chat redesign (commit `b4be12c`).

The user's observation: memory is identity continuity, and every agent
should have it at all times — including during chat consults. Cost is in
the same situation (universal observability of spend) and should get the
same treatment in one change.

## Goal

Make `memory` and `cost` truly universal — always available to every agent
runtime the server builds, regardless of configuration — and consolidate the
existing duplicated construction logic so the next universal tool is a
one-line change.

## Non-goals

- Changing `MemoryTool` or `CostTool` themselves. Same classes, same schemas,
  same file paths, same behavior.
- Making other tools (mail, notebook, web_search, etc.) universal. They stay
  opt-in.
- Giving the chat target any tools beyond memory + cost. Consults remain pure
  in the sense that B cannot mail, fetch URLs, generate images, or chat with
  a third agent. The "no cascade" property is preserved.
- Removing the legacy `case "memory":` / `case "cost":` branches entirely.
  They stay as silent no-ops to avoid noisy startup warnings on existing
  configs; eventual removal is left to a follow-up after enough time has
  passed for files to organically migrate.
- iOS app changes. The picker is driven by `tools.list`, which is driven by
  `GatewayService.AllTools`; once that filter is in place, the client
  automatically stops offering memory/cost.

## Design

### New: `UniversalTools` helper

Single source of truth for "what tools every agent always has." New file:
`src/Achates.Server/Tools/UniversalTools.cs`.

```csharp
namespace Achates.Server.Tools;

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

The `CostTool` is conditional on having at least one cost ledger in scope —
this matches today's behavior at `MobileTransport.cs:1884–1885` and avoids
constructing an empty-registry `CostTool`. `MemoryTool` is unconditional.

Adding a future universal tool means adding one line here, not editing four
call sites.

### Call-site consolidation

Four sites use the helper:

| Site | Today | After |
|---|---|---|
| `MobileTransport.CreateRuntime` | Builds memory + cost inline (lines ~1876–1885) | Calls `UniversalTools.Build(...)`; appends per-session extras (cron, sessions, chat) as today |
| `CronService.BuildJobTools` (~490–496) | Same inline construction | Calls `UniversalTools.Build(...)` |
| `CronService.BuildDreamtimeTools` (~509–516) | Same inline construction | Calls `UniversalTools.Build(...)`; still also adds the dreamtime-only `SessionsTool` |
| `AgentRuntimeFactory.Create` | No tools at all | **New** call to `UniversalTools.Build(...)`. Universal tools are computed by `MobileTransport` when it constructs the factory and passed in. |

`MobileTransport` builds the cost-ledger registry for each runtime today
(snapshot of `_agents`); it does the same when constructing the chat
factory. `AgentRuntimeFactory` receives a precomputed
`IReadOnlyList<AgentTool>` (universal tools) at construction time and seeds
it into `AgentOptions.Tools` in `Create()`.

### Backward-compatible config cleanup

- `GatewayService.ResolveTools`: `case "memory":` and `case "cost":` stay as
  silent no-op branches with a comment marking them deprecated. Their sole
  job is to suppress the "unknown tool" warning that the `default:` branch
  would otherwise emit on legacy AGENT.md files.
- `GatewayService.AllTools`: filter out `MemoryTool` and `CostTool` by name
  so they no longer appear in the `tools.list` RPC response. This is what
  hides them from the iOS picker.

### Implicit migration

The picker change handles cleanup with no extra code: when a user saves an
agent through the iOS edit sheet, the new `Tools:` list is rebuilt from the
picker selections (which no longer include memory/cost), and the entries
fall off the file. Hand-edited AGENT.md files keep their legacy entries
indefinitely; they remain functionally inert.

### Documentation

CLAUDE.md edits:

1. Inter-agent chat / `ChatTool` section: replace *"The target runtime gets
   all its tools except chat (prevents cascade)"* with an accurate
   description — "The target runtime is built by `AgentRuntimeFactory` and
   gets only the universal tools (memory + cost); no cascading, no
   side-effect tools."
2. New "Universal Tools (always available)" subsection under the existing
   "Tool System" section. Move the `MemoryTool` and `CostTool` entries out
   of the per-tool list and into this subsection. State explicitly that
   they are not opt-in and that the `memory` / `cost` entries in an
   AGENT.md `Tools:` list are accepted but ignored.
3. Remove the "requires `memory` in agent's tools list" and "requires
   `cost` in agent's tools list" phrases wherever they appear.

`README.md`: no change (user-visible config format is unchanged).

`docs/configuration.md`: verify; add a one-line deprecation note if it
mentions `memory` or `cost` in the tools list.

## Testing

xUnit, in `tests/Achates.Tests`:

- **`UniversalToolsTests`**
  - `Build` returns a one-element list containing `MemoryTool` when the
    cost-ledger registry is empty.
  - `Build` returns memory + cost when the registry has at least one entry.
  - The `MemoryTool` it returns is configured with the supplied
    `sharedMemoryPath` and `agentDef.MemoryPath`.
  - The `CostTool` it returns is configured with the supplied `agentName`
    and ledger registry.

- **`AgentRuntimeFactoryTests`**
  - `Create()` returns a runtime whose `AgentOptions.Tools` contains a
    `MemoryTool`. This is the regression test for the original gap that
    prompted this change — a chat target now has memory.
  - When the factory is constructed with a non-empty universal tools list
    that includes a `CostTool`, the runtime exposes it too.

- **`AgentLoaderTests`** (or existing equivalent)
  - Loading an AGENT.md with `memory` listed in `Tools:` succeeds without
    emitting an "unknown tool" warning, and the resulting `AgentDefinition`
    builds a working runtime.
  - Same for `cost`.

Existing tests should pass unchanged. No behavioral change for user
sessions, cron jobs, or dreamtime — only consolidation.

## Risks and trade-offs

- **Silent no-op cases linger in `ResolveTools`.** Anyone reading the
  switch may wonder why `memory` and `cost` are present but empty. The
  comment marking them deprecated mitigates this, and we can delete them
  in a follow-up once configs have drifted clean.
- **Chat-target memory introduces a (small) new way for B to mutate state
  during a consult.** Memory writes are by design persistent across
  sessions; a malicious or confused B prompt could pollute B's memory file
  during an A→B consult. This is the same exposure B already has during
  its own user sessions, so the surface area isn't new — just newly
  reachable through a different entry point. Acceptable.
- **Cost-tool surface area in chat-target.** B can now read its own ledger
  (and the cross-agent registry) during a consult. This is read-only and
  matches the access it has in normal sessions. No new risk.
