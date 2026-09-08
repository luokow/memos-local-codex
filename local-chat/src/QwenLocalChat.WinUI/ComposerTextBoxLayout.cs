using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QwenLocalChat.Core;
using Windows.Foundation;

namespace QwenLocalChat_WinUI;

internal static class ComposerTextBoxLayout
{
    public static void Apply(TextBox input, double viewportHeight, bool auxiliarySectionExpanded = false)
    {
        var availableTextWidth = input.ActualWidth - input.Padding.Left - input.Padding.Right;
        var desiredHeight = ComposerHeightPolicy.MinimumHeight;
        if (double.IsFinite(availableTextWidth) && availableTextWidth > 0)
        {
            var probe = new TextBlock
            {
                Text = string.IsNullOrEmpty(input.Text) ? " " : input.Text,
                FontFamily = input.FontFamily,
                FontSize = input.FontSize,
                FontStyle = input.FontStyle,
                FontWeight = input.FontWeight,
                CharacterSpacing = input.CharacterSpacing,
                TextWrapping = TextWrapping.Wrap,
            };
            probe.Measure(new Size(availableTextWidth, double.PositiveInfinity));
            desiredHeight = Math.Ceiling(
                probe.DesiredSize.Height + input.Padding.Top + input.Padding.Bottom + 8);
        }

        var resolved = ComposerHeightPolicy.Resolve(
            viewportHeight,
            desiredHeight,
            auxiliarySectionExpanded);
        input.Height = resolved.AppliedHeight;
        ScrollViewer.SetVerticalScrollBarVisibility(
            input,
            resolved.UsesInternalScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
    }
}
