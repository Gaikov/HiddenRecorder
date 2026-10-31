namespace HiddenRecorder.Core;

/// <summary>Coalesces edits and serializes atomic writes; flushing never needs a capture session.</summary>
public sealed class SettingsAutoSaver : IAsyncDisposable
{
    private readonly ISettingsStore store;
    private readonly ITimer timer;
    private readonly object sync = new();
    private readonly SemaphoreSlim writes = new(1);
    private readonly TimeSpan delay = TimeSpan.FromMilliseconds(500);
    private RecorderSettings? pending;
    private long revision, savedRevision;
    private bool disposed;
    public event Action? Saved;
    public event Action<Exception>? Failed;
    public bool HasPendingChanges { get { lock (sync) return revision != savedRevision; } }

    public SettingsAutoSaver(ISettingsStore store, TimeProvider? timeProvider = null)
    {
        this.store = store;
        timer = (timeProvider ?? TimeProvider.System).CreateTimer(state => { _ = SaveFromTimerAsync(); }, null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    public void RequestSave(RecorderSettings settings)
    {
        settings.Validate(requireDevices: false);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pending == settings && revision == savedRevision) return;
            pending = settings; revision++;
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }
    private async Task SaveFromTimerAsync()
    {
        try { await SavePendingAsync(); }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }
    public async Task FlushAsync()
    {
        lock (sync) { if (!disposed) timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        await SavePendingAsync();
    }
    private async Task SavePendingAsync()
    {
        await writes.WaitAsync();
        try
        {
            while (true)
            {
                RecorderSettings settings; long writingRevision;
                lock (sync)
                {
                    if (pending is null || revision == savedRevision) return;
                    settings = pending; writingRevision = revision;
                }
                await store.SaveAsync(settings);
                lock (sync) savedRevision = writingRevision;
                Saved?.Invoke();
            }
        }
        finally { writes.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await FlushAsync();
        await timer.DisposeAsync();
        lock (sync) disposed = true;
    }
}
