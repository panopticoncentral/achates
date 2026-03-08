# TODO

## Channels

- [ ] **Channel capabilities** — Declare what each channel supports (threading, reactions, media, streaming, etc.) so the gateway can adapt behavior. See OpenClaw's `ChannelCapabilities` pattern.
- [ ] **Rich content in ChannelMessage** — Extend beyond plain text: images, audio, files, reactions. Mirror the `CompletionUserContent` model.
- [ ] **Streaming support in IChannel** — Add a streaming send path (e.g. `StreamAsync` or draft-stream pattern) so channels like WebSocket and Discord can push incremental text. OpenClaw uses a `DraftStreamLoop` with throttling (minChars, idleMs) per channel.
- [ ] **Typing indicators** — Start/stop typing indicator lifecycle with keepalive loop. OpenClaw tracks this per-channel with TTL and failure circuit breakers.
- [ ] **Status reactions** — React to messages with status emojis (thinking, tool use, done, error). Debounce intermediate states, immediate for terminal. OpenClaw's `StatusReactionController` pattern.

## Gateway

- [ ] **Authentication** — Bearer token auth when binding beyond localhost. Localhost is trusted (no auth needed). Rate limiting on failed attempts. Later: device pairing, scoped roles.
- [ ] **Routing configuration** — YAML/JSON config that maps channel + peer patterns to specific agents or models. Like OpenClaw's binding system.
- [ ] **Multi-agent support** — Allow different agents (different models, system prompts, tool sets) for different routes.
- [ ] **Error handling and retries** — Graceful recovery when LLM calls fail mid-conversation.
- [ ] **Channel lifecycle management** — Auto-restart on failure with exponential backoff, per-account enable/disable, config hot-reload. See OpenClaw's `server-channels.ts`.

## Sessions

- [ ] **Per-peer sessions** — Each channel+peer pair should get its own conversation history, not a shared agent. Currently all channels share one agent.
- [ ] **Session persistence** — Save/load agent message history to disk or SQLite. Conversations should survive restarts.
- [ ] **Session expiry/cleanup** — TTL or max message count to bound memory and storage.
- [ ] **Session context windowing** — Prune old messages when approaching the model's context limit.
- [ ] **Thread bindings** — Associate sessions with platform threads (Slack threads, Discord threads, Telegram reply chains). OpenClaw supports idle timeout, max age, and per-channel thread policies.

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
