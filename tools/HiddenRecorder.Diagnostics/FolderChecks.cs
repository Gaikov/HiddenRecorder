using System.Collections.Concurrent;
using HiddenRecorder.Core;
using HiddenRecorder.Infrastructure;

internal static class FolderChecks
{
    public static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Use a new test directory.");
        Directory.CreateDirectory(output);
        var root = Path.Combine(output, "selected-output");
        var archive = Path.Combine(output, "verified");
        Directory.CreateDirectory(archive);
        await using var media = new GStreamerMedia(testSource: true);
        var errors = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string>? completed = null;
        long latestTicks = 0;
        media.Event += value =>
        {
            if (value is CaptureReady) ready.TrySetResult();
            if (value is AudioLevel level) Interlocked.Exchange(ref latestTicks, level.MediaTime.Ticks);
            if (value is FileClosed file) completed?.TrySetResult(file.Path);
            if (value is MediaFailure failure)
            {
                errors.Enqueue(failure.Message);
                ready.TrySetException(new IOException(failure.Message));
                completed?.TrySetException(new IOException(failure.Message));
            }
        };
        var options = new RecorderSettings
        {
            CameraId = "synthetic", MicrophoneId = "synthetic", OutputDirectory = root,
            VideoMode = new(640, 360, 30, 1, "video/x-raw,width=640,height=360,framerate=30/1"),
            PreRollSeconds = 1, VideoBitrateKbps = 1000, PartSeconds = 60
        };
        await media.StartAsync(options);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(1500);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var expected = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await media.BeginAsync(TimeSpan.FromTicks(Interlocked.Read(ref latestTicks)));
            if (!Directory.Exists(expected)) throw new IOException("Begin did not recreate the selected daily folder.");
            await Task.Delay(1500);
            await media.FinishAsync();
            var file = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!string.Equals(Path.GetDirectoryName(file), expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Recording switched away from the selected daily folder: " + file);
            // Preserve the test recording, then delete only the now-empty directory we created.
            File.Move(file, Path.Combine(archive, $"event-{attempt}.mp4"));
            Directory.Delete(expected, recursive: false);
            if (Directory.Exists(expected)) throw new IOException("Test folder was not deleted.");
            Console.WriteLine($"PASS event {attempt + 1}: recreated the selected date folder without restarting capture");
        }
        await media.StopAsync();
        if (!errors.IsEmpty) throw new IOException(string.Join(Environment.NewLine, errors));
        if (Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories).Any()) throw new IOException("Unexpected recording outside the verified folder.");
        Console.WriteLine("PASS " + output);
    }
}
