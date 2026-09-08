using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace QwenLocalChat_WinUI;

/// <summary>
/// Left-to-right wrap that keeps each child's own width.
/// ItemsWrapGrid sizes every item to the first chip, which clips later「清除」buttons.
/// </summary>
internal sealed class ChipWrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(ChipWrapPanel),
            new PropertyMetadata(10d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(ChipWrapPanel),
            new PropertyMetadata(4d, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var widthLimit = double.IsInfinity(availableSize.Width) ? double.MaxValue : Math.Max(0, availableSize.Width);
        var unconstrained = new Size(double.PositiveInfinity, availableSize.Height);
        double x = 0;
        double y = 0;
        double rowHeight = 0;
        double maxWidth = 0;

        foreach (var child in Children)
        {
            child.Measure(unconstrained);
            var size = child.DesiredSize;
            if (double.IsNaN(size.Width) || double.IsInfinity(size.Width))
                size = new Size(0, size.Height);
            if (double.IsNaN(size.Height) || double.IsInfinity(size.Height))
                size = new Size(size.Width, 0);
            if (x > 0 && x + size.Width > widthLimit)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }

            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
            maxWidth = Math.Max(maxWidth, x - HorizontalSpacing);
        }

        var width = double.IsInfinity(availableSize.Width) ? maxWidth : Math.Min(maxWidth, widthLimit);
        return new Size(width, y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        double rowHeight = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            var width = Math.Min(size.Width, Math.Max(0, finalSize.Width));
            if (x > 0 && x + width > finalSize.Width)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, width, size.Height));
            x += width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return finalSize;
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is ChipWrapPanel panel)
            panel.InvalidateMeasure();
    }
}
