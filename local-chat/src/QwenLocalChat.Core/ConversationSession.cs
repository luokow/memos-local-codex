namespace QwenLocalChat.Core;

/// <summary>How the multi-session picker orders threads.</summary>
public enum SessionSortMode
{
    /// <summary>Newest CreatedAt first — stable while chatting.</summary>
    CreatedDesc = 0,
    /// <summary>Newest UpdatedAt first — recently active threads rise to the top.</summary>
    UpdatedDesc = 1,
}

/// <summary>
/// One in-app chat thread: model history + display transcript + MemOS session key.
/// </summary>
public sealed class ConversationSession
{
    public required string Id { get; init; }
    public string Title { get; set; } = "新会话";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string MemosSessionId { get; init; } = $"qwen-local-chat-{Guid.NewGuid():N}";
    public List<ChatMessage> History { get; set; } = [];
    public List<ConversationTurn> Transcript { get; set; } = [];
    public string? LastFinishReason { get; set; }
    /// <summary>Unsent composer text preserved when switching away from this session.</summary>
    public string DraftInput { get; set; } = string.Empty;
    public List<string> DraftAttachmentPaths { get; set; } = [];
    /// <summary>
    /// In-progress segmented long-form job (约 N 字 → multi-turn). Not required for short chats.
    /// </summary>
    public LongFormPlan? LongFormPlan { get; set; }

    public static ConversationSession Create(string? title = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        return new ConversationSession
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? "新会话" : title.Trim(),
            MemosSessionId = $"qwen-local-chat-{id}",
        };
    }

    public static string SuggestTitle(IReadOnlyList<ChatMessage> history, int maxChars = 18)
    {
        var firstUser = history.FirstOrDefault(m => m.Role == "user")?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(firstUser)) return "新会话";
        var oneLine = firstUser.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        while (oneLine.Contains("  ", StringComparison.Ordinal))
            oneLine = oneLine.Replace("  ", " ", StringComparison.Ordinal);
        if (oneLine.Length <= maxChars) return oneLine;
        return oneLine[..maxChars].TrimEnd() + "…";
    }

    public void Capture(
        IReadOnlyList<ChatMessage> history,
        IEnumerable<(string Label, string Text)> transcript,
        string? lastFinishReason,
        string? draftInput = null,
        IReadOnlyList<string>? draftAttachmentPaths = null)
    {
        History = history.Select(m => new ChatMessage(m.Role, m.Content)).ToList();
        Transcript = ConversationExport.FromTranscript(transcript).ToList();
        LastFinishReason = lastFinishReason;
        if (draftInput is not null)
            DraftInput = draftInput;
        if (draftAttachmentPaths is not null)
            DraftAttachmentPaths = draftAttachmentPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        UpdatedAt = DateTimeOffset.Now;
        if (Title is "新会话" or "")
            Title = SuggestTitle(History);
    }
}

/// <summary>
/// Multi-session workspace for the desktop chat window (in-memory; persisted via ConversationSessionStore).
/// </summary>
public sealed class ConversationSessionWorkspace
{
    private readonly List<ConversationSession> _sessions = [];
    private string _activeId = string.Empty;
    private SessionSortMode _sortMode = SessionSortMode.CreatedDesc;

    public IReadOnlyList<ConversationSession> Sessions => _sessions;

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

    public ConversationSession Active =>
        _sessions.FirstOrDefault(s => s.Id == _activeId)
        ?? throw new InvalidOperationException("No active conversation session.");

    public ConversationSession EnsureBootstrap()
    {
        if (_sessions.Count == 0)
        {
            var first = ConversationSession.Create();
            _sessions.Add(first);
            _activeId = first.Id;
        }

        return Active;
    }

    /// <summary>
    /// Replace all sessions from a disk snapshot. Caller must pass at least one session.
    /// </summary>
    public void ReplaceAll(IReadOnlyList<ConversationSession> sessions, string activeId)
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

    public ConversationSession CreateAndActivate(Action<ConversationSession> captureActive)
    {
        EnsureBootstrap();
        captureActive(Active);
        var session = ConversationSession.Create();
        _sessions.Insert(0, session);
        _activeId = session.Id;
        ApplySort();
        return session;
    }

    public ConversationSession? Find(string id)
        => _sessions.FirstOrDefault(s => s.Id == id);

    public bool TryActivate(string id, Action<ConversationSession> captureActive, out ConversationSession session)
    {
        session = _sessions.FirstOrDefault(s => s.Id == id)!;
        if (session is null) return false;
        if (session.Id == _activeId) return true;
        captureActive(Active);
        _activeId = session.Id;
        return true;
    }

    public bool TryDeleteActive(Action<ConversationSession> captureActive, out ConversationSession nextActive)
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

    public void CaptureActive(
        IReadOnlyList<ChatMessage> history,
        IEnumerable<(string Label, string Text)> transcript,
        string? lastFinishReason)
    {
        if (_sessions.Count == 0) return;
        Active.Capture(history, transcript, lastFinishReason);
        ApplySort();
    }

    /// <summary>Re-order the list after a session's UpdatedAt/CreatedAt may have changed.</summary>
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
