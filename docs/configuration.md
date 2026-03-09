# Configuration Reference

Achates is configured via `~/.achates/config.yaml`. Override the path with the `ACHATES_CONFIG_PATH` environment variable.

YAML uses `snake_case` naming (mapped to C# `PascalCase` automatically). Unknown fields are silently ignored. If the config file doesn't exist, a default is created on first run.

## Default config

```yaml
provider: openrouter
model: anthropic/claude-sonnet-4
completion:
  reasoning_effort: medium
console:
  url: ws://localhost:5000/ws
```

## All settings

### Top-level

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `provider` | string | `openrouter` | LLM provider ID. Only `openrouter` is currently implemented. |
| `model` | string | `anthropic/claude-sonnet-4` | Model ID within the provider. Must exist in the provider's model list. |

### `completion`

Controls LLM generation parameters.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `reasoning_effort` | string | `medium` | Reasoning effort level. Only sent if the model supports it. |
| `temperature` | number | _(none)_ | Sampling temperature. Higher = more varied. |
| `max_tokens` | int | _(none)_ | Maximum output tokens per response. |

### `telegram`

Telegram bot channel. Omit this section entirely to disable Telegram.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `token` | string | _(none)_ | Bot token from @BotFather. Falls back to `TELEGRAM_BOT_TOKEN` env var. |
| `allowed_chat_ids` | int[] | _(none)_ | Restrict the bot to specific chat IDs. If omitted, all chats are allowed. |

### `console`

Settings for the CLI WebSocket client (`Achates.Console`).

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `url` | string | `ws://localhost:5000/ws` | WebSocket server URL to connect to. |
| `channel` | string | _(none)_ | Channel ID sent to the server. |
| `peer` | string | _(none)_ | Peer ID sent to the server. |

## Environment variables

| Variable | Purpose |
|----------|---------|
| `ACHATES_CONFIG_PATH` | Override the config file path (default: `~/.achates/config.yaml`). |
| `OPENROUTER_API_KEY` | **Required.** API key for the OpenRouter provider. |
| `TELEGRAM_BOT_TOKEN` | Fallback Telegram bot token if not set in config. |

## Data paths

| Path | Purpose |
|------|---------|
| `~/.achates/config.yaml` | Configuration file. |
| `~/.achates/sessions/{channelId}/{peerId}.json` | Persisted conversation history per session. |

## Implicit behavior

These features are always on and not configurable:

- **Session persistence** -- Conversations are saved to disk after each response and restored on restart.
- **Session compaction** -- When a conversation approaches 80% of the model's context window, older messages are summarized via the LLM and replaced with a compact summary. Falls back to truncation if summarization fails.
