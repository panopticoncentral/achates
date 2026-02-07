# Achates — Future Work

Tickets are organized by track. Each ticket is independent and suitable for an agent to pick up and implement. Tickets within a track are roughly ordered by priority/dependency.

---

## Track: Tools

Expand the tool ecosystem so Achates can interact with external systems.

- [ ] **TOOLS-001: Web search tool** — Integrate a search API (e.g., Brave Search, SearXNG) to let Achates search the web. Define the tool interface, handle API keys, return formatted results.
- [ ] **TOOLS-002: URL fetch tool** — Fetch and extract readable content from a URL. Use a library like `@mozilla/readability` or similar to strip HTML to text. Handle timeouts, redirects, and size limits.
- [ ] **TOOLS-003: File read/write tools** — Allow Achates to read and write files in a configurable sandbox directory. Implement path validation to prevent directory traversal. Tools: `read_file`, `write_file`, `list_directory`.
- [ ] **TOOLS-004: Code execution tool** — Execute JavaScript/TypeScript code in a sandboxed environment (e.g., `vm2` or isolated subprocess). Return stdout, stderr, and result. Set execution timeout.
- [ ] **TOOLS-005: Generic HTTP tool** — Make arbitrary HTTP requests (GET, POST, PUT, DELETE) with configurable headers and body. Useful for API integrations. Include response status, headers, and body in results.
- [ ] **TOOLS-006: Image generation tool** — Integrate an image generation model via OpenRouter or a dedicated API (DALL-E, Stable Diffusion). Save generated images locally and return the file path.
- [ ] **TOOLS-007: Shell command tool** — Execute shell commands in a sandboxed environment with configurable allowed commands. Return stdout/stderr. Requires careful security review.
- [ ] **TOOLS-008: Calculator tool** — Evaluate mathematical expressions safely. Use a math parsing library rather than `eval`.

## Track: Messaging

Enable Achates to communicate through external messaging platforms.

- [ ] **MSG-001: Messaging adapter interface** — Define an abstract adapter interface (`sendMessage`, `onMessage`, `connect`, `disconnect`) that all messaging integrations implement. Design the message routing layer that connects adapters to the agent loop.
- [ ] **MSG-002: Discord bot adapter** — Implement a Discord bot using `discord.js`. Handle message events, channel routing, message splitting for Discord's character limit, and bot token configuration.
- [ ] **MSG-003: Slack bot adapter** — Implement a Slack bot using the Bolt framework. Handle app mentions, direct messages, thread replies, and OAuth token management.
- [ ] **MSG-004: Telegram bot adapter** — Implement a Telegram bot using `node-telegram-bot-api` or `telegraf`. Handle commands, messages, markdown formatting, and bot token configuration.
- [ ] **MSG-005: Signal adapter** — Integrate with Signal via `signal-cli` or the Signal REST API. Handle message send/receive and identity management.
- [ ] **MSG-006: Webhook inbound adapter** — Accept messages via HTTP webhook POST. Return responses synchronously or asynchronously. Useful for custom integrations.
- [ ] **MSG-007: Email adapter** — Send and receive emails via IMAP/SMTP. Parse email content, handle attachments, support threaded conversations.

## Track: Scheduling

Enable Achates to run tasks on a schedule and manage reminders.

- [ ] **SCHED-001: Cron scheduler** — Implement a persistent cron scheduler using `node-cron` or similar. Store scheduled tasks as Markdown files in `data/schedules/`. Support creating, listing, pausing, and deleting scheduled tasks.
- [ ] **SCHED-002: Reminder system** — Allow the user to say "remind me in 2 hours about X" and have Achates create a scheduled task. Parse natural language time expressions. Deliver reminders through the active messaging channel.
- [ ] **SCHED-003: Recurring conversation tasks** — Support tasks like "every morning, summarize the top news" or "every Friday, review my weekly notes". Each task triggers an agent conversation that runs autonomously and saves results.
- [ ] **SCHED-004: Background worker** — Implement a background worker process that runs scheduled tasks independently of the web server. Use IPC or a shared task queue. Handle graceful shutdown and task recovery.
- [ ] **SCHED-005: Schedule management UI** — Add a UI panel to view, create, edit, and delete scheduled tasks. Show next run time, last result, and task history.

## Track: Memory

Enhance how Achates remembers and retrieves information.

