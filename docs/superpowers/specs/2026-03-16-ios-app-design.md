# Achates iOS App — Design Spec

## Overview

A native SwiftUI iOS app that serves as the primary client for the Achates agent framework. The app provides a messaging-app experience for conversing with multiple agents, managing sessions, and acting as a device service provider (location, camera) for the server. It connects via a new frame-based WebSocket protocol that coexists with the existing transports.

## Goals

- Rich chat experience with streaming, thinking blocks, tool call visibility, and markdown rendering
- Multi-agent support with a contacts-like agent list
- Session management: list, resume, create, auto-name, delete
- Device services: agent can request the user's location or a camera photo via the app
- Push notifications for offline message delivery (cron jobs)
- Voice input via on-device speech-to-text
- Connection resilience with reconnection and event replay

## Non-Goals (v1)

- Admin features (memory editing, cost viewing, config management)
- TTS / conversational voice mode
- Android or cross-platform
- Health/fitness, contacts, clipboard, or other device services beyond location and camera
- End-to-end encryption
- Offline message composition / queuing

## Architecture

### Approach

A new `/ws/v2` endpoint on the Achates server speaks a richer frame-based protocol purpose-built for the iOS app. The existing `/ws` (Console) and Telegram transports remain untouched. Both the new and old transports route through the same Gateway and AgentRuntime.

### Protocol

Three frame types over WebSocket, all JSON:

**Request** (bidirectional — client→server or server→client):
```json
{"type": "req", "id": "<uuid>", "method": "<string>", "params": {}}
```

**Response** (matches a request by `id`):
```json
{"type": "res", "id": "<uuid>", "ok": true, "payload": {}}
```
```json
{"type": "res", "id": "<uuid>", "ok": false, "error": {"code": "<string>", "message": "<string>"}}
```

**Event** (server→client, streaming, sequenced):
```json
{"type": "evt", "event": "<string>", "payload": {}, "seq": 42}
```

### Connection Lifecycle

1. Client connects to `/ws/v2`
2. Client sends `connect` request:
   ```json
   {
     "type": "req",
     "id": "<uuid>",
     "method": "connect",
     "params": {
       "agent": "paul",
       "peer": "<device-id>",
       "capabilities": ["location", "camera"],
       "device_token": "<apns-token>",
       "last_seq": null
     }
   }
   ```
3. Server responds with agent info and session state:
   ```json
   {
     "type": "res",
     "id": "<uuid>",
     "ok": true,
     "payload": {
       "agents": [{"name": "paul", "description": "Personal assistant", "tools": [...]}],
       "session": {"id": "<id>", "title": "...", "messages": [...]},
       "seq": 0
     }
   }
   ```
4. Connection is live — client can send RPCs and receives events.

### RPC Methods

**Client → Server:**

| Method | Params | Description |
|--------|--------|-------------|
| `connect` | `agent`, `peer`, `capabilities`, `device_token`, `last_seq` | Handshake, declares capabilities, resumes event stream |
| `chat.send` | `text` | Send a user message |
| `chat.cancel` | — | Abort the current agent turn |
| `sessions.list` | `agent` | List all sessions for an agent |
| `sessions.switch` | `session_id` | Switch to a different session |
| `sessions.new` | `agent` | Create a new session |
| `sessions.rename` | `session_id`, `title` | Rename a session |
| `sessions.delete` | `session_id` | Delete a session |
| `agents.list` | — | List available agents with descriptions |
| `ping` | — | Client-to-server health check (used on foreground return) |

**Server → Client (device commands):**

| Method | Params | Description |
|--------|--------|-------------|
| `device.location` | — | Request current GPS location |
| `device.camera` | `facing` (front/back) | Request a photo capture |

### Error Codes

| Code | Description |
|------|-------------|
| `UNKNOWN` | Unclassified server error |
| `AGENT_NOT_FOUND` | Requested agent name doesn't exist |
| `SESSION_NOT_FOUND` | Requested session ID doesn't exist |
| `ALREADY_CONNECTED` | Another mobile connection is already active for this peer |
| `PERMISSION_DENIED` | Device permission not granted (location, camera) |
| `DEVICE_UNAVAILABLE` | No mobile client connected to fulfill device command |
| `DEVICE_TIMEOUT` | Device command timed out |
| `CANCELLED` | Agent turn was cancelled via `chat.cancel` |

### Streaming Events

Same semantics as the current protocol, wrapped in `evt` frames with sequence numbers:

