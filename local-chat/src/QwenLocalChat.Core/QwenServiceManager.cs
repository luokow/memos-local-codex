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
    int Port = 18135,
    string ModelAlias = LocalChatSettings.DefaultModelAlias,
    int ContextSize = 8_192,
    int GpuLayers = 99,
    bool ReasoningEnabled = false,
    bool UseJinja = true,
    int ParallelSlots = 1,
    int StartupTimeoutSeconds = 120);

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
            if (await IsHealthyAsync(cancellationToken))
            {
                await RefreshEffectiveContextSizeAsync(cancellationToken);
                return new ModelAvailability(_ownedProcess is null, OwnedProcessId);
            }

            if (_ownedProcess is { HasExited: true })
            {
                await _ownedProcess.DisposeAsync();
                _ownedProcess = null;
            }
            if (_ownedProcess is null && await IsPortOccupiedAsync(cancellationToken))
                throw new InvalidOperationException($"端口 127.0.0.1:{options.Port} 已被占用，但没有返回健康的 Qwen 服务；已停止自动启动，未终止占用端口的进程");
            _ownedProcess ??= launcher.Start(options);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(options.StartupTimeoutSeconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_ownedProcess.HasExited)
                    throw new InvalidOperationException($"Qwen 服务进程已退出，请查看日志：{options.LogFile}");
                if (await IsHealthyAsync(cancellationToken))
                {
                    await RefreshEffectiveContextSizeAsync(cancellationToken);
                    return new ModelAvailability(false, _ownedProcess.Id);
                }
                await Task.Delay(250, cancellationToken);
            }
            throw new TimeoutException($"Qwen 服务在 {options.StartupTimeoutSeconds} 秒内未就绪，请查看日志：{options.LogFile}");
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
            await client.ConnectAsync("127.0.0.1", options.Port, timeout.Token);
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

    public async Task StopOwnedAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_ownedProcess is null) return;
            await _ownedProcess.StopAsync(cancellationToken);
            await _ownedProcess.DisposeAsync();
            _ownedProcess = null;
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
        await StopOwnedAsync(cancellationToken);
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
