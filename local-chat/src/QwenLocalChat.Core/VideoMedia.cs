using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

/// <summary>
/// Conditioning modes the Local AI client can drive.
/// H3 also accepts reference videos and standalone reference audio.
/// </summary>
public enum VideoConditioningMode
{
    Text = 0,
    FirstFrame = 1,
    FirstLastFrame = 2,
    SingleReferenceImage = 3,
    ReferenceVideo = 4,
    ReferenceAudio = 5,
    LastFrame = 6,
}

/// <summary>One add control that sits on the same wrap strip as existing reference chips.</summary>
public readonly record struct ReferenceAddAction(string Kind, string Label, string Name, string Tooltip);

/// <summary>
/// Per-profile media surface. Keep defaults conservative so older catalogs
/// without this block still load as text-only.
/// </summary>
public sealed record VideoMediaCapabilities
{
    [JsonPropertyName("textToVideo")]
    public bool TextToVideo { get; init; } = true;

    [JsonPropertyName("firstFrame")]
    public bool FirstFrame { get; init; }

    [JsonPropertyName("firstLastFrame")]
    public bool FirstLastFrame { get; init; }

    /// <summary>Current client cap (machine-safe default). Settings may raise this up to <see cref="EffectiveNodeMaxReferenceImages"/>.</summary>
    [JsonPropertyName("maxReferenceImages")]
    public int MaxReferenceImages { get; init; }

    /// <summary>Workflow/node ceiling. Settings NumberBox maximum. H3 Autogrow allows 9.</summary>
    [JsonPropertyName("nodeMaxReferenceImages")]
    public int NodeMaxReferenceImages { get; init; }

    [JsonPropertyName("defaultRefImageSize")]
    public string DefaultRefImageSize { get; init; } = "match";

    [JsonPropertyName("maxReferenceVideos")]
    public int MaxReferenceVideos { get; init; }

    [JsonPropertyName("nodeMaxReferenceVideos")]
    public int NodeMaxReferenceVideos { get; init; }

    [JsonPropertyName("maxReferenceAudios")]
    public int MaxReferenceAudios { get; init; }

