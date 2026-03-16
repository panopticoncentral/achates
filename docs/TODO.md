# Achates — Future Work

## iOS App — Stabilization

- [ ] Verify tool call events align between server and client (server sends `tool_call_id`/`tool_name`, client expects `id`/`name`)
- [ ] Verify session history loads when resuming a session (`sessions.switch` response shape vs client parsing)
- [ ] Add `Description` field to `AgentDefinition` (carry from `AgentConfig.Description` through startup) so agents show descriptions in the iOS app
- [ ] Test voice input (`SpeechService`) on a real device
- [ ] Test location and camera device commands end-to-end (server tool → iOS service → response)
- [ ] Markdown rendering in chat bubbles (currently plain text)

## iOS App — Features

- [ ] **Push notifications (APNs):** Server `PushNotificationService` and iOS `NotificationService` are scaffolded. Needs Apple Developer account setup, `.p8` key, and `apns` config section in `config.yaml`. Wire `CronService` fallback to push when no WebSocket connected.
- [ ] **Event replay on reconnect:** Server tracks `seq` numbers but has no replay buffer. Client sends `last_seq` on reconnect but server ignores it. Add a ring buffer (last 1000 events) and replay on reconnect.
- [ ] **Session auto-naming:** Server-side `AutoNameSessionAsync` is implemented. Verify it fires after first exchange and `session.renamed` event reaches the client.
- [ ] **Admin features (v2):** Cost viewing, memory editing, cron job management from the app. Currently chat-only by design.
- [ ] **TTS / voice output (v2):** System `AVSpeechSynthesizer` or ElevenLabs for agent responses read aloud.
- [ ] **Full conversational voice mode (v3):** Continuous listening, streaming TTS, barge-in support.

## Server — Security

- [ ] **Authentication for `/ws/v2`:** Currently no auth — anyone on the network can connect. Options: shared secret/API key in connect handshake, or token-based auth. Not urgent if server is on a private network.
- [ ] **Image storage in sessions:** Camera tool returns base64 inline in session JSON, which bloats session files. Consider saving images to disk with a reference in the session.

## Server — General

- [ ] **Multiple providers:** Only OpenRouter is implemented. Consider adding direct Anthropic, OpenAI, Google providers.
- [ ] **Web UI client:** The Blazor admin UI could be extended into a full chat client, similar to the iOS app but browser-based.
- [ ] **Android app:** The `/ws/v2` protocol is platform-agnostic. A Kotlin/Jetpack Compose client would follow the same architecture.
- [ ] **Multi-user support:** Currently single-user. If multiple people connect, they share the same agent state. Would need user accounts and per-user sessions.
- [ ] **Plugin system for tools:** Tools are currently compiled into the server. A plugin architecture (load from DLLs or scripts) would allow adding tools without rebuilding.
- [ ] **`${ENV_VAR}` expansion in config:** CLAUDE.md notes this isn't supported yet. Would simplify config management.

## Protocol

- [ ] **Simultaneous connections:** If iOS and Telegram are both connected, how do notifications and device commands route? Current design: push only fires when no WebSocket connected, Telegram is independent. May need explicit routing rules.
- [ ] **Binary frames:** Consider supporting binary WebSocket frames for image data instead of base64-encoding everything as JSON. Would reduce bandwidth ~33%.
- [ ] **Typing indicators over v2 protocol:** The v1 protocol sends `{"type":"typing"}` events. The v2 protocol doesn't have an equivalent. Consider adding for UI polish.
