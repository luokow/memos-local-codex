namespace QwenLocalChat.Core;

public readonly record struct ComposerHeight(double AppliedHeight, bool UsesInternalScroll);

public static class ComposerHeightPolicy
{
    public const double MinimumHeight = 78;
    public const double CompactMaximumHeight = 100;
    public const double MediumMaximumHeight = 144;
    public const double LargeMaximumHeight = 188;

    public static ComposerHeight Resolve(
        double viewportHeight,
        double desiredContentHeight,
        bool auxiliarySectionExpanded = false)
    {
        var maximumHeight = ResolveMaximumHeight(viewportHeight);
        if (auxiliarySectionExpanded)
            maximumHeight = Math.Min(maximumHeight, CompactMaximumHeight);

        var desiredHeight = double.IsFinite(desiredContentHeight)
            ? Math.Max(MinimumHeight, desiredContentHeight)
            : MinimumHeight;
        var appliedHeight = Math.Min(desiredHeight, maximumHeight);
        return new ComposerHeight(appliedHeight, desiredHeight > appliedHeight);
    }

    private static double ResolveMaximumHeight(double viewportHeight)
    {
        if (!double.IsFinite(viewportHeight) || viewportHeight < 720)
            return CompactMaximumHeight;
        if (viewportHeight < 900)
            return MediumMaximumHeight;
        return LargeMaximumHeight;
    }
}
