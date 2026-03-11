# Configuration Reference

Achates is configured via `~/.achates/config.yaml`. Override the path with the `ACHATES_CONFIG_PATH` environment variable.

YAML uses `snake_case` naming (mapped to C# `PascalCase` automatically). Unknown fields are silently ignored. If the config file doesn't exist, a default is created on first run.

## Default config

```yaml
provider: openrouter

agents:
  default:
    model: anthropic/claude-sonnet-4
    tools: [session, memory]
    completion:
      reasoning_effort: medium

channels:
  console:
    transport: websocket
    agent: default

console:
  url: ws://localhost:5000/ws
```

## All settings

### Top-level

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `provider` | string | `openrouter` | Default LLM provider ID. Agents can override this. |
| `agents` | map | _(required)_ | Named agent definitions. At least one required. |
| `channels` | map | _(required)_ | Named channel bindings (transport + agent). At least one required. |

### `agents.<name>`

Each agent is a named entry with its own configuration.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `description` | string | _(none)_ | Agent description, used in system prompt (e.g. "a personal assistant"). |
| `model` | string | _(required)_ | Model ID within the provider. |
| `provider` | string | _(top-level)_ | Override the provider for this agent. |
| `tools` | string[] | _(none)_ | Tool names to enable. Available: `session`, `memory`, `todo`, `notes`, `mail`, `calendar`, `web_search`, `web_fetch`. |
| `prompt` | string | _(none)_ | Custom system prompt text. Replaces the default opening line. |
| `todo_file` | string | _(none)_ | Path to a Markdown todo list file. Enables per-session todo access when `todo` is in `tools`. |
| `notes` | object | _(none)_ | Apple Notes settings for the `notes` tool. |
| `completion` | object | _(none)_ | Completion options (see below). |
| `graph` | map | _(none)_ | Microsoft Graph account settings for `mail` and `calendar`. |
| `web` | object | _(none)_ | Web tool settings for `web_search` and `web_fetch`. |

### `agents.<name>.notes`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `folder` | string | `Achates` | Apple Notes folder the `notes` tool is allowed to access. The tool is restricted to this folder only. |

### `agents.<name>.web`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `brave_api_key` | string | _(none)_ | Brave Search API key. Falls back to `BRAVE_API_KEY` env var. Required for `web_search`. |

### `agents.<name>.completion`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `reasoning_effort` | string | `medium` | Reasoning effort level. Only sent if the model supports it. |
| `temperature` | number | _(none)_ | Sampling temperature. |
| `max_tokens` | int | _(none)_ | Maximum output tokens per response. |

### `channels.<name>`

Each channel binds a transport to an agent.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `transport` | string | _(required)_ | Transport type: `websocket` or `telegram`. |
| `agent` | string | _(required)_ | Name of the agent to bind to (must match a key in `agents`). |
| `token` | string | _(none)_ | Telegram bot token. Falls back to `TELEGRAM_BOT_TOKEN` env var. |
| `allowed_chat_ids` | int[] | _(none)_ | Restrict Telegram bot to specific chat IDs. |

### `console`

Settings for the CLI WebSocket client (`Achates.Console`).

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `url` | string | `ws://localhost:5000/ws` | WebSocket server URL to connect to. |
| `channel` | string | _(none)_ | Channel name sent to the server. |
| `peer` | string | _(none)_ | Peer ID sent to the server. |

## Environment variables

| Variable | Purpose |
|----------|---------|
| `ACHATES_CONFIG_PATH` | Override the config file path (default: `~/.achates/config.yaml`). |
| `OPENROUTER_API_KEY` | **Required.** API key for the OpenRouter provider. |
| `TELEGRAM_BOT_TOKEN` | Fallback Telegram bot token if not set in channel config. |
| `BRAVE_API_KEY` | Brave Search API key. Fallback if not set in agent's `web.brave_api_key`. |

## Data paths

| Path | Purpose |
|------|---------|
| `~/.achates/config.yaml` | Configuration file. |
| `~/.achates/sessions/{channelName}/{peerId}.json` | Persisted conversation history per session. |
| `~/.achates/agents/{agentName}/memory.md` | Agent memory (shared across all peers using the agent). |

## Implicit behavior

These features are always on and not configurable:

- **Session persistence** — Conversations are saved to disk after each response and restored on restart.
- **Session compaction** — When a conversation approaches 80% of the model's context window, older messages are summarized via the LLM and replaced with a compact summary. Falls back to truncation if summarization fails.
- **Agent memory** — Each agent has a persistent memory file that survives session resets (`/new`). The agent reads it at conversation start and saves important facts.
- **Typing indicators** — Sent to transports every 4 seconds while processing. Telegram shows native typing; WebSocket sends a `{"type":"typing"}` JSON event.
