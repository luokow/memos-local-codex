namespace QwenLocalChat.Core;

/// <summary>
/// FIFO waiting list keyed by session/window id. Re-submitting the same id
/// updates the payload and keeps its place.
/// </summary>
public sealed class SessionJobQueue<T>
{
    private readonly List<T> _items = [];
    private readonly Func<T, string> _sessionId;

    public SessionJobQueue(Func<T, string> sessionId)
        => _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));

    public IReadOnlyList<T> Items => _items;
    public int Count => _items.Count;

    public int Enqueue(T job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var id = _sessionId(job);
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Queued job must belong to a session.", nameof(job));

        var existing = _items.FindIndex(item => _sessionId(item) == id);
        if (existing >= 0)
        {
            _items[existing] = job;
            return existing + 1;
        }

        _items.Add(job);
        return _items.Count;
    }

    public T? Dequeue()
    {
        if (_items.Count == 0) return default;
        var next = _items[0];
        _items.RemoveAt(0);
        return next;
    }

    public bool Remove(string sessionId)
    {
        var index = _items.FindIndex(item => _sessionId(item) == sessionId);
        if (index < 0) return false;
        _items.RemoveAt(index);
        return true;
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Clear();
        foreach (var item in items)
            Enqueue(item);
    }

    public int? Position(string sessionId)
    {
        var index = _items.FindIndex(item => _sessionId(item) == sessionId);
        return index < 0 ? null : index + 1;
    }

    public static string FormatStatus(int position)
        => position <= 1
            ? "排队中，当前任务结束后开始。"
            : $"排队中，前面还有 {position - 1} 个任务。";
}

public sealed record ChatQueuedTurn(
    string SessionId,
    string UserMessage,
    string VisibleUserText,
    string SubmittedInput,
    bool IsContinuation,
    bool IsSegmentTurn,
    bool UseMemos,
    bool SaveLog,
    string MemosSessionId,
    IReadOnlyList<ChatMessage> RequestHistory,
    int RequestedMaxOutputTokens,
    bool RestoreInputOnCancel);
