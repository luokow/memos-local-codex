using System.Text.Json;
using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

public sealed class HanhuaJobStore
{
    private const int Keep = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public HanhuaJobStore(string path) => _path = path;

    public static string DefaultPath(string localChatRoot)
        => Path.Combine(localChatRoot, "data", "hanhua-jobs.json");

    public string FilePath => _path;

    public HanhuaSnapshot Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return HanhuaSnapshot.Empty;
            try
            {
                var snapshot = JsonSerializer.Deserialize<HanhuaSnapshot>(File.ReadAllText(_path), JsonOptions);
                if (snapshot is null || snapshot.SchemaVersion != HanhuaSnapshot.CurrentSchemaVersion)
                    return HanhuaSnapshot.Empty;
                var jobs = snapshot.Jobs?
                    .Where(job => job is not null && !string.IsNullOrWhiteSpace(job.Id))
                    .ToArray() ?? [];
                return snapshot with { Jobs = jobs };
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                return HanhuaSnapshot.Empty;
            }
        }
    }

    public void Upsert(HanhuaJob job)
    {
        lock (_gate)
        {
            var current = Load().Jobs.ToList();
            var index = current.FindIndex(existing => string.Equals(existing.Id, job.Id, StringComparison.Ordinal));
            if (index >= 0) current[index] = job;
            else current.Insert(0, job);

            var keep = new List<HanhuaJob>(Keep + 1);
            foreach (var item in current)
            {
                if (item.IsActive || item.CanResume)
                {
                    keep.Add(item);
                    continue;
                }
                if (keep.Count < Keep) keep.Add(item);
            }

            Write(new HanhuaSnapshot(HanhuaSnapshot.CurrentSchemaVersion, keep));
        }
    }

    public HanhuaJob? MarkInterruptedIfRunning()
    {
        lock (_gate)
        {
            var snapshot = Load();
            var running = snapshot.Jobs.FirstOrDefault(job => job.Status is HanhuaJobStatus.Running or HanhuaJobStatus.Cancelling);
            if (running is null) return snapshot.Active;
            var interrupted = running with
            {
                Status = HanhuaJobStatus.Interrupted,
                Message = "上次汉化未完成。",
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            Upsert(interrupted);
            return interrupted;
        }
    }

    private void Write(HanhuaSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
