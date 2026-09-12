using System.Text.Json;
using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

public sealed record TextModelProfile(string Id, string DisplayName, string Adapter, ModelServiceConfig Service);
public sealed record VideoGenerationCapabilities(
    int FramesPerSecond, int AlignmentMultiple, int AlignmentOffset, int MinimumDimension, int MaximumDimension, int DimensionStep,
    int MaximumPixels, int MinimumDurationSeconds, int MaximumDurationSeconds, int MinimumSteps, int MaximumSteps)
{
    public IReadOnlyList<string> ValidateDefinition()
    {
        var errors = new List<string>();
        if (FramesPerSecond is < 1 or > 240)
            errors.Add("视频档案帧率必须在 1 到 240 之间");
        if (AlignmentMultiple < 1)
            errors.Add("视频档案帧对齐倍数必须大于 0");
        else if (AlignmentOffset < 0 || AlignmentOffset >= AlignmentMultiple)
            errors.Add("视频档案帧对齐偏移必须小于对齐倍数");
        if (MinimumDimension < VideoGenerationSettings.MinimumDimension
            || MaximumDimension > VideoGenerationSettings.MaximumDimension
            || MinimumDimension > MaximumDimension)
            errors.Add("视频档案尺寸范围无效");
        if (DimensionStep < 1
            || (MinimumDimension > 0 && MinimumDimension % DimensionStep != 0)
            || (MaximumDimension > 0 && MaximumDimension % DimensionStep != 0))
            errors.Add("视频档案尺寸上下限必须与尺寸步进对齐");
        if (MaximumPixels < 1)
            errors.Add("视频档案最大像素数必须大于 0");
        if (MinimumDurationSeconds < 1
            || MaximumDurationSeconds > 300
            || MinimumDurationSeconds > MaximumDurationSeconds)
            errors.Add("视频档案时长范围无效");
        if (MinimumSteps < 1 || MaximumSteps > 1000 || MinimumSteps > MaximumSteps)
            errors.Add("视频档案步数范围无效");
        return errors;
    }

    public int AlignFrames(int durationSeconds)
    {
        var definitionErrors = ValidateDefinition();
        if (definitionErrors.Count > 0) throw new InvalidOperationException(string.Join("；", definitionErrors));
        var requested = Math.Max(0, durationSeconds) * FramesPerSecond;
        return requested + ((AlignmentOffset - requested % AlignmentMultiple + AlignmentMultiple) % AlignmentMultiple);
    }

    public IReadOnlyList<string> Validate(VideoGenerationSettings settings)
    {
        var errors = ValidateDefinition().ToList();
        if (errors.Count > 0) return errors;
        if (settings.Width < MinimumDimension || settings.Width > MaximumDimension || settings.Width % DimensionStep != 0)
            errors.Add($"当前模型宽度必须在 {MinimumDimension} 到 {MaximumDimension} 之间且按 {DimensionStep} 递增");
        if (settings.Height < MinimumDimension || settings.Height > MaximumDimension || settings.Height % DimensionStep != 0)
            errors.Add($"当前模型高度必须在 {MinimumDimension} 到 {MaximumDimension} 之间且按 {DimensionStep} 递增");
        if ((long)settings.Width * settings.Height > MaximumPixels)
            errors.Add($"当前模型视频像素不能超过 {MaximumPixels}");
        if (settings.DurationSeconds < MinimumDurationSeconds || settings.DurationSeconds > MaximumDurationSeconds)
            errors.Add($"当前模型视频时长必须在 {MinimumDurationSeconds} 到 {MaximumDurationSeconds} 秒之间");
        if (settings.Steps < MinimumSteps || settings.Steps > MaximumSteps)
            errors.Add($"当前模型视频步数必须在 {MinimumSteps} 到 {MaximumSteps} 之间");
        return errors;
    }
}
public sealed record VideoNodeInputBinding(string NodeId, string InputName);
public sealed record VideoNodeInputMapping(
    VideoNodeInputBinding? Prompt, VideoNodeInputBinding? Width, VideoNodeInputBinding? Height, VideoNodeInputBinding? Frames,
    VideoNodeInputBinding? Steps, VideoNodeInputBinding? Seed, VideoNodeInputBinding? Fps, VideoNodeInputBinding? OutputPrefix,
    VideoNodeInputBinding? Format = null, VideoNodeInputBinding? Codec = null,
    VideoNodeInputBinding? Cfg = null, VideoNodeInputBinding? SamplerName = null, VideoNodeInputBinding? Scheduler = null,
    VideoNodeInputBinding? Denoise = null, VideoNodeInputBinding? ShiftVideo = null, VideoNodeInputBinding? ShiftAudio = null,
    VideoNodeInputBinding? NegativePrompt = null);

