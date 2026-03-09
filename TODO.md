# TODO

## Transports

- [ ] **Transport capabilities** — Declare what each transport supports (threading, reactions, media, streaming, etc.) so the gateway can adapt behavior.
- [ ] **Rich content in TransportMessage** — Extend beyond plain text: images, audio, files, reactions. Mirror the `CompletionUserContent` model.
- [ ] **Streaming send** — Add a streaming send path (e.g. `StreamAsync` or draft-stream pattern) so transports like WebSocket and Discord can push incremental text.
- [x] **Typing indicators** — `ITransport.SendTypingAsync` with gateway keepalive loop (4s interval). Implemented for Telegram (`SendChatAction`) and WebSocket (JSON event).
- [ ] **Status reactions** — React to messages with status emojis (thinking, tool use, done, error). Debounce intermediate states, immediate for terminal.

## Agents & Gateway

- [x] **Named agents** — Agents defined in config with identity, description, prompt, model, tools. Each agent has its own persistent memory. Resolved at startup into `AgentDefinition`.
- [x] **Channel bindings** — Config-level binding of transport + agent. Multiple channels can share the same agent. Different Telegram bots can use different agents.
- [x] **Multi-agent support** — Different agents (different models, system prompts, tool sets) for different channels.
- [ ] **Authentication** — Bearer token auth when binding beyond localhost. Rate limiting on failed attempts. Later: device pairing, scoped roles.
- [ ] **Error handling and retries** — Graceful recovery when LLM calls fail mid-conversation.
- [ ] **Transport lifecycle management** — Auto-restart on failure with exponential backoff, per-channel enable/disable, config hot-reload.

## Sessions

- [x] **Per-peer sessions** — Each channel+peer pair gets its own agent runtime with independent conversation history. Session key: `channelName:peerId`.
- [x] **Session persistence** — Save/load agent message history to disk. Conversations survive restarts.
- [x] **Session compaction** — Summarize oldest messages via LLM when over 80% of context window. Falls back to truncation on failure.
- [x] **Session reset** — `/new` command clears in-memory agent and persisted session. Idle timeout deferred.
- [ ] **Session cleanup** — TTL or max count to bound accumulated sessions on disk.

## Memory

- [x] **Per-agent memory** — `MemoryTool` with read/save actions, backed by `~/.achates/agents/{name}/memory.md`. Shared across all peers using the same agent. Survives `/new` session resets.
- [ ] **Memory search** — Semantic search over memory when files grow too large for full injection. Embedding-based retrieval (hybrid vector + FTS).
- [ ] **Memory flush** — Proactively save important context before session compaction runs, so key facts survive summarization.

## Tools

- [ ] **Skills directory** — Load tool definitions from a `skills/` directory. Discoverable, configurable, filterable.
- [ ] **Skill eligibility** — Filter skills by platform, required binaries, environment variables.
- [ ] **User-invocable skills** — Skills that users can trigger directly via slash commands.
- [ ] **Transport-provided tools** — Let transports contribute their own tools (e.g. "send message to Telegram group").

## Session Tool

- [ ] **Usage tracking** — Token counts (input/output) and estimated cost per session.
- [ ] **Cache metrics** — Prompt cache hit rate and cached token count.
- [ ] **Context utilization** — Show current token usage vs. model context window.
- [ ] **Model override** — Accept a `model` parameter to switch the active model mid-conversation.

## Provider

- [ ] **Additional providers** — Anthropic direct, OpenAI direct, Ollama (local models).
- [ ] **Provider failover** — Automatic fallback when a provider is down or rate-limited.
- [ ] **Auth rotation** — Support multiple API keys with rotation and quota tracking.

## Server / Operations

- [x] **Configuration file** — `~/.achates/config.yaml` with agents, channels, provider config.
- [ ] **Env var expansion in config** — Support `${ENV_VAR}` syntax in YAML values (e.g. for tokens and secrets).
- [ ] **Daemon mode** — Run the server as a background service (launchd on macOS, systemd on Linux).
