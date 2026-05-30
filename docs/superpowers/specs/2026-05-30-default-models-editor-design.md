# Design: Edit default models from the app

**Date:** 2026-05-30
**Status:** Approved

## Problem

The apps can change an individual agent's `**Model:**` / `**Thinking Model:**`
override, but there is no way to set the *global* defaults (`models.base` /
`models.thinking` in `~/.achates/config.yaml`). Today those can only be changed
by hand-editing `config.yaml` and restarting the server. Agents that don't
declare their own override fall back to these globals, so being unable to edit
them from the app is a real gap.

This adds a small editor — Settings → System → **Default Models** — that reads
and writes both global defaults and applies the change live (no server
restart).

## Scope

In scope: editing `models.base` and `models.thinking` from the Apple app.

Out of scope: a broader server-config editor (provider, API keys, tool config,
etc.). Only the two default-model fields are exposed.

## Server side (`Achates.Server`)

### New RPC pair

Added to the dispatch switch in `MobileTransport.DispatchRequestAsync`:

- **`config.get_models`** — no params. Returns the current globals straight from
  the in-memory `AchatesConfig`:
  ```json
  { "base": "anthropic/claude-sonnet-4.6", "thinking": "anthropic/claude-opus-4.7" }
  ```
  Either field may be `null` when no global default is configured.

- **`config.set_models`** — params `{ "base"?, "thinking"? }`. For each field:
  - A non-empty string sets that default.
  - An empty string or JSON `null` clears it (no global default — agents then
    rely on their own `**Model:**`).
  - A **missing** property leaves that field unchanged. This lets the client
    update one field without disturbing the other.

  Returns `{ "ok": true }`.

### Wiring (`GatewayService`)

`MobileTransport` already delegates side-effecting operations to `GatewayService`
via function properties (`AgentReloadFunc`, `ModelsListFunc`,
`GenerateAvatarFunc`, …). Follow that pattern:

- Add a transport property:
  ```csharp
  public Func<string?, string?, bool, bool, CancellationToken, Task>? SetDefaultModelsFunc { get; set; }
  ```
  Signature carries the new base value, the new thinking value, and two
  "was this field present in the request" booleans (so the handler can
  distinguish "clear to null" from "leave unchanged"). The handler parses the
  request JSON into these and invokes the func.

  (Implementation note: a small record — e.g. `DefaultModelsUpdate(string? Base,
  bool BaseSet, string? Thinking, bool ThinkingSet)` — may be cleaner than four
  positional args. Either is acceptable; the handler owns JSON parsing, the func
  owns the apply logic.)

- `GatewayService.SetDefaultModelsAsync(...)` wired into that property at startup
  (next to the existing `_mobileTransport.DefaultModelId = config.Models?.Base;`
  assignments) does, in order:
  1. Ensure `config.Models ??= new ModelsConfig();` then mutate `Base` /
     `Thinking` for each field that was present in the request.
  2. Persist via `ConfigLoader.Save(config)`.
  3. Update `_mobileTransport.DefaultModelId` / `DefaultThinkingModelId` so the
     agent-edit sheet's "Default (…)" fallback labels stay correct.
  4. Reload affected agents so the change takes effect without a restart.

### Live apply — which agents reload

The global default is resolved into each `AgentDefinition.Model` /
`.ThinkingModel` at load time (`ResolveAgentAsync`), so a runtime change only
takes effect after a reload. To apply live:

- For a changed **base** default: reload every agent whose `AgentConfig.Model`
  is null/empty (i.e. it relies on the global). Agents with their own
  `**Model:**` are unaffected — skip them to avoid needless churn (re-resolving
  Graph clients, dreamtime reconciliation, etc.).
- For a changed **thinking** default: reload every agent whose
  `AgentConfig.ThinkingModel` is null/empty **and** that has the `think` tool
  (only those resolve a thinking model).
- Base and thinking are evaluated independently; an agent that qualifies under
  either is reloaded once (dedupe the set before reloading).
- Reuse the existing `ReloadAgentAsync(name, ct)` per agent — the same call
  `agent.update` already makes for a single agent.

To decide which agents qualify, the handler needs each agent's current
`AgentConfig` (to read its `Model` / `ThinkingModel` overrides). `GatewayService`
can re-parse each `AGENT.md` (as `ReloadAgentAsync` already does) or consult the
loaded definitions; re-parsing is simplest and matches existing code.

If no agents qualify (e.g. every agent overrides), the config is still persisted
and the transport's `DefaultModelId` updated — only the reload loop is empty.

### Error handling

- `config.set_models` with neither `base` nor `thinking` present: no-op success
  (`{ ok: true }`), nothing persisted.
- A reload failure for one agent is logged and does not abort the others; the
  config has already been persisted. (Matches `agent.update`, which logs reload
  failures as a warning and still reports success.) The RPC still returns
  `{ ok: true }`.

## App side (`apple/`)

### New view: `DefaultModelsView.swift`

A `Form` with two rows, mirroring the Model / Thinking Model rows in
`AgentEditView`:

- **Default Model** → `NavigationLink` to the existing `ModelBrowseView`, bound
  to the base value.
- **Default Thinking Model** → `NavigationLink` to `ModelBrowseView`, bound to
  the thinking value.

`ModelBrowseView` is reused as-is. It treats "selection matches `defaultModel`"
as "set the binding to nil." On this screen there is **no** higher-level default
to fall back to — these *are* the defaults — so pass `defaultModel: nil`. A nil
selection therefore means "no server-wide default," which is a valid state
(agents then need their own `**Model:**`). Render nil as a plain **"None"** row
rather than "Default (…)".

State + save:

- Loads current values on appear via `config.get_models`.
- A **"Save"** toolbar button, enabled only when the values differ from what was
  loaded — matching `MemoryEditView`'s save pattern. On save, calls
  `config.set_models` with both fields and shows confirmation / resets the
  dirty baseline as `MemoryEditView` does.

### AppState methods

Next to the existing `loadAvailableModels()`:

- `loadDefaultModels() async throws -> (base: String?, thinking: String?)` —
  calls `config.get_models`.
- `saveDefaultModels(base: String?, thinking: String?) async throws` — calls
  `config.set_models`. Sends both fields (empty string for a cleared value) so
  the server applies the full intended state.

### Entry point

A third `NavigationLink` in `SettingsView`'s existing **System** section (which
only renders when `connectionStatus == .connected`), alongside Memory and
Scheduled Jobs:

```swift
NavigationLink {
    DefaultModelsView()
} label: {
    Label("Default Models", systemImage: "cpu")
}
```

## Docs

- `CLAUDE.md`: add `config.get_models` / `config.set_models` to the RPC methods
  list; note the new Settings → System → Default Models entry in the
  `MobileTransport` / SettingsView description.
- `docs/configuration.md`: if it documents `models.base` / `models.thinking`,
  note they're now editable from the app (no restart required).

## Testing

- Server unit test(s) for `config.set_models`: setting base only leaves thinking
  untouched; clearing (empty string) nulls the field; the persisted YAML
  round-trips through `ConfigLoader`.
- Selective-reload behavior: an agent with its own `**Model:**` is not reloaded
  when only the base default changes; an agent relying on the global is.
  (May be covered as a focused test on the qualify-which-agents helper if that
  logic is factored out, rather than a full integration test.)
- Manual: change defaults in the app, confirm a non-overriding agent's next turn
  uses the new model and `config.yaml` reflects the change.
```
