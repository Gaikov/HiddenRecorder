using System.Threading.Channels;

namespace HiddenRecorder.Core;

/// <summary>Single-reader actor. Finalization runs outside the actor so new sound is never blocked behind disk IO.</summary>
public sealed class RecordingCoordinator : IAsyncDisposable
{
    private readonly ICaptureSession capture;
    private readonly IEventRecorder recorder;
    private readonly IStorageGuard storage;
    private readonly IUserNotifier notifier;
    private readonly IPowerGuard power;
    private readonly IAppLog log;
    private readonly TimeProvider time;
    private readonly Channel<Func<Task>> mailbox = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    private readonly Task reader;
    private readonly ITimer timer;
    private RecorderSettings settings = new();
    private SoundTriggerPolicy policy = new(-25, TimeSpan.Zero);
    private RecorderSnapshot snapshot = new(RecorderState.Idle);
    private long lastSound, recordingStarted, lastDiskCheck;
    private TimeSpan? pendingTrigger;
    private Task? finalization;
    private long generation;
    private int tickQueued;
    private bool disposed;
    public RecorderSnapshot Snapshot => snapshot;
    public event Action<RecorderSnapshot>? Changed;

    public RecordingCoordinator(ICaptureSession capture, IEventRecorder recorder, IStorageGuard storage,
        IUserNotifier notifier, IPowerGuard power, IAppLog log, TimeProvider? timeProvider = null)
    {
        (this.capture, this.recorder, this.storage, this.notifier, this.power, this.log, time) =
            (capture, recorder, storage, notifier, power, log, timeProvider ?? TimeProvider.System);
        capture.Event += OnMedia;
        reader = Task.Run(ReadAsync);
        timer = time.CreateTimer(_ =>
        {
            if (Interlocked.Exchange(ref tickQueued, 1) == 0)
                mailbox.Writer.TryWrite(async () => { Interlocked.Exchange(ref tickQueued, 0); await TickAsync(); });
        }, null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
    }
    private async Task ReadAsync()
    {
        await foreach (var action in mailbox.Reader.ReadAllAsync())
            try { await action(); } catch (Exception ex) { await FaultAsync(ex); }
    }
    private Task Command(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!mailbox.Writer.TryWrite(async () =>
        {
            try { await action(); completion.TrySetResult(); }
            catch (Exception ex) { await FaultAsync(ex); completion.TrySetException(ex); }
        })) completion.TrySetException(new ObjectDisposedException(nameof(RecordingCoordinator)));
        return completion.Task;
    }
    public Task StartAsync(RecorderSettings options) => Command(async () =>
    {
        if (snapshot.State is not (RecorderState.Idle or RecorderState.Faulted or RecorderState.Previewing or RecorderState.PreviewStarting)) return;
        options.Validate(); storage.Check(options, true);
        var reuse = snapshot.State == RecorderState.Previewing && SameCaptureSettings(settings, options);
        if (!reuse && snapshot.State is RecorderState.Previewing or RecorderState.PreviewStarting) await StopInternalAsync();
        settings = options; policy = new(options.ThresholdDb, TimeSpan.FromMilliseconds(options.MinimumSoundMs));
        generation++; pendingTrigger = null; finalization = null;
        power.SetMonitoring(true); lastDiskCheck = time.GetTimestamp();
        if (reuse) SetState(RecorderState.Armed);
        else { snapshot = new(RecorderState.Starting); Publish(); await capture.StartAsync(settings); }
    });
    public Task StartPreviewAsync(RecorderSettings options) => Command(async () =>
    {
        if (snapshot.State is not (RecorderState.Idle or RecorderState.Faulted or RecorderState.Previewing or RecorderState.PreviewStarting)) return;
        options.Validate();
        if (snapshot.State is RecorderState.Previewing or RecorderState.PreviewStarting && SameCaptureSettings(settings, options)) return;
        if (snapshot.State != RecorderState.Idle) await StopInternalAsync();
        settings = options; generation++; pendingTrigger = null; finalization = null;
        snapshot = new(RecorderState.PreviewStarting); Publish();
        await capture.StartAsync(settings);
    });
    // Stop event recording while retaining the live camera/microphone for settings and meters.
    public Task DisarmAsync() => Command(async () =>
    {
        if (snapshot.State is RecorderState.Idle or RecorderState.Faulted or RecorderState.PreviewStarting or RecorderState.Previewing) return;
        if (snapshot.State == RecorderState.Starting)
        {
            await StopInternalAsync(); generation++; snapshot = new(RecorderState.PreviewStarting); Publish();
            await capture.StartAsync(settings); return;
        }
        generation++; pendingTrigger = null; SetState(RecorderState.Stopping);
        if (finalization is not null) await finalization; else await recorder.FinishAsync();
        finalization = null; power.SetMonitoring(false);
        snapshot = snapshot with { State = RecorderState.Previewing, CurrentFile = "", RemainingSeconds = 0, RecordedSeconds = 0 };
        Publish();
    });
    private static bool SameCaptureSettings(RecorderSettings a, RecorderSettings b) =>
        a.CameraId == b.CameraId && a.MicrophoneId == b.MicrophoneId && a.VideoMode == b.VideoMode &&
        a.PreRollSeconds == b.PreRollSeconds && a.VideoBitrateKbps == b.VideoBitrateKbps &&
        a.AudioBitrateKbps == b.AudioBitrateKbps && a.AudioChannels == b.AudioChannels &&
        a.OutputDirectory == b.OutputDirectory && a.PartSeconds == b.PartSeconds;
    public Task StopAsync() => Command(StopInternalAsync);
    public Task DrainAsync() => Command(() => Task.CompletedTask);
    private async Task StopInternalAsync()
    {
        if (snapshot.State == RecorderState.Idle) return;
        generation++; SetState(RecorderState.Stopping); pendingTrigger = null;
        if (finalization is not null) await finalization;
        else await recorder.FinishAsync();
        await capture.StopAsync(); power.SetMonitoring(false);
        snapshot = new(RecorderState.Idle); Publish();
    }
    private void OnMedia(MediaEvent value)
    {
        var currentGeneration = Volatile.Read(ref generation);
        var received = time.GetTimestamp();
        mailbox.Writer.TryWrite(() => currentGeneration == generation ? HandleAsync(value, received) : Task.CompletedTask);
    }
    private async Task HandleAsync(MediaEvent value, long received)
    {
        if (snapshot.State is RecorderState.Idle or RecorderState.Stopping or RecorderState.Faulted) return;
        switch (value)
        {
            case CaptureReady when snapshot.State == RecorderState.Starting: SetState(RecorderState.Armed); break;
            case CaptureReady when snapshot.State == RecorderState.PreviewStarting: SetState(RecorderState.Previewing); break;
            case AudioLevel level:
                snapshot = snapshot with { PeakDb = level.PeakDb, RmsDb = level.RmsDb };
                if (snapshot.State is (RecorderState.Armed or RecorderState.Recording or RecorderState.Finalizing) && policy.Observe(level))
                {
                    lastSound = received;
                    if (snapshot.State == RecorderState.Armed) await BeginAsync(level.MediaTime);
                    else if (snapshot.State == RecorderState.Finalizing) pendingTrigger ??= level.MediaTime;
                }
                break;
            case BufferProgress progress: snapshot = snapshot with { BufferedSeconds = progress.Seconds }; break;
            case FileOpened file when snapshot.State is RecorderState.Recording or RecorderState.Finalizing: snapshot = snapshot with { CurrentFile = file.Path }; log.Write($"File opened: {file.Path}"); break;
            case FileClosed file: log.Write($"File closed: {file.Path}"); break;
            case MediaFailure error: throw new IOException(error.Message);
        }
    }
    private async Task BeginAsync(TimeSpan trigger)
    {
        await recorder.BeginAsync(trigger); recordingStarted = time.GetTimestamp();
        SetState(RecorderState.Recording);
    }
    private async Task TickAsync()
    {
        if (snapshot.State is RecorderState.Idle or RecorderState.Faulted) return;
        var now = time.GetTimestamp();
        if (snapshot.State is not (RecorderState.Previewing or RecorderState.PreviewStarting) && time.GetElapsedTime(lastDiskCheck, now) >= TimeSpan.FromSeconds(1))
        { storage.Check(settings, false); lastDiskCheck = now; }
        if (snapshot.State == RecorderState.Recording)
        {
            var remaining = settings.SilenceSeconds - time.GetElapsedTime(lastSound, now).TotalSeconds;
            snapshot = snapshot with { RemainingSeconds = Math.Max(0, remaining), RecordedSeconds = time.GetElapsedTime(recordingStarted, now).TotalSeconds };
            if (remaining <= 0)
            {
                SetState(RecorderState.Finalizing);
                var currentGeneration = generation;
                finalization = recorder.FinishAsync();
                _ = CompleteFinalizationAsync(finalization, currentGeneration);
            }
        }
        Publish();
        await Task.CompletedTask;
    }
    private async Task CompleteFinalizationAsync(Task operation, long currentGeneration)
    {
        Exception? failure = null;
        try { await operation; } catch (Exception ex) { failure = ex; }
        mailbox.Writer.TryWrite(async () =>
        {
            if (generation != currentGeneration) return;
            finalization = null;
            if (failure is not null) throw failure;
            if (pendingTrigger is { } trigger) { pendingTrigger = null; await BeginAsync(trigger); }
            else { snapshot = snapshot with { CurrentFile = "", RemainingSeconds = 0, RecordedSeconds = 0 }; SetState(RecorderState.Armed); }
        });
    }
    private async Task FaultAsync(Exception error)
    {
        generation++; log.Write("Recorder failure", error);
        snapshot = snapshot with { State = RecorderState.Faulted, Error = error.Message }; Publish();
        try { if (finalization is not null) await finalization; else await recorder.FinishAsync(); } catch (Exception ex) { log.Write("Finalize after failure", ex); }
        try { await capture.StopAsync(); } catch (Exception ex) { log.Write("Stop after failure", ex); }
        finalization = null; pendingTrigger = null; power.SetMonitoring(false);
        notifier.Notify("Monitoring stopped", error.Message);
    }
    private void SetState(RecorderState state) { snapshot = snapshot with { State = state }; log.Write($"State: {state}"); Publish(); }
    private void Publish() => Changed?.Invoke(snapshot);
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        await timer.DisposeAsync();
        try { await StopAsync(); } catch { /* already reported by actor */ }
        capture.Event -= OnMedia; mailbox.Writer.TryComplete(); await reader; await capture.DisposeAsync();
    }
}
