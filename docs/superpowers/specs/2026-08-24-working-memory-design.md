# Working Memory — Design

Date: 2026-08-24
Status: Approved (pending spec review)

## Context

Achates stores agent knowledge in three places today:

- **Shared** (`~/.achates/memory.md`) — universal facts about the user, read
  and written by every agent.
- **Core** (`~/.achates/agents/{name}/memory.md`) — per-agent notes, with a
  soft token budget (`MemoryTool.DefaultCoreBudgetTokens`, currently 8000).
- **Archive** (`~/.achates/agents/{name}/memory/*.md`) — topical files
  retrieved on demand via `list` / `search` / read with a `file`.

**None of it is ever loaded automatically.** `MemoryPath` is only ever handed
to `MemoryTool`'s constructor (`src/Achates.Server/Tools/UniversalTools.cs:24`);
no code path reads it into a prompt. Agents are told to fetch it themselves —
`src/Achates.Server/SystemPrompt.cs:69` says "Read memory at the start of new
conversations to recall prior context" — and they comply unreliably.

Two prompts already assert the opposite. `MemoryTool`'s own schema tells the
model "Your main memory is small and always loaded", and the dreamtime
instructions describe core as "always loaded at the start of every
conversation" (`src/Achates.Server/Cron/CronService.cs:45`). Both are false
today.

The observed symptom is agents not knowing about ongoing threads, because
recalling them depends on a voluntary tool call that often does not happen.

## Goal

Two changes, together:

1. **Make the always-loaded tier actually always-loaded.** Core memory is
   injected into every session's context rather than fetched by tool call.