- [ ] **MEM-001: Conversation summarization** — Automatically generate summaries of long conversations. Store summaries alongside the full conversation. Use summaries to provide context in future conversations without sending the full history.
- [ ] **MEM-002: Semantic search** — Generate embeddings for conversation content (via OpenRouter or a local model). Store in a vector database (e.g., `vectra` for local file-based storage). Enable semantic similarity search in the `search_memory` tool.
- [ ] **MEM-003: User profile persistence** — Maintain a `data/profile.md` file that stores user preferences, facts, and context learned over time. Automatically update the profile when the user shares personal information. Include relevant profile context in the system prompt.
- [ ] **MEM-004: Knowledge extraction** — Extract named entities, facts, and relationships from conversations. Store as structured data in Markdown or JSON. Enable querying the knowledge base.
- [ ] **MEM-005: Cross-conversation context** — When starting a new conversation, automatically search for and include relevant context from past conversations. Use semantic search to find the most relevant prior discussions.
- [ ] **MEM-006: Memory compaction** — Periodically merge and compact old conversations. Archive detailed logs, keep summaries. Implement configurable retention policies.
- [ ] **MEM-007: Shared notes system** — Let the user and Achates collaboratively maintain Markdown notes in `data/notes/`. Achates can create, update, and reference notes. Notes persist across conversations.

## Track: UI

Improve the web interface and user experience.

- [ ] **UI-001: Settings panel** — Add a settings page accessible from the sidebar. Allow configuring: model selection (dropdown of OpenRouter models), API key, system prompt, theme. Persist settings to a config file.
- [ ] **UI-002: Memory browser** — Add a panel to browse, search, and delete past conversations. Show conversation metadata and preview. Support full-text search.
- [ ] **UI-003: Syntax highlighting** — Add syntax highlighting for code blocks in assistant responses. Use a lightweight library like `highlight.js` or `Prism.js`. Support common languages.
- [ ] **UI-004: LaTeX rendering** — Render LaTeX math expressions in assistant responses using KaTeX or MathJax.
- [ ] **UI-005: File upload** — Allow uploading files (images, documents, code) that get attached to the conversation and sent to the model. Support drag-and-drop.
- [ ] **UI-006: Conversation export** — Export conversations as Markdown, JSON, or PDF. Add an export button to the conversation view.
- [ ] **UI-007: Mobile-responsive layout** — Improve the layout for mobile devices. Collapsible sidebar, larger touch targets, swipe gestures.
- [ ] **UI-008: Light theme** — Add a light theme option. Toggle in settings. Respect system preference by default.
- [ ] **UI-009: Tool call visualization** — Show tool calls and results inline in the conversation with collapsible detail sections. Display tool name, arguments, and result clearly.
- [ ] **UI-010: Streaming markdown rendering** — Improve markdown rendering during streaming so formatting appears progressively rather than only after completion.

## Track: Infrastructure

Deployment, security, and operational concerns.

- [ ] **INFRA-001: Docker configuration** — Create a `Dockerfile` and `docker-compose.yml` for easy deployment. Include volume mounts for `data/` and `.env` configuration.
- [ ] **INFRA-002: Authentication** — Add optional authentication for the web UI. Support PIN code or password. Use session cookies. Configurable via `.env`.
- [ ] **INFRA-003: HTTPS support** — Add HTTPS support via self-signed certificate generation or reverse proxy documentation (nginx, Caddy).
- [ ] **INFRA-004: Logging** — Add structured logging (e.g., `pino`). Log API calls, tool executions, errors, and conversation events. Configurable log level.
- [ ] **INFRA-005: Rate limiting** — Add rate limiting to API endpoints to prevent runaway API costs. Configurable limits per endpoint.
- [ ] **INFRA-006: Token tracking** — Track token usage per conversation and in total. Display usage in the UI. Set configurable budget alerts.
- [ ] **INFRA-007: Backup and restore** — Script to backup `data/` directory. Support restore from backup. Document backup strategies.
- [ ] **INFRA-008: Multi-model support** — Allow switching models mid-conversation. Store model per message. Support model comparison (send same message to multiple models).
- [ ] **INFRA-009: Configuration management** — Move from `.env`-only config to a structured `config.yaml` or `config.json`. Support CLI flags, environment variables, and config file with precedence rules.
- [ ] **INFRA-010: Plugin system** — Design a plugin architecture where tools, adapters, and schedulers can be loaded dynamically from a `plugins/` directory. Define plugin manifest format and lifecycle hooks.
