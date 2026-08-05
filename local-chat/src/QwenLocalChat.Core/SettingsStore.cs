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

public sealed record LocalChatSettings
{
    public const string DefaultModelAlias = "qwen3.5:9b-uncensored-local";
    public const string DefaultModelPath = @"models\Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q6_K.gguf";

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

    /// <summary>
    /// Defaults tuned for RTX 4070 Laptop 8GB + Qwen3.5 9B Q6_K
    /// (ctx 8192 loads ~7.1GB; higher ctx leaves little headroom on 8GB).
    /// </summary>
    /// <summary>
    /// Per-request generation cap. 4096 is the practical quality ceiling on 9B Q6;
    /// 8192+ often fills the budget with idiom/slogan tails on free-form long prose.
    /// </summary>
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

    [JsonPropertyName("context_size")]
    public int ContextSize { get; init; } = 8_192;

    [JsonPropertyName("max_history_rounds")]
    public int MaxHistoryRounds { get; init; } = 40;

    [JsonPropertyName("gpu_layers")]
    public int GpuLayers { get; init; } = 99;

    [JsonPropertyName("parallel_slots")]
    public int ParallelSlots { get; init; } = 1;

    [JsonPropertyName("reasoning_enabled")]
    public bool ReasoningEnabled { get; init; }

    [JsonPropertyName("use_jinja")]
    public bool UseJinja { get; init; } = true;

    [JsonPropertyName("model_alias")]
    public string ModelAlias { get; init; } = DefaultModelAlias;

    [JsonPropertyName("model_path")]
    public string ModelPath { get; init; } = DefaultModelPath;

    [JsonPropertyName("memos_top_k")]
    public int MemosTopK { get; init; } = 5;

    [JsonPropertyName("port")]
    public int Port { get; init; } = 18135;

    [JsonPropertyName("startup_timeout_seconds")]
    public int StartupTimeoutSeconds { get; init; } = 180;

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

    public static LocalChatSettings SafeDefaults { get; } = new();

    public static LocalChatSettings ResetToDefaults() => SafeDefaults with { };

    public SessionSortMode ResolveSessionSortMode()
        => SessionSortModes.Parse(SessionSortMode);

    public bool RequiresModelRestartComparedWith(LocalChatSettings other)
        => ContextSize != other.ContextSize
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
        };

    public bool IsSameAs(LocalChatSettings other)
    {
        var left = Normalized();
        var right = other.Normalized();
        return left.MaxOutputTokens == right.MaxOutputTokens
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
               && left.UseMemos == right.UseMemos
               && left.SaveChatLogs == right.SaveChatLogs
               && left.OnboardingSeen == right.OnboardingSeen
               && string.Equals(
                   SessionSortModes.Normalize(left.SessionSortMode),
                   SessionSortModes.Normalize(right.SessionSortMode),
                   StringComparison.Ordinal);
    }

    public string DescribeSaveResult(LocalChatSettings previous, bool modelOwnedByWindow)
    {
        var restart = RequiresModelRestartComparedWith(previous);
        var generationBits = DescribeGenerationFieldChanges(previous);
        var restartBits = DescribeRestartFieldChanges(previous);

        if (!restart)
        {
            if (generationBits.Count == 0)
                return "设置已保存。【即时】无生成参数变化；下次对话沿用当前值。";
            return $"设置已保存。【即时·下次对话生效】{string.Join("、", generationBits)}。";
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
            bits.Add(left.AutoTightenOutputTokens ? "按字数收紧·开" : "按字数收紧·关");
        if (left.SegmentedLongForm != right.SegmentedLongForm)
            bits.Add(left.SegmentedLongForm ? "长文分段·开" : "长文分段·关");
        if (left.ClientRepetitionGuard != right.ClientRepetitionGuard)
            bits.Add(left.ClientRepetitionGuard ? "输出去重·开" : "输出去重·关");
        if (left.RequestTimeoutSeconds != right.RequestTimeoutSeconds)
            bits.Add($"超时 {left.RequestTimeoutSeconds}s");
        if (left.MaxHistoryRounds != right.MaxHistoryRounds)
            bits.Add("历史轮数");
        if (left.MemosTopK != right.MemosTopK)
            bits.Add("MemOS 召回");
        if (!string.Equals(
                SessionSortModes.Normalize(left.SessionSortMode),
                SessionSortModes.Normalize(right.SessionSortMode),
                StringComparison.Ordinal))
            bits.Add($"列表{SessionSortModes.DisplayName(left.ResolveSessionSortMode())}");
        return bits;
    }

    /// <summary>Human labels for knobs that require stopping/restarting llama-server.</summary>
    public IReadOnlyList<string> DescribeRestartFieldChanges(LocalChatSettings previous)
    {
        var bits = new List<string>();
        if (ContextSize != previous.ContextSize)
            bits.Add($"上下文 {ContextSize}");
        if (GpuLayers != previous.GpuLayers)
            bits.Add($"GPU 层 {GpuLayers}");
        if (ParallelSlots != previous.ParallelSlots)
            bits.Add($"并发槽 {ParallelSlots}");
        if (ReasoningEnabled != previous.ReasoningEnabled)
            bits.Add(ReasoningEnabled ? "思考·开" : "思考·关");
        if (UseJinja != previous.UseJinja)
            bits.Add(UseJinja ? "Jinja·开" : "Jinja·关");
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
        AddRangeError(errors, ContextSize, 2_048, 32_768, "上下文窗口");
        AddRangeError(errors, MaxHistoryRounds, 1, 100, "历史轮数");
        AddRangeError(errors, GpuLayers, 0, 999, "GPU 层数");
        AddRangeError(errors, ParallelSlots, 1, 8, "并发槽位");
        AddRangeError(errors, MemosTopK, 1, 20, "MemOS 召回数量");
        AddRangeError(errors, Port, 1_024, 65_535, "服务端口");
        AddRangeError(errors, StartupTimeoutSeconds, 30, 600, "启动等待秒数");
        if (MaxOutputTokens > ContextSize - 512)
            errors.Add("最大输出 Token 必须至少为上下文窗口保留 512 Token 的输入空间");
        // Laptop 9B Q4 is often ~20–40 tok/s; require timeout ≥ max_tokens/20 + 60s headroom.
        var minTimeout = MaxOutputTokens / 20 + 60;
        if (RequestTimeoutSeconds < minTimeout)
            errors.Add($"请求超时过短：最大输出 {MaxOutputTokens} 时建议至少 {minTimeout} 秒，否则长文可能中途超时");
        if (string.IsNullOrWhiteSpace(ModelAlias)) errors.Add("模型别名不能为空");
        if (string.IsNullOrWhiteSpace(ModelPath)) errors.Add("模型文件不能为空");
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
            return new(settings, null, null);
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
