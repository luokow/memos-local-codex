namespace QwenLocalChat.Core;

/// <summary>
/// Classifies SYSTEM transcript lines so model-lifecycle chatter is not re-persisted
/// and re-appended on every cold start (which previously duplicated "已复用…" notices).
/// </summary>
public static class SystemNoticePolicy
{
    public static bool IsModelLifecycleNotice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return t.Contains("已复用当前 Qwen", StringComparison.Ordinal)
               || t.Contains("已由本窗口启动", StringComparison.Ordinal)
               || t.StartsWith("正在检查本地 Qwen", StringComparison.Ordinal);
    }

    /// <summary>Whether a transcript row should be written into sessions.json.</summary>
    public static bool ShouldPersist(string label, string text)
    {
        if (!string.Equals(label, "SYSTEM", StringComparison.OrdinalIgnoreCase))
            return true;
        return !IsModelLifecycleNotice(text);
    }
}
