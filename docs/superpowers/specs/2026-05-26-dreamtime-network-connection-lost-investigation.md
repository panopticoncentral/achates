# Dreamtime "Network connection lost." Investigation

Date: 2026-05-26 (updated 2026-05-31)
Status: **REPRODUCED — root cause identified. It is an upstream (OpenRouter↔Anthropic)
stall reported as an inline SSE error, NOT a local socket drop.** Awaiting a fix-direction decision.

## Update 2026-05-31 — reproduced; root cause flipped

A manual "Run Now" of Claire's dreamtime reproduced the failure with full
diagnostics. The failing turn logged:

```
OpenRouter stream FAILED model=anthropic/claude-sonnet-4.6
  elapsed=303993ms chunks=38 maxGap=279ms gapAtFailure=300039ms
  lastStop=Error textChars=231 partialToolArgs=35chars currentBlock=CompletionToolCall
  exceptionChain=[Achates.Providers.OpenRouter.OpenRouterException: Network connection lost.]
    at OpenRouterClient.StreamOneAttemptAsync(...) OpenRouterClient.cs:line 219
```

**The earlier hypothesis (local `SocketsHttpHandler` RST) was WRONG.** The stack
trace lands on `OpenRouterClient.cs:219` — the **inline SSE error path**
(`if (doc.RootElement.TryGetProperty("error", ...)) throw new OpenRouterException(...)`).
So `"Network connection lost."` is **an error event OpenRouter sent us over a
healthy HTTP 200 SSE stream** (`data: {"error":{"message":"Network connection lost."}}`),
not a socket exception from our side. Our local connection to OpenRouter was
fine the whole time — it delivered the error message to us. The failure is
**upstream of OpenRouter's edge** (OpenRouter ↔ the Anthropic backend).

**Timeline of the failing turn (from the counters):**

- Headers returned 200 in 983 ms.
- 38 chunks streamed smoothly over the first **~4 s** (`maxGap` only 279 ms):
  the 231-char text block ("Good. I now have a clear picture…") plus the start
  of the `memory.save` tool call — `{"action":"save","scope":"agent"` (35 chars).
- Then **exactly 300.0 s of total silence** (`gapAtFailure=300039ms`) — no SSE
  bytes at all — right at the point where the 44 KB `content` body should begin.
- OpenRouter then emitted the inline `error` event and we surfaced it.

The **exactly-5-minute** gap is the signature of a **fixed 300 s upstream
idle/read timeout inside OpenRouter**: after the Anthropic backend stopped
sending tokens (right after the tool-call preamble), OpenRouter waited 300 s
and declared the upstream connection lost.

**Why the user didn't see the session for ~5 min:** the cron path saves the
session only after the stream ends (`ExecuteJobAsync` → after
`ConsumeJobStreamAsync` returns). The stream didn't end until the 300 s stall
resolved into an error, so the session
(`20260531-054427_a2ea1f4d8992_dreamtime.json`, failed turn at message [11],
`memory.save` with truncated args) appeared only then.

### Revised implications for the candidate fixes

- **Option D (local TCP keepalive pings) — CROSS OFF.** The local socket was
  healthy; it carried the error event. Keepalive on our side does nothing about
  an upstream stall. This was the previous front-runner; it is now ruled out.
- **Existing 502 retry — still cannot catch it.** It *is* now an
  `OpenRouterException` (type matches), but the retry is gated by
  `!yieldedAny`, and we yielded 38 chunks before the stall — so the guard
  hard-blocks any retry. (Whether `IsTransient502` would even match depends on
  the error's `code`/`metadata`, which the diagnostic now also logs;
  the message arrived bare, suggesting little/no metadata.)
- **Trigger is Claire-specific and deterministic.** Claire regenerates her
  **44 KB** `memory.md` as a single `memory.save` tool argument on the
  **Anthropic** route; Sasha (DeepSeek, 11 KB) never stalls. The stall happens
  *as the giant tool body is about to stream*.
- **Doom-loop note:** failed runs don't advance `LastRunAt`, so each retry's
  review window (and thus the consolidation it generates) grows — compounding
  over time, though the first failure (5/19, 1-day window) shows window size is
  not the originating trigger; the giant `memory.save` is.

### Route experiment result (2026-05-31) — NOT route-specific

Claire was temporarily pinned to `deepseek/deepseek-v4-flash` (the pair Sasha
uses) and re-run. It **still failed**, decisively ruling out an
Anthropic-only cause:

