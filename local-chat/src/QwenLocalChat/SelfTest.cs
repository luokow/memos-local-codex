using System.Diagnostics;
using System.Text.Json;
using QwenLocalChat.Core;

namespace QwenLocalChat;

internal static class SelfTest
{
    internal sealed record SemanticMemoryProbe(string Statement, string Query, string Marker);

    public static async Task<bool> RunAsync(AppPaths paths)
    {
        Directory.CreateDirectory(paths.DiagnosticsRoot);
        var reportPath = System.IO.Path.Combine(paths.DiagnosticsRoot, "self-test.json");
        var started = DateTimeOffset.Now;
        var modelConfig = new ModelServiceConfigStore(paths.ProjectRoot, paths.ModelServiceConfigFile)
            .LoadOrMigrate(paths.SettingsFile).Config;
        var serverExecutable = modelConfig.ResolveServerExecutable(paths.ProjectRoot);
        var before = ExactLlamaPids(serverExecutable);
        var checks = new Dictionary<string, object?>();
        Exception? failure = null;
        try
        {
            var options = modelConfig.ToLocalModelOptions(
                paths.ProjectRoot,
                paths.ModelLogFile,
                paths.ModelStartLockFile,
                paths.ModelLifecycleLogFile);
            await using var manager = new QwenServiceManager(options, new WindowsModelProcessLauncher());
            var availability = await manager.EnsureAvailableAsync();
            checks["model_reused"] = availability.Reused;

            using var chat = new QwenChatClient(options.ChatCompletionsUri);
            var marker = $"蓝鲸-{Guid.NewGuid():N}"[..15];
            var first = await chat.CompleteAsync([new("user", $"请只回答这个代号，不要添加其他内容：{marker}")]);
            var second = await chat.CompleteAsync(ConversationContext.Build(
                [new("user", $"请只回答这个代号，不要添加其他内容：{marker}"), new("assistant", first)],
                "我上一条指定的代号是什么？只回答代号。"));
            checks["two_turn_context"] = NormalizeToken(second).Contains(NormalizeToken(marker), StringComparison.OrdinalIgnoreCase);
            checks["first_answer"] = first;
            checks["second_answer"] = second;

            await using var memos = new MemosStdioClient("node.exe", paths.McpServerScript, paths.ProjectRoot);
            var health = await memos.HealthAsync();
            var memoryProbe = CreateSemanticMemoryProbe(Guid.NewGuid());
            var sessionId = $"qwen-local-chat-self-test-{Guid.NewGuid():N}";
            var remembered = await memos.RememberAsync($"请长期记住这件独特事实：{memoryProbe.Statement}", "已记录这件独特事实。", sessionId);
            var recent = await memos.ListRecentAsync(10);
            var recalled = await memos.RecallAsync(memoryProbe.Query, 5);
            checks["memos_health_ok"] = health.TryGetProperty("ok", out var ok) && ok.GetBoolean();
            checks["memos_stored"] = remembered.TryGetProperty("stored", out var stored) && stored.GetBoolean();
            checks["memos_trace_visible"] = RecentTraceContains(recent, sessionId, memoryProbe.Marker);
            checks["memos_semantic_recall_available"] = !string.IsNullOrWhiteSpace(recalled);
            checks["memos_probe_statement"] = memoryProbe.Statement;
            checks["memos_session_id"] = sessionId;

            var after = ExactLlamaPids(serverExecutable);
            checks["llama_pids_before"] = before;
            checks["llama_pids_after"] = after;
            checks["no_duplicate_model_process"] = SingleModelWasReused(before, after);
            checks["ok"] = checks.Values.OfType<bool>().All(value => value);
        }
        catch (Exception error)
        {
            failure = error;
            checks["ok"] = false;
        }

        var report = new
        {
            ok = checks.TryGetValue("ok", out var okValue) && okValue is true,
            started_at = started,
            finished_at = DateTimeOffset.Now,
            model = modelConfig.ModelAlias,
            endpoint = $"http://{modelConfig.BindHost}:{modelConfig.Port}",
            checks,
            error = failure?.ToString(),
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.ok;
    }

    private static int[] ExactLlamaPids(string executable)
    {
        var expected = System.IO.Path.GetFullPath(executable);
        var result = new List<int>();
        foreach (var process in Process.GetProcessesByName("llama-server"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(System.IO.Path.GetFullPath(process.MainModule?.FileName ?? ""), expected, StringComparison.OrdinalIgnoreCase))
                        result.Add(process.Id);
                }
                catch { }
            }
        }
        result.Sort();
        return result.ToArray();
    }

    internal static bool SingleModelWasReused(int[] before, int[] after)
        => before.Length == 1 && before.SequenceEqual(after);

    internal static SemanticMemoryProbe CreateSemanticMemoryProbe(Guid id)
    {
        string[] subjects = ["琥珀海燕", "靛蓝雪豹", "银白信天翁", "赤铜水獭", "墨绿云雀", "青瓷鲸鱼", "紫杉狐狸", "金砂雨燕"];
        string[] actions = ["守护", "携带", "藏起", "标记", "绕过", "托起", "寻找", "记录"];
        string[] objects = ["月相罗盘", "玻璃种子", "铜制星图", "松木钟摆", "云纹钥匙", "石英羽毛", "珊瑚书签", "磁性纸舟"];
        string[] places = ["雾松塔顶", "盐湖东岸", "玄武岩拱门", "银杏旧站", "白桦温室", "潮汐灯塔", "青苔庭院", "风铃码头"];
        var bytes = id.ToByteArray();
        var subject = subjects[bytes[0] % subjects.Length];
        var action = actions[bytes[1] % actions.Length];
        var item = objects[bytes[2] % objects.Length];
        var place = places[bytes[3] % places.Length];
        var marker = id.ToString("N")[..8];
        return new(
            $"{subject}{action}{item}在{place}，校验尾码{marker}。",
            $"回忆{subject}的独特事实，完整回答它的动作、物品、地点和校验尾码。",
            marker);
    }

    internal static bool RecentTraceContains(JsonElement result, string sessionId, string marker)
    {
        if (!result.TryGetProperty("traces", out var traces) || traces.ValueKind != JsonValueKind.Array) return false;
        foreach (var trace in traces.EnumerateArray())
        {
            var traceSession = trace.TryGetProperty("sessionId", out var session) ? session.GetString() : null;
            var userText = trace.TryGetProperty("userText", out var user) ? user.GetString() : null;
            if (string.Equals(traceSession, sessionId, StringComparison.Ordinal)
                && (userText?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false))
                return true;
        }
        return false;
    }

    internal static async Task<string> RecallUntilVisibleAsync(
        Func<Task<string>> recall,
        string marker,
        int maxAttempts,
        TimeSpan pollInterval)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        var latest = string.Empty;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            latest = await recall();
            if (latest.Contains(marker, StringComparison.OrdinalIgnoreCase)) return latest;
            if (attempt < maxAttempts && pollInterval > TimeSpan.Zero) await Task.Delay(pollInterval);
        }
        return latest;
    }

    private static string NormalizeToken(string value)
        => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
}
