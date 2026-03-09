# Achates

AI agent framework with pluggable providers, channels, and tools. .NET 10 preview.

> **Keep this file up to date.** When you add, remove, or rename projects, change architectural patterns, or modify conventions, update the relevant sections of this file before finishing the task.

## Build & Test

```bash
dotnet build Achates.slnx
dotnet test Achates.slnx
```

Solution file is `Achates.slnx` (XML format, not legacy `.sln`).

## Running

Server requires a model and API key:
```bash
OPENROUTER_API_KEY=... dotnet run --project src/Achates.Server
```
Model is configured in `~/.achates/config.yaml`. Console client connects via WebSocket:
```bash
dotnet run --project src/Achates.Console -- --url ws://localhost:5000/ws
```

## Project Structure

```
src/
  Achates.Providers/     LLM provider abstraction + OpenRouter implementation
  Achates.Agent/         Stateful agent engine (messages, tools, event streaming)
  Achates.Channels/      Channel interface + implementations (Telegram)
  Achates.Configuration/ YAML config loading (YamlDotNet, underscore naming)
  Achates.Server/        ASP.NET Core gateway service, tools, system prompt
  Achates.Console/       CLI WebSocket client (Spectre.Console)
```

### Dependency flow

```
Providers <- Agent <- Server -> Channels
                      Server -> Configuration
Console (standalone, no config dependency)
```

## Architecture

### Provider Layer (`Achates.Providers`)
- `IModelProvider` — interface with `GetModelsAsync()` and `GetCompletions()` (streaming)
- `ModelProviders.Create(id)` — factory for provider instances
- Only implementation: `OpenRouterProvider` (SSE streaming, env var `OPENROUTER_API_KEY`)
- Content types: `CompletionContent` base, subtypes for text, image, audio, thinking, tool calls, files
- `CompletionUserContent` — input-only base. `CompletionAudioContent` is output-only (extends `CompletionContent`), `CompletionAudioInputContent` is input-only (extends `CompletionUserContent`). This asymmetry is intentional.
- Event streaming via `CompletionEventStream` using `System.Threading.Channels`

### Agent Layer (`Achates.Agent`)
- `Agent` — one instance = one conversation thread. Stateful with message history.
- `PromptAsync()` returns `AgentEventStream`; `ContinueAsync()` resumes after tool results
- `Steer()` interrupts current tool execution; `FollowUp()` queues for after current turn
- `AgentOptions` — model, system prompt, tools, completion options, metadata, context transform hooks
- `ISessionStore` — interface for persisting conversation history by session key (Load/Save/Delete)
- `SessionCompactor` — proactive compaction before each turn. Estimates tokens (uses provider's reported input count + char heuristic), summarizes oldest messages via LLM when over 80% of context window, falls back to truncation on failure. Preserves tool call/result pairs. `SummaryMessage` type holds the summary.

### Tool System (`AgentTool` subclasses)
- `AgentTool` is the preferred pattern (class-based). Subclass and implement `Name`, `Description`, `Parameters` (JSON Schema as `JsonElement`), `ExecuteAsync()`.
- Returns `AgentToolResult` with `Content` (list of `CompletionContent`) and optional `Details` (for UI display).
- Tools live in `src/Achates.Server/Tools/`. Current tools: `SessionTool`.
- Tool schema pattern: use `JsonSchemaHelpers` (`ObjectSchema`, `StringSchema`, `NumberSchema`, `BooleanSchema`, `StringEnum`) via `using static Achates.Providers.Util.JsonSchemaHelpers`.

### Channel System (`Achates.Channels`)
- `IChannel` — `SendAsync()`, `StartAsync()`, `StopAsync()`, `MessageReceived` event
- `ChannelMessage` — ChannelId, PeerId, Text, Timestamp
- Implementations: `TelegramChannel` (in Channels project), `WebSocketChannel` (in Server project)

### Gateway (`Achates.Server`)
- `Gateway` — wires channels to per-peer agent sessions. Each `channelId:peerId` pair gets its own `Agent` instance (created on first message). Routes inbound messages, accumulates text deltas, sends responses back. Persists sessions via `ISessionStore` after each completed response.
- `FileSessionStore` — stores conversation history as JSON files in `~/.achates/sessions/{channelId}/{peerId}.json`.
- `GatewayService` — ASP.NET Core `IHostedLifecycleService`. Resolves model at startup, creates gateway, registers channels.
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
model: anthropic/claude-sonnet-4
completion:
  reasoning_effort: medium
```

Loaded by `ConfigLoader.Load()`. Env var override: `ACHATES_CONFIG_PATH`. YAML uses underscore naming convention (C# PascalCase <-> YAML snake_case).
