namespace QwenLocalChat.Core;

public sealed record AppPaths(
    string LocalChatRoot,
    string ProjectRoot,
    string SettingsFile,
    string SessionsFile,
    string LogsRoot,
    string DiagnosticsRoot,
    string ModelServiceConfigFile,
    string ModelStartLockFile,
    string ModelLifecycleLogFile,
    string ModelLogFile,
    string McpServerScript)
{
    public static AppPaths Discover()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("QWEN_LOCAL_CHAT_ROOT");
        var localChatRoot = string.IsNullOrWhiteSpace(overrideRoot)
            ? FindLocalChatRoot(AppContext.BaseDirectory)
            : System.IO.Path.GetFullPath(overrideRoot);
        var projectRoot = new DirectoryInfo(localChatRoot).Parent?.FullName
            ?? throw new InvalidOperationException($"无法确定项目根目录：{localChatRoot}");
        return new AppPaths(
            localChatRoot,
            projectRoot,
            System.IO.Path.Combine(localChatRoot, "data", "settings.json"),
            System.IO.Path.Combine(localChatRoot, "data", "sessions.json"),
            System.IO.Path.Combine(localChatRoot, "logs"),
            System.IO.Path.Combine(localChatRoot, "diagnostics"),
            ModelServiceConfigStore.ResolveConfigPath(projectRoot),
            System.IO.Path.Combine(projectRoot, "runtime", "locks", "model-service-start.lock"),
            System.IO.Path.Combine(projectRoot, "runtime", "logs", "model-service-lifecycle.jsonl"),
            System.IO.Path.Combine(localChatRoot, "diagnostics", "llama-local-chat.log"),
            System.IO.Path.Combine(projectRoot, "scripts", "mcp-server.mjs"));
    }

    private static string FindLocalChatRoot(string start)
    {
        for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
        {
            if (current.Name.Equals("local-chat", StringComparison.OrdinalIgnoreCase)
                && current.Parent is not null
                && Directory.Exists(System.IO.Path.Combine(current.Parent.FullName, "llama")))
                return current.FullName;
        }
        throw new InvalidOperationException("找不到 local-chat 项目根目录；请从 D 盘发布目录启动程序");
    }
}
