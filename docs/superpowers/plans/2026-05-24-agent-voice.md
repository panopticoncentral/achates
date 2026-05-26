# Agent Voice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Achates agents a local, private, unmoderated voice. Each completed sentence of an assistant reply is synthesized by a locally-hosted Kokoro TTS sidecar and streamed to the iOS app for playback. Voice is per-agent (declared in `AGENT.md`), playback is opt-in per session.

**Architecture:** A new bounded module `src/Achates.Server/Speech/` adds an `ISpeechSynthesizer` abstraction (Kokoro-FastAPI HTTP impl), a `KokoroSidecarProcess` IHostedService that supervises the child process, and a `SpeechBroker` that orchestrates per-turn text → sentence → audio. Text streaming through `MobileTransport` is unchanged; speech is a parallel path that emits new `audio.block` / `audio.error` events on the same WebSocket. The iOS app gains an `AVQueuePlayer`-based `SpeechPlayer`, a per-session toggle, a per-message replay button, and a voice picker in the agent edit sheet.

**Tech Stack:** .NET 10 preview, C# 13, xUnit (in `tests/Achates.Tests`). Solution file: `Achates.slnx`. iOS: Swift 6 / SwiftUI / AVFoundation. Sidecar: `kokoro-fastapi` (external — user-installed).

**Spec:** `docs/superpowers/specs/2026-05-24-agent-voice-design.md`

**Branch:** `feature/agent-voice` (already created and checked out)

---

## File Map

**Create — server:**
- `src/Achates.Server/Speech/ISpeechSynthesizer.cs` — engine-swap seam
- `src/Achates.Server/Speech/KokoroSpeechSynthesizer.cs` — HTTP client for kokoro-fastapi
- `src/Achates.Server/Speech/KokoroSidecarProcess.cs` — IHostedService child-process supervisor
- `src/Achates.Server/Speech/SpeechBroker.cs` — per-turn orchestration
- `src/Achates.Server/Speech/SentenceSegmenter.cs` — pure unit, splits stream into sentences
- `src/Achates.Server/Speech/SpeechSanitizer.cs` — pure unit, strips markdown/code/URLs
- `src/Achates.Server/Speech/SpeechConfig.cs` — config DTOs (Speech, Sidecar)

**Create — tests:**
- `tests/Achates.Tests/Speech/SentenceSegmenterTests.cs`
- `tests/Achates.Tests/Speech/SpeechSanitizerTests.cs`
- `tests/Achates.Tests/Speech/KokoroSpeechSynthesizerTests.cs`
- `tests/Achates.Tests/Speech/SpeechBrokerTests.cs`

**Create — iOS:**
- `apple/Achates/Services/SpeechPlayer.swift` — AVQueuePlayer wrapper
- `apple/Achates/Services/VoiceRegistry.swift` — caches `voices.list` response

**Create — docs:**
- `docs/speech-setup.md` — one-time setup recipe

**Modify — server:**
- `src/Achates.Server/AchatesConfig.cs` — add `SpeechConfig` to `ToolsConfig`
- `src/Achates.Server/AgentDefinition.cs` — add `Voice` property
- `src/Achates.Server/AgentLoader.cs` — parse `**Voice:**` capability; serialize it
- `src/Achates.Server/AgentConfig` (in `AchatesConfig.cs`) — add `Voice` property
- `src/Achates.Server/GatewayService.cs` — register `KokoroSidecarProcess` IHostedService; expose synthesizer + voice resolution
- `src/Achates.Server/Mobile/MobileSession.cs` — add `SpeechEnabled` field
- `src/Achates.Server/Mobile/MobileTransport.cs` — wire `SpeechBroker` into the streaming loop; add `session.set_speech` and `voices.list` RPC handlers
- `src/Achates.Server/Tools/ProfileTool.cs` — extend `get`/`update` for voice
- `src/Achates.Server/Tools/AgentManagerTool.cs` — extend `read`/`modify`/`create` for voice
- `Achates.slnx` (only if a brand-new test subfolder needs registering — verify it doesn't)

**Modify — iOS:**
- `apple/Achates/Models/Session.swift` — add `speechEnabled` field
- `apple/Achates/Views/ChatView.swift` — speak toggle in nav bar
- `apple/Achates/Views/MessageBubbleView.swift` — per-message replay button
- `apple/Achates/Views/AgentEditView.swift` — voice picker section

**Modify — docs:**
- `CLAUDE.md` — speech section, AGENT.md example, config example, MobileTransport events, RPCs, `MobileSession.SpeechEnabled`
- `docs/configuration.md` — `tools.speech` block explanation
- `README.md` — voice-supported mention pointing at `docs/speech-setup.md`

---

## Phase 1: Pure foundation units

### Task 1: `SentenceSegmenter`

**Files:**
- Create: `src/Achates.Server/Speech/SentenceSegmenter.cs`
- Test: `tests/Achates.Tests/Speech/SentenceSegmenterTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Achates.Tests/Speech/SentenceSegmenterTests.cs`:

```csharp
using Achates.Server.Speech;

namespace Achates.Tests.Speech;

public sealed class SentenceSegmenterTests
{
    [Fact]
    public void Emits_complete_sentences_on_terminal_punctuation()
    {
        var seg = new SentenceSegmenter();
        var out1 = seg.Push("Hello there. ");
        var out2 = seg.Push("How are you? I am ");
        var out3 = seg.Push("fine!");
        var tail = seg.Flush();

        Assert.Equal(new[] { "Hello there." }, out1);
        Assert.Equal(new[] { "How are you?" }, out2);
        Assert.Empty(out3);
        Assert.Equal(new[] { "I am fine!" }, tail);
    }

    [Fact]
    public void Treats_known_abbreviations_as_non_terminal()
    {
        var seg = new SentenceSegmenter();
        var sentences = seg.Push("Dr. Smith said e.g. cats are nice. Hi.");
        Assert.Equal(new[] { "Dr. Smith said e.g. cats are nice.", "Hi." }, sentences);
    }

    [Fact]
    public void Force_flush_after_max_chars_without_terminal_punctuation()
    {
        var seg = new SentenceSegmenter(maxChars: 50);
        var longRun = new string('a', 60);
        var sentences = seg.Push(longRun);
        Assert.Single(sentences);
        Assert.Equal(50, sentences[0].Length);
    }

    [Fact]
    public void Suppresses_segmentation_inside_code_fence()
    {
        var seg = new SentenceSegmenter();
        var sentences1 = seg.Push("Look at this. ```py\nprint('hi.')\n```\nDone.");
        // The terminal '.' inside the fence must not split.
        Assert.Equal(new[] { "Look at this.", "```py\nprint('hi.')\n```\nDone." }, sentences1);
    }

    [Fact]
    public void Flush_returns_buffered_remainder()
    {
        var seg = new SentenceSegmenter();
        seg.Push("No terminator yet");
        var tail = seg.Flush();
        Assert.Equal(new[] { "No terminator yet" }, tail);
    }

    [Fact]
    public void Flush_returns_empty_when_buffer_drained()
    {
        var seg = new SentenceSegmenter();
        seg.Push("Done. ");
        var tail = seg.Flush();
        Assert.Empty(tail);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~SentenceSegmenterTests"`
Expected: build fails — `SentenceSegmenter` type does not exist.

- [ ] **Step 3: Implement `SentenceSegmenter`**

Create `src/Achates.Server/Speech/SentenceSegmenter.cs`:

```csharp
using System.Text;

namespace Achates.Server.Speech;

/// <summary>
/// Buffers a streaming text delta source and emits complete sentences when
/// terminal punctuation (.!?) followed by whitespace or end-of-stream is seen.
/// Tracks code-fence (```) state so terminal punctuation inside a code block
/// does not produce a split; the whole fence ends up in the next emitted chunk.
/// </summary>
public sealed class SentenceSegmenter
{
    private static readonly string[] Abbreviations =
        ["dr.", "mr.", "mrs.", "ms.", "i.e.", "e.g.", "etc.", "vs.", "st."];

    private readonly StringBuilder _buffer = new();
    private readonly int _maxChars;
    private bool _inFence;

    public SentenceSegmenter(int maxChars = 280)
    {
        _maxChars = maxChars;
    }

    /// <summary>Append text; return any sentences that became complete.</summary>
    public IReadOnlyList<string> Push(string text)
    {
        var emitted = new List<string>();
        foreach (var ch in text)
        {
            _buffer.Append(ch);
            TrackFence();

            if (!_inFence && IsTerminator(ch))
            {
                if (!EndsWithAbbreviation())
                    Emit(emitted);
            }
            else if (_buffer.Length >= _maxChars)
            {
                Emit(emitted);
            }
        }
        return emitted;
    }

    /// <summary>Drain any remaining buffered text as a final sentence.</summary>
    public IReadOnlyList<string> Flush()
    {
        var emitted = new List<string>();
        if (_buffer.Length > 0)
            Emit(emitted);
        return emitted;
    }

    private static bool IsTerminator(char ch) => ch is '.' or '!' or '?';

    private void Emit(List<string> emitted)
    {
        var s = _buffer.ToString().Trim();
        if (s.Length > 0)
            emitted.Add(s);
        _buffer.Clear();
    }

    private void TrackFence()
    {
        if (_buffer.Length < 3) return;
        var tail = _buffer.ToString(_buffer.Length - 3, 3);
        if (tail == "```")
            _inFence = !_inFence;
    }

