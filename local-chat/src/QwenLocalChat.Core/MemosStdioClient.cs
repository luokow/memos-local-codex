using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace QwenLocalChat.Core;

public sealed class MemosStdioClient(string nodeExecutable, string serverScript, string workingDirectory) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private Task? _readerTask;
    private Task? _stderrTask;
    private long _nextId;

    public bool IsStarted => _process is { HasExited: false };
    public string LastDiagnostic
    {
        get { lock (_stderr) return _stderr.ToString(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsStarted) return;
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (IsStarted) return;
            var start = new ProcessStartInfo(nodeExecutable)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            start.ArgumentList.Add(serverScript);
            start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
            foreach (var key in new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "MEMOS_API_KEY" })
                start.Environment.Remove(key);
            _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 MemOS MCP 子进程");
            _readerTask = ReadResponsesAsync(_process);
            _stderrTask = ReadDiagnosticsAsync(_process);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            _ = await RequestAsync("initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "qwen-local-chat", version = "1.0.0" },
            }, timeout.Token);
            await NotifyAsync("notifications/initialized", new { }, timeout.Token);
        }
        catch
        {
            await StopProcessAsync();
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<string> RecallAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        var result = await CallToolAsync("memos_recall", new { query, top_k = topK }, cancellationToken);
        return result.TryGetProperty("context", out var context) ? context.GetString() ?? "" : "";
    }

    public Task<JsonElement> RememberAsync(string userMessage, string assistantResponse, string sessionId, CancellationToken cancellationToken = default)
        => CallToolAsync("memos_remember", new { user_message = userMessage, assistant_response = assistantResponse, session_id = sessionId }, cancellationToken);

    public Task<JsonElement> HealthAsync(CancellationToken cancellationToken = default)
        => CallToolAsync("memos_health", new { }, cancellationToken);

    public Task<JsonElement> ListRecentAsync(int limit = 10, CancellationToken cancellationToken = default)
        => CallToolAsync("memos_list_recent", new { limit }, cancellationToken);

    private async Task<JsonElement> CallToolAsync(string name, object arguments, CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);
        var result = await RequestAsync("tools/call", new Dictionary<string, object?>
        {
            ["name"] = name,
            ["arguments"] = arguments,
        }, cancellationToken);
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException(ReadToolText(result));
        var text = ReadToolText(result);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string ReadToolText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.GetArrayLength() == 0)
            throw new InvalidOperationException("MemOS MCP 返回内容为空");
        return content[0].GetProperty("text").GetString() ?? "{}";
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited) throw new InvalidOperationException($"MemOS MCP 未运行。{LastDiagnostic}");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("重复的 MCP 请求编号");
        try
        {
            await WriteMessageAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken)
        => WriteMessageAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        if (_process is null) throw new InvalidOperationException("MemOS MCP 未启动");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadResponsesAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)) continue;
                if (!_pending.TryGetValue(id, out var completion)) continue;
                if (root.TryGetProperty("error", out var error))
                    completion.TrySetException(new InvalidOperationException(error.ToString()));
                else if (root.TryGetProperty("result", out var result))
                    completion.TrySetResult(result.Clone());
            }
            FailPending(new EndOfStreamException($"MemOS MCP 已关闭。{LastDiagnostic}"));
        }
        catch (Exception error)
        {
            FailPending(error);
        }
    }

    private async Task ReadDiagnosticsAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            lock (_stderr)
            {
                _stderr.AppendLine(line);
                if (_stderr.Length > 8_000) _stderr.Remove(0, _stderr.Length - 8_000);
            }
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var completion in _pending.Values) completion.TrySetException(error);
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try { process.StandardInput.Close(); } catch { }
        if (!process.HasExited)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: false);
            }
        }
        process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopProcessAsync();
        if (_readerTask is not null) try { await _readerTask; } catch { }
        if (_stderrTask is not null) try { await _stderrTask; } catch { }
        _startGate.Dispose();
        _writeGate.Dispose();
    }
}
