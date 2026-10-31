using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HiddenRecorder.Core;
using Microsoft.Win32.SafeHandles;

namespace HiddenRecorder.Infrastructure;

public static class MediaRuntime
{
    private static bool initialized;
    public static string Root { get; private set; } = "";
    public static void Initialize()
    {
        if (initialized) return;
        var root = Environment.GetEnvironmentVariable("HIDDENRECORDER_GST_ROOT");
        if (string.IsNullOrEmpty(root)) root = Path.Combine(AppContext.BaseDirectory, "gstreamer");
        if (!Directory.Exists(root))
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".tools", "gstreamer"))) directory = directory.Parent;
            if (directory is not null) root = Path.Combine(directory.FullName, ".tools", "gstreamer");
        }
        Root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(Root, "bin", "gstreamer-1.0-0.dll"))) throw new FileNotFoundException("GStreamer libraries are missing. Extract the entire application package.");
        AddDllDirectory(Path.Combine(Root, "bin"));
        Environment.SetEnvironmentVariable("PATH", Path.Combine(Root, "bin") + ";" + Environment.GetEnvironmentVariable("PATH"));
        Environment.SetEnvironmentVariable("GST_PLUGIN_PATH_1_0", Path.Combine(Root, "lib", "gstreamer-1.0"));
        Environment.SetEnvironmentVariable("GST_PLUGIN_SYSTEM_PATH_1_0", Path.Combine(Root, "lib", "gstreamer-1.0"));
        Environment.SetEnvironmentVariable("GST_PLUGIN_SCANNER", Path.Combine(Root, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe"));
        initialized = true;
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint AddDllDirectory(string directory);
}

internal sealed class MediaHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public MediaHandle() : base(true) { SetHandle(Native.hr_create()); }
    protected override bool ReleaseHandle() { Native.hr_destroy(handle); return true; }
}
internal static unsafe class Native
{
    private const string Library = "HiddenRecorder.Media.dll";
    [StructLayout(LayoutKind.Sequential)] internal struct Config
    {
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string Camera;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string Microphone;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string Caps;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string Output;
        public int PreSeconds, VideoKbps, AudioKbps, Channels, PartSeconds, TestSource;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Event
    {
        public int Type;
        public double First, Second;
        public long TimeNs, DurationNs;
        public fixed byte Text[2048];
        public string Message { get { fixed (byte* text = Text) return Marshal.PtrToStringUTF8((nint)text) ?? ""; } }
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint hr_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hr_destroy(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_catalog(byte[] buffer, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_start(MediaHandle handle, ref Config config);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_stop(MediaHandle handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_begin(MediaHandle handle, long timeNs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_finish(MediaHandle handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_test_volume(MediaHandle handle, double volume);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_poll(MediaHandle handle, out Event result);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hr_preview(MediaHandle handle, nuint hwnd, int width, int height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint hr_last_error();
    internal static void Check(int result) { if (result == 0) throw new IOException(Marshal.PtrToStringUTF8(hr_last_error()) ?? "Media engine error"); }
}

public sealed class GStreamerMedia : ICaptureSession, IEventRecorder, IDeviceCatalog, IPreviewSurface
{
    private readonly MediaHandle handle;
    private readonly SemaphoreSlim operations = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task poller;
    private nint window;
    private int width = 640, height = 360;
    private bool disposed;
    private readonly bool testSource;
    public event Action<MediaEvent>? Event;
    public GStreamerMedia() : this(false) { }
    internal GStreamerMedia(bool testSource)
    {
        this.testSource = testSource;
        MediaRuntime.Initialize(); handle = new();
        if (handle.IsInvalid) throw new IOException("Could not initialize the media engine.");
        poller = Task.Run(PollAsync);
    }
    public async Task<DeviceCatalog> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var buffer = new byte[1024 * 1024]; Native.Check(Native.hr_catalog(buffer, buffer.Length));
            var length = Array.IndexOf(buffer, (byte)0);
            return JsonSerializer.Deserialize<DeviceCatalog>(buffer.AsSpan(0, length), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new([], []);
        }, cancellationToken);
    }
    public Task StartAsync(RecorderSettings settings, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        var config = new Native.Config { Camera = settings.CameraId, Microphone = settings.MicrophoneId, Caps = settings.VideoMode!.Caps,
            Output = settings.OutputDirectory, PreSeconds = settings.PreRollSeconds, VideoKbps = settings.VideoBitrateKbps,
            AudioKbps = settings.AudioBitrateKbps, Channels = settings.AudioChannels, PartSeconds = settings.PartSeconds, TestSource = testSource ? 2 : 0 };
        Native.hr_preview(handle, (nuint)window, width, height); Native.Check(Native.hr_start(handle, ref config));
    }, cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => RunAsync(() => Native.Check(Native.hr_stop(handle)), cancellationToken);
    public Task BeginAsync(TimeSpan triggerTime, CancellationToken cancellationToken = default) => RunAsync(() => Native.Check(Native.hr_begin(handle, triggerTime.Ticks * 100)), cancellationToken);
    public Task FinishAsync(CancellationToken cancellationToken = default) => RunAsync(() => Native.Check(Native.hr_finish(handle)), cancellationToken);
    internal Task SetTestVolumeAsync(double volume) => RunAsync(() => Native.Check(Native.hr_test_volume(handle, volume)), default);
    private async Task RunAsync(Action action, CancellationToken cancellationToken)
    {
        await operations.WaitAsync(cancellationToken);
        try { await Task.Run(action, cancellationToken); } finally { operations.Release(); }
    }
    public void Attach(nint windowHandle) { window = windowHandle; UpdatePreview(); }
    public void Resize(int w, int h) { w = Math.Max(1, w); h = Math.Max(1, h); if (width == w && height == h) return; width = w; height = h; UpdatePreview(); }
    private void UpdatePreview()
    {
        if (disposed) return;
        _ = RunAsync(() => Native.hr_preview(handle, (nuint)window, width, height), lifetime.Token)
            .ContinueWith(t => { if (t.Exception is { } e && !disposed) Event?.Invoke(new MediaFailure(e.GetBaseException().Message)); }, TaskContinuationOptions.OnlyOnFaulted);
    }
    private async Task PollAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                while (Native.hr_poll(handle, out var value) != 0)
                {
                    MediaEvent? item = value.Type switch
                    {
                        1 => new CaptureReady(),
                        2 => new AudioLevel(value.First, value.Second, TimeSpan.FromTicks(value.TimeNs / 100), TimeSpan.FromTicks(value.DurationNs / 100)),
                        3 => new BufferProgress(value.First), 4 => new FileOpened(value.Message), 5 => new FileClosed(value.Message),
                        6 => new MediaFailure(value.Message), _ => null
                    };
                    if (item is not null) Event?.Invoke(item);
                }
                await Task.Delay(10, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Event?.Invoke(new MediaFailure(ex.Message)); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        try { await StopAsync(); } finally { lifetime.Cancel(); await poller; handle.Dispose(); operations.Dispose(); lifetime.Dispose(); }
    }
}
