namespace QwenLocalChat.Core;

public sealed record VideoSessionSnapshot(
    string DraftPrompt,
    string ConditioningMode,
    string? FirstFramePath,
    string? LastFramePath,
    string? ReferenceImagePath,
    VideoGenerationOverrides? JobOverrides,
    string? LastOutputPath,
    string? LastStatus,
    string? LastError,
    string? LastPrompt,
    IReadOnlyList<string>? ReferenceImagePaths = null,
    IReadOnlyList<string>? ReferenceVideoPaths = null,
    IReadOnlyList<string>? ReferenceAudioPaths = null,
    string? DraftTemplate = null,
    string? DraftTemplateTitle = null,
    string? ExtraAction = null,
    string? ExtraSound = null,
    string? ExtraMusic = null,
    string? ExtraIdentity = null,
    string? AssembledOverride = null);

/// <summary>
/// One video window: prompt/media draft, last clip, and job overrides.
/// </summary>
public sealed class VideoSession
{
    public const string EmptyTitle = "新会话";

    public string Id { get; init; } = "";
    public string Title { get; set; } = EmptyTitle;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string DraftPrompt { get; set; } = string.Empty;
    public string ConditioningMode { get; set; } = VideoMediaCapabilities.ToId(VideoConditioningMode.Text);
    public string? FirstFramePath { get; set; }
    public string? LastFramePath { get; set; }
    public string? ReferenceImagePath { get; set; }
    public List<string> ReferenceImagePaths { get; set; } = [];
    public List<string> ReferenceVideoPaths { get; set; } = [];
    public List<string> ReferenceAudioPaths { get; set; } = [];
    public VideoGenerationOverrides? JobOverrides { get; set; }
    public string? LastOutputPath { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public string? LastPrompt { get; set; }
    public string? DraftTemplate { get; set; }
    public string? DraftTemplateTitle { get; set; }
    public string ExtraAction { get; set; } = "";
    public string ExtraSound { get; set; } = "";
    public string ExtraMusic { get; set; } = "";
    public string ExtraIdentity { get; set; } = "";
    public string? AssembledOverride { get; set; }

    public static VideoSession Create(string? title = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        return new VideoSession
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? EmptyTitle : title.Trim(),
        };
    }

    public static string SuggestTitle(string? prompt, int maxChars = 18)
    {
        var first = prompt?.Trim();
        if (string.IsNullOrWhiteSpace(first)) return EmptyTitle;
        var oneLine = first.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        while (oneLine.Contains("  ", StringComparison.Ordinal))
            oneLine = oneLine.Replace("  ", " ", StringComparison.Ordinal);
        if (oneLine.Length <= maxChars) return oneLine;
        return oneLine[..maxChars].TrimEnd() + "…";
    }

    public void Capture(VideoSessionSnapshot snapshot)
    {
        DraftPrompt = snapshot.DraftPrompt ?? string.Empty;
        ConditioningMode = string.IsNullOrWhiteSpace(snapshot.ConditioningMode)
            ? VideoMediaCapabilities.ToId(VideoConditioningMode.Text)
            : snapshot.ConditioningMode.Trim();
        FirstFramePath = EmptyToNull(snapshot.FirstFramePath);
        LastFramePath = EmptyToNull(snapshot.LastFramePath);
        ReferenceImagePaths = NormalizePaths(snapshot.ReferenceImagePaths, snapshot.ReferenceImagePath);
        ReferenceImagePath = ReferenceImagePaths.FirstOrDefault();
        ReferenceVideoPaths = NormalizePaths(snapshot.ReferenceVideoPaths, null);
        ReferenceAudioPaths = NormalizePaths(snapshot.ReferenceAudioPaths, null);
        JobOverrides = snapshot.JobOverrides;
        LastOutputPath = EmptyToNull(snapshot.LastOutputPath);
        LastStatus = EmptyToNull(snapshot.LastStatus);
        LastError = EmptyToNull(snapshot.LastError);
        LastPrompt = EmptyToNull(snapshot.LastPrompt);
        DraftTemplate = EmptyToNull(snapshot.DraftTemplate);
        DraftTemplateTitle = EmptyToNull(snapshot.DraftTemplateTitle);
        ExtraAction = snapshot.ExtraAction ?? "";
        ExtraSound = snapshot.ExtraSound ?? "";
        ExtraMusic = snapshot.ExtraMusic ?? "";
        ExtraIdentity = snapshot.ExtraIdentity ?? "";
        AssembledOverride = EmptyToNull(snapshot.AssembledOverride);
        UpdatedAt = DateTimeOffset.Now;
        if (Title is EmptyTitle or "")
            Title = SuggestTitle(LastPrompt ?? DraftPrompt);
    }

