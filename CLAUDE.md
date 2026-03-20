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

## Project Structure

```
src/
  Achates.Providers/     LLM provider abstraction + OpenRouter implementation
  Achates.Agent/         Stateful agent runtime engine (messages, tools, event streaming)
  Achates.Server/        ASP.NET Core gateway service, config, transports, tools, system prompt
```

### Dependency flow

```
Providers <- Agent <- Server
```

## Architecture

### Core Concepts

- **Agent** — Named entity with identity (name, description), prompt, model, tools, and persistent memory. Defined in `~/.achates/agents/{name}/AGENT.md` (YAML frontmatter + markdown prompt), resolved at startup into `AgentDefinition`.

### Provider Layer (`Achates.Providers`)
- `IModelProvider` — interface with `GetModelsAsync()` and `GetCompletions()` (streaming)
- `ModelProviders.Create(id)` — factory for provider instances
- Only implementation: `OpenRouterProvider` (SSE streaming, `api_key` in config or `OPENROUTER_API_KEY` env var)
- Content types: `CompletionContent` base, subtypes for text, image, audio, thinking, tool calls, files
- `CompletionUserContent` — input-only base. `CompletionAudioContent` is output-only (extends `CompletionContent`), `CompletionAudioInputContent` is input-only (extends `CompletionUserContent`). This asymmetry is intentional.
- Event streaming via `CompletionEventStream` using `System.Threading.Channels`

