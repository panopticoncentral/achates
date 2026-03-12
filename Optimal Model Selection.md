# Optimal model selection for a multi-agent personal assistant on OpenRouter

**The most cost-effective multi-agent architecture on OpenRouter uses 4–6 model tiers**, routing ~80% of traffic to models costing under $0.40/M input tokens while reserving frontier models for complex reasoning and planning. As of March 2026, OpenRouter hosts **298+ models from 60+ providers** with no markup on inference pricing, making it ideal for this tiered approach. The key insight from production deployments (including OpenClaw, which processes 360B tokens/week on OpenRouter) is that aggressive model tiering can cut costs by **50–80%** compared to single-model architectures. Below is a role-by-role breakdown with specific model IDs, pricing, and architectural guidance.

---

## The routing layer: fast and disposable

The router is the highest-volume, lowest-stakes component. It classifies intent, selects the right sub-agent, and must respond in **<500ms** with **90%+ accuracy**. Every extra dollar here multiplies across every request.

**Primary pick: `google/gemini-2.0-flash-001`** — $0.10/$0.40 per M tokens, 1.049M context, native function calling. At **169 tokens/sec** and ~0.5s TTFT, it hits the latency target comfortably. Use structured output mode to return a routing enum. The 1M context window means you can stuff generous conversation history into routing decisions without truncation.

**Budget alternative: `openai/gpt-4.1-nano`** — $0.10/$0.40 per M tokens, 1.048M context, native function calling and vision. This is OpenAI's purpose-built lightweight model, fast enough for classification.

**Ultra-budget: `openai/gpt-oss-20b`** — **$0.02/$0.10** per M tokens, 131K context, with function calling and reasoning tags. At 50x cheaper than GPT-4.1 Mini, this is worth testing for simple routing tasks.

**Long-term strategy**: Once you accumulate routing logs, fine-tune a small model (Qwen 1.7B or DeBERTa-v3) for local intent classification at ~10–50ms latency and near-zero cost. NVIDIA's LLM Router Blueprint validates this approach using Qwen 1.7B. Keep the LLM-based router as a fallback for ambiguous or novel intents.

OpenRouter also offers `openrouter/auto`, a NotDiamond-powered meta-router that selects from a curated pool (Claude Sonnet 4.5, Opus 4.5, GPT-5.1, Gemini 3 Pro, DeepSeek 3.2) at no additional fee. This is useful for prototyping before you build custom routing logic.

| Model | Input $/M | Output $/M | Context | Latency |
|---|---|---|---|---|
| `openai/gpt-oss-20b` | $0.02 | $0.10 | 131K | ~200ms |
| `google/gemini-2.0-flash-001` | $0.10 | $0.40 | 1.049M | ~500ms |
| `openai/gpt-4.1-nano` | $0.10 | $0.40 | 1.048M | ~400ms |
| `openai/gpt-4.1-mini` | $0.40 | $1.60 | 1.048M | ~500ms |

---

## The orchestrator: where reasoning quality pays for itself

The orchestrator decomposes complex requests, sequences sub-agent calls, handles error recovery, and decides when to escalate. This is the **one role where you should not optimize for cost** — a bad plan wastes more tokens downstream than a good model costs upstream.

**Primary pick: `anthropic/claude-sonnet-4.6`** — $3.00/$15.00 per M tokens, **1M context**, functions + vision + reasoning. Claude Sonnet achieves **82% on SWE-bench Verified** and Anthropic's own guidance says it handles ~90% of tasks that Opus can. Its 1M context window accommodates full conversation history plus tool results without summarization. It excels at structured planning, and the extended thinking mode gives you a reasoning trace for debugging orchestration logic.

**For the hardest 10%: `anthropic/claude-opus-4.6`** — $5.00/$25.00, 1M context. Reserve this for multi-step plans involving 5+ sub-agents or tasks requiring deep domain reasoning. The cost delta from Sonnet is 1.7x, but you can gate it behind a complexity classifier.

