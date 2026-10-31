using HiddenRecorder.Core;
using HiddenRecorder.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HiddenRecorder.Tests;

public sealed class SettingsAutoSaverTests
{
    private static RecorderSettings Options => new() { OutputDirectory = Path.GetTempPath() };

    [Fact] public async Task EditsSaveAutomaticallyWithoutDevicesOrMonitoring()
    {
        var clock = new FakeTimeProvider(); var store = new Store();
        await using var saver = new SettingsAutoSaver(store, clock);
        saver.RequestSave(Options with { SilenceSeconds = 10 });
        clock.Advance(TimeSpan.FromMilliseconds(300));
        saver.RequestSave(Options with { SilenceSeconds = 20, ThresholdDb = -40 });
        clock.Advance(TimeSpan.FromMilliseconds(499)); Assert.Empty(store.Written);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(20, Assert.Single(store.Written).SilenceSeconds);
        Assert.Equal(-40, store.Written[0].ThresholdDb); Assert.False(saver.HasPendingChanges);
    }

    [Fact] public async Task ExitFlushesLastEditBeforeDebounceExpires()
    {
        var store = new Store(); var saver = new SettingsAutoSaver(store, new FakeTimeProvider());
        saver.RequestSave(Options with { PreRollSeconds = 17 });
        await saver.DisposeAsync();
        Assert.Equal(17, Assert.Single(store.Written).PreRollSeconds);
    }

    [Fact] public async Task EditDuringSlowSaveWritesNewestSettingsLastWithoutConcurrentWrites()
    {
        var store = new Store { BlockFirstWrite = true };
        await using var saver = new SettingsAutoSaver(store, new FakeTimeProvider());
        saver.RequestSave(Options with { AudioChannels = 1 });
        var firstFlush = saver.FlushAsync(); await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        saver.RequestSave(Options with { AudioChannels = 2 });
        var exitFlush = saver.FlushAsync();
        store.Release.TrySetResult(); await Task.WhenAll(firstFlush, exitFlush);
        Assert.Equal(new[] { 1, 2 }, store.Written.Select(s => s.AudioChannels));
        Assert.Equal(1, store.MaxConcurrent); Assert.False(saver.HasPendingChanges);
    }

    [Fact] public async Task InvalidEditDoesNotReplaceLastValidSettings()
    {
        var store = new Store(); await using var saver = new SettingsAutoSaver(store, new FakeTimeProvider());
        saver.RequestSave(Options with { ThresholdDb = -30 });
        Assert.Throws<ArgumentException>(() => saver.RequestSave(Options with { ThresholdDb = double.NaN }));
        await saver.FlushAsync(); Assert.Equal(-30, Assert.Single(store.Written).ThresholdDb);
    }

    [Fact] public async Task FailedAutomaticSaveReportsErrorAndCanBeRetriedOnExit()
    {
        var store = new Store { Fail = true }; var clock = new FakeTimeProvider();
        await using var saver = new SettingsAutoSaver(store, clock);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        saver.Failed += ex => failure.TrySetResult(ex);
        saver.RequestSave(Options); clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.IsType<IOException>(await failure.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(saver.HasPendingChanges);
        await Assert.ThrowsAsync<IOException>(() => saver.DisposeAsync().AsTask());
        store.Fail = false; await saver.FlushAsync(); Assert.Single(store.Written);
    }

    [Fact] public async Task SavedSettingsSurviveNewStoreInstanceWithoutStartingCapture()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HR-settings-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        try
        {
            var options = Options with { PreRollSeconds = 12, SilenceSeconds = 37, ThresholdDb = -33, MinimumSoundMs = 120,
                VideoBitrateKbps = 4500, AudioBitrateKbps = 192, AudioChannels = 2, CameraId = "offline-camera", MicrophoneId = "offline-mic",
                VideoMode = new(1280, 720, 30, 1, "video/x-raw,width=1280,height=720,framerate=30/1") };
            await using (var saver = new SettingsAutoSaver(new JsonSettingsStore(path), new FakeTimeProvider())) saver.RequestSave(options);
            Assert.Equal(options, await new JsonSettingsStore(path).LoadAsync());
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private sealed class Store : ISettingsStore
    {
        public List<RecorderSettings> Written { get; } = [];
        public bool Fail, BlockFirstWrite;
        public int MaxConcurrent;
        private int active;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RecorderSettings> LoadAsync() => Task.FromResult(Written.LastOrDefault() ?? Options);
        public async Task SaveAsync(RecorderSettings settings)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref active));
            try
            {
                if (Fail) throw new IOException("Disk unavailable");
                if (BlockFirstWrite && Written.Count == 0) { Started.TrySetResult(); await Release.Task; }
                Written.Add(settings);
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
