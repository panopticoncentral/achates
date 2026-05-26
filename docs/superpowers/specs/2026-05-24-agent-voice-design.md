# Agent Voice — Design

**Status:** Design approved, ready for implementation planning
**Date:** 2026-05-24
**Owner:** paulv
**Tracking branch:** `feature/agent-voice`

## Summary

Give Achates agents a voice. The agent generates text on its normal model as
today; a local TTS engine (Kokoro) synthesizes each completed sentence into
audio, which the iOS app plays back via `AVQueuePlayer`. Voice is part of agent
identity (configured in `AGENT.md`), playback is opt-in per session, and the
entire pipeline runs locally so nothing about a conversation is sent to a cloud
TTS vendor.

## Goal

**A** — Read-aloud: every assistant reply can be spoken aloud as it streams.

**C** — Per-agent voice identity: each agent has a distinct, configured voice
(e.g. Paul sounds like `af_nicole(0.6)+af_sky(0.4)` everywhere).

**B (talk mode)** is the long-term north star and is *explicitly out of scope*
for this design. Section "Forward compatibility" confirms B can be added later
without rework.

## Constraints

Two non-negotiables drove most of the design:

1. **Privacy.** Conversations must not leave the user's machine. The user
   already routes LLM traffic through OpenRouter's ZDR option for the same
   reason; the voice path must be at least as private.
2. **No content moderation.** A spicy passage must not get refused by the TTS
   provider. The user has personal use cases (roleplay) that any moderated
   cloud TTS would block.

These two constraints rule out OpenAI TTS (both), ElevenLabs (moderation is
permissive but cloud is still cloud), and OpenRouter's native-audio chat models
(OpenAI moderation applies). The only option that fully satisfies both is
**local TTS**.

Within local TTS, Kokoro was chosen over the "strong local" tier
(F5-TTS, Chatterbox, XTTS-v2) because:

- Kokoro ships ~67 polished, pre-tuned voices + arbitrary blends.
- "Strong local" engines clone from a reference clip, which gives unbounded
  variety but inconsistent quality and requires the user to source/curate
  reference audio.
- Hands-on testing confirmed Kokoro's curated voices clear the user's quality
  bar (`af_nicole` landed first try), and blending gives effectively unlimited
  in-style variety.

## Architecture

```
┌──────────────────────────┐    WSS    ┌──────────────────────────┐
│  iOS App                 │ ◄───────► │  Achates.Server          │
│  • AVQueuePlayer         │  audio.*  │  • MobileTransport       │
│  • per-session "speak"   │   events  │  • SpeechBroker          │
│    toggle                │           │  • ISpeechSynthesizer    │
└──────────────────────────┘           └────────────┬─────────────┘
                                                    │ HTTP localhost:8880
                                                    ▼
                                        ┌──────────────────────────┐
                                        │  kokoro-fastapi sidecar  │
                                        │  • auto-launched by      │
                                        │    GatewayService        │
                                        └──────────────────────────┘
```

Speech is a **parallel path** alongside the existing text streaming, not a
replacement. Text events (`text.delta`, `text.end`, …) continue to be emitted
exactly as today. When speech is enabled for a session and the agent has a
voice, the assistant text stream is also teed into `SpeechBroker`, which
synthesizes each completed sentence and emits new `audio.block` events on the
same WebSocket. Tools, thinking blocks, and inter-agent chat continue to behave
exactly as they do now.

The existing `CompletionAudioContent` / `CompletionAudioOptions` plumbing in
`Achates.Providers` (built for native-audio chat models like `gpt-4o-audio`)
is **not** used by this design and stays untouched. It remains available for a
future talk-mode path if we ever want to A/B against a true audio model.

## Code Layout

Mirrors the existing pattern of bounded service clients (`WithingsClient`,
`GraphClient`):

```
src/Achates.Server/Speech/
├── ISpeechSynthesizer.cs       interface — engine-swap seam (ElevenLabs,
│                               gpt-4o-audio, etc. for future)
├── KokoroSpeechSynthesizer.cs  HTTP client for kokoro-fastapi
├── KokoroSidecarProcess.cs     IHostedService — child-process lifecycle
├── SpeechBroker.cs             per-turn orchestration:
│                               text deltas → sentences → audio events
├── SentenceSegmenter.cs        pure unit: streaming-text → sentences
└── SpeechSanitizer.cs          pure unit: strips code, URLs, markdown noise
```

