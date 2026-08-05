using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QwenLocalChat.Core;

public static class ModelServiceLifecycleLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task AppendAsync(
        LocalModelOptions options,
        string action,
        string reason,
        string result,
        int? pid = null,
        string? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.LifecycleLogFile)) return;
        var record = new
        {
            schema_version = 1,
            event_id = Guid.NewGuid().ToString("N"),
            timestamp = DateTimeOffset.UtcNow,
            client = "qwen-local",
            action,
            reason,
            result,
            pid,
            port = options.Port,
            server_path_sha256 = PathHash(options.ServerExecutable),
            model_path_sha256 = PathHash(options.ModelFile),
            config_sha256 = options.ConfigSha256,
            error_code = errorCode,
        };
        var line = JsonSerializer.Serialize(record) + Environment.NewLine;
        Directory.CreateDirectory(Path.GetDirectoryName(options.LifecycleLogFile)
            ?? throw new InvalidOperationException("生命周期日志路径缺少目录"));
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(options.LifecycleLogFile, line, new UTF8Encoding(false), cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string PathHash(string path)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).ToLowerInvariant();
}
