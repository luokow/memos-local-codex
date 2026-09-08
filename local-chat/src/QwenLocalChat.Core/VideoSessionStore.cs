using System.Text.Json;
using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

public sealed record VideoSessionLoadResult(
    bool Restored,
    int SessionCount,
    string? Warning,
    string? CorruptBackupPath);

/// <summary>Persists video windows under data/video-sessions.json.</summary>
public sealed class VideoSessionStore(string path)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path;

    public VideoSessionLoadResult LoadInto(VideoSessionWorkspace workspace)
    {
        if (!File.Exists(Path))
        {
            workspace.EnsureBootstrap();
            return new(false, workspace.Sessions.Count, null, null);
        }

        try
        {
            var document = JsonSerializer.Deserialize<SessionsDocument>(File.ReadAllText(Path), JsonOptions)
                ?? throw new JsonException("视频会话文件内容为空");
            var sessions = document.Sessions
                .Select(ToSession)
                .Where(s => s is not null)
                .Cast<VideoSession>()
                .ToList();
            if (sessions.Count == 0)
                throw new JsonException("视频会话列表为空");

            var activeId = document.ActiveId;
            if (string.IsNullOrWhiteSpace(activeId) || sessions.All(s => s.Id != activeId))
                activeId = sessions[0].Id;

            workspace.ReplaceAll(sessions, activeId);
            return new(true, sessions.Count, null, null);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            string? backup = null;
            try
            {
                backup = $"{Path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(Path, backup, overwrite: false);
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
            {
                workspace.EnsureBootstrap();
                return new(false, workspace.Sessions.Count,
                    $"视频会话文件无法读取且备份失败：{backupError.Message}", null);
            }

            workspace.EnsureBootstrap();
            return new(false, workspace.Sessions.Count,
                $"视频会话文件损坏，已恢复空窗口：{error.Message}", backup);
        }
    }

    public void Save(VideoSessionWorkspace workspace)
    {
        if (workspace.Sessions.Count == 0) return;

        var document = new SessionsDocument
        {
            Version = CurrentVersion,
            ActiveId = workspace.Active.Id,
            Sessions = workspace.Sessions.Select(ToDto).ToList(),
        };

        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("视频会话路径缺少目录");
        Directory.CreateDirectory(directory);
        var temporary = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch { /* best effort */ }
            }
        }
    }

    private static SessionDto ToDto(VideoSession session) => new()
    {
        Id = session.Id,
        Title = session.Title,
        CreatedAt = session.CreatedAt,
        UpdatedAt = session.UpdatedAt,
        DraftPrompt = session.DraftPrompt,
        ConditioningMode = session.ConditioningMode,
        FirstFramePath = session.FirstFramePath,
        LastFramePath = session.LastFramePath,
        ReferenceImagePath = session.ReferenceImagePath,
        ReferenceImagePaths = session.ReferenceImagePaths,
        ReferenceVideoPaths = session.ReferenceVideoPaths,
        ReferenceAudioPaths = session.ReferenceAudioPaths,
        LastOutputPath = session.LastOutputPath,
        LastStatus = session.LastStatus,
        LastError = session.LastError,
        LastPrompt = session.LastPrompt,
        DraftTemplate = session.DraftTemplate,
        DraftTemplateTitle = session.DraftTemplateTitle,
        ExtraAction = session.ExtraAction,
        ExtraSound = session.ExtraSound,
        ExtraMusic = session.ExtraMusic,
        ExtraIdentity = session.ExtraIdentity,
        AssembledOverride = session.AssembledOverride,
        Width = session.JobOverrides?.Width,
        Height = session.JobOverrides?.Height,
        DurationSeconds = session.JobOverrides?.DurationSeconds,
        Steps = session.JobOverrides?.Steps,
        Seed = session.JobOverrides?.Seed,
        RandomSeed = session.JobOverrides?.RandomSeed,
    };

    private static VideoSession? ToSession(SessionDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Id)) return null;
        var created = dto.CreatedAt == default ? DateTimeOffset.Now : dto.CreatedAt;
        var updated = dto.UpdatedAt == default ? created : dto.UpdatedAt;
        VideoGenerationOverrides? overrides = null;
        if (dto.Width is not null || dto.Height is not null || dto.DurationSeconds is not null
            || dto.Steps is not null || dto.Seed is not null || dto.RandomSeed is not null)
        {
            overrides = new VideoGenerationOverrides
            {
                Width = dto.Width,
                Height = dto.Height,
                DurationSeconds = dto.DurationSeconds,
                Steps = dto.Steps,
                Seed = dto.Seed,
                RandomSeed = dto.RandomSeed,
            };
        }

        return new VideoSession
        {
            Id = dto.Id.Trim(),
            Title = string.IsNullOrWhiteSpace(dto.Title) ? VideoSession.EmptyTitle : dto.Title.Trim(),
            CreatedAt = created,
            UpdatedAt = updated,
            DraftPrompt = dto.DraftPrompt ?? string.Empty,
            ConditioningMode = string.IsNullOrWhiteSpace(dto.ConditioningMode)
                ? VideoMediaCapabilities.ToId(VideoConditioningMode.Text)
                : dto.ConditioningMode.Trim(),
            FirstFramePath = dto.FirstFramePath,
            LastFramePath = dto.LastFramePath,
            ReferenceImagePath = dto.ReferenceImagePath,
            ReferenceImagePaths = VideoSession.NormalizeStoredPaths(dto.ReferenceImagePaths, dto.ReferenceImagePath),
            ReferenceVideoPaths = VideoSession.NormalizeStoredPaths(dto.ReferenceVideoPaths, null),
            ReferenceAudioPaths = VideoSession.NormalizeStoredPaths(dto.ReferenceAudioPaths, null),
            JobOverrides = overrides,
            LastOutputPath = dto.LastOutputPath,
            LastStatus = dto.LastStatus,
            LastError = dto.LastError,
            LastPrompt = dto.LastPrompt,
            DraftTemplate = dto.DraftTemplate,
            DraftTemplateTitle = dto.DraftTemplateTitle,
            ExtraAction = dto.ExtraAction ?? "",
            ExtraSound = dto.ExtraSound ?? "",
            ExtraMusic = dto.ExtraMusic ?? "",
            ExtraIdentity = dto.ExtraIdentity ?? "",
            AssembledOverride = dto.AssembledOverride,
        };
    }

    private sealed class SessionsDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("activeId")]
        public string ActiveId { get; set; } = string.Empty;

        [JsonPropertyName("sessions")]
        public List<SessionDto> Sessions { get; set; } = [];
    }

    private sealed class SessionDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonPropertyName("draftPrompt")]
        public string? DraftPrompt { get; set; }

        [JsonPropertyName("conditioningMode")]
        public string? ConditioningMode { get; set; }

        [JsonPropertyName("firstFramePath")]
        public string? FirstFramePath { get; set; }

        [JsonPropertyName("lastFramePath")]
        public string? LastFramePath { get; set; }

        [JsonPropertyName("referenceImagePath")]
        public string? ReferenceImagePath { get; set; }

        [JsonPropertyName("referenceImagePaths")]
        public List<string>? ReferenceImagePaths { get; set; }

        [JsonPropertyName("referenceVideoPaths")]
        public List<string>? ReferenceVideoPaths { get; set; }

        [JsonPropertyName("referenceAudioPaths")]
        public List<string>? ReferenceAudioPaths { get; set; }

        [JsonPropertyName("lastOutputPath")]
        public string? LastOutputPath { get; set; }

        [JsonPropertyName("lastStatus")]
        public string? LastStatus { get; set; }

        [JsonPropertyName("lastError")]
        public string? LastError { get; set; }

        [JsonPropertyName("lastPrompt")]
        public string? LastPrompt { get; set; }

        [JsonPropertyName("draftTemplate")]
        public string? DraftTemplate { get; set; }

        [JsonPropertyName("draftTemplateTitle")]
        public string? DraftTemplateTitle { get; set; }

        [JsonPropertyName("extraAction")]
        public string? ExtraAction { get; set; }

        [JsonPropertyName("extraSound")]
        public string? ExtraSound { get; set; }

        [JsonPropertyName("extraMusic")]
        public string? ExtraMusic { get; set; }

        [JsonPropertyName("extraIdentity")]
        public string? ExtraIdentity { get; set; }

        [JsonPropertyName("assembledOverride")]
        public string? AssembledOverride { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        [JsonPropertyName("durationSeconds")]
        public int? DurationSeconds { get; set; }

        [JsonPropertyName("steps")]
        public int? Steps { get; set; }

        [JsonPropertyName("seed")]
        public long? Seed { get; set; }

        [JsonPropertyName("randomSeed")]
        public bool? RandomSeed { get; set; }
    }
}
