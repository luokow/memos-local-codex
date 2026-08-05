namespace QwenLocalChat.Core;

public sealed class MemoryWriteQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _tail = Task.CompletedTask;
    private int _pendingCount;

    public int PendingCount => Volatile.Read(ref _pendingCount);
    public Exception? LastError { get; private set; }
    public event EventHandler? StateChanged;

    public void Enqueue(Func<CancellationToken, Task> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        Interlocked.Increment(ref _pendingCount);
        StateChanged?.Invoke(this, EventArgs.Empty);
        lock (_gate)
            _tail = RunAfterAsync(_tail, write);
    }

    private async Task RunAfterAsync(Task previous, Func<CancellationToken, Task> write)
    {
        try
        {
            try { await previous.ConfigureAwait(false); } catch { }
            _stop.Token.ThrowIfCancellationRequested();
            await write(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            LastError = error;
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCount);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task DrainAsync()
    {
        lock (_gate) return _tail;
    }

    public void CancelPending() => _stop.Cancel();

    public void Dispose() => _stop.Dispose();
}
