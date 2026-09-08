using System.Text.Json;
using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

public sealed record VideoServiceConfiguration(
    [property: JsonPropertyName("bindHost")] string BindHost,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("comfyUiRoot")] string ComfyUiRoot,
    [property: JsonPropertyName("outputDirectory")] string OutputDirectory)
{
    public Uri BaseUri => new($"http://{(BindHost == "::1" ? "[::1]" : BindHost)}:{Port}/");

    public void Validate(string projectRoot)
    {
        if (!string.Equals(BindHost, "127.0.0.1", StringComparison.Ordinal) && !string.Equals(BindHost, "::1", StringComparison.Ordinal))
            throw new InvalidOperationException("视频服务只能绑定回环地址。");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("视频服务端口无效。");
        if (string.IsNullOrWhiteSpace(ComfyUiRoot) || !Path.IsPathFullyQualified(ComfyUiRoot))
            throw new InvalidOperationException("ComfyUI 根目录必须是绝对路径。");
        if (string.IsNullOrWhiteSpace(OutputDirectory) || !Path.IsPathFullyQualified(OutputDirectory))
            throw new InvalidOperationException("视频输出目录必须是绝对路径。");
        if (!IsContained(ComfyUiRoot, OutputDirectory)) throw new InvalidOperationException("视频输出目录必须位于 ComfyUI 根目录内。");
    }

    public static bool IsContained(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class VideoServiceConfigurationStore(string projectRoot, string configPath)
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "bindHost", "port", "comfyUiRoot", "outputDirectory",
    };

    public VideoServiceConfiguration Load()
    {
        if (!File.Exists(configPath)) throw new InvalidOperationException($"视频服务配置不存在：{configPath}");
        var raw = File.ReadAllText(configPath);
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("视频服务配置必须是 JSON 对象。");
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!AllowedFields.Contains(property.Name))
                throw new InvalidOperationException($"视频服务配置包含未知字段：{property.Name}");
        }
        foreach (var required in AllowedFields)
        {
            if (!document.RootElement.TryGetProperty(required, out _))
                throw new InvalidOperationException($"视频服务配置缺少字段：{required}");
        }

        var config = JsonSerializer.Deserialize<VideoServiceConfiguration>(raw)
            ?? throw new InvalidOperationException("视频服务配置为空。");
        config.Validate(projectRoot);
        return config;
    }
}
