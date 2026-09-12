namespace QwenLocalChat.Core;

/// <summary>Short status-bar copy versus the full backend dump kept for details.</summary>
public static class HanhuaErrorPresentation
{
    public static string Summarize(string? error, HanhuaPhase phase)
    {
        if (string.IsNullOrWhiteSpace(error))
            return $"{HanhuaProgressStatus.PhaseLabel(phase)}失败。";

        var text = error.Trim();
        if (LooksLikeMitQuit(text))
            return $"{HanhuaProgressStatus.PhaseLabel(phase)}失败：图片工具提前退出，没有处理完全部页。";
        if (VideoErrorPresentation.LooksLikeOutOfMemory(text))
            return "显存不足，汉化失败。";
        if (ContainsAny(text, "no png", "没有 png", "没有识别到文字"))
            return "这个目录里没有可用的对白图片。";
        if (ContainsAny(text, "请先启动文本模型", "not listening", "未就绪", "没能自动启动"))
            return "本机 Qwen 没能自动启动，填字无法继续。";
        if (ContainsAny(text, "仍在占用", "still on 18135"))
            return "抽字需要显卡，本机 Qwen 还没停干净。";
        if (text.StartsWith("mit_exit=", StringComparison.OrdinalIgnoreCase)
            || text.Contains("脚本退出码", StringComparison.Ordinal))
            return $"{HanhuaProgressStatus.PhaseLabel(phase)}失败：图片工具异常退出。";

        var first = FirstLine(text);
        return first.Length <= 80 ? first : first[..80].TrimEnd() + "…";
    }

    public static string FormatFailed(string summary)
        => $"{summary} 不用点右上角启动。点开始汉化可重试。";

    public static string DisplayMessage(
        HanhuaKind kind,
        HanhuaPhase phase,
        HanhuaJobStatus status,
        string raw,
        int done = 0,
        int total = 0)
        => status switch
        {
            HanhuaJobStatus.Succeeded => kind == HanhuaKind.Image
                ? "汉化完成。可点打开结果看嵌好字的图片。"
                : done <= 0
                    ? "插件已就绪。请完全退出游戏，点打开结果启动（不要直接开 exe）。Local AI 不会给正在开着的游戏改字。新对白要先玩到，再点开始汉化预填。"
                    : "汉化完成。可点打开结果启动游戏。Unity 玩过新场景后再点开始汉化填新句子。",
            HanhuaJobStatus.Interrupted => "已取消。已完成的部分还在，点开始汉化可从当前步继续。",
            HanhuaJobStatus.Failed => FormatFailed(Summarize(raw, phase)),
            _ => raw,
        };

    public static bool LooksLikeMitQuit(string error)
    {
        var text = error.ToLowerInvariant();
        return text.Contains("mit_exit=4294967295", StringComparison.Ordinal)
            || text.Contains("mit_exit=-1", StringComparison.Ordinal)
            || text.Contains("don't continue if --save-text", StringComparison.Ordinal)
            || text.Contains("提前退出", StringComparison.Ordinal);
    }

    private static bool ContainsAny(string text, params string[] parts)
        => parts.Any(part => text.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static string FirstLine(string error)
    {
        var span = error.AsSpan().Trim();
        var breakAt = span.IndexOfAny('\r', '\n');
        return (breakAt < 0 ? span : span[..breakAt]).Trim().ToString();
    }
}