```
OpenRouter stream FAILED model=deepseek/deepseek-v4-flash
  elapsed=177149ms chunks=550 maxGap=474ms gapAtFailure=159070ms
  lastStop=Error textChars=82 partialToolArgs=0chars currentBlock=CompletionTextContent
  exceptionChain=[OpenRouterException: Upstream idle timeout exceeded
                  {orCode=502, orMetadata={"error_type":"provider_unavailable"}}]
```

Comparison of the two reproductions:

| | Claire / Anthropic | Claire / DeepSeek |
|---|---|---|
| Streamed before stall | ~4 s, 38 chunks | ~18 s, 550 chunks |
| Idle gap before failure | **300.0 s** (exactly) | **159 s** |
| Died in | `memory.save` tool args (35 ch) | a text block (82 ch), after heavy reasoning |
| OpenRouter error | "Network connection lost." | "Upstream idle timeout exceeded" |
| code / metadata | (not captured — pre-enhancement) | **502 / `provider_unavailable`** |

**Conclusion: the failure is route-agnostic.** Both upstream providers stall
mid-stream during Claire's heavy dreamtime turn, and OpenRouter surfaces it as
a **transient upstream idle timeout** — explicitly `502 provider_unavailable`
on the DeepSeek run. The determinant is **per-turn load/duration**, not the
provider: Claire's dreamtime regenerates a 44 KB memory *and* (right now) chews
through a 5-day review backlog; Sasha's light turns never stall on the same
provider. The two providers simply have different idle ceilings (~300 s vs
~159 s) and stall at different points.

**Key consequence for the fix:** the DeepSeek failure is *exactly* the
`502 / provider_unavailable` that the existing retry
(`OpenRouterClient.IsTransient502`) already classifies as transient. The retry
doesn't fire only because of its **`!yieldedAny` guard** — by the time the
upstream stalls we've already streamed hundreds of chunks. So recovery must
happen **post-yield, at the turn level** (discard the partial assistant
message and re-stream), paired with a **client-side idle-read timeout** so a
stall fails in ~60 s instead of 159–300 s.

### Resilience fix — IMPLEMENTED (2026-05-31)

Built the post-yield recovery layer (TDD; 5 new tests in
`OpenRouterProviderStreamRetryTests`, full suite 387 green):

- **Client-side idle-read timeout** (`OpenRouterClient`): each SSE
  `ReadLineAsync` is bound by `StreamIdleTimeout` (default **60 s**, reset on
  every line — including OpenRouter's `: PROCESSING` keepalive comments, so it
  only trips on genuine silence). A stall now throws `StreamIdleTimeoutException`
  in ~60 s instead of hanging for OpenRouter's 159–300 s ceiling.
- **Turn-level replay** (`OpenRouterProvider.ProcessStreamAsync`): the stream
  loop is wrapped in an attempt loop (`MaxStreamAttempts = 3`). On a transient
  mid-stream failure that occurred *after* yielding (`yieldedThisAttempt`), it
  discards the partial assistant message and re-requests from scratch. This is
  the layer the existing `!yieldedAny` client retry structurally cannot reach.
  The two layers stay disjoint: **pre-yield** handshake retries in the client,
  **post-yield** replays in the provider.
- **Transient predicate** (`IsRetryableTransient`): `StreamIdleTimeoutException`,
  `HttpRequestException`/`IOException`, or `OpenRouterException` with code 502 /
  `error_type "provider_unavailable"` / message containing "idle timeout" |
  "connection lost" | "timed out". Genuine model/request errors (400,
  content_filter, etc.) are **not** retried.
- Budget: worst case 3 × ~60 s + backoff ≈ 3 min, well under the 15-min cron
  per-job cap (vs. a single 5-min hang today). Cost: a failed attempt dies early
  (idle), so only input tokens are re-billed per replay, capped at 3.

Known limitation: on an interactive (non-cron) turn, a replay re-emits stream
events, so a live viewer briefly sees the partial then a restart. The persisted
message (from the final `Done` event) is always correct. Acceptable for the
dreamtime/cron target; future polish could emit a reset signal.

#### Correction after first live run — keepalives were masking the idle timeout

