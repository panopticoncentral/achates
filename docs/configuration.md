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

**Tools:**
  - session

**Reasoning Effort:** medium

## Prompt

You are a helpful assistant.
```

## Global settings (`config.yaml`)

### Top-level

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `provider` | object | _(required)_ | LLM provider configuration (see below). |
| `models` | object | _(none)_ | Default base/thinking model fallbacks. Per-agent values in AGENT.md override these (see below). |
| `tools` | object | _(none)_ | Shared tool configuration (see below). |
| `cron` | object | _(none)_ | Cron session retention policy (see below). |

### `models`

Defaults used when an agent's AGENT.md doesn't declare its own `**Model:**` / `**Thinking Model:**`. Setting them per-agent is preferred; the global block exists so a freshly scaffolded agent has something to fall back to.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `base` | string | _(none)_ | Fallback base model when an agent doesn't declare `**Model:**`. At least one source (per-agent or global) must be set or the agent fails to load. |
| `thinking` | string | _(none)_ | Fallback thinking model when an agent doesn't declare `**Thinking Model:**`. Without one from either source, agents with the `think` tool simply skip the tool. |

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
| `model` | string | _(base model)_ | Model used to auto-generate session titles after the first response. |

#### `tools.avatar`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `model` | string | `google/gemini-2.5-flash-image` | Image-capable model used by `agent.generate_avatar`. |

#### `tools.image`

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `models` | list of strings | _(none)_ | Image-capable models the `image` tool can choose from. When more than one is configured, the tool exposes a required `model` enum parameter so the agent picks per call. The first entry is the default. |
| `model` | string | _(none)_ | Legacy single-model form. Equivalent to a one-element `models` list. Ignored when `models` is non-empty. |
| `api_key` | string | _(falls back to `provider.api_key`)_ | Optional override key used for image generation only (the `image` tool and avatar generation). Lets you route image traffic through a separate key — useful when the main key has a ZDR / privacy restriction that excludes image-only providers like Black Forest Labs. |

At least one of `models` or `model` is required when an agent enables the `image` tool; otherwise the tool is skipped on startup.

**Discovering image models on OpenRouter.** The `/api/v1/models` catalog endpoint only returns chat-completions-style image models (Google's Nano Banana family, OpenAI's GPT-5-Image family). The per-image-priced models — Black Forest Labs Flux, Recraft, Sourceful Riverflow, ByteDance Seedream, etc. — are not listed there but are usable via the same chat-completions endpoint with `modalities: ["image"]`. Browse them at [openrouter.ai/models?modality=text->image](https://openrouter.ai/models?modality=text-%3Eimage). Verified slugs include `black-forest-labs/flux.2-{pro,max,flex,klein-4b}`, `recraft/recraft-v{3,4,4-pro}`, `sourceful/riverflow-v2-{pro,fast,max-preview,standard-preview,fast-preview}`, `bytedance-seed/seedream-4.5`. Note: image-only providers are typically not ZDR-eligible — use `tools.image.api_key` to route through a non-ZDR key when the main provider key has ZDR enabled.

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
| `Provider` | string | _(global)_ | Override the provider for this agent. |
| `Model` | string | _(`models.base`)_ | Base model id for this agent. Falls back to `models.base` in config.yaml. |
| `Thinking Model` | string | _(`models.thinking`)_ | Thinking model id used by the `think` tool. Falls back to `models.thinking`. Only consulted when `think` is enabled. |
| `Tools` | list | _(none)_ | Tool names to enable. Available: `session`, `notebook`, `notes`, `mail`, `calendar`, `web_search`, `web_fetch`, `cron`, `imessage`, `transcribe`, `think`, `health`, `chat`, `location`, `camera`, `image`, `profile`, `agent_manager`. Note: `memory` and `cost` are always available to every agent; listing them here is accepted but ignored. |
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

models:
  base: anthropic/claude-sonnet-4.6
  thinking: anthropic/claude-opus-4.7

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

**Model:** anthropic/claude-sonnet-4.6

**Thinking Model:** anthropic/claude-opus-4.7

**Tools:**
  - session
  - notebook
  - mail
  - calendar
  - web_search
  - web_fetch
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
| `~/.achates/config.yaml` | Global configuration (provider, models, tools). |
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
