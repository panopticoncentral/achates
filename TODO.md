# TODO

## Channels

- [ ] **Channel capabilities** — Declare what each channel supports (threading, reactions, media, streaming, etc.) so the gateway can adapt behavior. See OpenClaw's `ChannelCapabilities` pattern.
- [ ] **Rich content in ChannelMessage** — Extend beyond plain text: images, audio, files, reactions. Mirror the `CompletionUserContent` model.
- [ ] **Streaming support in IChannel** — Add a streaming send path (e.g. `StreamAsync` or draft-stream pattern) so channels like WebSocket and Discord can push incremental text. OpenClaw uses a `DraftStreamLoop` with throttling (minChars, idleMs) per channel.
- [x] **Typing indicators** — `IChannel.SendTypingAsync` with gateway keepalive loop (4s interval). Implemented for Telegram (`SendChatAction`) and WebSocket (JSON event).
- [ ] **Status reactions** — React to messages with status emojis (thinking, tool use, done, error). Debounce intermediate states, immediate for terminal. OpenClaw's `StatusReactionController` pattern.

## Gateway

- [ ] **Authentication** — Bearer token auth when binding beyond localhost. Localhost is trusted (no auth needed). Rate limiting on failed attempts. Later: device pairing, scoped roles.
- [ ] **Routing configuration** — YAML/JSON config that maps channel + peer patterns to specific agents or models. Like OpenClaw's binding system.
- [ ] **Multi-agent support** — Allow different agents (different models, system prompts, tool sets) for different routes.
- [ ] **Error handling and retries** — Graceful recovery when LLM calls fail mid-conversation.
- [ ] **Channel lifecycle management** — Auto-restart on failure with exponential backoff, per-account enable/disable, config hot-reload. See OpenClaw's `server-channels.ts`.

## Sessions

- [x] **Per-peer sessions** — Each channel+peer pair gets its own agent instance with independent conversation history. Session key: `channelId:peerId`.
- [x] **Session persistence** — Save/load agent message history to disk or SQLite. Conversations should survive restarts.
- [x] **Session compaction** — When conversation approaches the model's context limit, summarize older messages via the LLM and replace them with the summary. Preserve identifiers and key facts. Progressive fallback if summarization fails.
- [x] **Session reset** — `/new` command clears in-memory agent and persisted session. Idle timeout deferred.
- [ ] **Session cleanup** — TTL or max count to bound accumulated sessions on disk. Least urgent — files are small.

## Session Tool

- [ ] **Usage tracking** — Token counts (input/output) and estimated cost per session.
- [ ] **Cache metrics** — Prompt cache hit rate and cached token count.
- [ ] **Context utilization** — Show current token usage vs. model context window.
- [ ] **Session info** — Session key, last activity timestamp, compaction count (once per-peer sessions exist).
- [ ] **Model override** — Accept a `model` parameter to switch the active model mid-conversation.
- [ ] **User timezone detection** — Detect 12h/24h preference from OS settings (macOS `defaults read`, etc.).

## Memory

- [x] **Per-peer memory** — `MemoryTool` with read/save actions, backed by `~/.achates/memory/{channelId}/{peerId}.md`. Survives `/new` session resets. System prompt instructs agent to read at conversation start and save important facts.
- [ ] **Memory search** — Semantic search over memory when files grow too large for full injection. Embedding-based retrieval (hybrid vector + FTS).
- [ ] **Memory flush** — Proactively save important context before session compaction runs, so key facts survive summarization.
- [ ] **Shared memory** — Per-channel or global memory files in addition to per-peer, for information that should be shared across peers.

## Skills / Tools

- [ ] **Skills directory** — Load tool definitions from a `skills/` directory (markdown + code, like OpenClaw). Discoverable, configurable, filterable.
- [ ] **Skill eligibility** — Filter skills by platform, required binaries, environment variables.
- [ ] **User-invocable skills** — Skills that users can trigger directly via slash commands.
- [ ] **Channel-provided tools** — Let channels contribute their own tools (e.g. "send message to Telegram group"). OpenClaw's threading adapter builds a `ChannelThreadingToolContext` that tools can use to send to specific threads.

## Provider

- [ ] **Additional providers** — Anthropic direct, OpenAI direct, Ollama (local models).
- [ ] **Provider failover** — Automatic fallback when a provider is down or rate-limited.
- [ ] **Auth rotation** — Support multiple API keys with rotation and quota tracking.

## Server / Operations

- [ ] **Configuration file** — `~/.achates/config.yaml` for default model, provider, API keys, channel setup.
- [ ] **Daemon mode** — Run the server as a background service (launchd on macOS, systemd on Linux).
