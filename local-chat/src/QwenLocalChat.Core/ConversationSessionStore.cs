using System.Text.Json;
using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

public sealed record ConversationSessionLoadResult(
    bool Restored,
    int SessionCount,
    string? Warning,
    string? CorruptBackupPath);

/// <summary>
/// Persists multi-session workspace (history, transcript, drafts, active id) under data/sessions.json.
/// </summary>
public sealed class ConversationSessionStore(string path)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path;

    public ConversationSessionLoadResult LoadInto(ConversationSessionWorkspace workspace)
    {
        if (!File.Exists(Path))
        {
            workspace.EnsureBootstrap();
            return new(false, workspace.Sessions.Count, null, null);
        }

        try
        {
            var document = JsonSerializer.Deserialize<SessionsDocument>(File.ReadAllText(Path), JsonOptions)
                ?? throw new JsonException("会话文件内容为空");
            var sessions = document.Sessions
                .Select(ToSession)
                .Where(s => s is not null)
                .Cast<ConversationSession>()
                .ToList();
            if (sessions.Count == 0)
                throw new JsonException("会话列表为空");

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
                backup = null;
                workspace.EnsureBootstrap();
                return new(false, workspace.Sessions.Count,
                    $"会话文件无法读取且备份失败：{backupError.Message}", null);
            }

            workspace.EnsureBootstrap();
            return new(false, workspace.Sessions.Count,
                $"会话文件损坏，已恢复空会话：{error.Message}", backup);
        }
    }

    public void Save(ConversationSessionWorkspace workspace)
    {
        if (workspace.Sessions.Count == 0) return;

        var document = new SessionsDocument
        {
            Version = CurrentVersion,
            ActiveId = workspace.Active.Id,
            Sessions = workspace.Sessions.Select(ToDto).ToList(),
        };

        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("会话路径缺少目录");
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

    private static SessionDto ToDto(ConversationSession session) => new()
    {
        Id = session.Id,
        Title = session.Title,
        CreatedAt = session.CreatedAt,
        UpdatedAt = session.UpdatedAt,
        MemosSessionId = session.MemosSessionId,
        LastFinishReason = session.LastFinishReason,
        DraftInput = session.DraftInput,
        DraftAttachmentPaths = session.DraftAttachmentPaths,
        History = session.History
            .Select(m => new HistoryMessageDto { Role = m.Role, Content = m.Content })
            .ToList(),
        Transcript = session.Transcript
            .Select(t => new TranscriptTurnDto { Label = t.Label, Text = t.Text })
            .ToList(),
        LongForm = session.LongFormPlan is null
            ? null
            : new LongFormPlanDto
            {
                OriginalUserPrompt = session.LongFormPlan.OriginalUserPrompt,
                TargetChars = session.LongFormPlan.TargetChars,
                SegmentChars = session.LongFormPlan.SegmentChars,
                TotalSegments = session.LongFormPlan.TotalSegments,
                CompletedSegments = session.LongFormPlan.CompletedSegments,
            },
    };

    private static ConversationSession? ToSession(SessionDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Id)) return null;
        var id = dto.Id.Trim();
        var memos = string.IsNullOrWhiteSpace(dto.MemosSessionId)
            ? $"qwen-local-chat-{id}"
            : dto.MemosSessionId.Trim();
        var created = dto.CreatedAt == default ? DateTimeOffset.Now : dto.CreatedAt;
        var updated = dto.UpdatedAt == default ? created : dto.UpdatedAt;
        return new ConversationSession
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(dto.Title) ? "新会话" : dto.Title.Trim(),
            CreatedAt = created,
            UpdatedAt = updated,
            MemosSessionId = memos,
            LastFinishReason = dto.LastFinishReason,
            DraftInput = dto.DraftInput ?? string.Empty,
            DraftAttachmentPaths = (dto.DraftAttachmentPaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList(),
            History = (dto.History ?? [])
                .Where(m => !string.IsNullOrWhiteSpace(m.Role))
                .Select(m => new ChatMessage(m.Role!.Trim(), m.Content ?? string.Empty))
                .ToList(),
            Transcript = (dto.Transcript ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t.Label) && !string.IsNullOrWhiteSpace(t.Text))
                .Select(t => new ConversationTurn(t.Label!.Trim(), t.Text!))
                .ToList(),
            LongFormPlan = dto.LongForm is null
                    || string.IsNullOrWhiteSpace(dto.LongForm.OriginalUserPrompt)
                    || dto.LongForm.TotalSegments < 2
                ? null
                : new LongFormPlan
                {
                    OriginalUserPrompt = dto.LongForm.OriginalUserPrompt.Trim(),
                    TargetChars = Math.Max(200, dto.LongForm.TargetChars),
                    SegmentChars = Math.Clamp(
                        dto.LongForm.SegmentChars <= 0
                            ? LongFormPlanner.SegmentDefaultChars
                            : dto.LongForm.SegmentChars,
                        LongFormPlanner.SegmentMinChars,
                        LongFormPlanner.SegmentMaxChars),
                    TotalSegments = Math.Clamp(dto.LongForm.TotalSegments, 2, 12),
                    CompletedSegments = Math.Clamp(dto.LongForm.CompletedSegments, 0, 12),
                },
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

        [JsonPropertyName("memosSessionId")]
        public string? MemosSessionId { get; set; }

        [JsonPropertyName("lastFinishReason")]
        public string? LastFinishReason { get; set; }

        [JsonPropertyName("draftInput")]
        public string? DraftInput { get; set; }

        [JsonPropertyName("draftAttachmentPaths")]
        public List<string>? DraftAttachmentPaths { get; set; }

        [JsonPropertyName("history")]
        public List<HistoryMessageDto>? History { get; set; }

        [JsonPropertyName("transcript")]
        public List<TranscriptTurnDto>? Transcript { get; set; }

        [JsonPropertyName("longForm")]
        public LongFormPlanDto? LongForm { get; set; }
    }

    private sealed class LongFormPlanDto
    {
        [JsonPropertyName("originalUserPrompt")]
        public string? OriginalUserPrompt { get; set; }

        [JsonPropertyName("targetChars")]
        public int TargetChars { get; set; }

        [JsonPropertyName("segmentChars")]
        public int SegmentChars { get; set; }

        [JsonPropertyName("totalSegments")]
        public int TotalSegments { get; set; }

        [JsonPropertyName("completedSegments")]
        public int CompletedSegments { get; set; }
    }

    private sealed class HistoryMessageDto
    {
        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private sealed class TranscriptTurnDto
    {
        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