**Best value alternative: `google/gemini-2.5-pro`** — $1.25/$10.00, **1.049M context**, functions + vision + reasoning + audio. Leads the Artificial Analysis Intelligence Index (score 57) and the LMArena ranking. At **2.4x cheaper than Claude Sonnet** on input tokens, it's the best option if your orchestration doesn't need Anthropic's particular strengths in structured agent workflows.

**Budget orchestrator: `deepseek/deepseek-r1-0528`** — $0.50/$2.15, 65K context. Frontier-tier reasoning (97.3% MATH-500, 79.8% AIME) at **6x cheaper than Gemini 2.5 Pro**. However, known reliability issues: multi-turn degradation after 5–7 exchanges and occasional API instability. Best used for single-shot planning tasks with a fallback to Claude, not as a persistent orchestration backbone.

**Architectural note**: A Google/MIT paper (March 2026) found that **centralized orchestration outperforms decentralized** topologies, and that agents succeed more on focused tasks than when selecting from many tools. This supports the pattern of a single orchestrator routing to specialized sub-agents rather than giving one agent all tools.

---

## Function calling and structured output: the agent's hands

The tool-use agent executes function calls, parses structured responses, and handles multi-step tool chains. Reliability matters more than raw intelligence here — a hallucinated function argument is worse than a slow response.

**Primary pick: `openai/gpt-4.1`** — $2.00/$8.00, 1.048M context. OpenAI has the most mature function calling API, with native `structured_outputs` that guarantee valid JSON schema compliance. GPT-4.1 supports parallel tool calls and has years of battle-tested function-calling infrastructure.

**Best benchmark performance: `anthropic/claude-sonnet-4.6`** — Claude Sonnet 4 scored **70.29% on BFCL V4** (Berkeley Function Calling Leaderboard), second only to GLM-4.5. On the more realistic **MCPMark benchmark** (127 tasks, avg 16.2 turns, 17.4 tool calls per task), GPT-5 achieved a **52.6% pass@1** — roughly 2x Claude Opus's 29.9%.

**Budget tool caller: `openai/gpt-4.1-mini`** — $0.40/$1.60, 1.048M context. Strong function calling at **5x cheaper** than full GPT-4.1. For sub-agents that call 1–2 tools per turn (API lookups, database queries, calendar checks), this is sufficient.

