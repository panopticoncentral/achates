# Achates

A persistent AI assistant with memory, tools, and a clean web interface.

Named after Aeneas's faithful companion in Virgil's *Aeneid*.

## Quick Start

1. Install dependencies:
   ```bash
   npm install
   ```
2. Create a `.env` file:
   ```bash
   cp .env.example .env
   ```
3. Add your [OpenRouter API key](https://openrouter.ai/keys) to `.env`
4. Start the development server:
   ```bash
   npm run dev
   ```
5. Open http://localhost:3000

## Features

- **Streaming chat** with any model available through OpenRouter
- **Persistent memory** — conversations saved as Markdown files
- **Tool use** — extensible tool system (date/time, memory search)
- **Localhost only** — runs only on your machine

## Project Structure

```
src/
  server.ts          — Express server
  agent.ts           — Core agent loop
  openrouter.ts      — OpenRouter API client
  memory.ts          — Markdown file persistence
  tools/
    index.ts         — Tool registry
    datetime.ts      — Date/time tool
    memory-search.ts — Memory search tool
public/              — Frontend (vanilla HTML/CSS/JS)
data/conversations/  — Stored conversations
```

## Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `OPENROUTER_API_KEY` | *(required)* | Your OpenRouter API key |
| `MODEL` | `anthropic/claude-sonnet-4` | Model to use |
| `PORT` | `3000` | Server port |

## Future Work

See [TICKETS.md](TICKETS.md) for planned features organized by track.