    private bool EndsWithAbbreviation()
    {
        var s = _buffer.ToString();
        foreach (var abbr in Abbreviations)
        {
            if (s.EndsWith(abbr, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~SentenceSegmenterTests"`
Expected: all 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Achates.Server/Speech/SentenceSegmenter.cs \
        tests/Achates.Tests/Speech/SentenceSegmenterTests.cs
git commit -m "feat(speech): SentenceSegmenter for streaming text → sentences"
```

---

### Task 2: `SpeechSanitizer`

**Files:**
- Create: `src/Achates.Server/Speech/SpeechSanitizer.cs`
- Test: `tests/Achates.Tests/Speech/SpeechSanitizerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Achates.Tests/Speech/SpeechSanitizerTests.cs`:

```csharp
using Achates.Server.Speech;

namespace Achates.Tests.Speech;

public sealed class SpeechSanitizerTests
{
    [Theory]
    [InlineData("**bold** word", "bold word")]
    [InlineData("_italic_ word", "italic word")]
    [InlineData("# Heading", "Heading")]
    [InlineData("plain prose", "plain prose")]
    public void Strips_inline_emphasis_and_headers(string input, string expected)
    {
        Assert.Equal(expected, SpeechSanitizer.Sanitize(input));
    }

    [Fact]
    public void Drops_entire_code_fence_block()
    {
        var input = "Before.\n```py\nprint('hi')\n```\nAfter.";
        Assert.Equal("Before.\n\nAfter.", SpeechSanitizer.Sanitize(input));
    }

    [Fact]
    public void Drops_inline_code_contents()
    {
        Assert.Equal("Use the  function.", SpeechSanitizer.Sanitize("Use the `foo.bar()` function."));
    }

    [Fact]
    public void Keeps_link_text_drops_url()
    {
        Assert.Equal("See the docs for more.", SpeechSanitizer.Sanitize("See the [docs](https://example.com) for more."));
    }

    [Fact]
    public void Strips_bare_urls()
    {
        Assert.Equal("Check out  for that.", SpeechSanitizer.Sanitize("Check out https://example.com/foo?x=1 for that."));
    }

    [Fact]
    public void Strips_image_references()
    {
        Assert.Equal("Behold: ", SpeechSanitizer.Sanitize("Behold: ![cat](https://example.com/cat.png)"));
    }

    [Fact]
    public void Strips_emoji()
    {
        Assert.Equal("Hello world", SpeechSanitizer.Sanitize("Hello 😀 world 🚀"));
    }

    [Fact]
    public void Strips_blockquote_marker_keeps_content()
    {
        Assert.Equal("a quoted line", SpeechSanitizer.Sanitize("> a quoted line"));
    }

    [Fact]
    public void Collapses_horizontal_rule_to_nothing()
    {
        Assert.Equal("Before\n\nAfter", SpeechSanitizer.Sanitize("Before\n---\nAfter"));
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal(string.Empty, SpeechSanitizer.Sanitize(""));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~SpeechSanitizerTests"`
Expected: build fails — `SpeechSanitizer` does not exist.

- [ ] **Step 3: Implement `SpeechSanitizer`**

Create `src/Achates.Server/Speech/SpeechSanitizer.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace Achates.Server.Speech;

/// <summary>
/// Strips markdown noise and unspeakable content from a chunk of assistant
/// text before sending it to the TTS engine. Pragmatic regex-based passes;
/// not a full Markdown parser — keep it simple until it proves insufficient.
/// </summary>
public static partial class SpeechSanitizer
{
    public static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var s = input;

        // Drop fenced code blocks entirely (multiline, non-greedy).
        s = CodeFenceRegex().Replace(s, "");

        // Image refs ![alt](url) → "" (must run before plain links).
        s = ImageRegex().Replace(s, "");

        // Links [text](url) → "text"
        s = LinkRegex().Replace(s, "$1");

        // Bare URLs → ""
        s = BareUrlRegex().Replace(s, "");

        // Inline code `…` → ""
        s = InlineCodeRegex().Replace(s, "");

        // Horizontal rules → ""
        s = HorizontalRuleRegex().Replace(s, "");

        // Blockquote markers at line start → ""
        s = BlockquoteRegex().Replace(s, "");

        // Headers at line start: drop "# ", keep content.
        s = HeaderRegex().Replace(s, "");

        // Bold/italic markers (** or __ or * or _) — drop the marks only.
        s = EmphasisRegex().Replace(s, "$1");

        // Emoji — strip (cover common BMP + supplementary planes used for emoji).
        s = EmojiRegex().Replace(s, "");

        // Collapse runs of >2 blank lines.
        s = ExtraBlankLinesRegex().Replace(s, "\n\n");

        return s;
    }

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Multiline)]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex BareUrlRegex();

    [GeneratedRegex(@"`[^`]*`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"^\s*-{3,}\s*$", RegexOptions.Multiline)]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"^>\s?", RegexOptions.Multiline)]
    private static partial Regex BlockquoteRegex();

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"(?:\*\*|__|\*|_)(.+?)(?:\*\*|__|\*|_)")]
    private static partial Regex EmphasisRegex();

    [GeneratedRegex(@"[☀-➿]|[\uD83C-\uDBFF][\uDC00-\uDFFF]")]
    private static partial Regex EmojiRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLinesRegex();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~SpeechSanitizerTests"`
Expected: all 14 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Achates.Server/Speech/SpeechSanitizer.cs \
        tests/Achates.Tests/Speech/SpeechSanitizerTests.cs
git commit -m "feat(speech): SpeechSanitizer strips markdown/code/urls/emoji"
```

---

## Phase 2: Config and capability

### Task 3: `SpeechConfig` in `AchatesConfig`

**Files:**
- Modify: `src/Achates.Server/AchatesConfig.cs`
- Create: `src/Achates.Server/Speech/SpeechConfig.cs`

- [ ] **Step 1: Create the config DTOs**

Create `src/Achates.Server/Speech/SpeechConfig.cs`:

```csharp
namespace Achates.Server.Speech;

public sealed class SpeechConfig
{
    /// <summary>
    /// Managed sidecar process to launch on server startup. Mutually
    /// exclusive with <see cref="Endpoint"/> — if both are set, Endpoint wins
    /// (and a warning is logged).
    /// </summary>
    public SidecarConfig? Sidecar { get; set; }

    /// <summary>
    /// External sidecar URL (e.g. <c>http://127.0.0.1:8880</c>). When set,
    /// Achates does not launch a child process; it only health-checks the
    /// endpoint. Falls back to a value derived from <see cref="Sidecar"/>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Global default voice id used when an agent doesn't declare
    /// <c>**Voice:**</c>. Off by default — voiceless agents stay silent.
    /// </summary>
    public string? DefaultVoice { get; set; }
}

public sealed class SidecarConfig
{
    public string? WorkingDir { get; set; }
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
}
```

- [ ] **Step 2: Wire `SpeechConfig` into `ToolsConfig`**

Edit `src/Achates.Server/AchatesConfig.cs`. Find the `ToolsConfig` class and add a `Speech` property:

```csharp
public sealed class ToolsConfig
{
    public NotebookConfig? Notebook { get; set; }
    public WebSearchConfig? WebSearch { get; set; }
    public TranscribeConfig? Transcribe { get; set; }
    public AvatarConfig? Avatar { get; set; }
    public ImageConfig? Image { get; set; }
    public TitleConfig? Title { get; set; }
    public Dictionary<string, GraphConfig>? Graph { get; set; }
    public WithingsConfig? Withings { get; set; }
    public Achates.Server.Speech.SpeechConfig? Speech { get; set; }
}
```

- [ ] **Step 3: Build to verify everything compiles**

Run: `dotnet build Achates.slnx`
Expected: build succeeds, no warnings related to the new types.

- [ ] **Step 4: Commit**

```bash
git add src/Achates.Server/AchatesConfig.cs \
        src/Achates.Server/Speech/SpeechConfig.cs
git commit -m "feat(config): add tools.speech config schema (SpeechConfig, SidecarConfig)"
```

---

### Task 4: `**Voice:**` capability — parse, serialize, AgentDefinition

**Files:**
- Modify: `src/Achates.Server/AchatesConfig.cs` (AgentConfig)
- Modify: `src/Achates.Server/AgentDefinition.cs`
- Modify: `src/Achates.Server/AgentLoader.cs`
- Modify: `tests/Achates.Tests/AgentLoaderTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `tests/Achates.Tests/AgentLoaderTests.cs` (find the existing test class and append a new test). If you're unsure of the existing file layout, run:

```bash
grep -n "public sealed class AgentLoaderTests" tests/Achates.Tests/AgentLoaderTests.cs
```

Add at the end of the class (before the final `}`):

```csharp
    [Fact]
    public void Parse_reads_voice_capability()
    {
        var md = """
            # Test Agent

            ## Capabilities

            **Voice:** af_nicole

            ## Prompt

            hello
            """;

        var config = AgentLoader.Parse(md);

        Assert.NotNull(config);
        Assert.Equal("af_nicole", config!.Voice);
    }

    [Fact]
    public void Parse_reads_voice_blend()
    {
        var md = """
            # Test

            ## Capabilities

            **Voice:** af_nicole(0.7)+af_bella(0.3)
            """;

        var config = AgentLoader.Parse(md);

        Assert.Equal("af_nicole(0.7)+af_bella(0.3)", config!.Voice);
    }

    [Fact]
    public void Serialize_emits_voice_capability()
    {
        var config = new AgentConfig
        {
            Title = "Test",
            Description = "",
            Voice = "af_nicole",
            Prompt = "hi",
        };

        var md = AgentLoader.Serialize("test", config);

        Assert.Contains("**Voice:** af_nicole", md);
    }

    [Fact]
    public void Parse_voice_capability_absent_yields_null()
    {
        var md = """
            # Test

            ## Capabilities

            **Model:** anthropic/claude-sonnet-4.6

            ## Prompt

            hi
            """;

        var config = AgentLoader.Parse(md);
        Assert.Null(config!.Voice);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~AgentLoaderTests"`
Expected: 4 new tests fail — `AgentConfig.Voice` does not exist.

- [ ] **Step 3: Add `Voice` to `AgentConfig`**

Edit `src/Achates.Server/AchatesConfig.cs`. In the `AgentConfig` class, add after the `ThinkingModel` property:

```csharp
    /// <summary>
    /// Per-agent voice id for TTS (e.g. "af_nicole" or a Kokoro blend like
    /// "af_nicole(0.7)+af_bella(0.3)"). Null/empty means the agent is
    /// voiceless — speech is not generated even when the per-session toggle
    /// is on, unless <c>tools.speech.default_voice</c> is set globally.
    /// </summary>
    public string? Voice { get; set; }
```

- [ ] **Step 4: Add `Voice` to `AgentDefinition`**

Edit `src/Achates.Server/AgentDefinition.cs`. After `ThinkingModel`, add:

```csharp
    /// <summary>
    /// Per-agent voice id for TTS. Resolved from the <c>**Voice:**</c>
    /// capability in AGENT.md. Null means the agent is voiceless unless
    /// <c>tools.speech.default_voice</c> is set globally.
    /// </summary>
    public string? Voice { get; init; }
```

- [ ] **Step 5: Parse `**Voice:**` in `AgentLoader.ParseCapabilities`**

Edit `src/Achates.Server/AgentLoader.cs`. In `ApplyCapability`, add a new case in the `switch (key)`:

```csharp
            case "voice":
                config.Voice = string.IsNullOrWhiteSpace(value) ? null : value;
                break;
```

- [ ] **Step 6: Serialize `**Voice:**` in `AgentLoader.Serialize`**

Edit `src/Achates.Server/AgentLoader.cs`. In `Serialize`, after the `ThinkingModel` block, add:

```csharp
        if (!string.IsNullOrWhiteSpace(config.Voice))
            sb.AppendLine($"**Voice:** {config.Voice}");
```

- [ ] **Step 7: Update the XML doc comment header on the parser**

Edit the `///` comment block at the top of `AgentLoader.cs` (around lines 9-15) to mention `**Voice:**` in the example so the documentation matches reality. Find the existing block and add the line after `**Tools:**`:

```csharp
///   - **Voice:** af_nicole
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~AgentLoaderTests"`
Expected: all AgentLoader tests pass, including the 4 new ones.

- [ ] **Step 9: Wire `Voice` into `AgentDefinition` construction**

Find where `AgentDefinition` is created from `AgentConfig` (likely `GatewayService.ResolveAgentsAsync` or similar):

```bash
grep -n "new AgentDefinition" src/Achates.Server/GatewayService.cs
```

In that constructor expression, add the `Voice` assignment:

```csharp
            Voice = agentConfig.Voice,
```

- [ ] **Step 10: Build, test, commit**

```bash
dotnet build Achates.slnx
dotnet test Achates.slnx --filter "FullyQualifiedName~AgentLoader"
git add src/Achates.Server/AchatesConfig.cs \
        src/Achates.Server/AgentDefinition.cs \
        src/Achates.Server/AgentLoader.cs \
        src/Achates.Server/GatewayService.cs \
        tests/Achates.Tests/AgentLoaderTests.cs
git commit -m "feat(agents): **Voice:** capability in AGENT.md + AgentDefinition.Voice"
```

---

## Phase 3: Synthesizer

### Task 5: `ISpeechSynthesizer` interface

**Files:**
- Create: `src/Achates.Server/Speech/ISpeechSynthesizer.cs`

- [ ] **Step 1: Define the interface**

Create `src/Achates.Server/Speech/ISpeechSynthesizer.cs`:

```csharp
namespace Achates.Server.Speech;

/// <summary>
/// Engine-agnostic TTS synthesizer. The concrete implementation today is
/// <see cref="KokoroSpeechSynthesizer"/>; the interface exists so we can
/// swap in alternatives (ElevenLabs, gpt-4o-audio) per-session in the future
/// without touching the call sites in <see cref="SpeechBroker"/>.
/// </summary>
public interface ISpeechSynthesizer
{
    /// <summary>
    /// Whether the synthesizer is reachable and ready. False when the sidecar
    /// is starting up, has crashed, or was never configured. <see cref="SpeechBroker"/>
    /// reads this before attempting any synthesis call.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Synthesize <paramref name="text"/> in the named voice and return the
    /// complete audio bytes plus the format (e.g. "mp3"). May throw on
    /// network errors or non-2xx HTTP responses; callers must handle
    /// failures.
    /// </summary>
    Task<SynthesisResult> SynthesizeAsync(string text, string voice, CancellationToken ct);

    /// <summary>
    /// List of voice ids known to the synthesizer (e.g. for populating an
    /// iOS picker). Returns an empty list if the synthesizer is not
    /// reachable.
    /// </summary>
    Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct);
}

public sealed record SynthesisResult(byte[] Audio, string Format);
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/Achates.Server/Achates.Server.csproj`
Expected: builds clean.

- [ ] **Step 3: Commit**

```bash
git add src/Achates.Server/Speech/ISpeechSynthesizer.cs
git commit -m "feat(speech): ISpeechSynthesizer engine-agnostic interface"
```

---

### Task 6: `KokoroSpeechSynthesizer` (HTTP client)

**Files:**
- Create: `src/Achates.Server/Speech/KokoroSpeechSynthesizer.cs`
- Test: `tests/Achates.Tests/Speech/KokoroSpeechSynthesizerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Achates.Tests/Speech/KokoroSpeechSynthesizerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Achates.Server.Speech;

namespace Achates.Tests.Speech;

public sealed class KokoroSpeechSynthesizerTests
{
    [Fact]
    public async Task SynthesizeAsync_posts_openai_compatible_payload_and_returns_bytes()
    {
        byte[]? capturedBody = null;
        string? capturedUrl = null;

        var handler = new StubHandler(async req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            capturedBody = await req.Content!.ReadAsByteArrayAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0xff, 0xfb, 0x90, 0x44]) // fake MP3 header bytes
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("audio/mpeg") },
                },
            };
        });

        var client = new HttpClient(handler);
        var synth = new KokoroSpeechSynthesizer(client, new Uri("http://127.0.0.1:8880"));
        synth.MarkAvailable(true);

        var result = await synth.SynthesizeAsync("hello", "af_nicole", CancellationToken.None);

        Assert.Equal("mp3", result.Format);
        Assert.Equal(new byte[] { 0xff, 0xfb, 0x90, 0x44 }, result.Audio);
        Assert.Equal("http://127.0.0.1:8880/v1/audio/speech", capturedUrl);

        var doc = JsonDocument.Parse(Encoding.UTF8.GetString(capturedBody!));
        Assert.Equal("kokoro", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("af_nicole", doc.RootElement.GetProperty("voice").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("input").GetString());
        Assert.Equal("mp3", doc.RootElement.GetProperty("response_format").GetString());
    }

    [Fact]
    public async Task SynthesizeAsync_throws_on_non_2xx()
    {
        var handler = new StubHandler(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("unknown voice"),
        }));

        var synth = new KokoroSpeechSynthesizer(new HttpClient(handler), new Uri("http://127.0.0.1:8880"));
        synth.MarkAvailable(true);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => synth.SynthesizeAsync("hi", "bad_voice", CancellationToken.None));
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public async Task ListVoicesAsync_returns_empty_when_unreachable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var synth = new KokoroSpeechSynthesizer(new HttpClient(handler), new Uri("http://127.0.0.1:8880"));
        // Not marked available — sidecar never came up.
        var voices = await synth.ListVoicesAsync(CancellationToken.None);
        Assert.Empty(voices);
    }

    [Fact]
    public async Task ListVoicesAsync_parses_voices_array()
    {
        var json = """{"voices":["af_nicole","af_bella","am_michael"]}""";
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

        var synth = new KokoroSpeechSynthesizer(new HttpClient(handler), new Uri("http://127.0.0.1:8880"));
        synth.MarkAvailable(true);
        var voices = await synth.ListVoicesAsync(CancellationToken.None);
        Assert.Equal(new[] { "af_nicole", "af_bella", "am_michael" }, voices);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => handler(request);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~KokoroSpeechSynthesizerTests"`
Expected: build fails — `KokoroSpeechSynthesizer` does not exist.

- [ ] **Step 3: Implement `KokoroSpeechSynthesizer`**

Create `src/Achates.Server/Speech/KokoroSpeechSynthesizer.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Server.Speech;

/// <summary>
/// HTTP client targeting the OpenAI-compatible /v1/audio/speech endpoint
/// exposed by kokoro-fastapi (or any other Kokoro server with the same
/// shape). Availability is gated externally by <see cref="KokoroSidecarProcess"/>
/// which calls <see cref="MarkAvailable"/> after a successful health check.
/// </summary>
public sealed class KokoroSpeechSynthesizer(HttpClient http, Uri baseUrl) : ISpeechSynthesizer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private volatile bool _available;

    public bool IsAvailable => _available;

    /// <summary>Set by the sidecar supervisor after a successful health probe.</summary>
    public void MarkAvailable(bool value) => _available = value;

    public async Task<SynthesisResult> SynthesizeAsync(string text, string voice, CancellationToken ct)
    {
        var url = new Uri(baseUrl, "/v1/audio/speech");
        var body = new SpeechRequest("kokoro", voice, text, "mp3");

        var response = await http.PostAsJsonAsync(url, body, Json, ct);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return new SynthesisResult(bytes, "mp3");
    }

    public async Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct)
    {
        try
        {
            var url = new Uri(baseUrl, "/v1/audio/voices");
            var doc = await http.GetFromJsonAsync<VoicesResponse>(url, Json, ct);
            return doc?.Voices ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed record SpeechRequest(string Model, string Voice, string Input, string ResponseFormat);
    private sealed record VoicesResponse(IReadOnlyList<string>? Voices);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~KokoroSpeechSynthesizerTests"`
Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Achates.Server/Speech/KokoroSpeechSynthesizer.cs \
        tests/Achates.Tests/Speech/KokoroSpeechSynthesizerTests.cs
git commit -m "feat(speech): KokoroSpeechSynthesizer HTTP client (OpenAI-compatible)"
```

---

## Phase 4: Sidecar lifecycle

### Task 7: `KokoroSidecarProcess` IHostedService

**Files:**
- Create: `src/Achates.Server/Speech/KokoroSidecarProcess.cs`

This task is NOT unit-tested with TDD because it manages a real OS child process and HTTP polling — the integration surface area is small but the unit-test value is low. We rely on the synthesizer's tests plus the end-to-end manual verification step at the end of the plan.

- [ ] **Step 1: Implement `KokoroSidecarProcess`**

Create `src/Achates.Server/Speech/KokoroSidecarProcess.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Achates.Server.Speech;

/// <summary>
/// Supervises an optional kokoro-fastapi child process. Spawns it on startup
/// (managed mode), polls its /health endpoint, and exposes availability via
/// the shared <see cref="KokoroSpeechSynthesizer"/>. On unexpected exit,
/// restarts with exponential backoff (1s → 5s → 30s → 5min, then steady).
/// When config provides only <see cref="SpeechConfig.Endpoint"/>, no child
/// is spawned — only the health check runs.
/// </summary>
public sealed class KokoroSidecarProcess(
    SpeechConfig config,
    KokoroSpeechSynthesizer synth,
    HttpClient httpClient,
    ILogger<KokoroSidecarProcess> logger) : BackgroundService
{
    private static readonly TimeSpan[] BackoffSchedule =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    private Process? _process;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (config.Sidecar is null && string.IsNullOrWhiteSpace(config.Endpoint))
        {
            logger.LogInformation("Speech: tools.speech configured but neither sidecar nor endpoint provided. Speech disabled.");
            return;
        }

        if (config.Sidecar is not null && !string.IsNullOrWhiteSpace(config.Endpoint))
            logger.LogWarning("Speech: both tools.speech.sidecar and tools.speech.endpoint set; endpoint wins, sidecar will not be auto-launched.");

        var managed = config.Sidecar is not null && string.IsNullOrWhiteSpace(config.Endpoint);
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (managed && !TrySpawn(out _process))
                {
                    await DelayBackoff(attempt++, ct);
                    continue;
                }

                if (await WaitForHealthyAsync(ct))
                {
                    synth.MarkAvailable(true);
                    attempt = 0;
                    logger.LogInformation("Speech: Kokoro sidecar is ready.");

                    // Block until the process exits OR shutdown is requested.
                    if (_process is not null)
                        await _process.WaitForExitAsync(ct);
                    else
                        await Task.Delay(Timeout.Infinite, ct);
                }
                else
                {
                    logger.LogError("Speech: health check timed out. Speech disabled until next attempt.");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Speech: sidecar supervisor error.");
            }
            finally
            {
                synth.MarkAvailable(false);
                TryDisposeProcess();
            }

            if (!ct.IsCancellationRequested && managed)
            {
                logger.LogWarning("Speech: sidecar exited; scheduling restart.");
                await DelayBackoff(attempt++, ct);
            }
            else if (!managed)
            {
                // External endpoint went down; re-poll after a delay.
                await DelayBackoff(attempt++, ct);
            }
        }

        TryDisposeProcess();
    }

