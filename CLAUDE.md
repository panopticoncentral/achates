# Achates

AI agent framework with pluggable providers, transports, and tools. .NET 10 preview.

> **Keep this file up to date.** When you add, remove, or rename projects, change architectural patterns, or modify conventions, update the relevant sections of this file before finishing the task.

## Build & Test

```bash
dotnet build Achates.slnx
dotnet test Achates.slnx
```

Solution file is `Achates.slnx` (XML format, not legacy `.sln`).

## Running

Server requires a config with at least one agent and channel:
```bash
OPENROUTER_API_KEY=... dotnet run --project src/Achates.Server
```
Config lives at `~/.achates/config.yaml`. Console client connects via WebSocket:
```bash
dotnet run --project src/Achates.Console -- --url ws://localhost:5000/ws
```

## Project Structure

```
src/
  Achates.Providers/     LLM provider abstraction + OpenRouter implementation
  Achates.Agent/         Stateful agent runtime engine (messages, tools, event streaming)
  Achates.Transports/    Transport interface + implementations (Telegram)
  Achates.Configuration/ YAML config loading (YamlDotNet, underscore naming)
  Achates.Server/        ASP.NET Core gateway service, tools, system prompt
  Achates.Console/       CLI WebSocket client (Spectre.Console)
```

### Dependency flow

```
Providers <- Agent <- Server -> Transports
                      Server -> Configuration
Console (standalone, no config dependency)
```

## Architecture

### Core Concepts

- **Agent** — Named entity with identity (name, description), prompt, model, tools, and persistent memory. Defined in config, resolved at startup into `AgentDefinition`.
- **Transport** — A messaging mechanism (`ITransport`). Implementations: `TelegramTransport`, `WebSocketTransport`.
- **Channel** — A binding of a transport instance to an agent (`ChannelBinding`). Defined in config. Multiple channels can share the same agent.

### Provider Layer (`Achates.Providers`)
- `IModelProvider` — interface with `GetModelsAsync()` and `GetCompletions()` (streaming)
- `ModelProviders.Create(id)` — factory for provider instances
- Only implementation: `OpenRouterProvider` (SSE streaming, env var `OPENROUTER_API_KEY`)
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
- Tools live in `src/Achates.Server/Tools/`. Current tools: `SessionTool`, `MemoryTool`.
- Tools can be shared (same instance for all sessions) or per-session. The Gateway builds per-session tool lists via `BuildSessionTools()` (e.g. `MemoryTool` uses per-agent memory path).
- Tool schema pattern: use `JsonSchemaHelpers` (`ObjectSchema`, `StringSchema`, `NumberSchema`, `BooleanSchema`, `StringEnum`) via `using static Achates.Providers.Util.JsonSchemaHelpers`.

### Transport System (`Achates.Transports`)
- `ITransport` — `SendAsync()`, `SendTypingAsync()` (default no-op), `StartAsync()`, `StopAsync()`, `MessageReceived` event
- `TransportMessage` — TransportId, PeerId, Text, Timestamp
- Implementations: `TelegramTransport` (in Transports project), `WebSocketTransport` (in Server project)

### Gateway (`Achates.Server`)
- `Gateway` — takes a list of `ChannelBinding` (transport + agent pairs) and an optional `ISessionStore`. Each `channelName:peerId` pair gets its own `AgentRuntime` instance configured from the channel's `AgentDefinition`. Routes inbound messages, accumulates text deltas, sends responses back. Persists sessions after each completed response. Sends typing indicators via a keepalive loop (4s interval). Handles `/new` command to reset sessions.
- `ChannelBinding` — binds a channel name to a transport and an agent definition.
- `AgentDefinition` — resolved agent with Model, SystemPrompt, Tools, CompletionOptions, MemoryPath.
- `FileSessionStore` — stores conversation history as JSON files in `~/.achates/sessions/{channelName}/{peerId}.json`.
- `MemoryTool` — per-agent persistent memory at `~/.achates/agents/{agentName}/memory.md`. Read/save actions; survives `/new` resets. Shared across all peers using the same agent.
- `GatewayService` — ASP.NET Core `IHostedLifecycleService`. Resolves agents and channels from config at startup, creates transports, builds `ChannelBinding` list, creates gateway.
- WebSocket endpoint: `/ws` (query params: `channel`, `peer`)
- Health check: `GET /health`

## Conventions

- File-scoped namespaces everywhere
- Primary constructors on services (e.g. `GatewayService(AchatesConfig config, ...)`)
- Nullable reference types enabled, implicit usings enabled
- `sealed` on concrete classes by default
- Collection expressions (`[]`) preferred over `new List<T>()`
- Raw string literals for multi-line JSON/text
- No test framework established yet

## Configuration (`~/.achates/config.yaml`)

```yaml
provider: openrouter

agents:
  paul:
    description: Personal assistant
    model: anthropic/claude-sonnet-4
    tools: [session, memory]
    completion:
      reasoning_effort: medium

channels:
  telegram:
    transport: telegram
    agent: paul
    token: ${TELEGRAM_BOT_TOKEN}
    allowed_chat_ids: [12345]
  console:
    transport: websocket
    agent: paul

console:
  url: ws://localhost:5000/ws
```

Loaded by `ConfigLoader.Load()`. Env var override: `ACHATES_CONFIG_PATH`. YAML uses underscore naming convention (C# PascalCase <-> YAML snake_case).

### Data paths

```
~/.achates/config.yaml                          Configuration
~/.achates/sessions/{channelName}/{peerId}.json  Conversation history
~/.achates/agents/{agentName}/memory.md          Agent memory (shared across peers)
```