    [JsonPropertyName("nodeMaxReferenceAudios")]
    public int NodeMaxReferenceAudios { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveNodeMaxReferenceImages =>
        NodeMaxReferenceImages > 0
            ? NodeMaxReferenceImages
            : MaxReferenceImages > 0 ? Math.Max(MaxReferenceImages, 9) : 0;

    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveNodeMaxReferenceVideos =>
        NodeMaxReferenceVideos > 0
            ? NodeMaxReferenceVideos
            : EffectiveNodeMaxReferenceImages > 0 ? Math.Max(MaxReferenceVideos, 3) : 0;

    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveNodeMaxReferenceAudios =>
        NodeMaxReferenceAudios > 0
            ? NodeMaxReferenceAudios
            : EffectiveNodeMaxReferenceImages > 0 ? Math.Max(MaxReferenceAudios, 3) : 0;

    /// <summary>Default UI mode id: text | first_frame | first_last_frame | single_reference_image | reference_video | reference_audio.</summary>
    [JsonPropertyName("defaultMode")]
    public string DefaultMode { get; init; } = "text";

    public static VideoMediaCapabilities TextOnly { get; } = new();

    /// <summary>H3 local surface: text, keyframes, 4 match-sized ref images, and one video/audio slot by default.</summary>
    public static VideoMediaCapabilities MiniMaxH3Local8Gb { get; } = new()
    {
        TextToVideo = true,
        FirstFrame = true,
        FirstLastFrame = true,
        MaxReferenceImages = 4,
        NodeMaxReferenceImages = 9,
        DefaultRefImageSize = "match",
        MaxReferenceVideos = 1,
        NodeMaxReferenceVideos = 3,
        MaxReferenceAudios = 1,
        NodeMaxReferenceAudios = 3,
        // Text remains default so a cold start never blocks on missing media.
        DefaultMode = "text",
    };

    public IReadOnlyList<string> ValidateDefinition()
    {
        var errors = new List<string>();
        if (!TextToVideo && !FirstFrame && !FirstLastFrame
            && MaxReferenceImages < 1 && MaxReferenceVideos < 1 && MaxReferenceAudios < 1)
            errors.Add("至少开启一种生成模式");
        if (MaxReferenceImages < 0 || MaxReferenceImages > EffectiveNodeMaxReferenceImages)
            errors.Add($"参考图上限必须在 0 到 {EffectiveNodeMaxReferenceImages} 张之间");
        if (MaxReferenceVideos < 0 || MaxReferenceVideos > EffectiveNodeMaxReferenceVideos)
            errors.Add($"参考视频上限必须在 0 到 {EffectiveNodeMaxReferenceVideos} 个之间");
        if (MaxReferenceAudios < 0 || MaxReferenceAudios > EffectiveNodeMaxReferenceAudios)
            errors.Add($"参考音频上限必须在 0 到 {EffectiveNodeMaxReferenceAudios} 个之间");
        if (!string.Equals(DefaultRefImageSize, "match", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(DefaultRefImageSize, "max", StringComparison.OrdinalIgnoreCase))
            errors.Add("参考图尺寸模式必须是 match 或 max");
        return errors;
    }

    public IReadOnlyList<VideoConditioningMode> EnabledModes()
    {
        var modes = new List<VideoConditioningMode>();
        if (TextToVideo) modes.Add(VideoConditioningMode.Text);
        if (FirstFrame) modes.Add(VideoConditioningMode.FirstFrame);
        if (FirstLastFrame)
        {
            modes.Add(VideoConditioningMode.FirstLastFrame);
            modes.Add(VideoConditioningMode.LastFrame);
        }
        if (MaxReferenceImages >= 1 || MaxReferenceVideos >= 1 || MaxReferenceAudios >= 1)
            modes.Add(VideoConditioningMode.SingleReferenceImage);
        if (modes.Count == 0) modes.Add(VideoConditioningMode.Text);
        return modes;
    }

    public static bool IsReferenceFamily(VideoConditioningMode mode)
        => mode is VideoConditioningMode.SingleReferenceImage
            or VideoConditioningMode.ReferenceVideo
            or VideoConditioningMode.ReferenceAudio;

    /// <summary>
    /// I2VA / FL2VA / L2VA all use MiniMaxH3ImageToVideo.
    /// Comfy labels first_frame as Picture 1; last_frame is Picture 2 when both
    /// keyframes are present, or Picture 1 when it is the only keyframe.
    /// </summary>
    public static bool IsKeyframeFamily(VideoConditioningMode mode)
        => mode is VideoConditioningMode.FirstFrame
            or VideoConditioningMode.FirstLastFrame
            or VideoConditioningMode.LastFrame;

    public VideoConditioningMode ResolveDefaultMode()
        => ParseMode(DefaultMode) is { } mode && EnabledModes().Contains(mode)
            ? mode
            : EnabledModes()[0];

    public static VideoConditioningMode? ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "text" or "t2v" or "text_only" => VideoConditioningMode.Text,
        "first_frame" or "first" or "i2v" => VideoConditioningMode.FirstFrame,
        "first_last_frame" or "first_last" or "fl2v" => VideoConditioningMode.FirstLastFrame,
        "last_frame" or "last" or "l2v" => VideoConditioningMode.LastFrame,
        "single_reference_image" or "ref_image" or "reference" => VideoConditioningMode.SingleReferenceImage,
        "reference_video" or "ref_video" => VideoConditioningMode.ReferenceVideo,
        "reference_audio" or "ref_audio" => VideoConditioningMode.ReferenceAudio,
        _ => null,
    };

    public static string ToId(VideoConditioningMode mode) => mode switch
    {
        VideoConditioningMode.Text => "text",
        VideoConditioningMode.FirstFrame => "first_frame",
        VideoConditioningMode.FirstLastFrame => "first_last_frame",
        VideoConditioningMode.LastFrame => "last_frame",
        VideoConditioningMode.SingleReferenceImage => "single_reference_image",
        VideoConditioningMode.ReferenceVideo => "reference_video",
        VideoConditioningMode.ReferenceAudio => "reference_audio",
        _ => "text",
    };

    public static string DisplayName(VideoConditioningMode mode) => mode switch
    {
        VideoConditioningMode.Text => "文生视频",
        VideoConditioningMode.FirstFrame => "首帧图生视频",
        VideoConditioningMode.FirstLastFrame => "首帧 + 尾帧",
        VideoConditioningMode.LastFrame => "尾帧图生视频",
        VideoConditioningMode.SingleReferenceImage => "参考（图/视频/音频）",
        VideoConditioningMode.ReferenceVideo => "参考（图/视频/音频）",
        VideoConditioningMode.ReferenceAudio => "参考（图/视频/音频）",
        _ => mode.ToString(),
    };

    /// <summary>
    /// Compact prompt-tag guidance shown next to the composer for the selected mode.
    /// Tags follow MiniMax H3 presentation order (1-based in the prompt text).
    /// </summary>
    public static string? PromptTagHint(VideoConditioningMode mode) => mode switch
    {
        VideoConditioningMode.FirstFrame =>
            "首帧锚定开场；提示词写镜头与运动即可。Comfy 把这张图标成 <Picture 1>。",
        VideoConditioningMode.FirstLastFrame =>
            "首帧开场、尾帧收束；提示词写中间过程即可。Comfy 固定图 1 = 首帧、图 2 = 尾帧。",
        VideoConditioningMode.LastFrame =>
            "尾帧锚定收束；提示词写如何落到这张图。Comfy 把这张图标成 <Picture 1>。",
        VideoConditioningMode.SingleReferenceImage =>
            ReferencePromptTagHint(1),
        VideoConditioningMode.ReferenceVideo =>
            ReferenceVideoPromptTagHint(1),
        VideoConditioningMode.ReferenceAudio =>
            ReferenceAudioPromptTagHint(1),
        _ => null,
    };

    public static string ReferencePromptTagHint(int count)
    {
        var n = Math.Max(1, count);
        if (n == 1) return "用 <Picture 1> 对应图 1；输入 @ 插入";
        var tags = string.Join("、", Enumerable.Range(1, n).Select(i => $"<Picture {i}>"));
        return $"用 {tags} 对应图 1–{n}；输入 @ 插入";
    }

    public static string ReferenceVideoPromptTagHint(int count)
    {
        var n = Math.Max(1, count);
        if (n == 1) return "用 <Video 1> 对应参考视频；输入 @ 插入";
        var tags = string.Join("、", Enumerable.Range(1, n).Select(i => $"<Video {i}>"));
        return $"用 {tags} 对应视频 1–{n}；输入 @ 插入";
    }

    public static string ReferenceAudioPromptTagHint(int count)
    {
        var n = Math.Max(1, count);
        if (n == 1) return "用 <Audio 1> 对应参考音频；输入 @ 插入";
        var tags = string.Join("、", Enumerable.Range(1, n).Select(i => $"<Audio {i}>"));
        return $"用 {tags} 对应音频 1–{n}；输入 @ 插入";
    }

    public static IReadOnlyList<ReferenceAddAction> ReferenceAddActions(
        int imageCount, int imageMax,
        int videoCount, int videoMax,
        int audioCount, int audioMax)
    {
        var actions = new List<ReferenceAddAction>(3);
        if (imageMax > 0 && imageCount < imageMax)
            actions.Add(new("image", "添加图", "添加参考图", "一次可选多张参考图"));
        if (videoMax > 0 && videoCount < videoMax)
            actions.Add(new("video", "添加视频", "添加参考视频", "一次可选多段参考视频"));
        if (audioMax > 0 && audioCount < audioMax)
            actions.Add(new("audio", "添加音频", "添加参考音频", "一次可选多段参考音频"));
        return actions;
    }

    public static string ReferenceFamilyPromptTagHint(int imageCount, int videoCount, int audioCount)
    {
        var parts = new List<string>();
        if (imageCount > 0)
            parts.Add(StripInsertHintSuffix(ReferencePromptTagHint(imageCount)));
        if (videoCount > 0)
            parts.Add(StripInsertHintSuffix(ReferenceVideoPromptTagHint(videoCount)));
        if (audioCount > 0)
            parts.Add(StripInsertHintSuffix(ReferenceAudioPromptTagHint(audioCount)));
        if (parts.Count == 0)
            return "用 <Picture N>、<Video N>、<Audio N> 引用参考素材；输入 @ 插入";
        return string.Join("；", parts) + "；输入 @ 插入";
    }

    private static string StripInsertHintSuffix(string hint)
    {
        const string suffix = "；输入 @ 插入";
        return hint.EndsWith(suffix, StringComparison.Ordinal)
            ? hint[..^suffix.Length]
            : hint;
    }

    public static string PromptPlaceholder(VideoConditioningMode mode) => mode switch
    {
        VideoConditioningMode.FirstFrame =>
            "描述从首帧开始的运动、镜头和环境声音",
        VideoConditioningMode.FirstLastFrame =>
            "描述从首帧到尾帧的过渡、镜头和环境声音",
        VideoConditioningMode.LastFrame =>
            "描述如何从合理前态落到尾帧，以及镜头和环境声音",
        VideoConditioningMode.SingleReferenceImage
            or VideoConditioningMode.ReferenceVideo
            or VideoConditioningMode.ReferenceAudio =>
            "描述画面；用 <Picture N> / <Video N> / <Audio N> 引用已上传的参考素材",
        _ => "描述画面、动作、镜头和环境声音",
    };
}

/// <summary>Local media paths selected in the UI before upload to ComfyUI.</summary>
public sealed record VideoMediaInputs(
    VideoConditioningMode Mode,
    string? FirstFramePath = null,
    string? LastFramePath = null,
    string? ReferenceImagePath = null,
    string RefImageSize = "match",
    IReadOnlyList<string>? ReferenceImagePaths = null,
    IReadOnlyList<string>? ReferenceVideoPaths = null,
    IReadOnlyList<string>? ReferenceAudioPaths = null)
{
    public static VideoMediaInputs TextOnly { get; } = new(VideoConditioningMode.Text);

    public IReadOnlyList<string> ResolvedReferenceImages()
        => ResolvePaths(ReferenceImagePaths, ReferenceImagePath);

    public IReadOnlyList<string> ResolvedReferenceVideos()
        => ResolvePaths(ReferenceVideoPaths, null);

    public IReadOnlyList<string> ResolvedReferenceAudios()
        => ResolvePaths(ReferenceAudioPaths, null);

    /// <summary>Append incoming local paths onto the current list, drop blanks/duplicates, and cap at <paramref name="max"/>.</summary>
    public static IReadOnlyList<string> MergeReferencePaths(
        IEnumerable<string>? current,
        IEnumerable<string>? incoming,
        int max)
    {
        var cap = Math.Max(0, max);
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in (current ?? []).Concat(incoming ?? []))
        {
            if (merged.Count >= cap) break;
            if (string.IsNullOrWhiteSpace(path)) continue;
            var trimmed = path.Trim();
            if (!seen.Add(trimmed)) continue;
            merged.Add(trimmed);
        }
        return merged;
    }