| Event | Payload | Description |
|-------|---------|-------------|
| `text.delta` | `delta` | Streaming assistant text |
| `text.end` | — | End of text block |
| `thinking.delta` | `delta` | Streaming thinking/reasoning |
| `thinking.end` | — | End of thinking block |
| `tool.start` | `tool`, `arguments` | Tool execution begins |
| `tool.end` | `result`, `error` | Tool execution completes |
| `message.end` | `usage` (input, output, cost) | Agent turn complete |
| `done` | — | Full conversation turn complete |
| `session.renamed` | `session_id`, `title` | Auto-generated session title ready |

### Reconnection

- Client tracks the last received `seq` number
- On reconnect, sends `connect` with `last_seq`
- Server replays buffered events if available (buffer: last 1000 events)
- If buffer is flushed, server responds with `replay: false` and client reloads session history via `sessions.switch`
- Reconnection uses exponential backoff: 500ms → 1s → 2s → 4s → ... → 30s max
- On app foreground after ≥3s in background: send `ping` RPC, reconnect if no response within 2s

## Server-Side Changes

All changes are in `Achates.Server`. No changes to `Achates.Agent`, `Achates.Providers`, or `Achates.Transports`.

### New Components

**`MobileTransport`** — does NOT implement `ITransport`. Unlike the existing transports that map 1:1 to a Gateway `ChannelBinding`, `MobileTransport` operates outside the transport/channel model. It is a standalone WebSocket handler that:
- Accepts the `/ws/v2` connection and manages the frame protocol
- Holds references to all `AgentDefinition` instances (populated at startup by `GatewayService`)
- Creates and manages its own `AgentRuntime` instances per agent+peer+session, independent of Gateway
- Handles session lifecycle directly (list, switch, new, delete, rename) via `MobileSessionStore`
- Translates `AgentEventStream` events into sequenced `evt` frames
- Buffers recent events (last 1000) for reconnection replay
- Forwards device command requests from tools to the connected client
- Shares the same `AgentDefinition` objects as Gateway, so tool configuration, models, and prompts stay consistent

This separation is intentional: `MobileTransport` needs multi-session and multi-agent support that doesn't fit the existing one-transport-one-agent `ChannelBinding` model. Gateway continues to handle Telegram and Console unchanged.

**`MobileSessionStore`** — extends the session concept for multi-session support. Unlike `FileSessionStore` (one session per transport+peer), this stores multiple named sessions per agent+peer:
- Storage path: `~/.achates/agents/{agentName}/sessions/mobile/{peerId}/{sessionId}.json`
- Each session file contains a JSON object with metadata header and message array:
  ```json
  {
    "id": "<uuid>",
    "title": "Calendar & email check",
    "created": "2026-03-16T10:00:00Z",
    "updated": "2026-03-16T10:05:00Z",
    "messages": [...]
  }
  ```
- `ListAsync(agentName, peerId)` — returns session metadata (reads headers only, not full message arrays) sorted by last updated
- `LoadAsync(agentName, peerId, sessionId)` — returns full session with messages
- `SaveAsync(agentName, peerId, sessionId, session)` — writes session to disk
- `DeleteAsync(agentName, peerId, sessionId)` — removes session file
- `UpdateMetadataAsync(agentName, peerId, sessionId, title)` — updates title without rewriting messages

**`DeviceCommandBridge`** — singleton mediates between server-side tools and the connected iOS client. Holds a reference to the active `MobileTransport` connection (if any). When a tool needs device data:
1. Bridge checks if a mobile client is connected with the required capability
2. Sends an RPC frame to the client via `MobileTransport`
3. Awaits the response with a timeout (15s location, 30s camera)
4. Returns the result to the tool
5. If no mobile client is connected, returns error with code `DEVICE_UNAVAILABLE`

**`LocationTool`** — `AgentTool` subclass. Parameters: none. Calls `DeviceCommandBridge` to request GPS coordinates. Returns lat/lon/accuracy/timestamp as text. Always present in agent's tool list when configured; returns a clear error message if no mobile device is connected (the agent can explain this to the user).

**`CameraTool`** — `AgentTool` subclass. Parameters: `facing` (front/back, default back). Calls `DeviceCommandBridge` to request a photo. Receives base64 JPEG (max ~500KB), returns as `CompletionImageContent`. Always present when configured; returns error if no mobile device is connected.

