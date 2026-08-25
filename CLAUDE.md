# Achates

AI agent framework with pluggable providers and tools. .NET 10 preview.

> **Keep this file up to date.** When you add, remove, or rename projects, change architectural patterns, or modify conventions, update the relevant sections of this file before finishing the task. Also update `README.md` when changes affect configuration format, tool setup instructions, or user-facing behavior. Update `docs/configuration.md` when changing the config system (adding/removing/renaming fields, env vars, data paths).

## Build & Test

```bash
dotnet build Achates.slnx
dotnet test Achates.slnx
```

Solution file is `Achates.slnx` (XML format, not legacy `.sln`).

## Running

Server requires at least one agent defined as an `AGENT.md` file. API key can be set in config (`api_key`) or via environment variable:
```bash
dotnet run --project src/Achates.Server
```
Config lives at `~/.achates/config.yaml`. Agents live at `~/.achates/agents/{name}/AGENT.md`.

## Architecture

### Core Concepts

- **Agent** — Named entity with identity (name, description), prompt, tools, and persistent memory. Defined in `~/.achates/agents/{name}/AGENT.md` (YAML frontmatter + markdown prompt), resolved at startup into `AgentDefinition`. Each agent may declare its own base model and thinking model via `**Model:**` and `**Thinking Model:**` capabilities; if absent they fall back to `models.base` / `models.thinking` in `config.yaml`. Memory has four tiers — shared (cross-agent, fetched on demand), core (preloaded at the head of every session), working (a small rolling scratchpad, preloaded on every turn), and archive (topical files retrieved on demand) — see `docs/configuration.md` for the config format and `src/Achates.Server/CLAUDE.md` for the mechanics.

### Where the rest lives

Per-project detail loads automatically when you work in that directory:

- `src/Achates.Providers/CLAUDE.md` — provider layer, content types, prompt caching
- `src/Achates.Agent/CLAUDE.md` — agent runtime, session store, compaction
- `src/Achates.Server/CLAUDE.md` — tool system, universal tools, server, transport

Config file format, `AGENT.md` capabilities keys, environment variables, and data paths are documented in `docs/configuration.md` — keep that file current instead of duplicating it here.

## Conventions

- Nullable reference types enabled, implicit usings enabled
- `sealed` on concrete classes by default
- Collection expressions (`[]`) preferred over `new List<T>()`
- Raw string literals for multi-line JSON/text
- xUnit test project at `tests/Achates.Tests` (run via `dotnet test Achates.slnx`)
