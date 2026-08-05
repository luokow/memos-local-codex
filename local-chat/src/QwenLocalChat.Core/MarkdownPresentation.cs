using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace QwenLocalChat.Core;

[Flags]
public enum MarkdownInlineStyle
{
    None = 0,
    Strong = 1,
    Emphasis = 2,
    Code = 4,
}

public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    Quote,
    Code,
    ListItem,
    Rule,
}

public sealed record MarkdownInline(string Text, MarkdownInlineStyle Style = MarkdownInlineStyle.None, string? Link = null);

public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    IReadOnlyList<MarkdownInline> Inlines,
    int Level = 0,
    string? Prefix = null,
    string? Language = null);

public static class MarkdownPresentation
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return [];

        var result = new List<MarkdownBlock>();
        AppendBlocks(Markdown.Parse(markdown, Pipeline), result);
        return result;
    }

    public static string ToPlainText(string markdown)
    {
        var blocks = Parse(markdown);
        var result = new System.Text.StringBuilder();
        MarkdownBlockKind? previousKind = null;

        foreach (var block in blocks)
        {
            if (result.Length > 0)
            {
                result.Append(previousKind == MarkdownBlockKind.ListItem && block.Kind == MarkdownBlockKind.ListItem
                    ? '\n'
                    : "\n\n");
            }

            var text = string.Concat(block.Inlines.Select(run => run.Text)).TrimEnd('\r', '\n');
            switch (block.Kind)
            {
                case MarkdownBlockKind.ListItem:
                    result.Append(block.Prefix);
                    if (!string.IsNullOrEmpty(block.Prefix)) result.Append(' ');
                    result.Append(text);
                    break;
                case MarkdownBlockKind.Quote:
                    result.Append("> ");
                    result.Append(text);
                    break;
                case MarkdownBlockKind.Rule:
                    result.Append("────────");
                    break;
                default:
                    result.Append(text);
                    break;
            }
            previousKind = block.Kind;
        }

        return result.ToString();
    }

    private static void AppendBlocks(ContainerBlock container, List<MarkdownBlock> result, MarkdownBlockKind? forcedKind = null)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case ParagraphBlock paragraph:
                    result.Add(new MarkdownBlock(
                        forcedKind ?? MarkdownBlockKind.Paragraph,
                        ParseInlines(paragraph.Inline)));
                    break;
                case HeadingBlock heading:
                    result.Add(new MarkdownBlock(MarkdownBlockKind.Heading, ParseInlines(heading.Inline), heading.Level));
                    break;
                case QuoteBlock quote:
                    AppendBlocks(quote, result, MarkdownBlockKind.Quote);
                    break;
                case ListBlock list:
                    AppendList(list, result);
                    break;
                case FencedCodeBlock fenced:
                    result.Add(new MarkdownBlock(
                        MarkdownBlockKind.Code,
                        [new MarkdownInline(fenced.Lines.ToString(), MarkdownInlineStyle.Code)],
                        Language: fenced.Info?.ToString()));
                    break;
                case CodeBlock code:
                    result.Add(new MarkdownBlock(
                        MarkdownBlockKind.Code,
                        [new MarkdownInline(code.Lines.ToString(), MarkdownInlineStyle.Code)]));
                    break;
                case HtmlBlock html:
                    result.Add(new MarkdownBlock(
                        forcedKind ?? MarkdownBlockKind.Paragraph,
                        [new MarkdownInline(html.Lines.ToString())]));
                    break;
                case ThematicBreakBlock:
                    result.Add(new MarkdownBlock(MarkdownBlockKind.Rule, []));
                    break;
                case ContainerBlock nested:
                    AppendBlocks(nested, result, forcedKind);
                    break;
                case LeafBlock leaf when leaf.Inline is not null:
                    result.Add(new MarkdownBlock(forcedKind ?? MarkdownBlockKind.Paragraph, ParseInlines(leaf.Inline)));
                    break;
            }
        }
    }

    private static void AppendList(ListBlock list, List<MarkdownBlock> result)
    {
        var number = 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var itemBlocks = new List<MarkdownBlock>();
            AppendBlocks(item, itemBlocks);
            var prefix = list.IsOrdered ? $"{number}." : "•";
            foreach (var itemBlock in itemBlocks)
            {
                result.Add(itemBlock with
                {
                    Kind = MarkdownBlockKind.ListItem,
                    Prefix = prefix,
                });
                prefix = string.Empty;
            }
            number++;
        }
    }

    private static IReadOnlyList<MarkdownInline> ParseInlines(ContainerInline? container)
    {
        if (container is null) return [];
        var result = new List<MarkdownInline>();
        AppendInlines(container.FirstChild, result, MarkdownInlineStyle.None, null);
        return result;
    }

    private static void AppendInlines(Inline? current, List<MarkdownInline> result, MarkdownInlineStyle style, string? link)
    {
        while (current is not null)
        {
            switch (current)
            {
                case LiteralInline literal:
                    AddRun(result, literal.Content.ToString(), style, link);
                    break;
                case CodeInline code:
                    AddRun(result, code.Content, style | MarkdownInlineStyle.Code, link);
                    break;
                case LineBreakInline:
                    AddRun(result, "\n", style, link);
                    break;
                case EmphasisInline emphasis:
                    var emphasisStyle = emphasis.DelimiterCount >= 2
                        ? MarkdownInlineStyle.Strong
                        : MarkdownInlineStyle.Emphasis;
                    AppendInlines(emphasis.FirstChild, result, style | emphasisStyle, link);
                    break;
                case LinkInline markdownLink:
                    AppendInlines(markdownLink.FirstChild, result, style, SafeLink(markdownLink.Url));
                    break;
                case HtmlInline html:
                    AddRun(result, html.Tag, style, link);
                    break;
                case ContainerInline nested:
                    AppendInlines(nested.FirstChild, result, style, link);
                    break;
            }
            current = current.NextSibling;
        }
    }

    private static string? SafeLink(string? candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }

    private static void AddRun(List<MarkdownInline> result, string text, MarkdownInlineStyle style, string? link)
    {
        if (text.Length == 0) return;
        if (result.Count > 0 && result[^1].Style == style && result[^1].Link == link)
        {
            result[^1] = result[^1] with { Text = result[^1].Text + text };
            return;
        }
        result.Add(new MarkdownInline(text, style, link));
    }
}