**Session Management RPCs** — handled directly by `MobileTransport`, delegating to `MobileSessionStore`:
- `sessions.list` returns metadata for all sessions (id, title, preview, timestamp, message count)
- `sessions.new` creates a new session, switches the active `AgentRuntime` to it
- `sessions.switch` loads a different session's messages into a new `AgentRuntime`
- `sessions.rename` updates title via `MobileSessionStore.UpdateMetadataAsync`
- `sessions.delete` removes session file and discards runtime if active

**Session Naming** — after the first exchange in a new session completes (`message.end`), the server fires an async LLM call to generate a short title from the first user message + assistant response. Stored as metadata in the session JSON. Client receives a `session.renamed` event.

**`PushNotificationService`** — singleton responsible for sending APNs notifications. Initialized at startup if `apns` config is present.
- Stores device tokens per peer (persisted at `~/.achates/apns-tokens.json`)
- Tokens registered during `connect` handshake via `MobileTransport`
- `SendAsync(peerId, agentName, sessionId, preview)` — sends a push notification
- Uses HTTP/2 APNs provider API with JWT authentication (from `.p8` key file)
- Called by `CronService` when delivering a cron job result and `MobileTransport` has no active connection for that peer
- In the future, a `notify` tool could also use this service

Integration with `CronService`: After `CronService` delivers a cron result via the transport, if the transport's `SendAsync` fails (no active connection), `CronService` falls back to `PushNotificationService`. This requires `CronService` to receive `PushNotificationService` as a dependency.

### Endpoint

New WebSocket endpoint mapped in `Program.cs`:
```
/ws/v2  — frame-based mobile protocol
```

Existing endpoints unchanged:
```
/ws     — plain-text Console protocol
/health — health check
/admin  — Blazor admin UI
```

## iOS App Architecture

### Project Structure

```
Achates/
  App/
    AchatesApp.swift           — entry point, app lifecycle
    AppState.swift             — @Observable root state
  Connection/
    WebSocketClient.swift      — URLSessionWebSocketTask, reconnection logic
    RPCClient.swift            — request/response correlation, pending request map
    EventStream.swift          — sequenced event handling, seq tracking
  Models/
    Agent.swift                — agent info (name, description, tools)
    Session.swift              — session metadata (id, title, preview, timestamp)
    Message.swift              — chat message with content blocks
    Frame.swift                — protocol frame types (Codable)
  Views/
    AgentListView.swift        — root screen, "contacts" list
    SessionListView.swift      — per-agent session list
    ChatView.swift             — main chat interface, message list + composer
    MessageBubble.swift        — user/assistant bubble rendering
    ThinkingView.swift         — collapsible thinking block
    ToolCallView.swift         — compact tool call pill, expandable
    ComposerView.swift         — text input, mic button, send/stop button
    SettingsView.swift         — server URL configuration
  Services/
    DeviceCommandRouter.swift  — dispatches server RPC to device services
    LocationService.swift      — CLLocationManager wrapper
    CameraService.swift        — AVCaptureSession, JPEG compression
    SpeechService.swift        — SFSpeechRecognizer for voice input
    NotificationService.swift  — APNs registration and handling
```

### State Management

Single `@Observable` class `AppState` as the root:

```
AppState
├── connectionStatus: ConnectionStatus (.connected, .connecting, .offline)
├── serverURL: URL
├── agents: [Agent]
├── currentAgent: Agent?
├── sessions: [Session]          // for current agent
├── currentSession: Session?
├── messages: [Message]          // for current session
├── isStreaming: Bool
├── streamingText: String
├── streamingThinking: String?
├── activeToolCalls: [ToolCall]
```

Views observe `AppState` directly. The connection layer updates `AppState` in response to events.

### Screen Flow

```
Settings (first launch) → Agent List (root) → Session List → Chat
```

- **Settings:** Server URL entry, connection test. Shown on first launch or from gear icon.
- **Agent List:** Agents displayed as contacts — avatar (initial + gradient), name, description, last message preview, timestamp, unread badge. Tapping navigates to session list.
- **Session List:** Per-agent conversations — auto-generated title, preview, message count, timestamp. "+ New" button to create. Swipe to delete. Active session highlighted. Pull to refresh.
- **Chat:** Full conversation with streaming. Navigation bar shows agent name and connection status.

### Chat UI

**Message types rendered:**
- **User messages:** Right-aligned purple bubbles
- **Assistant messages:** Left-aligned dark bubbles with markdown rendering (bold, italic, lists, code blocks with syntax highlighting, inline code)
- **Thinking blocks:** Collapsed by default, tap to expand. Shows duration. Italic text with left border.
- **Tool calls:** Compact pills showing tool name, action, status (spinner while running, checkmark when done), duration. Tap to expand and see arguments + result.

