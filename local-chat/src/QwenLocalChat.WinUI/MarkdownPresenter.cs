using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using QwenLocalChat.Core;

namespace QwenLocalChat_WinUI;

public sealed class MarkdownPresenter : Grid
{
    private readonly RichTextBlock _content;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarkdownPresenter),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public MarkdownPresenter()
    {
        _content = new RichTextBlock
        {
            Style = (Style)Application.Current.Resources["MarkdownBodyTextStyle"],
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        Children.Add(_content);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((MarkdownPresenter)sender).Render(args.NewValue as string ?? string.Empty);

    private void Render(string markdown)
    {
        _content.Blocks.Clear();
        foreach (var block in MarkdownPresentation.Parse(markdown))
        {
            _content.Blocks.Add(CreateParagraph(block));
        }
    }

    private static Paragraph CreateParagraph(MarkdownBlock block)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8),
        };

        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading:
                paragraph.FontSize = block.Level <= 1 ? 20 : block.Level == 2 ? 18 : 16;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 4, 0, 8);
                break;
            case MarkdownBlockKind.ListItem:
                paragraph.Margin = new Thickness(12, 0, 0, 4);
                paragraph.Inlines.Add(new Run { Text = $"{block.Prefix} " });
                break;
            case MarkdownBlockKind.Quote:
                paragraph.Margin = new Thickness(12, 2, 0, 8);
                paragraph.Inlines.Add(new Run
                {
                    Text = "│ ",
                    Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                });
                break;
            case MarkdownBlockKind.Code:
                paragraph.FontFamily = new FontFamily("Cascadia Mono");
                paragraph.Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"];
                paragraph.Margin = new Thickness(12, 4, 0, 8);
                break;
            case MarkdownBlockKind.Rule:
                paragraph.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
                paragraph.Margin = new Thickness(0, 2, 0, 8);
                paragraph.Inlines.Add(new Run { Text = "────────────────" });
                return paragraph;
        }

        AppendInlines(paragraph.Inlines, block.Inlines);
        return paragraph;
    }

    private static void AppendInlines(InlineCollection target, IReadOnlyList<MarkdownInline> source)
    {
        foreach (var item in source)
        {
            if (item.Text == "\n")
            {
                target.Add(new LineBreak());
                continue;
            }

            var run = new Run { Text = item.Text };
            if (item.Style.HasFlag(MarkdownInlineStyle.Strong)) run.FontWeight = FontWeights.SemiBold;
            if (item.Style.HasFlag(MarkdownInlineStyle.Emphasis)) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (item.Style.HasFlag(MarkdownInlineStyle.Code))
            {
                run.FontFamily = new FontFamily("Cascadia Mono");
                run.Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            }

            if (item.Link is not null)
            {
                var hyperlink = new Hyperlink { NavigateUri = new Uri(item.Link) };
                hyperlink.Inlines.Add(run);
                target.Add(hyperlink);
            }
            else
            {
                target.Add(run);
            }
        }
    }
}
