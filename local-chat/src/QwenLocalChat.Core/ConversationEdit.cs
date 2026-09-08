namespace QwenLocalChat.Core;

/// <summary>
/// Truncates a thread from a user turn so the client can replace that question and regenerate.
/// </summary>
public static class ConversationEdit
{
    public sealed record Result(
        IReadOnlyList<ChatMessage> History,
        IReadOnlyList<ConversationTurn> Transcript,
        string ModelUserText,
        string VisibleUserText);

    public static bool CanEdit(string? label)
        => TranscriptPresentationPolicy.CanEdit(label);

    public static bool TryPrepareRegenerate(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ConversationTurn> transcript,
        string originalVisibleUserText,
        int occurrenceFromEnd,
        string newUserText,
        out Result? result,
        out string? error)
    {
        result = null;
        error = null;
        history ??= [];
        transcript ??= [];
        if (occurrenceFromEnd < 0)
        {
            error = "要编辑的提问位置无效。";
            return false;
        }

        var transcriptIndex = FindUserTranscriptIndex(transcript, originalVisibleUserText, occurrenceFromEnd);
        if (transcriptIndex < 0)
        {
            error = "找不到要编辑的提问。";
            return false;
        }

        var historyIndex = MapHistoryUserIndex(history, transcript, transcriptIndex);
        if (historyIndex < 0)
        {
            error = "这条提问无法对应到模型历史，不能重新生成。";
            return false;
        }

        var edited = (newUserText ?? "").Trim();
        if (edited.Length == 0)
        {
            error = "修改后的提问不能为空。";
            return false;
        }

        var originalVisible = originalVisibleUserText ?? "";
        var originalModel = history[historyIndex].Content ?? "";
        var unchanged = string.Equals(edited, originalVisible.Trim(), StringComparison.Ordinal);
        var modelText = unchanged ? originalModel : edited;
        var visibleText = unchanged ? originalVisible : edited;
        if (string.IsNullOrWhiteSpace(modelText))
        {
            error = "修改后的提问不能为空。";
            return false;
        }

        result = new Result(
            history.Take(historyIndex).ToArray(),
            transcript.Take(transcriptIndex).ToArray(),
            modelText,
            visibleText);
        return true;
    }

    public static int FindUserTranscriptIndex(
        IReadOnlyList<ConversationTurn> transcript,
        string visibleUserText,
        int occurrenceFromEnd)
    {
        var seen = 0;
        for (var i = transcript.Count - 1; i >= 0; i--)
        {
            if (transcript[i].Label != "你") continue;
            if (!string.Equals(transcript[i].Text, visibleUserText, StringComparison.Ordinal)) continue;
            if (seen == occurrenceFromEnd) return i;
            seen++;
        }
        return -1;
    }

    private static int MapHistoryUserIndex(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ConversationTurn> transcript,
        int transcriptIndex)
    {
        var userOrdinal = 0;
        for (var i = 0; i <= transcriptIndex; i++)
        {
            if (transcript[i].Label == "你") userOrdinal++;
        }
        if (userOrdinal == 0) return -1;

        var historyUsers = new List<int>();
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Role == "user") historyUsers.Add(i);
        }
        if (historyUsers.Count == 0) return -1;

        var transcriptUsers = transcript.Count(turn => turn.Label == "你");
        if (historyUsers.Count == transcriptUsers && userOrdinal <= historyUsers.Count)
            return historyUsers[userOrdinal - 1];

        var lastTranscriptUser = -1;
        for (var i = 0; i < transcript.Count; i++)
        {
            if (transcript[i].Label == "你") lastTranscriptUser = i;
        }
        if (transcriptIndex == lastTranscriptUser)
            return historyUsers[^1];
        return -1;
    }
}
