# Achates.Agent

Stateful agent runtime engine (messages, tools, event streaming). Depends on `Achates.Providers`.

- `AgentRuntime` — one instance = one conversation thread. Stateful with message history.
- `PromptAsync()` returns `AgentEventStream`; `ContinueAsync()` resumes after tool results
- `Steer()` interrupts current tool execution; `FollowUp()` queues for after current turn
- `AgentOptions` — model, system prompt, tools, completion options, metadata, context transform hooks
- `ISessionStore` — interface for persisting conversation history by session key (Load/Save/Delete)
- `SessionCompactor` — proactive compaction before each turn. Estimates tokens (uses provider's reported input count + char heuristic), summarizes oldest messages via LLM when over 80% of context window, falls back to truncation on failure. Preserves tool call/result pairs. `SummaryMessage` type holds the summary.
- Workbook content converts to preview text in `MessageConversion`; compaction counts that preview and supplies workbook IDs/names to the summarizer. Original workbook retention is handled by the server's session-scoped store, independent of compacted messages.

- Before starting or continuing, the loop closes missing results in the trailing interrupted tool batch with explicit unknown-outcome errors. Completed results stay intact and missing calls are never automatically replayed, since side effects may already have occurred.
