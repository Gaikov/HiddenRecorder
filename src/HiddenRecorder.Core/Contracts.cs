namespace HiddenRecorder.Core;

public interface IDeviceCatalog { Task<DeviceCatalog> EnumerateAsync(CancellationToken cancellationToken = default); }
public interface ICaptureSession : IAsyncDisposable
{
    event Action<MediaEvent>? Event;
    Task StartAsync(RecorderSettings settings, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
public interface IEventRecorder
{
    Task BeginAsync(TimeSpan triggerTime, CancellationToken cancellationToken = default);
    Task FinishAsync(CancellationToken cancellationToken = default);
}
public interface IPreviewSurface { void Attach(nint windowHandle); void Resize(int width, int height); }
public interface ISettingsStore
{
    Task<RecorderSettings> LoadAsync();
    Task SaveAsync(RecorderSettings settings);
}
public interface IStorageGuard { void Check(RecorderSettings settings, bool probeWrite); }
public interface IUserNotifier { void Notify(string title, string message); }
public interface IAppLog { void Write(string message, Exception? exception = null); }
public interface IPowerGuard { void SetMonitoring(bool enabled); }
