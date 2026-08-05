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

/// <summary>Result of the exit prompt when the window owns a local model process.</summary>
public enum AppCloseDecision
{
    Cancel,
    ExitKeepModel,
    ExitStopModel,
}
