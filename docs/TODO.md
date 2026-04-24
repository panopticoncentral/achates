# Achates — Future Work

## Apple App — Stabilization

- [ ] Verify tool call events align between server and client (server sends `tool_call_id`/`tool_name`, client expects `id`/`name`)
- [ ] Test voice input (`SpeechService`) on a real device
- [ ] Test location and camera device commands end-to-end (server tool → Apple app service → response)
- [ ] Markdown rendering in chat bubbles (currently plain text)

## Apple App — Features

- [ ] **Push notifications (APNs):** Server `PushNotificationService` and app `NotificationService` are scaffolded. Needs Apple Developer account setup, `.p8` key, and `apns` config section in `config.yaml`. Wire `CronService` fallback to push when no WebSocket connected.
- [ ] **Event replay on reconnect:** Server tracks `seq` numbers but has no replay buffer. Client sends `last_seq` on reconnect but server ignores it. Add a ring buffer (last 1000 events) and replay on reconnect.
- [ ] **TTS / voice output:** System `AVSpeechSynthesizer` or ElevenLabs for agent responses read aloud.
- [ ] **Full conversational voice mode:** Continuous listening, streaming TTS, barge-in support.

## Server — Security

- [ ] **Authentication for `/ws`:** Currently no auth — anyone on the network can connect. Options: shared secret/API key in connect handshake, or token-based auth. Not urgent if server is on a private network.
- [ ] **Image storage in sessions:** Camera tool returns base64 inline in session JSON, which bloats session files. Consider saving images to disk with a reference in the session.

## Server — General

- [ ] **Multiple providers:** Only OpenRouter is implemented. Consider adding direct Anthropic, OpenAI, Google providers.
- [ ] **Web UI client:** A browser-based chat client on top of the existing WebSocket protocol. (The previous Blazor admin was retired in favour of folding management into the Apple app — see commit 91d6c07.)
- [ ] **Android app:** The WebSocket protocol is platform-agnostic. A Kotlin/Jetpack Compose client would follow the same architecture.
- [ ] **Multi-user support:** Currently single-user. If multiple people connect, they share the same agent state. Would need user accounts and per-user sessions.
- [ ] **Plugin system for tools:** Tools are currently compiled into the server. A plugin architecture (load from DLLs or scripts) would allow adding tools without rebuilding.
- [ ] **`${ENV_VAR}` expansion in config:** CLAUDE.md notes this isn't supported yet. Would simplify config management.
- [ ] **Retire the empty `ios/` directory:** Superseded by `apple/`; only an empty `.xcodeproj` remains.

## Protocol

- [ ] **Binary frames:** Consider supporting binary WebSocket frames for image data instead of base64-encoding everything as JSON. Would reduce bandwidth ~33%.
- [ ] **Typing indicators:** The mobile protocol has no typing event. Consider adding for UI polish.
