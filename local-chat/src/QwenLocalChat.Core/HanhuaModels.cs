using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

public enum HanhuaKind
{
    Game,
    Image,
}

public enum HanhuaEngine
{
    LocalQwen,
    Aliyun,
}

public enum HanhuaPhase
{
    Copy,
    Extract,
    Translate,
    Inject,
    Ocr,
    Fill,
    Typeset,
}

public enum HanhuaJobStatus
{
    Queued,
    Running,
    Cancelling,
    Succeeded,
    Failed,
    Interrupted,
}

public enum HanhuaGpuNeed
{
    None,
    Qwen,
    Mit,
}

public static class HanhuaEngineCodec
{
    public const string Local = "local";
    public const string Aliyun = "aliyun";

    public static HanhuaEngine Parse(string? value)
        => string.Equals(value?.Trim(), Aliyun, StringComparison.OrdinalIgnoreCase)
            ? HanhuaEngine.Aliyun
            : HanhuaEngine.LocalQwen;

    public static string ToJson(HanhuaEngine engine)
        => engine == HanhuaEngine.Aliyun ? Aliyun : Local;
}

public sealed record HanhuaJob(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] HanhuaKind Kind,
    [property: JsonPropertyName("engine")] HanhuaEngine Engine,
    [property: JsonPropertyName("phase")] HanhuaPhase Phase,
    [property: JsonPropertyName("status")] HanhuaJobStatus Status,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("work_path")] string? WorkPath,
    [property: JsonPropertyName("output_path")] string? OutputPath,
    [property: JsonPropertyName("done")] int Done,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("started_utc")] DateTimeOffset StartedUtc,
    [property: JsonPropertyName("updated_utc")] DateTimeOffset UpdatedUtc)
{
    public bool IsActive => Status is HanhuaJobStatus.Queued or HanhuaJobStatus.Running or HanhuaJobStatus.Cancelling;

    public bool CanResume => Status is HanhuaJobStatus.Running or HanhuaJobStatus.Interrupted or HanhuaJobStatus.Cancelling;
}

public sealed record HanhuaLaunch(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

public readonly record struct HanhuaProgressEvent(
    string Type,
    string? Phase,
    int Done,
    int Total,
    string? Message,
    string? Output,
    int Empty,
    string RawLine,
    bool IsJson);

public sealed record HanhuaSnapshot(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("jobs")] IReadOnlyList<HanhuaJob> Jobs)
{
    public const int CurrentSchemaVersion = 1;

    public static HanhuaSnapshot Empty { get; } = new(CurrentSchemaVersion, []);

    public HanhuaJob? Active
        => Jobs.FirstOrDefault(job => job.IsActive)
           ?? Jobs.FirstOrDefault(job => job.CanResume);
}
