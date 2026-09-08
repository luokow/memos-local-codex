using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace QwenLocalChat.Core;

public static class SettingsNumberInput
{
    public static int Whole(string? visibleText, double committedValue)
        => (int)Math.Round(Decimal(visibleText, committedValue), MidpointRounding.AwayFromZero);

    public static double Decimal(string? visibleText, double committedValue)
    {
        if (!string.IsNullOrWhiteSpace(visibleText)
            && (double.TryParse(visibleText, NumberStyles.Float, CultureInfo.CurrentCulture, out var current)
                || double.TryParse(visibleText, NumberStyles.Float, CultureInfo.InvariantCulture, out current)))
            return current;
        return committedValue;
    }

    /// <summary>
    /// Temperature uses 0.1 steps. Round so spin/save never keep binary float noise (0.7000000001).
    /// </summary>
    public static double Temperature(double value)
        => Math.Clamp(Math.Round(value, 1, MidpointRounding.AwayFromZero), 0, 2);

    public static double Temperature(string? visibleText, double committedValue)
        => Temperature(Decimal(visibleText, committedValue));

    /// <summary>OpenAI-style penalties use 0.05 steps in the 0..2 range.</summary>
    public static double OpenAiPenalty(double value)
        => Math.Clamp(Math.Round(value * 20, MidpointRounding.AwayFromZero) / 20, 0, 2);

    public static double OpenAiPenalty(string? visibleText, double committedValue)
        => OpenAiPenalty(Decimal(visibleText, committedValue));

    /// <summary>llama.cpp repeat_penalty: 1.0 = off; steps of 0.01 in 1.0..2.0.</summary>
    public static double RepeatPenalty(double value)
        => Math.Clamp(Math.Round(value, 2, MidpointRounding.AwayFromZero), 1.0, 2.0);

    public static double RepeatPenalty(string? visibleText, double committedValue)
        => RepeatPenalty(Decimal(visibleText, committedValue));

    /// <summary>DRY multiplier: 0 = off; 0.05 steps up to 5.</summary>
    public static double DryMultiplier(double value)
        => Math.Clamp(Math.Round(value * 20, MidpointRounding.AwayFromZero) / 20, 0, 5);

    public static double DryMultiplier(string? visibleText, double committedValue)
        => DryMultiplier(Decimal(visibleText, committedValue));
}

/// <summary>Persistent video request settings. Model-specific limits come from the selected video profile.</summary>
public sealed record VideoGenerationSettings
{
    public const int MinimumDimension = 64;
    public const int MaximumDimension = 4096;

    [JsonPropertyName("width")]
    public int Width { get; init; } = 864;

    [JsonPropertyName("height")]
    public int Height { get; init; } = 480;

    [JsonPropertyName("duration_seconds")]
    public int DurationSeconds { get; init; } = 10;

    [JsonPropertyName("steps")]
    public int Steps { get; init; } = 20;

    [JsonPropertyName("seed")]
    public long Seed { get; init; }

    [JsonPropertyName("random_seed")]
    public bool RandomSeed { get; init; } = true;

    [JsonPropertyName("output_format")]
    public string OutputFormat { get; init; } = "mp4";

    [JsonPropertyName("video_codec")]
    public string VideoCodec { get; init; } = "auto";

    public static VideoGenerationSettings SafeDefaults { get; } = new();

    public VideoGenerationSettings Normalized()
        => this with
        {
            Width = Math.Clamp(Width, MinimumDimension, MaximumDimension),
            Height = Math.Clamp(Height, MinimumDimension, MaximumDimension),
            DurationSeconds = Math.Clamp(DurationSeconds, 1, 300),
            Steps = Math.Clamp(Steps, 1, 1000),
            Seed = Math.Clamp(Seed, 0, int.MaxValue),
            OutputFormat = string.IsNullOrWhiteSpace(OutputFormat) ? "mp4" : OutputFormat.Trim().ToLowerInvariant(),
            VideoCodec = string.IsNullOrWhiteSpace(VideoCodec) ? "auto" : VideoCodec.Trim().ToLowerInvariant(),
        };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Width is < MinimumDimension or > MaximumDimension) errors.Add("视频宽度必须在 64 到 4096 之间");
        if (Height is < MinimumDimension or > MaximumDimension) errors.Add("视频高度必须在 64 到 4096 之间");
        if (DurationSeconds is < 1 or > 300) errors.Add("视频时长必须在 1 到 300 秒之间");
        if (Steps is < 1 or > 1000) errors.Add("视频步数必须在 1 到 1000 之间");
        if (Seed is < 0 or > int.MaxValue) errors.Add("固定种子必须在 0 到 2147483647 之间");
        if (OutputFormat is not ("auto" or "mp4")) errors.Add("视频输出格式必须是自动或 MP4");
        if (VideoCodec is not ("auto" or "h264")) errors.Add("视频编码必须是自动或 H.264");
        return errors;
    }
}

