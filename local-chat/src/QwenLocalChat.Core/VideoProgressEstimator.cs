namespace QwenLocalChat.Core;

/// <summary>
/// How a progress fraction was derived. New local models can add sources without
/// changing UI formatting; the estimator picks the highest-confidence available signal.
/// </summary>
public enum VideoProgressSource
{
    /// <summary>No usable progress yet (queued / encoding / unknown).</summary>
    None = 0,

    /// <summary>Coarse phase only (e.g. pending vs in_progress).</summary>
    Phase = 1,

    /// <summary>Sampler/checkpoint steps: completed / total.</summary>
    Steps = 2,

    /// <summary>Adapter-reported unit counters (frames, tiles, chunks, …).</summary>
    Units = 3,

    /// <summary>Backend job progress field (0–1 or 0–100).</summary>
    BackendFraction = 4,
}

/// <summary>
/// Multi-source observation for one poll tick. Adapters fill whatever they know;
/// empty fields are ignored. Future models can set Units* or BackendFraction without
/// depending on sampler steps.
/// </summary>
public sealed record VideoProgressObservation(
    TimeSpan Elapsed,
    string Status,
    double? BackendProgress = null,
    int? CompletedSteps = null,
    int? TotalSteps = null,
    int? CompletedUnits = null,
    int? TotalUnits = null,
    string? PhaseLabel = null,
    string? UnitLabel = null)
{
    public static VideoProgressObservation FromJob(
        VideoJob job,
        TimeSpan elapsed,
        int? completedSteps = null,
        int? totalSteps = null,
        int? completedUnits = null,
        int? totalUnits = null,
        string? phaseLabel = null,
        string? unitLabel = null)
    {
        if (completedSteps is null && totalSteps is > 0 && job.Progress is double raw && raw > 0)
            completedSteps = (int)Math.Round(VideoProgressEstimator.NormalizeBackendProgress(raw) * totalSteps.Value);
        return new(
            elapsed,
            job.Status,
            job.Progress,
            completedSteps,
            totalSteps,
            completedUnits ?? (job.OutputsCount > 0 ? job.OutputsCount : null),
            totalUnits,
            phaseLabel,
            unitLabel);
    }
}

/// <summary>Resolved progress for UI (status text + determinate progress bar).</summary>
public sealed record VideoProgressEstimate(
    double? Fraction,
    TimeSpan? Remaining,
    int? Completed,
    int? Total,
    string? CounterLabel,
    VideoProgressSource Source,
    string? Detail = null)
{
    public bool HasDeterminateProgress => Fraction is >= 0 and <= 1;
}

/// <summary>
/// Pure merge of heterogeneous progress signals. Priority (highest first):
/// BackendFraction → Units → Steps → Phase.
/// Remaining time uses linear extrapolation once a positive fraction and elapsed exist.
/// </summary>
public static class VideoProgressEstimator
{
    public static readonly TimeSpan MinimumElapsedForEta = TimeSpan.FromSeconds(8);
    public const double MinimumFractionForEta = 0.02;

    public static VideoProgressEstimate Estimate(VideoProgressObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (IsTerminalSuccess(observation.Status))
        {
            return new VideoProgressEstimate(
                Fraction: 1,
                Remaining: TimeSpan.Zero,
                Completed: observation.TotalSteps ?? observation.TotalUnits ?? observation.CompletedSteps ?? observation.CompletedUnits,
                Total: observation.TotalSteps ?? observation.TotalUnits,
                CounterLabel: observation.UnitLabel ?? (observation.TotalSteps is not null ? "步" : null),
                Source: VideoProgressSource.BackendFraction,
                Detail: "完成");
        }

        if (TryBackendFraction(observation.BackendProgress, out var backendFraction))
        {
            return Build(
                backendFraction,
                observation,
                completed: observation.CompletedSteps,
                total: observation.TotalSteps,
                counterLabel: observation.TotalSteps is not null ? "步" : observation.UnitLabel,
                VideoProgressSource.BackendFraction,
                detail: null);
        }

        if (TryRatio(observation.CompletedUnits, observation.TotalUnits, out var unitFraction))
        {
            return Build(
                unitFraction,
                observation,
                observation.CompletedUnits,
                observation.TotalUnits,
                observation.UnitLabel ?? "单元",
                VideoProgressSource.Units,
                detail: null);
        }

        if (TryRatio(observation.CompletedSteps, observation.TotalSteps, out var stepFraction))
        {
            return Build(
                stepFraction,
                observation,
                observation.CompletedSteps,
                observation.TotalSteps,
                "步",
                VideoProgressSource.Steps,
                detail: null);
        }

        // Phase-only: we know the job is alive, but not how far. Do not invent 5%.
        var phase = observation.PhaseLabel ?? PhaseFromStatus(observation.Status);
        return new VideoProgressEstimate(
            Fraction: null,
            Remaining: null,
            Completed: observation.CompletedSteps ?? observation.CompletedUnits,
            Total: observation.TotalSteps ?? observation.TotalUnits,
            CounterLabel: observation.TotalSteps is not null ? "步"
                : observation.TotalUnits is not null ? (observation.UnitLabel ?? "单元")
                : null,
            Source: observation.Status is "pending" or "queued" or "in_progress"
                ? VideoProgressSource.Phase
                : VideoProgressSource.None,
            Detail: phase);
    }

