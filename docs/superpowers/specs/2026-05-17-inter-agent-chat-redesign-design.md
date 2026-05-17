# Inter-Agent Chat Redesign — Design

Date: 2026-05-17
Status: Approved (pending spec review)

## Context

Inter-agent chat was just wired into production (commit `dd9d23f`): the `ChatTool`
lets one agent consult another. Real-world use surfaced two problems plus a
deeper modeling issue:

1. **The conversation "disappears" from the initiating agent's session.** The
   server persists the whole exchange, but only as a single `ToolResultMessage`.
   The iOS client collapses tool results by default
   ([ToolCallView.swift:40-51](../../../apple/Achates/Views/ToolCallView.swift))
   and *drops them entirely on session reload*
   ([Session.swift:92-95](../../../apple/Achates/Models/Session.swift) parses
   tool calls with `result: nil`). After reload the initiating agent's session
   shows only an empty "chat" chip — the conversation is effectively lost to the
   user.

2. **The consulted agent gets a new session per round.** `ChatTool` saved the
   target's side once per `chat` tool call with a fresh GUID and a brand-new
   ephemeral runtime. The initiating agent calls the tool once per exchange, so
   each call spun a memoryless target runtime and wrote a new target session —
   session sprawl, and the target never "remembers" across rounds.