    public IReadOnlyList<string> Validate(VideoMediaCapabilities capabilities)
    {
        var errors = new List<string>();
        var enabled = capabilities.EnabledModes();
        if (!enabled.Contains(Mode)
            && !(VideoMediaCapabilities.IsReferenceFamily(Mode)
                 && enabled.Contains(VideoConditioningMode.SingleReferenceImage)))
            errors.Add($"当前视频档案不支持模式：{VideoMediaCapabilities.DisplayName(Mode)}");

        switch (Mode)
        {
            case VideoConditioningMode.Text:
                break;
            case VideoConditioningMode.FirstFrame:
                if (!IsExistingImage(FirstFramePath))
                    errors.Add("首帧图生视频需要选择一张首帧图片。");
                break;
            case VideoConditioningMode.FirstLastFrame:
                if (!IsExistingImage(FirstFramePath))
                    errors.Add("首帧 + 尾帧模式需要选择首帧图片。");
                if (!IsExistingImage(LastFramePath))
                    errors.Add("首帧 + 尾帧模式需要选择尾帧图片。");
                break;
            case VideoConditioningMode.LastFrame:
                if (!IsExistingImage(LastFramePath))
                    errors.Add("尾帧图生视频需要选择一张尾帧图片。");
                break;
            case VideoConditioningMode.SingleReferenceImage:
            case VideoConditioningMode.ReferenceVideo:
            case VideoConditioningMode.ReferenceAudio:
                ValidateReferenceFamily(errors, capabilities);
                break;
        }

        return errors;
    }

