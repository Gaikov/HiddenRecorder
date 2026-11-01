using System.Diagnostics;
using HiddenRecorder.Core;
using HiddenRecorder.Infrastructure;

internal static class ResumeChecks
{
    // Exercise the same application lifecycle as power notifications without sleeping the host.
    public static async Task RunAsync(string output, bool hardware)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Use a new test directory.");
        var media = new GStreamerMedia(testSource: !hardware);
        using var power = new PowerGuard();
        var observer = new Observer();
        await using var coordinator = new RecordingCoordinator(media, media, new StorageGuard(), observer, power, observer);
        long levels = 0, ready = 0, closed = 0;
        media.Event += value =>
        {
            if (value is AudioLevel) Interlocked.Increment(ref levels);
            if (value is CaptureReady) Interlocked.Increment(ref ready);
            if (value is FileClosed) Interlocked.Increment(ref closed);
        };
        var options = new RecorderSettings
        {
            CameraId = "synthetic", MicrophoneId = "synthetic", OutputDirectory = output,
            VideoMode = new(640, 360, 30, 1, "video/x-raw,width=640,height=360,framerate=30/1"),
            PreRollSeconds = 1, SilenceSeconds = 3, VideoBitrateKbps = 1000, ThresholdDb = 0
        };
        if (hardware)
        {
            var catalog = await media.EnumerateAsync();
            var camera = catalog.Cameras.First(); var mic = catalog.Microphones.First();
            var mode = camera.Modes.FirstOrDefault(m => m.Width == 1920 && m.Height == 1080 && m.FpsNumerator == 30 && m.FpsDenominator == 1) ?? camera.Modes.First();
            options = options with { CameraId = camera.Id, MicrophoneId = mic.Id, VideoMode = mode, VideoBitrateKbps = 6000 };
            Console.WriteLine($"Devices: {camera.Name}; {mic.Name}; {mode}");
        }
        async Task WaitFor(RecorderState wanted)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                await coordinator.DrainAsync();
                if (coordinator.Snapshot.State == RecorderState.Faulted) throw new IOException(coordinator.Snapshot.Error);
                if (coordinator.Snapshot.State == wanted) return;
                await Task.Delay(20);
            }
            throw new TimeoutException($"Expected {wanted}, got {coordinator.Snapshot}");
        }
        static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }
        await coordinator.StartPreviewAsync(options); await WaitFor(RecorderState.Previewing);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            await Task.Delay(1500);
            Require(coordinator.Snapshot.BufferedSeconds >= .9, "Video/audio buffer did not refill");
            var beforeReady = Interlocked.Read(ref ready);
            await coordinator.SuspendAsync();
            Require(coordinator.Snapshot.State == RecorderState.Suspended && coordinator.Snapshot.BufferedSeconds == 0, "Suspend did not reset capture");
            var suspendedLevels = Interlocked.Read(ref levels);
            await Task.Delay(250); Require(Interlocked.Read(ref levels) == suspendedLevels, "Capture continued while suspended");
            await coordinator.ResumeAsync(); await coordinator.ResumeAsync(); await WaitFor(RecorderState.Previewing);
            await Task.Delay(300);
            Require(Interlocked.Read(ref ready) == beforeReady + 1, "Resume did not open exactly one new pipeline");
            Require(Interlocked.Read(ref levels) > suspendedLevels, "Microphone levels did not restart");
            Require(!Directory.Exists(output), "Preview unexpectedly wrote files");
            Console.WriteLine($"PASS preview suspend/resume cycle {cycle + 1}");
        }
        // Quiet arming must also survive sleep, without recording until a fresh sound.
        await coordinator.StartAsync(options); await WaitFor(RecorderState.Armed);
        await coordinator.SuspendAsync(); await coordinator.ResumeAsync(); await WaitFor(RecorderState.Armed);
        await Task.Delay(500); Require(Interlocked.Read(ref closed) == 0, "Silent resume created a recording");
        await coordinator.DisarmAsync();
        await coordinator.StartAsync(options with { ThresholdDb = -60 });
        if (!hardware) { await WaitFor(RecorderState.Armed); await media.SetTestVolumeAsync(.5); }
        await WaitFor(RecorderState.Recording); await Task.Delay(1500);
        await coordinator.SuspendAsync();
        Require(Interlocked.Read(ref closed) == 1, "Suspend did not finalize the first recording");
        await coordinator.ResumeAsync();
        if (!hardware) { await WaitFor(RecorderState.Armed); await media.SetTestVolumeAsync(.5); }
        await WaitFor(RecorderState.Recording); await Task.Delay(1500);
        await coordinator.StopAsync();
        Require(Interlocked.Read(ref closed) == 2, "Resume/stop did not finalize the second recording");
        Require(!Directory.EnumerateFiles(output, "*.partial.mp4", SearchOption.AllDirectories).Any(), "Unfinished recording remains");
        Require(Directory.EnumerateFiles(output, "*.mp4", SearchOption.AllDirectories).Count() == 2, "Expected two separate events across sleep");
        Console.WriteLine("PASS monitoring restoration, fresh audio triggers, and completed recordings before/after suspend");
        Console.WriteLine("These are application lifecycle checks, not an actual Windows sleep or lid-close test.");
    }
    private sealed class Observer : IAppLog, IUserNotifier
    {
        public void Write(string message, Exception? exception = null) => Console.WriteLine(message + (exception is null ? "" : ": " + exception.Message));
        public void Notify(string title, string message) => Console.WriteLine(title + ": " + message);
    }
}
