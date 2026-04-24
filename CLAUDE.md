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
- `IModelProvider` — interface with `GetModelsAsync()`, `GetCompletions()` (streaming), and `GenerateImageAsync()` (single image from prompt)
- `ModelProviders.Create(id)` — factory for provider instances
- Only implementation: `OpenRouterProvider` (SSE streaming, `api_key` in config or `OPENROUTER_API_KEY` env var)
- Content types: `CompletionContent` base, subtypes for text, image, audio, thinking, tool calls, files. `CompletionImageContent` has optional `Url` for lightweight references (empty `Data` + URL).
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
- Returns `AgentToolResult` with `Content` (list of `CompletionContent`), optional `ImageUrl` (relative URL for generated images), and optional `Details` (transient UI metadata, `[JsonIgnore]`'d from session persistence).
- Tools live in `src/Achates.Server/Tools/`. Current tools: `SessionTool`, `MemoryTool`, `NotebookTool`, `NotesTool`, `MailTool`, `CalendarTool`, `WebSearchTool`, `WebFetchTool`, `CostTool`, `CronTool`, `IMessageTool`, `HealthTool`, `ChatTool`, `TranscribeTool`, `LocationTool`, `CameraTool`, `ImageTool`, `ProfileTool`, `AgentCreatorTool`, `ThinkTool`, `SessionReviewTool`.
- Tools can be shared (same instance for all sessions) or per-session. `MobileTransport.CreateRuntime` builds per-session tool lists (e.g. `MemoryTool` uses per-agent memory path).
- Tool schema pattern: use `JsonSchemaHelpers` (`ObjectSchema`, `StringSchema`, `NumberSchema`, `BooleanSchema`, `ArraySchema`, `StringEnum`) via `using static Achates.Providers.Util.JsonSchemaHelpers`.

### Server (`Achates.Server`)
- `AgentLoader` — discovers agents by scanning `~/.achates/agents/*/AGENT.md`. Parses pure markdown: H1 title, description text, `## Capabilities` (`**Key:** value` lines with optional sub-bullet lists → `AgentConfig` fields), `## Prompt` (system prompt). Creates a default agent if none found. `NormalizeId(displayName)` derives a filesystem-safe agent ID from a display name (lowercase, spaces to hyphens, strip non-alphanumeric, collapse hyphens, max 64 chars).
- `AgentDefinition` — resolved agent with Model, ThinkingModel, SystemPrompt, Tools, CompletionOptions, MemoryPath, CostLedger, CronStore, GraphClient, AvatarData. Avatar is loaded from `avatar.jpg` (or `.png`) in the agent directory; sent as base64 in `agents.list` responses.
- `MemoryTool` — layered persistent memory with two scopes. **Shared memory** at `~/.achates/memory.md` stores universal user facts (name, family, preferences) accessible to all agents. **Agent memory** at `~/.achates/agents/{agentName}/memory.md` stores agent-specific notes. `scope` parameter (`shared` or `agent`) controls which file to target; `read` without a scope returns both. Survives session boundaries.
- `NotebookTool` — a user-configured folder of markdown files for long-term notes, todos, drafts, and ideas. Actions: `list`, `read`, `write` (replaces whole file), `mkdir`. `read`/`write` restricted to `.md`; every path resolved against the root and rejected if it escapes. Root comes from `tools.notebook.root` in config. Singleton; requires `notebook` in agent's tools list.
- `MailTool` — reads Outlook email via Microsoft Graph API. Actions: list, read, search, folders. `folders` lists mail folders (top-level or children of a parent folder ID), enabling folder navigation. `list` accepts folder by well-known name or ID. Accepts multiple graph accounts; `account` parameter appears when >1 configured.
- `CalendarTool` — reads Outlook calendar via Microsoft Graph API. Actions: upcoming, read, availability. Accepts multiple graph accounts; `account` parameter appears when >1 configured.
- `WebSearchTool` — searches the web via Brave Search API. Parameters: query, count. Returns numbered results with title, URL, description. Singleton; requires `brave_api_key` in config or `BRAVE_API_KEY` env var.
- `WebFetchTool` — fetches a URL and extracts readable content using SmartReader (Readability). Parameters: url, max_chars. Handles HTML, JSON, plain text. Singleton; no config required.
- `CostTool` — queries the persistent cost ledger. Actions: summary (totals for a period), recent (last N entries), breakdown (grouped by day or model). Per-session; requires `cost` in agent's tools list. Ledger is always recorded regardless of tool config.
- `CronTool` — manages scheduled tasks. Actions: list, add, update, remove, run. Per-session; requires `cron` in agent's tools list. Schedule types: `at` (one-shot timestamp), `every` (interval in minutes), `cron` (cron expression with optional timezone). Jobs run in isolation (fresh AgentRuntime) and deliver results to the active mobile connection as `cron.result` events. Delivery target defaults to the current session's peer.
- `NotesTool` — reads and writes Apple Notes on macOS via AppleScript (`osascript`). Actions: `folders` (list every folder across all accounts as `Account / Folder`), `list` (note titles in a named folder), `read` (fetch a note by exact title; HTML body converted to markdown), `create` (make a new note; markdown body converted to HTML via Markdig). No update/rename actions — `create` errors if a note with that title already exists. Folder is a per-call parameter; tool will also error if the same folder name exists in multiple accounts. Singleton; requires `notes` in agent's tools list and Notes automation permission (prompted on first use).
- `IMessageTool` — reads local macOS iMessage database (`~/Library/Messages/chat.db`) via SQLite (read-only). Actions: chats (list recent conversations), read (messages from a chat by ID), search (full-text search). Surfaces voice messages with their audio file paths (joins `attachment` table, filters by audio mime types). Resolves phone numbers and emails to contact names via `ContactResolver` (fetches from Microsoft Graph API contacts, cached 30 minutes; requires graph config). Singleton; requires Full Disk Access for the host process.
- `TranscribeTool` — transcribes audio files to text using an audio-capable model. Parameters: file (absolute path). Reads the file, base64 encodes it, sends to the configured transcription model via `CompletionAudioInputContent`. Singleton; requires `transcribe` in agent's tools list. Model configured via `tools.transcribe.model` (default: `google/gemini-2.5-flash`). Useful for transcribing iMessage voice messages surfaced by `IMessageTool`.
- `ThinkTool` — escalates to a thinking model for complex reasoning. Parameters: prompt (the problem to reason about). Sends the prompt to the agent's configured thinking model and returns the response. Singleton; requires `think` in agent's tools list. Model configured per-agent via `**Thinking Model:**` in AGENT.md capabilities.
- `SessionReviewTool` — read-only session browser for dreamtime. Actions: list (sessions since last dreamtime), read (full transcript). Injected only during dreamtime execution; not available in normal sessions.
- `ChatTool` — inter-agent communication. Actions: agents (list available agents with descriptions and tools), chat (start a ping-pong conversation with another agent). Per-session; requires `chat` in agent's tools list. Creates isolated `AgentRuntime` instances for both agents (target gets all its tools except chat to prevent cascade). Supports up to 5 back-and-forth turns; either agent can end early with `<<DONE>>`. `allow_chat` in agent config restricts which agents can be contacted (null/empty = all). Costs recorded to each agent's ledger with channel `chat`. `AgentInfo` registry built at startup provides agent discovery metadata.
- `LocationTool` — gets the user's current GPS location via the mobile device. Requires `DeviceCommandBridge` and an active mobile connection with `location` capability. Invokes `device.location` with 15s timeout. Singleton; requires `location` in agent's tools list.
- `CameraTool` — captures a photo from the user's mobile device camera. Parameters: facing (back/front). Returns `CompletionImageContent` with base64 JPEG. Requires `DeviceCommandBridge` and an active mobile connection with `camera` capability. Singleton; requires `camera` in agent's tools list.
- `ImageTool` — generates images using an image-capable model. Actions: `models` (list image-capable models with IDs, descriptions, costs), `generate` (create an image). Generate params: model (required), prompt (required), images (optional base64 references). Saves JPEG to `~/.achates/agents/{agentName}/images/{timestamp}-{id}.jpg`. Returns only text to the model (file path); image data is passed via `Details` (`ImageDetails` record) for live UI delivery and `ImageUrl` for session persistence. Images served via `GET /agents/{name}/images/{file}` endpoint. Per-agent; requires `image` in agent's tools list.
- `ProfileTool` — allows the agent to read and update its own profile. Actions: get (returns current description, prompt, and avatar), update (changes description, prompt, and/or avatar — only provide fields to change). Avatar is compressed to 512x512 JPEG. Writes changes to AGENT.md and triggers agent reload. Per-agent; requires `profile` in agent's tools list.
- `AgentCreatorTool` — creates new agents at runtime. Parameters: name (display name, required), description (required), prompt (required), model (optional), tools (optional list). Writes AGENT.md via `AgentLoader.Serialize`, creates agent directory, triggers reload so the agent is immediately live. Per-agent; requires `agent_creator` in agent's tools list.
- `HealthTool` — queries health data from Withings API. Actions: weight (body composition), blood_pressure, sleep, activity, authorize. Singleton; requires `withings` config with client_id and client_secret. OAuth 2.0 authorization code flow with browser redirect to `/withings/callback`.
- `WithingsClient` (`src/Achates.Server/Withings/`) — Withings Health API client. OAuth 2.0 authorization code flow: user visits auth URL, Withings redirects to `/withings/callback`, tokens persisted at `~/.achates/withings-tokens.json`. Access tokens auto-refresh. All API calls are POST with form-encoded params; responses are `{ "status": 0, "body": { ... } }`.
- `GraphClient` (`src/Achates.Server/Graph/`) — Microsoft Graph API client supporting two auth flows. Multiple named accounts per agent. Created per-account during startup. Eagerly authenticates so device code prompts appear at startup. `AsyncLocal` notifier routes device code messages through the transport to the user's chat. Flow is selected by presence of `client_secret`:
  - **Client credentials** (work/school): `client_secret` set → application permissions, `/users/{email}/` paths. Requires `tenant_id`, `user_email`.
  - **Device code** (personal or work/school): no `client_secret` → delegated permissions, `/me/` paths. `tenant_id` defaults to `consumers`. Token cache persisted at `~/.achates/graph-token-cache.bin`.
- `CronService` (`src/Achates.Server/Cron/`) — background timer loop for scheduled task execution. Not DI-registered; created by `GatewayService` after agents are resolved. Timer sleeps until next due job (max 60s), executes due jobs sequentially, delivers results via `MobileTransport`. `CronStore` persists jobs per-agent as JSON. `CronScheduler` computes next run times using Cronos library for cron expressions. Max turns safety valve (20 turns) prevents runaway job execution. `CronJobKind` distinguishes `User` (agent-managed) from `Dreamtime` (system-managed) jobs. Sessions saved by cron runs carry a `JobId` field tying them back to the originating job.
- `CronSessionReaper` (`src/Achates.Server/Cron/CronSessionReaper.cs`) — prunes old cron-origin sessions so recurring jobs don't bloat the session list. Invoked from the cron loop after each tick, self-throttled to once per 5 min per agent. Rule: for each `JobId`, keep the N most-recent sessions (default 1, via `cron.keep_last_per_job`); additionally drop anything older than `cron.max_age_days` (default 30). Only touches `User`-kind cron sessions — dreamtime sessions are exempt for auditability.
- **Dreamtime** — nightly memory consolidation. Enabled per-agent via `**Dreamtime:** 3:00 AM` in AGENT.md. System auto-creates a protected `CronJob` (Kind=Dreamtime) that agents cannot modify via CronTool. When executed, creates an isolated AgentRuntime with the agent's normal prompt + dreamtime instructions, equipped with `SessionReviewTool` (list/read sessions since last run) and `MemoryTool`. The agent triages recent sessions, reads interesting ones, and updates memory. Reconciled on agent load/reload. Saved as a normal session for auditability.
- `GatewayService` — ASP.NET Core `IHostedLifecycleService`. Resolves agents from config at startup, creates `MobileTransport` and `CronService`. Reconciles dreamtime jobs on agent load/reload.
- WebSocket endpoint: `/ws` (query params: `peer`)
- Health check: `GET /health`
- Agent images: `GET /agents/{name}/images/{file}` (serves generated images from disk)
- Memory and scheduled-jobs management live in the Apple app under Settings → System (see `memory.*` and `jobs.*` RPC methods below). Costs are exposed via the Costs sheet from the session list.

### Transport (`Achates.Server.Mobile`)
- `MobileTransport` — WebSocket handler for `/ws` connections. Supports multiple concurrent clients. All clients share the same session namespace (sessions are per-agent, not per-client). Events are broadcast to all connected clients. Manages RPC dispatch, agent event streaming, and session persistence.
- `MobileConnection` — per-connection state: RPC correlation, event sequencing, agent runtimes, `Capabilities` set (populated from `connect` params).
- `MobileSessionStore` — session persistence per agent under `~/.achates/agents/{agentName}/sessions/{sessionId}.json`. Operations: `ListAsync` (paginated, sorted by Updated desc, returns `(sessions, hasMore)` tuple), `LoadAsync`, `SaveAsync`, `DeleteAsync`, `CreateAsync` (new empty session), `DeleteAllAsync`, `UpdateMetadataAsync` (title updates).
- `MobileSession` — session model with Id, Title, Created, Updated, Messages.
- `DeviceCommandBridge` — routes tool requests (location, camera) to any connected client with the required capability. Used by `LocationTool` and `CameraTool`.
- Frame protocol: `RequestFrame` (req), `ResponseFrame` (res), `EventFrame` (evt). JSON with snake_case naming.
- RPC methods: `connect`, `ping`, `agents.list`, `sessions.list`, `sessions.create`, `sessions.get`, `sessions.delete`, `sessions.rename`, `sessions.delete_all`, `chat.send`, `chat.cancel`, `chat.read`, `agent.get`, `agent.update`, `agent.rename`, `agent.generate_avatar`, `tools.list`, `models.list`, `costs.summary`, `memory.list`, `memory.get`, `memory.set`, `jobs.list`, `jobs.update`, `jobs.delete`.
- Broadcast events: `session.updated`, `agents.changed`, `agent.renamed`, `cron.result`, `memory.updated`, `jobs.updated`, plus agent streaming events (`text.delta`, `text.end`, `thinking.delta`, `thinking.end`, `tool.start`, `tool.end`, `image.block`, `message.end`, `done`).
- Session model: discrete sessions per agent (like ChatGPT/Claude). Each session is a standalone conversation. Users see a list of sessions and explicitly create or revisit them. `chat.send` requires `session_id`. Auto-titling generates a short title via LLM after the first response (uses `tools.title.model` or falls back to agent's model), broadcast as `session.updated` event.
- Device commands (server-to-client requests): `device.location`, `device.camera`.
- Per-session tool injection: `CreateRuntime` adds MemoryTool, NotebookTool, CostTool, CronTool per-session.

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
  notebook:
    root: ~/path/to/notebook
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
  title:
    model: google/gemini-2.5-flash  # model for auto-generating session titles (default: agent's model)
  avatar:
    model: google/gemini-2.5-flash-image  # image-capable model for avatar generation (default)
  withings:
    client_id: <withings-client-id>
    client_secret: <withings-client-secret>  # or set WITHINGS_CLIENT_SECRET env var
    redirect_uri: http://localhost:5000/withings/callback  # optional, this is the default

cron:
  keep_last_per_job: 1    # sessions retained per cron job (default 1, 0 to disable)
  max_age_days: 30        # absolute ceiling on cron session age (default 30)
```

Loaded by `ConfigLoader.Load()` (in Server project). Env var override: `ACHATES_CONFIG_PATH`. YAML uses underscore naming convention (C# PascalCase <-> YAML snake_case). `~` is expanded in file paths (e.g. `notebook_root`). `${ENV_VAR}` expansion is **not yet supported** — use literal values or env vars directly.

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
  - notebook
  - mail

**Allowed Chats:**
  - val
  - claire

**Reasoning Effort:** medium

**Dreamtime:** 3:00 AM

## Prompt

You are a personal assistant...
```

Capabilities keys: `Model`, `Thinking Model`, `Provider`, `Tools`, `Allowed Chats`, `Reasoning Effort`, `Temperature`, `Max Tokens`, `Dreamtime`. List values use sub-bullets; scalar values go inline after the key.

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
~/.achates/agents/{agentName}/avatar.jpg                       Agent profile picture (optional, JPEG)
~/.achates/agents/{agentName}/images/                          Generated images from ImageTool
~/.achates/agents/{agentName}/read-state.json                  Read tracking (last read timestamp)
~/.achates/graph-token-cache.bin                               Graph device code token cache
~/.achates/withings-tokens.json                                Withings OAuth tokens (access + refresh)
```
