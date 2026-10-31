namespace HiddenRecorder.Core;

public enum RecorderState { Idle, Starting, Armed, Recording, Finalizing, Stopping, Faulted, PreviewStarting, Previewing }
public sealed record VideoMode(int Width, int Height, int FpsNumerator, int FpsDenominator, string Caps)
{
    public override string ToString() => $"{Width} × {Height} · {(double)FpsNumerator / FpsDenominator:0.##} FPS";
}
public sealed record CaptureDevice(string Id, string Name, IReadOnlyList<VideoMode> Modes);
public sealed record DeviceCatalog(IReadOnlyList<CaptureDevice> Cameras, IReadOnlyList<CaptureDevice> Microphones);
public sealed record RecorderSettings
{
    public string CameraId { get; init; } = "";
    public string MicrophoneId { get; init; } = "";
    public VideoMode? VideoMode { get; init; }
    public string OutputDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "HiddenRecorder");
    public int PreRollSeconds { get; init; } = 5;
    public int SilenceSeconds { get; init; } = 300;
    public double ThresholdDb { get; init; } = -25;
    public int MinimumSoundMs { get; init; }
    public int VideoBitrateKbps { get; init; } = 6000;
    public int AudioBitrateKbps { get; init; } = 128;
    public int AudioChannels { get; init; } = 1;
    public int PartSeconds { get; init; } = 3600;
    public long ReserveBytes { get; init; } = 1024L * 1024 * 1024;
    public void Validate(bool requireDevices = true)
    {
        if (PreRollSeconds is < 1 or > 30 || SilenceSeconds is < 1 or > 86400 ||
            !double.IsFinite(ThresholdDb) || ThresholdDb is < -60 or > 0 || MinimumSoundMs is < 0 or > 1000 ||
            VideoBitrateKbps is < 250 or > 20000 || AudioBitrateKbps is < 64 or > 320 ||
            AudioChannels is < 1 or > 2 || PartSeconds < 1 || ReserveBytes < 0)
            throw new ArgumentException("Проверьте диапазоны настроек записи.");
        if (!Path.IsPathFullyQualified(OutputDirectory)) throw new ArgumentException("Выберите абсолютный путь папки записи.");
        if (requireDevices && (string.IsNullOrWhiteSpace(CameraId) || string.IsNullOrWhiteSpace(MicrophoneId) || VideoMode is null))
            throw new ArgumentException("Выберите камеру, микрофон и поддерживаемый режим видео.");
    }
}
public abstract record MediaEvent;
public sealed record CaptureReady : MediaEvent;
public sealed record AudioLevel(double PeakDb, double RmsDb, TimeSpan MediaTime, TimeSpan Duration) : MediaEvent;
public sealed record BufferProgress(double Seconds) : MediaEvent;
public sealed record FileOpened(string Path) : MediaEvent;
public sealed record FileClosed(string Path) : MediaEvent;
public sealed record MediaFailure(string Message) : MediaEvent;
public sealed record RecorderSnapshot(RecorderState State, double PeakDb = -90, double RmsDb = -90,
    double BufferedSeconds = 0, double RemainingSeconds = 0, double RecordedSeconds = 0, string CurrentFile = "", string Error = "");
