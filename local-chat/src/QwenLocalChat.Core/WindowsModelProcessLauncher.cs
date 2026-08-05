using System.Diagnostics;

namespace QwenLocalChat.Core;

public static class ModelLaunchCommand
{
    public static IReadOnlyList<string> BuildArguments(LocalModelOptions options)
    {
        var arguments = new List<string>
        {
            "--model", options.ModelFile,
            "--alias", options.ModelAlias,
            "--host", options.BindHost,
            "--port", options.Port.ToString(),
            "--ctx-size", options.ContextSize.ToString(),
            "--n-gpu-layers", options.GpuLayers.ToString(),
            "--reasoning", options.ReasoningEnabled ? "on" : "off",
        };
        if (options.UseJinja) arguments.Add("--jinja");
        arguments.Add("--parallel");
        arguments.Add(options.ParallelSlots.ToString());
        // Without unified KV, llama-server splits --ctx-size across slots
        // (e.g. 8192 ctx + 4 parallel → ~2048 per chat). Unified KV keeps one shared
        // pool so a single active generation can use the full window; concurrent
        // jobs share the same pool only when they actually run together.
        if (options.ParallelSlots > 1)
            arguments.Add("--kv-unified");
        return arguments;
    }
}

public sealed class WindowsModelProcessLauncher : IModelProcessLauncher
{
    public IOwnedModelProcess Start(LocalModelOptions options)
    {
        if (!File.Exists(options.ServerExecutable))
            throw new FileNotFoundException("找不到 llama-server.exe", options.ServerExecutable);
        if (!File.Exists(options.ModelFile))
            throw new FileNotFoundException("找不到 Qwen 模型", options.ModelFile);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(options.LogFile)!);

        var start = new ProcessStartInfo(options.ServerExecutable)
        {
            WorkingDirectory = System.IO.Path.GetDirectoryName(options.ServerExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in ModelLaunchCommand.BuildArguments(options)) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--log-file");
        start.ArgumentList.Add(options.LogFile);
        start.ArgumentList.Add("--log-timestamps");

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动 llama-server.exe");
        return new WindowsOwnedModelProcess(process, options.ServerExecutable, options.ModelFile);
    }
}

internal sealed class WindowsOwnedModelProcess(Process process, string expectedExecutable, string expectedModel) : IOwnedModelProcess
{
    public int Id => process.Id;
    public bool HasExited
    {
        get
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (HasExited) return;
        var actualExecutable = process.MainModule?.FileName;
        var arguments = process.StartInfo.ArgumentList;
        if (!string.Equals(System.IO.Path.GetFullPath(actualExecutable ?? ""), System.IO.Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase)
            || !arguments.Any(argument => string.Equals(System.IO.Path.GetFullPath(argument), System.IO.Path.GetFullPath(expectedModel), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("模型进程身份校验失败，拒绝终止未知进程");

        process.Kill(entireProcessTree: false);
        await process.WaitForExitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