/// <summary>Sampler / shift values applied at submit time. Defaults match the H3 workflow file.</summary>
public sealed record VideoWorkflowParameters
{
    [JsonPropertyName("cfg")]
    public double? Cfg { get; init; }

    [JsonPropertyName("samplerName")]
    public string? SamplerName { get; init; }

    [JsonPropertyName("scheduler")]
    public string? Scheduler { get; init; }

    [JsonPropertyName("denoise")]
    public double? Denoise { get; init; }

    [JsonPropertyName("shiftVideo")]
    public double? ShiftVideo { get; init; }

    [JsonPropertyName("shiftAudio")]
    public double? ShiftAudio { get; init; }

    [JsonPropertyName("negativePrompt")]
    public string? NegativePrompt { get; init; }

    public static VideoWorkflowParameters Empty { get; } = new();

    public static VideoWorkflowParameters MiniMaxH3Local8Gb { get; } = new()
    {
        Cfg = 1,
        SamplerName = "euler",
        Scheduler = "simple",
        Denoise = 1,
        ShiftVideo = 12,
        ShiftAudio = 3,
        NegativePrompt = "",
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Cfg is < 0 or > 100) errors.Add("CFG 必须在 0 到 100 之间");
        if (Denoise is < 0 or > 1) errors.Add("去噪强度必须在 0 到 1 之间");
        if (ShiftVideo is < 0.01 or > 100) errors.Add("视频 shift 必须在 0.01 到 100 之间");
        if (ShiftAudio is < 0.01 or > 100) errors.Add("音频 shift 必须在 0.01 到 100 之间");
        if (SamplerName is { Length: > 0 } && string.IsNullOrWhiteSpace(SamplerName))
            errors.Add("采样器名称不能为空白");
        if (Scheduler is { Length: > 0 } && string.IsNullOrWhiteSpace(Scheduler))
            errors.Add("调度器名称不能为空白");
        return errors;
    }
}

public sealed record VideoModelProfile(
    string Id, string DisplayName, string Adapter, VideoServiceConfiguration Service, string WorkflowPath,
    IReadOnlyList<string> RequiredNodeClasses, VideoGenerationCapabilities Capabilities, VideoNodeInputMapping InputMapping,
    VideoMediaCapabilities? Media = null, VideoWorkflowParameters? Workflow = null)
{
    /// <summary>Resolved media surface; older catalogs without Media stay text-only.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public VideoMediaCapabilities EffectiveMedia => Media ?? VideoMediaCapabilities.TextOnly;

    [System.Text.Json.Serialization.JsonIgnore]
    public VideoWorkflowParameters EffectiveWorkflow => Workflow ?? VideoWorkflowParameters.Empty;
}

public sealed record ModelProfileCatalog(int SchemaVersion, string DefaultId, IReadOnlyList<TextModelProfile> Profiles)
{
    public TextModelProfile Resolve(string? selectedId)
    {
        var id = string.IsNullOrWhiteSpace(selectedId) ? DefaultId : selectedId;
        return Profiles.SingleOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"不存在文本模型档案：{id}");
    }

    public TextModelProfile? FindHanhuaFillProfile(string? selectedId = null)
    {
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            var selected = Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, selectedId, StringComparison.Ordinal)
                || string.Equals(profile.Service.ModelAlias, selectedId, StringComparison.Ordinal));
            if (selected is not null)
                return selected;
        }
        foreach (var profile in Profiles)
        {
            if (LooksLikeHanhuaFill(profile.Id)
                || LooksLikeHanhuaFill(profile.DisplayName)
                || LooksLikeHanhuaFill(profile.Service.ModelAlias))
                return profile;
        }
        return null;
    }

    private static bool LooksLikeHanhuaFill(string? value)
    {
        var text = value ?? "";
        return text.Contains("galtransl", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sakura", StringComparison.OrdinalIgnoreCase);
    }

    public ModelProfileCatalog Replace(TextModelProfile updated)
    {
        var index = Profiles.ToList().FindIndex(profile => string.Equals(profile.Id, updated.Id, StringComparison.Ordinal));
        if (index < 0) throw new InvalidOperationException($"不存在文本模型档案：{updated.Id}");
        var profiles = Profiles.ToList();
        profiles[index] = updated;
        return this with { Profiles = profiles };
    }

    public ModelProfileCatalog AddOrReplace(TextModelProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id)) throw new InvalidOperationException("文本模型档案 id 不能为空。");
        if (Profiles.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != Profiles.Count)
            throw new InvalidOperationException("文本模型档案 id 必须唯一。");
        var profiles = Profiles.ToList();
        var index = profiles.FindIndex(item => string.Equals(item.Id, profile.Id, StringComparison.Ordinal));
        if (index >= 0) profiles[index] = profile;
        else profiles.Add(profile);
        return this with { Profiles = profiles };
    }
}

