using System.Text.Json;
using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

/// <summary>The in-flight video job plus FIFO waiters, written so a restart can resume them.</summary>
public sealed record VideoRuntimeRunningJob(
    [property: JsonPropertyName("job")] VideoQueuedJob Job,
    [property: JsonPropertyName("prompt_id")] string? PromptId,
    [property: JsonPropertyName("profile_id")] string? ProfileId,
    [property: JsonPropertyName("started_at_utc")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("elapsed_seconds")] double ElapsedSeconds = 0);

public sealed record VideoJobRuntimeSnapshot(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("running")] VideoRuntimeRunningJob? Running,
    [property: JsonPropertyName("queue")] IReadOnlyList<VideoQueuedJob> Queue)
{
    public const int CurrentSchemaVersion = 1;

    public bool HasWork => Running is not null || Queue.Count > 0;

    public int QueuedCount => Queue.Count;

    public static VideoJobRuntimeSnapshot Empty { get; } = new(CurrentSchemaVersion, null, []);
}

/// <summary>Persists the current video job and wait list under data/video-job-runtime.json.</summary>
public sealed class VideoJobRuntimeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public VideoJobRuntimeStore(string path) => _path = path;

    public static string DefaultPath(string localChatRoot)
        => System.IO.Path.Combine(localChatRoot, "data", "video-job-runtime.json");

    public string FilePath => _path;

    public VideoJobRuntimeSnapshot Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return VideoJobRuntimeSnapshot.Empty;
            try
            {
                var snapshot = JsonSerializer.Deserialize<VideoJobRuntimeSnapshot>(File.ReadAllText(_path), JsonOptions);
                if (snapshot is null || snapshot.SchemaVersion != VideoJobRuntimeSnapshot.CurrentSchemaVersion)
                    return VideoJobRuntimeSnapshot.Empty;
                var queue = snapshot.Queue?
                    .Where(job => job is not null && !string.IsNullOrWhiteSpace(job.SessionId) && !string.IsNullOrWhiteSpace(job.Prompt))
                    .ToArray() ?? [];
                var running = snapshot.Running is { Job.SessionId.Length: > 0, Job.Prompt.Length: > 0 }
                    ? snapshot.Running
                    : null;
                return snapshot with { Queue = queue, Running = running };
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                return VideoJobRuntimeSnapshot.Empty;
            }
        }
    }

    public void Save(VideoJobRuntimeSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!snapshot.HasWork)
            {
                Clear();
                return;
            }

            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var payload = snapshot with
            {
                SchemaVersion = VideoJobRuntimeSnapshot.CurrentSchemaVersion,
                Queue = snapshot.Queue.ToArray(),
            };
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(payload, JsonOptions));
            File.Copy(temporary, _path, overwrite: true);
            File.Delete(temporary);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
            var temporary = _path + ".tmp";
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
