namespace QwenLocalChat.Core;

public readonly record struct VideoPreviewSize(double Width, double Height);

public static class VideoPreviewLayout
{
    public static VideoPreviewSize Fit(double availableWidth, double availableHeight, double mediaWidth, double mediaHeight)
    {
        if (!IsPositiveFinite(availableWidth) || !IsPositiveFinite(availableHeight) ||
            !IsPositiveFinite(mediaWidth) || !IsPositiveFinite(mediaHeight))
            return new VideoPreviewSize(0, 0);

        var scale = Math.Min(availableWidth / mediaWidth, availableHeight / mediaHeight);
        return new VideoPreviewSize(mediaWidth * scale, mediaHeight * scale);
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}

public static class VideoPreviewInteraction
{
    public static bool ShouldShowControls(bool pointerOver, bool keyboardFocused)
        => pointerOver || keyboardFocused;
}

public static class VideoPreviewHistory
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".mkv",
    };

    public static string? FindLatestOutput(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory)) return null;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };
        string? latestPath = null;
        var latestWriteTime = DateTime.MinValue;
        foreach (var path in Directory.EnumerateFiles(outputDirectory, "*", options))
        {
            if (!SupportedExtensions.Contains(Path.GetExtension(path))) continue;
            try
            {
                var writeTime = File.GetLastWriteTimeUtc(path);
                if (latestPath is null || writeTime > latestWriteTime)
                {
                    latestPath = path;
                    latestWriteTime = writeTime;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return latestPath;
    }

    public static string? ResolveSessionPreview(string? sessionOutputPath, string? outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionOutputPath)) return null;
        return File.Exists(sessionOutputPath) ? sessionOutputPath : null;
    }
}
