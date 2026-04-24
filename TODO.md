# TODO

Server- and agent-layer work. Apple-app and protocol-level items live in [`docs/TODO.md`](docs/TODO.md).

## Sessions

- [ ] **User session cleanup** — TTL or max count to bound accumulated non-cron sessions on disk. (Cron sessions are already pruned by `CronSessionReaper`.)

## Memory

- [ ] **Memory search** — Semantic search over memory when files grow too large for full injection. Embedding-based retrieval (hybrid vector + FTS).
- [ ] **Memory flush** — Proactively save important context before session compaction runs, so key facts survive summarization.

## Tools

- [ ] **Skills directory** — Load tool definitions from a `skills/` directory. Discoverable, configurable, filterable.
- [ ] **Skill eligibility** — Filter skills by platform, required binaries, environment variables.
- [ ] **User-invocable skills** — Skills users can trigger directly.
- [ ] **Apple Notes: update/replace action** — `NotesTool` can only `create` today; extend to editing existing notes.

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

- [ ] **Authentication on `/ws`** — Currently unauthenticated. Shared secret or token-based auth in the `connect` handshake.
- [ ] **Error handling and retries** — Graceful recovery when LLM calls fail mid-conversation.
- [ ] **Env var expansion in config** — Support `${ENV_VAR}` syntax in YAML values (e.g. for tokens and secrets).
- [ ] **Daemon mode** — Run the server as a background service (launchd on macOS, systemd on Linux).