/// <summary>
/// Optional values for one video job. Applying them creates a new complete request and never
/// mutates or persists the global defaults supplied by the caller.
/// </summary>
public sealed record VideoGenerationOverrides
{
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationSeconds { get; init; }
    public int? Steps { get; init; }
    public long? Seed { get; init; }
    public bool? RandomSeed { get; init; }

    public VideoGenerationSettings ApplyTo(VideoGenerationSettings defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return defaults with
        {
            Width = Width ?? defaults.Width,
            Height = Height ?? defaults.Height,
            DurationSeconds = DurationSeconds ?? defaults.DurationSeconds,
            Steps = Steps ?? defaults.Steps,
            Seed = Seed ?? defaults.Seed,
            RandomSeed = RandomSeed ?? defaults.RandomSeed,
        };
    }
}

public static class StartupModelSelection
{
    public const string None = "none";
    public const string Text = "text";
    public const string Video = "video";

    public static string Normalize(string? value)
        => value is None or Video ? value : Text;
}

public sealed record StartupModelPlan(
    string Mode,
    bool StartTextModel,
    bool StartVideoModel,
    string? ChatNotice)
{
    public static StartupModelPlan Resolve(string? selection)
        => StartupModelSelection.Normalize(selection) switch
        {
            StartupModelSelection.Video => new(StartupModelSelection.Video, false, true, null),
            StartupModelSelection.None => new(StartupModelSelection.None, false, false, "已按设置打开客户端，未自动启动模型。"),
            _ => new(StartupModelSelection.Text, true, false, "正在启动当前文本模型…"),
        };
}

public sealed record LocalChatSettings
{
    public const string DefaultModelAlias = "local-model";
    public const string DefaultModelPath = "";
    public const string DefaultHanhuaPackRoot = @"D:\grok\内嵌汉化";
    public const string DefaultHanhuaPythonExe = @"C:\Users\kow\AppData\Local\Programs\Python\Python312\python.exe";
    public const string DefaultHanhuaMitRoot = @"D:\grok\tools\manga-image-translator";

    // Neutral sampling by default so free-form quality stays natural.
    // Stronger anti-rep (freq/DRY/…) is opt-in via settings when long loops reappear.
    public const double DefaultFrequencyPenalty = 0;
    public const double DefaultPresencePenalty = 0;
    public const double DefaultRepeatPenalty = 1.0;
    public const int DefaultRepeatLastN = 64;
    public const double DefaultDryMultiplier = 0;
    public const double DefaultDryBase = 1.75;
    public const int DefaultDryAllowedLength = 2;
    public const int DefaultDryPenaltyLastN = -1;

    public LocalChatSettings() { }

    public LocalChatSettings(bool useMemos, bool saveChatLogs)
    {
        UseMemos = useMemos;
        SaveChatLogs = saveChatLogs;
    }

    [JsonPropertyName("use_memos")]
    public bool UseMemos { get; init; }

    [JsonPropertyName("save_chat_logs")]
    public bool SaveChatLogs { get; init; }

    [JsonPropertyName("enforce_text_video_model_exclusivity")]
    public bool EnforceTextVideoModelExclusivity { get; init; } = true;

    [JsonPropertyName("selected_text_profile_id")]
    public string? SelectedTextProfileId { get; init; }

    [JsonPropertyName("selected_video_profile_id")]
    public string? SelectedVideoProfileId { get; init; }

