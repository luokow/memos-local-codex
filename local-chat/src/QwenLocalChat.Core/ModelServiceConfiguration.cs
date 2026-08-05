using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

public sealed record ModelServiceConfig
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("bind_host")]
    public string BindHost { get; init; } = "";

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("server_executable")]
    public string ServerExecutable { get; init; } = "";

    [JsonPropertyName("model_path")]
    public string ModelPath { get; init; } = "";

    [JsonPropertyName("model_alias")]
    public string ModelAlias { get; init; } = "";

    [JsonPropertyName("context_size")]
    public int ContextSize { get; init; }

    [JsonPropertyName("gpu_layers")]
    public int GpuLayers { get; init; }

    [JsonPropertyName("parallel_slots")]
    public int ParallelSlots { get; init; }

    [JsonPropertyName("reasoning_enabled")]
    public bool ReasoningEnabled { get; init; }

    [JsonPropertyName("use_jinja")]
    public bool UseJinja { get; init; }

    [JsonPropertyName("startup_timeout_seconds")]
    public int StartupTimeoutSeconds { get; init; }

    [JsonPropertyName("auto_start_on_demand")]
    public bool AutoStartOnDemand { get; init; }

    public string ResolveServerExecutable(string projectRoot) => ResolveProjectPath(projectRoot, ServerExecutable);
    public string ResolveModelPath(string projectRoot) => ResolveProjectPath(projectRoot, ModelPath);

    public LocalModelOptions ToLocalModelOptions(
        string projectRoot,
        string modelLogFile,
        string startLockFile = "",
        string lifecycleLogFile = "")
    {
        var host = BindHost == "::1" ? "[::1]" : BindHost;
        var endpoint = $"http://{host}:{Port}";
        return new LocalModelOptions(
            new Uri($"{endpoint}/health"),
            new Uri($"{endpoint}/v1/chat/completions"),
            ResolveServerExecutable(projectRoot),
            ResolveModelPath(projectRoot),
            modelLogFile,
            Port,
            ModelAlias,
            ContextSize,
            GpuLayers,
            ReasoningEnabled,
            UseJinja,
            ParallelSlots,
            StartupTimeoutSeconds,
            BindHost,
            startLockFile,
            lifecycleLogFile,
            ConfigSha256(),
            VerifyServiceIdentity: true);
    }

    public IReadOnlyList<string> Validate(bool checkFiles = true, string? projectRoot = null)
    {
        var errors = new List<string>();
        if (SchemaVersion != 1) errors.Add($"不支持的模型服务配置版本：{SchemaVersion}");
        if (BindHost is not ("127.0.0.1" or "::1")) errors.Add("模型服务只允许绑定回环地址 127.0.0.1 或 ::1");
        if (Port is < 1024 or > 65535) errors.Add("模型服务端口必须在 1024 到 65535 之间");
        if (string.IsNullOrWhiteSpace(ServerExecutable)) errors.Add("模型服务可执行文件不能为空");
        if (string.IsNullOrWhiteSpace(ModelPath)) errors.Add("模型文件不能为空");
        if (string.IsNullOrWhiteSpace(ModelAlias)) errors.Add("模型别名不能为空");
        if (ContextSize is < 2048 or > 32768) errors.Add("上下文窗口必须在 2048 到 32768 之间");
        if (GpuLayers is < 0 or > 999) errors.Add("GPU 层数必须在 0 到 999 之间");
        if (ParallelSlots is < 1 or > 8) errors.Add("并发槽位必须在 1 到 8 之间");
        if (StartupTimeoutSeconds is < 30 or > 600) errors.Add("启动等待秒数必须在 30 到 600 之间");
        if (checkFiles)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                errors.Add("校验模型服务文件时必须提供项目根目录");
            else
            {
                if (!string.IsNullOrWhiteSpace(ServerExecutable) && !File.Exists(ResolveServerExecutable(projectRoot)))
                    errors.Add($"找不到模型服务可执行文件：{ResolveServerExecutable(projectRoot)}");
                if (!string.IsNullOrWhiteSpace(ModelPath) && !File.Exists(ResolveModelPath(projectRoot)))
                    errors.Add($"找不到模型文件：{ResolveModelPath(projectRoot)}");
            }
        }
        return errors;
    }

    public string ToCanonicalJson()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(this, ModelServiceConfigStore.JsonOptions));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string ConfigSha256()
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson()))).ToLowerInvariant();

    private static string ResolveProjectPath(string projectRoot, string configuredPath)
        => Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(projectRoot, configuredPath));

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

