namespace QwenLocalChat.Core;

public enum ComposerKeyAction
{
    None,
    Send,
    InsertLineBreak,
}

public readonly record struct ComposerInteractionState(
    bool InputEnabled,
    bool InputReadOnly,
    bool ActionEnabled,
    bool StopMode);

public static class ChatInteractionPolicy
{
    public static ComposerKeyAction ResolveEnter(bool isEnter, bool shift)
    {
        if (!isEnter) return ComposerKeyAction.None;
        return shift ? ComposerKeyAction.InsertLineBreak : ComposerKeyAction.Send;
    }

    public static ComposerInteractionState ComposerState(bool busy)
    {
        // Keep the action button enabled while generating so the user can stop.
        return new ComposerInteractionState(
            InputEnabled: true,
            InputReadOnly: false,
            ActionEnabled: true,
            StopMode: busy);
    }

    public static string ActionLabel(bool busy)
        => busy ? "停止" : "发送";
}