public static class TextModelProfileImport
{
    public static string FindSingleGguf(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new InvalidOperationException("请选择存在的文本模型目录。");
        var matches = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("所选目录未找到 GGUF 模型文件。"),
            _ => throw new InvalidOperationException("所选目录包含多个 GGUF 模型文件；请只保留要导入的一个文件。"),
        };
    }

    public static TextModelProfile CreateProfile(ModelProfileCatalog catalog, TextModelProfile serviceTemplate, string ggufPath)
    {
        var modelPath = FindExistingGguf(ggufPath);
        var baseId = ToId(Path.GetFileNameWithoutExtension(modelPath));
        var id = UniqueId(catalog.Profiles.Select(profile => profile.Id), baseId);
        return serviceTemplate with
        {
            Id = id,
            DisplayName = Path.GetFileNameWithoutExtension(modelPath),
            Service = serviceTemplate.Service with { ModelPath = modelPath, ModelAlias = id },
        };
    }

    private static string FindExistingGguf(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("导入的文本模型必须是存在的 GGUF 文件。");
        return Path.GetFullPath(path);
    }

    private static string ToId(string name)
    {
        var characters = name.Trim().ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        var normalized = new string(characters).Trim('-');
        while (normalized.Contains("--", StringComparison.Ordinal)) normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(normalized) ? "imported-model" : normalized;
    }

    private static string UniqueId(IEnumerable<string> existingIds, string baseId)
    {
        var known = existingIds.ToHashSet(StringComparer.Ordinal);
        if (!known.Contains(baseId)) return baseId;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";
            if (!known.Contains(candidate)) return candidate;
        }
    }
}

public sealed class ModelProfileCatalogStore(string projectRoot, string configPath)
{
    private readonly string _projectRoot = Path.GetFullPath(projectRoot);
    private readonly string _configPath = Path.GetFullPath(configPath);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };

    public ModelProfileCatalog Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_configPath));
        RequireFields(document.RootElement, "schema_version", "default_id", "profiles");
        RejectUnknown(document.RootElement, "schema_version", "default_id", "profiles");
        var schema = document.RootElement.GetProperty("schema_version").GetInt32();
        if (schema != 1) throw new InvalidOperationException("不支持的模型档案版本。");
        var defaultId = RequiredString(document.RootElement, "default_id");
        var profiles = new List<TextModelProfile>();
        foreach (var item in document.RootElement.GetProperty("profiles").EnumerateArray())
        {
            RequireFields(item, "id", "display_name", "adapter", "service");
            RejectUnknown(item, "id", "display_name", "adapter", "service");
            var adapter = RequiredString(item, "adapter");
            if (adapter != "llama.cpp-openai") throw new InvalidOperationException("不支持的文本档案适配器。");
            var service = JsonSerializer.Deserialize<ModelServiceConfig>(item.GetProperty("service").GetRawText(), Options)
                ?? throw new InvalidOperationException("文本档案服务为空。");
            var serviceErrors = service.Validate(checkFiles: false, _projectRoot);
            if (serviceErrors.Count > 0) throw new InvalidOperationException(string.Join("；", serviceErrors));
            profiles.Add(new(RequiredString(item, "id"), RequiredString(item, "display_name"), adapter, service));
        }
        if (profiles.Count == 0 || profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Count)
            throw new InvalidOperationException("文本档案必须非空且 id 唯一。");
        var catalog = new ModelProfileCatalog(schema, defaultId, profiles);
        _ = catalog.Resolve(defaultId);
        return catalog;
    }

    public void Save(ModelProfileCatalog catalog)
    {
        if (catalog.SchemaVersion != 1) throw new InvalidOperationException("不支持的模型档案版本。");
        if (catalog.Profiles.Count == 0
            || catalog.Profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Profiles.Count)
            throw new InvalidOperationException("文本档案必须非空且 id 唯一。");
        _ = catalog.Resolve(catalog.DefaultId);
        foreach (var profile in catalog.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.DisplayName))
                throw new InvalidOperationException("文本档案 id 和显示名称不能为空。");
            if (profile.Adapter != "llama.cpp-openai") throw new InvalidOperationException("不支持的文本档案适配器。");
            var errors = profile.Service.Validate(checkFiles: false, _projectRoot);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
        }

        var payload = new
        {
            schema_version = catalog.SchemaVersion,
            default_id = catalog.DefaultId,
            profiles = catalog.Profiles.Select(profile => new
            {
                id = profile.Id,
                display_name = profile.DisplayName,
                adapter = profile.Adapter,
                service = profile.Service,
            }),
        };
        var directory = Path.GetDirectoryName(_configPath) ?? throw new InvalidOperationException("模型档案路径缺少目录。");
        Directory.CreateDirectory(directory);
        var temporary = $"{_configPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, _configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string RequiredString(JsonElement item, string field) => item.GetProperty(field).GetString() is { Length: > 0 } value ? value : throw new InvalidOperationException($"档案字段 {field} 必须为非空字符串。");
    private static void RequireFields(JsonElement item, params string[] names) { if (item.ValueKind != JsonValueKind.Object || names.Any(name => !item.TryGetProperty(name, out _))) throw new InvalidOperationException("模型档案字段不完整。"); }
    private static void RejectUnknown(JsonElement item, params string[] names) { var allowed = names.ToHashSet(StringComparer.Ordinal); if (item.EnumerateObject().Any(property => !allowed.Contains(property.Name))) throw new InvalidOperationException("模型档案包含未知字段。"); }
}

