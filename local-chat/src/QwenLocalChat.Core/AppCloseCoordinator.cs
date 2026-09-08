namespace QwenLocalChat.Core;

public sealed class AppCloseCoordinator
{
    private int _cleanupStarted;
    private int _closeApproved;

    public bool ShouldCancelClose => Volatile.Read(ref _closeApproved) == 0;

    public bool TryBeginCleanup()
        => Interlocked.CompareExchange(ref _cleanupStarted, 1, 0) == 0;

    /// <summary>
    /// User cancelled the close prompt; allow a later close attempt to begin cleanup again.
    /// </summary>
    public void AbortCleanup()
        => Volatile.Write(ref _cleanupStarted, 0);

    public void ApproveClose() => Volatile.Write(ref _closeApproved, 1);
}

/// <summary>Result of the explicit exit prompt.</summary>
public enum AppCloseDecision
{
    Cancel,
    ExitKeepModel,
    ExitStopModel,
}

/// <summary>Pure presentation policy for the inline exit prompt.</summary>
public sealed record AppClosePromptPresentation
{
    public static AppClosePromptPresentation Create() => new();

    public bool CanStopModel => true;

    public string Message => "确定要退出 Local AI 吗？";

    public string Hint => "仅退出不会关闭模型服务；也可以退出并关闭模型服务。";
}