### Agent Runtime (`Achates.Agent`)
- `AgentRuntime` — one instance = one conversation thread. Stateful with message history.
- `PromptAsync()` returns `AgentEventStream`; `ContinueAsync()` resumes after tool results
- `Steer()` interrupts current tool execution; `FollowUp()` queues for after current turn
- `AgentOptions` — model, system prompt, tools, completion options, metadata, context transform hooks
- `ISessionStore` — interface for persisting conversation history by session key (Load/Save/Delete)
- `SessionCompactor` — proactive compaction before each turn. Estimates tokens (uses provider's reported input count + char heuristic), summarizes oldest messages via LLM when over 80% of context window, falls back to truncation on failure. Preserves tool call/result pairs. `SummaryMessage` type holds the summary.

### Tool System (`AgentTool` subclasses)
- `AgentTool` is the preferred pattern (class-based). Subclass and implement `Name`, `Description`, `Parameters` (JSON Schema as `JsonElement`), `ExecuteAsync()`.
- Returns `AgentToolResult` with `Content` (list of `CompletionContent`) and optional `Details` (for UI display).
- Tools live in `src/Achates.Server/Tools/`. Current tools: `SessionTool`, `MemoryTool`, `TodoTool`, `MailTool`, `CalendarTool`, `WebSearchTool`, `WebFetchTool`, `CostTool`, `CronTool`, `IMessageTool`, `HealthTool`, `ChatTool`, `TranscribeTool`, `LocationTool`, `CameraTool`.
- Tools can be shared (same instance for all sessions) or per-session. `MobileTransport.CreateRuntime` builds per-session tool lists (e.g. `MemoryTool` uses per-agent memory path).
- Tool schema pattern: use `JsonSchemaHelpers` (`ObjectSchema`, `StringSchema`, `NumberSchema`, `BooleanSchema`, `StringEnum`) via `using static Achates.Providers.Util.JsonSchemaHelpers`.

### Server (`Achates.Server`)
- `AgentLoader` — discovers agents by scanning `~/.achates/agents/*/AGENT.md`. Parses pure markdown: H1 title, description text, `## Capabilities` (`**Key:** value` lines with optional sub-bullet lists → `AgentConfig` fields), `## Prompt` (system prompt). Creates a default agent if none found.
- `AgentDefinition` — resolved agent with Model, SystemPrompt, Tools, CompletionOptions, MemoryPath, CostLedger, CronStore, GraphClient.
- `MemoryTool` — layered persistent memory with two scopes. **Shared memory** at `~/.achates/memory.md` stores universal user facts (name, family, preferences) accessible to all agents. **Agent memory** at `~/.achates/agents/{agentName}/memory.md` stores agent-specific notes. `scope` parameter (`shared` or `agent`) controls which file to target; `read` without a scope returns both. Survives `/new` resets.
- `MailTool` — reads Outlook email via Microsoft Graph API. Actions: list, read, search. Accepts multiple graph accounts; `account` parameter appears when >1 configured.
- `CalendarTool` — reads Outlook calendar via Microsoft Graph API. Actions: upcoming, read, availability. Accepts multiple graph accounts; `account` parameter appears when >1 configured.
- `WebSearchTool` — searches the web via Brave Search API. Parameters: query, count. Returns numbered results with title, URL, description. Singleton; requires `brave_api_key` in config or `BRAVE_API_KEY` env var.
- `WebFetchTool` — fetches a URL and extracts readable content using SmartReader (Readability). Parameters: url, max_chars. Handles HTML, JSON, plain text. Singleton; no config required.
- `CostTool` — queries the persistent cost ledger. Actions: summary (totals for a period), recent (last N entries), breakdown (grouped by day or model). Per-session; requires `cost` in agent's tools list. Ledger is always recorded regardless of tool config.
- `CronTool` — manages scheduled tasks. Actions: list, add, update, remove, run. Per-session; requires `cron` in agent's tools list. Schedule types: `at` (one-shot timestamp), `every` (interval in minutes), `cron` (cron expression with optional timezone). Jobs run in isolation (fresh AgentRuntime) and deliver results to the active mobile connection as `cron.result` events. Delivery target defaults to the current session's peer.
- `IMessageTool` — reads local macOS iMessage database (`~/Library/Messages/chat.db`) via SQLite (read-only). Actions: chats (list recent conversations), read (messages from a chat by ID), search (full-text search). Surfaces voice messages with their audio file paths (joins `attachment` table, filters by audio mime types). Resolves phone numbers and emails to contact names via `ContactResolver` (fetches from Microsoft Graph API contacts, cached 30 minutes; requires graph config). Singleton; requires Full Disk Access for the host process.
- `TranscribeTool` — transcribes audio files to text using an audio-capable model. Parameters: file (absolute path). Reads the file, base64 encodes it, sends to the configured transcription model via `CompletionAudioInputContent`. Singleton; requires `transcribe` in agent's tools list. Model configured via `tools.transcribe.model` (default: `google/gemini-2.5-flash`). Useful for transcribing iMessage voice messages surfaced by `IMessageTool`.
- `ChatTool` — inter-agent communication. Actions: agents (list available agents with descriptions and tools), chat (start a ping-pong conversation with another agent). Per-session; requires `chat` in agent's tools list. Creates isolated `AgentRuntime` instances for both agents (target gets all its tools except chat to prevent cascade). Supports up to 5 back-and-forth turns; either agent can end early with `<<DONE>>`. `allow_chat` in agent config restricts which agents can be contacted (null/empty = all). Costs recorded to each agent's ledger with channel `chat`. `AgentInfo` registry built at startup provides agent discovery metadata.
- `LocationTool` — gets the user's current GPS location via the mobile device. Requires `DeviceCommandBridge` and an active mobile connection with `location` capability. Invokes `device.location` with 15s timeout. Singleton; requires `location` in agent's tools list.
- `CameraTool` — captures a photo from the user's mobile device camera. Parameters: facing (back/front). Returns `CompletionImageContent` with base64 JPEG. Requires `DeviceCommandBridge` and an active mobile connection with `camera` capability. Singleton; requires `camera` in agent's tools list.
- `HealthTool` — queries health data from Withings API. Actions: weight (body composition), blood_pressure, sleep, activity, authorize. Singleton; requires `withings` config with client_id and client_secret. OAuth 2.0 authorization code flow with browser redirect to `/withings/callback`.
- `WithingsClient` (`src/Achates.Server/Withings/`) — Withings Health API client. OAuth 2.0 authorization code flow: user visits auth URL, Withings redirects to `/withings/callback`, tokens persisted at `~/.achates/withings-tokens.json`. Access tokens auto-refresh. All API calls are POST with form-encoded params; responses are `{ "status": 0, "body": { ... } }`.
- `GraphClient` (`src/Achates.Server/Graph/`) — Microsoft Graph API client supporting two auth flows. Multiple named accounts per agent. Created per-account during startup. Eagerly authenticates so device code prompts appear at startup. `AsyncLocal` notifier routes device code messages through the transport to the user's chat. Flow is selected by presence of `client_secret`:
  - **Client credentials** (work/school): `client_secret` set → application permissions, `/users/{email}/` paths. Requires `tenant_id`, `user_email`.
  - **Device code** (personal or work/school): no `client_secret` → delegated permissions, `/me/` paths. `tenant_id` defaults to `consumers`. Token cache persisted at `~/.achates/graph-token-cache.bin`.
- `CronService` (`src/Achates.Server/Cron/`) — background timer loop for scheduled task execution. Not DI-registered; created by `GatewayService` after agents are resolved. Timer sleeps until next due job (max 60s), executes due jobs sequentially, delivers results via `MobileTransport`. `CronStore` persists jobs per-agent as JSON. `CronScheduler` computes next run times using Cronos library for cron expressions.
- `GatewayService` — ASP.NET Core `IHostedLifecycleService`. Resolves agents from config at startup, creates `MobileTransport` and `CronService`.
- WebSocket endpoint: `/ws` (query params: `peer`)
- Health check: `GET /health`
- Admin console: Blazor Interactive Server UI at `/admin`. Pages: Dashboard, Sessions, Memory, Costs, Config.
- `AdminService` — singleton data access layer for admin pages. Reads sessions, memory, costs, config from disk.
- Blazor files live in `Components/` (App, Routes, Layout, Pages). Static assets in `wwwroot/css/` (Bootstrap 5, app.css).

### Transport (`Achates.Server.Mobile`)
- `MobileTransport` — WebSocket handler for `/ws` connections. Supports multiple concurrent clients. All clients share the same session namespace (sessions are per-agent, not per-client). Events are broadcast to all connected clients. Manages RPC dispatch, agent event streaming, and session persistence.
- `MobileConnection` — per-connection state: RPC correlation, event sequencing, agent runtimes, `Capabilities` set (populated from `connect` params).
- `MobileSessionStore` — session persistence per agent under `~/.achates/agents/{agentName}/sessions/{sessionId}.json`. Provides timeline operations: `LoadTimelineAsync` (paginated chronological loading), `GetLatestSessionAsync`, `MergeSessionsAsync` (combine adjacent sessions), `SplitSessionAsync` (split at message boundary).
- `MobileSession` — session model with Id, Title, Created, Updated, Messages.
- `DeviceCommandBridge` — routes tool requests (location, camera) to any connected client with the required capability. Used by `LocationTool` and `CameraTool`.
- Frame protocol: `RequestFrame` (req), `ResponseFrame` (res), `EventFrame` (evt). JSON with snake_case naming.
- RPC methods: `connect`, `ping`, `agents.list`, `timeline.load`, `timeline.break.add`, `timeline.break.remove`, `timeline.clear`, `chat.send`, `chat.cancel`, `chat.read`.
- Timeline model: sessions are presented as a continuous timeline per agent (like iMessage). Session breaks appear as date/time dividers. Server auto-creates a new session after 4h of inactivity. Users can manually add breaks (split) or remove them (merge). `chat.send` no longer requires `session_id` — server auto-resolves to the latest session.
- Device commands (server-to-client requests): `device.location`, `device.camera`.
- Per-session tool injection: `CreateRuntime` adds MemoryTool, TodoTool, CostTool, CronTool per-session.

## Conventions

- File-scoped namespaces everywhere
- Primary constructors on services (e.g. `GatewayService(AchatesConfig config, ...)`)
- Nullable reference types enabled, implicit usings enabled
- `sealed` on concrete classes by default
- Collection expressions (`[]`) preferred over `new List<T>()`
- Raw string literals for multi-line JSON/text
- No test framework established yet

## Configuration

### Global config (`~/.achates/config.yaml`)

```yaml
provider:
  name: openrouter
  api_key: sk-...  # or set OPENROUTER_API_KEY env var

tools:
  todo:
    file: ~/path/to/todo.md
  web_search:
    brave_api_key: BSA...  # or set BRAVE_API_KEY env var
  graph:
    # Each entry is a named account. Multiple accounts supported.
    personal:
      client_id: <app-client-id>       # device code flow (no client_secret)
      # tenant_id defaults to "consumers" for personal accounts
    work:
      tenant_id: <azure-tenant-id>
      client_id: <app-client-id>
      client_secret: <secret>          # presence triggers client credentials flow; or set GRAPH_CLIENT_SECRET env var
      user_email: user@example.com     # required for client credentials
  transcribe:
    model: google/gemini-2.5-flash  # audio-capable model for transcription
  withings:
    client_id: <withings-client-id>
    client_secret: <withings-client-secret>  # or set WITHINGS_CLIENT_SECRET env var
    redirect_uri: http://localhost:5000/withings/callback  # optional, this is the default
```

Loaded by `ConfigLoader.Load()` (in Server project). Env var override: `ACHATES_CONFIG_PATH`. YAML uses underscore naming convention (C# PascalCase <-> YAML snake_case). `~` is expanded in file paths (e.g. `todo_file`). `${ENV_VAR}` expansion is **not yet supported** — use literal values or env vars directly.

### Agent definitions (`~/.achates/agents/{name}/AGENT.md`)

Each agent is a pure markdown file. Directory name = agent name. Discovered by `AgentLoader` at startup. Structure: H1 title, description paragraph(s), `## Capabilities` with bold-key bullet list, optional `## Prompt` section for the system prompt.

```markdown
# Paul

Personal assistant.

## Capabilities

**Model:** anthropic/claude-sonnet-4

**Tools:**
  - session
  - memory
  - todo
  - mail

**Allowed Chats:**
  - val
  - claire

**Reasoning Effort:** medium

## Prompt

You are a personal assistant...
```

Capabilities keys: `Model`, `Provider`, `Tools`, `Allowed Chats`, `Reasoning Effort`, `Temperature`, `Max Tokens`. List values use sub-bullets; scalar values go inline after the key.

If no agents are found, a default agent is scaffolded at `~/.achates/agents/default/AGENT.md`.

### Data paths

```
~/.achates/config.yaml                                         Configuration (provider + tools)
~/.achates/agents/{agentName}/AGENT.md                         Agent definition (markdown)
~/.achates/agents/{agentName}/sessions/{sessionId}.json        Conversation history
~/.achates/memory.md                                           Shared memory (universal user facts, all agents)
~/.achates/agents/{agentName}/memory.md                        Agent memory (agent-specific notes)
~/.achates/agents/{agentName}/costs.jsonl                      Cost ledger (append-only, always recorded)
~/.achates/agents/{agentName}/cron.json                        Scheduled task definitions and state
~/.achates/agents/{agentName}/read-state.json                  Read tracking (last read timestamp)
~/.achates/graph-token-cache.bin                               Graph device code token cache
~/.achates/withings-tokens.json                                Withings OAuth tokens (access + refresh)
```
