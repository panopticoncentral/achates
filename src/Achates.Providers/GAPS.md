# Gap Analysis: Achates.Providers.OpenRouter vs achates-second-draft

This document compares the current `Achates.Providers.OpenRouter` implementation against the earlier `achates-second-draft` sibling repository. The second draft took a different architectural approach — a generic `OpenAiCompletionsApi` layer targeting any OpenAI-compatible provider — and built several higher-level abstractions on top of the raw chat completions API. This report examines each capability gap, explains what it does, why it matters, and whether it should be carried forward.

---

## Starting Point

The current implementation has solid wire-format coverage. `OpenRouterClient` exposes model listing, model count, non-streaming completions, and streaming completions. `ChatCompletionRequest` covers the full parameter surface including sampling controls, tool definitions, provider preferences, reasoning config, response format, and metadata. The model types capture all OpenRouter fields. JSON serialization uses source generation for AOT compatibility. Error handling extracts structured error responses into `OpenRouterException`.

What's missing is not API surface — it's the higher-level machinery that makes the raw API usable for building agents and interactive applications.

---

## 1. Event-Based Streaming

### What the second draft has

An `AssistantMessageEventStream` class that wraps `IAsyncEnumerable<AssistantMessageEvent>` and provides a `ResultAsync` property for the final assembled message. Events are pushed through a `Channel<T>` for thread-safe producer/consumer streaming. Nine distinct event types form a hierarchy:

- **TextStartEvent / TextDeltaEvent / TextEndEvent** — text content lifecycle
- **ThinkingStartEvent / ThinkingDeltaEvent / ThinkingEndEvent** — reasoning content lifecycle
- **ToolCallStartEvent / ToolCallDeltaEvent / ToolCallEndEvent** — tool call lifecycle
- **DoneEvent** — stream complete with stop reason and final `AssistantMessage`
- **ErrorEvent** — stream error with details

Each event carries a `ContentIndex` (position in the content block array) and a `Partial` snapshot of the message assembled so far. This lets consumers react to specific content types without parsing raw chunks.

### What the current implementation has

`StreamChatCompletionAsync` returns `IAsyncEnumerable<ChatCompletionChunk>` — the raw SSE data deserialized into the OpenRouter response format. Consumers must inspect `chunk.Choices[0].Delta.Content`, `Delta.ToolCalls`, and `Delta.Reasoning` themselves and maintain their own accumulation state.

### Why it matters

Raw chunks are sufficient for simple text streaming (the chat TUI does this now). But as soon as you need to handle tool calls, reasoning content, or multiple content blocks in a single response, the consumer code becomes complex and error-prone. Every consumer reimplements the same accumulation logic. The event model pushes that complexity into a single, tested layer.

### Recommendation

Add an event streaming layer as a separate class that consumes `IAsyncEnumerable<ChatCompletionChunk>` and produces typed events. This can sit alongside the raw streaming method — consumers who want raw chunks still have them, while higher-level consumers get events. The existing `ChatCompletionChunk` types remain the wire format; the event layer is a consumer convenience built on top.

---

## 2. Reasoning and Thinking Content

### What the second draft has

A `ThinkingLevel` enum (`Minimal`, `Low`, `Medium`, `High`) with associated `ThinkingBudgets` that map each level to a token count. `SimpleStreamOptions` wraps these for easy configuration. The streaming layer detects reasoning content from multiple provider field names (`reasoning_content`, `reasoning`, `reasoning_text`) and emits `ThinkingStartEvent`/`ThinkingDeltaEvent`/`ThinkingEndEvent`. Completed thinking is stored as `ThinkingContent` blocks with a `ThinkingSignature` that records which provider field the content came from, enabling correct replay on the same model.

A `SimpleOptionsHelper.AdjustMaxTokensForThinking` method ensures the total token budget (thinking + output) fits within model limits, automatically balancing the allocation.

### What the current implementation has

`ChatReasoningConfig` with `Effort`, `MaxTokens`, and `Exclude` fields on the request. `ChatDelta.Reasoning` captures the reasoning field from streaming chunks. But there's no thinking content type, no level abstraction, no budget management, and no signature tracking.

### Why it matters

