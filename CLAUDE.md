# Achates

AI agent framework with pluggable providers and tools. .NET 10 preview.

> **Keep this file up to date.** When you add, remove, or rename projects, change architectural patterns, or modify conventions, update the relevant sections of this file before finishing the task. Also update `README.md` when changes affect configuration format, tool setup instructions, or user-facing behavior. Update `docs/configuration.md` when changing the config system (adding/removing/renaming fields, env vars, data paths).

## Build & Test

```bash
dotnet build Achates.slnx
dotnet test Achates.slnx
```

Solution file is `Achates.slnx` (XML format, not legacy `.sln`).

## Running

Server requires at least one agent defined as an `AGENT.md` file. API key can be set in config (`api_key`) or via environment variable:
```bash
dotnet run --project src/Achates.Server
```
Config lives at `~/.achates/config.yaml`. Agents live at `~/.achates/agents/{name}/AGENT.md`.

## Architecture

### Core Concepts

- **Agent** — Named entity with identity (name, description), prompt, tools, and persistent memory. Defined in `~/.achates/agents/{name}/AGENT.md` (YAML frontmatter + markdown prompt), resolved at startup into `AgentDefinition`. Each agent may declare its own base model and thinking model via `**Model:**` and `**Thinking Model:**` capabilities; if absent they fall back to `models.base` / `models.thinking` in `config.yaml`. Memory has four tiers — shared (cross-agent, fetched on demand), core (preloaded at the head of every session), working (a small rolling scratchpad, preloaded on every turn), and archive (topical files retrieved on demand) — see `docs/configuration.md` for the config format and `src/Achates.Server/CLAUDE.md` for the mechanics.

### Where the rest lives

Per-project detail loads automatically when you work in that directory:

- `src/Achates.Providers/CLAUDE.md` — provider layer, content types, prompt caching
- `src/Achates.Agent/CLAUDE.md` — agent runtime, session store, compaction
- `src/Achates.Server/CLAUDE.md` — tool system, universal tools, server, transport

Config file format, `AGENT.md` capabilities keys, environment variables, and data paths are documented in `docs/configuration.md` — keep that file current instead of duplicating it here.

## Conventions

- Nullable reference types enabled, implicit usings enabled
- `sealed` on concrete classes by default
- Collection expressions (`[]`) preferred over `new List<T>()`
- Raw string literals for multi-line JSON/text
- xUnit test project at `tests/Achates.Tests` (run via `dotnet test Achates.slnx`)

## Data root

`ConfigLoader.DataDir` resolves `ACHATES_HOME` (default `~/.achates`). All configuration and persistent agent/shared data must use this root instead of constructing a home-directory path. `ACHATES_CONFIG_PATH` overrides only the YAML file location. Paths shown above use the default root.

## Apple client UI

The shared SwiftUI client is in `apple/Achates`, with the `Achates` scheme and `AchatesTests` target in `apple/Achates.xcodeproj`. Use `xcodebuild test -project apple/Achates.xcodeproj -scheme Achates -destination 'platform=macOS' -only-testing:AchatesTests` or select an installed iOS simulator destination. Test hosts skip the live root view; appearance smoke tests supply isolated in-memory fixtures and retain rendered screenshots as test attachments.

`AppState` owns navigation, explicit load/error state, and in-memory `ConversationDraft` objects keyed by server URL, agent ID, and session ID. Do not move drafts back into view-local state. `canSubmitMessage` is the shared gate for keyboard/button/voice sends and retries. Request identities prevent stale conversation/history loads from replacing the current selection. `ChatSessionState` owns each conversation’s live transcript, streaming status, and failed-send state, keyed by server, agent, and session. WebSocket reply events update their owning conversation even when it is offscreen; audio playback remains scoped to the visible conversation. Timeout notices and continuation availability belong to `ChatSessionState` and are restored from session history. **Continue** uses `chat.continue` to resume saved work; resubmit still rewinds. A server history snapshot reporting `is_running: false` reconciles a missed `done` after reconnect, guarded against a newer local send.

Use `EditorDismissal` for transactional editors, `EditorCommands`/focused values for Mac menu actions, and `InterfaceStyle`/`ConversationMarkdown` for shared semantic surfaces, metrics, and message formatting. Native containers adapt to the platform: compact iOS stacks, regular-width iPad/Mac split views, Mac Settings and Manage scenes. Attachment previews use Quick Look through `AttachmentPreview`. Keep nested editor changes provisional until the parent saves. Drafts currently survive navigation, not process restarts.

Excel `.xlsx` attachments use the server's per-session `WorkbookTool` for previews and range reads. Original bytes remain in client history and the session's workbook archive; the model receives preview text, never raw XLSX file content. Shared document types, labels, and symbols live in `DraftAttachment`.

After a background-to-active transition, replace the WebSocket transport even if its cached status is connected, then reload the selected conversation independently of session-list pagination. Preserve `AppState` selection, transcript, and drafts during this transport reset. Transient inactive-to-active transitions only resync. Connection generations fence delayed reconnects; conversation turn revisions let history reconcile a missed reply even if `done` arrives during the fetch, without overwriting a newer local turn. `ForegroundRecoveryTests` exercises these paths with a suspended transport fixture.

On Mac, only the agent sidebar uses SwiftUI `.searchable` in the main window. AppKit forbids duplicate `com.apple.SwiftUI.search` toolbar identifiers; additional split-column filtering must use an inline `ColumnSearchField`. `AchatesUITests/AgentNavigationTests` launches a DEBUG-only, network-free fixture in the real app scene and tests agent selection and independent search fields. Run it with `-only-testing:AchatesUITests`; set `ACHATES_APP_BUNDLE_IDENTIFIER=AchatesSoftware.Achates.UIRegression` to keep the UI test app separate from a running development app (the normal bundle identifier is unchanged).

In the regular-width iPad split layout, present Settings in a sheet with a Done button; a sidebar navigation link can replace a column without providing a back action. Compact iOS keeps its normal Settings push. `AchatesUITests/SettingsNavigationTests` uses the same isolated fixture on iOS to verify closing and reopening Settings in both orientations; run it on an iPad and an iPhone simulator.
