using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HiddenRecorder.Core;
using HiddenRecorder.Infrastructure;

internal static class IntegrationChecks
{
    public static async Task RunAsync(string output, bool hardware)
    {
        Directory.CreateDirectory(output);
        Require(!Directory.EnumerateFiles(output, "*.mp4", SearchOption.AllDirectories).Any(), "Use a new output directory");
        var media = new GStreamerMedia(testSource: !hardware);
        using var power = new PowerGuard();
        var observer = new Observer();
        await using var coordinator = new RecordingCoordinator(media, media, new StorageGuard(), observer, power, observer);
        var opened = new ConcurrentQueue<string>(); var closed = new ConcurrentQueue<string>();
        media.Event += e => { if (e is FileOpened f) opened.Enqueue(f.Path); if (e is FileClosed c) closed.Enqueue(c.Path); };
        var options = new RecorderSettings { CameraId = "synthetic", MicrophoneId = "synthetic",
            VideoMode = new(640, 360, 30, 1, "video/x-raw,width=640,height=360,framerate=30/1"),
            OutputDirectory = output, SilenceSeconds = 3, PartSeconds = 60, VideoBitrateKbps = 1000 };

        async Task WaitFor(RecorderState wanted, double seconds = 15)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                await coordinator.DrainAsync();
                var value = coordinator.Snapshot;
                if (value.State == RecorderState.Faulted) throw new IOException(value.Error);
                if (value.State == wanted) return;
                await Task.Delay(20);
            }
            throw new TimeoutException($"Expected {wanted}; got {coordinator.Snapshot}");
        }
        async Task CheckStateAfter(double seconds, RecorderState state)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds)); await coordinator.DrainAsync();
            Require(coordinator.Snapshot.State == state, $"Expected {state}; got {coordinator.Snapshot}");
        }
        async Task Pulse(int milliseconds = 300)
        {
            await media.SetTestVolumeAsync(.5); await Task.Delay(milliseconds); await media.SetTestVolumeAsync(0);
        }
        async Task WaitForClosedFiles(int count)
        {
            // Native completion and its polled notification are separate asynchronous events.
            var timer = Stopwatch.StartNew();
            while (closed.Count < count && timer.Elapsed.TotalSeconds < 3) await Task.Delay(20);
            Require(closed.Count == count, $"Expected {count} completed files, got {closed.Count}");
        }
        void NoPartialFiles() => Require(!Directory.EnumerateFiles(output, "*.partial.mp4").Any(), "Unfinished MP4 remains after finalization");

        if (hardware)
        {
            var devices = await media.EnumerateAsync();
            var camera = devices.Cameras.First(); var mic = devices.Microphones.First();
            var mode = camera.Modes.FirstOrDefault(m => m.Width == 1920 && m.Height == 1080 && m.FpsNumerator == 30 && m.FpsDenominator == 1) ?? camera.Modes.First();
            Console.WriteLine($"Real camera: {camera.Name}; microphone: {mic.Name}; mode: {mode}");
            options = options with { CameraId = camera.Id, MicrophoneId = mic.Id, VideoMode = mode, VideoBitrateKbps = 6000, ThresholdDb = 0 };
            await coordinator.StartAsync(options); await WaitFor(RecorderState.Armed);
            await CheckStateAfter(4, RecorderState.Armed);
            Require(opened.IsEmpty, "Below-threshold microphone signal created a file");
            Console.WriteLine($"PASS no false triggers; measured peak {coordinator.Snapshot.PeakDb:F1} dBFS");
            await coordinator.StopAsync();
            await coordinator.StartAsync(options with { ThresholdDb = -60 });
            await WaitFor(RecorderState.Recording);
            await CheckStateAfter(6, RecorderState.Recording);
            await coordinator.StopAsync();
            Require(coordinator.Snapshot.State == RecorderState.Idle, "Hardware stop did not reach Idle");
            await WaitForClosedFiles(1); NoPartialFiles();
            Console.WriteLine("PASS real microphone triggered recording through the application coordinator; stop finalized MP4 and released devices");
        }
        else
        {
            await coordinator.StartPreviewAsync(options); await WaitFor(RecorderState.Previewing);
            await media.SetTestVolumeAsync(.5); await CheckStateAfter(.4, RecorderState.Previewing);
            Require(coordinator.Snapshot.PeakDb > -25 && opened.IsEmpty, "Preview must report loud audio without recording");
            await media.SetTestVolumeAsync(0);
            await CheckStateAfter(.3, RecorderState.Previewing);
            Require(coordinator.Snapshot.PeakDb < -25, "Synthetic source did not become quiet before arming");
            await coordinator.StartAsync(options); await WaitFor(RecorderState.Armed);
            await CheckStateAfter(6, RecorderState.Armed);
            Require(opened.IsEmpty && !Directory.EnumerateFiles(output, "*.mp4").Any(), "Silence created a media file");
            Require(coordinator.Snapshot.BufferedSeconds >= 4.9, "Pre-roll did not fill");
            Console.WriteLine("PASS silence produces no files; five-second pre-roll filled");

            await Pulse(); await WaitFor(RecorderState.Recording);
            await CheckStateAfter(1.8, RecorderState.Recording);
            await Pulse();
            var quiet = Stopwatch.StartNew();
            await CheckStateAfter(2, RecorderState.Recording);
            await WaitFor(RecorderState.Armed, 8);
            Require(quiet.Elapsed.TotalSeconds >= 2.8 && quiet.Elapsed.TotalSeconds < 6, "Silence deadline was not extended from the last pulse");
            await WaitForClosedFiles(1); NoPartialFiles();
            Console.WriteLine($"PASS repeated sound extends event; finalization after {quiet.Elapsed.TotalSeconds:F2} s of silence");

            await media.SetTestVolumeAsync(.5); await WaitFor(RecorderState.Recording);
            await CheckStateAfter(4.5, RecorderState.Recording);
            await media.SetTestVolumeAsync(0); await WaitFor(RecorderState.Armed, 8);
            await WaitForClosedFiles(2);
            Console.WriteLine("PASS continuous sound keeps recording beyond the silence interval");

            await coordinator.StopAsync();
            await coordinator.StartAsync(options with { MinimumSoundMs = 500 }); await WaitFor(RecorderState.Armed);
            await Pulse(100); await CheckStateAfter(.7, RecorderState.Armed);
            Require(closed.Count == 2 && opened.Count == 2, "Short pulse bypassed duration filter");
            await media.SetTestVolumeAsync(.5); await WaitFor(RecorderState.Recording);
            await Task.Delay(600); await coordinator.DisarmAsync();
            await CheckStateAfter(.4, RecorderState.Previewing);
            Require(coordinator.Snapshot.PeakDb > -25, "Disarm stopped the microphone meter");
            await coordinator.StopAsync();
            Require(coordinator.Snapshot.State == RecorderState.Idle, "Manual stop did not reach Idle");
            await WaitForClosedFiles(3); NoPartialFiles();
            Console.WriteLine("PASS restart, duration filter, early trigger, manual stop");
        }
        Require(observer.Errors.IsEmpty, string.Join(Environment.NewLine, observer.Errors));
        var report = new { Hardware = hardware, CompletedAt = DateTimeOffset.Now, Files = closed.ToArray(), State = coordinator.Snapshot.State.ToString() };
        await File.WriteAllTextAsync(Path.Combine(output, "integration.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS " + output);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Observer : IUserNotifier, IAppLog
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public void Notify(string title, string message) => Errors.Enqueue(title + ": " + message);
        public void Write(string message, Exception? exception = null)
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {message}");
            if (exception is not null) Errors.Enqueue(exception.ToString());
        }
    }
}