    /// <summary>
    /// Status line fragment after the status label, e.g.
    /// "已用时 00:05:12  剩余约 00:08:30  8/21 步  38%".
    /// </summary>
    public static string FormatStatusSuffix(VideoProgressEstimate estimate, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var parts = new List<string>
        {
            $"已用时 {FormatClock(elapsed)}",
        };

        if (estimate.Remaining is { } remaining && remaining > TimeSpan.Zero)
            parts.Add($"剩余约 {FormatClock(remaining)}");
        else if (estimate.Remaining == TimeSpan.Zero && estimate.Fraction >= 1)
            parts.Add("剩余约 00:00:00");

        if (estimate.Completed is int completed && estimate.Total is int total && total > 0)
        {
            var unit = string.IsNullOrWhiteSpace(estimate.CounterLabel) ? "" : $" {estimate.CounterLabel}";
            parts.Add($"{completed}/{total}{unit}");
        }
        else if (estimate.Total is int planned && planned > 0 && estimate.Source is VideoProgressSource.Steps or VideoProgressSource.Units)
        {
            var unit = string.IsNullOrWhiteSpace(estimate.CounterLabel) ? "" : $" {estimate.CounterLabel}";
            parts.Add($"0/{planned}{unit}");
        }

        return string.Join("  ", parts);
    }

    public static string FormatLiveStatus(string statusLabel, VideoProgressEstimate estimate, TimeSpan elapsed)
        => $"{statusLabel}  {FormatStatusSuffix(estimate, elapsed)}";

    public static string FormatPercent(VideoProgressEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        if (estimate.Source is VideoProgressSource.Phase || estimate.Fraction is null)
            return estimate.Source is VideoProgressSource.None ? "0%" : "";
        var fraction = Math.Clamp(estimate.Fraction.Value, 0, 1);
        return $"{fraction * 100:0}%";
    }

    public static double NormalizeBackendProgress(double raw)
    {
        // Accept 0–1 fractions and 0–100 percentages from heterogeneous backends.
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < 0)
            return 0;
        if (raw <= 1.0)
            return Math.Clamp(raw, 0, 1);
        if (raw <= 100.0)
            return Math.Clamp(raw / 100.0, 0, 1);
        return 1;
    }

    public static TimeSpan? EstimateRemaining(TimeSpan elapsed, double fraction)
    {
        if (fraction >= 1) return TimeSpan.Zero;
        if (fraction < MinimumFractionForEta) return null;
        if (elapsed < MinimumElapsedForEta) return null;
        var remainingSeconds = elapsed.TotalSeconds * (1.0 - fraction) / fraction;
        if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds) || remainingSeconds < 0)
            return null;
        // Cap absurd extrapolations (cold start / stalled first step).
        remainingSeconds = Math.Min(remainingSeconds, TimeSpan.FromDays(1).TotalSeconds);
        return TimeSpan.FromSeconds(remainingSeconds);
    }

    private static VideoProgressEstimate Build(
        double fraction,
        VideoProgressObservation observation,
        int? completed,
        int? total,
        string? counterLabel,
        VideoProgressSource source,
        string? detail)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        return new VideoProgressEstimate(
            Fraction: fraction,
            Remaining: EstimateRemaining(observation.Elapsed, fraction),
            Completed: completed,
            Total: total,
            CounterLabel: counterLabel,
            Source: source,
            Detail: detail);
    }

    private static bool TryBackendFraction(double? raw, out double fraction)
    {
        fraction = 0;
        if (raw is null) return false;
        fraction = NormalizeBackendProgress(raw.Value);
        // Treat exact 0 as "unknown" so we can still use steps while waiting for the first tick.
        return fraction > 0;
    }

    private static bool TryRatio(int? completed, int? total, out double fraction)
    {
        fraction = 0;
        if (completed is null || total is null || total <= 0) return false;
        var done = Math.Clamp(completed.Value, 0, total.Value);
        fraction = (double)done / total.Value;
        // completed==0 is still a valid ratio for counters ("0/21 步").
        return true;
    }

    private static bool IsTerminalSuccess(string status)
        => status is "completed" or "success";

    private static string? PhaseFromStatus(string status) => status switch
    {
        "pending" or "queued" => "排队中",
        "in_progress" => "生成中",
        "failed" or "error" => "失败",
        "cancelled" => "已取消",
        _ => null,
    };

    private static string FormatClock(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        // Always hh:mm:ss so elapsed / remaining line up in the status strip.
        return value.ToString(@"hh\:mm\:ss");
    }
}
