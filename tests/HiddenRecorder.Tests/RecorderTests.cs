using HiddenRecorder.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HiddenRecorder.Tests;
public sealed class RecorderTests
{
    [Fact] public async Task SleepRestartsPreviewWithEmptyBufferAndNoMonitoring()
    {
        await using var h = new Harness();
        await h.Coordinator.StartPreviewAsync(h.Options); h.Media.Emit(new CaptureReady());
        h.Media.Emit(new BufferProgress(5)); h.Level(-5); await h.Coordinator.DrainAsync();
        await h.Coordinator.SuspendAsync();
        Assert.Equal(RecorderState.Suspended, h.Coordinator.Snapshot.State);
        Assert.True(h.Media.Stopped); Assert.Equal(0, h.Coordinator.Snapshot.BufferedSeconds);
        h.Media.Emit(new MediaFailure("device disappeared during sleep")); h.Media.Emit(new CaptureReady());
        await h.Advance(3600); Assert.Equal(RecorderState.Suspended, h.Coordinator.Snapshot.State);
        await h.Coordinator.ResumeAsync(); await h.Coordinator.ResumeAsync();
        await h.Advance(1); Assert.Equal(1, h.Media.Starts);
        await h.Advance(1); Assert.Equal(2, h.Media.Starts);
        h.Media.Emit(new CaptureReady()); h.Level(-5); await h.Coordinator.DrainAsync();
        Assert.Equal(RecorderState.Previewing, h.Coordinator.Snapshot.State);
        Assert.False(h.Power.Enabled); Assert.Empty(h.Media.Triggers); Assert.Empty(h.Notifications.Messages);
        await h.Coordinator.ResumeAsync(); await h.Advance(5); Assert.Equal(2, h.Media.Starts);
    }
    [Fact] public async Task SleepFinalizesRecordingAndOnlyFreshSoundStartsNewEvent()
    {
        await using var h = new Harness(); await h.Start(); h.Level(-5); await h.Coordinator.DrainAsync();
        await h.Coordinator.SuspendAsync(); Assert.Equal(1, h.Media.Finishes); Assert.False(h.Power.Enabled);
        await h.Advance(3600); await h.Coordinator.ResumeAsync(); await h.Advance(2);
        h.Level(-5); await h.Coordinator.DrainAsync(); Assert.Single(h.Media.Triggers);
        h.Media.Emit(new CaptureReady()); await h.Coordinator.DrainAsync();
        Assert.Equal(RecorderState.Armed, h.Coordinator.Snapshot.State); Assert.True(h.Power.Enabled);
        h.Level(-5); await h.Coordinator.DrainAsync(); Assert.Equal(2, h.Media.Triggers.Count);
        await h.Advance(299); Assert.Equal(RecorderState.Recording, h.Coordinator.Snapshot.State);
        await h.Advance(1); await h.WaitFor(RecorderState.Armed);
    }
    [Fact] public async Task ResumeRetriesSynchronousAndAsynchronousDeviceErrors()
    {
        await using var h = new Harness(); await h.Start(); await h.Coordinator.SuspendAsync();
        h.Media.FailStart = true; await h.Coordinator.ResumeAsync(); await h.Advance(2);
        Assert.Equal(RecorderState.Reconnecting, h.Coordinator.Snapshot.State); Assert.True(h.Media.Stopped);
        h.Media.FailStart = false; await h.Advance(2);
        h.Media.Emit(new MediaFailure("driver not ready")); await h.Coordinator.DrainAsync();
        Assert.Equal(RecorderState.Reconnecting, h.Coordinator.Snapshot.State);
        await h.Advance(2); h.Media.Emit(new CaptureReady()); await h.Coordinator.DrainAsync();
        Assert.Equal(RecorderState.Armed, h.Coordinator.Snapshot.State);
        Assert.Equal(4, h.Media.Starts); Assert.Empty(h.Notifications.Messages); Assert.Empty(h.Coordinator.Snapshot.Error);
    }
    [Fact] public async Task MissingReadinessStopsAfterFiveResumeAttempts()
    {
        await using var h = new Harness(); await h.Start(); await h.Coordinator.SuspendAsync(); await h.Coordinator.ResumeAsync();
        for (var i = 0; i < 5; i++) { await h.Advance(2); await h.Advance(10); }
        Assert.Equal(RecorderState.Faulted, h.Coordinator.Snapshot.State); Assert.Equal(6, h.Media.Starts);
        Assert.True(h.Media.Stopped); Assert.False(h.Power.Enabled); Assert.Single(h.Notifications.Messages);
        await h.Advance(300); Assert.Equal(6, h.Media.Starts);
    }
    [Fact] public async Task StopCancelsResumeAndDoesNotReopenDevices()
    {
        await using var h = new Harness(); await h.Start(); await h.Coordinator.SuspendAsync(); await h.Coordinator.ResumeAsync();
        await h.Coordinator.StopAsync(); await h.Advance(300); await h.Coordinator.ResumeAsync();
        Assert.Equal(RecorderState.Idle, h.Coordinator.Snapshot.State); Assert.Equal(1, h.Media.Starts);
    }
    [Fact] public async Task RepeatedSleepCancelsPendingResumeUntilNextWake()
    {
        await using var h = new Harness(); await h.Start(); await h.Coordinator.SuspendAsync(); await h.Coordinator.ResumeAsync();
        await h.Coordinator.SuspendAsync(); await h.Coordinator.SuspendAsync(); await h.Advance(300);
        Assert.Equal(1, h.Media.Starts); Assert.Equal(RecorderState.Suspended, h.Coordinator.Snapshot.State);
        await h.Coordinator.ResumeAsync(); await h.Advance(2); h.Media.Emit(new CaptureReady()); await h.Coordinator.DrainAsync();
        Assert.Equal(RecorderState.Armed, h.Coordinator.Snapshot.State); Assert.Equal(2, h.Media.Starts);
    }
    [Fact] public async Task SuspendReleasesDevicesEvenWhenFileFinalizationFails()
    {
        await using var h = new Harness(); await h.Start(); h.Media.FailFinish = true;
        await h.Coordinator.SuspendAsync(); Assert.True(h.Media.Stopped); Assert.False(h.Power.Enabled);
        Assert.Equal(RecorderState.Suspended, h.Coordinator.Snapshot.State);
        h.Media.FailFinish = false; await h.Coordinator.ResumeAsync(); await h.Advance(2);
        h.Media.Emit(new CaptureReady()); await h.Coordinator.DrainAsync(); Assert.Equal(RecorderState.Armed, h.Coordinator.Snapshot.State);
    }
    [Fact] public async Task ResumeAfterDeviceFaultRecoversButAnOrdinaryFaultDoesNotRetry()
    {
        await using var h = new Harness(); await h.Start(); h.Media.Emit(new MediaFailure("camera lost power"));
        await h.Coordinator.DrainAsync(); await h.Advance(300); Assert.Equal(1, h.Media.Starts);
        await h.Coordinator.ResumeAsync(); await h.Advance(2); h.Media.Emit(new CaptureReady()); await h.Coordinator.DrainAsync();
        Assert.Equal(RecorderState.Armed, h.Coordinator.Snapshot.State); Assert.Empty(h.Coordinator.Snapshot.Error);
    }
    [Fact] public async Task ResumeRechecksStorageBeforeReenablingMonitoring()
    {
        await using var h = new Harness(); await h.Start(); await h.Coordinator.SuspendAsync(); h.Disk.Fail = true;
        await h.Coordinator.ResumeAsync(); await h.Advance(2); h.Media.Emit(new CaptureReady()); await h.Coordinator.DrainAsync();
        Assert.Equal(RecorderState.Faulted, h.Coordinator.Snapshot.State); Assert.True(h.Media.Stopped); Assert.False(h.Power.Enabled);
        await h.Advance(300); Assert.Equal(2, h.Media.Starts);
    }
    [Fact] public async Task IdleSleepDoesNotAcquireDevicesOnWake()
    {
        await using var h = new Harness(); await h.Coordinator.SuspendAsync(); await h.Coordinator.ResumeAsync(); await h.Advance(300);
        Assert.Equal(RecorderState.Idle, h.Coordinator.Snapshot.State); Assert.Equal(0, h.Media.Starts);
    }
    [Fact] public async Task DisarmingWhileSuspendedRestoresOnlyPreview()
    {
        await using var h = new Harness(); await h.Start(); await h.Coordinator.SuspendAsync(); await h.Coordinator.DisarmAsync();
        Assert.Equal(RecorderState.Suspended, h.Coordinator.Snapshot.State);
        await h.Coordinator.ResumeAsync(); await h.Advance(2); h.Media.Emit(new CaptureReady()); await h.Coordinator.DrainAsync();
        Assert.Equal(RecorderState.Previewing, h.Coordinator.Snapshot.State); Assert.False(h.Power.Enabled);
    }
    [Fact] public async Task ResumeWaitsForSuspendingFileFinalization()
    {
        await using var h = new Harness(); await h.Start(); h.Level(-5); await h.Coordinator.DrainAsync();
        h.Media.BlockFinish = true;
        var suspend = h.Coordinator.SuspendAsync(); var resume = h.Coordinator.ResumeAsync();
        Assert.False(resume.IsCompleted); Assert.Equal(1, h.Media.Starts);
        h.Media.ReleaseFinish(); await suspend; await resume; await h.Advance(2);
        h.Media.Emit(new CaptureReady()); await h.Coordinator.DrainAsync();
        Assert.Equal(RecorderState.Armed, h.Coordinator.Snapshot.State); Assert.Equal(2, h.Media.Starts); Assert.Single(h.Media.Triggers);
    }
    [Fact] public async Task PreviewUpdatesLevelsButNeverRecordsOrRequiresDiskSpace()
    {
        await using var h = new Harness(); h.Disk.Fail = true;
        await h.Coordinator.StartPreviewAsync(h.Options); h.Media.Emit(new CaptureReady());
        h.Level(-5); await h.Coordinator.DrainAsync(); await h.Advance(600);
        Assert.Equal(RecorderState.Previewing, h.Coordinator.Snapshot.State);
        Assert.Equal(-5, h.Coordinator.Snapshot.PeakDb); Assert.Empty(h.Media.Triggers);
        Assert.False(h.Power.Enabled); Assert.False(h.Media.Stopped);
    }
    [Fact] public async Task ArmingPreviewRetainsCaptureAndOnlySubsequentSoundsTrigger()
    {
        await using var h = new Harness();
        await h.Coordinator.StartPreviewAsync(h.Options); h.Media.Emit(new CaptureReady()); h.Level(-5);
        await h.Coordinator.DrainAsync(); await h.Coordinator.StartAsync(h.Options with { ThresholdDb = -20 });
        Assert.Equal(1, h.Media.Starts); Assert.Empty(h.Media.Triggers); Assert.True(h.Power.Enabled);
        h.Level(-5); await h.Coordinator.DrainAsync(); Assert.Single(h.Media.Triggers);
    }
    [Fact] public async Task DisarmingFinalizesFileButKeepsLivePreview()
    {
        await using var h = new Harness(); await h.Start(); h.Level(-5); await h.Coordinator.DrainAsync();
        await h.Coordinator.DisarmAsync(); h.Level(-3); await h.Coordinator.DrainAsync(); await h.Advance(600);
        Assert.Equal(RecorderState.Previewing, h.Coordinator.Snapshot.State);
        Assert.Equal(-3, h.Coordinator.Snapshot.PeakDb); Assert.Single(h.Media.Triggers);
        Assert.False(h.Media.Stopped); Assert.False(h.Power.Enabled);
        await h.Coordinator.StopAsync(); Assert.True(h.Media.Stopped);
    }
    [Fact] public async Task ChangingPreviewProfileRestartsCaptureAndRejectsStaleReadiness()
    {
        await using var h = new Harness();
        await h.Coordinator.StartPreviewAsync(h.Options); h.Media.Emit(new CaptureReady()); await h.Coordinator.DrainAsync();
        await h.Coordinator.StartPreviewAsync(h.Options with { AudioChannels = 2 });
        Assert.Equal(2, h.Media.Starts); Assert.Equal(RecorderState.PreviewStarting, h.Coordinator.Snapshot.State);
        h.Media.Emit(new CaptureReady()); await h.Coordinator.DrainAsync(); Assert.Equal(RecorderState.Previewing, h.Coordinator.Snapshot.State);
    }
    [Fact] public async Task DisarmDuringFinalizationDoesNotStartPendingEvent()
    {
        await using var h = new Harness(); h.Media.BlockFinish = true; await h.Start();
        h.Level(-5); await h.Coordinator.DrainAsync(); await h.Advance(300);
        h.Level(-5); await h.Coordinator.DrainAsync();
        var disarm = h.Coordinator.DisarmAsync(); h.Media.ReleaseFinish(); await disarm; await h.Coordinator.DrainAsync();
        Assert.Equal(RecorderState.Previewing, h.Coordinator.Snapshot.State); Assert.Single(h.Media.Triggers);
    }
    [Fact] public async Task SilenceDoesNotCreateFiles()
    {
        await using var h = new Harness(); await h.Start();
        h.Level(-40); await h.Coordinator.DrainAsync(); await h.Advance(600);
        Assert.Empty(h.Media.Triggers); Assert.Equal(RecorderState.Armed, h.Coordinator.Snapshot.State);
    }
    [Fact] public async Task NewSoundRenewsFiveMinuteDeadline()
    {
        await using var h = new Harness(); await h.Start();
        h.Level(-10); await h.Coordinator.DrainAsync(); Assert.Single(h.Media.Triggers);
        await h.Advance(240); h.Level(-10); await h.Coordinator.DrainAsync();
        await h.Advance(60); Assert.Equal(RecorderState.Recording, h.Coordinator.Snapshot.State);
        await h.Advance(239); Assert.Equal(RecorderState.Recording, h.Coordinator.Snapshot.State);
        await h.Advance(1); await h.WaitFor(RecorderState.Armed); Assert.Equal(1, h.Media.Finishes);
    }
    [Fact] public async Task ContinuousSoundKeepsSameRecording()
    {
        await using var h = new Harness(); await h.Start();
        for (var i = 0; i < 20; i++) { h.Level(-5); await h.Coordinator.DrainAsync(); await h.Advance(100); }
        Assert.Single(h.Media.Triggers); Assert.Equal(RecorderState.Recording, h.Coordinator.Snapshot.State);
    }
    [Fact] public async Task SoundDuringFinalizationStartsAnotherEvent()
    {
        await using var h = new Harness(); h.Media.BlockFinish = true; await h.Start();
        h.Level(-10); await h.Coordinator.DrainAsync(); await h.Advance(300);
        Assert.Equal(RecorderState.Finalizing, h.Coordinator.Snapshot.State);
        h.Level(-10); await h.Coordinator.DrainAsync(); h.Media.ReleaseFinish();
        await h.WaitFor(RecorderState.Recording); Assert.Equal(2, h.Media.Triggers.Count);
    }
    [Fact] public async Task SoundAtDeadlineRenewsBeforeNextTick()
    {
        await using var h = new Harness(); await h.Start(); h.Level(-10); await h.Coordinator.DrainAsync();
        await h.Advance(299.98); h.Level(-10); await h.Coordinator.DrainAsync(); await h.Advance(.02);
        Assert.Equal(RecorderState.Recording, h.Coordinator.Snapshot.State); Assert.Single(h.Media.Triggers);
    }
    [Fact] public async Task DiskFailureStopsCaptureAndNotifiesWithoutDeletingFiles()
    {
        await using var h = new Harness(); await h.Start(); h.Level(-10); await h.Coordinator.DrainAsync();
        h.Disk.Fail = true; await h.Advance(1); await h.WaitFor(RecorderState.Faulted);
        Assert.True(h.Media.Stopped); Assert.False(h.Power.Enabled); Assert.Single(h.Notifications.Messages);
    }
    [Fact] public async Task DeviceFailureIsVisibleAndRestartIsManual()
    {
        await using var h = new Harness(); await h.Start(); h.Media.Emit(new MediaFailure("device disconnected"));
        await h.Coordinator.DrainAsync(); Assert.Equal(RecorderState.Faulted, h.Coordinator.Snapshot.State);
        await h.Advance(60); Assert.Equal(1, h.Media.Starts);
        await h.Start(); Assert.Equal(2, h.Media.Starts);
    }
    [Fact] public async Task StopFinalizesAndReleasesDevices()
    {
        await using var h = new Harness(); await h.Start(); h.Level(-10); await h.Coordinator.DrainAsync();
        await h.Coordinator.StopAsync(); Assert.Equal(RecorderState.Idle, h.Coordinator.Snapshot.State); Assert.True(h.Media.Stopped); Assert.False(h.Power.Enabled);
    }
    [Fact] public async Task BackwardWallClockChangeDoesNotExtendDeadline()
    {
        await using var h = new Harness(); await h.Start(); h.Level(-10); await h.Coordinator.DrainAsync();
        h.Time.AdjustTime(h.Time.GetUtcNow().AddHours(-3));
        await h.Advance(300); await h.WaitFor(RecorderState.Armed);
    }
    [Fact] public void DurationFilterRequiresConsecutiveIntervals()
    {
        var policy = new SoundTriggerPolicy(-25, TimeSpan.FromMilliseconds(60));
        Assert.False(policy.Observe(Level(-10, 20))); Assert.False(policy.Observe(Level(-10, 40)));
        Assert.False(policy.Observe(Level(-40, 60))); Assert.False(policy.Observe(Level(-10, 80)));
        Assert.False(policy.Observe(Level(-10, 100))); Assert.True(policy.Observe(Level(-10, 120)));
        Assert.True(policy.Observe(Level(-10, 140)));
    }
    [Fact] public void DisabledDurationFilterAcceptsSingleImpulse()
    {
        var policy = new SoundTriggerPolicy(-25, TimeSpan.Zero);
        Assert.True(policy.Observe(Level(-10, 20))); Assert.False(policy.Observe(Level(-40, 40)));
    }
    [Fact] public void MissingAudioIntervalsResetDurationFilter()
    {
        var policy = new SoundTriggerPolicy(-25, TimeSpan.FromMilliseconds(100));
        Assert.False(policy.Observe(Level(-10, 20))); Assert.False(policy.Observe(Level(-10, 1000)));
    }
    [Theory] [InlineData(0)] [InlineData(31)] public void RejectsOutOfRangePreRoll(int pre) => Assert.Throws<ArgumentException>(() => new RecorderSettings { PreRollSeconds = pre }.Validate(false));
    private static AudioLevel Level(double peak, int milliseconds) => new(peak, peak - 5, TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromMilliseconds(20));

