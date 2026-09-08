namespace QwenLocalChat.Core;

/// <summary>Short status-bar copy versus the full backend dump kept for details.</summary>
public static class VideoErrorPresentation
{
    public static string Summarize(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return string.Empty;
        if (LooksLikeOutOfMemory(error))
            return "显存不足，生成失败。";

        var first = FirstLine(error);
        return first.Length <= 80 ? first : first[..80].TrimEnd() + "…";
    }

    public static string Detail(string? error)
        => string.IsNullOrWhiteSpace(error) ? string.Empty : error.Trim();

    public static bool LooksLikeOutOfMemory(string error)
    {
        var text = error.ToLowerInvariant();
        return text.Contains("out of memory", StringComparison.Ordinal)
            || text.Contains("cudaerrormemoryallocation", StringComparison.Ordinal)
            || (text.Contains("cuda", StringComparison.Ordinal) && text.Contains("oom", StringComparison.Ordinal));
    }

    private static string FirstLine(string error)
    {
        var span = error.AsSpan().Trim();
        var breakAt = span.IndexOfAny('\r', '\n');
        return (breakAt < 0 ? span : span[..breakAt]).Trim().ToString();
    }
}
