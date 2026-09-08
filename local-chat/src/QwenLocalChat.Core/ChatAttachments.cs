namespace QwenLocalChat.Core;

public enum ChatAttachmentKind
{
    Text = 0,
    Image = 1,
    Audio = 2,
    Video = 3,
}

public sealed record ChatAttachmentPolicy
{
    public const int DefaultMaxFiles = 4;
    public const int NodeMaxFiles = 9;
    public const int DefaultMaxBytesPerFile = 256 * 1024;
    public const int MaximumMaxBytesPerFile = 16 * 1024 * 1024;
    public const int DefaultMaxCharsPerFile = 12_000;

    [System.Text.Json.Serialization.JsonPropertyName("allow_text")]
    public bool AllowText { get; init; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("allow_image")]
    public bool AllowImage { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("allow_audio")]
    public bool AllowAudio { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("allow_video")]
    public bool AllowVideo { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("max_files")]
    public int MaxFiles { get; init; } = DefaultMaxFiles;

    [System.Text.Json.Serialization.JsonPropertyName("max_bytes_per_file")]
    public int MaxBytesPerFile { get; init; } = DefaultMaxBytesPerFile;

    /// <summary>Comma-separated extra extensions, e.g. <c>.rst,.tex</c>. Treated as text.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("extra_extensions")]
    public string ExtraExtensions { get; init; } = "";

    public static ChatAttachmentPolicy SafeDefaults { get; } = new();

