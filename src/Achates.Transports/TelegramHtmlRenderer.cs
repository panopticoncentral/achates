using System.Net;
using System.Text;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Achates.Transports;

/// <summary>
/// Converts standard Markdown to the HTML subset supported by Telegram's Bot API.
/// </summary>
public static class TelegramHtmlRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseTaskLists()
        .Build();

    public static string Convert(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var sb = new StringBuilder();
        RenderBlocks(sb, document, listDepth: 0);
        return sb.ToString().TrimEnd();
    }

    private static void RenderBlocks(StringBuilder sb, ContainerBlock container, int listDepth)
    {
        for (var i = 0; i < container.Count; i++)
        {
            var block = container[i];

            if (i > 0 && block is not ListItemBlock)
                sb.Append('\n');

            switch (block)
            {
                case ParagraphBlock paragraph:
                    RenderInlines(sb, paragraph.Inline!);
                    sb.Append('\n');
                    break;

                case HeadingBlock heading:
                    sb.Append("<b>");
                    RenderInlines(sb, heading.Inline!);
                    sb.Append("</b>\n");
                    break;

                case FencedCodeBlock fenced:
                    RenderCodeBlock(sb, fenced, fenced.Info);
                    break;

                case CodeBlock code:
                    RenderCodeBlock(sb, code, info: null);
                    break;

                case QuoteBlock quote:
                    sb.Append("<blockquote>");
                    RenderBlocks(sb, quote, listDepth);
                    sb.Append("</blockquote>");
                    break;

                case ListBlock list:
                    RenderList(sb, list, listDepth);
                    break;

                case ListItemBlock listItem:
                    RenderListItem(sb, listItem, listDepth);
                    break;

                case ThematicBreakBlock:
                    sb.Append("---\n");
                    break;

                case HtmlBlock html:
                    foreach (var line in html.Lines.Lines)
                    {
                        if (line.Slice.Text is not null)
                            sb.Append(line.Slice);
                    }
                    sb.Append('\n');
                    break;

                case ContainerBlock nested:
                    RenderBlocks(sb, nested, listDepth);
                    break;

                case LeafBlock leaf:
                    if (leaf.Inline is not null)
                        RenderInlines(sb, leaf.Inline);
                    else
                        AppendLeafRawLines(sb, leaf);
                    sb.Append('\n');
                    break;
            }
        }
    }

    private static void RenderCodeBlock(StringBuilder sb, LeafBlock code, string? info)
    {
        if (!string.IsNullOrWhiteSpace(info))
            sb.Append($"<pre><code class=\"language-{Escape(info.Trim())}\">");
        else
            sb.Append("<pre><code>");

        AppendLeafRawLines(sb, code);

        sb.Append("</code></pre>\n");
    }

    private static void RenderList(StringBuilder sb, ListBlock list, int listDepth)
    {
        var index = list.OrderedStart is { } start && int.TryParse(start, out var n) ? n : 1;

        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem) continue;

            var indent = new string(' ', listDepth * 2);

            if (list.IsOrdered)
            {
                sb.Append($"{indent}{index}. ");
                index++;
            }
            else
            {
                // Check for task list items
                var taskItem = listItem.Descendants<TaskList>().FirstOrDefault();
                if (taskItem is not null)
                    sb.Append(taskItem.Checked ? $"{indent}☑ " : $"{indent}☐ ");
                else
                    sb.Append($"{indent}• ");
            }

            RenderListItem(sb, listItem, listDepth);
        }
    }

    private static void RenderListItem(StringBuilder sb, ListItemBlock listItem, int listDepth)
    {
        for (var i = 0; i < listItem.Count; i++)
        {
            var child = listItem[i];
            switch (child)
            {
                case ParagraphBlock paragraph:
                    // Filter out TaskList inlines (already rendered as prefix)
                    RenderInlines(sb, paragraph.Inline!, skipTaskList: true);
                    if (i < listItem.Count - 1)
                        sb.Append('\n');
                    break;

                case ListBlock nestedList:
                    sb.Append('\n');
                    RenderList(sb, nestedList, listDepth + 1);
                    return; // nested list handles its own newlines

                default:
                    if (child is ContainerBlock container)
                        RenderBlocks(sb, container, listDepth);
                    else if (child is LeafBlock leaf)
                    {
                        if (leaf.Inline is not null)
                            RenderInlines(sb, leaf.Inline);
                        else
                            AppendLeafRawLines(sb, leaf);
                    }
                    break;
            }
        }
        sb.Append('\n');
    }

    private static void RenderInlines(StringBuilder sb, ContainerInline container, bool skipTaskList = false)
    {
        var inline = container.FirstChild;
        while (inline is not null)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(Escape(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                    var tag = emphasis.DelimiterChar switch
                    {
                        '~' => "s",
                        _ => emphasis.DelimiterCount >= 2 ? "b" : "i",
                    };
                    sb.Append($"<{tag}>");
                    RenderInlines(sb, emphasis);
                    sb.Append($"</{tag}>");
                    break;

                case CodeInline code:
                    sb.Append("<code>");
                    sb.Append(Escape(code.Content));
                    sb.Append("</code>");
                    break;

                case LinkInline link:
                    if (link.IsImage)
                    {
                        // Telegram has no image tag — render as link
                        sb.Append($"<a href=\"{Escape(link.Url ?? "")}\">");
                        if (link.FirstChild is not null)
                            RenderInlines(sb, link);
                        else
                            sb.Append(Escape(link.Url ?? ""));
                        sb.Append("</a>");
                    }
                    else
                    {
                        sb.Append($"<a href=\"{Escape(link.Url ?? "")}\">");
                        RenderInlines(sb, link);
                        sb.Append("</a>");
                    }
                    break;

                case AutolinkInline autolink:
                    sb.Append($"<a href=\"{Escape(autolink.Url)}\">{Escape(autolink.Url)}</a>");
                    break;

                case LineBreakInline:
                    sb.Append('\n');
                    break;

                case HtmlInline html:
                    sb.Append(html.Tag);
                    break;

                case HtmlEntityInline entity:
                    sb.Append(Escape(entity.Transcoded.ToString()));
                    break;

                case TaskList when skipTaskList:
                    // Already rendered as list prefix
                    break;

                case ContainerInline nested:
                    RenderInlines(sb, nested);
                    break;
            }

            inline = inline.NextSibling;
        }
    }

    private static void AppendLeafRawLines(StringBuilder sb, LeafBlock leaf)
    {
        var lines = leaf.Lines.Lines;
        for (var i = 0; i < leaf.Lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            var text = lines[i].Slice.Text;
            if (text is not null)
                sb.Append(Escape(lines[i].Slice.ToString()));
        }
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);
}
