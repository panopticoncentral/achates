using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Providers.Completions.Content;

namespace Achates.Tests;

public class AgentLoopInjectionTests
{
    private static ToolResultMessage Result(IReadOnlyList<CompletionUserContent>? injected) =>
        new()
        {
            ToolCallId = "t",
            ToolName = "library",
            Content = [new CompletionTextContent { Text = "ok" }],
            InjectedUserContent = injected,
        };

    [Fact]
    public void Returns_null_when_no_results_carry_injected_content()
    {
        var msg = AgentLoop.BuildInjectedUserMessage([Result(null), Result([])]);
        Assert.Null(msg);
    }

    [Fact]
    public void Merges_injected_content_from_multiple_results_into_one_message()
    {
        var pdf1 = new CompletionFileContent { Data = "AA==", MimeType = "application/pdf", FileName = "a.pdf" };
        var pdf2 = new CompletionFileContent { Data = "BB==", MimeType = "application/pdf", FileName = "b.pdf" };

        var msg = AgentLoop.BuildInjectedUserMessage([Result([pdf1]), Result(null), Result([pdf2])]);

        Assert.NotNull(msg);
        Assert.Equal(2, msg!.Content!.Count);
        Assert.Contains("a.pdf", msg.Text);
        Assert.Contains("b.pdf", msg.Text);
    }
}
