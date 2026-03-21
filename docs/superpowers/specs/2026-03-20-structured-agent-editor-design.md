# Structured Agent Editor

## Context

The app currently has a raw markdown editor for AGENT.md files. This replaces it with a structured, iOS Settings-style form with sub-screens for model picking, tool toggles, and prompt editing. Includes a model browser that queries the provider for available models.

## Data Flow

### Server RPC Changes

**`agent.get`** — Returns structured JSON instead of raw content:
```json
{
  "description": "Personal assistant",
  "model": "anthropic/claude-sonnet-4",
  "tools": ["session", "memory", "todo", "mail"],
  "reasoning_effort": "medium",
  "temperature": null,
  "max_tokens": null,
  "allowed_chats": ["val", "claire"],
  "prompt": "You are a personal assistant...",
  "agent_models": ["anthropic/claude-opus-4", "google/gemini-2.5-flash"]
}
```
- `agent_models`: model IDs used by other agents (for "Used by agents" favorites in picker)
- `null` for temperature/max_tokens means "use default"

**`agent.update`** — Accepts structured JSON (same shape minus `agent_models`). Server serializes to AGENT.md via new `AgentLoader.Serialize()` method, writes to disk, triggers hot-reload via existing `AgentReloadFunc`.

**`models.list`** (new) — Returns all models from the provider:
```json
{
  "models": [
    { "id": "anthropic/claude-sonnet-4", "name": "Claude Sonnet 4", "context_window": 200000 },
    ...
  ]
}
```
Model list cached server-side after first fetch. Provider prefix before `/` used for grouping.

### AgentLoader.Serialize()

New static method — inverse of `Parse()`. Takes structured fields, produces deterministic AGENT.md:

```markdown
# {Name}

{Description}

## Capabilities

**Model:** {model}

**Tools:**
  - {tool1}
  - {tool2}

**Reasoning Effort:** {reasoning_effort}

**Temperature:** {temperature}

**Max Tokens:** {max_tokens}

**Allowed Chats:**
  - {chat1}

## Prompt

{prompt}
```

Omits fields that are null/default (no Temperature line if not set, etc.).

## iOS Views

### AgentEditView (main screen)

Form with inset grouped style, presented as a sheet from ChatView.

**Identity section:**
- Description — inline text field
- System Prompt — navigation row → PromptEditView

**Model section:**
- Model — navigation row showing current model name → ModelPickerView
- Reasoning Effort — segmented picker (low / medium / high)
- Temperature — optional text field (placeholder "Default")
- Max Tokens — optional text field (placeholder "Default")

**Tools section:**
- Navigation row showing "N enabled" → ToolsEditView

**Advanced section** (only if agent has chat tool):
- Allowed Chats — navigation row → AllowedChatsEditView

Toolbar: Cancel (left), Save (right, disabled until changes made).

### ModelPickerView (sub-screen)

- Search bar at top (filters all sections)
- **Current** section — selected model with checkmark
- **Used by Agents** section — models from `agent_models`, minus current
- **Browse All Models** row → pushes ModelBrowseView
- Tapping any model selects it and pops back

### ModelBrowseView (drill-down from ModelPickerView)

- Search bar at top (filters across all providers)
- Sections grouped by provider prefix (text before `/` in model ID, capitalized)
- Each row: model name (primary), model ID + context window (secondary)
- Tapping selects and pops back two levels
- Models fetched via `models.list` RPC, loaded on appear

### ToolsEditView (sub-screen)

- List of all known tool names with Toggle switches
- Enabled tools match the agent's current tool list
- Tools sorted alphabetically
- Known tools: session, memory, todo, cost, cron, chat, mail, notes, calendar, web_search, web_fetch, imessage, health, transcribe, location, camera

### PromptEditView (sub-screen)

- Full-screen TextEditor, monospaced font
- Navigation title "System Prompt"

### AllowedChatsEditView (sub-screen)

- List of all other agent names with Toggle switches
- Empty means all agents allowed (shown as footer text)
- Agent names from `agents.list` already in AppState

## iOS Models

### AgentEditModel

Swift struct for the editor state, parsed from `agent.get` response:

```swift
struct AgentEditModel {
    var description: String
    var model: String
    var tools: [String]
    var reasoningEffort: String?   // "low", "medium", "high", or nil
    var temperature: Double?
    var maxTokens: Int?
    var allowedChats: [String]
    var prompt: String
    var agentModels: [String]      // read-only, from other agents
}
```

### ModelInfo

```swift
struct ModelInfo: Identifiable {
    let id: String      // e.g. "anthropic/claude-sonnet-4"
    let name: String    // e.g. "Claude Sonnet 4"
    let contextWindow: Int

    var provider: String {
        id.components(separatedBy: "/").first ?? id
    }
}
```

## What Changes From Current Implementation

### Replaced
- `AgentEditView` (raw markdown TextEditor) → structured form with sub-screens
- `AppState.loadAgentContent` / `saveAgentContent` → work with `AgentEditModel` instead of raw String
- `AgentEditError` enum → updated for structured responses

### Modified
- `MobileTransport.HandleAgentGetAsync` — return structured JSON instead of raw file content
- `MobileTransport.HandleAgentUpdateAsync` — accept structured JSON, serialize to markdown before writing
- `MobileTransport.DispatchRequestAsync` — add `models.list` case

### Added (server)
- `AgentLoader.Serialize()` — structured fields → AGENT.md markdown
- `HandleModelsListAsync` — new RPC handler, queries provider for model list
- Model list caching in MobileTransport or GatewayService

### Added (iOS)
- `AgentEditModel` struct
- `ModelInfo` struct
- `ModelPickerView`
- `ModelBrowseView`
- `ToolsEditView`
- `PromptEditView`
- `AllowedChatsEditView`

### Unchanged
- Hot-reload infrastructure (ResolveAgentAsync, ReloadAgentAsync, UpdateAgent, AgentReloadFunc)
- ChatView sheet presentation wiring
- WebSocketClient RPC infrastructure

## Verification

1. `dotnet build Achates.slnx` — server compiles
2. Build iOS/macOS app in Xcode
3. Open chat → menu → Edit Agent → verify form loads with correct values
4. Change model via picker → Save → verify AGENT.md updated on disk
5. Toggle tools → Save → verify tools list updated
6. Edit prompt → Save → verify prompt updated
7. Send a message after save → verify agent uses updated definition
8. Test model browser search filtering
9. Test "Used by agents" shows models from other agents
