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

    public static string RunningLabel(HanhuaJob job, IReadOnlyList<HanhuaPhase>? phases = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Status == HanhuaJobStatus.Cancelling) return "正在取消";
        phases ??= HanhuaCommand.Phases(job.Kind);
        var index = IndexOf(phases, job.Phase);
        var verb = $"正在{PhaseLabel(job.Phase)}";
        return index < 0 ? verb : $"第 {index + 1}/{phases.Count} 步 {verb}";
    }

    public static HanhuaJob BeginPhase(HanhuaJob job, HanhuaPhase phase)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Phase == phase)
            return job with
            {
                Status = HanhuaJobStatus.Running,
                Error = null,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        return job with
        {
            Phase = phase,
            Done = 0,
            Total = 0,
            Status = HanhuaJobStatus.Running,
            Error = null,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }

    public static (int Done, int Total) MergePhaseCounters(
        int currentDone,
        int currentTotal,
        int incomingDone,
        int incomingTotal)
    {
        if (incomingTotal <= 0)
            return (Math.Max(0, currentDone), Math.Max(0, currentTotal));
        if (currentTotal > incomingTotal)
        {
            var done = currentTotal - incomingTotal + Math.Clamp(incomingDone, 0, incomingTotal);
            return (Math.Clamp(done, 0, currentTotal), currentTotal);
        }
        return (Math.Clamp(incomingDone, 0, incomingTotal), incomingTotal);
    }

    public static double? OverallFraction(HanhuaJob job, IReadOnlyList<HanhuaPhase>? phases = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Status == HanhuaJobStatus.Succeeded) return 1;
        phases ??= HanhuaCommand.Phases(job.Kind);
        if (phases.Count == 0) return null;
        var index = IndexOf(phases, job.Phase);
        if (index < 0) return null;
        var part = job.Total > 0
            ? Math.Clamp(job.Done, 0, job.Total) / (double)job.Total
            : 0;
        return (index + part) / phases.Count;
    }

    public static string StartBanner(HanhuaKind kind, bool unity = false)
        => kind == HanhuaKind.Image
            ? "漫画图片分三步，会自动连续跑完：抽字 → 填字 → 嵌字。不用点右上角启动。"
            : unity
                ? "Unity 会先装插件，再预填已经玩到的句子。没有新句子就去玩游戏，再点开始汉化。不用点右上角启动。"
                : "游戏文本会自动：复制 → 抽字 → 翻译 → 回写。本机 Qwen 会自动启动。";

    public static string EmptyHint(HanhuaKind kind)
        => kind == HanhuaKind.Image
            ? "选含 png 的目录，点开始汉化即可。会自动抽字、填字、嵌字。不用点右上角启动。"
            : "选 RPG Maker（Game.exe）或 Unity 游戏目录，点开始汉化即可。不用点右上角启动。";

    public static string ComposerHint(HanhuaKind kind)
        => kind == HanhuaKind.Image
            ? "自动三步：抽字 → 填字 → 嵌字。完成后可点补翻译，只补未译句子和缺页。"
            : "RPG Maker：复制 → 抽字 → 翻译 → 回写。Unity：装插件 → 预填已抽出的句子。本机模型会自动启动。";

    public static string DuplicateStartPrompt
        => "这个目录已经汉化过。接着上次只补未译对白和缺页；全新会整本重抽。";

    public static HanhuaLiveProgress FromJob(
        HanhuaJob job,
        TimeSpan elapsed,
        IReadOnlyList<HanhuaPhase>? phases = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        phases ??= HanhuaCommand.Phases(job.Kind);
        var observation = new VideoProgressObservation(
            elapsed,
            MapStatus(job.Status),
            CompletedUnits: job.Total > 0 ? Math.Max(0, job.Done) : null,
            TotalUnits: job.Total > 0 ? job.Total : null,
            PhaseLabel: PhaseLabel(job.Phase),
            UnitLabel: UnitLabel(job.Phase));
        var estimate = VideoProgressEstimator.Estimate(observation);
        var running = job.Status is HanhuaJobStatus.Running or HanhuaJobStatus.Cancelling or HanhuaJobStatus.Queued;
        if (running && OverallFraction(job, phases) is double overall)
        {
            var countable = estimate.Source is VideoProgressSource.Units or VideoProgressSource.Steps;
            estimate = estimate with
            {
                Fraction = overall,
                Remaining = VideoProgressEstimator.EstimateRemaining(elapsed, overall),
                Source = countable ? estimate.Source : VideoProgressSource.Units,
            };
        }
        var label = RunningLabel(job, phases);
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
