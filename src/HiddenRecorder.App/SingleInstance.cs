using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HiddenRecorder.App;

/// <summary>Signals the existing UI even when its window and tray icon are hidden.</summary>
internal sealed class SingleInstance : IDisposable
{
    private const string ShowEventName = "Local\\HiddenRecorder.ShowWindow";
    private readonly Mutex mutex;
    private readonly EventWaitHandle? showEvent;
    private RegisteredWaitHandle? listener;
    private bool disposed;
    public bool IsPrimary { get; }
    public SingleInstance()
    {
        mutex = new Mutex(true, "Local\\HiddenRecorder.Desktop", out var first);
        IsPrimary = first;
        if (first) showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
    }
    public void Listen(Action show)
    {
        if (!IsPrimary || listener is not null) throw new InvalidOperationException("Only the primary instance can listen once.");
        listener = ThreadPool.RegisterWaitForSingleObject(showEvent!, (_, _) =>
        {
            if (!Volatile.Read(ref disposed)) show();
        }, null, Timeout.Infinite, false);
    }
    public bool ShowExisting()
    {
        // The first process may still be creating its event immediately after acquiring the mutex.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var signal))
            {
                using (signal)
                {
                    using var current = Process.GetCurrentProcess();
                    foreach (var process in Process.GetProcessesByName(current.ProcessName))
                    {
                        using (process)
                        {
                            try { if (process.Id != current.Id && process.SessionId == current.SessionId) AllowSetForegroundWindow((uint)process.Id); }
                            catch (InvalidOperationException) { /* The other process exited while enumerating. */ }
                        }
                    }
                    return signal.Set();
                }
            }
            Thread.Sleep(50);
        }
        return false;
    }
    public void Dispose()
    {
        if (disposed) return; Volatile.Write(ref disposed, true);
        listener?.Unregister(null); showEvent?.Dispose();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
    [DllImport("user32")] private static extern bool AllowSetForegroundWindow(uint processId);
}