public sealed record ModelServiceConfigLoadResult(ModelServiceConfig Config, bool Migrated);

public sealed class ModelServiceConfigStore(string projectRoot, string configPath)
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "schema_version", "bind_host", "port", "server_executable", "model_path", "model_alias",
        "context_size", "gpu_layers", "parallel_slots", "reasoning_enabled", "use_jinja",
        "startup_timeout_seconds", "auto_start_on_demand",
    };

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
    };

    public string ProjectRoot { get; } = Path.GetFullPath(projectRoot);
    public string ConfigPath { get; } = Path.GetFullPath(configPath);

    public static string ResolveConfigPath(string projectRoot)
    {
        var overridePath = Environment.GetEnvironmentVariable("MODEL_SERVICE_CONFIG_PATH");
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(Path.GetFullPath(projectRoot), "runtime", "model-service.json")
            : Path.GetFullPath(overridePath);
    }

    public ModelServiceConfig Load()
    {
        if (!File.Exists(ConfigPath)) throw new FileNotFoundException("找不到共享模型服务配置", ConfigPath);
        try
        {
            var text = File.ReadAllText(ConfigPath, Encoding.UTF8);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("共享模型服务配置必须是 JSON 对象");
            var unknown = document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !AllowedFields.Contains(name))
                .ToArray();
            if (unknown.Length > 0) throw new JsonException($"共享模型服务配置包含未知字段：{string.Join(", ", unknown)}");
            var config = JsonSerializer.Deserialize<ModelServiceConfig>(text, JsonOptions)
                ?? throw new JsonException("共享模型服务配置内容为空");
            ThrowIfInvalid(config);
            return config;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"无法读取共享模型服务配置 {ConfigPath}：{error.Message}", error);
        }
    }

    public void Save(ModelServiceConfig config)
    {
        ThrowIfInvalid(config);
        var directory = Path.GetDirectoryName(ConfigPath) ?? throw new InvalidOperationException("共享配置路径缺少目录");
        Directory.CreateDirectory(directory);
        var temporary = $"{ConfigPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, JsonOptions) + Environment.NewLine);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, ConfigPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public ModelServiceConfigLoadResult LoadOrMigrate(string legacySettingsPath)
    {
        if (File.Exists(ConfigPath)) return new(Load(), false);
        if (!File.Exists(legacySettingsPath))
            throw new FileNotFoundException("共享模型服务配置不存在，且没有可迁移的 Qwen Local 设置", legacySettingsPath);

        using var document = JsonDocument.Parse(File.ReadAllText(legacySettingsPath, Encoding.UTF8));
        var root = document.RootElement;
        var config = new ModelServiceConfig
        {
            SchemaVersion = 1,
            BindHost = "127.0.0.1",
            Port = RequiredInt(root, "port"),
            ServerExecutable = FindLegacyServerExecutable(),
            ModelPath = RequiredString(root, "model_path"),
            ModelAlias = RequiredString(root, "model_alias"),
            ContextSize = RequiredInt(root, "context_size"),
            GpuLayers = RequiredInt(root, "gpu_layers"),
            ParallelSlots = RequiredInt(root, "parallel_slots"),
            ReasoningEnabled = RequiredBool(root, "reasoning_enabled"),
            UseJinja = RequiredBool(root, "use_jinja"),
            StartupTimeoutSeconds = RequiredInt(root, "startup_timeout_seconds"),
            AutoStartOnDemand = true,
        };
        Save(config);
        return new(Load(), true);
    }

    private string FindLegacyServerExecutable()
    {
        var expected = Path.Combine(ProjectRoot, "llama", "bin", "llama-server.exe");
        if (!File.Exists(expected)) throw new FileNotFoundException("迁移时找不到现有 llama-server.exe", expected);
        return Path.GetRelativePath(ProjectRoot, expected);
    }

    private void ThrowIfInvalid(ModelServiceConfig config)
    {
        var errors = config.Validate(checkFiles: true, ProjectRoot);
        if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors), nameof(config));
    }

    private static string RequiredString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new JsonException($"旧设置缺少有效字段：{name}");

    private static int RequiredInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw new JsonException($"旧设置缺少有效字段：{name}");

    private static bool RequiredBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new JsonException($"旧设置缺少有效字段：{name}");
}
