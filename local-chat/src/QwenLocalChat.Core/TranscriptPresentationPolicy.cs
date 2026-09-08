namespace QwenLocalChat.Core;

public static class TranscriptPresentationPolicy
{
    public const string AssistantLabel = "AI";

    public static bool IsAssistant(string label)
        => string.Equals(label, AssistantLabel, StringComparison.OrdinalIgnoreCase)
           || string.Equals(label, "Qwen", StringComparison.OrdinalIgnoreCase);

    public static bool CanCopy(string label)
        => IsAssistant(label);

    public static bool CanEdit(string? label)
        => string.Equals(label, "你", StringComparison.Ordinal);

    public static T AnchorAfterReply<T>(T submittedQuestion, T assistantReply)
    {
        _ = assistantReply;
        return submittedQuestion;
    }

    public static int FindLatestQuestionIndex(IReadOnlyList<ConversationTurn> turns, string submittedQuestion)
    {
        for (var index = turns.Count - 1; index >= 0; index--)
        {
            var turn = turns[index];
            if (string.Equals(turn.Label, "你", StringComparison.Ordinal)
                && string.Equals(turn.Text, submittedQuestion, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }

}
