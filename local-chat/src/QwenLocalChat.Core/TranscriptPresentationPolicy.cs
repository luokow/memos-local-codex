namespace QwenLocalChat.Core;

public static class TranscriptPresentationPolicy
{
    public static bool CanCopy(string label)
        => string.Equals(label, "QWEN", StringComparison.OrdinalIgnoreCase);

    public static T AnchorAfterReply<T>(T submittedQuestion, T assistantReply)
    {
        _ = assistantReply;
        return submittedQuestion;
    }

}