The first live run with the fix confirmed the retry *fires* (predicate matched —
and notably the Anthropic "Network connection lost." error now also shows
`orCode=502, orMetadata={"error_type":"provider_unavailable"}`, so **both**
providers' stalls are the same retryable class). But the attempt still took
**303967 ms** — the idle timeout did **not** save us:

```
OpenRouter stream transient mid-stream failure (attempt 1/3) after 303967ms, 24 chunks — replaying turn.
  exceptionChain=[OpenRouterException: Network connection lost. {orCode=502, orMetadata={"error_type":"provider_unavailable"}}]
```

Root cause of the miss: **OpenRouter sends `: OPENROUTER PROCESSING` keepalive
comment lines while it waits on a stalled upstream.** The first version of the
idle watchdog reset on *every line read*, so the keepalives kept it alive and it
never tripped — we waited out OpenRouter's own ~300 s ceiling. With 3 attempts ×
~5 min that would approach the 15-min cron cap, making the retry a liability.

Fix: the watchdog now measures time since the last **`data:`** line; keepalive
comments and blank lines no longer reset it (`OpenRouterClient.StreamOneAttemptAsync`).
Regression-tested by `IdleTimeout_NotResetByKeepalives_StillFires` (a stream that
drips keepalives forever after one chunk must still time out). Now a stall fails
each attempt in ~60 s → ≤ ~3 min for the full 3-attempt budget. Suite: 388 green.

Still open (needs another live run): whether a *replay* of Claire's heavy turn
actually **succeeds** (transient) or stalls again (persistent). Her past
successes (5/21, 5/25) say transient is likely, but this particular 5-day-backlog
run may be stall-prone until the load-reduction work lands.

#### Second live run — RECOVERED ✅, and the keepalive-aware timeout was WRONG

Claire's dreamtime **succeeded on retry**. Attempt 1 errored at ~304 s
(`provider_unavailable`) → turn-replay → **attempt 2 completed** (`stop=ToolUse`,
the 44 KB `memory.save` streamed as 7343 chunks) and the whole job finished. The
turn-replay resilience approach is **validated end-to-end**: Claire's stall is
**transient**, exactly as her 5/21 + 5/25 successes predicted.

But the success log carried a decisive surprise:

```
OpenRouter stream OK ... stop=ToolUse elapsed=383486ms chunks=7343 maxGap=371322ms attempt=2
```

`maxGap=371322ms` — the *successful* attempt went **371 s** silent between two
chunks and still completed. So a legitimately recovering upstream can pause for
**6+ minutes** (held open by keepalives) before finishing. That **overturns the
previous correction**: the "keepalive-aware" idle timeout (reset only on `data:`
lines) would have aborted this very attempt at 60 s and likely failed the run.
There is **no idle threshold** that separates "stalled and will fail" from "slow
and will succeed" — both look like multi-minute keepalived silence.

**Final design (reverted to this):**
- **Turn-replay retry** on OpenRouter's transient error is the real fix — proven
  to recover Claire. The error reliably arrives (~300 s) only when the upstream
  is *actually* dead, which is the correct, non-speculative signal to replay on.
- **Idle timeout** is reverted to a plain **total-silence** backstop: every line
  (including keepalives) resets it, so it fires only when *nothing at all*
  arrives for 60 s — a true black hole. It deliberately does **not** abort
  keepalived slow turns. Mainly relevant to interactive turns (cron has its own
  15-min wall-clock cap, which the retry loop respects via cancellation).
- Removed the `IdleTimeout_NotResetByKeepalives_StillFires` test — it encoded the
  wrong requirement. Suite back to 387 green.

Cost note: a stalled run now costs ~300 s per failed attempt (OpenRouter's
ceiling) before replay; bounded by `MaxStreamAttempts` and ultimately by the
15-min cron cap. This is why **load reduction (Option E)** still matters — it
cuts the per-turn work so the heavy `memory.save` is less likely to stall (and
each attempt is cheaper).

Note: Claire is **not** a 100 %-deterministic size failure — she *succeeded* on
5/21 and 5/25 with comparably large saves. Her stalls are **transient**, just
more frequent on heavy turns. So turn-replay alone should recover most of her
runs; a recovered run advances `LastRunAt`, which also unwinds the backlog
doom-loop on its own.

### Recommended direction (revised again, post-experiment)

1. ~~Disambiguate route vs. payload~~ — **done**; result above: not route-specific.
2. ~~Resilience: idle timeout + post-yield turn-replay~~ — **done**.
3. ~~Load reduction (Option E)~~ — **done** (see below).

### Load reduction (Option E) — IMPLEMENTED (2026-05-31)

Cuts the per-turn work so the heavy `memory.save` is both less likely to stall
and cheaper. TDD; full suite 396 green.

- **Incremental memory writes** (`MemoryTool`): added `append` (add text to the
  end) and `edit` (replace a *unique* substring; empty replacement deletes;
  refuses missing/ambiguous matches) alongside the existing `read`/`save`. Most
  nightly updates are now small targeted writes instead of regenerating the full
  ~44 KB file as a single tool-call argument — which was the exact thing that
  stalled. Backward-compatible: `read`/`save` unchanged, new actions are additive,
  and the tool is universal so every agent benefits.
- **Dreamtime prompt** now steers step 5 toward `append`/`edit`, reserving full
  `save` for genuine reorganization.
- **Review-window cap** (`CronService.MaxDreamtimeReviewWindow` = 14 days): a
  single dreamtime never looks back further than 14 days, so a stuck `LastRunAt`
  after a failure streak can't make one run grind through weeks of sessions.
  Deliberately minimal — only a *non-null, older-than-14-day* `LastRunAt` is
  clamped; null (never-run) and recent values are untouched, so normal daily runs
  and Claire's current 5-day backlog are unaffected.

Net: resilience (turn-replay) makes the inevitable transient stall recoverable;
load reduction makes stalls rarer and each turn cheaper. Together they address
both the *frequency* and the *survivability* of the upstream-stall failure mode.
2. **Durable cure — Option E (incremental memory writes):** give `MemoryTool`
   an append/patch/section-edit action so dreamtime stops regenerating the full
   body as one ~45 KB tool argument. This removes the trigger regardless of
   route.
3. **General resilience — fail-fast + replay:** add a **client-side idle-read
   timeout** (abort a stream after ~60–90 s with no SSE bytes, instead of
   waiting OpenRouter's 300 s) plus a turn-level replay (Option C). This makes
   transient upstream blips recoverable and stops a single stall from burning
   5 min (3 retries would otherwise approach the 15-min cron cap). It will not,
   by itself, fix a *deterministic* stall on Claire's big save.

---


## Update 2026-05-30 — diagnostics landed; reproduce on demand

Claire's dreamtime now fails **every** night (`consecutive_errors: 5`,
stuck at `last_run_at: 2026-05-25`); Sasha's succeeds nightly. A 100%
deterministic failure with a clean control (Sasha) is ideal for diagnosis.

**New differentiators found (Claire vs Sasha):**

| | Claire (fails) | Sasha (works) |
|---|---|---|
| `memory.md` size | **44 KB** | 11 KB |
| Completion model | `anthropic/claude-sonnet-4.6` | `deepseek/deepseek-v4-flash` |

So Claire differs on **both** payload size (the `memory.save` body it
regenerates is ~4× larger) **and** upstream route (Anthropic vs DeepSeek via
OpenRouter). Either could drive the determinism.

**Refined failure shape (important):** the stream dies *right as the final
`memory.save` begins* — the partial tool args are only
`{"action":"save","scope":"agent"` (36 chars), **before** the 44 KB body
streams. So it is NOT "200 s of streaming the body trips a reset"; it is a
drop in the **gap between the text block finishing and the large tool-body
starting**. That makes *max inter-chunk gap* and *gap-at-failure* the decisive
measurements (idle-connection reset vs. hard timeout vs. size cap).

**Instrumentation added (Option A from the recommendation below):**

- `IModelProvider.Logger` — optional `ILogger` setter (default no-op so the
  6 test stubs and other providers are unaffected).
  (`src/Achates.Providers/IModelProvider.cs`)
- `OpenRouterProvider.ProcessStreamAsync` now times the SSE stream and, on
  failure, logs at **Warning**: `elapsed`, `chunks`, `maxGap`,
  **`gapAtFailure`**, `lastStop`, `textChars`, `partialToolArgs`,
  `currentBlock`, and the **full exception chain incl. `SocketError`/errno**
  via `DescribeExceptionChain`. On success it logs the same counters at
  **Debug**. (`src/Achates.Providers/OpenRouter/OpenRouterProvider.cs`)
- Logger wired in `GatewayService.ResolveModelAsync` (the provider instance
  reused for every completion). (`src/Achates.Server/GatewayService.cs`)
- `appsettings.json` enables `Achates.Providers.OpenRouter: Debug` so the
  success-side counters (Sasha) are visible for comparison.
- `Achates.Providers.csproj` references `Microsoft.Extensions.Logging.Abstractions`.

Build clean (0 warnings); all 382 tests pass.

**How to reproduce on demand (non-destructive):**

1. `dotnet run --project src/Achates.Server` with the console visible.
2. App → the agent's scheduled jobs → Claire → **Dreamtime** → **"Run Now"**
   (the button is not gated for system jobs). This calls the `jobs.run` RPC →
   `CronService.RunJobAsync("claire", "92219587cb96", skipNext: false)`.
   With `skipNext: false` the schedule is untouched, and because the failing
   `memory.save` never completes, Claire's memory is not modified — safe to
   repeat.
3. Watch the server console for the `OpenRouter stream FAILED …` warning.

**Reading the result:**

- Large **`gapAtFailure`** (tens of seconds) → idle-connection reset during
  the pre-tool-body pause → fix is TCP keepalive pings on the
  `SocketsHttpHandler` (Option D), promoted from "insurance" to "the fix".
- Consistent **`elapsed`** across runs → a hard timeout at some hop.
- Consistent **byte/`textChars`** offset → an upstream size cap.
- The **`SocketError`/errno** in the chain distinguishes `ConnectionReset`
  (ECONNRESET) from `TimedOut` from a TLS/DNS failure.

A Sasha "Run Now" produces the Debug success line for side-by-side
comparison (expected: small `maxGap`, no failure).

---

_Original investigation (2026-05-26) follows._

## TL;DR

Claire's dreamtime job has failed in **8 of the last 8 nightly runs** (5/19 →
5/26, 100% failure rate after a previous 13-for-13 success streak through
5/18). The failure is always identical in shape: the SSE stream from
OpenRouter is cut off mid-way through the *third or fourth assistant turn*,
specifically while streaming the `arguments.content` of the agent's
`memory.save` tool call. The recorded error message is the .NET runtime string
`"Network connection lost."` — a `HttpRequestException` / `IOException` from
`SocketsHttpHandler` on macOS, surfaced verbatim through
[OpenRouterProvider.cs:243](../../src/Achates.Providers/OpenRouter/OpenRouterProvider.cs:243)'s
`catch (Exception ex) { ErrorMessage = ex.Message }`.