    private bool TrySpawn(out Process? process)
    {
        process = null;
        var sidecar = config.Sidecar!;

        if (string.IsNullOrWhiteSpace(sidecar.WorkingDir) || !Directory.Exists(ExpandPath(sidecar.WorkingDir)))
        {
            logger.LogError("Speech: sidecar working_dir '{Dir}' does not exist. See docs/speech-setup.md.", sidecar.WorkingDir);
            return false;
        }

        if (string.IsNullOrWhiteSpace(sidecar.Command))
        {
            logger.LogError("Speech: sidecar command is empty in config.");
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = sidecar.Command,
            WorkingDirectory = ExpandPath(sidecar.WorkingDir),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in sidecar.Args ?? [])
            psi.ArgumentList.Add(arg);

        try
        {
            var p = Process.Start(psi);
            if (p is null) return false;
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) logger.LogInformation("[kokoro] {Line}", e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) logger.LogWarning("[kokoro] {Line}", e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            process = p;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Speech: failed to spawn sidecar process.");
            return false;
        }
    }

    private async Task<bool> WaitForHealthyAsync(CancellationToken ct)
    {
        var endpoint = ResolveEndpoint();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var resp = await httpClient.GetAsync(new Uri(endpoint, "/health"), ct);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* not ready yet */ }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        return false;
    }

    private Uri ResolveEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(config.Endpoint))
            return new Uri(config.Endpoint);

        // Derive from --port in sidecar.args, default 8880.
        var args = config.Sidecar?.Args ?? [];
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--port") return new Uri($"http://127.0.0.1:{args[i + 1]}");
        }
        return new Uri("http://127.0.0.1:8880");
    }

    private static Task DelayBackoff(int attempt, CancellationToken ct)
    {
        var idx = Math.Min(attempt, BackoffSchedule.Length - 1);
        return Task.Delay(BackoffSchedule[idx], ct);
    }

    private void TryDisposeProcess()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Speech: error cleaning up sidecar process.");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static string ExpandPath(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build Achates.slnx`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Achates.Server/Speech/KokoroSidecarProcess.cs