3. **The "persona split" is a fidelity compromise.** The old design ran the
   A-side of the conversation as a separate, context-starved "A-persona"
   sub-runtime (it only knew the opening message, not the user's conversation),
   and fed only that persona's *summary* back to A's real runtime. Both ends
   were lossy.

This redesign fixes all three. The desired outcome: when an agent consults
another, the exchange appears as real, attributed, streamed messages in the
user's transcript and in one continuing session on the consulted agent's side,
with maximum context fidelity and minimal moving parts.

## Decisions (from brainstorming)

These were settled with the user, in order; each later decision simplified the
prior ones:

- **Lifecycle:** the consulted agent participates with continuity (one
  persistent session), not an ephemeral per-call dump.
- **Interaction:** initially "autonomous but resumable", later **simplified to
  round-by-round driven by the initiating agent** (see "persona split" below).
- **Representation:** **first-class attributed messages** carrying a speaker,
  not content buried in a collapsed tool result.
- **Streaming:** the consulted agent's reply **streams token-by-token live**
  into the user's transcript.
- **Persisted target session:** **one continuing session per
  `(initiator-session, target-agent)` pairing**, appended on every call (no
  sprawl, no per-episode sessions).
- **Persona split: removed.** The split existed only because a tool call is
  blocking — the initiator's runtime is suspended inside the call, so it could
  not itself volley with the target, forcing a stand-in persona whose only
  payoff was cheap autonomous multi-turn bursts. The user chose fidelity +
  simplicity over autonomous bursts: the initiator talks to the target
  **directly, one round per `chat` call**, and drives the consult through its
  own normal turn loop.
- **Architecture:** a `ChatRoomManager` owned by `MobileTransport`; `ChatTool`
  is a thin façade. With the persona/burst removed, the manager is **stateless
  between calls**.

Explicitly rejected: making inter-agent chat a non-blocking runtime-level
feature (would preserve autonomy *and* fidelity but requires deep `AgentLoop`
surgery — disproportionate).

## Model

The `chat` tool exposes two actions:

- **`agents`** — list agents the caller may consult, filtered by the caller's
  `allow_chat` allowlist (unchanged behavior).
- **`ask`** — params `agent`, `message`. One round:
  1. Locate-or-create the one continuing session for
     `(initiatorSessionId, targetAgentId)`.
  2. Build a fresh target `AgentRuntime` seeded with that session's prior
     messages (so the target "remembers" earlier rounds), with the target's own
     tools **minus `chat`** (anti-cascade).
  3. Run a single target reply, streaming it live.
  4. Append two attributed messages — initiator→target (the `message`) and
     target→initiator (the reply) — to **both** the initiator's persisted
     `MobileSession` and the target's continuing session; broadcast updates.
  5. Dispose the target runtime. Return the target's reply **text** to the
     initiator's main runtime as the tool result (full fidelity).

The initiator's main runtime, holding the full user-conversation context and the
target's actual words (as the tool result in its own message history), decides
on its next turn whether to `ask` again or answer the user. "Conversation over"
is implicit: the initiator simply stops calling `ask`. Nothing is resident
between calls; there is no `end`, no idle timer, no in-memory room.

## Components

### `ChatRoomManager` (new)

Owned by `MobileTransport`. Stateless except for a per-pairing async lock.

```
AskAsync(initiatorAgentId, initiatorSessionId, targetAgentId,
         message, IChatSink sink, CancellationToken ct) -> string  // target reply text
```

Responsibilities:
- Resolve the target `AgentDefinition` from the live `_agents` map; enforce the
  caller's allowlist; reject unknown/self targets.
- Find the single continuing target session for
  `(initiatorSessionId, targetAgentId)` (stamped with both ids + `Source =
  SessionSource.Chat`); create it if absent.
- Build the target runtime seeded from that session's messages, tools minus
  `chat`.
- Stream the reply via `sink`; accumulate final text.
- Append the two `AgentSpeechMessage`s to the initiator's `MobileSession` and
  the target's continuing session; persist; broadcast `session.updated` for the
  target session.
- Hold a per-`(initiatorSessionId, targetAgentId)` lock so concurrent `ask`
  calls serialize (prevents interleaved appends / racing rebuilds).
- Dispose the target runtime before returning.

### `IChatSink` (new)

Narrow bridge so the manager stays testable and decoupled from transport
internals:

- Emit attributed streaming events (`agent_turn.start/delta/end`) into the
  initiator session's live event stream.
- Append an attributed message to the initiator's persisted `MobileSession` in
  the correct position (between the `chat` tool call's `tool.start` and
  `tool.end` in that turn).

Implemented by `MobileTransport` over its existing broadcast + `MobileSessionStore`.
A fake implementation backs unit tests.

### `ChatTool` (modified)

Façade only. Actions reduce to `agents` and `ask` (drop the current single
`chat` action and the persona/ping-pong loop and the end-of-call target-session
write). `ask` calls `ChatRoomManager.AskAsync` and returns the reply text. The
constructor gains the manager (and keeps `selfAgentName`, registry, allowlist).
The `sessionStore`/`onSessionSaved` constructor params added in `dd9d23f` move
into the manager and are removed from the tool.

## Data model

New `AgentMessage` subtype (in `Achates.Agent.Messages`):

```
AgentSpeechMessage : AgentMessage
{
    string SpeakerAgentId;
    string SpeakerDisplayName;
    string ToAgentId;
    string Text;
}
```

Persisted into both sessions' `MobileSession.Messages`. **Integration risk
(must be resolved in the plan):**

- `AgentMessage` is polymorphic; the new subtype must be registered wherever
  the polymorphic (de)serialization is configured (`MessageConversion` /
  `JsonDerivedType` attributes / the `MobileSessionStore` JSON options). The
  plan must locate and update every such site.
- These messages are injected into the persisted `MobileSession` only — they
  are **not** part of the initiator's in-memory runtime history during the live
  turn (for the initiator the exchange is carried by the `chat` tool call +
  tool result). But when the initiator's session is later **reloaded and the
  runtime rebuilt from `MobileSession.Messages`**, the rebuild path encounters
  them. The plan must define how `MessageConversion` maps `AgentSpeechMessage`
  when reconstructing LLM context: the expected behavior is that they are
  **presentation/persistence artifacts excluded from the rebuilt LLM context**
  (the tool call/result already encode the exchange for the initiator), so the
  target's words are not double-counted. This mapping must be explicitly
  implemented and tested, not left implicit.

Target session identification: the continuing session is stamped with
`Source = SessionSource.Chat` (already exists) plus the initiator session id and
target agent id so the manager can find the one session for the pairing. Add the
minimal fields needed to `MobileSession` (e.g. an `OriginSessionId` /
`PeerAgentId`, or reuse/extend existing stamping); the plan picks the least
invasive option consistent with the reaper, which already treats
`Source == Chat` sessions as max-age-pruned.

No episode/segment markers (YAGNI — dreamtime reviews by `Updated` timestamp and
reads session content directly).

## Streaming protocol

New WebSocket events, broadcast like existing streaming events:

- `agent_turn.start` `{ agent, session_id, speaker_id, speaker_name, to_id }`
- `agent_turn.delta` `{ ..., delta }`
- `agent_turn.end`   `{ ..., text }`

- The target's reply streams token-by-token via `agent_turn.delta`.
- The initiator's outgoing line is emitted as a single completed
  `agent_turn.start`+`agent_turn.end` (it is just the literal `message` arg; no
  generation to stream).
- The `chat` `tool.start`/`tool.end` still occur; iOS collapses them to a thin
  "{A} is talking with {B}…" header rather than a content-bearing chip.

iOS changes:
- `WebSocketClient` handles the three new events → appends/updates a distinctly
  styled, speaker-labeled bubble in the active session.
- `Session.swift` deserializer learns the persisted `agent_speech` block so a
  reloaded session shows the conversation. **This is the fix for bug #1.**
- `ToolCallView` collapses the `chat` tool to the header form.

## Lifecycle & errors

- **Lifecycle:** nothing resident between calls. The target runtime exists only
  for one `ask`. If the initiator's session is deleted mid-`ask`, the
  cancellation token aborts the call and cleans up; the target's continuing
  session remains on disk (dreamtime-visible, max-age pruned by the existing
  reaper). No `end` verb, no idle timers, no resident rooms.
- **Errors:** a target-runtime exception or mid-stream network drop stops the
  round, writes a `(consult failed: <reason>)` attributed note to both sessions,
  and returns a descriptive error string to the initiator's main runtime — real
  failures surface instead of the old silent "chat failed". Cancellation
  persists any partial turn.
- **Cost:** the target side records to its own ledger, channel `chat`
  (unchanged). The initiator's rounds are recorded normally by its own session's
  accounting (the `chat` tool call is part of a normal initiator turn).
- **Anti-cascade:** the target runtime is built with its tools minus `chat`
  (unchanged).
- **Allowlist:** `allow_chat` enforced in `agents` and `ask` (unchanged).

## Testing

Unit tests (`ChatRoomManager`, fake `IChatSink`, existing `<<DONE>>` stub
provider from `ChatToolTests`):
- Attributed messages land in both the initiator's and target's sessions.
- **One** continuing target session across repeated `ask` calls for the same
  pairing (regression for the sprawl bug).
- Seeding-from-prior: a second `ask` rebuilds the target with the first round's
  messages in context (the target "remembers").
- Concurrent `ask` calls for the same pairing serialize (no interleaved
  appends).
- Mid-`ask` cancellation cleans up and persists partial state.
- Target failure returns a descriptive error and writes the failure note.
- Fake sink asserts `agent_turn.*` ordering, speaker attribution, and
  per-token streaming of the reply.
- Reload/rebuild: an initiator session containing `AgentSpeechMessage`s is
  reloaded and its runtime rebuilt; assert `MessageConversion` excludes them
  from the LLM context (no double-counting of the target's words) while they
  still render in the transcript.

Regression: existing `ChatToolTests` adapted to the new `agents`/`ask` actions;
session-store and reaper tests stay green; the `dd9d23f` end-of-call
target-session persistence test is replaced by the per-`ask` persistence tests.

Manual E2E: two real agents; user chats A; A consults B; verify live streamed
attributed bubbles, session reload still shows the conversation, B has exactly
one continuing session that grows across rounds, B's dreamtime can read it.

## Out of scope (YAGNI)

- Autonomous multi-turn agent-to-agent bursts (the removed persona feature).
- More than one target per consult.
- The user interjecting mid-`ask` (one round is blocking, like any tool call).
- Fixing the generic tool-result-drop-on-reload for *other* tools (separate
  issue; sidestepped here by using attributed messages instead of the tool
  result for content).
- Episode/segment markers in the target session.
- A "fresh / don't remember" flag — `ask` always seeds from the pairing's
  continuing session (can be added later if a real need appears).
