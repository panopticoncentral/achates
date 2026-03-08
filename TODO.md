# TODO

## Channels

- [ ] **Telegram channel** — HTTP webhook-based. Simplest real-world channel to implement.
- [ ] **Discord channel** — WebSocket gateway + REST API for sending.
- [ ] **Slack channel** — Socket Mode or Events API.
- [ ] **iMessage channel** — AppleScript or BlueBubbles bridge on macOS.
- [ ] **Rich content in ChannelMessage** — Extend beyond plain text: images, audio, files, reactions. Mirror the `CompletionUserContent` model.

## Gateway

- [x] **HTTP/WebSocket server** — `Achates.Server` exposes the gateway via REST (`POST /chat`) and WebSocket (`/ws`) endpoints.
- [ ] **Authentication** — Bearer token auth when binding beyond localhost. Localhost is trusted (no auth needed). Rate limiting on failed attempts. Later: device pairing, scoped roles.
- [ ] **Routing configuration** — YAML/JSON config that maps channel + peer patterns to specific agents or models. Like OpenClaw's binding system.
- [ ] **Multi-agent support** — Allow different agents (different models, system prompts, tool sets) for different routes.
- [ ] **Error handling and retries** — Graceful recovery when LLM calls fail mid-conversation.

## Sessions

- [ ] **Session persistence** — Save/load agent message history to disk or SQLite. Conversations should survive restarts.
- [ ] **Session expiry/cleanup** — TTL or max message count to bound memory and storage.
- [ ] **Session context windowing** — Prune old messages when approaching the model's context limit.

## Skills / Tools

- [ ] **Skills directory** — Load tool definitions from a `skills/` directory (markdown + code, like OpenClaw). Discoverable, configurable, filterable.
- [ ] **Skill eligibility** — Filter skills by platform, required binaries, environment variables.
- [ ] **User-invocable skills** — Skills that users can trigger directly via slash commands.
- [ ] **Channel-provided tools** — Let channels contribute their own tools (e.g. "send message to Telegram group").

## Provider

- [ ] **Additional providers** — Anthropic direct, OpenAI direct, Ollama (local models).
- [ ] **Provider failover** — Automatic fallback when a provider is down or rate-limited.
- [ ] **Auth rotation** — Support multiple API keys with rotation and quota tracking.

## Console Client

- [ ] **Rich content** — Support sending images, audio, files over WebSocket (binary frames or base64).
- [ ] **Microphone recording** — Re-add `/record` support as a client-side feature that sends audio to the server.
- [ ] **Auto-reconnect** — Reconnect automatically if the server restarts.

## Server / Operations

- [ ] **Configuration file** — `~/.achates/config.yaml` for default model, provider, API keys, channel setup.
- [ ] **Daemon mode** — Run the server as a background service (launchd on macOS, systemd on Linux).