public sealed record VideoModelProfileCatalog(int SchemaVersion, string DefaultId, IReadOnlyList<VideoModelProfile> Profiles)
{
    public VideoModelProfile Resolve(string? selectedId) => Profiles.SingleOrDefault(p => p.Id == (string.IsNullOrWhiteSpace(selectedId) ? DefaultId : selectedId)) ?? throw new InvalidOperationException("不存在视频模型档案。");

    public VideoModelProfileCatalog AddOrReplace(VideoModelProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id)) throw new InvalidOperationException("视频模型档案 id 不能为空。");
        if (Profiles.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != Profiles.Count)
            throw new InvalidOperationException("视频模型档案 id 必须唯一。");
        var profiles = Profiles.ToList();
        var index = profiles.FindIndex(item => string.Equals(item.Id, profile.Id, StringComparison.Ordinal));
        if (index >= 0) profiles[index] = profile;
        else profiles.Add(profile);
        return this with { Profiles = profiles };
    }
}

public static class VideoModelProfileImport
{
    public static VideoModelProfile CreateCompatibleProfile(VideoModelProfileCatalog catalog, VideoModelProfile template, string comfyUiRoot)
    {
        var root = ValidateComfyUiRoot(comfyUiRoot);
        var displayName = new DirectoryInfo(root).Name;
        var baseId = ToId(displayName);
        var id = UniqueId(catalog.Profiles.Select(profile => profile.Id), baseId);
        return template with
        {
            Id = id,
            DisplayName = displayName,
            Service = template.Service with
            {
                ComfyUiRoot = root,
                OutputDirectory = Path.Combine(root, "output"),
            },
        };
    }

    private static string ValidateComfyUiRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new InvalidOperationException("请选择存在的 ComfyUI 根目录。");
        var root = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(root, "main.py")) || !File.Exists(Path.Combine(root, ".venv", "Scripts", "python.exe")))
            throw new InvalidOperationException("所选目录缺少 main.py 或 .venv\\Scripts\\python.exe，不是可启动的 ComfyUI 部署。");
        return root;
    }

    private static string ToId(string name)
    {
        var characters = name.Trim().ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        var normalized = new string(characters).Trim('-');
        while (normalized.Contains("--", StringComparison.Ordinal)) normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(normalized) ? "imported-video-model" : normalized;
    }

    private static string UniqueId(IEnumerable<string> existingIds, string baseId)
    {
        var known = existingIds.ToHashSet(StringComparer.Ordinal);
        if (!known.Contains(baseId)) return baseId;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";
            if (!known.Contains(candidate)) return candidate;
        }
    }
}