    public ChatAttachmentPolicy Normalized()
        => this with
        {
            MaxFiles = Math.Clamp(MaxFiles, 1, NodeMaxFiles),
            MaxBytesPerFile = Math.Clamp(MaxBytesPerFile, 4 * 1024, MaximumMaxBytesPerFile),
            ExtraExtensions = NormalizeExtra(ExtraExtensions),
        };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaxFiles is < 1 or > NodeMaxFiles)
            errors.Add($"聊天附件数量必须在 1 到 {NodeMaxFiles} 之间");
        if (MaxBytesPerFile is < 4 * 1024 or > MaximumMaxBytesPerFile)
            errors.Add("聊天附件单文件大小必须在 4 KB 到 16 MB 之间");
        if (!AllowText && !AllowImage && !AllowAudio && !AllowVideo)
            errors.Add("至少开启一种聊天附件类型");
        return errors;
    }

    public IReadOnlyList<string> AllowedExtensions()
    {
        var list = new List<string>();
        if (AllowText)
        {
            list.AddRange(ChatAttachmentCatalog.TextExtensions);
            list.AddRange(ParseExtra(ExtraExtensions));
        }
        if (AllowImage) list.AddRange(ChatAttachmentCatalog.ImageExtensions);
        if (AllowAudio) list.AddRange(ChatAttachmentCatalog.AudioExtensions);
        if (AllowVideo) list.AddRange(ChatAttachmentCatalog.VideoExtensions);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(ext => ext, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool Allows(string? pathOrExtension)
    {
        var ext = NormalizeExtension(pathOrExtension);
        return ext.Length > 0 && AllowedExtensions().Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizeExtra(string? raw)
        => string.Join(",", ParseExtra(raw));

    public static IReadOnlyList<string> ParseExtra(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split([',', ';', ' ', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeExtension)
            .Where(ext => ext.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizeExtension(string? pathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension)) return "";
        var value = pathOrExtension.Trim();
        if (value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('/', StringComparison.Ordinal)
            || (value.Contains('.', StringComparison.Ordinal) && !value.StartsWith('.')))
            value = Path.GetExtension(value);
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!value.StartsWith('.')) value = "." + value.TrimStart('.');
        return value.ToLowerInvariant();
    }
}

public static class ChatAttachmentCatalog
{
    public static readonly string[] TextExtensions =
    [
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml",
        ".log", ".ini", ".cfg", ".toml", ".html", ".htm", ".css", ".js", ".ts", ".cs", ".py",
        ".ps1", ".bat", ".cmd", ".sh", ".pdf",
    ];

    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    public static readonly string[] AudioExtensions = [".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aac"];
    public static readonly string[] VideoExtensions = [".mp4", ".webm", ".mov", ".mkv", ".avi"];

    public static ChatAttachmentKind? KindFor(string? pathOrExtension)
    {
        var ext = ChatAttachmentPolicy.NormalizeExtension(pathOrExtension);
        if (TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return ChatAttachmentKind.Text;
        if (ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return ChatAttachmentKind.Image;
        if (AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return ChatAttachmentKind.Audio;
        if (VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return ChatAttachmentKind.Video;
        if (ext.Length > 1) return ChatAttachmentKind.Text;
        return null;
    }
}

public sealed record ChatAttachment(
    string Path,
    string FileName,
    ChatAttachmentKind Kind,
    string? TextContent = null,
    bool Truncated = false,
    string? Error = null)
{
    public bool HasTextForModel => Kind == ChatAttachmentKind.Text && !string.IsNullOrEmpty(TextContent);
}

public sealed record ChatAttachmentLoadResult(
    IReadOnlyList<ChatAttachment> Attachments,
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<ChatAttachment> Readable => Attachments.Where(item => item.HasTextForModel).ToArray();
}

public static class ChatAttachmentComposer
{
    public static ChatAttachmentLoadResult Load(IEnumerable<string> paths, ChatAttachmentPolicy policy)
    {
        var normalized = (policy ?? ChatAttachmentPolicy.SafeDefaults).Normalized();
        var attachments = new List<ChatAttachment>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in paths ?? [])
        {
            if (attachments.Count >= normalized.MaxFiles) break;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try { full = System.IO.Path.GetFullPath(raw.Trim()); }
            catch (Exception)
            {
                errors.Add("附件路径无效。");
                continue;
            }
            if (!seen.Add(full)) continue;
            attachments.Add(ReadOne(full, normalized, errors));
        }

        return new(attachments, errors);
    }

    public static string BuildModelMessage(string prompt, IReadOnlyList<ChatAttachment> attachments)
    {
        var readable = (attachments ?? []).Where(item => item.HasTextForModel).ToArray();
        var body = prompt?.Trim() ?? "";
        if (readable.Length == 0) return body;

        var blocks = new System.Text.StringBuilder();
        if (body.Length > 0)
        {
            blocks.AppendLine(body);
            blocks.AppendLine();
        }
        foreach (var file in readable)
        {
            blocks.Append("<attached_file name=\"");
            blocks.Append(file.FileName);
            blocks.AppendLine("\">");
            blocks.AppendLine(file.TextContent);
            if (file.Truncated)
                blocks.AppendLine("[truncated]");
            blocks.AppendLine("</attached_file>");
        }
        return blocks.ToString().TrimEnd();
    }

    public static string BuildVisibleMessage(string prompt, IReadOnlyList<ChatAttachment> attachments)
    {
        var names = (attachments ?? [])
            .Where(item => string.IsNullOrWhiteSpace(item.Error))
            .Select(item => item.FileName)
            .ToArray();
        var body = prompt?.Trim() ?? "";
        if (names.Length == 0) return body;
        var suffix = "附件：" + string.Join("、", names);
        return body.Length == 0 ? suffix : body + Environment.NewLine + Environment.NewLine + suffix;
    }

    private static ChatAttachment ReadOne(string fullPath, ChatAttachmentPolicy policy, List<string> errors)
    {
        var name = System.IO.Path.GetFileName(fullPath);
        var kind = ChatAttachmentCatalog.KindFor(fullPath) ?? ChatAttachmentKind.Text;
        if (!policy.Allows(fullPath))
        {
            var message = $"未允许的附件类型：{name}";
            errors.Add(message);
            return new(fullPath, name, kind, Error: message);
        }
        if (!File.Exists(fullPath))
        {
            var message = $"附件不存在：{name}";
            errors.Add(message);
            return new(fullPath, name, kind, Error: message);
        }

        if (kind != ChatAttachmentKind.Text)
        {
            var message = $"当前文本模型不能读取{DisplayKind(kind)}，已跳过 {name}。";
            errors.Add(message);
            return new(fullPath, name, kind, Error: message);
        }

        FileInfo info;
        try { info = new FileInfo(fullPath); }
        catch (Exception error)
        {
            var message = $"无法读取 {name}：{error.Message}";
            errors.Add(message);
            return new(fullPath, name, kind, Error: message);
        }
        if (info.Length > policy.MaxBytesPerFile)
        {
            var message = $"{name} 超过单文件上限 {policy.MaxBytesPerFile / 1024} KB。";
            errors.Add(message);
            return new(fullPath, name, kind, Error: message);
        }

        if (PdfAttachmentText.IsPdf(fullPath))
            return ReadPdf(fullPath, name, kind, errors);

        string text;
        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            if (bytes.Contains((byte)0))
            {
                var message = $"{name} 不是可读取的文本文件。";
                errors.Add(message);
                return new(fullPath, name, kind, Error: message);
            }
            text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (Exception error)
        {
            var message = $"无法读取 {name}：{error.Message}";
            errors.Add(message);
            return new(fullPath, name, kind, Error: message);
        }

        var truncated = false;
        if (text.Length > ChatAttachmentPolicy.DefaultMaxCharsPerFile)
        {
            text = text[..ChatAttachmentPolicy.DefaultMaxCharsPerFile];
            truncated = true;
        }
        return new(fullPath, name, kind, text, truncated);
    }

    private static ChatAttachment ReadPdf(string fullPath, string name, ChatAttachmentKind kind, List<string> errors)
    {
        try
        {
            var text = PdfAttachmentText.Extract(fullPath, ChatAttachmentPolicy.DefaultMaxCharsPerFile, out var truncated);
            if (string.IsNullOrWhiteSpace(text))
            {
                var message = $"未能从 {name} 抽出文字（可能是扫描件或加密文档）。";
                errors.Add(message);
                return new(fullPath, name, kind, Error: message);
            }
            return new(fullPath, name, kind, text, truncated);
        }
        catch (Exception error)
        {
            var message = $"无法读取 PDF {name}：{error.Message}";
            errors.Add(message);
            return new(fullPath, name, kind, Error: message);
        }
    }

    private static string DisplayKind(ChatAttachmentKind kind) => kind switch
    {
        ChatAttachmentKind.Image => "图片",
        ChatAttachmentKind.Audio => "音频",
        ChatAttachmentKind.Video => "视频",
        _ => "该文件",
    };
}
