namespace QwenLocalChat.Core;

/// <summary>
/// Footer status for a hanhua job. Reuses the video elapsed/ETA formatter so the
/// two long-running workspaces read the same clock.
/// </summary>
public sealed record HanhuaLiveProgress(
    string StatusText,
    string PercentText,
    double? Fraction,
    bool Determinate);

public static class HanhuaProgressStatus
{
    public static string PhaseLabel(HanhuaPhase phase) => phase switch
    {
        HanhuaPhase.Copy => "复制",
        HanhuaPhase.Extract => "抽字",
        HanhuaPhase.Translate => "翻译",
        HanhuaPhase.Inject => "回写",
        HanhuaPhase.Ocr => "抽字",
        HanhuaPhase.Fill => "填字",
        HanhuaPhase.Typeset => "嵌字",
        _ => phase.ToString(),
    };

    public static string UnitLabel(HanhuaPhase phase) => phase switch
    {
        HanhuaPhase.Ocr or HanhuaPhase.Typeset => "页",
        HanhuaPhase.Copy => "项",
        _ => "句",
    };

    public static string RunningLabel(HanhuaJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Status == HanhuaJobStatus.Cancelling) return "正在取消";
        var phases = HanhuaCommand.Phases(job.Kind);
        var index = IndexOf(phases, job.Phase);
        var verb = $"正在{PhaseLabel(job.Phase)}";
        return index < 0 ? verb : $"第 {index + 1}/{phases.Count} 步 {verb}";
    }

    public static string StartBanner(HanhuaKind kind)
        => kind == HanhuaKind.Image
            ? "漫画图片分三步，会自动连续跑完：抽字 → 填字 → 嵌字。不用点右上角启动。"
            : "游戏文本会自动：复制 → 抽字 → 翻译 → 回写。本机 Qwen 会自动启动。";

    public static string EmptyHint(HanhuaKind kind)
        => kind == HanhuaKind.Image
            ? "选含 png 的目录，点开始汉化即可。会自动抽字、填字、嵌字。不用点右上角启动。"
            : "选含 Game.exe 的目录，点开始汉化即可。会自动复制、抽字、翻译、回写。不用点右上角启动。";

    public static string ComposerHint(HanhuaKind kind)
        => kind == HanhuaKind.Image
            ? "自动三步：抽字 → 填字 → 嵌字。不用点右上角启动。"
            : "自动四步：复制 → 抽字 → 翻译 → 回写。本机 Qwen 会自动启动。";

    public static HanhuaLiveProgress FromJob(HanhuaJob job, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(job);
        var observation = new VideoProgressObservation(
            elapsed,
            MapStatus(job.Status),
            CompletedUnits: job.Total > 0 ? Math.Max(0, job.Done) : null,
            TotalUnits: job.Total > 0 ? job.Total : null,
            PhaseLabel: PhaseLabel(job.Phase),
            UnitLabel: UnitLabel(job.Phase));
        var estimate = VideoProgressEstimator.Estimate(observation);
        var label = RunningLabel(job);
        var determinate = estimate.HasDeterminateProgress
            && estimate.Source is not VideoProgressSource.Phase
            && (estimate.Fraction ?? 0) > 0;
        return new HanhuaLiveProgress(
            VideoProgressEstimator.FormatLiveStatus(label, estimate, elapsed),
            VideoProgressEstimator.FormatPercent(estimate),
            estimate.Fraction,
            determinate);
    }

    public static string FormatFinished(string message, TimeSpan elapsed)
    {
        var text = string.IsNullOrWhiteSpace(message)
            ? "汉化结束"
            : message.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return $"{text}  已用时 {elapsed.ToString(@"hh\:mm\:ss")}";
    }

    private static int IndexOf(IReadOnlyList<HanhuaPhase> phases, HanhuaPhase phase)
    {
        for (var i = 0; i < phases.Count; i++)
            if (phases[i] == phase) return i;
        return -1;
    }

    private static string MapStatus(HanhuaJobStatus status) => status switch
    {
        HanhuaJobStatus.Succeeded => "completed",
        HanhuaJobStatus.Failed => "failed",
        HanhuaJobStatus.Interrupted or HanhuaJobStatus.Cancelling => "cancelled",
        HanhuaJobStatus.Queued => "queued",
        _ => "in_progress",
    };
}
