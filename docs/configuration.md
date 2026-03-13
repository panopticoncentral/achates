# Configuration Reference

<!-- NOTE: Keep this file in sync with AchatesConfig.cs, ConfigLoader.cs, and GatewayService.cs.
     When adding/removing/renaming config fields, update the matching section here. -->

Achates is configured via `~/.achates/config.yaml`. Override the path with the `ACHATES_CONFIG_PATH` environment variable.

YAML uses `snake_case` naming (mapped to C# `PascalCase` automatically). Unknown fields are silently ignored. `~` is expanded in file paths. If the config file doesn't exist, a default is created on first run.

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
      websocket: {}

console:
  url: ws://localhost:5000/ws
```

## All settings

### Top-level

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `provider` | string | `openrouter` | Default LLM provider ID. Agents can override this. |
| `agents` | map | _(required)_ | Named agent definitions. At least one required. |
| `tools` | object | _(none)_ | Shared tool configuration (see below). |
| `console` | object | _(none)_ | CLI WebSocket client settings. |

### `agents.<name>`

Each agent is a named entry with its own configuration. Channels are nested under the agent.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `description` | string | _(none)_ | Agent description, used in system prompt (e.g. "a personal assistant"). |
| `model` | string | _(required)_ | Model ID within the provider. |
| `provider` | string | _(top-level)_ | Override the provider for this agent. |
| `tools` | string[] | _(none)_ | Tool names to enable. Available: `session`, `memory`, `todo`, `notes`, `mail`, `calendar`, `web_search`, `web_fetch`, `cost`, `cron`, `imessage`, `health`. |
| `prompt` | string | _(none)_ | Inline custom system prompt text. Cannot be used with `prompt_file`. |
| `prompt_file` | string | _(none)_ | Path to a file containing the system prompt. Supports `~` expansion. Cannot be used with `prompt`. |
| `completion` | object | _(none)_ | Completion options (see below). |
| `channels` | map | _(none)_ | Transport bindings for this agent, keyed by transport type. |

### `agents.<name>.completion`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `reasoning_effort` | string | `medium` | Reasoning effort level. Only sent if the model supports it. |
| `temperature` | number | _(none)_ | Sampling temperature. |
| `max_tokens` | int | _(none)_ | Maximum output tokens per response. |

### `agents.<name>.channels.<type>`

Each channel is keyed by transport type (`websocket` or `telegram`). The channel name is derived as `{agentName}/{transportType}`.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `token` | string | _(none)_ | Telegram bot token. Falls back to `TELEGRAM_BOT_TOKEN` env var. Required for `telegram`. |
| `allowed_chat_ids` | long[] | _(none)_ | Restrict Telegram bot to specific chat IDs. |

WebSocket channels have no required config — use `websocket: {}`.

### `tools`

Shared tool configuration at the top level. Individual tools are enabled per-agent via the agent's `tools` list.

#### `tools.todo`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `file` | string | _(none)_ | Path to a Markdown todo list file. Supports `~` expansion. |

#### `tools.notes`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `folder` | string | `Achates` | Apple Notes folder the `notes` tool is restricted to. |

#### `tools.web_search`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `brave_api_key` | string | _(none)_ | Brave Search API key. Falls back to `BRAVE_API_KEY` env var. Required for `web_search`. |

#### `tools.graph`

Microsoft Graph API accounts for `mail` and `calendar` tools. Each entry is a named account. Multiple accounts supported.

Auth flow is selected by the presence of `client_secret`:
- **With `client_secret`** → client credentials flow (work/school accounts, application permissions)
- **Without `client_secret`** → device code flow (personal or work/school, delegated permissions)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `client_id` | string | _(required)_ | Azure app registration client ID. |
| `tenant_id` | string | `consumers` | Azure AD tenant ID. Defaults to `consumers` for device code flow. |
| `client_secret` | string | _(none)_ | App secret. Presence triggers client credentials flow. Falls back to `GRAPH_CLIENT_SECRET` env var. |
| `user_email` | string | _(none)_ | Required for client credentials flow. |

#### `tools.withings`

Withings Health API for the `health` tool. OAuth 2.0 authorization code flow — the agent provides an auth URL, Withings redirects to the callback endpoint.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `client_id` | string | _(required)_ | Withings app client ID. |
| `client_secret` | string | _(none)_ | Withings app secret. Falls back to `WITHINGS_CLIENT_SECRET` env var. |
| `redirect_uri` | string | `http://localhost:5000/withings/callback` | OAuth callback URL. |

### `console`

Settings for the CLI WebSocket client (`Achates.Console`).

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `url` | string | `ws://localhost:5000/ws` | WebSocket server URL to connect to. |
| `agent` | string | _(none)_ | Agent name sent to the server. |
| `peer` | string | _(none)_ | Peer ID sent to the server. |

## Full example

```yaml
provider: openrouter

tools:
  todo:
    file: ~/todo.md
  web_search:
    brave_api_key: BSA...
  graph:
    personal:
      client_id: <app-client-id>
    work:
      tenant_id: <azure-tenant-id>
      client_id: <app-client-id>
      client_secret: <secret>
      user_email: user@example.com
  withings:
    client_id: <withings-client-id>
    client_secret: <withings-client-secret>

agents:
  paul:
    description: Personal assistant
    model: anthropic/claude-sonnet-4
    prompt_file: ~/.achates/agents/paul/prompt.md
    tools: [session, memory, todo, mail, calendar, web_search, web_fetch, cost, cron, imessage, health]
    completion:
      reasoning_effort: medium
    channels:
      telegram:
        token: your-bot-token
        allowed_chat_ids: [12345]
      websocket: {}

console:
  url: ws://localhost:5000/ws
```

## Environment variables

| Variable | Purpose |
|----------|---------|
| `ACHATES_CONFIG_PATH` | Override the config file path (default: `~/.achates/config.yaml`). |
| `OPENROUTER_API_KEY` | **Required.** API key for the OpenRouter provider. |
| `TELEGRAM_BOT_TOKEN` | Fallback Telegram bot token if not set in channel config. |
| `BRAVE_API_KEY` | Brave Search API key fallback. |
| `GRAPH_CLIENT_SECRET` | Microsoft Graph client secret fallback. |
| `WITHINGS_CLIENT_SECRET` | Withings client secret fallback. |

## Data paths

| Path | Purpose |
|------|---------|
| `~/.achates/config.yaml` | Configuration file. |
| `~/.achates/sessions/{agentName}/{transportType}/{peerId}.json` | Persisted conversation history. |
| `~/.achates/agents/{agentName}/memory.md` | Agent memory (shared across all peers). |
| `~/.achates/agents/{agentName}/costs.jsonl` | Cost ledger (append-only, always recorded). |
| `~/.achates/agents/{agentName}/cron.json` | Scheduled task definitions and state. |
| `~/.achates/graph-token-cache.bin` | Graph device code token cache. |
| `~/.achates/withings-tokens.json` | Withings OAuth tokens (access + refresh). |

## Implicit behavior

These features are always on and not configurable:

- **Session persistence** — Conversations are saved to disk after each response and restored on restart.
- **Session compaction** — When a conversation approaches 80% of the model's context window, older messages are summarized via the LLM and replaced with a compact summary. Falls back to truncation if summarization fails.
- **Agent memory** — Each agent has a persistent memory file that survives session resets (`/new`). The agent reads it at conversation start and saves important facts.
- **Cost tracking** — Every completion is logged to the agent's cost ledger, regardless of whether the `cost` tool is enabled.
- **Typing indicators** — Sent to transports every 4 seconds while processing. Telegram shows native typing; WebSocket sends a `{"type":"typing"}` JSON event.