public sealed class VideoModelProfileCatalogStore(string projectRoot, string configPath)
{
    private readonly string _projectRoot = Path.GetFullPath(projectRoot);
    private readonly string _configPath = Path.GetFullPath(configPath);
    public VideoModelProfileCatalog Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_configPath));
        Require(document.RootElement, "schema_version", "default_id", "profiles");
        var allowed = new HashSet<string>(["schema_version", "default_id", "profiles"], StringComparer.Ordinal);
        if (document.RootElement.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new InvalidOperationException("视频档案包含未知字段。");
        if (document.RootElement.GetProperty("schema_version").GetInt32() != 1) throw new InvalidOperationException("不支持的视频档案版本。");
        var defaultId = document.RootElement.GetProperty("default_id").GetString() ?? throw new InvalidOperationException("视频默认档案不能为空。");
        var profiles = JsonSerializer.Deserialize<List<VideoModelProfile>>(document.RootElement.GetProperty("profiles").GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("视频档案为空。");
        if (profiles.Count == 0 || profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Count) throw new InvalidOperationException("视频档案必须非空且 id 唯一。");
        foreach (var profile in profiles)
        {
            if (profile.Adapter != "comfyui-workflow") throw new InvalidOperationException("不支持的视频档案适配器。");
            profile.Service.Validate(_projectRoot);
            _ = ResolveWorkflowPath(profile.WorkflowPath);
            if (profile.RequiredNodeClasses.Count == 0 || profile.RequiredNodeClasses.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("视频档案必须声明所需节点。");
            var capabilityErrors = profile.Capabilities.ValidateDefinition()
                .Concat(profile.EffectiveMedia.ValidateDefinition())
                .Concat(profile.EffectiveWorkflow.Validate())
                .ToList();
            if (capabilityErrors.Count > 0) throw new InvalidOperationException(string.Join("；", capabilityErrors));
        }
        var catalog = new VideoModelProfileCatalog(1, defaultId, profiles);
        _ = catalog.Resolve(null);
        return catalog;
    }
    public void Save(VideoModelProfileCatalog catalog)
    {
        ValidateCatalog(catalog);
        var payload = new
        {
            schema_version = catalog.SchemaVersion,
            default_id = catalog.DefaultId,
            profiles = catalog.Profiles,
        };
        var directory = Path.GetDirectoryName(_configPath) ?? throw new InvalidOperationException("视频档案路径缺少目录。");
        Directory.CreateDirectory(directory);
        var temporary = $"{_configPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, _configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    public string ResolveWorkflowPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || Path.IsPathRooted(configuredPath)) throw new InvalidOperationException("视频工作流必须是项目内相对路径。");
        var full = Path.GetFullPath(Path.Combine(_projectRoot, configuredPath));
        if (!VideoServiceConfiguration.IsContained(_projectRoot, full)) throw new InvalidOperationException("视频工作流路径越出项目根目录。");
        return full;
    }
    private static void Require(JsonElement root, params string[] names) { if (root.ValueKind != JsonValueKind.Object || names.Any(name => !root.TryGetProperty(name, out _))) throw new InvalidOperationException("视频档案字段不完整。"); }
    private void ValidateCatalog(VideoModelProfileCatalog catalog)
    {
        if (catalog.SchemaVersion != 1 || catalog.Profiles.Count == 0 || catalog.Profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Profiles.Count)
            throw new InvalidOperationException("视频档案版本、内容或 id 无效。");
        _ = catalog.Resolve(catalog.DefaultId);
        foreach (var profile in catalog.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.DisplayName) || profile.Adapter != "comfyui-workflow")
                throw new InvalidOperationException("视频档案字段无效。");
            profile.Service.Validate(_projectRoot);
            _ = ResolveWorkflowPath(profile.WorkflowPath);
            if (profile.RequiredNodeClasses.Count == 0 || profile.RequiredNodeClasses.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("视频档案必须声明所需节点。");
            var capabilityErrors = profile.Capabilities.ValidateDefinition()
                .Concat(profile.EffectiveMedia.ValidateDefinition())
                .Concat(profile.EffectiveWorkflow.Validate())
                .ToList();
            if (capabilityErrors.Count > 0) throw new InvalidOperationException(string.Join("；", capabilityErrors));
        }
    }
}
