using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;

namespace QwenLocalChat.Core;

public sealed record LocalModelOptions(
    Uri HealthUri,
    Uri ChatCompletionsUri,
    string ServerExecutable,
    string ModelFile,
    string LogFile,
    int Port,
    string ModelAlias,
    int ContextSize,
    int GpuLayers,
    bool ReasoningEnabled,
    bool UseJinja,
    int ParallelSlots,
    int StartupTimeoutSeconds,
    string BindHost = "127.0.0.1",
    string StartLockFile = "",
    string LifecycleLogFile = "",
    string ConfigSha256 = "",
    bool VerifyServiceIdentity = false);

public sealed record ModelAvailability(bool Reused, int? OwnedProcessId);

public interface IModelProcessLauncher
{
    IOwnedModelProcess Start(LocalModelOptions options);
}

public interface IOwnedModelProcess : IAsyncDisposable
{
    int Id { get; }
    bool HasExited { get; }
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class QwenServiceManager(
    LocalModelOptions options,
    IModelProcessLauncher launcher,
    HttpClient? httpClient = null) : IAsyncDisposable
{
    private readonly HttpClient _http = httpClient ?? new HttpClient(new SocketsHttpHandler { UseProxy = false });
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private IOwnedModelProcess? _ownedProcess;

    public bool OwnsModel => _ownedProcess is { HasExited: false };
    public int? OwnedProcessId => OwnsModel ? _ownedProcess!.Id : null;

    /// <summary>
    /// Live per-request context window from the server (after start/reuse). Falls back to the
    /// configured <see cref="LocalModelOptions.ContextSize"/> when /props is unavailable.
    /// With --kv-unified this is typically the shared pool size; without it, often ctx/parallel.
    /// </summary>
    public int EffectiveContextSize { get; private set; } = options.ContextSize;

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _http.GetAsync(options.HealthUri, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<ModelAvailability> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (await IsHealthyTargetAsync(cancellationToken))
            {
                await RefreshEffectiveContextSizeAsync(cancellationToken);
                await ModelServiceLifecycleLog.AppendAsync(options, "reuse", "qwen_local_start", "success", OwnedProcessId, cancellationToken: cancellationToken);
                return new ModelAvailability(_ownedProcess is null, OwnedProcessId);
            }

            if (_ownedProcess is { HasExited: true })
            {
                await _ownedProcess.DisposeAsync();
                _ownedProcess = null;
            }
            if (_ownedProcess is null && string.IsNullOrWhiteSpace(options.StartLockFile) && await IsPortOccupiedAsync(cancellationToken))
                throw new InvalidOperationException($"端口 {options.BindHost}:{options.Port} 已被占用，但没有返回健康且身份匹配的 Qwen 服务；已停止自动启动，未终止占用端口的进程");

            ModelServiceStartLock? crossProcessLock = null;
            if (_ownedProcess is null && !string.IsNullOrWhiteSpace(options.StartLockFile))
            {
                crossProcessLock = await ModelServiceStartLock.AcquireAsync(
                    options.StartLockFile,
                    "qwen-local",
                    options.ConfigSha256,
                    TimeSpan.FromSeconds(options.StartupTimeoutSeconds),
                    IsHealthyTargetAsync,
                    cancellationToken);
                if (crossProcessLock is null)
                {
                    await RefreshEffectiveContextSizeAsync(cancellationToken);
                    await ModelServiceLifecycleLog.AppendAsync(options, "reuse", "qwen_local_start", "success", cancellationToken: cancellationToken);
                    return new ModelAvailability(true, null);
                }
            }

            await using (crossProcessLock)
            {
                if (await IsHealthyTargetAsync(cancellationToken))
                {
                    await RefreshEffectiveContextSizeAsync(cancellationToken);
                    await ModelServiceLifecycleLog.AppendAsync(options, "reuse", "qwen_local_start", "success", cancellationToken: cancellationToken);
                    return new ModelAvailability(true, null);
                }
                if (_ownedProcess is null && await IsPortOccupiedAsync(cancellationToken))
                    throw new InvalidOperationException($"端口 {options.BindHost}:{options.Port} 在取得启动锁后被未知进程占用；已拒绝启动");
                _ownedProcess ??= launcher.Start(options);

                var deadline = DateTimeOffset.UtcNow.AddSeconds(options.StartupTimeoutSeconds);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_ownedProcess.HasExited)
                        throw new InvalidOperationException($"Qwen 服务进程已退出，请查看日志：{options.LogFile}");
                    if (await IsHealthyTargetAsync(cancellationToken))
                    {
                        await RefreshEffectiveContextSizeAsync(cancellationToken);
                        await ModelServiceLifecycleLog.AppendAsync(options, "start", "qwen_local_start", "success", _ownedProcess.Id, cancellationToken: cancellationToken);
                        return new ModelAvailability(false, _ownedProcess.Id);
                    }
                    await Task.Delay(250, cancellationToken);
                }
                throw new TimeoutException($"Qwen 服务在 {options.StartupTimeoutSeconds} 秒内未就绪，请查看日志：{options.LogFile}");
            }
        }
        catch
        {
            await ModelServiceLifecycleLog.AppendAsync(options, "ensure", "qwen_local_start", "failed", OwnedProcessId, "ensure_failed", cancellationToken: CancellationToken.None);
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Read live n_ctx from llama-server /props (best-effort).</summary>
    public async Task RefreshEffectiveContextSizeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var propsUri = new Uri(options.HealthUri, "/props");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _http.GetAsync(propsUri, timeout.Token);
            if (!response.IsSuccessStatusCode) return;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (document.RootElement.TryGetProperty("default_generation_settings", out var settings)
                && settings.TryGetProperty("n_ctx", out var nCtx)
                && nCtx.TryGetInt32(out var value)
                && value >= 512)
            {
                EffectiveContextSize = value;
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            // Keep last known / configured value.
        }
    }

    private async Task<bool> IsPortOccupiedAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            await client.ConnectAsync(options.BindHost, options.Port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task StopOwnedAsync(string reason = "qwen_local_stop", CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_ownedProcess is null) return;
            var stoppedPid = _ownedProcess.Id;
            await _ownedProcess.StopAsync(cancellationToken);
            await _ownedProcess.DisposeAsync();
            _ownedProcess = null;
            await ModelServiceLifecycleLog.AppendAsync(options, "stop", reason, "success", stoppedPid, cancellationToken: cancellationToken);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>
    /// Stop the local model service: owned process first; if the port is still healthy
    /// (reused from a previous session), stop the listener only when it is our server exe.
    /// </summary>
    public async Task StopServiceAsync(CancellationToken cancellationToken = default)
    {
        await StopOwnedAsync("user_exit_stop", cancellationToken);
        if (!await IsHealthyAsync(cancellationToken))
            return;

        var pid = await TryFindListeningPidAsync(options.Port, cancellationToken);
        if (pid is null) return;

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            string? path = null;
            try { path = process.MainModule?.FileName; } catch { /* access denied / exited */ }
            if (string.IsNullOrWhiteSpace(path)) return;

            var expected = Path.GetFullPath(options.ServerExecutable);
            var actual = Path.GetFullPath(path);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                return;

            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync(cancellationToken);
            await ModelServiceLifecycleLog.AppendAsync(options, "stop", "user_exit_stop", "success", pid, cancellationToken: cancellationToken);
        }
        catch (ArgumentException)
        {
            // process already gone
        }
        catch (InvalidOperationException)
        {
            // process exited while we inspected it
        }
    }

    private async Task<bool> IsHealthyTargetAsync(CancellationToken cancellationToken)
    {
        if (!await IsHealthyAsync(cancellationToken)) return false;
        if (!options.VerifyServiceIdentity) return true;

        var pid = await TryFindListeningPidAsync(options.Port, cancellationToken);
        if (pid is null) return true; // health is real, but listener identity is partial
        string? actualPath = null;
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            actualPath = process.MainModule?.FileName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true; // access denied or race: reportable partial, never a reason to kill
        }
        if (string.IsNullOrWhiteSpace(actualPath)) return true;
        if (!string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(options.ServerExecutable), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"端口 {options.BindHost}:{options.Port} 的健康服务进程路径与共享配置不一致，拒绝复用");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _http.GetAsync(new Uri(options.HealthUri, "/v1/models"), timeout.Token);
            if (!response.IsSuccessStatusCode) return true;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return true;
            var aliases = data.EnumerateArray()
                .Where(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("id").GetString())
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .ToArray();
            if (aliases.Length > 0 && !aliases.Contains(options.ModelAlias, StringComparer.Ordinal))
                throw new InvalidOperationException($"端口 {options.BindHost}:{options.Port} 的模型别名与共享配置不一致，拒绝复用");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return true; // alias unavailable is partial after executable path matched
        }
        return true;
    }

    /// <summary>Best-effort PID lookup for a TCP listener on 127.0.0.1:port via netstat.</summary>
    internal static async Task<int?> TryFindListeningPidAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            // Example:  TCP    127.0.0.1:18135    0.0.0.0:0    LISTENING    12345
            var needle = $":{port}";
            foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (line.IndexOf(needle, StringComparison.Ordinal) < 0) continue;
                // Prefer loopback listeners for this app's fixed security boundary.
                if (line.IndexOf("127.0.0.1", StringComparison.Ordinal) < 0
                    && line.IndexOf("[::1]", StringComparison.Ordinal) < 0)
                    continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (int.TryParse(parts[^1], out var pid) && pid > 0)
                    return pid;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _startGate.Dispose();
        if (_ownedProcess is not null) return _ownedProcess.DisposeAsync();
        return ValueTask.CompletedTask;
    }
}