`SpeechBroker` is intentionally separate from `MobileTransport` (which is
already ~1500 lines). The broker subscribes to the assistant text stream for
one turn, accumulates deltas, runs them through the sanitizer and segmenter,
fans completed sentences into the synthesizer, and forwards the resulting
audio events back through the transport.

`SentenceSegmenter` and `SpeechSanitizer` are pure functions / small stateful
units, easy to unit-test independently.

## Per-Agent Voice Config

### AGENT.md capability

New scalar capability matching `Model` / `Reasoning Effort` style:

```markdown
## Capabilities

**Voice:** af_nicole
```

Blend syntax accepted verbatim:

```markdown
**Voice:** af_nicole(0.6)+af_sky(0.4)
```

The capability is **optional**. Omitting it means the agent is *voiceless* —
no audio is generated for sessions involving this agent, even when the
per-session toggle is on. This is intentional: voice is part of identity, and
we don't surprise the user with a default voice they didn't pick.

### `AgentDefinition` addition

`AgentDefinition.Voice` (`string?`), populated by `AgentLoader` from the
capability.

Validation:

- **Soft-validate at load time** against the sidecar's `/v1/audio/voices`
  endpoint *when available*. Warn on unknown voices but don't fail agent
  load — the sidecar may not be up at startup.
- **Final validation at first synthesis call** — a bad voice surfaces as a
  sidecar error. `SpeechBroker` treats it like any other synth failure:
  emits `audio.error` for the affected turn (see Transport Protocol), logs
  it server-side, and the turn's text continues streaming normally.
- Blend syntax is passed through verbatim; the sidecar handles syntax errors,
  which we surface the same way.

### Global default (opt-in escape hatch)

`tools.speech.default_voice` in `config.yaml`. Off by default. When set,
agents without an explicit `**Voice:**` capability fall through to this
voice. Useful during initial rollout to avoid editing every agent's AGENT.md
at once.

### Hot reload

Voice changes in `AGENT.md` flow through the existing `AgentLoader` →
`ReloadAgentAsync` pipeline (same path as Model / Tools / Allowed Chats
edits). New voice takes effect on the next turn; no session reset needed.

### Tool integration

Extending existing tools — no new tools introduced:

- **`ProfileTool`** — `get`/`update` extended to include `voice`. Agents can
  change their own voice, consistent with letting them edit their own
  description, prompt, and avatar.
- **`AgentManagerTool`** — `read`/`modify`/`create` extended to include
  `voice`. Humans manage voices via the iOS agent-edit sheet.

## What Gets Spoken

### Content sanitization (`SpeechSanitizer`)

A small stateful pass over each completed sentence. Not a full Markdown
parser — we keep this simple until it proves insufficient.

