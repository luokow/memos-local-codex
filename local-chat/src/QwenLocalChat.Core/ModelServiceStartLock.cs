using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

public sealed record ModelStartLockDocument
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("owner_pid")]
    public int OwnerPid { get; init; }

    [JsonPropertyName("owner_client")]
    public string OwnerClient { get; init; } = "";

    [JsonPropertyName("acquired_at")]
    public DateTimeOffset AcquiredAt { get; init; }

    [JsonPropertyName("config_sha256")]
    public string ConfigSha256 { get; init; } = "";
}

public sealed class ModelServiceStartLock : IAsyncDisposable
{
    private static readonly HashSet<string> AllowedClients = new(StringComparer.Ordinal)
    {
        "qwen-local",
        "memos-codex",
    };

    private readonly string _path;
    private readonly string _ownedContent;
    private bool _released;

    private ModelServiceStartLock(string path, string ownedContent)
    {
        _path = path;
        _ownedContent = ownedContent;
    }

    public static async Task<ModelServiceStartLock?> AcquireAsync(
        string path,
        string ownerClient,
        string configSha256,
        TimeSpan timeout,
        Func<CancellationToken, Task<bool>> serviceHealthy,
        CancellationToken cancellationToken = default)
    {
        if (!AllowedClients.Contains(ownerClient)) throw new ArgumentException("未知的模型启动锁客户端", nameof(ownerClient));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("模型启动锁路径不能为空", nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("模型启动锁路径缺少目录"));
        var recoveryPath = $"{path}.recovery";
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(recoveryPath))
            {
                if (await serviceHealthy(cancellationToken)) return null;
                var observedRecovery = await TryReadAsync(recoveryPath, cancellationToken);
                if (observedRecovery is not null && !IsProcessAlive(observedRecovery.Value.Document.OwnerPid))
                {
                    var retiredDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(observedRecovery.Value.Raw))).ToLowerInvariant();
                    var retiredPath = $"{recoveryPath}.retired.{retiredDigest}";
                    try
                    {
                        File.Move(recoveryPath, retiredPath);
                        continue;
                    }
                    catch (IOException)
                    {
                    }
                }
                await Task.Delay(250, cancellationToken);
                continue;
            }
            var document = new ModelStartLockDocument
            {
                OwnerPid = Environment.ProcessId,
                OwnerClient = ownerClient,
                AcquiredAt = DateTimeOffset.UtcNow,
                ConfigSha256 = configSha256,
            };
            var content = JsonSerializer.Serialize(document);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                return new ModelServiceStartLock(path, content);
            }
            catch (IOException) when (File.Exists(path))
            {
                if (await serviceHealthy(cancellationToken)) return null;
                var observed = await TryReadAsync(path, cancellationToken);
                if (observed is not null && !IsProcessAlive(observed.Value.Document.OwnerPid))
                {
                    var recoveryContent = JsonSerializer.Serialize(new ModelStartLockDocument
                    {
                        OwnerPid = Environment.ProcessId,
                        OwnerClient = ownerClient,
                        AcquiredAt = DateTimeOffset.UtcNow,
                        ConfigSha256 = configSha256,
                    });
                    try
                    {
                        var recoveryBytes = Encoding.UTF8.GetBytes(recoveryContent);
                        using var recoveryStream = new FileStream(recoveryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        recoveryStream.Write(recoveryBytes);
                        recoveryStream.Flush(flushToDisk: true);
                    }
                    catch (IOException) when (File.Exists(recoveryPath))
                    {
                        await Task.Delay(250, cancellationToken);
                        continue;
                    }

                    try
                    {
                        var current = await TryReadAsync(path, cancellationToken);
                        if (current is not null
                            && string.Equals(observed.Value.Raw, current.Value.Raw, StringComparison.Ordinal)
                            && !IsProcessAlive(current.Value.Document.OwnerPid))
                        {
                            File.Delete(path);
                        }
                    }
                    finally
                    {
                        await DeleteIfOwnedAsync(recoveryPath, recoveryContent, cancellationToken);
                    }
                    continue;
                }
                await Task.Delay(250, cancellationToken);
            }
        }
        throw new TimeoutException($"等待模型启动锁超时：{path}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_released) return;
        _released = true;
        try
        {
            if (!File.Exists(_path)) return;
            var current = await File.ReadAllTextAsync(_path);
            if (string.Equals(current, _ownedContent, StringComparison.Ordinal)) File.Delete(_path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<(ModelStartLockDocument Document, string Raw)?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await File.ReadAllTextAsync(path, cancellationToken);
            var document = JsonSerializer.Deserialize<ModelStartLockDocument>(raw);
            return document is null || document.SchemaVersion != 1 ? null : (document, raw);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task DeleteIfOwnedAsync(string path, string ownedContent, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return;
            var current = await File.ReadAllTextAsync(path, cancellationToken);
            if (string.Equals(current, ownedContent, StringComparison.Ordinal)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
