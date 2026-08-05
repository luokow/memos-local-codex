using System.Text;

namespace QwenLocalChat.Core;

public sealed class MarkdownChatLog(string logsRoot, string shortSessionId, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private readonly string _safeSessionId = new(shortSessionId.Where(char.IsLetterOrDigit).Take(8).ToArray());

    public bool Enabled { get; set; }
    public string? CurrentPath { get; private set; }

    public void RecordSuccessfulRound(string userMessage, string assistantMessage)
    {
        if (!Enabled) return;
        var timestamp = _now();
        if (CurrentPath is null)
        {
            var dailyDirectory = System.IO.Path.Combine(logsRoot, timestamp.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dailyDirectory);
            CurrentPath = System.IO.Path.Combine(
                dailyDirectory,
                $"session-{timestamp:yyyyMMdd-HHmmss}-{_safeSessionId}.md");
        }

        var entry = $"## {timestamp:yyyy-MM-dd HH:mm:ss zzz}\n\n### 你\n\n{userMessage}\n\n### Qwen\n\n{assistantMessage}\n\n---\n\n";
        File.AppendAllText(CurrentPath, entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
