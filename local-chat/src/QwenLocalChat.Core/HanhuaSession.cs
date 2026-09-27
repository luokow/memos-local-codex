namespace QwenLocalChat.Core;

public sealed record HanhuaSession(
    string Id,
    string Title,
    string SourcePath,
    HanhuaKind Kind,
    HanhuaEngine Engine,
    string? JobId)
{
    public const string DraftTitle = "新任务";

    public static string TitleFor(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return DraftTitle;
        var name = Path.GetFileName(sourcePath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? DraftTitle : name;
    }
}

public sealed record HanhuaQueuedStart(
    string SessionId,
    string SourcePath,
    HanhuaKind Kind,
    HanhuaEngine Engine,
    bool CatchUp);

/// <summary>
/// How many hanhua jobs may run at once. The default matches this machine.
/// A larger configured cap starts the next job instead of queueing it.
/// </summary>
public static class HanhuaSlotPolicy
{
    public const int DefaultMaxParallelJobs = 1;

    public static bool CanStart(int runningCount, int maxParallel = DefaultMaxParallelJobs)
        => runningCount >= 0 && runningCount < Math.Max(1, maxParallel);
}

public sealed class HanhuaSessionWorkspace
{
    private readonly List<HanhuaSession> _sessions = [];
    private string _activeId = "";

    public IReadOnlyList<HanhuaSession> Sessions => _sessions;

    public HanhuaSession Active
        => _sessions.First(session => session.Id == _activeId);

    public static HanhuaSessionWorkspace FromJobs(IReadOnlyList<HanhuaJob> jobs)
    {
        var workspace = new HanhuaSessionWorkspace();
        foreach (var job in jobs)
            workspace._sessions.Add(FromJob(job));
        if (workspace._sessions.Count == 0)
            workspace._sessions.Add(Draft());
        var preferred = jobs.FirstOrDefault(job => job.IsActive)
            ?? jobs.FirstOrDefault(job => job.CanResume)
            ?? jobs.FirstOrDefault();
        workspace._activeId = preferred is null
            ? workspace._sessions[0].Id
            : workspace._sessions.First(session => session.JobId == preferred.Id).Id;
        return workspace;
    }

    public HanhuaSession NewSession()
    {
        var session = Draft();
        _sessions.Insert(0, session);
        _activeId = session.Id;
        return session;
    }

    public bool Delete(string id)
    {
        if (_sessions.Count <= 1) return false;
        var index = _sessions.FindIndex(session => session.Id == id);
        if (index < 0) return false;
        _sessions.RemoveAt(index);
        if (_activeId == id)
            _activeId = _sessions[0].Id;
        return true;
    }

    public void Select(string id)
    {
        if (_sessions.Any(session => session.Id == id))
            _activeId = id;
    }

    public void Remember(HanhuaSession session)
    {
        var index = _sessions.FindIndex(existing => existing.Id == session.Id);
        if (index >= 0) _sessions[index] = session;
    }

    public void ReplaceAll(IReadOnlyList<HanhuaSession> sessions, string activeId)
    {
        _sessions.Clear();
        _sessions.AddRange(sessions);
        if (_sessions.Count == 0)
            _sessions.Add(Draft());
        _activeId = _sessions.Any(session => session.Id == activeId) ? activeId : _sessions[0].Id;
    }

    public void Append(HanhuaSession session)
    {
        if (_sessions.Any(existing => existing.Id == session.Id
            || (session.JobId is not null && existing.JobId == session.JobId)))
            return;
        _sessions.Add(session);
    }

    public static HanhuaSession FromJob(HanhuaJob job)
        => new(job.Id, HanhuaSession.TitleFor(job.SourcePath), job.SourcePath, job.Kind, job.Engine, job.Id);

    private static HanhuaSession Draft()
        => new(Guid.NewGuid().ToString("N")[..12], HanhuaSession.DraftTitle, "", HanhuaKind.Game, HanhuaEngine.LocalQwen, null);
}
