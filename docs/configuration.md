# Configuration Reference

<!-- NOTE: Keep this file in sync with AchatesConfig.cs, AgentLoader.cs, ConfigLoader.cs, and GatewayService.cs.
     When adding/removing/renaming config fields, update the matching section here. -->

Achates configuration is split into two parts:
- **Global config** at `~/.achates/config.yaml` — provider and shared tool settings
- **Agent definitions** at `~/.achates/agents/{name}/AGENT.md` — one file per agent

YAML uses `snake_case` naming (mapped to C# `PascalCase` automatically). Unknown fields are silently ignored. `~` is expanded in file paths. If the config file doesn't exist, a default is created on first run. If no agents are found, a default agent is scaffolded.

## Default config

```yaml
provider:
  name: openrouter
```

## Default agent (`~/.achates/agents/default/AGENT.md`)

```markdown
# Default

A helpful assistant.

## Capabilities

**Model:** anthropic/claude-sonnet-4

**Tools:**
  - session
  - memory

**Reasoning Effort:** medium

## Prompt

You are a helpful assistant.
```

## Global settings (`config.yaml`)

### Top-level

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `provider` | object | _(required)_ | LLM provider configuration (see below). |
| `tools` | object | _(none)_ | Shared tool configuration (see below). |
| `cron` | object | _(none)_ | Cron session retention policy (see below). |

### `tools`

Shared tool configuration at the top level. Individual tools are enabled per-agent via the agent's `tools` list in AGENT.md.

#### `tools.notebook`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `root` | string | _(none)_ | Path to the agent's notebook directory. Supports `~` expansion. The directory must already exist — the tool is skipped with a warning if it doesn't. |

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

#### `tools.transcribe`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `model` | string | `google/gemini-2.5-flash` | Audio-capable model used by the `transcribe` tool. |

#### `tools.title`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `model` | string | _(agent's model)_ | Model used to auto-generate session titles after the first response. |

#### `tools.avatar`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `model` | string | `google/gemini-2.5-flash-image` | Image-capable model used by `agent.generate_avatar`. |

### `cron`

Retention policy for sessions saved by scheduled cron job runs. Applied by `CronSessionReaper` after each cron tick (self-throttled to once per 5 minutes per agent). Only affects `User`-kind cron sessions — dreamtime sessions are exempt.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `keep_last_per_job` | int | `1` | Number of most-recent sessions retained per cron job. Set to `0` to disable retention-based pruning. |
| `max_age_days` | int | `30` | Absolute ceiling in days; any cron-origin session older than this is pruned. |

## Agent definitions (`AGENT.md`)

Each agent is defined by a single `AGENT.md` file at `~/.achates/agents/{name}/AGENT.md`. The directory name becomes the agent name. The file is pure markdown with a conventional structure:

### Structure

| Section | Required | Description |
|---------|----------|-------------|
| `# Title` | yes | H1 heading. Display name for the agent. |
| _(description)_ | no | Paragraph(s) between H1 and first H2. Used in system prompt and agent listing. |
| `## Capabilities` | yes | Agent settings as `**Key:** value` lines (see below). |
| `## Prompt` | no | Everything under this heading becomes the system prompt. |

### Capabilities keys

Each capability is a `**Key:** value` line. List values (tools, allowed chats) use indented sub-bullets on following lines. Scalar values go inline.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Model` | string | _(required)_ | Model ID within the provider. |
| `Thinking Model` | string | _(none)_ | Model used by the `think` tool. Required when `think` is in the tools list. |
| `Provider` | string | _(global)_ | Override the provider for this agent. |
| `Tools` | list | _(none)_ | Tool names to enable. Available: `session`, `memory`, `notebook`, `notes`, `mail`, `calendar`, `web_search`, `web_fetch`, `cost`, `cron`, `imessage`, `transcribe`, `think`, `health`, `chat`, `location`, `camera`, `image`, `profile`, `agent_creator`. |
| `Allowed Chats` | list | _(all)_ | Allowlist of agent names this agent can chat with. Omit to allow all. Only relevant when `chat` is in tools. |
| `Reasoning Effort` | string | `medium` | Reasoning effort level. Only sent if the model supports it. |
| `Temperature` | number | _(none)_ | Sampling temperature. |
| `Max Tokens` | int | _(none)_ | Maximum output tokens per response. |
| `Dreamtime` | time | _(none)_ | Local time (e.g. `3:00 AM`) for nightly memory consolidation. Omit to disable dreamtime for this agent. |

## Full example

### `~/.achates/config.yaml`

```yaml
provider:
  name: openrouter
  api_key: sk-...

tools:
  notebook:
    root: ~/notebook
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
```

### `~/.achates/agents/paul/AGENT.md`

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
  - calendar
  - web_search
  - web_fetch
  - cost
  - cron
  - imessage
  - health
  - chat

**Allowed Chats:**
  - research

**Reasoning Effort:** medium

## Prompt

You are Paul's personal assistant...
```

## Environment variables

| Variable | Purpose |
|----------|---------|
| `ACHATES_CONFIG_PATH` | Override the config file path (default: `~/.achates/config.yaml`). |
| `OPENROUTER_API_KEY` | **Required.** API key for the OpenRouter provider. |
| `BRAVE_API_KEY` | Brave Search API key fallback. |
| `GRAPH_CLIENT_SECRET` | Microsoft Graph client secret fallback. |
| `WITHINGS_CLIENT_SECRET` | Withings client secret fallback. |

## Data paths

| Path | Purpose |
|------|---------|
| `~/.achates/config.yaml` | Global configuration (provider + tools). |
| `~/.achates/agents/{name}/AGENT.md` | Agent definition (markdown). |
| `~/.achates/agents/{name}/sessions/{sessionId}.json` | Persisted conversation history. |
| `~/.achates/memory.md` | Shared memory (universal user facts, all agents). |
| `~/.achates/agents/{name}/memory.md` | Agent memory (agent-specific notes). |
| `~/.achates/agents/{name}/costs.jsonl` | Cost ledger (append-only, always recorded). |
| `~/.achates/agents/{name}/cron.json` | Scheduled task definitions and state. |
| `~/.achates/agents/{name}/avatar.jpg` | Agent profile picture (optional; `.png` also accepted). |
| `~/.achates/agents/{name}/images/` | Images generated by the `image` tool. |
| `~/.achates/agents/{name}/read-state.json` | Read tracking (last read timestamp). |
| `~/.achates/graph-token-cache.bin` | Graph device code token cache. |
| `~/.achates/withings-tokens.json` | Withings OAuth tokens (access + refresh). |

## Implicit behavior

These features are always on and not configurable:

- **Session persistence** — Conversations are saved to disk after each response and restored on restart. Sessions are per-agent and created explicitly (one session = one conversation thread).
- **Session compaction** — When a conversation approaches 80% of the model's context window, older messages are summarized via the LLM and replaced with a compact summary. Falls back to truncation if summarization fails.
- **Agent memory** — Each agent has a persistent memory file that survives session boundaries. The agent reads it at conversation start and saves important facts.
- **Cost tracking** — Every completion is logged to the agent's cost ledger, regardless of whether the `cost` tool is enabled.
- **Auto-titling** — After the first response in a new session, the server generates a short title via `tools.title.model` (or the agent's own model) and broadcasts it as a `session.updated` event.
- **Cron session retention** — Sessions saved by cron job runs are pruned by `CronSessionReaper` according to the `cron` config (default: keep 1 per job, max 30 days).