| Element | Behavior | Why |
|---|---|---|
| Plain prose | speak as-is | the point |
| Bold / italic (`**foo**`, `_foo_`) | speak content, drop marks | natural reading |
| Headers (`# foo`) | speak content, drop `#` | natural reading |
| Code fences (` ``` ` blocks) | **entire block silently skipped** | reading code aloud is misery |
| Inline code (`` `foo` ``) | skip content | `foo.bar.baz()` aloud is awful |
| Links (`[text](url)`) | speak `text` only | URLs aloud = noise |
| Bare URLs (`http://…`) | skip entirely | same |
| Images (`![alt](url)`) | skip entirely | not speakable |
| Tables | skip the entire table | structure doesn't linearize |
| Math (`$$…$$`) | skip | not speakable |
| Emoji | strip | inconsistent pronunciation |
| Horizontal rules, blockquote marks | drop syntax, keep content | natural reading |

**No "code block omitted" announcement.** Silence is less jarring than the
robot apologizing.

### Sentence segmentation (`SentenceSegmenter`)

Watches the raw text-delta stream for sentence-ending punctuation
(`.`, `!`, `?`) followed by whitespace or end-of-stream. Small abbreviation
guard list (`Dr.`, `Mr.`, `Mrs.`, `i.e.`, `e.g.`, `etc.`, `vs.`) prevents
false splits.

Safety valves:

- **Max-chars flush**: if no sentence boundary appears within ~280 chars
  (long winding sentences), flush what we have. Better an awkward seam than
  8 seconds of nothing.
- **End-of-stream flush**: any trailing prose is sent as a final sentence,
  even without terminal punctuation.
- **Code-fence awareness**: while inside a ` ``` ` block, sentence boundaries
  are not detected (and the content's being skipped anyway).

### Pipelining

Sentence N is synthesized while the LLM is producing sentence N+1. Sentences
are emitted to the transport in `sentence_index` order — `SpeechBroker`
maintains a sequential queue so audio plays correctly. On M4 CPU + Kokoro,
expected RTF is ~0.3–0.5, so synthesis comfortably outpaces both the LLM and
playback.

### What's never spoken (excluded by construction)

- **Tool calls / results** — separate message types, never in `text.delta`.
- **Thinking blocks** — separate `thinking.delta` stream.
- **Inter-agent chat replies during a turn** — the target agent's
  `agent_turn.*` events are *not* fed to the speech path. **One voice per
  turn** (the agent the user is talking to). The target's reply still
  appears on screen as today.

### Cancellation

When a user cancels a turn (`chat.cancel`), `SpeechBroker` cancels the active
synthesis HTTP request and stops dispatching queued sentences. The iOS player
drains its current buffer and stops.

## Transport Protocol

### New events

One streaming event for audio data, one for error reporting. Both use
JSON+base64 (matches existing `image.block` pattern — bandwidth is not a
concern over local WiFi):

```jsonc
{
  "type": "audio.block",
  "turn_id": "turn_abc123",
  "sentence_index": 0,            // 0, 1, 2… within this turn
  "voice": "af_nicole",           // for client display / debug
  "format": "mp3",
  "data": "<base64>",
  "text": "Hello, how can I help?" // sanitized text that was spoken
}
```

```jsonc
{
  "type": "audio.error",
  "turn_id": "turn_abc123",
  "sentence_index": 2,            // omitted = whole-turn error
  "message": "kokoro sidecar unreachable"
}
```

The `text` field lets the iOS client correlate audio to specific spans of
the assistant message (foundation for highlight / karaoke later) and is
invaluable for debugging.

### Granularity: per-sentence

One `audio.block` per complete sentence. Sub-sentence chunked deltas were
considered but deferred — on M4 + Kokoro, a typical sentence synthesizes in
~0.5–1s, and the protocol/client complexity of streaming deltas isn't
justified for that latency saving. Easy to upgrade later if needed (new
event types, no breaking change).

### Event sequence (typical turn)

```
→ chat.send {session_id, text: "Tell me about Kokoro."}
← text.delta "Kokoro is a small..."
← text.delta " open-source TTS model."          [sentence 0 → Kokoro]
← text.delta " It runs on Apple Silicon."       [sentence 1 → Kokoro]
← audio.block {sentence_index: 0, data: "<mp3>"}   [client plays s0]
← text.delta " Voices are pre-trained."         [sentence 2 → Kokoro]
← audio.block {sentence_index: 1, data: "<mp3>"}
← text.end
← audio.block {sentence_index: 2, data: "<mp3>"}
← message.end
← done
```

Expected end-to-end "user submit → first audio" latency: 2–4s.

### New RPCs

| RPC | Purpose |
|---|---|
| `session.set_speech` | `{session_id, enabled}` → flips the per-session toggle; broadcasts `session.updated` so other connected devices stay in sync. |
| `voices.list` | Passes through to sidecar `/v1/audio/voices`; returns `[]` if sidecar not running (so the iOS picker degrades gracefully). |

### `MobileSession` additions

New persisted field `speech_enabled: bool` (default `false`). Returned in
`session.get` / `session.list`. Existing sessions on disk get the default on
load, so the change is backward-compatible without migration.

### Error semantics

Speech failures never abort the turn. If the sidecar is down or a sentence
fails to synthesize:

- Log server-side.
- Emit `audio.error` once per affected turn (or per failed sentence if
  mid-turn).
- Text continues streaming normally — the user always gets the assistant's
  response; audio is best-effort.
- Client shows a subtle "🔇 speech unavailable" chip on the affected
  message — same pattern as image-generation failures today.

### Cost ledger

No speech traffic in the cost ledger. It's free local compute — recording
zero-cost rows would be noise. The ledger stays focused on what costs money
(model + image API calls).

## Sidecar Lifecycle

### Two modes, both supported

| Mode | Config | Use case |
|---|---|---|
| **Managed** | `tools.speech.sidecar` block | "It just works" — Achates spawns and supervises |
| **External** | `tools.speech.endpoint` only | You run the sidecar yourself (Docker, dev loop, shared instance) |

If both are specified, `endpoint` wins (no auto-launch) and a warning is
logged. Useful when iterating on the sidecar separately.

### `KokoroSidecarProcess` (IHostedService)

Owned by the same DI container as `GatewayService`. Conditional on
`tools.speech` being configured, same pattern as the existing `CronService`
registration.

**Startup:**

- If `tools.speech` is absent → no-op. Speech is disabled. `voices.list`
  returns `[]`.
- If `endpoint` only → assume external; just health-check it.
- If `sidecar` configured → spawn `command + args` in `working_dir`. Redirect
  stdout/stderr into the Achates log, prefixed `[kokoro]` for grep-ability.
- Poll `GET /health` every 500 ms until 200 OK or 60s timeout.
- On timeout: log error, mark `ISpeechSynthesizer.IsAvailable = false`,
  leave the server running. Other Achates functionality unaffected.

**Steady state:**

- Monitor process exit. On unexpected exit: log stderr tail, mark
  unavailable, schedule restart with backoff (1s → 5s → 30s → 5min, then
  steady at 5min).
- `IsAvailable` flips back to true only after a successful health check
  post-restart.

**Shutdown:**

- Send SIGTERM, wait 5s grace, SIGKILL if still alive. Clean shutdown is
  important so port 8880 doesn't linger held.

### Failure handling at the request level

When a speech request comes in and `IsAvailable` is false:

- `SpeechBroker` emits `audio.error` once per turn, type "sidecar
  unavailable".
- Text streaming continues unaffected.
- iOS shows the subtle "🔇 speech unavailable" chip on affected messages.

### Port conflict & misconfig handling

- **Port 8880 already in use at startup** → log clearly, disable speech,
  *do not* try a different port (predictability beats auto-fallback magic).
- **`working_dir` missing** → log a clear hint pointing at
  `docs/speech-setup.md`. Disable speech, keep server running.
- **Command fails immediately** → log stderr, same disable-with-clear-message
  pattern. Backoff applies.

### First-run experience (follow-up, not blocking this design)

A `scripts/install-speech-sidecar.sh` would handle the one-time setup
(`git clone` + `uv sync` + `download_model.sh` + scaffold a working `.env`).
Worth a follow-up issue but not required for v1 — the manual install path is
documented in `docs/speech-setup.md`.

## iOS Playback

### Player choice

`AVQueuePlayer` with `AVPlayerItem`s. Each `audio.block` event's MP3 is
written to a temp file in `NSTemporaryDirectory()` and enqueued. The player
drains the queue in order — synth pipelining ensures sentence N+1 is ready
by the time N finishes. Temp files are cleaned up via KVO on `currentItem`
advance.

(`AVAudioPlayer(data:)` would let us skip the temp-file dance but has no
queue support — manual chaining of "play next on finish" is more code and
worse than `AVQueuePlayer`'s built-in behavior.)

### Audio session

```swift
try AVAudioSession.sharedInstance().setCategory(
    .playback,
    mode: .spokenAudio,
    options: [.duckOthers]
)
```

- **`.playback`** — plays through speaker even with mute switch on (mute is
  for keyboard taps, not media — Apple's own convention).
- **`.spokenAudio`** — tells iOS this is voice content; tunes AirPods
  routing, ducks other audio politely.
- **`.duckOthers`** — quiet down music in the background instead of
  stopping it.

`audio` background mode added to `Info.plist` so playback continues when the
user backgrounds the app — walking into another room mid-response shouldn't
kill the audio.

### Interruption handling

`AVAudioSession.interruptionNotification`:

- `.began` (phone call, Siri) → pause player.
- `.ended` → don't auto-resume; the message stays where it is, user can tap
  replay if they want.

### Cross-turn cleanup

- New user message while previous is still speaking → clear queue, stop
  playback, fresh start.
- User cancels a turn → same.
- Switch sessions → clear queue, drop pending temp files.

### UI surfaces

**Chat view nav bar:**

- 🔊 / 🔇 toggle for the per-session speech flag. Tap → sends
  `session.set_speech` RPC, updates locally on ack.
- When toggle is on but the active agent is voiceless: icon shows a
  different state, tap shows a hint to set the agent's voice in the edit
  sheet.

**Assistant message bubble:**

- Small speaker icon at the bottom of each spoken assistant message.
  States: queued / playing / played / failed.
- Tap = replay (re-enqueues that message's sentences using the cached
  `text` field from `audio.block` events).
- Failed messages show the "🔇 speech unavailable" chip from
  `audio.error`.

**Agent edit sheet (new section):**

- "Voice" picker populated from `voices.list` at sheet-open. Empty option =
  voiceless.
- Power-user "Custom blend" text field accepting `af_nicole(0.7)+af_bella(0.3)`
  syntax.
- "Preview" button → synthesizes a short fixed phrase ("Hello, this is how
  I sound") and plays through the player. Validates the voice and lets you
  A/B before saving.

### Routing

Default iOS audio routing — AirPods if connected, speaker otherwise. No
app-level override; trust the user's hardware choice.

### iOS code placement

```
apple/Achates/
├── Services/
│   ├── SpeechService.swift           ← existing (input/STT; reserved for B)
│   ├── SpeechPlayer.swift            ← NEW: AVQueuePlayer wrapper
│   └── VoiceRegistry.swift           ← NEW: caches voices.list response
├── Views/
│   ├── ChatView.swift                ← speak toggle in nav bar
│   ├── MessageBubbleView.swift       ← per-message play/replay control
│   └── AgentEditView.swift           ← voice picker section
└── Models/
    └── Session.swift                 ← speech_enabled field
```

`SpeechService` (input/STT) and `SpeechPlayer` (output/TTS) are
complementary — both will be active when talk mode is built.

### Deferred to a later iteration (good YAGNI for v1)

- Per-message progress bar / waveform / karaoke-style text highlighting.
- Voice cloning / custom voice upload.
- Per-session speed control.
- STT input integration (that's the B phase).

## Config Schema

### `~/.achates/config.yaml` additions

```yaml
tools:
  speech:
    # Mode A: managed sidecar (Achates spawns and supervises it)
    sidecar:
      working_dir: ~/kokoro-fastapi
      command: uv
      args: [run, uvicorn, api.src.main:app, --host, "127.0.0.1", --port, "8880"]

    # Mode B: external sidecar (you manage it; Docker, dev loop, shared instance)
    # endpoint: http://127.0.0.1:8880

    # Optional: global default voice for agents that don't declare **Voice:**
    # Off by default — voiceless agents stay silent.
    # default_voice: af_nicole
```

### Field semantics

| Field | Behavior |
|---|---|
| `tools.speech` absent | Speech fully disabled. No sidecar launched, `voices.list` returns `[]`, iOS toggle grayed out. Existing behavior unchanged for non-opt-in users. |
| `tools.speech` present but empty `{}` | Same as absent — treated as misconfigured, speech disabled, warning logged. |
| `tools.speech.sidecar` only | Managed mode. Endpoint derived from `--port` in args (default `http://127.0.0.1:8880`). |
| `tools.speech.endpoint` only | External mode. No process launched, just health-checked. |
| both | Endpoint wins; sidecar block is ignored (with a warning). |
| `tools.speech.default_voice` | Used when an agent has no explicit `**Voice:**`. Off by default. |

### Validation at config load

- Both `sidecar` and `endpoint` missing inside `tools.speech` → load-time
  warning, speech disabled, server starts normally.
- `sidecar.working_dir` missing → load-time warning logged with the path;
  startup will retry per the lifecycle policy above.
- `default_voice` unknown to the sidecar → soft-validated at first
  `/v1/audio/voices` fetch. Logs a warning naming the voice; agents fall
  through to "voiceless" instead.

### No environment variables

Local Kokoro needs no API keys. The schema is structured so adding
alternative synthesizer types later (e.g., `engine: elevenlabs` with an
`api_key` field) is a non-breaking extension. Keys would follow the existing
pattern: `tools.speech.api_key` with env var fallback like `tools.image.api_key`.

## Documentation Deliverables

Per the rule at the top of `CLAUDE.md`, this PR must update the project's
living documentation to match the new behavior:

- **`CLAUDE.md`** — add `tools.speech` to the config example; add `**Voice:**`
  capability to the AGENT.md example; add `src/Achates.Server/Speech/`
  subsection under Server; update the MobileTransport events list with
  `audio.block` / `audio.error`; add the new RPCs (`session.set_speech`,
  `voices.list`); add `MobileSession.speech_enabled` field note.
- **`docs/configuration.md`** — new section explaining the `tools.speech`
  block end-to-end.
- **`docs/speech-setup.md`** (new) — one-time setup recipe (install
  `kokoro-fastapi`, the `.env`, troubleshooting).
- **`README.md`** — brief "voice supported" mention pointing at
  `docs/speech-setup.md`.

## Forward Compatibility (B / Talk Mode)

The v1 design (A + C) sets up B (full talk mode) as an additive extension,
not a rewrite.

### What carries over unchanged

- **`AGENT.md` voice config** — same `**Voice:**` capability, same per-agent
  semantics.
- **`ISpeechSynthesizer`** — abstraction stays; talk mode might *also*
  register an alternative implementation (e.g., gpt-4o-audio for lower
  latency) selectable per session.
- **`SpeechPlayer`** (iOS) — same `AVQueuePlayer` foundation; just needs the
  audio session reconfigured for `.playAndRecord` when talk mode is active.
- **`SpeechService`** (iOS) — already exists today for STT; B wires it.
- **`SpeechBroker`** — same text→sentence→synth pipeline. STT-originated
  turns flow through the existing `chat.send` path.
- **WebSocket protocol** — bidirectional already; B would add
  `audio.input.*` events for inbound audio, but the frame model is the same.

### What B adds (deliberately out of scope for v1)

| Need | Why deferred |
|---|---|
| **Barge-in** (cancel agent reply when user speaks) | Requires VAD + always-on mic + product call on detection sensitivity. Mechanisms exist (`chat.cancel`, queue clear) — B just adds the trigger. |
| **Lower-latency synth path** | v1's per-sentence model gives ~2–4s first-audio. Talk mode wants sub-second. Solved either by streaming sub-sentence deltas (the deferred protocol upgrade) or a different engine for talk sessions. Abstraction supports both. |
| **`.playAndRecord` audio session with echo cancellation** | Different category, different routing, possibly different player config. Player accepts session category as init param — no v1 lock-in. |
| **Turn detection / endpointing** | VAD-based vs push-to-talk vs wake-word — open product question. |
| **Talk-mode UI** | Different surface (hands-free, big mic button, wake indicator). Separate design pass. |

### What we deliberately avoid in v1 that would have blocked B

- **No hard-coding "per-sentence" semantics in the player.** `SpeechPlayer`
  accepts a queue of audio chunks of any granularity. Future streaming-delta
  upgrade just pushes more, smaller items.
- **No fixed audio session category in the player.** Constructor parameter.
  v1 passes `.playback`; B can pass `.playAndRecord`.
- **Naming.** `speech_enabled` (output only) leaves room for a future
  `talk_mode_enabled` (bidirectional) without renaming. The two are
  independent toggles.
- **Voice scoping at the agent level, not session.** Both A and B want the
  same voice per agent. No re-architecture needed.

## Out of Scope

Explicitly not part of v1:

- **Talk mode (B).** Bidirectional voice conversation, barge-in, hot-mic
  endpointing.
- **Sub-sentence streaming audio deltas.** Per-sentence is the v1 protocol;
  upgrade later if latency becomes a problem.
- **Per-message playback UI beyond replay.** No progress bars, no waveforms,
  no karaoke-style highlighting.
- **Per-session speed control.**
- **Voice cloning / custom voice upload.** If/when needed, route specific
  agents through an alternative `ISpeechSynthesizer` impl (F5/Chatterbox).
- **Cost-ledger entries for speech.** Free local compute; no entries.
- **`scripts/install-speech-sidecar.sh`.** Worth a follow-up issue;
  documented manual setup suffices for v1.
- **Engines other than Kokoro.** Abstraction is in place; concrete additions
  (ElevenLabs, gpt-4o-audio) are separate work.

## Open Questions

None. All product calls resolved during brainstorming:

- Read-aloud (A) + per-agent voice (C); talk mode (B) explicitly deferred.
- Auto-play with per-session toggle.
- Agent-owned voice (no user-side override).
- Local Kokoro via `kokoro-fastapi` sidecar (Approach 1).
- Per-sentence `audio.block` granularity, not sub-sentence streaming.
- Voiceless agents stay silent (no surprise default).
- Code blocks silently omitted (no spoken hint).
- One voice per turn during inter-agent chat (target's reply not spoken).
- Background playback enabled (`audio` background mode).
- No auto-resume after interruption.
- No port auto-fallback (predictability over magic).
- Restart backoff caps at 5min indefinitely (no hard-fail-after-N).