The existing 502 retry on `main` (`792cdb7`) does **not** cover this and
cannot cover it without changes — its retry predicate only fires on
`OpenRouterException`, which is never the exception class here.

**Recommendation:** small diagnostic-logging change first (one nightly cycle
to confirm the exception chain), then add (a) an `HttpClient` hardening pass
and (b) a turn-replay retry in `AgentLoop` for streaming completions that
end in `StopReason.Error` with a transient-looking message. Skip the
"broaden the pre-yield predicate" idea — it can't help this pattern because
text has already been streamed by the time the drop occurs.

## Evidence

### Frequency in saved sessions

Scan of `~/.achates/agents/*/sessions/`:

| Agent  | Total dreamtime runs | With `stop_reason=error` | With `"Network connection lost."` |
|--------|----------------------|--------------------------|-----------------------------------|
| claire | 21                   | 6                        | **8** (incl. self-recovered runs) |
| sasha  | 10                   | 1                        | 0                                 |
| vera   | 5                    | 0                        | 0                                 |
| friday/sofia/val | (no dreamtime)| —                       | —                                 |

Other observed error strings across all sessions:

- `"Upstream idle timeout exceeded"` — sasha dreamtime 5/22, exactly **one
  occurrence**. This is an OpenRouter inline mid-stream JSON error and goes
  through `OpenRouterException` correctly; it is a separate (much rarer)
  failure mode. Not in scope here.