    public void ClearWindow()
    {
        DraftPrompt = string.Empty;
        ConditioningMode = VideoMediaCapabilities.ToId(VideoConditioningMode.Text);
        FirstFramePath = null;
        LastFramePath = null;
        ReferenceImagePath = null;
        ReferenceImagePaths = [];
        ReferenceVideoPaths = [];
        ReferenceAudioPaths = [];
        JobOverrides = null;
        LastOutputPath = null;
        LastStatus = null;
        LastError = null;
        LastPrompt = null;
        DraftTemplate = null;
        DraftTemplateTitle = null;
        ExtraAction = "";
        ExtraSound = "";
        ExtraMusic = "";
        ExtraIdentity = "";
        AssembledOverride = null;
        Title = EmptyTitle;
        UpdatedAt = DateTimeOffset.Now;
    }

    public static List<string> NormalizeStoredPaths(IReadOnlyList<string>? paths, string? fallback)
        => NormalizePaths(paths, fallback);

    private static List<string> NormalizePaths(IReadOnlyList<string>? paths, string? fallback)
    {
        var list = (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .ToList();
        if (list.Count == 0 && !string.IsNullOrWhiteSpace(fallback))
            list.Add(fallback.Trim());
        return list;
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class VideoJobSlotPolicy
{
    public const int DefaultMaxParallelJobs = 1;

    public static bool CanStart(int runningCount, int maxParallel = DefaultMaxParallelJobs)
        => runningCount >= 0 && runningCount < Math.Max(1, maxParallel);

    public static string OccupiedMessage(int runningCount)
        => $"已有 {Math.Max(0, runningCount)} 路视频任务在生成。请等完成或到对应窗口取消。";
}

/// <summary>In-memory video windows; persisted via VideoSessionStore.</summary>
public sealed class VideoSessionWorkspace
{
    private readonly List<VideoSession> _sessions = [];
    private string _activeId = string.Empty;
    private SessionSortMode _sortMode = SessionSortMode.CreatedDesc;

    public IReadOnlyList<VideoSession> Sessions => _sessions;

    public SessionSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (_sortMode == value) return;
            _sortMode = value;
            ApplySort();
        }
    }

    public VideoSession Active =>
        _sessions.FirstOrDefault(s => s.Id == _activeId)
        ?? throw new InvalidOperationException("No active video session.");

    public VideoSession EnsureBootstrap()
    {
        if (_sessions.Count == 0)
        {
            var first = VideoSession.Create();
            _sessions.Add(first);
            _activeId = first.Id;
        }

        return Active;
    }

    public void ReplaceAll(IReadOnlyList<VideoSession> sessions, string activeId)
    {
        if (sessions.Count == 0)
            throw new ArgumentException("At least one session is required.", nameof(sessions));

        _sessions.Clear();
        _sessions.AddRange(sessions);
        ApplySort();
        _activeId = _sessions.Any(s => s.Id == activeId)
            ? activeId
            : _sessions[0].Id;
    }

    public VideoSession CreateAndActivate(Action<VideoSession> captureActive)
    {
        EnsureBootstrap();
        captureActive(Active);
        var session = VideoSession.Create();
        _sessions.Insert(0, session);
        _activeId = session.Id;
        ApplySort();
        return session;
    }

    public VideoSession? Find(string id)
        => _sessions.FirstOrDefault(s => s.Id == id);

    public bool TryActivate(string id, Action<VideoSession> captureActive, out VideoSession session)
    {
        session = _sessions.FirstOrDefault(s => s.Id == id)!;
        if (session is null) return false;
        if (session.Id == _activeId) return true;
        captureActive(Active);
        _activeId = session.Id;
        return true;
    }

    public bool TryDeleteActive(Action<VideoSession> captureActive, out VideoSession nextActive)
    {
        nextActive = Active;
        if (_sessions.Count <= 1) return false;
        captureActive(Active);
        var index = _sessions.FindIndex(s => s.Id == _activeId);
        if (index < 0) return false;
        _sessions.RemoveAt(index);
        var nextIndex = Math.Min(index, _sessions.Count - 1);
        _activeId = _sessions[nextIndex].Id;
        nextActive = Active;
        return true;
    }

    public void ApplySort()
    {
        if (_sessions.Count <= 1) return;
        if (_sortMode == SessionSortMode.UpdatedDesc)
        {
            _sessions.Sort((a, b) =>
            {
                var byUpdated = b.UpdatedAt.CompareTo(a.UpdatedAt);
                if (byUpdated != 0) return byUpdated;
                var byCreated = b.CreatedAt.CompareTo(a.CreatedAt);
                return byCreated != 0 ? byCreated : string.CompareOrdinal(b.Id, a.Id);
            });
        }
        else
        {
            _sessions.Sort((a, b) =>
            {
                var byCreated = b.CreatedAt.CompareTo(a.CreatedAt);
                return byCreated != 0 ? byCreated : string.CompareOrdinal(b.Id, a.Id);
            });
        }
    }
}
