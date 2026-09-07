using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace QwenLocalChat.Core;

public static class HanhuaProgress
{
    public static HanhuaProgressEvent ParseLine(string line)
    {
        var raw = line ?? "";
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return new("log", null, 0, 0, null, null, 0, raw, false);
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new("log", null, 0, 0, trimmed, null, 0, raw, false);
            var type = ReadString(root, "type") ?? "progress";
            return new(
                type,
                ReadString(root, "phase"),
                ReadInt(root, "done"),
                ReadInt(root, "total"),
                ReadString(root, "message"),
                ReadString(root, "output"),
                ReadInt(root, "empty"),
                raw,
                true);
        }
        catch (JsonException)
        {
            return new("log", null, 0, 0, trimmed, null, 0, raw, false);
        }
    }

    public static HanhuaPhase? TryPhase(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "copy" => HanhuaPhase.Copy,
            "extract" => HanhuaPhase.Extract,
            "translate" => HanhuaPhase.Translate,
            "inject" => HanhuaPhase.Inject,
            "ocr" => HanhuaPhase.Ocr,
            "fill" => HanhuaPhase.Fill,
            "typeset" => HanhuaPhase.Typeset,
            _ => null,
        };

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n)) return n;
        return 0;
    }
}

public sealed class HanhuaProcessHost
{
    public async Task<int> RunAsync(
        HanhuaLaunch launch,
        Action<HanhuaProgressEvent> onEvent,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = launch.FileName,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Clear();
        foreach (var argument in launch.Arguments)
            start.ArgumentList.Add(argument);
        foreach (var pair in launch.Environment)
            start.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { stdoutDone.TrySetResult(); return; }
            var parsed = HanhuaProgress.ParseLine(e.Data);
            onEvent(parsed);
            if (!parsed.IsJson && !string.IsNullOrWhiteSpace(parsed.RawLine))
                onLog(parsed.RawLine);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { stderrDone.TrySetResult(); return; }
            onLog(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("无法启动汉化进程。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await using var cancelReg = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            });
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { }
            return -1;
        }
    }
}