2. **Add a `working` tier** — a small, agent-curated rolling scratchpad of
   live threads and pending intentions ("I noticed X during dreamtime, raise
   it next time we talk"), injected on every turn.

Core and working are in context automatically. The archive remains
retrieval-on-demand; that is what `list` and `search` are for.

## Why a new tier rather than a bigger core

Core holds *stable facts*. Working holds *pending intentions*, which have a
lifecycle — created, surfaced, dropped — and a different write discipline.
Folding them into one file recreates the ambiguity that makes a model file
things in the wrong place, and denies working memory an independent budget,
which is the entire point of a tier that must stay small.

## Non-goals

- **No structured item model.** Working memory is free-form markdown, edited
  through the existing `read`/`save`/`append`/`edit` verbs. Ids, timestamps,
  and `add`/`drop` actions were considered and rejected as a second write
  path parallel to the memory verbs, guarding against a failure (a botched
  `edit` match) the model recovers from by re-reading. Revisit if free-form
  curation proves unreliable in practice.
- **No shared working memory.** Pending intentions are per-agent by nature,
  and the shared file has no owner to curate them.
- **Shared memory is not injected.** Only core and working become
  always-loaded. Shared (`~/.achates/memory.md`) stays behind a tool call, so
  universal user facts remain subject to the same "the model didn't fetch it"
  failure this work fixes for the other two tiers. Injecting it would mean a
  fourth always-loaded block and a budget for a file no single agent owns;
  deferred as a separate decision.
- **No changes to the archive** — same directory, same verbs, same
  on-demand retrieval.
- **No mobile memory browser support.** See Deferred below.
- **No hard budget enforcement.** Both tiers keep the existing soft-nudge
  pattern (`CoreBudgetNote`).

## Design

### 1. Storage and budgets

New file per agent: `~/.achates/agents/{name}/working.md`. A sibling of
`memory.md`, deliberately *not* inside the archive directory
(`agents/{name}/memory/`) — the archive is a searchable corpus, this is a
single curated page.

| Tier    | Default budget | Resolution chain                                                       |
|---------|----------------|------------------------------------------------------------------------|
| Working | ~500 tokens    | `**Working Budget:**` → `memory.default_working_budget_tokens` → const |
| Core    | 8000 → **2000**| `**Memory Budget:**` → `memory.default_budget_tokens` → const (unchanged chain, new default) |

Both are soft nudges appended to a write result, following the existing
`CoreBudgetNote` shape.

**Core's tightening is intentional churn, not a silent default swap.** Agents
whose `memory.md` already exceeds 2000 tokens begin receiving the over-budget
nudge on their next write. Dreamtime step 6 already mandates that an
over-budget run must leave core smaller than it started, so existing agents
converge over several nights without a data migration.

### 2. Injection

A new `MemoryContext` class alongside `TemporalContext`, using the same
`AgentOptions.TransformContext` slot. `TemporalContext.InjectIntoUserMessage`
(private today, `src/Achates.Server/TemporalContext.cs:143`) is lifted into a
shared helper; both transforms need it.

`MemoryContext.CreateTransform(corePath, workingPath, includeWorking)` returns
a `Func<CompletionContext, CompletionContext>` matching
`TemporalContext.CreateTransform`'s shape, so the two compose at each call
site. `includeWorking: false` is what the consult path passes.

Injection rides the transform path rather than the system prompt for two
reasons. `AgentDefinition.SystemPrompt` is built once per agent at load
(`src/Achates.Server/GatewayService.cs:577`) and shared across every session,
so a system-prompt injection would serve a stale scratchpad — stale in
exactly the long-running-thread case this feature exists for. And the
Anthropic cache breakpoints in `OpenRouterProvider` sit on the system prompt
as a byte-stable prefix, which a mid-session edit would invalidate.
`TemporalContext`'s doc comment records this same lesson from the date block.

**The two tiers inject at different points, because they have different
volatility.**

- **Core → head.** Prepended to the *first* user message in the outgoing
  payload, recomputed from disk each time. Core changes nightly at dreamtime,
  so on essentially every turn the bytes are identical and the block sits
  inside the stable cached prefix — paid once per session rather than per
  turn. When dreamtime does change it, the next session re-caches once, which
  it would have anyway.
- **Working → tail.** Prepended to the *latest* user message, alongside the
  temporal note. It is ~500 tokens and is the tier the agent actively edits
  mid-conversation, so freshness is the point and per-turn re-processing is
  cheap.

The rejected alternative was uniform tail injection for both tiers: one
injection point and one concept to explain, at roughly 2500 uncached tokens
per user turn on every transport instead of ~500.

Consequences of this design:

- Both transforms cache on the latest user message's timestamp, so
  within-turn tool iterations keep a stable prefix. An agent that writes to
  working memory mid-turn will not see it reflected until the next user turn —
  acceptable, since it just wrote the content itself.
- If `SessionCompactor` summarizes away the original first message, "first
  user message in the payload" resolves to whatever survives. Compaction
  invalidates the prefix regardless.
- Core is re-read from disk once per new user turn, not once per completion
  request; within-turn iterations reuse the cached block.
- If core memory *does* change mid-session — a user editing it through the
  mobile browser, since dreamtime runs overnight — the head block changes and
  the whole prefix re-caches for the rest of that session. Accepted: the write
  is rare, and serving stale core for the remainder of a session would be
  worse than paying for one re-cache.
- For cron and dreamtime sessions the head and tail are the same single
  hidden user message. Both blocks land on it.

### 3. Injection sites

Three call sites set `TransformContext` today:

| Site | Core | Working |
|---|---|---|
| `Mobile/MobileTransport.cs:2230` (user sessions) | yes | yes |
| `Cron/CronService.cs:379` (scheduled + dreamtime) | yes | yes |
| `Chat/AgentRuntimeFactory.cs:29` (agent-to-agent consult) | yes | **no** |

Working memory is withheld from the consult path deliberately: injecting
"raise this when we next talk" into a one-round agent-to-agent consult risks
the agent raising a user-directed intention with another *agent*, and a
single-round consult has no "next time" to speak of.

`AgentOptions` has one transform slot, so the memory transform composes with
the temporal one at each site.

### 4. Tool surface

`scope` gains a third value, `working`, reusing the existing
`read`/`save`/`append`/`edit` verbs. No new actions.

The roleplay path needs care. `_agentOnlySchema` has no `scope` parameter at
all, and `src/Achates.Server/Tools/MemoryTool.cs:104` hard-forces
`scope = "agent"` whenever `_sharedEnabled` is false — a deliberate defense
against a model ignoring the schema, which as written would silently swallow
every `working` write on in-character agents. The forcing inverts: honor the
requested scope, and downgrade only `shared` → `agent` when shared is
disabled.

- `_bothScopesSchema`: `scope` enum becomes `[agent, working, shared]`.
- `_agentOnlySchema`: gains a `scope` enum of `[agent, working]`.
- Unscoped `read` in shared-enabled mode returns shared + core + working.

`AgentDefinition` gains `WorkingMemoryPath` and `WorkingBudgetTokens` —
explicit rather than derived from `MemoryPath` the way `_archiveDir` is,
because `MemoryContext` needs the same path and must not re-derive it
independently. Plumbing follows the existing budget chain:
`AgentConfig.WorkingBudgetTokens`, a `**Working Budget:**` case in
`AgentLoader`'s capability parser and its serializer, and
`MemoryConfig.DefaultWorkingBudgetTokens`.

### 5. Prompt changes

Two existing prompts assert things that are false today and become true with
this change. Both are rewritten, not merely extended.

**`SystemPrompt.cs:69`** — "Read memory at the start of new conversations to
recall prior context." Once core is injected, obeying this burns a tool call
re-reading what is already in context. Replaced with a statement that core
and working memory are already present and that the archive is what needs
fetching. Both the shared-enabled and shared-disabled variants change.

**Dreamtime instructions (`CronService.cs:38-84`)** — describes *two* tiers
and calls core "always loaded at the start of every conversation". Becomes
three tiers. Step 2's careful instruction to "Read your core memory ONCE, up
front … it is large and every full read is persisted into this session,
bloating it" largely dissolves: core now arrives in context for free, so
dreamtime stops paying that cost. This is a side benefit of the change, not
a workaround.

Dreamtime gains a working-memory step:

- Prune items already surfaced or resolved.
- Promote anything durable into core, or relocate it to the archive.
- Add items for things noticed tonight that are worth raising next time.

The nightly review is the main producer of this tier.

**The injected working block carries its own framing.** The obvious failure
mode of an auto-loaded pending-items list is an agent that opens every
conversation reciting it. The block instructs: raise these when the
conversation makes them relevant, not all at once and not as a checklist;
remove an item once it has been surfaced or resolved.

## Testing

- **`MemoryContextTests`** (new), mirroring `TemporalContextTests`: core
  lands on the first user message and working on the last; both no-op on
  empty or missing files; both recompute only when the latest user timestamp
  changes; composition with the temporal transform produces the expected
  order.
- **`MemoryToolTests`**: working-scope round-trips for each verb; the
  working budget nudge; and the case that matters most — working writes
  succeed while `shared` is still refused when `SharedMemory: false`.
- **`SystemPromptTests`**: the rewritten memory section, both variants.
- **`OpenRouterCacheControlTests`**: the invariant the design rests on — an
  unchanged `memory.md` produces a byte-identical head across turns.
- **`AgentLoaderTests`** / **`ConfigLoaderTests`**: the `**Working Budget:**`
  capability round-trip and the new config default.
- **`AgentRuntimeFactoryTests`**: the consult path injects core but not
  working.

## Deferred

**The mobile memory browser.** `HandleMemoryList` / `Get` / `Set` key scope
as `"shared"` or a bare *agent name*
(`src/Achates.Server/Mobile/MobileTransport.cs:1759`) — a different namespace
from the tool's scopes, with no room for a second file per agent. Exposing
`working.md` there needs a key convention (e.g. `{agent}:working`) plus a
matching iOS client change. Working memory is agent-curated and
dreamtime-maintained without it. Recorded as a decision, not an oversight.

## Documentation

- `docs/configuration.md` — the `working` scope, `working.md` data path, the
  `**Working Budget:**` capability, and `memory.default_working_budget_tokens`.
  Note the changed core default.
- `src/Achates.Server/CLAUDE.md` — `MemoryContext` in the tool/prompt
  section; `AgentDefinition`'s new fields.
- `README.md` — only if the memory tiers are described in user-facing terms.