**Composer:**
- Text field with placeholder, grows vertically for multi-line
- Mic button: tap to start voice recognition, tap again to stop. Live transcription appears in the text field. User can edit before sending.
- Send button: disabled when empty, purple when active
- During agent response: send button becomes a stop button (square icon) to cancel via `chat.cancel`

**Streaming:**
- Text appears character-by-character with a blinking cursor
- "Responding..." indicator with stop button below the streaming bubble
- Tool calls appear inline as they execute

### Device Services

**DeviceCommandRouter** receives `req` frames from the server and dispatches:

**Location (`device.location`):**
- Uses `CLLocationManager` with `kCLLocationAccuracyHundredMeters`
- Returns `{lat, lon, accuracy, timestamp}` in response frame
- Timeout: 15s
- Permission: requested during onboarding, not during command execution

**Camera (`device.camera`):**
- Uses `AVCaptureSession` to capture a still photo
- Compresses to JPEG, max ~500KB
- Base64 encodes and returns in response frame
- `facing` param selects front/back camera
- Timeout: 30s
- Permission: requested on first use (iOS allows this for camera)

**Permission Model:**
- Location permission requested during onboarding (always-when-in-use is sufficient)
- Camera permission requested on first device command (standard iOS pattern)
- If permission denied, response frame returns error with code `PERMISSION_DENIED`

### Voice Input

- `SFSpeechRecognizer` with on-device recognition (no network required on modern iOS)
- Tap mic → start recognition → live transcription fills text field → tap mic to stop → user can edit → tap send
- Requires microphone + speech recognition permissions (requested on first use)

### Push Notifications

- App registers for remote notifications on launch
- Device token sent to server during `connect` handshake
- When notification received and app is backgrounded: badge the app icon
- When notification tapped: open app, navigate to the relevant agent + session
- Notification payload: `{"agent": "paul", "session_id": "...", "preview": "Your cron job completed..."}`

### Connection Resilience

- `URLSessionWebSocketTask` for the WebSocket connection
- Exponential backoff on disconnect: 500ms, 1s, 2s, 4s, 8s, 16s, 30s (max)
- On app foreground after ≥3s background: send `ping` RPC, reconnect if no pong within 2s
- Sequence tracking: store `last_seq`, send on reconnect for event replay
- If server can't replay (buffer flushed), reload session via `sessions.switch`
- Connection status reflected in UI (green dot / "Connecting..." / "Offline")

## Configuration Changes

New section in `~/.achates/config.yaml`:

```yaml
apns:
  key_file: ~/.achates/apns-key.p8    # APNs auth key
  key_id: ABC123DEF4                    # Key ID from Apple
  team_id: TEAM123456                   # Apple Developer Team ID
  bundle_id: com.achates.app            # iOS app bundle identifier
  environment: development              # or "production"
```

Session storage path for mobile transport:
```
~/.achates/agents/{agentName}/sessions/mobile/{peerId}/{sessionId}.json
```

Session metadata (title, created, updated) stored as fields in the session JSON file alongside the messages array. See `MobileSessionStore` above for the format.

## Data Paths (New)

```
~/.achates/agents/{agentName}/sessions/mobile/{peerId}/{sessionId}.json  — mobile session history
~/.achates/apns-key.p8                                                    — APNs auth key file
```

## Design Notes

- **`agents.list` vs `connect` response:** The `connect` response includes the agent list for the initial render. `agents.list` exists for refreshing without reconnecting (e.g., if server config changes while connected). Both return the same data.
- **Voice input vs `TranscribeTool`:** These are fully independent. Voice input is client-side only (`SFSpeechRecognizer` → text → send as message). `TranscribeTool` is server-side (transcribes audio files like iMessage voice messages). They don't interact.

## Open Questions

- **Authentication:** The current system has no auth — anyone who can reach the server can connect. For a mobile app on a real network, we'll likely want at minimum a shared secret or API key. Not blocking v1 if the server is on a private network.
- **Image handling in session history:** When the camera tool returns image data, how is it stored in the session JSON? Base64 inline (large files) vs. saved to disk with a reference. Can defer this decision to implementation.
- **Multiple simultaneous connections:** If the iOS app and Telegram are both connected for the same agent, how do notifications route? Current design: push notifications only fire when no WebSocket is connected. Telegram messages go through Telegram as usual. The mobile app only sees its own sessions.
