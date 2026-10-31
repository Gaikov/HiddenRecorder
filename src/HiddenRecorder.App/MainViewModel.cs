using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HiddenRecorder.Core;
using HiddenRecorder.Infrastructure;
using Microsoft.UI.Dispatching;

namespace HiddenRecorder.App;
public partial class MainViewModel : ObservableObject
{
    private readonly IDeviceCatalog devices;
    private readonly ISettingsStore store;
    private readonly SettingsAutoSaver settingsSaver;
    private readonly RecordingCoordinator coordinator;
    private readonly DispatcherQueue dispatcher;
    private RecorderSettings loaded = new();
    private readonly Microsoft.UI.Xaml.DispatcherTimer previewRefresh = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool initialized, previewSuspended, refreshingDevices, closing, settingsInputValid = true;
    public ObservableCollection<CaptureDevice> Cameras { get; } = [];
    public ObservableCollection<CaptureDevice> Microphones { get; } = [];
    public ObservableCollection<VideoMode> Modes { get; } = [];
    [ObservableProperty] public partial CaptureDevice? SelectedCamera { get; set; }
    [ObservableProperty] public partial CaptureDevice? SelectedMicrophone { get; set; }
    [ObservableProperty] public partial VideoMode? SelectedMode { get; set; }
    [ObservableProperty] public partial string OutputDirectory { get; set; } = "";
    [ObservableProperty] public partial double PreRollSeconds { get; set; } = 5;
    [ObservableProperty] public partial double SilenceMinutes { get; set; } = 5;
    [ObservableProperty] public partial double ThresholdDb { get; set; } = -25;
    [ObservableProperty] public partial double MinimumSoundMs { get; set; }
    [ObservableProperty] public partial double VideoBitrateKbps { get; set; } = 6000;
    [ObservableProperty] public partial double AudioBitrateKbps { get; set; } = 128;
    [ObservableProperty] public partial double AudioChannels { get; set; } = 1;
    public int AudioChannelIndex { get => AudioChannels == 2 ? 1 : 0; set => AudioChannels = value == 1 ? 2 : 1; }
    partial void OnAudioChannelsChanged(double value) => OnPropertyChanged(nameof(AudioChannelIndex));
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditSettings))] public partial bool CanConfigure { get; set; } = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditSettings))] public partial bool Busy { get; set; }
    public bool CanEditSettings => CanConfigure && !Busy;
    [ObservableProperty] public partial string Status { get; set; } = "Ready to configure";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string SettingsMessage { get; set; } = "Settings are saved automatically";
    [ObservableProperty] public partial double PeakDb { get; set; } = -90;
    [ObservableProperty] public partial double RmsDb { get; set; } = -90;
    [ObservableProperty] public partial double BufferPercent { get; set; }
    [ObservableProperty] public partial string Timers { get; set; } = "";
    [ObservableProperty] public partial string CurrentFile { get; set; } = "";
    public string Levels => $"Peak: {PeakDb:0.0} dBFS  ·  RMS: {RmsDb:0.0} dBFS";
    public Action<RecorderState>? StateChanged;
    public MainViewModel(IDeviceCatalog devices, ISettingsStore store, RecordingCoordinator coordinator, DispatcherQueue dispatcher)
    {
        (this.devices, this.store, this.coordinator, this.dispatcher) = (devices, store, coordinator, dispatcher);
        settingsSaver = new(store);
        settingsSaver.Saved += () => dispatcher.TryEnqueue(() =>
        {
            if (settingsInputValid && !settingsSaver.HasPendingChanges) SettingsMessage = "Settings saved";
        });
        settingsSaver.Failed += ex => dispatcher.TryEnqueue(() =>
        {
            if (settingsSaver.HasPendingChanges) SettingsMessage = "Settings not saved: " + ex.Message;
        });
        coordinator.Changed += value => dispatcher.TryEnqueue(() => Apply(value));
        previewRefresh.Tick += async (_, _) =>
        {
            if (Busy) return;
            previewRefresh.Stop();
            if (CanConfigure && !previewSuspended) await PreviewAsync();
        };
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (initialized && !refreshingDevices && !closing && e.PropertyName is
            nameof(SelectedCamera) or nameof(SelectedMicrophone) or nameof(SelectedMode) or nameof(OutputDirectory) or
            nameof(PreRollSeconds) or nameof(SilenceMinutes) or nameof(ThresholdDb) or nameof(MinimumSoundMs) or
            nameof(VideoBitrateKbps) or nameof(AudioBitrateKbps) or nameof(AudioChannels)) QueueSettingsSave();
        if (initialized && !previewSuspended && CanConfigure && e.PropertyName is
            nameof(SelectedCamera) or nameof(SelectedMicrophone) or nameof(SelectedMode) or
            nameof(PreRollSeconds) or nameof(VideoBitrateKbps) or nameof(AudioBitrateKbps) or nameof(AudioChannels))
        { previewRefresh.Stop(); previewRefresh.Start(); }
    }
    public async Task InitializeAsync()
    {
        if (initialized) return;
        try { loaded = await store.LoadAsync(); }
        catch (Exception ex) { Error = "Could not load settings: " + ex.Message; }
        OutputDirectory = loaded.OutputDirectory; PreRollSeconds = loaded.PreRollSeconds; SilenceMinutes = loaded.SilenceSeconds / 60d;
        ThresholdDb = loaded.ThresholdDb; MinimumSoundMs = loaded.MinimumSoundMs; VideoBitrateKbps = loaded.VideoBitrateKbps;
        AudioBitrateKbps = loaded.AudioBitrateKbps; AudioChannels = loaded.AudioChannels;
        await RefreshAsync();
        initialized = true;
        await PreviewAsync();
    }
    partial void OnSelectedCameraChanged(CaptureDevice? value)
    {
        Modes.Clear(); if (value is not null) foreach (var mode in value.Modes.OrderByDescending(m => m.Width).ThenByDescending(m => (double)m.FpsNumerator / m.FpsDenominator)) Modes.Add(mode);
        SelectedMode = Modes.FirstOrDefault(m => m.Caps == loaded.VideoMode?.Caps)
            ?? Modes.FirstOrDefault(m => m.Width == 1920 && m.Height == 1080 && m.FpsNumerator == 30 && m.FpsDenominator == 1);
    }
    [RelayCommand] private async Task RefreshAsync()
    {
        if (!CanConfigure || Busy) return;
        Busy = true; refreshingDevices = true;
        try
        {
            var catalog = await devices.EnumerateAsync(); Cameras.Clear(); Microphones.Clear();
            foreach (var device in catalog.Cameras) Cameras.Add(device);
            foreach (var device in catalog.Microphones) Microphones.Add(device);
            SelectedCamera = Cameras.FirstOrDefault(d => d.Id == loaded.CameraId) ?? Cameras.FirstOrDefault();
            SelectedMicrophone = Microphones.FirstOrDefault(d => d.Id == loaded.MicrophoneId) ?? Microphones.FirstOrDefault();
            Status = Cameras.Count == 0 || Microphones.Count == 0 ? "Connect a camera and microphone" : "Choose your settings and start monitoring";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { refreshingDevices = false; Busy = false; }
        if (initialized) { previewSuspended = false; await PreviewAsync(); }
    }
    private async Task PreviewAsync()
    {
        if (Busy || !CanConfigure || previewSuspended) return;
        Busy = true;
        try
        {
            if (SelectedCamera is null || SelectedMicrophone is null || SelectedMode is null)
            {
                await coordinator.StopAsync(); Status = "Select a camera, microphone, and video mode for preview"; return;
            }
            await coordinator.StartPreviewAsync(ReadSettings()); Error = "";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
    private RecorderSettings ReadSettings(bool requireDevices = true)
    {
        static int Integer(double value) => double.IsFinite(value) && value == Math.Truncate(value) ? checked((int)value) : throw new ArgumentException("Enter whole numbers for these settings.");
        if (requireDevices && (SelectedCamera is null || SelectedMicrophone is null || SelectedMode is null))
            throw new ArgumentException("Select a camera, microphone, and supported video mode.");
        if (!double.IsFinite(SilenceMinutes) || SilenceMinutes < 1d / 60 || SilenceMinutes > 1440)
            throw new ArgumentException("Stop delay must be between 1/60 and 1440 minutes (1 second to 24 hours).");
        var options = loaded with { CameraId = SelectedCamera?.Id ?? loaded.CameraId, MicrophoneId = SelectedMicrophone?.Id ?? loaded.MicrophoneId,
            VideoMode = SelectedCamera is null ? loaded.VideoMode : SelectedMode,
            OutputDirectory = OutputDirectory, PreRollSeconds = Integer(PreRollSeconds), SilenceSeconds = (int)Math.Round(SilenceMinutes * 60, MidpointRounding.AwayFromZero), ThresholdDb = ThresholdDb,
            MinimumSoundMs = Integer(MinimumSoundMs), VideoBitrateKbps = Integer(VideoBitrateKbps), AudioBitrateKbps = Integer(AudioBitrateKbps), AudioChannels = Integer(AudioChannels) };
        options.Validate(requireDevices);
        if (!new[] {64,96,128,160,192,256,320}.Contains(options.AudioBitrateKbps)) throw new ArgumentException("AAC bitrate must be 64, 96, 128, 160, 192, 256, or 320 kbit/s.");
        return options;
    }
    private void QueueSettingsSave()
    {
        try
        {
            var options = ReadSettings(requireDevices: false);
            settingsInputValid = true; loaded = options;
            settingsSaver.RequestSave(options);
            SettingsMessage = settingsSaver.HasPendingChanges ? "Saving settings…" : "Settings saved";
        }
        catch (ArgumentException ex) { settingsInputValid = false; SettingsMessage = "Changes not saved: " + ex.Message; }
        catch (OverflowException) { settingsInputValid = false; SettingsMessage = "Changes not saved: the number is too large."; }
    }
    public async Task SaveSettingsBeforeExitAsync()
    {
        if (initialized) QueueSettingsSave();
        closing = true;
        try { await settingsSaver.DisposeAsync(); }
        catch { closing = false; throw; }
    }
    [RelayCommand] public async Task StartAsync()
    {
        if (Busy || !CanConfigure) return; Busy = true; Error = "";
        previewRefresh.Stop(); previewSuspended = false;
        try { loaded = ReadSettings(); settingsSaver.RequestSave(loaded); await settingsSaver.FlushAsync(); await coordinator.StartAsync(loaded); }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
    [RelayCommand] public async Task StopAsync()
    {
        if (Busy) return; Busy = true;
        try { await coordinator.DisarmAsync(); } catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
    public async Task SuspendAsync()
    {
        PrepareForExit();
        try { await settingsSaver.FlushAsync(); } catch (Exception ex) { SettingsMessage = "Settings not saved: " + ex.Message; }
        try { await coordinator.StopAsync(); } catch (Exception ex) { Error = ex.Message; }
    }
    public void PrepareForExit() { previewSuspended = true; previewRefresh.Stop(); }
    private void Apply(RecorderSnapshot value)
    {
        CanConfigure = value.State is RecorderState.Idle or RecorderState.Faulted or RecorderState.Previewing or RecorderState.PreviewStarting;
        Status = value.State switch { RecorderState.Idle => "Camera and microphone off", RecorderState.PreviewStarting => "Connecting camera and microphone…", RecorderState.Previewing => "Preview · sound-triggered recording off", RecorderState.Starting => "Starting monitoring…", RecorderState.Armed => "Monitoring · waiting for sound", RecorderState.Recording => "● Recording", RecorderState.Finalizing => "Saving file…", RecorderState.Stopping => "Stopping…", _ => "Error · devices stopped" };
        PeakDb = value.PeakDb; RmsDb = value.RmsDb; BufferPercent = Math.Clamp(value.BufferedSeconds / Math.Max(1, PreRollSeconds) * 100, 0, 100);
        OnPropertyChanged(nameof(Levels));
        Timers = $"Pre-recording: {value.BufferedSeconds:0.0} s  ·  Recorded: {TimeSpan.FromSeconds(value.RecordedSeconds):hh\\:mm\\:ss}  ·  Time remaining: {value.RemainingSeconds:0} s";
        CurrentFile = value.CurrentFile; if (!string.IsNullOrEmpty(value.Error)) Error = value.Error;
        StateChanged?.Invoke(value.State);
    }
}
