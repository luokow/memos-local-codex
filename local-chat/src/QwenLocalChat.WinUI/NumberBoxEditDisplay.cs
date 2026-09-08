using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace QwenLocalChat_WinUI;

/// <summary>
/// NumberBox inline spinners plus the inner TextBox delete glyph leave about one
/// digit of viewport, so editing 50 shows only 0. Hide that glyph and keep text in view.
/// </summary>
internal static class NumberBoxEditDisplay
{
    public static void AttachTree(DependencyObject root)
    {
        foreach (var box in EnumerateDescendants<NumberBox>(root))
            Attach(box);
    }

    public static void Attach(NumberBox box)
    {
        box.Loaded -= Box_Loaded;
        box.Loaded += Box_Loaded;
        box.GotFocus -= Box_GotFocus;
        box.GotFocus += Box_GotFocus;
        box.ValueChanged -= Box_ValueChanged;
        box.ValueChanged += Box_ValueChanged;
        box.LayoutUpdated -= Box_LayoutUpdated;
        box.LayoutUpdated += Box_LayoutUpdated;
        if (box.IsLoaded)
            Apply(box);
    }

    private static void Box_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is NumberBox box) Apply(box);
    }

    private static void Box_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is NumberBox box) Apply(box);
    }

    private static void Box_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => Apply(sender);

    private static void Box_LayoutUpdated(object? sender, object e)
    {
        if (sender is NumberBox box)
            HideInnerDeleteButtons(box);
    }

    private static void Apply(NumberBox box)
    {
        var input = FindDescendant<TextBox>(box);
        if (input is null) return;

        input.TextChanged -= Input_TextChanged;
        input.TextChanged += Input_TextChanged;
        input.TextAlignment = TextAlignment.Left;
        HideInnerDeleteButtons(box);
        KeepTextInView(input);
    }

    private static void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox input) return;
        HideInnerDeleteButtons(input);
        KeepTextInView(input);
    }

    private static void HideInnerDeleteButtons(DependencyObject root)
    {
        var input = root as TextBox ?? FindDescendant<TextBox>(root);
        if (input is null) return;
        foreach (var button in EnumerateDescendants<Button>(input))
        {
            button.Visibility = Visibility.Collapsed;
            button.Width = 0;
            button.MinWidth = 0;
            button.Opacity = 0;
            button.Margin = new Thickness(0);
            button.Padding = new Thickness(0);
            button.IsTabStop = false;
            button.IsHitTestVisible = false;
        }
    }

    private static void KeepTextInView(TextBox input)
    {
        var viewer = FindDescendant<ScrollViewer>(input);
        if (viewer is null || viewer.HorizontalOffset <= 0) return;
        var text = input.Text ?? "";
        if (text.Length == 0) return;
        var estimated = text.Length * input.FontSize * 0.7 + input.Padding.Left + input.Padding.Right;
        if (estimated <= viewer.ViewportWidth + 2)
            viewer.ChangeView(0, null, null, disableAnimation: true);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var item in EnumerateDescendants<T>(root))
            return item;
        return null;
    }

    private static IEnumerable<T> EnumerateDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in EnumerateDescendants<T>(child))
                yield return nested;
        }
    }
}
