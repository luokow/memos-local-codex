namespace QwenLocalChat.Core;

public sealed record VideoQueuedJob(
    string SessionId,
    string Prompt,
    VideoGenerationSettings Settings,
    VideoMediaInputs Media,
    bool ResumeRequested = false);

/// <summary>
/// FIFO waiting list for video windows. Only one job runs; others wait here.
/// Re-submitting the same window updates the payload and keeps its place.
/// </summary>
public sealed class VideoJobQueue
{
    private readonly SessionJobQueue<VideoQueuedJob> _inner = new(job => job.SessionId);

    public IReadOnlyList<VideoQueuedJob> Items => _inner.Items;
    public int Count => _inner.Count;

    public int Enqueue(VideoQueuedJob job) => _inner.Enqueue(job);
    public VideoQueuedJob? Dequeue() => _inner.Dequeue();
    public bool Remove(string sessionId) => _inner.Remove(sessionId);
    public int? Position(string sessionId) => _inner.Position(sessionId);
    public void ReplaceAll(IEnumerable<VideoQueuedJob> jobs) => _inner.ReplaceAll(jobs);
    public static string FormatStatus(int position) => SessionJobQueue<VideoQueuedJob>.FormatStatus(position);
}
