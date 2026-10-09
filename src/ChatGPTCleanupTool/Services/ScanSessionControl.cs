namespace ChatGPTCleanupTool.Services;

/// <summary>Cooperative pause barrier: no index/metadata operation is in flight when PauseAsync returns.</summary>
public sealed class ScanSessionControl
{
    private readonly object _sync = new();
    private TaskCompletionSource _running = Completed();
    private TaskCompletionSource _idle = Completed();
    private int _active;
    private bool _paused;
    public bool IsPaused { get { lock (_sync) return _paused; } }
    public Task PauseAsync()
    {
        lock (_sync)
        {
            if (!_paused) { _paused = true; _running = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            return _idle.Task;
        }
    }
    public void Resume()
    {
        lock (_sync) { _paused = false; _running.TrySetResult(); }
    }
    public async ValueTask<IDisposable> EnterAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task gate;
            lock (_sync)
            {
                if (!_paused)
                {
                    if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    return new Lease(this);
                }
                gate = _running.Task;
            }
            await gate.WaitAsync(token);
        }
    }
    public async Task WaitAsync(CancellationToken token)
    {
        using var lease = await EnterAsync(token);
    }
    private void Leave()
    { lock (_sync) { if (--_active == 0) _idle.TrySetResult(); } }
    private sealed class Lease(ScanSessionControl owner) : IDisposable
    { private ScanSessionControl? _owner = owner; public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave(); }
    private static TaskCompletionSource Completed()
    { var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
}
