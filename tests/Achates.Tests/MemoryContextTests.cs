using Achates.Providers.Completions;
using Achates.Providers.Completions.Messages;
using Achates.Server;

namespace Achates.Tests;

public sealed class MemoryContextTests : IDisposable
{
    private readonly string _dir;
    private readonly string _corePath;
    private readonly string _workingPath;

    public MemoryContextTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"achates-memctx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _corePath = Path.Combine(_dir, "memory.md");
        _workingPath = Path.Combine(_dir, "working.md");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static CompletionContext Ctx(params CompletionMessage[] messages) =>
        new() { Messages = messages };

    private static string TextAt(CompletionContext c, int i) =>
        ((CompletionUserTextMessage)c.Messages[i]).Text;

    // --- Formatting ---

    [Fact]
    public void FormatCore_is_empty_for_empty_content()
    {
        Assert.Equal("", MemoryContext.FormatCore(null));
        Assert.Equal("", MemoryContext.FormatCore("   "));
    }

    [Fact]
    public void FormatWorking_tells_the_model_not_to_recite_the_list()
    {
        var block = MemoryContext.FormatWorking("- ask about the surgery");

        Assert.Contains("- ask about the surgery", block);
        Assert.Contains("Working Memory", block);
        Assert.Contains("checklist", block);
    }

    // --- Transform ---

    [Fact]
    public void Transform_is_noop_when_no_user_message_present()
    {
        File.WriteAllText(_corePath, "core");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);
        var context = Ctx();

        Assert.Same(context, transform(context));
    }

    [Fact]
    public void Transform_is_noop_when_both_files_missing()
    {
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);
        var context = Ctx(new CompletionUserTextMessage { Text = "hi", Timestamp = 100 });

        var result = transform(context);

        Assert.Equal("hi", TextAt(result, 0));
    }

    [Fact]
    public void Transform_puts_core_on_the_first_user_message_and_working_on_the_last()
    {
        File.WriteAllText(_corePath, "core fact");
        File.WriteAllText(_workingPath, "- live thread");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        var result = transform(Ctx(
            new CompletionUserTextMessage { Text = "first", Timestamp = 100 },
            new CompletionUserTextMessage { Text = "second", Timestamp = 200 }));

        var first = TextAt(result, 0);
        var last = TextAt(result, 1);

        Assert.Contains("core fact", first);
        Assert.DoesNotContain("live thread", first);
        Assert.Contains("live thread", last);
        Assert.DoesNotContain("core fact", last);
    }

    [Fact]
    public void Transform_puts_core_ahead_of_working_on_a_single_message()
    {
        File.WriteAllText(_corePath, "core fact");
        File.WriteAllText(_workingPath, "- live thread");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        var text = TextAt(transform(Ctx(
            new CompletionUserTextMessage { Text = "hi", Timestamp = 100 })), 0);

        Assert.True(text.IndexOf("core fact", StringComparison.Ordinal)
                  < text.IndexOf("live thread", StringComparison.Ordinal));
        Assert.EndsWith("hi", text);
    }

    [Fact]
    public void Transform_omits_working_when_includeWorking_is_false()
    {
        File.WriteAllText(_corePath, "core fact");
        File.WriteAllText(_workingPath, "- live thread");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: false);

        var text = TextAt(transform(Ctx(
            new CompletionUserTextMessage { Text = "hi", Timestamp = 100 })), 0);

        Assert.Contains("core fact", text);
        Assert.DoesNotContain("live thread", text);
    }

    [Fact]
    public void Transform_does_not_reread_within_the_same_user_turn()
    {
        File.WriteAllText(_corePath, "original");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        var turn = new CompletionUserTextMessage { Text = "hi", Timestamp = 100 };
        var first = TextAt(transform(Ctx(turn)), 0);

        // Same user turn, mid-tool-loop: the file changed but the prefix must not.
        File.WriteAllText(_corePath, "changed");
        var second = TextAt(transform(Ctx(turn)), 0);

        Assert.Equal(first, second);
        Assert.Contains("original", second);
    }

    [Fact]
    public void Transform_rereads_on_a_new_user_turn()
    {
        File.WriteAllText(_corePath, "original");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        transform(Ctx(new CompletionUserTextMessage { Text = "hi", Timestamp = 100 }));

        File.WriteAllText(_corePath, "changed");
        var second = TextAt(transform(Ctx(
            new CompletionUserTextMessage { Text = "hi", Timestamp = 100 },
            new CompletionUserTextMessage { Text = "again", Timestamp = 200 })), 0);

        Assert.Contains("changed", second);
    }

    [Fact]
    public void Core_block_is_byte_identical_across_turns_when_the_file_is_unchanged()
    {
        // Scope note: this checks MemoryContext IN ISOLATION — an unchanged
        // memory.md renders the same bytes onto message 0 turn over turn. It does
        // NOT claim the whole cacheable prefix is stable; composed with
        // TemporalContext the way the runtime composes them, the turn-1 head
        // differs. See Composed_head_is_not_stable_on_the_very_first_turn below.
        File.WriteAllText(_corePath, "core fact");
        var transform = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        var turn1 = TextAt(transform(Ctx(
            new CompletionUserTextMessage { Text = "first", Timestamp = 100 })), 0);

        var turn2 = TextAt(transform(Ctx(
            new CompletionUserTextMessage { Text = "first", Timestamp = 100 },
            new CompletionUserTextMessage { Text = "second", Timestamp = 200 })), 0);

        Assert.Equal(turn1, turn2);
    }

    [Fact]
    public void Composed_head_is_not_stable_on_the_very_first_turn()
    {
        // Honest sibling to the byte-identical test above. MemoryContext's own core
        // block is stable, but the head the provider actually sees is
        // `memory(temporal(ctx))`. On turn 1 the first user message IS the latest
        // one, so message 0 also carries the working block and the temporal note;
        // both move off message 0 on turn 2. So the turn-1 prefix does not match
        // turn 2's, and core is billed uncached twice per session, not once.
        // Stability starts at turn 2 — which is what the design actually buys.
        File.WriteAllText(_corePath, "core fact");
        File.WriteAllText(_workingPath, "- live thread");
        var temporal = TemporalContext.CreateTransform();
        var memory = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);
        string Head(params CompletionMessage[] messages) => TextAt(memory(temporal(Ctx(messages))), 0);

        var turn1 = Head(new CompletionUserTextMessage { Text = "first", Timestamp = 100 });
        var turn2 = Head(
            new CompletionUserTextMessage { Text = "first", Timestamp = 100 },
            new CompletionUserTextMessage { Text = "second", Timestamp = 200 });

        Assert.NotEqual(turn1, turn2);
        Assert.Contains("core fact", turn1);
        Assert.Contains("core fact", turn2);
        Assert.Contains("live thread", turn1);
        Assert.DoesNotContain("live thread", turn2);
        Assert.Contains("Current time:", turn1);
        Assert.DoesNotContain("Current time:", turn2);
    }

    [Fact]
    public void Composes_with_the_temporal_transform_without_either_clobbering_the_other()
    {
        File.WriteAllText(_corePath, "core fact");
        File.WriteAllText(_workingPath, "- live thread");
        var temporal = TemporalContext.CreateTransform();
        var memory = MemoryContext.CreateTransform(_corePath, _workingPath, includeWorking: true);

        var text = TextAt(memory(temporal(Ctx(
            new CompletionUserTextMessage { Text = "hi", Timestamp = 100 }))), 0);

        Assert.Contains("core fact", text);
        Assert.Contains("live thread", text);
        Assert.Contains("Current time:", text);
        Assert.EndsWith("hi", text);
    }
}
