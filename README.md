# Achates

A personal AI assistant that runs on your own devices and answers on the channels you use.

## Architecture

```
┌─────────────┐         WebSocket          ┌──────────────────────────┐
│   Console   │ ◄──────────────────────► │         Server           │
│   (client)  │                            │  Gateway → Agent → LLM  │
└─────────────┘                            └──────────────────────────┘
```

### Projects

| Project | Purpose |
|---------|---------|
| `Achates.Providers` | Model provider abstraction and OpenRouter implementation. Completions, streaming events, tool definitions, cost tracking. |
| `Achates.Agent` | Stateful AI agent with conversation history, tool execution loop, steering/follow-up queues, and event streaming. |
| `Achates.Channels` | Channel abstraction (`IChannel`) and built-in implementations. A channel normalizes I/O for a messaging platform. |
| `Achates.Server` | ASP.NET Core server that hosts the gateway. Contains the `Gateway` orchestrator, session routing, tools, and REST/WebSocket endpoints. |
| `Achates.Console` | Thin CLI client. Connects to the server via WebSocket, sends user input, renders streamed events. |

### Data flow

```
Console (WebSocket client)
  → sends text to Server /ws endpoint
    → Gateway.GetOrCreateSession(channel, peer)
      → Agent.PromptAsync(text)
        → AgentLoop: LLM call → tool calls → repeat
      → JSON events streamed back over WebSocket
  → Console renders events (text deltas, tool calls, usage)
```

### Key types

- **`IChannel`** — Start/Stop lifecycle, `MessageReceived` event, `SendAsync`. One implementation per platform.
- **`ChannelMessage`** — Normalized message: `ChannelId`, `PeerId`, `Text`, `Timestamp`.
- **`Gateway`** — Owns channels and a single shared agent. Routes messages, subscribes to agent events.
- **`Agent`** — Stateful conversation thread. Manages message history, tool execution, mid-run steering.
- **`AgentTool`** — Base class for tools the agent can invoke. Subclass and implement `ExecuteAsync`.
- **`IModelProvider`** — Abstraction over LLM APIs. Currently implemented by `OpenRouterProvider`.

## Build

Requires .NET 10 preview.

```bash
dotnet build Achates.slnx
dotnet test Achates.slnx
```

## Usage

Start the server, then connect with the console client.

### Server

```bash
# Set your API key
export OPENROUTER_API_KEY=sk-...

# Start the server
dotnet run --project src/Achates.Server -- --model <id>
```

#### Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Health check |
| `POST` | `/chat` | Send a message, get the complete response |
| `GET` | `/ws` | WebSocket — stream agent events in real time |

**POST /chat**
```json
{ "message": "What time is it?", "channel": "api", "peer": "user1" }
// → { "reply": "It's 3:42 PM UTC.", "session": "api:user1" }
```

**WebSocket /ws?channel=ws&peer=user1**
Send plain text messages, receive JSON events:
```json
{ "type": "text.delta", "delta": "Hello" }
{ "type": "tool.start", "tool": "time", "arguments": {} }
{ "type": "tool.end", "tool": "time", "result": "...", "error": false }
{ "type": "message.end", "usage": { "input": 150, "output": 42, "cost": 0.001 } }
```

### Console

```bash
# Connect to the running server (default: ws://localhost:5000/ws)
dotnet run --project src/Achates.Console

# Connect to a different server or session
dotnet run --project src/Achates.Console -- --url ws://host:port/ws --channel mychannel --peer mypeer
```