- `"JSON error injected into SSE stream"` — sasha 5/18 + 5/19, in non-dreamtime
  sessions. This is the **test fixture** string from
  `tests/Achates.Tests/OpenRouterClientRetryTests.cs:30`; it leaked into a real
  session file (sasha was apparently used as a dev playground). Ignore.

### The behavioral cliff

Claire's dreamtime is the canary. Looking at the by-day breakdown
(`tot_in` = sum of `usage.input` across all assistant messages in the run;
`err_idx` = message index where `error: "Network connection lost."` appears):

| Date  | err_idx | tot_in | tot_out | Outcome             |
|-------|---------|--------|---------|---------------------|
| 5/06–5/18 | —   | 30k–130k | 4k–13k | ✅ 13/13 clean      |
| 5/19  | 9       | 18k    | 377     | ❌ errored          |
| 5/20  | 11      | 40k    | 514     | ❌ errored          |
| 5/21  | —       | 128k   | 13.5k   | ✅ clean            |
| 5/22  | 10      | 45k    | 438     | ❌ errored          |
| 5/23  | 11      | 46k    | 493     | ❌ errored          |
| 5/24  | 8       | 99k    | 10.1k   | ⚠ recovered (Paul prodded the agent in-session next day) |
| 5/25  | —       | 89k    | 11.8k   | ✅ clean            |
| 5/26  | 8       | 115k   | 12.0k   | ⚠ recovered (same)  |

