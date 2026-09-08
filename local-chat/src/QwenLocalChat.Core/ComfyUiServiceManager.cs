using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace QwenLocalChat.Core;

public static class ComfyUiProcessIdentity
{
    public static IReadOnlySet<string> ExpectedExecutables(string venvPython)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(venvPython),
        };

        // Windows venv launchers can retain the same PID/start time while the image path
        // resolves to the base interpreter. Accept only the base recorded by this venv.
        var venvRoot = Directory.GetParent(Path.GetDirectoryName(venvPython)!)?.FullName;
        var configPath = venvRoot is null ? null : Path.Combine(venvRoot, "pyvenv.cfg");
        if (configPath is not null && File.Exists(configPath))
        {
            var homeLine = File.ReadLines(configPath)
                .FirstOrDefault(line => line.StartsWith("home", StringComparison.OrdinalIgnoreCase));
            if (homeLine is not null)
            {
                var separator = homeLine.IndexOf('=');
                if (separator >= 0)
                {
                    var home = homeLine[(separator + 1)..].Trim();
                    if (Path.IsPathFullyQualified(home))
                        expected.Add(Path.GetFullPath(Path.Combine(home, "python.exe")));
                }
            }
        }

        return expected;
    }

    public static bool MatchesExpectedExecutable(string executablePath, IReadOnlySet<string> expectedExecutables)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        try { return expectedExecutables.Contains(Path.GetFullPath(executablePath)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>Owns only the ComfyUI process created by this instance. It never kills a listener it did not start.</summary>
public sealed class ComfyUiServiceManager(VideoServiceConfiguration configuration, HttpClient? httpClient = null, IReadOnlyList<string>? requiredNodeClasses = null) : ILocalModelService
{
    private readonly HttpClient _http = httpClient ?? new HttpClient(new SocketsHttpHandler { UseProxy = false });
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _owned;
    private HashSet<string>? _ownedExecutables;
    private DateTime? _ownedStartTimeUtc;
    public bool OwnsModel => _owned is { HasExited: false };
    public int? OwnedProcessId => OwnsModel ? _owned!.Id : null;

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        if (!await ProbeSystemStatsAsync(cancellationToken)) return false;
        var nodes = requiredNodeClasses ?? Array.Empty<string>();
        foreach (var node in nodes)
            if (!await ProbeNodeAsync(node, cancellationToken)) return false;
        return true;
    }

    public async Task<bool> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken);
        var startedHere = false;
        try
        {
            if (await IsHealthyAsync(cancellationToken)) return false;

            if (await ProbeSystemStatsAsync(cancellationToken))
                throw new InvalidOperationException($"端口 {configuration.BindHost}:{configuration.Port} 上的 ComfyUI 缺少当前视频档案要求的节点，已拒绝复用。");
            if (await IsPortOccupiedAsync(cancellationToken))
                throw new InvalidOperationException($"端口 {configuration.BindHost}:{configuration.Port} 已被未知服务占用，已拒绝启动 ComfyUI。");

            if (_owned is { HasExited: false })
                throw new InvalidOperationException("已启动的 ComfyUI 尚未就绪。");
            _owned?.Dispose();
            _owned = null;

            var python = Path.GetFullPath(Path.Combine(configuration.ComfyUiRoot, ".venv", "Scripts", "python.exe"));
            var main = Path.GetFullPath(Path.Combine(configuration.ComfyUiRoot, "main.py"));
            if (!File.Exists(python) || !File.Exists(main))
                throw new InvalidOperationException("未找到已安装的 ComfyUI Python 或 main.py。");

            var startInfo = new ProcessStartInfo(python)
            {
                WorkingDirectory = configuration.ComfyUiRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "main.py", "--listen", configuration.BindHost, "--port", configuration.Port.ToString(), "--lowvram", "--reserve-vram", "1.0" })
                startInfo.ArgumentList.Add(argument);

            _owned = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动本地 ComfyUI。");
            startedHere = true;
            _ownedExecutables = new HashSet<string>(ComfyUiProcessIdentity.ExpectedExecutables(python), StringComparer.OrdinalIgnoreCase);
            _ownedStartTimeUtc = _owned.StartTime.ToUniversalTime();

            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_owned.HasExited)
                    throw new InvalidOperationException($"ComfyUI 在就绪前退出（exit={_owned.ExitCode}）。");
                if (await IsHealthyAsync(cancellationToken)) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            }

            await StopOwnedCoreAsync(cancellationToken);
            throw new TimeoutException("ComfyUI 在 120 秒内未完成当前视频档案的节点就绪检查。");
        }
        catch
        {
            if (startedHere)
            {
                try { await StopOwnedCoreAsync(CancellationToken.None); } catch { }
            }
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task UnloadReusedAsync(CancellationToken cancellationToken = default)
    {
        if (OwnsModel) return;
        if (await IsHealthyAsync(cancellationToken))
        {
            using var response = await _http.PostAsJsonAsync(
                new Uri(configuration.BaseUri, "free"),
                new { unload_models = true, free_memory = true },
                cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task StopServiceAsync(CancellationToken cancellationToken = default)
    {
        await StopOwnedAsync(cancellationToken);
        if (!await IsHealthyAsync(cancellationToken)) return;

        var pid = await QwenServiceManager.TryFindListeningPidAsync(configuration.Port, cancellationToken);
        if (pid is null) return;

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            string? actualExecutable = null;
            try { actualExecutable = process.MainModule?.FileName; }
            catch (System.ComponentModel.Win32Exception) { return; }

            var venvPython = Path.Combine(configuration.ComfyUiRoot, ".venv", "Scripts", "python.exe");
            var expected = ComfyUiProcessIdentity.ExpectedExecutables(venvPython);
            if (actualExecutable is null || !ComfyUiProcessIdentity.MatchesExpectedExecutable(actualExecutable, expected))
                return;

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (ArgumentException)
        {
            // Listener exited after the health probe.
        }
        catch (InvalidOperationException)
        {
            // Listener exited while its identity was being checked.
        }
    }

    public async Task StopOwnedAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken);
        try { await StopOwnedCoreAsync(cancellationToken); }
        finally { _startGate.Release(); }
    }

    private async Task StopOwnedCoreAsync(CancellationToken cancellationToken)
    {
        if (_owned is null) return;
        if (_owned.HasExited)
        {
            ClearOwnedProcess();
            return;
        }
        if (!OwnedProcessIdentityMatches())
            throw new InvalidOperationException("ComfyUI 自有进程身份校验失败；为避免结束未知进程，未执行停止。");

        _owned.Kill(entireProcessTree: true);
        await _owned.WaitForExitAsync(cancellationToken);
        ClearOwnedProcess();
    }

    private bool OwnedProcessIdentityMatches()
    {
        if (_owned is null || _owned.HasExited || _ownedExecutables is null || _ownedStartTimeUtc is null)
            return false;
        try
        {
            var actualExecutable = _owned.MainModule?.FileName;
            return actualExecutable is not null
                && _ownedExecutables.Contains(Path.GetFullPath(actualExecutable))
                && _owned.StartTime.ToUniversalTime() == _ownedStartTimeUtc.Value;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void ClearOwnedProcess()
    {
        _owned?.Dispose();
        _owned = null;
        _ownedExecutables = null;
        _ownedStartTimeUtc = null;
    }

    private async Task<bool> ProbeSystemStatsAsync(CancellationToken cancellationToken)
        => await ProbeAsync("system_stats", static _ => true, cancellationToken);

    private async Task<bool> ProbeNodeAsync(string nodeClass, CancellationToken cancellationToken)
        => await ProbeAsync($"object_info/{Uri.EscapeDataString(nodeClass)}", raw =>
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(nodeClass, out var node)
                && node.ValueKind == JsonValueKind.Object;
        }, cancellationToken);

    private async Task<bool> ProbeAsync(string relativePath, Func<string, bool> validate, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _http.GetAsync(new Uri(configuration.BaseUri, relativePath), timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            var raw = await response.Content.ReadAsStringAsync(timeout.Token);
            return validate(raw);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    private async Task<bool> IsPortOccupiedAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(configuration.BindHost, configuration.Port, timeout.Token);
            return true;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_owned is not null)
        {
            _owned.Dispose();
            _owned = null;
        }
        _http.Dispose();
        _startGate.Dispose();
        await ValueTask.CompletedTask;
    }
}