**Open-source option: `qwen/qwen3-coder`** — $0.22/$0.95, 262K context. On MCPMark, Qwen-3-Coder achieved the cheapest cost per successful task ($147 vs. GPT-5's $242), making it the best value for high-volume agentic tool use.

OpenRouter standardizes tool calling across all providers using the OpenAI function-calling format. When you include `tools` or `tool_choice` in your request, OpenRouter **automatically routes only to providers that support tool use**. Enable `parallel_tool_calls: true` for concurrent execution and use `require_parameters: true` in provider preferences to ensure schema support.

---

## Conversational response generation: the user-facing voice

This agent produces the final natural-language response the user sees. It needs to be coherent, natural, and appropriately detailed — but doesn't need to do heavy reasoning since the orchestrator and specialists handle that.

**Primary pick: `anthropic/claude-sonnet-4.5`** — $3.00/$15.00, 1M context. Claude's conversational output is widely considered the most natural and nuanced among frontier models. Sonnet 4.5 grew **51% in weekly usage** on OpenRouter recently (now processing 530B tokens/week), signaling strong user preference.

**Budget voice: `openai/gpt-4.1-mini`** — $0.40/$1.60. For most conversational responses where the content is already determined by the orchestrator (the model just needs to phrase it well), GPT-4.1 Mini delivers good output quality at **7.5x lower cost** than Sonnet.

**Ultra-budget voice: `deepseek/deepseek-v3.2`** — $0.28/$0.40, 164K context. DeepSeek V3.2 processes **775B tokens/week** on OpenRouter (4th most popular model) and supports functions + reasoning. At roughly **10x cheaper** than Claude Sonnet on input, it's the best option if you're optimizing hard on cost.

**Practical pattern**: Use Claude Sonnet or GPT-4.1 for responses to complex or sensitive queries, and route simple acknowledgments/confirmations to GPT-4.1 Mini or DeepSeek V3.2. The orchestrator's complexity classification drives this.

---

## Summarization and context compression: managing the window

As conversations grow, you need a model that can compress history, summarize documents, and manage context windows cheaply. The key metric is **cost per context token** since you're processing large volumes of text.

**Primary pick: `google/gemini-2.5-flash`** — $0.30/$2.50, **1.049M context**. The 1M context window means you can ingest entire conversation histories or long documents without chunking. With context caching, cached tokens cost **$0.0375/M** — a 90% discount. This makes Gemini Flash the clear winner for summarization workloads.

**Ultra-budget: `google/gemini-2.0-flash-001`** — $0.10/$0.40, 1.049M context. For bulk summarization where you don't need the reasoning improvements of 2.5, this is 3x cheaper on input.

**Quality-critical: `anthropic/claude-sonnet-4.6`** — When summarization quality matters (e.g., preserving technical nuance in code reviews or legal documents), Claude produces the most faithful summaries. Use it selectively.

**For enormous contexts: `x-ai/grok-4.1-fast`** — $0.20/$0.50, **2M context window**. Or if available through Meta providers, Llama 4 Scout supports **10M tokens** at $0.11/M. These are useful for full-codebase analysis or very long document processing.

**Context management strategy**: Research on AgentDiet shows trajectory reduction — removing redundant/expired information from agent context — cuts costs by **50%+ without performance loss**. Combine this with rolling summarization using Gemini Flash and you can maintain indefinite conversations at low cost.

---

## Code generation: the developer sub-agent

**Primary pick: `anthropic/claude-sonnet-4.6`** — **82% SWE-bench Verified**, top-tier on Aider Polyglot. Claude dominates agentic coding benchmarks and produces clean, well-structured code. For a personal assistant that writes and modifies code, this is the default.

**Heavy lifting: `anthropic/claude-opus-4.6`** — **80.8% SWE-bench** (holds the #1 overall spot), 89.4% Aider Polyglot. Use for complex refactoring, multi-file changes, or architectural decisions.

**Best value: `deepseek/deepseek-v3.2`** — $0.28/$0.40. Near-frontier coding quality at **~10x cheaper** than Claude Sonnet. Processes 775B tokens/week on OpenRouter, suggesting strong real-world adoption for coding tasks.

**Dedicated coding models**: `qwen/qwen3-coder` ($0.22/$0.95, 262K context) and `mistralai/devstral-2512` ($0.05/$0.22, 256K context) are purpose-built for code. Devstral is particularly interesting at **$0.05/M input** — 60x cheaper than Claude Sonnet.

**Tiered approach**: Route boilerplate/CRUD generation to DeepSeek V3.2 or Devstral, standard coding tasks to Claude Sonnet, and complex architectural work to Claude Opus. A compiler-design-aware complexity classifier could distinguish these tiers.

---

## Embedding and retrieval for RAG pipelines

OpenRouter exposes embedding models through the same API, making it straightforward to add retrieval to your architecture.

| Model | Cost/M tokens | MTEB Score | Dimensions | Best For |
|---|---|---|---|---|
| `google/gemini-embedding-001` | ~$0.004/1K chars | **68.32** | 768–3072 | Best API retrieval quality |
| `qwen/qwen3-embedding-8b` | $0.01/M | **70.58** | 32–4096 | Best multilingual, open-weight |
| `openai/text-embedding-3-large` | $0.13/M | 64.6 | 256–3072 | Most consistent across tasks |
| `openai/text-embedding-3-small` | $0.02/M | 62.26 | 512–1536 | Best price/performance |

**Recommendation**: Use `google/gemini-embedding-001` for retrieval — it leads the MTEB English retrieval benchmark (67.71) at near-zero cost. For multilingual use cases, `qwen/qwen3-embedding-8b` tops the multilingual MTEB at $0.01/M. Both support Matryoshka dimensions, letting you trade off vector size for speed.

---

## Specialist sub-agents and domain models

For domain-specific sub-agents, match the model to the task profile:

- **Web search/real-time info**: `google/gemini-3-flash-preview` ($0.50/$3.00) and `x-ai/grok-4` ($3.00/$15.00) have **native web search** capability — no external tool needed. Gemini 3 Flash is the cost leader here.
- **Vision/multimodal**: Most frontier models now support vision. For cost-sensitive image analysis, `openai/gpt-4.1-nano` ($0.10/$0.40) or `mistralai/ministral-8b-2512` ($0.15/$0.15) handle basic vision tasks cheaply.
- **Math/STEM reasoning**: `openai/o3-mini` ($1.10/$4.40) excels at STEM problems with its reasoning architecture. DeepSeek R1 ($0.55/$2.19) offers 97.3% MATH-500 at half the price.
- **Agentic swarms**: `moonshotai/kimi-k2.5` ($0.60/$3.00) supports **1,500 parallel tool calls** and is purpose-built for agent swarms.

---

## Putting it together: the recommended stack

Here is a concrete model assignment with OpenRouter model IDs and approximate monthly cost at 1M requests/month (assuming 500 input + 200 output tokens per routing call, 2K+1K for other roles):

| Role | Model ID | Input/Output $/M | Monthly est. | Fallback |
|---|---|---|---|---|
| **Router** | `google/gemini-2.0-flash-001` | $0.10 / $0.40 | ~$130 | `openai/gpt-4.1-nano` |
| **Orchestrator** | `anthropic/claude-sonnet-4.6` | $3.00 / $15.00 | ~$900* | `google/gemini-2.5-pro` |
| **Tool Calling** | `openai/gpt-4.1-mini` | $0.40 / $1.60 | ~$200 | `openai/gpt-4.1` |
| **Conversation** | `deepseek/deepseek-v3.2` | $0.28 / $0.40 | ~$170 | `anthropic/claude-sonnet-4.5` |
| **Summarization** | `google/gemini-2.5-flash` | $0.30 / $2.50 | ~$140 | `google/gemini-2.0-flash-001` |
| **Code Gen** | `anthropic/claude-sonnet-4.6` | $3.00 / $15.00 | ~$450* | `deepseek/deepseek-v3.2` |
| **Embeddings** | `google/gemini-embedding-001` | ~$0.004/1K chars | ~$20 | `openai/text-embedding-3-small` |

*\*Orchestrator and code gen costs assume ~10% of traffic reaches these tiers after routing.*

**Key implementation details for OpenRouter**:

Use **model fallbacks** via the `models` array instead of single `model` for resilience. Set `provider.sort.by: "throughput"` with `partition: "none"` when you care more about speed than a specific model. Use `max_price` to cap per-request cost. The `:floor` suffix on model slugs (e.g., `anthropic/claude-sonnet-4.6:floor`) routes to the cheapest available provider, while `:nitro` prioritizes throughput.

For **confidence-based escalation**, implement a cascade: try the cheap model first, check output confidence (e.g., logprobs or structured output validation), and escalate to a premium model only when confidence is low. AWS prescriptive guidance validates this pattern, and practitioners report **88% cost reduction** with tiered routing.

**Prompt caching** is critical for multi-turn agents. Anthropic, OpenAI, and Google all offer cached token pricing at 50–90% discounts. Structure your system prompts to maximize cache hits — put stable instructions first, variable content last.

## Conclusion

The optimal architecture isn't about finding one "best" model — it's about **matching model capability to task complexity at each layer**. Three principles drive the design: route aggressively to the cheapest viable model (80% of requests should hit Tier 1–2 models under $0.50/M); invest in orchestration quality since a good plan saves downstream tokens; and use OpenRouter's built-in fallback chains for resilience rather than building your own retry logic. The models in this stack represent March 2026 pricing, which has dropped roughly **80% year-over-year** — revisit selections quarterly as new models (GPT-5.4, Gemini 3.1 Pro, Claude Opus 4.6) continue to push the cost-performance frontier downward.