    [JsonPropertyName("startup_model")]
    public string StartupModel { get; init; } = StartupModelSelection.Text;

    /// <summary>Per-request generation cap.</summary>
    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; init; } = 4_096;

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = 0.7;

    /// <summary>OpenAI-style frequency penalty; reduces reuse of already-common tokens.</summary>
    [JsonPropertyName("frequency_penalty")]
    public double FrequencyPenalty { get; init; } = DefaultFrequencyPenalty;

    /// <summary>OpenAI-style presence penalty; mild push toward new tokens.</summary>
    [JsonPropertyName("presence_penalty")]
    public double PresencePenalty { get; init; } = DefaultPresencePenalty;

    /// <summary>llama.cpp repeat penalty; 1.0 disables.</summary>
    [JsonPropertyName("repeat_penalty")]
    public double RepeatPenalty { get; init; } = DefaultRepeatPenalty;

    /// <summary>How many recent tokens feed repeat_penalty (paragraph loops need &gt; 64).</summary>
    [JsonPropertyName("repeat_last_n")]
    public int RepeatLastN { get; init; } = DefaultRepeatLastN;

    /// <summary>DRY long-range n-gram penalty; 0 disables. Best lever against end-of-story loops.</summary>
    [JsonPropertyName("dry_multiplier")]
    public double DryMultiplier { get; init; } = DefaultDryMultiplier;

    [JsonPropertyName("stream_responses")]
    public bool StreamResponses { get; init; } = true;

    /// <summary>
    /// When true, prompts like「约4000字」shrink this turn's max_tokens toward the soft goal
    /// instead of always using the full settings cap. Off by default — models that handle
    /// long free-form well can keep the full budget.
    /// </summary>
    [JsonPropertyName("auto_tighten_output_tokens")]
    public bool AutoTightenOutputTokens { get; init; }

    /// <summary>
    /// When true, long soft goals (≥2000字) are split into ~800–1500字 multi-turn segments
    /// with a「下一段」button. Off by default so single-shot 4k is available when quality is OK.
    /// </summary>
    [JsonPropertyName("segmented_long_form")]
    public bool SegmentedLongForm { get; init; }

    /// <summary>
    /// When true, client trims paragraph/slogan loops and may stop streaming early if loops appear.
    /// Off by default — false positives cut normal long Chinese mid-story and feel “乱/断”.
    /// </summary>
    [JsonPropertyName("client_repetition_guard")]
    public bool ClientRepetitionGuard { get; init; }

    [JsonPropertyName("request_timeout_seconds")]
    public int RequestTimeoutSeconds { get; init; } = 900;

    [JsonIgnore]
    public int ContextSize { get; init; }

    [JsonPropertyName("max_history_rounds")]
    public int MaxHistoryRounds { get; init; } = 40;

    [JsonIgnore]
    public int GpuLayers { get; init; }

    [JsonIgnore]
    public int ParallelSlots { get; init; }

    [JsonIgnore]
    public bool ReasoningEnabled { get; init; }

    [JsonIgnore]
    public bool UseJinja { get; init; }

    [JsonIgnore]
    public string ModelAlias { get; init; } = DefaultModelAlias;

    [JsonIgnore]
    public string ModelPath { get; init; } = DefaultModelPath;

    [JsonPropertyName("memos_top_k")]
    public int MemosTopK { get; init; } = 5;

    [JsonIgnore]
    public int Port { get; init; }

    [JsonIgnore]
    public int StartupTimeoutSeconds { get; init; }

    [JsonIgnore]
    public bool AutoStartOnDemand { get; init; }

    /// <summary>
    /// Whether the one-time first-run tips have already been shown.
    /// </summary>
    [JsonPropertyName("onboarding_seen")]
    public bool OnboardingSeen { get; init; }

    /// <summary>
    /// Session list order: <c>created</c> (newest created first) or <c>updated</c> (recently active first).
    /// Unknown values fall back to <c>created</c>.
    /// </summary>
    [JsonPropertyName("session_sort_mode")]
    public string SessionSortMode { get; init; } = SessionSortModes.Created;

    [JsonPropertyName("video_generation")]
    public VideoGenerationSettings VideoGeneration { get; init; } = VideoGenerationSettings.SafeDefaults;

    [JsonPropertyName("video_prompt_phrases")]
    public VideoPromptTemplatePhrases VideoPromptPhrases { get; init; } = VideoPromptTemplatePhrases.OfficialDefaults;

    /// <summary>Chat composer attachments. Default is text files only; other kinds are picker reservations.</summary>
    [JsonPropertyName("chat_attachments")]
    public ChatAttachmentPolicy ChatAttachments { get; init; } = ChatAttachmentPolicy.SafeDefaults;

    [JsonPropertyName("hanhua_pack_root")]
    public string HanhuaPackRoot { get; init; } = DefaultHanhuaPackRoot;

    [JsonPropertyName("hanhua_python_exe")]
    public string HanhuaPythonExe { get; init; } = DefaultHanhuaPythonExe;

    [JsonPropertyName("hanhua_mit_root")]
    public string HanhuaMitRoot { get; init; } = DefaultHanhuaMitRoot;

    /// <summary><c>local</c> or <c>aliyun</c>. Unknown values normalize to local.</summary>
    [JsonPropertyName("hanhua_engine")]
    public string HanhuaEngine { get; init; } = HanhuaEngineCodec.Local;

    public static LocalChatSettings SafeDefaults { get; } = new();

    public static LocalChatSettings ResetToDefaults() => SafeDefaults with { };

    public SessionSortMode ResolveSessionSortMode()
        => SessionSortModes.Parse(SessionSortMode);

    public TextModelProfile ResolveTextProfile(ModelProfileCatalog catalog)
        => catalog.Resolve(SelectedTextProfileId);

    public bool RequiresModelRestartComparedWith(LocalChatSettings other)
        => !string.Equals(SelectedTextProfileId, other.SelectedTextProfileId, StringComparison.Ordinal)
           || ContextSize != other.ContextSize
           || GpuLayers != other.GpuLayers
           || ParallelSlots != other.ParallelSlots
           || ReasoningEnabled != other.ReasoningEnabled
           || UseJinja != other.UseJinja
           || !string.Equals(ModelAlias, other.ModelAlias, StringComparison.Ordinal)
           || !string.Equals(ModelPath, other.ModelPath, StringComparison.OrdinalIgnoreCase)
           || Port != other.Port
           || StartupTimeoutSeconds != other.StartupTimeoutSeconds;

    /// <summary>
    /// Normalize values that the settings UI rounds so draft vs saved comparisons stay stable.
    /// </summary>
    public LocalChatSettings Normalized()
        => this with
        {
            Temperature = SettingsNumberInput.Temperature(Temperature),
            FrequencyPenalty = SettingsNumberInput.OpenAiPenalty(FrequencyPenalty),
            PresencePenalty = SettingsNumberInput.OpenAiPenalty(PresencePenalty),
            RepeatPenalty = SettingsNumberInput.RepeatPenalty(RepeatPenalty),
            DryMultiplier = SettingsNumberInput.DryMultiplier(DryMultiplier),
            StartupModel = StartupModelSelection.Normalize(StartupModel),
            VideoGeneration = (VideoGeneration ?? VideoGenerationSettings.SafeDefaults).Normalized(),
            VideoPromptPhrases = (VideoPromptPhrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults(),
            ChatAttachments = (ChatAttachments ?? ChatAttachmentPolicy.SafeDefaults).Normalized(),
            HanhuaPackRoot = CoalesceHanhuaPath(HanhuaPackRoot, DefaultHanhuaPackRoot),
            HanhuaPythonExe = CoalesceHanhuaPath(HanhuaPythonExe, DefaultHanhuaPythonExe),
            HanhuaMitRoot = CoalesceHanhuaPath(HanhuaMitRoot, DefaultHanhuaMitRoot),
            HanhuaEngine = HanhuaEngineCodec.ToJson(HanhuaEngineCodec.Parse(HanhuaEngine)),
        };

    public static string CoalesceHanhuaPath(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public bool IsSameAs(LocalChatSettings other)
    {
        var left = Normalized();
        var right = other.Normalized();
        return left.MaxOutputTokens == right.MaxOutputTokens
               && string.Equals(left.SelectedTextProfileId, right.SelectedTextProfileId, StringComparison.Ordinal)
               && string.Equals(left.SelectedVideoProfileId, right.SelectedVideoProfileId, StringComparison.Ordinal)
               && string.Equals(left.StartupModel, right.StartupModel, StringComparison.Ordinal)
               && left.Temperature == right.Temperature
               && left.FrequencyPenalty == right.FrequencyPenalty
               && left.PresencePenalty == right.PresencePenalty
               && left.RepeatPenalty == right.RepeatPenalty
               && left.RepeatLastN == right.RepeatLastN
               && left.DryMultiplier == right.DryMultiplier
               && left.StreamResponses == right.StreamResponses
               && left.AutoTightenOutputTokens == right.AutoTightenOutputTokens
               && left.SegmentedLongForm == right.SegmentedLongForm
               && left.ClientRepetitionGuard == right.ClientRepetitionGuard
               && left.RequestTimeoutSeconds == right.RequestTimeoutSeconds
               && left.ContextSize == right.ContextSize
               && left.MaxHistoryRounds == right.MaxHistoryRounds
               && left.GpuLayers == right.GpuLayers
               && left.ParallelSlots == right.ParallelSlots
               && left.ReasoningEnabled == right.ReasoningEnabled
               && left.UseJinja == right.UseJinja
               && string.Equals(left.ModelAlias, right.ModelAlias, StringComparison.Ordinal)
               && string.Equals(left.ModelPath, right.ModelPath, StringComparison.OrdinalIgnoreCase)
               && left.MemosTopK == right.MemosTopK
               && left.Port == right.Port
               && left.StartupTimeoutSeconds == right.StartupTimeoutSeconds
               && left.AutoStartOnDemand == right.AutoStartOnDemand
               && left.UseMemos == right.UseMemos
               && left.SaveChatLogs == right.SaveChatLogs
               && left.EnforceTextVideoModelExclusivity == right.EnforceTextVideoModelExclusivity
               && left.OnboardingSeen == right.OnboardingSeen
               && string.Equals(
                   SessionSortModes.Normalize(left.SessionSortMode),
                   SessionSortModes.Normalize(right.SessionSortMode),
                   StringComparison.Ordinal)
               && left.VideoGeneration == right.VideoGeneration
               && left.VideoPromptPhrases == right.VideoPromptPhrases
               && left.ChatAttachments == right.ChatAttachments
               && string.Equals(left.HanhuaPackRoot, right.HanhuaPackRoot, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.HanhuaPythonExe, right.HanhuaPythonExe, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.HanhuaMitRoot, right.HanhuaMitRoot, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.HanhuaEngine, right.HanhuaEngine, StringComparison.Ordinal);
    }

    public bool HanhuaFieldsChanged(LocalChatSettings previous)
    {
        var left = Normalized();
        var right = previous.Normalized();
        return !string.Equals(left.HanhuaPackRoot, right.HanhuaPackRoot, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(left.HanhuaPythonExe, right.HanhuaPythonExe, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(left.HanhuaMitRoot, right.HanhuaMitRoot, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(left.HanhuaEngine, right.HanhuaEngine, StringComparison.Ordinal);
    }

    public string DescribeSaveResult(LocalChatSettings previous, bool modelOwnedByWindow)
    {
        var restart = RequiresModelRestartComparedWith(previous);
        var generationBits = DescribeGenerationFieldChanges(previous);
        var restartBits = DescribeRestartFieldChanges(previous);

        if (!restart)
        {
            if (generationBits.Count == 0)
            {
                if (HanhuaFieldsChanged(previous))
                    return "设置已保存。【即时】汉化路径或引擎已更新。";
                return "设置已保存。【即时】无生成参数变化；下次对话沿用当前值。";
            }
            return $"设置已保存。【即时生效，下次对话使用】{string.Join("、", generationBits)}。";
        }

        var genPart = generationBits.Count > 0
            ? $"生成参数（下次对话）：{string.Join("、", generationBits)}。"
            : "生成参数无变化。";
        var startPart = restartBits.Count > 0
            ? $"启动参数：{string.Join("、", restartBits)}。"
            : "启动参数已变更。";

        if (modelOwnedByWindow)
            return $"设置已保存。【已重启本窗口模型】{genPart}{startPart}";

        return $"设置已保存。【需重启模型】{genPart}{startPart}"
               + "请停止并重新启动模型后生效（关闭窗口时可选择是否停止服务）。";
    }

    /// <summary>Human labels for generation knobs that apply on the next chat turn (no process restart).</summary>
    public IReadOnlyList<string> DescribeGenerationFieldChanges(LocalChatSettings previous)
    {
        var left = Normalized();
        var right = previous.Normalized();
        var bits = new List<string>();
        if (left.MaxOutputTokens != right.MaxOutputTokens)
            bits.Add($"最大输出 {left.MaxOutputTokens}");
        if (left.Temperature != right.Temperature)
            bits.Add($"温度 {left.Temperature:0.#}");
        if (left.FrequencyPenalty != right.FrequencyPenalty)
            bits.Add("频率惩罚");
        if (left.PresencePenalty != right.PresencePenalty)
            bits.Add("存在惩罚");
        if (left.RepeatPenalty != right.RepeatPenalty)
            bits.Add("重复惩罚");
        if (left.RepeatLastN != right.RepeatLastN)
            bits.Add("重复检测窗口");
        if (left.DryMultiplier != right.DryMultiplier)
            bits.Add("DRY 抗重复");
        if (left.StreamResponses != right.StreamResponses)
            bits.Add(left.StreamResponses ? "流式开" : "流式关");
        if (left.AutoTightenOutputTokens != right.AutoTightenOutputTokens)
            bits.Add(left.AutoTightenOutputTokens ? "按字数收紧：开" : "按字数收紧：关");
        if (left.SegmentedLongForm != right.SegmentedLongForm)
            bits.Add(left.SegmentedLongForm ? "长文分段：开" : "长文分段：关");
        if (left.ClientRepetitionGuard != right.ClientRepetitionGuard)
            bits.Add(left.ClientRepetitionGuard ? "输出去重：开" : "输出去重：关");
        if (left.RequestTimeoutSeconds != right.RequestTimeoutSeconds)
            bits.Add($"超时 {left.RequestTimeoutSeconds}s");
        if (left.MaxHistoryRounds != right.MaxHistoryRounds)
            bits.Add("历史轮数");
        if (left.MemosTopK != right.MemosTopK)
            bits.Add("MemOS 召回");
        if (left.AutoStartOnDemand != right.AutoStartOnDemand)
            bits.Add(left.AutoStartOnDemand ? "按需启动：开" : "按需启动：关");
        if (left.EnforceTextVideoModelExclusivity != right.EnforceTextVideoModelExclusivity)
            bits.Add(left.EnforceTextVideoModelExclusivity ? "模型显存互斥：开" : "模型显存互斥：关");
        if (!string.Equals(left.StartupModel, right.StartupModel, StringComparison.Ordinal))
            bits.Add(left.StartupModel switch
            {
                StartupModelSelection.Video => "启动客户端时开启视频模型",
                StartupModelSelection.None => "启动客户端时不开启模型",
                _ => "启动客户端时开启文本模型",
            });
        if (!string.Equals(left.SelectedVideoProfileId, right.SelectedVideoProfileId, StringComparison.Ordinal))
            bits.Add("视频模型档案");
        if (!string.Equals(
                SessionSortModes.Normalize(left.SessionSortMode),
                SessionSortModes.Normalize(right.SessionSortMode),
                StringComparison.Ordinal))
            bits.Add($"列表{SessionSortModes.DisplayName(left.ResolveSessionSortMode())}");
        if (left.VideoGeneration.Width != right.VideoGeneration.Width || left.VideoGeneration.Height != right.VideoGeneration.Height)
            bits.Add($"视频分辨率 {left.VideoGeneration.Width}×{left.VideoGeneration.Height}");
        if (left.VideoGeneration.DurationSeconds != right.VideoGeneration.DurationSeconds)
            bits.Add($"视频时长 {left.VideoGeneration.DurationSeconds} 秒");
        if (left.VideoGeneration.Steps != right.VideoGeneration.Steps)
            bits.Add($"视频步数 {left.VideoGeneration.Steps}");
        if (left.VideoGeneration.RandomSeed != right.VideoGeneration.RandomSeed || left.VideoGeneration.Seed != right.VideoGeneration.Seed)
            bits.Add(left.VideoGeneration.RandomSeed ? "视频随机种子" : $"视频种子 {left.VideoGeneration.Seed}");
        if (left.ChatAttachments != right.ChatAttachments)
            bits.Add("聊天附件");
        if (left.VideoPromptPhrases != right.VideoPromptPhrases)
            bits.Add("视频模板套话");
        return bits;
    }

    /// <summary>Human labels for knobs that require stopping/restarting llama-server.</summary>
    public IReadOnlyList<string> DescribeRestartFieldChanges(LocalChatSettings previous)
    {
        var bits = new List<string>();
        if (!string.Equals(SelectedTextProfileId, previous.SelectedTextProfileId, StringComparison.Ordinal))
            bits.Add("文本模型档案");
        if (ContextSize != previous.ContextSize)
            bits.Add($"上下文 {ContextSize}");
        if (GpuLayers != previous.GpuLayers)
            bits.Add($"GPU 层 {GpuLayers}");
        if (ParallelSlots != previous.ParallelSlots)
            bits.Add($"并发槽 {ParallelSlots}");
        if (ReasoningEnabled != previous.ReasoningEnabled)
            bits.Add(ReasoningEnabled ? "思考：开" : "思考：关");
        if (UseJinja != previous.UseJinja)
            bits.Add(UseJinja ? "Jinja：开" : "Jinja：关");
        if (!string.Equals(ModelAlias, previous.ModelAlias, StringComparison.Ordinal))
            bits.Add("模型别名");
        if (!string.Equals(ModelPath, previous.ModelPath, StringComparison.OrdinalIgnoreCase))
            bits.Add("模型文件");
        if (Port != previous.Port)
            bits.Add($"端口 {Port}");
        if (StartupTimeoutSeconds != previous.StartupTimeoutSeconds)
            bits.Add("启动等待");
        return bits;
    }

    public string ResolveModelFile(string projectRoot)
        => System.IO.Path.IsPathRooted(ModelPath)
            ? System.IO.Path.GetFullPath(ModelPath)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, ModelPath));

    public LocalChatSettings WithModelService(ModelServiceConfig config)
        => this with
        {
            ContextSize = config.ContextSize,
            GpuLayers = config.GpuLayers,
            ParallelSlots = config.ParallelSlots,
            ReasoningEnabled = config.ReasoningEnabled,
            UseJinja = config.UseJinja,
            ModelAlias = config.ModelAlias,
            ModelPath = config.ModelPath,
            Port = config.Port,
            StartupTimeoutSeconds = config.StartupTimeoutSeconds,
            AutoStartOnDemand = config.AutoStartOnDemand,
        };

    public ModelServiceConfig ApplyToModelService(ModelServiceConfig current)
        => current with
        {
            ContextSize = ContextSize,
            GpuLayers = GpuLayers,
            ParallelSlots = ParallelSlots,
            ReasoningEnabled = ReasoningEnabled,
            UseJinja = UseJinja,
            ModelAlias = ModelAlias,
            ModelPath = ModelPath,
            Port = Port,
            StartupTimeoutSeconds = StartupTimeoutSeconds,
            AutoStartOnDemand = AutoStartOnDemand,
        };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        AddRangeError(errors, MaxOutputTokens, 128, 28_672, "最大输出 Token");
        if (Temperature is < 0 or > 2) errors.Add("温度必须在 0 到 2 之间");
        if (FrequencyPenalty is < 0 or > 2) errors.Add("频率惩罚必须在 0 到 2 之间");
        if (PresencePenalty is < 0 or > 2) errors.Add("存在惩罚必须在 0 到 2 之间");
        if (RepeatPenalty is < 1.0 or > 2.0) errors.Add("重复惩罚必须在 1.0 到 2.0 之间（1.0 为关闭）");
        AddRangeError(errors, RepeatLastN, 0, 2_048, "重复检测窗口");
        if (DryMultiplier is < 0 or > 5) errors.Add("DRY 抗重复必须在 0 到 5 之间（0 为关闭）");
        AddRangeError(errors, RequestTimeoutSeconds, 30, 1_800, "请求超时秒数");
        AddRangeError(errors, MaxHistoryRounds, 1, 100, "历史轮数");
        AddRangeError(errors, MemosTopK, 1, 20, "MemOS 召回数量");
        // Keep enough headroom for local generation throughput and first-token latency.
        var minTimeout = MaxOutputTokens / 20 + 60;
        if (RequestTimeoutSeconds < minTimeout)
            errors.Add($"请求超时过短：最大输出 {MaxOutputTokens} 时建议至少 {minTimeout} 秒，否则长文可能中途超时");
        errors.AddRange((VideoGeneration ?? VideoGenerationSettings.SafeDefaults).Validate());
        errors.AddRange((ChatAttachments ?? ChatAttachmentPolicy.SafeDefaults).Validate());
        return errors;
    }

    private static void AddRangeError(List<string> errors, int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            errors.Add($"{name}必须在 {minimum} 到 {maximum} 之间");
    }
}

/// <summary>Canonical string values for <see cref="LocalChatSettings.SessionSortMode"/>.</summary>
public static class SessionSortModes
{
    public const string Created = "created";
    public const string Updated = "updated";

    public static string Normalize(string? value)
        => string.Equals(value, Updated, StringComparison.OrdinalIgnoreCase) ? Updated : Created;

    public static SessionSortMode Parse(string? value)
        => string.Equals(value, Updated, StringComparison.OrdinalIgnoreCase)
            ? SessionSortMode.UpdatedDesc
            : SessionSortMode.CreatedDesc;

    public static string ToStorage(SessionSortMode mode)
        => mode == SessionSortMode.UpdatedDesc ? Updated : Created;

    public static string DisplayName(SessionSortMode mode)
        => mode == SessionSortMode.UpdatedDesc ? "按最近更新" : "按创建时间";
}

public sealed record SettingsLoadResult(LocalChatSettings Settings, string? Warning, string? CorruptBackupPath);

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string Path { get; } = path;

    public SettingsLoadResult Load()
    {
        if (!File.Exists(Path))
            return new(LocalChatSettings.SafeDefaults, null, null);

        try
        {
            var settings = JsonSerializer.Deserialize<LocalChatSettings>(File.ReadAllText(Path), JsonOptions)
                ?? throw new JsonException("设置文件内容为空");
            var errors = settings.Validate();
            if (errors.Count > 0) throw new JsonException(string.Join("；", errors));
            return new(settings with
            {
                StartupModel = StartupModelSelection.Normalize(settings.StartupModel),
                VideoGeneration = settings.VideoGeneration ?? VideoGenerationSettings.SafeDefaults,
                VideoPromptPhrases = (settings.VideoPromptPhrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults(),
                ChatAttachments = settings.ChatAttachments ?? ChatAttachmentPolicy.SafeDefaults,
                HanhuaPackRoot = LocalChatSettings.CoalesceHanhuaPath(settings.HanhuaPackRoot, LocalChatSettings.DefaultHanhuaPackRoot),
                HanhuaPythonExe = LocalChatSettings.CoalesceHanhuaPath(settings.HanhuaPythonExe, LocalChatSettings.DefaultHanhuaPythonExe),
                HanhuaMitRoot = LocalChatSettings.CoalesceHanhuaPath(settings.HanhuaMitRoot, LocalChatSettings.DefaultHanhuaMitRoot),
            }, null, null);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            var backup = $"{Path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(Path, backup, overwrite: false);
                return new(LocalChatSettings.SafeDefaults, $"设置文件损坏，已恢复安全默认值：{error.Message}", backup);
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
            {
                return new(LocalChatSettings.SafeDefaults, $"设置文件无法读取且备份失败：{backupError.Message}", null);
            }
        }
    }

    public void Save(LocalChatSettings settings)
    {
        var errors = settings.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors), nameof(settings));
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("设置路径缺少目录");
        Directory.CreateDirectory(directory);
        var temporary = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
