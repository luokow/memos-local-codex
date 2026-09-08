namespace QwenLocalChat.Core;

/// <summary>
/// Multi-signal liveness for long-running ComfyUI video jobs.
/// Timeout alone is never enough: require backend terminal state, disconnect,
/// orphaned queue disappearance, or several concurrent "no progress" signals.
/// </summary>
public sealed record VideoJobSnapshot(
    string Status,
    double? Progress,
    int OutputsCount,
    string? Error,
    bool PresentInQueue,
    bool HistoryPresent,
    bool ServiceReachable,
    bool QueueAndHistoryObserved = false)
{
    public string Fingerprint =>
        $"{Status}|{Progress?.ToString("R") ?? "-"}|{OutputsCount}|{Error ?? ""}|{PresentInQueue}|{HistoryPresent}";
}

public enum VideoJobLivenessAction
{
    Continue,
    UseSnapshot,
    MarkFailed,
}

public sealed record VideoJobLivenessDecision(
    VideoJobLivenessAction Action,
    string Status,
    string? Error,
    string Reason);

public static class VideoJobLiveness
{
    /// <summary>
    /// Stall window when the job is still listed in ComfyUI queue_running.
    /// H3 on low-VRAM often runs 15–60+ minutes for a few seconds of video with
    /// no jobs-API progress field — 12 minutes was false-killing live KSampler runs.
    /// </summary>
    public static readonly TimeSpan InQueueStuckThreshold = TimeSpan.FromMinutes(90);

    /// <summary>
    /// Stall window when the job is NOT observed in the queue (ambiguous / zombie-adjacent).
    /// Shorter than in-queue so true hang-outs without queue membership still fail.
    /// </summary>
    public static readonly TimeSpan DefaultStuckThreshold = TimeSpan.FromMinutes(25);

    public static TimeSpan ResolveStuckThreshold(VideoJobSnapshot current, TimeSpan? overrideThreshold = null)
    {
        if (overrideThreshold is not null) return overrideThreshold.Value;
        // Still in queue_running/pending: treat as live sampling unless absurdly long.
        if (current.PresentInQueue) return InQueueStuckThreshold;
        return DefaultStuckThreshold;
    }

    public static VideoJobLivenessDecision Evaluate(
        VideoJobSnapshot current,
        TimeSpan unchangedDuration,
        TimeSpan? stuckThreshold = null)
    {
        var threshold = ResolveStuckThreshold(current, stuckThreshold);

        if (!current.ServiceReachable)
        {
            return new(
                VideoJobLivenessAction.MarkFailed,
                "failed",
                "视频服务已断开连接或无响应。请确认 ComfyUI 仍在运行后重试。",
                "service_unreachable");
        }

        if (IsTerminal(current.Status))
            return new(VideoJobLivenessAction.UseSnapshot, current.Status, current.Error, "terminal_status");

        if (!string.IsNullOrWhiteSpace(current.Error)
            && current.Status is "failed" or "error" or "in_progress" or "pending" or "queued")
        {
            // Explicit backend error while still non-terminal is treated as failed.
            if (LooksLikeFatalBackendError(current.Error))
                return new(VideoJobLivenessAction.MarkFailed, "failed", current.Error, "backend_error_message");
        }

        // Job vanished from both queue and history → worker crash / lost task.
        // Only when both endpoints were actually observed (avoid false orphans from partial fixtures).
        if (current.QueueAndHistoryObserved
            && !current.PresentInQueue
            && !current.HistoryPresent
            && current.Status is "in_progress" or "pending" or "queued" or "unknown")
        {
            return new(
                VideoJobLivenessAction.MarkFailed,
                "failed",
                "任务已从 ComfyUI 队列消失且没有历史记录（可能进程崩溃或假运行）。请重试。",
                "orphaned_job");
        }

        // Multi-signal stuck: fingerprint frozen for the resolved window + still non-terminal
        // + empty history. In-queue uses the long window so real KSampler is not cancelled.
        if (unchangedDuration >= threshold
            && !current.HistoryPresent
            && current.Status is "in_progress" or "pending" or "queued"
            && (current.PresentInQueue || current.QueueAndHistoryObserved)
            && (current.QueueAndHistoryObserved || unchangedDuration >= threshold + TimeSpan.FromMinutes(5)))
        {
            return new(
                VideoJobLivenessAction.MarkFailed,
                "failed",
                $"任务超过 {FormatDuration(threshold)} 无任何进度变化，且后端历史仍为空，判定为假运行（常见于 CUDA OOM 后队列未释放）。请降低分辨率/帧数后重试。",
                "stale_no_progress");
        }

        return new(VideoJobLivenessAction.Continue, current.Status, current.Error, "live");
    }

    public static bool IsTerminal(string status)
        => status is "completed" or "failed" or "cancelled";

    public static bool LooksLikeFatalBackendError(string error)
    {
        var text = error.ToLowerInvariant();
        return text.Contains("out of memory", StringComparison.Ordinal)
            || text.Contains("cuda", StringComparison.Ordinal)
            || text.Contains("oom", StringComparison.Ordinal)
            || text.Contains("acceleratorerror", StringComparison.Ordinal)
            || text.Contains("exception", StringComparison.Ordinal);
    }

    private static string FormatDuration(TimeSpan value)
        => value.TotalHours >= 1
            ? $"{(int)value.TotalHours} 小时 {value.Minutes} 分钟"
            : $"{Math.Max(1, (int)Math.Round(value.TotalMinutes))} 分钟";
}