    private sealed class Harness : IAsyncDisposable
    {
        public IndependentClock Time { get; } = new();
        public FakeMedia Media { get; } = new(); public FakeDisk Disk { get; } = new();
        public FakeNotifications Notifications { get; } = new(); public FakePower Power { get; } = new();
        public RecordingCoordinator Coordinator { get; }
        public RecorderSettings Options { get; } = new() { CameraId = "camera", MicrophoneId = "mic", VideoMode = new(640,360,30,1,"video/x-raw"), OutputDirectory = Path.GetTempPath() };
        public Harness() { Coordinator = new(Media, Media, Disk, Notifications, Power, new FakeLog(), Time); }
        public async Task Start()
        {
            await Coordinator.StartAsync(Options);
            Media.Emit(new CaptureReady()); await Coordinator.DrainAsync();
        }
        public void Level(double peak) => Media.Emit(new AudioLevel(peak, peak - 5, TimeSpan.FromTicks(Time.GetTimestamp()), TimeSpan.FromMilliseconds(20)));
        public async Task Advance(double seconds) { Time.Advance(TimeSpan.FromSeconds(seconds)); await Coordinator.DrainAsync(); }
        public async Task WaitFor(RecorderState state)
        {
            for (var i = 0; i < 100; i++) { await Coordinator.DrainAsync(); if (Coordinator.Snapshot.State == state) return; await Task.Delay(10); }
            Assert.Equal(state, Coordinator.Snapshot.State);
        }
        public async ValueTask DisposeAsync() { Media.ReleaseFinish(); await Coordinator.DisposeAsync(); }
    }
    private sealed class FakeMedia : ICaptureSession, IEventRecorder
    {
        public event Action<MediaEvent>? Event;
        public List<TimeSpan> Triggers { get; } = [];
        public bool Stopped, BlockFinish, FailStart, FailFinish; public int Starts, Finishes;
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(MediaEvent value) => Event?.Invoke(value);
        public Task StartAsync(RecorderSettings settings, CancellationToken cancellationToken = default) { Starts++; Stopped = false; if (FailStart) throw new IOException("Device not available"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { Stopped = true; return Task.CompletedTask; }
        public Task BeginAsync(TimeSpan time, CancellationToken cancellationToken = default) { Triggers.Add(time); return Task.CompletedTask; }
        public Task FinishAsync(CancellationToken cancellationToken = default) { Finishes++; if (FailFinish) throw new IOException("Cannot finalize"); return BlockFinish ? completion.Task : Task.CompletedTask; }
        public void ReleaseFinish() { BlockFinish = false; completion.TrySetResult(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeDisk : IStorageGuard { public bool Fail; public void Check(RecorderSettings settings, bool probeWrite) { if (Fail) throw new IOException("disk full"); } }
    private sealed class FakeNotifications : IUserNotifier { public List<string> Messages { get; } = []; public void Notify(string title, string message) => Messages.Add(message); }
    private sealed class FakePower : IPowerGuard { public bool Enabled; public void SetMonitoring(bool enabled) => Enabled = enabled; }
    private sealed class FakeLog : IAppLog { public void Write(string message, Exception? exception = null) { } }
    private sealed class IndependentClock : TimeProvider
    {
        private readonly FakeTimeProvider timers = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        private long ticks; private TimeSpan wallOffset;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => timers.GetUtcNow() + wallOffset;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => timers.CreateTimer(callback, state, dueTime, period);
        public void Advance(TimeSpan duration) { ticks += duration.Ticks; timers.Advance(duration); }
        public void AdjustTime(DateTimeOffset value) { wallOffset = value - timers.GetUtcNow(); }
    }
}