The low `tot_in` / `tot_out` numbers on the failed days are misleading: the
failing assistant message has `usage.input = 0` (because usage was never
reported before the stream died) and the loop terminates immediately after,
so it never gets the chance to accumulate more turns.

### What "recovered" actually means

Three runs (5/24, 5/26 and previously 5/10/5/11) appear successful at first
glance but contain `Network connection lost.` mid-history. These are **not**
auto-recovered. Examining the example session
`~/.achates/agents/claire/sessions/20260526-100717_8781c0e9ecf1_dreamtime.json`:

```
[8] role=assistant stop_reason=error  error: Network connection lost.
    text: "Good. I now have a clear picture..."
    tool_call: memory  args = {"action":"save", "scope":"agent"}     ← content field missing!
[9] role=user
    text: "It looks like there was a problem, do you want to continue?"  ← Paul typed this
[10..14] agent recovers, re-reads memory, re-saves with full body
```

Message [9] is a **real user message** — Paul opened the dreamtime session
the next morning and manually prompted it to continue (per CLAUDE.md:
"When a user replies via `chat.send` to a session that originated from a
dreamtime job, the existing `JobId` is preserved and `SessionsTool` is
re-injected …"). Without that intervention the run is dead.

### The repeating fingerprint

All 8 failed turns share the **identical** assistant-message shape:

| Date     | Asst turn # at failure | text blocks | thinking blocks | tool calls | tool name | args size |
|----------|------------------------|-------------|-----------------|------------|-----------|-----------|
| 5/19     | 3                      | 1           | 0               | 1          | `memory`  | 36        |
| 5/20     | 4                      | 1           | 0               | 1          | `memory`  | 36        |
| 5/22     | 4                      | 1           | 0               | 1          | `memory`  | 36        |
| 5/23     | 4                      | 1           | 0               | 1          | `memory`  | 36        |
| 5/24     | 3                      | 1           | 0               | 1          | `memory`  | 36        |
| 5/26     | 3                      | 1           | 0               | 1          | `memory`  | 36        |
| 5/10, 5/11 | 3                    | 1           | 0               | 1          | `memory`  | 36        |

`args size = 36` = `{"action":"save","scope":"agent"}` with no `content`
field, i.e. `PartialJsonParser.ParseStreamingJson(tracker.PartialArgs)` saw
enough of the streamed args to parse `action` and `scope` but the stream
died before any of the `content` string was emitted (or before enough was
emitted to parse). The complete tool call when it eventually does succeed
(message [12] of the 5/26 session) has `args size = 45,220` — so the model
is mid-way through streaming roughly 45 KB of JSON-encoded markdown as a
single tool-call-argument string when the connection drops.

### Timing/architectural correlation

The cliff lands on 5/19, three days after these commits landed:

- `b4be12c` (5/17) **feat(chat): redesign inter-agent chat — direct, attributed, streamed**
- `dd9d23f` (5/17) **feat(chat): wire ChatTool into production and persist the target agent's side**
- `ba53695` (5/17) **feat(sessions): let agents browse their own past sessions any time** *(adds `SessionsTool`)*
- `b8f2eba` (5/20) **feat(tools): make memory and cost universal across all agent runtimes**

Combined effect on dreamtime, all of which point in the same direction (more
data through the pipe):

1. `SessionsTool` is now injected with `since: last_run`, so dreamtime
   actively reads back the full bodies of every chat-origin and
   cron-origin session since the prior run. Those bodies were previously
   invisible.
2. The inter-agent chat redesign means chat-origin sessions (`Source = Chat`)
   for *other* agents that messaged Claire are also visible to her review.
3. The combined corpus on a typical day grew from ~30k input tokens
   (pre-5/19 average) to 50–130k (5/21, 5/24–5/26 successful runs), and
   the `memory.save` body grew correspondingly because there is more to
   consolidate each night.

Bigger request + longer streamed response = the response stream is held open
longer with mostly idle gaps between SSE chunks, and the failure rate against
flaky NAT/keepalive paths rises accordingly. This is consistent with the
"failure on big memory.save" pattern.

## Root cause: where the string comes from

`"Network connection lost."` does not appear anywhere in the Achates source
tree. It is produced by **`System.Net.Http.SocketsHttpHandler`** when the
underlying TCP socket sees an unexpected `RST` / `FIN` while a request body
upload or a response stream read is in progress. On macOS this corresponds
to `errno = ECONNRESET` / the Network framework's
`NSURLErrorNetworkConnectionLost` (-1005). It surfaces as:

```
System.Net.Http.HttpRequestException: Network connection lost.
  ---> System.IO.IOException: Network connection lost.
    ---> System.Net.Sockets.SocketException (54): Connection reset by peer
```

In our path it is thrown from inside the SSE read loop of
`OpenRouterClient.StreamOneAttemptAsync` at the `reader.ReadLineAsync` or
the underlying `HttpContent.ReadAsStreamAsync` call, bubbles up to
`OpenRouterProvider.ProcessStreamAsync`'s blanket
`catch (Exception ex)` at
[OpenRouterProvider.cs:243](../../src/Achates.Providers/OpenRouter/OpenRouterProvider.cs:243),
gets stamped onto the assistant message as
`CompletionStopReason.Error` + `ErrorMessage = ex.Message`, and is pushed as
a `CompletionErrorEvent`. **No exception escapes upward** — the agent loop
sees the assistant message come back with `StopReason = Error` and just
ends the inner loop (see below).

We **cannot** currently confirm from the saved sessions which exception
class it is, because the catch site drops the type and the inner chain. That
is the first thing to fix.

### Why the agent loop doesn't retry on its own

In [AgentLoop.cs:230](../../src/Achates.Agent/AgentLoop.cs:230):

```csharp
if (assistantMessage.StopReason == CompletionStopReason.ToolUse)
{
    continueLoop = true;
}
```

The inner loop continues **only** when `StopReason == ToolUse`. For
`StopReason.Error` the loop falls through, the broken assistant message is
persisted as-is (with `text` block intact, `tool_call.arguments` partial),
and control returns to `CronService.ConsumeJobStreamAsync` at
[CronService.cs:421–424](../../src/Achates.Server/Cron/CronService.cs:421),
which correctly records `streamError = err` and marks the job
`status = "error"` with `advanceLastRunAt: false`. So the *next* dreamtime
24 hours later does re-review the same sessions — but if the underlying
network conditions are persistent (which is what claire's 8/8 streak
suggests), it just fails again the same way.

### Why the existing 502 retry can't catch this

`OpenRouterClient.StreamOpenRouterChatCompletionAsync`
([OpenRouterClient.cs:125](../../src/Achates.Providers/OpenRouter/OpenRouterClient.cs:125))
retries only when **all three** are true:

1. The exception is `OpenRouterException` (not `HttpRequestException` / `IOException`).
2. `IsTransient502(ex)` (HTTP 502 or `error_type == "provider_unavailable"`).
3. `yieldedAny == false` (no chunks have been emitted yet on this attempt).

For this failure mode condition (1) fails (wrong exception type) **and** in
every observed case condition (3) also fails (the text block always streams
successfully before the drop, so `yieldedAny == true`). Even simply
broadening the predicate to `HttpRequestException` would still be blocked by
(3) — by the time the tool-args stream dies the SSE has been flowing for
seconds.

## Options considered

### Option A — Add a diagnostic logger at the catch site (do this first, cheap)

In `OpenRouterProvider.ProcessStreamAsync`, when `StopReason.Error` is set,
log `ex.GetType().FullName` plus the full inner-exception chain
(`ex.ToString()` is good enough), the URL, and whether `yieldedAny` was true
(would require plumbing one bool out of `StreamOneAttemptAsync`, or just
checking whether the partial `output.Content` has any non-empty blocks).
Achates currently links the project against `Microsoft.Extensions.Logging.Abstractions`
in `Server` only; `Providers` has no logger. Two reasonable shapes:

- Inject `ILogger<OpenRouterProvider>` via a property/setter parallel to
  `HttpClient` (low-friction, no DI changes needed in `Server`).
- Surface the exception details in the `CompletionErrorEvent` payload itself
  (it already has access to the message; widen to `Exception?` for the
  internal path and stringify only at the session-save boundary).

The first is simpler and matches the framework convention. Either way, run
for one nightly cycle, confirm the exception class, then proceed.

### Option B — Broaden the pre-yield retry predicate

Add `HttpRequestException` / `IOException` to the retry predicate in
`StreamOpenRouterChatCompletionAsync`. **Will not help dreamtime.** Worth
~zero on its own for this bug. It *might* help separate handshake-time drops
that today fail loudly without retry; if so, do it as a small parallel fix,
not as "the fix for dreamtime".

### Option C — Turn-level replay in the agent loop (the actual fix)

When `StreamAssistantResponseAsync` returns with `StopReason == Error` and
the error message matches a transient predicate (`Network connection lost.`,
`HttpRequestException`-derived, etc.), `AgentLoop.RunTurnAsync` should:

1. Discard the just-added broken assistant message from `messages` and
   `newMessages` (it has no semantic value — its tool call can't be executed
   because the args are incomplete).
2. Wait with backoff (reuse `OpenRouterClient.RetryDelay`).
3. Loop back and stream a fresh assistant response from the same
   conversation state.
4. Cap at N attempts (3 matches `OpenRouterClient.MaxAttempts`); on the
   final failure, leave the broken message in place so existing behavior is
   preserved.

Trade-offs:
- **Loses the unstreamable text the model produced before the drop.** Because
  we discard the partial assistant message, the model's pre-drop reasoning
  is gone and the retry starts cold. In practice this is fine because the
  model has no commitment to that text — it will regenerate equivalent
  intent.
- **Cost: pays for one full extra request** (the retry includes the entire
  prior context). Bounded by the attempt cap.
- **Touches `AgentLoop`** (the shared engine), which is heavier than a
  provider-only fix. Worth testing carefully — particularly that the
  discarded message and any subscriber events (`MessageEndEvent` for the
  partial) don't leak inconsistent state to clients.

### Option D — Harden the `HttpClient` for long SSE streams (cheap insurance)

`Program.cs:10` does `builder.Services.AddHttpClient()` (no named `"achates"`
registration, so `httpClientFactory.CreateClient("achates")` falls through
to defaults — `PooledConnectionLifetime = Infinite`, no HTTP/2 PINGs).
Register the client explicitly with a `SocketsHttpHandler` tuned for SSE:

```csharp
builder.Services.AddHttpClient("achates")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
        EnableMultipleHttp2Connections = true,
    });
```

This won't *eliminate* legitimate drops but reduces the surface for
NAT/keepalive zombies and gives faster detection. Low risk.

### Option E — Stop streaming the full `memory.save` body

A side-channel fix at the tool layer: give `MemoryTool` a smaller-payload
action (patch / append / structured-section edit) so dreamtime doesn't have
to round-trip the entire ~45 KB memory body through `tool_call.arguments`
every night. This drops the failure surface dramatically — by far the
cheapest fix for the *dreamtime* case specifically — but it changes tool
semantics, requires prompt updates, and only helps memory-shaped traffic.
Worth considering separately from the network-resilience question.

## Recommendation

1. **Land Option A first** (one PR). Logging-only, ships in an evening.
   Wait one nightly cycle to capture the actual exception class on the next
   failure. Confirm `HttpRequestException` (and likely `IOException` /
   `SocketException` inner). This is the only thing that closes the
   "we still don't know what the exception type is" gap, which the
   investigation brief explicitly asked for.
2. **Then land Option C + D together** (second PR). C is the only fix that
   actually catches this failure mode at runtime; D is cheap insurance and
   the right defaults anyway. C should be predicate-gated by a small
   "transient error message" matcher that covers the strings we confirm in
   step 1; default to no retry for unknown error strings to avoid
   masking real model errors.
3. **Do not bother with Option B.** The 3-condition predicate cannot match
   this pattern; broadening it adds risk for no expected upside on
   dreamtime.
4. **File Option E as a separate ticket** for the dreamtime/memory team. It's
   a real win but it's a tool-API change and out of scope for a transport
   bug fix.

## Open questions

- Are the network drops happening at the client's local egress (home Wi-Fi,
  router NAT timeout) or at OpenRouter's edge (Cloudflare RST)? Today's
  evidence doesn't distinguish; the diagnostic logging in step 1 may help
  if it surfaces socket-level errno values, but truly localizing this would
  need a packet capture during a failing run. Probably not worth the
  effort given the symptoms-only fix in step 2 works regardless.
- Should the cron `CronService.ExecuteJobAsync` also do its own
  whole-job retry on `streamError` (e.g., one auto-retry within the same
  cron tick before giving up to the next-day reschedule)? That's a separate
  decision; with Option C in place it may be unnecessary.
