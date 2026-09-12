namespace QwenLocalChat.Core;

public static class HanhuaCatchUp
{
    public static readonly HashSet<string> ImageSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp",
    };

    public static bool CanCatchUp(HanhuaJob? job)
    {
        if (job is null || job.IsActive || job.Kind != HanhuaKind.Image)
            return false;
        if (string.IsNullOrWhiteSpace(job.WorkPath) || !Directory.Exists(job.WorkPath))
            return false;
        return File.Exists(Path.Combine(job.WorkPath, "translations.json"));
    }

    public static HanhuaJob? Latest(IEnumerable<HanhuaJob> jobs)
        => jobs
            .Where(CanCatchUp)
            .OrderByDescending(job => job.UpdatedUtc)
            .FirstOrDefault();

    public static bool SameSource(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static bool ShouldPromptInsteadOfFreshStart(
        HanhuaKind kind,
        string sourcePath,
        HanhuaJob? currentJob,
        IEnumerable<HanhuaJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (kind != HanhuaKind.Image || string.IsNullOrWhiteSpace(sourcePath))
            return false;
        if (currentJob is { CanResume: true, Kind: HanhuaKind.Image }
            && SameSource(currentJob.SourcePath, sourcePath))
            return false;
        return Latest(jobs.Where(job => SameSource(job.SourcePath, sourcePath))) is not null;
    }

    public static IReadOnlyList<string> ListImageFiles(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];
        return Directory.EnumerateFiles(folder)
            .Where(path => ImageSuffixes.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool NeedsOcr(string sourcePath, string workPath)
    {
        if (string.IsNullOrWhiteSpace(workPath) || !Directory.Exists(workPath))
            return true;
        var typesetIn = Path.Combine(workPath, "typeset_in");
        var sidecarDirs = new[]
        {
            typesetIn,
            Path.Combine(workPath, "ocr_sidecars"),
            Path.Combine(workPath, "ocr_dump"),
        };
        var sourceImages = ListImageFiles(sourcePath);
        if (sourceImages.Count == 0)
            sourceImages = ListImageFiles(typesetIn);
        foreach (var src in sourceImages)
        {
            var name = Path.GetFileName(src);
            if (!File.Exists(Path.Combine(typesetIn, name)))
                return true;
            var stem = Path.GetFileNameWithoutExtension(src);
            if (!sidecarDirs.Any(dir => File.Exists(Path.Combine(dir, stem + "_ocr.json"))))
                return true;
        }
        return false;
    }

    public static IReadOnlyList<HanhuaPhase> Phases(string sourcePath, string workPath)
    {
        var phases = new List<HanhuaPhase>(3);
        if (NeedsOcr(sourcePath, workPath))
            phases.Add(HanhuaPhase.Ocr);
        phases.Add(HanhuaPhase.Fill);
        phases.Add(HanhuaPhase.Typeset);
        return phases;
    }

    public static HanhuaJob Begin(HanhuaJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var start = Phases(job.SourcePath, job.WorkPath ?? "").First();
        return job with
        {
            Status = HanhuaJobStatus.Running,
            Phase = start,
            Error = null,
            Message = "补翻译",
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }
}