git commit -m "feat(speech): KokoroSidecarProcess IHostedService for sidecar supervision"
```

---

### Task 8: Register speech services in DI

**Files:**
- Modify: `src/Achates.Server/GatewayService.cs` and/or `src/Achates.Server/Program.cs`

- [ ] **Step 1: Locate the existing DI registration site**

```bash
grep -n "AddHostedService\|AddSingleton\|builder.Services" src/Achates.Server/Program.cs
```

- [ ] **Step 2: Register speech services conditionally on `tools.speech`**

Edit `src/Achates.Server/Program.cs` (after the existing service registrations, before `var app = builder.Build()`):

```csharp
// Speech (conditional on tools.speech being configured).
if (config.Tools?.Speech is { } speechConfig)
{
    builder.Services.AddSingleton(speechConfig);
    builder.Services.AddSingleton<KokoroSpeechSynthesizer>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("kokoro");
        var endpoint = !string.IsNullOrWhiteSpace(speechConfig.Endpoint)
            ? new Uri(speechConfig.Endpoint)
            : new Uri("http://127.0.0.1:8880"); // matches sidecar default; sidecar process resolves the same way
        return new KokoroSpeechSynthesizer(http, endpoint);
    });
    builder.Services.AddSingleton<ISpeechSynthesizer>(sp => sp.GetRequiredService<KokoroSpeechSynthesizer>());
    builder.Services.AddHostedService<KokoroSidecarProcess>();
    builder.Services.AddHttpClient("kokoro").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
}
```

If `Program.cs` doesn't already have a `using Achates.Server.Speech;` declaration, add one at the top.

If `IHttpClientFactory` isn't already registered in `Program.cs`, also add (idempotent — only if not already present):

```csharp
builder.Services.AddHttpClient();
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build Achates.slnx`
Expected: builds clean.

- [ ] **Step 4: Commit**

```bash
git add src/Achates.Server/Program.cs
git commit -m "feat(speech): register KokoroSpeechSynthesizer + KokoroSidecarProcess in DI"
```

---

## Phase 5: Per-turn orchestration

### Task 9: `SpeechBroker`

**Files:**
- Create: `src/Achates.Server/Speech/SpeechBroker.cs`
- Test: `tests/Achates.Tests/Speech/SpeechBrokerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Achates.Tests/Speech/SpeechBrokerTests.cs`:

```csharp
using System.Collections.Concurrent;
using Achates.Server.Speech;

namespace Achates.Tests.Speech;

public sealed class SpeechBrokerTests
{
    [Fact]
    public async Task Emits_audio_block_per_complete_sentence_in_order()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth();
        var broker = new SpeechBroker(synth, sink, voice: "af_nicole", turnId: "turn-1");

        await broker.PushTextAsync("Hello. ");
        await broker.PushTextAsync("How are you? ");
        await broker.PushTextAsync("Goodbye.");
        await broker.FinishAsync();

