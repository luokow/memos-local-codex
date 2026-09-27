using System.Text.Json;
using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

public sealed record HanhuaSessionState(
    HanhuaSessionWorkspace Workspace,
    IReadOnlyList<HanhuaQueuedStart> Queue);

public sealed class HanhuaSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;

    public HanhuaSessionStore(string path) => _path = path;

    public void Save(HanhuaSessionWorkspace workspace, IReadOnlyList<HanhuaQueuedStart> queue)
    {
        if (workspace.Sessions.Count == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var document = new Document(
            1,
            workspace.Active.Id,
            workspace.Sessions.ToArray(),
            queue.Where(item => workspace.Sessions.Any(session => session.Id == item.SessionId)).ToArray());
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    public HanhuaSessionState Load(IReadOnlyList<HanhuaJob> jobs)
    {
        var workspace = new HanhuaSessionWorkspace();
        var queue = new List<HanhuaQueuedStart>();
        if (File.Exists(_path))
        {
            try
            {
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), JsonOptions);
                if (document?.Sessions is { Length: > 0 })
                {
                    workspace.ReplaceAll(document.Sessions, document.ActiveId ?? "");
                    queue.AddRange((document.Queue ?? []).Where(item =>
                        workspace.Sessions.Any(session => session.Id == item.SessionId)));
                }
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                workspace = HanhuaSessionWorkspace.FromJobs(jobs);
            }
        }

        if (workspace.Sessions.Count == 0)
            workspace = HanhuaSessionWorkspace.FromJobs(jobs);

        foreach (var job in jobs)
            workspace.Append(HanhuaSessionWorkspace.FromJob(job));

        foreach (var job in jobs.Where(job => job.Status == HanhuaJobStatus.Queued))
        {
            if (queue.Any(item => item.SessionId == job.Id)) continue;
            var session = workspace.Sessions.FirstOrDefault(item => item.JobId == job.Id || item.Id == job.Id);
            if (session is null) continue;
            queue.Add(new HanhuaQueuedStart(session.Id, job.SourcePath, job.Kind, job.Engine, false));
        }

        return new HanhuaSessionState(workspace, queue);
    }

    private sealed record Document(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("active_id")] string ActiveId,
        [property: JsonPropertyName("sessions")] HanhuaSession[] Sessions,
        [property: JsonPropertyName("queue")] HanhuaQueuedStart[] Queue);
}
