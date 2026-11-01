using HiddenRecorder.Core;
using HiddenRecorder.Infrastructure;
using System.Text.Json;

try
{
    if (args.Length == 2 && args[0] is "verify-resume" or "verify-resume-hardware")
    {
        await ResumeChecks.RunAsync(args[1], args[0] == "verify-resume-hardware"); return 0;
    }
    if (args.Length == 2 && args[0] == "verify-folders")
    {
        await FolderChecks.RunAsync(args[1]); return 0;
    }
    if (args.Length == 2 && args[0] is "verify" or "verify-hardware")
    {
        await IntegrationChecks.RunAsync(Path.GetFullPath(args[1]), args[0] == "verify-hardware"); return 0;
    }
    if (args.Length == 2 && args[0] == "recover")
    {
        Console.WriteLine(await new FragmentRecovery().RecoverAsync(Path.GetFullPath(args[1]))); return 0;
    }
    await using var media = new GStreamerMedia();
    var devices = await media.EnumerateAsync();
    if (args.Length == 0 || args[0] == "devices") { Console.WriteLine(JsonSerializer.Serialize(devices, new JsonSerializerOptions { WriteIndented = true })); return 0; }
    if (args[0] != "record" || args.Length < 2) throw new ArgumentException("Commands: devices | recover FILE | record OUTPUT_DIRECTORY [SECONDS] | verify OUTPUT_DIRECTORY | verify-hardware OUTPUT_DIRECTORY | verify-folders OUTPUT_DIRECTORY | verify-resume OUTPUT_DIRECTORY | verify-resume-hardware OUTPUT_DIRECTORY");
    var camera = devices.Cameras.First(); var microphone = devices.Microphones.First();
    var mode = camera.Modes.FirstOrDefault(m => m.Width == 1920 && m.Height == 1080 && m.FpsNumerator == 30) ?? camera.Modes.First();
    var settings = new RecorderSettings { CameraId = camera.Id, MicrophoneId = microphone.Id, VideoMode = mode, OutputDirectory = Path.GetFullPath(args[1]), PartSeconds = 10 };
    new StorageGuard().Check(settings, true);
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    TimeSpan mediaTime = TimeSpan.Zero; string? error = null;
    media.Event += value =>
    {
        if (value is CaptureReady) ready.TrySetResult();
        if (value is AudioLevel level) mediaTime = level.MediaTime;
        if (value is MediaFailure failure) { error = failure.Message; ready.TrySetException(new IOException(error)); }
        if (value is FileClosed file) Console.WriteLine("Closed: " + file.Path);
    };
    Console.WriteLine($"Recording real devices: {camera.Name}; {microphone.Name}; {mode}");
    await media.StartAsync(settings);
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    await Task.Delay(TimeSpan.FromSeconds(6));
    if (error is not null) throw new IOException(error);
    await media.BeginAsync(mediaTime);
    await Task.Delay(TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 12));
    await media.FinishAsync(); await media.StopAsync();
    if (error is not null) throw new IOException(error);
    Console.WriteLine("PASS"); return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