        Assert.Equal(3, sink.Blocks.Count);
        Assert.Equal("Hello.", sink.Blocks[0].text);
        Assert.Equal(0, sink.Blocks[0].sentence_index);
        Assert.Equal("How are you?", sink.Blocks[1].text);
        Assert.Equal(1, sink.Blocks[1].sentence_index);
        Assert.Equal("Goodbye.", sink.Blocks[2].text);
        Assert.Equal(2, sink.Blocks[2].sentence_index);
        Assert.Empty(sink.Errors);
    }

    [Fact]
    public async Task Strips_markdown_before_synthesis()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth();
        var broker = new SpeechBroker(synth, sink, voice: "af_nicole", turnId: "turn-1");

        await broker.PushTextAsync("**bold** word. ");
        await broker.FinishAsync();

        Assert.Single(sink.Blocks);
        Assert.Equal("bold word.", sink.Blocks[0].text);
    }

    [Fact]
    public async Task Skips_code_fence_blocks_in_emitted_audio()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth();
        var broker = new SpeechBroker(synth, sink, voice: "af_nicole", turnId: "turn-1");

        await broker.PushTextAsync("Hello.\n```py\nprint('hi')\n```\nDone.");
        await broker.FinishAsync();

        // Two sentences total: "Hello." then the fence-suppressed rest sanitized to "\n\nDone."
        Assert.True(sink.Blocks.Count >= 1);
        // No emitted audio should contain code-fence content.
        Assert.All(sink.Blocks, b => Assert.DoesNotContain("print(", b.text));
    }

    [Fact]
    public async Task Synth_failure_emits_audio_error_and_does_not_throw()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth { ThrowOnSynth = true };
        var broker = new SpeechBroker(synth, sink, voice: "bad", turnId: "turn-1");

        await broker.PushTextAsync("Hello.");
        await broker.FinishAsync();

        Assert.Empty(sink.Blocks);
        Assert.Single(sink.Errors);
        Assert.Contains("turn-1", sink.Errors[0].turn_id);
    }

    [Fact]
    public async Task Skips_synthesis_when_synthesizer_unavailable()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth { Available = false };
        var broker = new SpeechBroker(synth, sink, voice: "af_nicole", turnId: "turn-1");

        await broker.PushTextAsync("Hello.");
        await broker.FinishAsync();

        Assert.Empty(sink.Blocks);
        Assert.Single(sink.Errors);
        Assert.Contains("unavailable", sink.Errors[0].message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FlushAsync_drains_trailing_unterminated_sentence()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth();
        var broker = new SpeechBroker(synth, sink, voice: "af_nicole", turnId: "turn-1");

        await broker.PushTextAsync("No terminator here");
        await broker.FinishAsync();

        Assert.Single(sink.Blocks);
        Assert.Equal("No terminator here", sink.Blocks[0].text);
    }

    private sealed class FakeSynth : ISpeechSynthesizer
    {
        public bool Available { get; set; } = true;
        public bool ThrowOnSynth { get; set; }
        public bool IsAvailable => Available;
        public Task<SynthesisResult> SynthesizeAsync(string text, string voice, CancellationToken ct)
        {
            if (ThrowOnSynth) throw new HttpRequestException("boom");
            return Task.FromResult(new SynthesisResult(new byte[] { 1, 2, 3 }, "mp3"));
        }
        public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingSink : ISpeechSink
    {
        public ConcurrentBag<dynamic> _blocks = new();
        public ConcurrentBag<dynamic> _errors = new();
        public List<dynamic> Blocks => _blocks.OrderBy(b => (int)b.sentence_index).ToList();
        public List<dynamic> Errors => _errors.ToList();

        public Task EmitAudioBlockAsync(string turnId, int sentenceIndex, string voice, string format, byte[] data, string text, CancellationToken ct)
        {
            _blocks.Add(new { turn_id = turnId, sentence_index = sentenceIndex, voice, format, data, text });
            return Task.CompletedTask;
        }

        public Task EmitAudioErrorAsync(string turnId, int? sentenceIndex, string message, CancellationToken ct)
        {
            _errors.Add(new { turn_id = turnId, sentence_index = sentenceIndex, message });
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~SpeechBrokerTests"`
Expected: build fails — `SpeechBroker`, `ISpeechSink` do not exist.

- [ ] **Step 3: Implement `ISpeechSink` and `SpeechBroker`**

Create `src/Achates.Server/Speech/SpeechBroker.cs`:

```csharp
namespace Achates.Server.Speech;

/// <summary>
/// The sink that <see cref="SpeechBroker"/> calls back into to deliver
/// audio events to wherever they need to go (the WebSocket transport in
/// production; a recorder in tests).
/// </summary>
public interface ISpeechSink
{
    Task EmitAudioBlockAsync(string turnId, int sentenceIndex, string voice, string format, byte[] data, string text, CancellationToken ct);
    Task EmitAudioErrorAsync(string turnId, int? sentenceIndex, string message, CancellationToken ct);
}

/// <summary>
/// Per-turn orchestrator: consumes streaming text deltas, segments and
/// sanitizes them, synthesizes each completed sentence sequentially, and
/// forwards the resulting audio events through an <see cref="ISpeechSink"/>.
/// Sequential per-sentence processing is intentional so audio plays in order
/// downstream.
/// </summary>
public sealed class SpeechBroker(
    ISpeechSynthesizer synth,
    ISpeechSink sink,
    string voice,
    string turnId)
{
    private readonly SentenceSegmenter _segmenter = new();
    private int _sentenceIndex;
    private bool _emittedUnavailableError;

    public async Task PushTextAsync(string text, CancellationToken ct = default)
    {
        var sentences = _segmenter.Push(text);
        foreach (var sentence in sentences)
            await SynthesizeAndEmitAsync(sentence, ct);
    }

    public async Task FinishAsync(CancellationToken ct = default)
    {
        var tail = _segmenter.Flush();
        foreach (var sentence in tail)
            await SynthesizeAndEmitAsync(sentence, ct);
    }

    private async Task SynthesizeAndEmitAsync(string raw, CancellationToken ct)
    {
        var spoken = SpeechSanitizer.Sanitize(raw).Trim();
        if (string.IsNullOrWhiteSpace(spoken))
            return; // Nothing speakable; skip silently.

        if (!synth.IsAvailable)
        {
            if (!_emittedUnavailableError)
            {
                _emittedUnavailableError = true;
                await sink.EmitAudioErrorAsync(turnId, sentenceIndex: null,
                    message: "Speech engine unavailable for this turn.", ct);
            }
            return;
        }

        var index = _sentenceIndex++;
        try
        {
            var result = await synth.SynthesizeAsync(spoken, voice, ct);
            await sink.EmitAudioBlockAsync(turnId, index, voice, result.Format, result.Audio, spoken, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await sink.EmitAudioErrorAsync(turnId, index, ex.Message, ct);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Achates.Tests/Achates.Tests.csproj --filter "FullyQualifiedName~SpeechBrokerTests"`
Expected: all 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Achates.Server/Speech/SpeechBroker.cs \
        tests/Achates.Tests/Speech/SpeechBrokerTests.cs
git commit -m "feat(speech): SpeechBroker per-turn orchestrator (sanitize → segment → synth)"
```

---

## Phase 6: Transport integration

### Task 10: Add `SpeechEnabled` field to `MobileSession`

**Files:**
- Modify: `src/Achates.Server/Mobile/MobileSession.cs`

- [ ] **Step 1: Add the field**

Edit `src/Achates.Server/Mobile/MobileSession.cs`. After the `Messages` property, add:

```csharp
    /// <summary>
    /// Per-session opt-in for spoken assistant replies. Default false; flipped
    /// by the <c>session.set_speech</c> RPC. When true AND the active agent
    /// has a voice (or a global default is configured), <see cref="Achates.Server.Speech.SpeechBroker"/>
    /// is wired into the streaming loop and emits <c>audio.block</c> events.
    /// </summary>
    public bool SpeechEnabled { get; set; }
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Achates.slnx`
Expected: builds clean. Existing JSON-serialized sessions on disk get `false` by default on deserialization (System.Text.Json behavior for missing fields).

- [ ] **Step 3: Commit**

```bash
git add src/Achates.Server/Mobile/MobileSession.cs
git commit -m "feat(transport): MobileSession.SpeechEnabled persisted field"
```

---

### Task 11: `session.set_speech` and `voices.list` RPC handlers

**Files:**
- Modify: `src/Achates.Server/Mobile/MobileTransport.cs`

- [ ] **Step 1: Find the RPC dispatch table and add the two new entries**

Edit `src/Achates.Server/Mobile/MobileTransport.cs`. In the `switch` statement around line 343 (where `"agents.list"`, `"sessions.list"`, etc. are dispatched), add the two new methods. Find the line ending `=> await HandleJobsRunAsync(request, ct),` (around line 368) and insert after it (before the default `_ =>` arm):

```csharp
                "session.set_speech" => await HandleSessionSetSpeechAsync(request, ct),
                "voices.list" => await HandleVoicesListAsync(request, ct),
```

- [ ] **Step 2: Implement `HandleSessionSetSpeechAsync`**

Inside `MobileTransport` (place near the other `Handle*Async` methods), add:

```csharp
    private async Task<ResponseFrame> HandleSessionSetSpeechAsync(RequestFrame request, CancellationToken ct)
    {
        var agentName = GetStringParam(request.Params, "agent");
        var sessionId = GetStringParam(request.Params, "session_id");
        var enabled = request.Params.TryGetProperty("enabled", out var e) && e.GetBoolean();

        if (string.IsNullOrEmpty(agentName) || string.IsNullOrEmpty(sessionId))
            return ResponseFrame.Failure(request.Id, "bad_request", "agent and session_id are required");

        if (!_agents.TryGetValue(agentName, out _))
            return ResponseFrame.Failure(request.Id, "not_found", $"Unknown agent: {agentName}");

        var store = sessionStores.GetStore(agentName);
        var session = await store.LoadAsync(sessionId, ct);
        if (session is null)
            return ResponseFrame.Failure(request.Id, "not_found", $"Unknown session: {sessionId}");

        session.SpeechEnabled = enabled;
        session.Updated = DateTimeOffset.UtcNow;
        await store.SaveAsync(session, ct);

        await BroadcastEventAsync("session.updated", new
        {
            agent = agentName,
            id = sessionId,
            speech_enabled = enabled,
        }, ct);

        return ResponseFrame.Success(request.Id, JsonSerializer.SerializeToElement(new
        {
            ok = true,
            speech_enabled = enabled,
        }));
    }
```

NOTE: `sessionStores.GetStore(agentName)` and `BroadcastEventAsync` are the existing helpers — verify their exact names by:

```bash
grep -nE "GetStore\(|BroadcastEventAsync\(|_sessionStores" src/Achates.Server/Mobile/MobileTransport.cs | head -5
```

If a name differs in your tree, adjust the snippet to match.

- [ ] **Step 3: Implement `HandleVoicesListAsync`**

Add inside `MobileTransport`:

```csharp
    private async Task<ResponseFrame> HandleVoicesListAsync(RequestFrame request, CancellationToken ct)
    {
        var synth = serviceProvider.GetService<Achates.Server.Speech.ISpeechSynthesizer>();
        var voices = synth is null
            ? Array.Empty<string>()
            : (await synth.ListVoicesAsync(ct)).ToArray();

        return ResponseFrame.Success(request.Id, JsonSerializer.SerializeToElement(new
        {
            voices,
        }));
    }
```

This requires `MobileTransport` to have an `IServiceProvider` available. Check whether one is already injected:

```bash
grep -nE "IServiceProvider|serviceProvider" src/Achates.Server/Mobile/MobileTransport.cs | head -5
```

If not present, add `IServiceProvider serviceProvider` to the primary constructor parameter list of `MobileTransport`. Find the constructor signature:

```bash
grep -n "public sealed class MobileTransport\|public MobileTransport" src/Achates.Server/Mobile/MobileTransport.cs | head -3
```

Then add `IServiceProvider serviceProvider` to the parameter list. The DI container already provides it; no other wiring needed.

- [ ] **Step 4: Make `MobileSession.SpeechEnabled` flow through `sessions.get`**

Find `HandleSessionsGetAsync` (around line 556 per the earlier grep) and add `speech_enabled = session.SpeechEnabled` to the response payload. The exact format depends on the existing return shape — open the method, locate the anonymous-object payload returned to the caller, and add `speech_enabled = session.SpeechEnabled,`.

Also find `HandleSessionsListAsync` and add `speech_enabled = s.SpeechEnabled` (or the equivalent for the list-item projection) to each session row.

- [ ] **Step 5: Build, test, commit**

```bash
dotnet build Achates.slnx
git add src/Achates.Server/Mobile/MobileTransport.cs
git commit -m "feat(transport): session.set_speech + voices.list RPCs; speech_enabled in session payloads"
```

---

### Task 12: Wire `SpeechBroker` into `MobileTransport.StreamAgentResponseAsync`

**Files:**
- Modify: `src/Achates.Server/Mobile/MobileTransport.cs`

This is the central integration point — where the existing text streaming gets a parallel speech path.

- [ ] **Step 1: Find the streaming loop**

```bash
grep -n "StreamAgentResponseAsync\|text.delta\|text.end" src/Achates.Server/Mobile/MobileTransport.cs | head -20
```

The streaming loop reads events from the agent runtime and emits `text.delta` events to the connected client. The new speech path teeses into that.

- [ ] **Step 2: Add an `ISpeechSink` adapter for the transport**

Inside `MobileTransport`, add a private nested helper:

```csharp
    private sealed class TransportSpeechSink(MobileTransport transport, CancellationToken broadcastCt) : Achates.Server.Speech.ISpeechSink
    {
        public Task EmitAudioBlockAsync(string turnId, int sentenceIndex, string voice, string format, byte[] data, string text, CancellationToken ct)
            => transport.BroadcastEventAsync("audio.block", new
            {
                turn_id = turnId,
                sentence_index = sentenceIndex,
                voice,
                format,
                data = Convert.ToBase64String(data),
                text,
            }, broadcastCt);

        public Task EmitAudioErrorAsync(string turnId, int? sentenceIndex, string message, CancellationToken ct)
            => transport.BroadcastEventAsync("audio.error", new
            {
                turn_id = turnId,
                sentence_index = sentenceIndex,
                message,
            }, broadcastCt);
    }
```

- [ ] **Step 3: Construct the broker per turn when speech is enabled**

In `StreamAgentResponseAsync` (or whichever method drives the per-turn loop), near the top — after the session is loaded and before the event-stream loop — add:

```csharp
    var synth = serviceProvider.GetService<Achates.Server.Speech.ISpeechSynthesizer>();
    var resolvedVoice = ResolveVoice(agentDef, config.Tools?.Speech?.DefaultVoice);
    Achates.Server.Speech.SpeechBroker? speechBroker = null;
    if (session.SpeechEnabled && synth is not null && !string.IsNullOrEmpty(resolvedVoice))
    {
        var turnId = Guid.NewGuid().ToString("N");
        var sink = new TransportSpeechSink(this, ct);
        speechBroker = new Achates.Server.Speech.SpeechBroker(synth, sink, resolvedVoice, turnId);
    }
```

And add the `ResolveVoice` helper (place near other private statics):

```csharp
    private static string? ResolveVoice(AgentDefinition agentDef, string? globalDefault)
        => !string.IsNullOrWhiteSpace(agentDef.Voice) ? agentDef.Voice : globalDefault;
```

- [ ] **Step 4: Fork text deltas into the broker**

Inside the existing event-stream loop, where `text.delta` events are handled (i.e. text is emitted to the WebSocket), also push into the broker:

```csharp
    case CompletionTextDeltaEvent textDelta:
        // ... existing code that emits "text.delta" to the client ...
        if (speechBroker is not null)
            await speechBroker.PushTextAsync(textDelta.Text, ct);
        break;
```

NOTE: the existing event-handling code may use different type/property names; the call to add is `speechBroker.PushTextAsync(<the text fragment>, ct)`. Find the right text-fragment property by reading the existing handler.

- [ ] **Step 5: Drain the broker at end-of-turn**

After the loop ends (where `done` / `message.end` is emitted), add:

```csharp
    if (speechBroker is not null)
        await speechBroker.FinishAsync(ct);
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build Achates.slnx`
Expected: builds clean. (Existing tests still pass — speech is dormant unless the session opt-in is on.)

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test Achates.slnx`
Expected: all tests pass. No regression in existing transport behavior.

- [ ] **Step 8: Commit**

```bash
git add src/Achates.Server/Mobile/MobileTransport.cs
git commit -m "feat(transport): wire SpeechBroker into per-turn text streaming loop"
```

---

## Phase 7: Tool integration

### Task 13: `ProfileTool` extension — read/write voice

**Files:**
- Modify: `src/Achates.Server/Tools/ProfileTool.cs`
- Modify: `tests/Achates.Tests/` (add `ProfileToolTests.cs` if not present)

- [ ] **Step 1: Read the existing tool to understand its `get`/`update` shape**

```bash
grep -nE "case \"get\"|case \"update\"|GenerateSchema|JsonSchemaHelpers" src/Achates.Server/Tools/ProfileTool.cs | head -20
```

- [ ] **Step 2: Add `voice` to the JSON schema**

Edit `src/Achates.Server/Tools/ProfileTool.cs`. In the `Parameters` schema (find the `ObjectSchema(...)` or equivalent), add a `voice` field:

```csharp
        "voice", StringSchema("Per-agent TTS voice id (e.g. 'af_nicole' or a Kokoro blend). Empty string clears the voice (makes the agent voiceless).", required: false),
```

Adjust the helper call to match the project's `JsonSchemaHelpers` API — look at adjacent fields like `description` to match the style exactly.

- [ ] **Step 3: Read and return `voice` in the `get` action**

In the `get` branch of `ExecuteAsync`, add `voice` to the returned object alongside `description`, `prompt`, etc.:

```csharp
            voice = agentDef.Voice,
```

- [ ] **Step 4: Persist `voice` in the `update` action**

In the `update` branch, after the other field updates, add:

```csharp
            if (TryGetStringParam(args, "voice", out var voice))
                config.Voice = string.IsNullOrEmpty(voice) ? null : voice;
```

Match the helper-method name (`TryGetStringParam` or similar) to whatever the file already uses for the other fields.

After updates, the existing `update` code already calls `AgentLoader.Serialize` and triggers a reload — no changes needed there.

- [ ] **Step 5: Build, test, commit**

```bash
dotnet build Achates.slnx
dotnet test Achates.slnx
git add src/Achates.Server/Tools/ProfileTool.cs
git commit -m "feat(tools): ProfileTool exposes voice (agent self-edit)"
```

---

### Task 14: `AgentManagerTool` extension — read/modify/create with voice

**Files:**
- Modify: `src/Achates.Server/Tools/AgentManagerTool.cs`

- [ ] **Step 1: Add `voice` to the JSON schema for `modify` and `create`**

Edit `src/Achates.Server/Tools/AgentManagerTool.cs`. In the modify/create parameter schemas, add:

```csharp
        "voice", StringSchema("Per-agent TTS voice id (e.g. 'af_nicole'). Empty string clears the voice.", required: false),
```

- [ ] **Step 2: Return `voice` in `read` and `list`**

In the `read` handler's returned anonymous object, add:

```csharp
            voice = agentDef.Voice,
```

In `list` (if it returns per-agent metadata), also include `voice = def.Voice`.

- [ ] **Step 3: Apply `voice` in `modify` and `create`**

In both handlers, after the other field updates, add:

```csharp
            if (TryGetStringParam(args, "voice", out var voice))
                config.Voice = string.IsNullOrEmpty(voice) ? null : voice;
```

- [ ] **Step 4: Build, test, commit**

```bash
dotnet build Achates.slnx
dotnet test Achates.slnx
git add src/Achates.Server/Tools/AgentManagerTool.cs
git commit -m "feat(tools): AgentManagerTool exposes voice in read/modify/create"
```

---

### Task 15: `agent.get` / `agent.update` / `agent.create` RPCs expose voice

**Files:**
- Modify: `src/Achates.Server/Mobile/MobileTransport.cs`

- [ ] **Step 1: Add `voice` to `agent.get` response**

Find `HandleAgentGetAsync` and add `voice = def.Voice` to its returned payload.

- [ ] **Step 2: Add `voice` to `agent.update` input**

Find `HandleAgentUpdateAsync` and parse `voice` from the params, applying it to the loaded `AgentConfig` before serializing:

```csharp
        if (request.Params.TryGetProperty("voice", out var voiceProp))
            config.Voice = voiceProp.ValueKind == JsonValueKind.String
                ? (string.IsNullOrEmpty(voiceProp.GetString()) ? null : voiceProp.GetString())
                : null;
```

- [ ] **Step 3: Add `voice` to `agents.list` response per agent**

Find `HandleAgentsListAsync` and add `voice = def.Voice` to each agent row.

- [ ] **Step 4: Build, test, commit**

```bash
dotnet build Achates.slnx
dotnet test Achates.slnx
git add src/Achates.Server/Mobile/MobileTransport.cs
git commit -m "feat(transport): agent.get/update + agents.list expose voice"
```

---

## Phase 8: iOS playback

NOTE: The iOS app uses Swift / SwiftUI and does not have an xUnit test target. Tasks in this phase are exercised via `dotnet build` (which doesn't affect Swift) and manual smoke tests after each. If the project has a `Tests` target under `apple/`, add Swift tests there; otherwise rely on manual verification.

### Task 16: `Session.swift` — add `speechEnabled` field

**Files:**
- Modify: `apple/Achates/Models/Session.swift`

- [ ] **Step 1: Add the field**

Edit `apple/Achates/Models/Session.swift`. Inside the `Session` struct, after the existing fields, add:

```swift
    var speechEnabled: Bool = false
```

If the struct is `Codable`, the JSON decoder will tolerate the field missing (per Swift's default behavior when using a default value).

- [ ] **Step 2: If using a custom `CodingKeys` enum, add `speechEnabled = "speech_enabled"`**

Look for an existing `CodingKeys` block in `Session.swift`. If present, add:

```swift
        case speechEnabled = "speech_enabled"
```

If not present, the default `JSONDecoder` with `.convertFromSnakeCase` (already used elsewhere in the app) handles it.

- [ ] **Step 3: Build iOS app**

Open Xcode workspace at `apple/Achates.xcodeproj` (or `.xcworkspace`) and Build (⌘B). Expect: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add apple/Achates/Models/Session.swift
git commit -m "feat(ios): Session.speechEnabled mirror of server flag"
```

---

### Task 17: `VoiceRegistry` — caches `voices.list`

**Files:**
- Create: `apple/Achates/Services/VoiceRegistry.swift`

- [ ] **Step 1: Implement `VoiceRegistry`**

Create `apple/Achates/Services/VoiceRegistry.swift`:

```swift
import Foundation
import Observation

@MainActor
@Observable
final class VoiceRegistry {
    var voices: [String] = []
    private var lastFetched: Date?
    private let cacheLifetime: TimeInterval = 60 // seconds; sheet-scoped is fine

    private let api: AchatesAPI

    init(api: AchatesAPI) {
        self.api = api
    }

    func loadIfStale() async {
        if let lastFetched, Date().timeIntervalSince(lastFetched) < cacheLifetime, !voices.isEmpty {
            return
        }
        await refresh()
    }

    func refresh() async {
        do {
            let response = try await api.call(method: "voices.list", params: [:])
            if let arr = response["voices"] as? [String] {
                voices = arr
                lastFetched = Date()
            }
        } catch {
            // Silent degrade — empty list keeps the picker showing a graceful "voice unavailable" state.
            voices = []
        }
    }
}
```

NOTE: `AchatesAPI` and its `call(method:params:)` shape may differ. Find the existing API client class in `apple/Achates/Services/` (likely `MobileClient.swift` or similar) and use whichever method name actually exists:

```bash
grep -rnE "func call|func sendRequest|MobileClient|AchatesAPI" apple/Achates/Services/ | head
```

Match the existing convention.

- [ ] **Step 2: Build iOS app, commit**

```bash
git add apple/Achates/Services/VoiceRegistry.swift
git commit -m "feat(ios): VoiceRegistry caches voices.list response"
```

---

### Task 18: `SpeechPlayer` — AVQueuePlayer wrapper

**Files:**
- Create: `apple/Achates/Services/SpeechPlayer.swift`

- [ ] **Step 1: Implement `SpeechPlayer`**

Create `apple/Achates/Services/SpeechPlayer.swift`:

```swift
import AVFoundation
import Observation

/// Plays a stream of MP3 audio chunks (one per assistant sentence) in order
/// via AVQueuePlayer. Chunks arrive over the WebSocket as base64-encoded MP3
/// bytes inside `audio.block` events; we write each to a temp file and
/// enqueue it. Temp files are cleaned up when the player advances past them.
@MainActor
@Observable
final class SpeechPlayer {
    private var player: AVQueuePlayer?
    private var pendingURLs: [URL] = []
    /// turnId → list of temp-file URLs played for that turn (for replay).
    private var turnArchive: [String: [URL]] = [:]
    private(set) var currentTurnId: String?

    init() {
        configureAudioSession()
    }

    private func configureAudioSession() {
        #if os(iOS)
        do {
            try AVAudioSession.sharedInstance().setCategory(.playback, mode: .spokenAudio, options: [.duckOthers])
            try AVAudioSession.sharedInstance().setActive(true)
        } catch {
            // Non-fatal — playback may still work but mixing/routing behavior degrades.
        }
        #endif
    }

    /// Enqueue an MP3 chunk for the given turn. Called for every `audio.block` event.
    func enqueue(turnId: String, sentenceIndex: Int, mp3Data: Data) {
        if currentTurnId != turnId {
            // New turn — drain any pending and reset (the previous turn's archive stays available for replay).
            stop()
            currentTurnId = turnId
        }

        let url = makeTempURL(turnId: turnId, sentenceIndex: sentenceIndex)
        do {
            try mp3Data.write(to: url, options: .atomic)
        } catch {
            return
        }

        turnArchive[turnId, default: []].append(url)

        let item = AVPlayerItem(url: url)

        if let player {
            player.insert(item, after: nil)
        } else {
            let p = AVQueuePlayer(items: [item])
            player = p
            p.play()
        }
    }

    /// Replay all sentences from a previously-played turn. Used by the per-message replay button.
    func replay(turnId: String) {
        guard let urls = turnArchive[turnId], !urls.isEmpty else { return }
        stop()
        let items = urls.map { AVPlayerItem(url: $0) }
        let p = AVQueuePlayer(items: items)
        player = p
        currentTurnId = turnId
        p.play()
    }

    /// Stop and clear the current queue (called on new turn, cancel, or session switch).
    func stop() {
        player?.pause()
        player?.removeAllItems()
        player = nil
    }

    /// Drop archived temp files for a turn (called when session is deleted).
    func purge(turnId: String) {
        if let urls = turnArchive.removeValue(forKey: turnId) {
            urls.forEach { try? FileManager.default.removeItem(at: $0) }
        }
    }

    private func makeTempURL(turnId: String, sentenceIndex: Int) -> URL {
        let name = "achates-speech-\(turnId)-\(sentenceIndex).mp3"
        return FileManager.default.temporaryDirectory.appendingPathComponent(name)
    }
}
```

- [ ] **Step 2: Add `audio` background mode to Info.plist**

Locate `apple/Achates/Info.plist` and add the `UIBackgroundModes` array with `audio`:

```xml
<key>UIBackgroundModes</key>
<array>
    <string>audio</string>
</array>
```

If `UIBackgroundModes` is already present, just add `<string>audio</string>` to the existing array (don't replace).

- [ ] **Step 3: Build iOS app, commit**

```bash
git add apple/Achates/Services/SpeechPlayer.swift apple/Achates/Info.plist
git commit -m "feat(ios): SpeechPlayer (AVQueuePlayer wrapper) + audio background mode"
```

---

### Task 19: Wire `audio.block` / `audio.error` events into the WebSocket handler

**Files:**
- Modify: the existing WebSocket event-handling site in `apple/Achates/Services/`

- [ ] **Step 1: Locate the event dispatch**

```bash
grep -rnE "text.delta|text.end|case \"agent_turn|EventFrame|handleEvent" apple/Achates/Services/ | head
```

Find the switch/if-else that routes incoming event types.

- [ ] **Step 2: Add cases for `audio.block` and `audio.error`**

In the event handler, add:

```swift
        case "audio.block":
            guard
                let turnId = payload["turn_id"] as? String,
                let sentenceIndex = payload["sentence_index"] as? Int,
                let dataString = payload["data"] as? String,
                let data = Data(base64Encoded: dataString)
            else { return }
            speechPlayer.enqueue(turnId: turnId, sentenceIndex: sentenceIndex, mp3Data: data)

            // Cache the text + turn association on the matching message so the replay button works.
            if let text = payload["text"] as? String {
                viewModel.recordAudioMetadata(turnId: turnId, sentenceIndex: sentenceIndex, text: text)
            }

        case "audio.error":
            let turnId = payload["turn_id"] as? String ?? "<unknown>"
            let message = payload["message"] as? String ?? "Speech unavailable."
            viewModel.recordAudioError(turnId: turnId, message: message)
```

The exact property access syntax depends on whether the event payload is `[String: Any]`, a typed `EventPayload`, or a JSON `Decodable`. Match the existing handler style.

`speechPlayer` and `viewModel` should be available in the handler's scope; if not, inject them via the existing dependency-injection mechanism (e.g., `@Environment`, `@EnvironmentObject`, or constructor).

- [ ] **Step 3: Add `recordAudioMetadata` and `recordAudioError` on the view model**

Find the chat view model (likely `ChatViewModel.swift`):

```bash
find apple/Achates -name "ChatViewModel.swift" -o -name "Chat*.swift" | head -5
```

Add methods to associate the turnId with the most-recent assistant message. The exact implementation depends on how messages are stored — at minimum, append the text and remember the audio status per assistant message.

```swift
    func recordAudioMetadata(turnId: String, sentenceIndex: Int, text: String) {
        guard let lastIndex = messages.lastIndex(where: { $0.role == .assistant }) else { return }
        var msg = messages[lastIndex]
        if msg.audioTurnId == nil { msg.audioTurnId = turnId }
        if msg.audioTurnId == turnId {
            // Append the sentence text to a sidecar transcript for replay/debug.
            msg.audioTranscript.append(text)
        }
        messages[lastIndex] = msg
    }

    func recordAudioError(turnId: String, message: String) {
        guard let lastIndex = messages.lastIndex(where: { $0.role == .assistant && $0.audioTurnId == turnId }) else {
            // Fallback to the latest assistant message even if turnId hasn't been bound yet.
            guard let li = messages.lastIndex(where: { $0.role == .assistant }) else { return }
            var msg = messages[li]
            msg.audioError = message
            messages[li] = msg
            return
        }
        var msg = messages[lastIndex]
        msg.audioError = message
        messages[lastIndex] = msg
    }
```

Add the supporting fields (`audioTurnId: String?`, `audioTranscript: [String]`, `audioError: String?`) to the `Message` model. Default initializers should make this additive.

- [ ] **Step 4: Build iOS app**

Build via Xcode (⌘B). Expect: compiles clean. Audio events have nowhere to surface in UI yet — that's the next tasks.

- [ ] **Step 5: Commit**

```bash
git add apple/Achates/Services/ apple/Achates/Models/ apple/Achates/ViewModels/
git commit -m "feat(ios): handle audio.block + audio.error events; per-message audio metadata"
```

---

### Task 20: Speak toggle in `ChatView` nav bar

**Files:**
- Modify: `apple/Achates/Views/ChatView.swift`

- [ ] **Step 1: Add a toolbar button bound to the session's `speechEnabled`**

Find `ChatView.swift` and locate its `.toolbar { ... }` modifier (or add one if absent). Add a toolbar item:

```swift
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        Task { await viewModel.toggleSpeech() }
                    } label: {
                        Image(systemName: viewModel.session.speechEnabled ? "speaker.wave.2.fill" : "speaker.slash")
                    }
                    .accessibilityLabel(viewModel.session.speechEnabled ? "Disable speech" : "Enable speech")
                }
            }
```

- [ ] **Step 2: Implement `toggleSpeech` on the view model**

```swift
    func toggleSpeech() async {
        let newState = !session.speechEnabled
        do {
            _ = try await api.call(method: "session.set_speech", params: [
                "agent": agentName,
                "session_id": session.id,
                "enabled": newState,
            ])
            session.speechEnabled = newState
        } catch {
            // Toggle stays at its previous state; surface error if there's an existing error path.
        }
    }
```

- [ ] **Step 3: Build iOS app, commit**

```bash
git add apple/Achates/Views/ChatView.swift apple/Achates/ViewModels/
git commit -m "feat(ios): speak toggle in chat nav bar (session.set_speech RPC)"
```

---

### Task 21: Per-message replay button in `MessageBubbleView`

**Files:**
- Modify: `apple/Achates/Views/MessageBubbleView.swift`

- [ ] **Step 1: Add a small replay button to assistant messages with audio**

In `MessageBubbleView.swift`, add at the bottom of the assistant-message branch:

```swift
            if message.role == .assistant, let turnId = message.audioTurnId, !message.audioTranscript.isEmpty {
                HStack(spacing: 4) {
                    Button {
                        speechPlayer.replay(turnId: turnId)
                    } label: {
                        Image(systemName: "play.circle")
                            .font(.caption)
                    }
                    .accessibilityLabel("Replay audio")
                    .buttonStyle(.plain)
                }
                .foregroundStyle(.secondary)
            }

            if let err = message.audioError {
                HStack(spacing: 4) {
                    Image(systemName: "speaker.slash")
                    Text("Speech unavailable")
                }
                .font(.caption2)
                .foregroundStyle(.secondary)
                .help(err)
            }
```

`speechPlayer` should be available via `@Environment` or injected. Match the existing wiring style in the project — search for `@Environment` or `@EnvironmentObject` usage:

```bash
grep -rnE "@Environment|@EnvironmentObject" apple/Achates/Views/ | head
```

- [ ] **Step 2: Build iOS app, commit**

```bash
git add apple/Achates/Views/MessageBubbleView.swift
git commit -m "feat(ios): per-message replay button + speech-unavailable indicator"
```

---

### Task 22: Voice picker in `AgentEditView`

**Files:**
- Modify: `apple/Achates/Views/AgentEditView.swift`

- [ ] **Step 1: Add a voice picker section**

In `AgentEditView.swift`, after the existing fields (description, prompt, etc.), add a `Section` for voice:

```swift
                Section("Voice") {
                    Picker("Voice", selection: $voice) {
                        Text("Voiceless").tag("")
                        ForEach(voiceRegistry.voices, id: \.self) { v in
                            Text(v).tag(v)
                        }
                    }
                    .pickerStyle(.menu)
                    .task { await voiceRegistry.loadIfStale() }

                    TextField("Custom blend (e.g. af_nicole(0.7)+af_bella(0.3))",
                              text: $customBlend,
                              prompt: Text("Override picker with a custom blend"))
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .onChange(of: customBlend) { _, new in
                            if !new.isEmpty { voice = new }
                        }

                    Button("Preview Voice") {
                        Task { await previewVoice() }
                    }
                    .disabled(voice.isEmpty)
                }
```

Add state at the top of the view:

```swift
    @State private var voice: String = ""
    @State private var customBlend: String = ""
    @State private var voiceRegistry: VoiceRegistry
```

(Initialize `voiceRegistry` from the environment / DI container the project uses, matching how other services are passed in.)

In `onAppear` or `task`, seed `voice` from the agent's current value:

```swift
            .task {
                voice = agent.voice ?? ""
            }
```

When saving, send `voice` (or empty string for voiceless) in the `agent.update` RPC params.

- [ ] **Step 2: Implement `previewVoice`**

```swift
    private func previewVoice() async {
        let phrase = "Hello, this is how I sound."
        // Use the same on-device player by calling a server-side preview, OR
        // synthesize via the existing app/server API if a "preview" endpoint exists.
        // For v1, simply call session.set_speech off + use voices preview via the
        // existing speech path on a transient hidden turn:
        // (Implementation depends on whether the server exposes a preview RPC; if
        // not, set the agent's voice, save, send the phrase, and rely on the
        // normal speech path.)
    }
```

For v1, preview = "save voice, then send a test message". If you want a non-mutating preview, add a future RPC `speech.preview {voice, text}` — note as a follow-up issue.

- [ ] **Step 3: Build iOS app, commit**

```bash
git add apple/Achates/Views/AgentEditView.swift
git commit -m "feat(ios): voice picker in agent edit sheet (with custom blend + preview)"
```

---

## Phase 9: Documentation

### Task 23: Update `CLAUDE.md`

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Add `tools.speech` to the config example**

Find the YAML config example in `CLAUDE.md` (under "Global config (`~/.achates/config.yaml`)"). In the `tools:` block, after the existing tool configs, add:

````markdown
  speech:
    sidecar:
      working_dir: ~/kokoro-fastapi
      command: uv
      args: [run, uvicorn, api.src.main:app, --host, "127.0.0.1", --port, "8880"]
    # Alternatively, use an external sidecar:
    # endpoint: http://127.0.0.1:8880
    # Optional global default voice:
    # default_voice: af_nicole
````

- [ ] **Step 2: Add `**Voice:**` capability to the AGENT.md example**

Find the AGENT.md example in `CLAUDE.md` (under "Agent definitions"). After `**Reasoning Effort:** medium`, add:

```markdown

**Voice:** af_nicole
```

Also update the "Capabilities keys" sentence to include `Voice`.

- [ ] **Step 3: Add a `Speech` subsection under "Server"**

In the `### Server` section, after the existing tool descriptions, add:

```markdown
- **Speech pipeline** (`src/Achates.Server/Speech/`) — local TTS via a
  Kokoro-FastAPI sidecar. `ISpeechSynthesizer` is the engine-agnostic seam;
  `KokoroSpeechSynthesizer` calls the OpenAI-compatible `/v1/audio/speech`
  endpoint on the sidecar. `KokoroSidecarProcess` (IHostedService) supervises
  the optional managed sidecar (spawn on startup, health-check, restart with
  backoff, kill on shutdown) — external sidecars are also supported via
  `tools.speech.endpoint`. `SpeechBroker` is constructed per turn when the
  session has `SpeechEnabled` and the agent has a `Voice` (or
  `tools.speech.default_voice` is set); it consumes the assistant text
  stream, segments into sentences via `SentenceSegmenter`, sanitizes them
  via `SpeechSanitizer` (strips markdown, code fences, URLs, emoji),
  synthesizes each, and emits `audio.block` events on the WebSocket. Tool
  calls, thinking blocks, and inter-agent chat replies are never spoken.
```

- [ ] **Step 4: Update the MobileTransport events list and RPCs**

Find the "Broadcast events" line in the Transport section and add `audio.block`, `audio.error` to the agent streaming events list.

Find the "RPC methods" line and add `session.set_speech`, `voices.list` to the list.

- [ ] **Step 5: Add `SpeechEnabled` to the `MobileSession` description**

In the description of `MobileSession`, mention the new field:

```markdown
... and optional `SpeechEnabled` (`bool`, default `false`) controlling
whether the assistant's text replies are also spoken via Kokoro TTS for this
session.
```

- [ ] **Step 6: Commit**

```bash
git add CLAUDE.md
git commit -m "docs(claude.md): document voice/speech feature"
```

---

### Task 24: Update `docs/configuration.md`

**Files:**
- Modify: `docs/configuration.md`

- [ ] **Step 1: Add a new section explaining `tools.speech`**

Append (or insert in the appropriate location) a new section:

````markdown
## Speech (TTS)

Voice playback uses a locally-hosted [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI)
sidecar — nothing about a conversation leaves your machine, and there is no
content moderation.

### Managed sidecar (recommended)

Achates will auto-launch and supervise the sidecar:

```yaml
tools:
  speech:
    sidecar:
      working_dir: ~/kokoro-fastapi
      command: uv
      args: [run, uvicorn, api.src.main:app, --host, "127.0.0.1", --port, "8880"]
```

The sidecar must be installed separately (see `docs/speech-setup.md`).
Achates spawns the process on startup, polls `GET /health`, marks
availability when it responds 200 (60s timeout), and restarts on
unexpected exit with exponential backoff (1s → 5s → 30s → 5min steady).
Clean shutdown sends SIGTERM and falls back to SIGKILL after 5 seconds.

### External sidecar

If you run the sidecar yourself (Docker, dev loop, shared instance), point
Achates at it:

```yaml
tools:
  speech:
    endpoint: http://127.0.0.1:8880
```

Achates will not spawn a process; it only health-checks the endpoint. If
both `sidecar` and `endpoint` are configured, `endpoint` wins (and a
warning is logged).

### Default voice (opt-in)

```yaml
tools:
  speech:
    # ... sidecar or endpoint ...
    default_voice: af_nicole
```

When set, agents without an explicit `**Voice:**` capability use this voice.
Off by default — voiceless agents stay silent.

### Per-agent voice

Each agent declares its voice in `~/.achates/agents/{name}/AGENT.md`:

```markdown
## Capabilities

**Voice:** af_nicole
```

Kokoro voice blending is supported verbatim:

```markdown
**Voice:** af_nicole(0.7)+af_bella(0.3)
```

Omitting `**Voice:**` makes the agent voiceless — speech is never generated
for this agent unless `tools.speech.default_voice` is configured globally.

### Per-session toggle

Each session has a `speech_enabled` flag (default `false`). The iOS app
exposes a 🔊/🔇 toggle in the chat nav bar that flips it via the
`session.set_speech` RPC. Audio events (`audio.block`) are only emitted for
sessions where the flag is on.
````

- [ ] **Step 2: Commit**

```bash
git add docs/configuration.md
git commit -m "docs(configuration): document tools.speech block"
```

---

### Task 25: Create `docs/speech-setup.md`

**Files:**
- Create: `docs/speech-setup.md`

- [ ] **Step 1: Write the setup recipe**

Create `docs/speech-setup.md`:

````markdown
# Speech Setup (Kokoro-FastAPI)

Achates uses [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) as
a local TTS sidecar. The model runs entirely on your machine; nothing is
sent to a cloud TTS vendor, and there is no content moderation.

This guide walks through a one-time install on macOS (Apple Silicon).
Linux is similar; Windows is not currently tested.

## Prerequisites

```bash
brew install ffmpeg uv jq
```

## Install Kokoro-FastAPI

```bash
git clone https://github.com/remsky/Kokoro-FastAPI.git ~/kokoro-fastapi
cd ~/kokoro-fastapi
uv sync
bash docker/scripts/download_model.sh   # downloads kokoro-v1_0.pth (~330MB)
```

## Configure for native (non-Docker) run

The default config in the repo is wired for the Docker container layout
(`/app/...` paths, CUDA). Override for native run:

```bash
cat > ~/kokoro-fastapi/.env <<'EOF'
USE_GPU=false
MODEL_DIR=/Users/<you>/kokoro-fastapi/api/src/models
VOICES_DIR=/Users/<you>/kokoro-fastapi/api/src/voices/v1_0
EOF
```

Replace `<you>` with your username. (The `.env` is loaded by uvicorn from
the working directory.)

## Test the sidecar standalone

```bash
cd ~/kokoro-fastapi
uv run uvicorn api.src.main:app --host 127.0.0.1 --port 8880
```

You should see `Initializing Kokoro V1 on cpu` and `Application startup
complete`. From another terminal:

```bash
curl -X POST http://127.0.0.1:8880/v1/audio/speech \
  -H "Content-Type: application/json" \
  -d '{"model":"kokoro","voice":"af_nicole","input":"Hello.","response_format":"mp3"}' \
  --output /tmp/test.mp3 && afplay /tmp/test.mp3
```

If you hear the greeting, the sidecar is healthy.

## Tell Achates about it

In `~/.achates/config.yaml`:

```yaml
tools:
  speech:
    sidecar:
      working_dir: ~/kokoro-fastapi
      command: uv
      args: [run, uvicorn, api.src.main:app, --host, "127.0.0.1", --port, "8880"]
```

Restart Achates. On startup it will spawn the sidecar (you'll see
`[kokoro]`-prefixed lines in the log) and mark speech available after the
health check passes.

## Per-agent voice

Add a `**Voice:**` capability to any agent's `AGENT.md`:

```markdown
## Capabilities

**Voice:** af_nicole
```

Or a blend:

```markdown
**Voice:** af_nicole(0.7)+af_bella(0.3)
```

## Enable speech for a session

In the iOS app, tap the 🔇 icon in the chat nav bar to toggle it on.
Replies will be spoken as they stream.

## Troubleshooting

- **`Read-only file system: /app`** — `MODEL_DIR`/`VOICES_DIR` in `.env`
  point at container paths. Set them to absolute paths under your home.
- **`Initializing Kokoro V1 on cuda`** — `USE_GPU=true` (default).
  Set `USE_GPU=false` in `.env`.
- **Port 8880 in use** — kill the existing listener (`lsof -ti :8880 | xargs kill`)
  or change `--port` in both the sidecar args and the Achates config.
- **No audio events in app** — check the per-session toggle is on
  (🔊 in nav bar) and the agent has `**Voice:**` set (or
  `tools.speech.default_voice` configured globally).
- **`audio.error` chip on every message** — check Achates server logs for
  `[kokoro]`-prefixed errors; verify the sidecar process is running with
  `lsof -i :8880`.

## Uninstall

```bash
rm -rf ~/kokoro-fastapi /tmp/voice-*.mp3 /tmp/achates-speech-*.mp3
```

Remove the `tools.speech` block from `~/.achates/config.yaml`.
````

- [ ] **Step 2: Commit**

```bash
git add docs/speech-setup.md
git commit -m "docs: speech-setup.md (Kokoro-FastAPI install recipe)"
```

---

### Task 26: Update `README.md`

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Add a brief mention of voice support**

Find an appropriate section in `README.md` (Features, or near tool descriptions). Add:

```markdown
- **Voice** — Agents can speak their replies via a local TTS sidecar
  ([Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI)). Fully
  private (no cloud), no content moderation, per-agent voice identity.
  See [`docs/speech-setup.md`](docs/speech-setup.md) to enable.
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs(readme): mention voice/speech feature"
```

---

## Phase 10: End-to-end verification

### Task 27: Manual end-to-end smoke test

**Files:** None (manual test plan).

This task is non-mechanical. Walk through it once both sides (server + iOS) are built.

- [ ] **Step 1: Install kokoro-fastapi** per `docs/speech-setup.md`. Verify it runs standalone via the `curl` test.

- [ ] **Step 2: Add `tools.speech` to your `~/.achates/config.yaml`** with the managed-sidecar block.

- [ ] **Step 3: Add `**Voice:** af_nicole` to one of your agents' AGENT.md** (or use `default_voice: af_nicole` in config to apply to all).

- [ ] **Step 4: Start the Achates server**

```bash
cd ~/Projects/achates
dotnet run --project src/Achates.Server
```

Expected: log lines from the server, plus `[kokoro]`-prefixed lines from the sidecar, ending with `Speech: Kokoro sidecar is ready.`

- [ ] **Step 5: Connect from the iOS app**, open a session with the voiced agent, tap the 🔊 toggle. Send a prompt that elicits a multi-sentence reply (e.g., "Tell me about the history of the printing press in three sentences.").

Expected:
- Text streams normally.
- About 2–4 seconds after the first sentence appears in text, you hear it spoken in `af_nicole`.
- Each subsequent sentence plays in order.
- The 🔊 icon is "on" in the nav bar.
- The assistant message has a small play.circle button at the bottom; tapping it re-plays the reply.

- [ ] **Step 6: Verify speech failure handling**

Stop the kokoro-fastapi process while Achates is running (`lsof -ti :8880 | xargs kill`). Send another prompt with speech still enabled.

Expected:
- Text streams normally (unaffected).
- An `audio.error` event lands on the message — the bubble shows "🔇 Speech unavailable".
- After ~1 minute, Achates retries the sidecar; it's gone, so the chip persists.

Restart the sidecar (`cd ~/kokoro-fastapi && uv run uvicorn api.src.main:app --host 127.0.0.1 --port 8880`). After the next backoff window, speech recovers for new turns.

- [ ] **Step 7: Verify voiceless agent**

In an agent without `**Voice:**` set (and without `default_voice` in config), enable speech for a session.

Expected:
- Toggle flips on.
- No `audio.block` events emitted on assistant replies (text streams normally).
- iOS shows the alternate icon state hinting "no voice configured for this agent" when toggle is on for a voiceless agent.

- [ ] **Step 8: Verify code/markdown stripping**

Send the agent a prompt that elicits a code block:

> "Show me a 5-line Python example, then a one-sentence summary."

Expected:
- Text shows the code block fully.
- Audio plays the surrounding prose but is *silent* during the code block.
- The post-code summary sentence is spoken.

- [ ] **Step 9: Verify spicy content path** (the original "no moderation" requirement)

Send the agent a deliberately spicy prompt with speech on.

Expected: the reply is spoken in full without any TTS refusal. (If this fails, the wrong engine is being used — verify it's going to the local sidecar.)

- [ ] **Step 10: Run the full test suite once more**

```bash
dotnet test Achates.slnx
```

Expected: all tests pass, including the new speech tests.

---

## Self-review checklist

Run through this before declaring the plan complete:

- [ ] **Spec coverage:** Open `docs/superpowers/specs/2026-05-24-agent-voice-design.md` side-by-side. For each numbered section (Architecture, Code Layout, Per-Agent Voice Config, What Gets Spoken, Transport Protocol, Sidecar Lifecycle, iOS Playback, Config Schema, Documentation Deliverables, Forward Compatibility, Out of Scope), point to the task(s) that implement it. Any gaps → add a task.
- [ ] **No placeholders:** grep this plan for "TBD", "TODO", "implement later", "appropriate", "similar to" — all should be absent.
- [ ] **Type / name consistency:** `ISpeechSynthesizer.SynthesizeAsync`, `SynthesisResult(Audio, Format)`, `ISpeechSink.EmitAudioBlockAsync` — names used in Task 9 match names used in Task 12. `SpeechConfig.Sidecar`/`.Endpoint`/`.DefaultVoice` consistent across tasks.
- [ ] **iOS structure:** confirmed the iOS app uses SwiftUI views + view models (`@Observable` per the existing `SpeechService.swift` pattern). Tasks 16-22 follow that style.