Extended thinking is a significant capability differentiator across models (Claude, DeepSeek R1, QwQ, o1/o3). Without a thinking content model, reasoning output is either discarded or treated as plain text. Without signature tracking, replaying a conversation on the same model may fail because the provider expects its specific thinking format back. Without budget management, users must manually calculate token allocations.

### Recommendation

Add a `ThinkingContent` type and integrate thinking detection into the event streaming layer (gap #1). The `ThinkingLevel`/`ThinkingBudgets` abstraction is a convenience that can come later — the critical piece is correctly parsing and preserving thinking content from responses.

---

## 3. Tool Argument Validation

### What the second draft has

A `ToolValidator` class that validates tool call arguments against the tool's JSON Schema definition using `System.Text.Json.Schema`. It recursively collects validation errors with path information and produces detailed error messages. If schema validation infrastructure fails, it falls back to trusting the LLM output.

### What the current implementation has

`ChatTool` and `ChatFunction` define tools with a `Parameters` JSON Schema field. `ChatToolCall` and `ChatToolCallFunction` capture tool calls from responses. But there's no validation step — arguments are accepted as-is.

### Why it matters

LLMs produce malformed tool arguments frequently enough that validation matters for reliability. Without it, tools receive invalid input, produce confusing errors, and the LLM gets unhelpful feedback. Schema validation catches these problems early with precise error messages that can be fed back to the LLM for self-correction. The graceful fallback ensures the validator never blocks a valid-but-unexpectedly-formatted call.

### Recommendation

Add a `ToolValidator` utility. This is a standalone class with no dependencies on the streaming or event layer — it can be added independently. It does require a JSON Schema validation library (`System.Text.Json.Schema` or similar). Since tool use is central to agent workflows, this is high-value.

---

## 4. Partial JSON Parser

### What the second draft has

A `PartialJsonParser` with two methods: `ParseStreamingJson` (returns `Dictionary<string, object?>`) and `ParseStreamingJsonElement` (returns `JsonElement?`). It uses a two-stage approach: try standard deserialization first (fast path for complete JSON), then repair incomplete JSON by tracking open braces, brackets, and string state to append the correct closing characters.

### What the current implementation has

Nothing. Tool call arguments arrive as string fragments during streaming. The delta's `Function.Arguments` field contains a partial JSON string that grows with each chunk.

### Why it matters

During streaming, tool call arguments arrive incrementally. A tool call like `{"city": "San Francisco", "units": "celsius"}` might arrive as `{"city": "San Fr` → `ancisco", "uni` → `ts": "celsius"}`. Without partial parsing, you can't display or validate the arguments until the stream completes. With it, you can show the user what the model is thinking as it constructs the call, and detect problems earlier.

This is a nice-to-have for display but becomes important when implementing tool call events in the streaming layer — the event needs to carry the partially-parsed arguments.

### Recommendation

Add as part of the event streaming layer (gap #1). It's a small, self-contained utility (~130 lines) with no external dependencies.

---

## 5. Context Overflow Detection

### What the second draft has

A `ContextOverflow` static class with 15 compiled regex patterns matching common overflow error messages from various providers ("prompt is too long", "exceeds the context window", "too many tokens", etc.). It also detects providers that return 400/413 with no body (Cerebras, Mistral style) and implements silent overflow detection by comparing input tokens against the model's context window.

### What the current implementation has

`OpenRouterException` captures error codes and messages, but there's no classification of error types. A consumer would need to parse the error message string themselves to determine if the error is a context overflow versus a rate limit versus an auth failure.

### Why it matters

Context overflow is the most common error in long-running conversations and agent loops. Detecting it programmatically enables automatic mitigation: truncating conversation history, summarizing older messages, or switching to a model with a larger context window. Without detection, the error surfaces as a generic failure that requires user intervention.

The silent overflow case is particularly insidious — some providers accept the request but produce degraded output because they silently truncated the input. Token counting against the context window catches this.

### Recommendation

Add a `ContextOverflow` utility. It's standalone (~60 lines, no dependencies) and immediately useful. The regex patterns are collected from real provider behavior and represent significant debugging time saved.

---

## 6. Message Transformation

### What the second draft has

A `MessageTransformer` class that normalizes conversation history for cross-provider compatibility:

1. **Thinking block conversion** — When replaying a conversation on a different model, thinking blocks are converted to plain text (since the target model won't understand the original model's thinking format). Same-model replay preserves thinking blocks with signatures.

2. **Tool call ID normalization** — Different providers use different ID formats. The transformer remaps IDs to maintain consistency when switching models.

3. **Synthetic tool results** — If a conversation contains assistant tool calls without corresponding tool results (e.g., because the conversation was interrupted), the transformer inserts synthetic "No result provided" results. Many providers reject conversations with orphaned tool calls.

### What the current implementation has

Messages are passed through as-is. `ChatMessage` with `JsonElement?` content is the wire format.

### Why it matters

This matters when building multi-model experiences — switching between models during a conversation, or using different models for different tasks within the same conversation history. Without transformation, switching models can produce errors (orphaned tool calls, unrecognized thinking formats) or degraded results (thinking blocks treated as text by a model that doesn't understand them).

The synthetic tool result insertion is particularly important: most OpenAI-compatible APIs reject a conversation where an assistant message contains tool calls but no tool results follow. This happens naturally when a user interrupts a tool-calling flow or when an error occurs mid-execution.

### Recommendation

Defer until multi-model support is needed. This is valuable but tightly coupled to a normalized message model (content blocks rather than raw `JsonElement`). Adding it prematurely would require either introducing a parallel message type system or changing `ChatMessage.Content` from `JsonElement?` to something typed — both significant changes that should be driven by actual multi-model requirements.

---

## 7. Usage and Cost Calculation

### What the second draft has

A `Usage` record with `Input`, `Output`, `CacheRead`, `CacheWrite`, and `TotalTokens` fields, plus an embedded `UsageCost` record with per-category dollar amounts. The `Model` class has a `CalculateCost(Usage)` method that multiplies token counts by per-million-token pricing to produce dollar costs. This enables per-message and cumulative cost tracking.

### What the current implementation has

`ChatUsage` with `PromptTokens`, `CompletionTokens`, `TotalTokens`, plus `PromptTokensDetails` and `CompletionTokensDetails` as `JsonElement?` for future extensibility. `OpenRouterPricing` stores prices as strings. No cost calculation.

### Why it matters

Cost visibility is important for any application that makes API calls at scale. Without it, users have no feedback on spending until they check their OpenRouter dashboard. Per-message cost tracking enables budget limits, cost-per-conversation analytics, and informed model selection ("this model costs 3x more but is only slightly better").

The cache token breakdown matters for prompt caching optimization — knowing how many tokens hit the cache versus missed tells you whether your caching strategy is working.

### Recommendation

Add a `UsageCost` calculation utility. The pricing data is already available in `OpenRouterPricing` (as strings that need parsing to decimals). A `CalculateCost` method on a model or as a static helper is straightforward. The cache token details can be extracted from the `PromptTokensDetails` / `CompletionTokensDetails` `JsonElement` fields that are already in `ChatUsage`.

---

## 8. Cache Retention Configuration

### What the second draft has

A `CacheRetention` enum (`None`, `Short`, `Long`) included in `StreamOptions`. This controls whether and how long the provider should cache the prompt for reuse in subsequent requests.

### What the current implementation has

Nothing. OpenRouter supports prompt caching through provider-specific mechanisms, but there's no way to request it from the client.

### Why it matters

Prompt caching can reduce costs by 75-90% for repeated prefixes (system prompts, conversation history). For agent loops that make many calls with the same long system prompt, cache retention pays for itself quickly. Without explicit control, caching behavior depends on provider defaults.

### Recommendation

This is provider-specific. OpenRouter's caching is controlled through provider preferences and is somewhat implicit. Check what OpenRouter actually supports for cache control before adding an abstraction — the second draft's enum may have been designed for direct Anthropic/OpenAI API access rather than OpenRouter specifically.

---

## 9. Unicode Sanitizer

### What the second draft has

A `UnicodeSanitizer` with a `SanitizeSurrogates` method that detects and removes unpaired UTF-16 surrogate characters. Uses a fast-path scan that short-circuits for clean strings (the common case), and a repair path that rebuilds the string only when necessary.

### What the current implementation has

Nothing.

### Why it matters

Unpaired surrogates cause `JsonSerializer` to throw. They appear occasionally in LLM output (especially when models hallucinate binary data or emoji sequences) and in user input copied from certain sources. Without sanitization, a single bad character in a multi-turn conversation can crash the entire stream. The failure mode is frustrating because the error message ("invalid UTF-16") doesn't indicate which message or character is the problem.

### Recommendation

Add it. It's ~60 lines, zero dependencies, and prevents a class of crashes that are difficult to debug. Apply it to content before JSON serialization in the streaming layer.

---

## 10. SSE Parser

### What the second draft has

A reusable `SseParser` class that implements the Server-Sent Events specification: handles `event`, `data`, `id`, and `retry` fields, multi-line data concatenation, comment lines (`:` prefix), and the optional space after the colon. Returns `IAsyncEnumerable<SseEvent>` where each event has an `Event` type (defaulting to `"message"`) and `Data` string.

### What the current implementation has

Inline SSE parsing in `OpenRouterClient.StreamChatCompletionAsync` (lines 94-117): reads lines, skips blanks, checks for `data: ` prefix, handles `[DONE]` sentinel, and deserializes JSON. This works but doesn't handle comments, multi-line data, event types, or the optional space after the colon.

### Why it matters

The inline parser works correctly for OpenRouter's current SSE format, which uses single-line `data:` fields and no comments or event types. A spec-compliant parser is more robust against future API changes or different providers. More practically, extracting it into a reusable component means the streaming logic in `OpenRouterClient` becomes cleaner and any new streaming endpoints get SSE parsing for free.

### Recommendation

Low priority as a standalone change. The current inline parsing works. If building the event streaming layer (gap #1), extract SSE parsing into a reusable component at that time rather than as a separate effort.

---

## 11. Content Block Model

### What the second draft has

A `ContentBlock` base class with typed subclasses: `TextContent` (with `TextSignature`), `ThinkingContent` (with `ThinkingSignature`), and `ToolCall`. An `AssistantMessage` contains `IReadOnlyList<ContentBlock>` rather than a flat string or `JsonElement`. This enables pattern matching, type-safe access, and distinct handling of each content type.

### What the current implementation has

`ChatMessage.Content` is `JsonElement?` — it can be a JSON string (simple text) or a JSON array of content parts (for multi-modal messages). `ChatContentPart` exists for the array case with `Type`, `Text`, and `ImageUrl` fields. But there's no typed content block hierarchy for assistant responses.

### Why it matters

The `JsonElement?` approach is faithful to the wire format but pushes type checking to every consumer. Code that needs to extract text content must check `ValueKind`, handle both string and array cases, and cast appropriately. A typed model eliminates this — `message.Content.OfType<TextContent>()` is self-documenting and compile-time safe.

This becomes especially important when combined with thinking content (gap #2) and the event streaming layer (gap #1), which need to distinguish content block types during accumulation.

### Recommendation

Defer until the event streaming layer is built. Introducing a content block hierarchy is a design decision that affects many consumers. It makes most sense as part of a higher-level message model that sits above the wire-format types, rather than replacing them.

---

## Priority Summary

| Gap | Effort | Value | Recommendation |
|-----|--------|-------|----------------|
| Event-based streaming | Large | High | Build when agent/tool workflows are needed |
| Reasoning/thinking content | Medium | High | Build alongside event streaming |
| Tool argument validation | Small | High | Add now — standalone, immediately useful |
| Partial JSON parser | Small | Medium | Add with event streaming |
| Context overflow detection | Small | High | Add now — standalone, immediately useful |
| Message transformation | Large | Medium | Defer until multi-model support needed |
| Usage and cost calculation | Small | Medium | Add now — data is already available |
| Cache retention | Small | Low | Investigate OpenRouter-specific support first |
| Unicode sanitizer | Small | Medium | Add now — prevents hard-to-debug crashes |
| SSE parser | Small | Low | Extract when building event streaming |
| Content block model | Large | Medium | Defer until event streaming layer exists |

The highest-value standalone additions that can be made immediately: **tool validation**, **context overflow detection**, **cost calculation**, and **unicode sanitization**. These are small, independent utilities with no architectural dependencies. The event streaming system is the largest gap and the foundation for several other features, but it represents a significant design commitment that should be driven by concrete agent workflow requirements.