    private void ValidateReferenceFamily(List<string> errors, VideoMediaCapabilities capabilities)
    {
        var images = ResolvedReferenceImages();
        var videos = ResolvedReferenceVideos();
        var audios = ResolvedReferenceAudios();
        if (images.Count == 0 && videos.Count == 0 && audios.Count == 0)
            errors.Add("参考模式至少需要一张图、一段视频或一段音频。");

        if (images.Count > 0)
        {
            if (capabilities.MaxReferenceImages < 1)
                errors.Add("当前档案未开放参考图。");
            if (images.Any(path => !IsExistingImage(path)))
                errors.Add("有参考图无效或已不存在。");
            if (images.Count > capabilities.MaxReferenceImages)
                errors.Add($"参考图最多 {capabilities.MaxReferenceImages} 张。");
            if (!string.Equals(RefImageSize, "match", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(RefImageSize, "max", StringComparison.OrdinalIgnoreCase))
                errors.Add("参考图尺寸模式必须是 match 或 max。");
        }

        if (videos.Count > 0)
        {
            if (capabilities.MaxReferenceVideos < 1)
                errors.Add("当前档案未开放参考视频。");
            if (videos.Any(path => !IsExistingVideo(path)))
                errors.Add("有参考视频无效或已不存在。");
            if (videos.Count > capabilities.MaxReferenceVideos)
                errors.Add($"参考视频最多 {capabilities.MaxReferenceVideos} 个。");
        }

        if (audios.Count > 0)
        {
            if (capabilities.MaxReferenceAudios < 1)
                errors.Add("当前档案未开放参考音频。");
            if (audios.Any(path => !IsExistingAudio(path)))
                errors.Add("有参考音频无效或已不存在。");
            if (audios.Count > capabilities.MaxReferenceAudios)
                errors.Add($"参考音频最多 {capabilities.MaxReferenceAudios} 个。");
        }
    }

    private static IReadOnlyList<string> ResolvePaths(IReadOnlyList<string>? paths, string? fallback)
    {
        if (paths is { Count: > 0 })
            return paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        return string.IsNullOrWhiteSpace(fallback) ? [] : [fallback];
    }

    private static bool IsExistingImage(string? path)
        => IsExistingFile(path, [".png", ".jpg", ".jpeg", ".webp", ".bmp"]);

    private static bool IsExistingVideo(string? path)
        => IsExistingFile(path, [".mp4", ".webm", ".mov", ".mkv", ".avi"]);

    private static bool IsExistingAudio(string? path)
        => IsExistingFile(path, [".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aac"]);

    private static bool IsExistingFile(string? path, string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var ext = Path.GetExtension(path);
        return extensions.Any(candidate => ext.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
