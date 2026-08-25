# Achates.Providers

LLM provider abstraction + OpenRouter implementation. Depended on by `Achates.Agent` and `Achates.Server`.

- `IModelProvider` — interface with `GetModelsAsync()`, `GetCompletions()` (streaming), and `GenerateImageAsync()` (single image from prompt)
- `ModelProviders.Create(id)` — factory for provider instances
- Only implementation: `OpenRouterProvider` (SSE streaming, `api_key` in config or `OPENROUTER_API_KEY` env var)
- **Prompt caching**: for `anthropic/*` models the provider injects explicit `cache_control: {type: "ephemeral"}` breakpoints into the request (Anthropic does not cache automatically; OpenAI/DeepSeek/Gemini do, so they get no markers). `ConvertMessages` → `ApplyAnthropicCacheBreakpoints` places two breakpoints: one on the system prompt (byte-stable prefix) and a rolling one on the final message, so the growing conversation prefix — including large memory tool results re-sent on every within-turn tool iteration — is cached. `WithCacheBreakpoint` normalizes string content into a text-part array so the marker has a content block to attach to. Exception: when the conversation contains any `file` content part (PDF attachments) and the final message is a tool result, the rolling breakpoint moves to the last user message — OpenRouter's Anthropic adapter returns a bare HTTP 500 for the file+tool-marker combination (verified live 2026-06-10; pinned by `OpenRouterCacheControlTests`).
- Content types: `CompletionContent` base, subtypes for text, image, audio, thinking, tool calls, files. `CompletionImageContent` has optional `Url` for lightweight references (empty `Data` + URL).
- `CompletionUserContent` — input-only base. `CompletionAudioContent` is output-only (extends `CompletionContent`), `CompletionAudioInputContent` is input-only (extends `CompletionUserContent`). This asymmetry is intentional.
- Event streaming via `CompletionEventStream` using `System.Threading.Channels`
