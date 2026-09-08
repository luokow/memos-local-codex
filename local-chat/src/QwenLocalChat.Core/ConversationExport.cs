using System.Text;

namespace QwenLocalChat.Core;

public sealed record ConversationTurn(string Label, string Text);

/// <summary>
/// Continue-generation is assistant prefill: the last incomplete assistant message stays the
/// final chat turn and the model extends it. A separate "请继续写" user turn is a workaround
/// that re-enters thinking and often burns the whole max_tokens budget with empty content.
/// </summary>
public static class AssistantContinuation
{
    public static bool CanContinue(IReadOnlyList<ChatMessage> history, string? lastFinishReason)
        => ConversationExport.CanContinue(history, lastFinishReason);

    /// <summary>
    /// Build the request that ends on the incomplete assistant message (prefill), applying
    /// the same history/budget trimming as a normal turn — without inventing a new user prompt.
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildPrefillMessages(
        IReadOnlyList<ChatMessage> committedHistory,
        ContextWindowPolicy? policy = null)
    {
        if (committedHistory.Count == 0 || committedHistory[^1].Role is not "assistant")
            throw new InvalidOperationException("没有可继续的助手回复");
        if (string.IsNullOrWhiteSpace(committedHistory[^1].Content))
            throw new InvalidOperationException("上一条助手回复为空，无法继续");

        policy ??= new ContextWindowPolicy();
        var start = Math.Max(0, committedHistory.Count - (policy.MaxHistoryRounds * 2));
        if (start % 2 != 0) start++;
        var result = committedHistory.Skip(start).ToList();
        // Keep the trailing assistant prefill; drop oldest complete user/assistant pairs only.
        while (result.Count >= 3
               && TokenEstimate.Messages(result) > ContextBudget.InputTokenBudget(policy))
        {
            result.RemoveRange(0, 2);
        }

        if (result.Count == 0 || result[^1].Role is not "assistant")
            throw new InvalidOperationException("上下文预算不足，无法保留待续写的回复，请提高上下文窗口或缩短前文。");
        return result;
    }

    /// <summary>
    /// llama.cpp returns the full assistant message (previous prefill + new tokens). Some
    /// endpoints return only the suffix — both are merged into one canonical assistant body.
    /// </summary>
    public static string MergeResponse(string previousAssistant, string modelContent)
    {
        var previous = previousAssistant ?? string.Empty;
        var model = modelContent?.Trim() ?? string.Empty;
        if (model.Length == 0) return previous.Trim();
        if (previous.Length == 0) return model;
        if (model.StartsWith(previous, StringComparison.Ordinal))
            return model;
        // Suffix-only response (no re-emitted prefill).
        return previous + model;
    }

    public static IReadOnlyList<ChatMessage> ReplaceLastAssistant(
        IReadOnlyList<ChatMessage> history,
        string mergedAssistantContent)
    {
        if (history.Count == 0 || history[^1].Role is not "assistant")
            throw new InvalidOperationException("历史末尾不是助手回复，无法合并续写结果");
        var next = history.Take(history.Count - 1).ToList();
        next.Add(new ChatMessage("assistant", mergedAssistantContent));
        return next;
    }
}

public static class ConversationExport
{
    public const string ContinueUserLabel = "（继续生成）";
    public const string ContinuePrompt = "请从上次中断处继续写，不要重复已经输出的内容。";

    public static bool CanContinue(IReadOnlyList<ChatMessage> history, string? lastFinishReason)
    {
        if (history.Count == 0) return false;
        if (history[^1].Role is not "assistant") return false;
        if (string.IsNullOrWhiteSpace(history[^1].Content)) return false;
        return string.Equals(lastFinishReason, "length", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lastFinishReason, "cancelled", StringComparison.OrdinalIgnoreCase);
    }

    public static string SuggestFileName(DateTimeOffset now)
        => $"local-ai-chat-{now:yyyyMMdd-HHmmss}.md";

    public static string ToMarkdown(IReadOnlyList<ConversationTurn> turns, DateTimeOffset? exportedAt = null)
    {
        var stamp = exportedAt ?? DateTimeOffset.Now;
        var builder = new StringBuilder();
        builder.AppendLine("# Local AI 对话导出");
        builder.AppendLine();
        builder.AppendLine($"导出时间：{stamp:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();

        foreach (var turn in turns)
        {
            if (string.IsNullOrWhiteSpace(turn.Text)) continue;
            builder.AppendLine($"## {turn.Label}");
            builder.AppendLine();
            builder.AppendLine(turn.Text.Trim());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static IReadOnlyList<ConversationTurn> FromTranscript(
        IEnumerable<(string Label, string Text)> items)
    {
        var turns = new List<ConversationTurn>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Text)) continue;
            if (item.Label is not ("你" or "SYSTEM") && !TranscriptPresentationPolicy.IsAssistant(item.Label)) continue;
            turns.Add(new ConversationTurn(item.Label, item.Text));
        }
        return turns;
    }
}
