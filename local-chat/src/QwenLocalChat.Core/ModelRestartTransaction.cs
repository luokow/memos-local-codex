namespace QwenLocalChat.Core;

public sealed class ModelRestartException(
    Exception applyError,
    IReadOnlyList<Exception> rollbackErrors,
    bool previousRuntimeRestored)
    : Exception(
        previousRuntimeRestored
            ? $"New model settings failed; the previous runtime was restored: {applyError.Message}"
            : $"New model settings failed and the previous runtime could not be fully restored: {applyError.Message}",
        applyError)
{
    public Exception ApplyError { get; } = applyError;
    public IReadOnlyList<Exception> RollbackErrors { get; } = rollbackErrors;
    public bool PreviousRuntimeRestored { get; } = previousRuntimeRestored;
}

public static class ModelRestartTransaction
{
    public static async Task RunAsync(
        Func<Task> stopPrevious,
        Func<Task> startCandidate,
        Func<Task> restorePersistentState,
        Func<Task> stopCandidate,
        Func<Task> startPrevious)
    {
        try
        {
            await stopPrevious();
            await startCandidate();
            return;
        }
        catch (Exception applyError)
        {
            var rollbackErrors = new List<Exception>();
            await TryRollbackStepAsync(restorePersistentState, rollbackErrors);
            await TryRollbackStepAsync(stopCandidate, rollbackErrors);
            await TryRollbackStepAsync(startPrevious, rollbackErrors);
            throw new ModelRestartException(applyError, rollbackErrors, rollbackErrors.Count == 0);
        }
    }

    private static async Task TryRollbackStepAsync(Func<Task> step, List<Exception> errors)
    {
        try
        {
            await step();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }
}
