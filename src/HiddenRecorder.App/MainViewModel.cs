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
    private readonly RecordingCoordinator coordinator;
    private readonly DispatcherQueue dispatcher;
    private RecorderSettings loaded = new();
    private readonly Microsoft.UI.Xaml.DispatcherTimer previewRefresh = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool initialized, previewSuspended;
    public ObservableCollection<CaptureDevice> Cameras { get; } = [];
    public ObservableCollection<CaptureDevice> Microphones { get; } = [];
    public ObservableCollection<VideoMode> Modes { get; } = [];
    [ObservableProperty] public partial CaptureDevice? SelectedCamera { get; set; }
    [ObservableProperty] public partial CaptureDevice? SelectedMicrophone { get; set; }
    [ObservableProperty] public partial VideoMode? SelectedMode { get; set; }
    [ObservableProperty] public partial string OutputDirectory { get; set; } = "";
    [ObservableProperty] public partial double PreRollSeconds { get; set; } = 5;
    [ObservableProperty] public partial double SilenceSeconds { get; set; } = 300;
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
    [ObservableProperty] public partial string Status { get; set; } = "Готово к настройке";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial double PeakDb { get; set; } = -90;
    [ObservableProperty] public partial double RmsDb { get; set; } = -90;
    [ObservableProperty] public partial double BufferPercent { get; set; }
    [ObservableProperty] public partial string Timers { get; set; } = "";
    [ObservableProperty] public partial string CurrentFile { get; set; } = "";
    public string Levels => $"Пик: {PeakDb:0.0} dBFS  ·  RMS: {RmsDb:0.0} dBFS";
    public Action<RecorderState>? StateChanged;
    public MainViewModel(IDeviceCatalog devices, ISettingsStore store, RecordingCoordinator coordinator, DispatcherQueue dispatcher)
    {
        (this.devices, this.store, this.coordinator, this.dispatcher) = (devices, store, coordinator, dispatcher);
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
        if (initialized && !previewSuspended && CanConfigure && e.PropertyName is
            nameof(SelectedCamera) or nameof(SelectedMicrophone) or nameof(SelectedMode) or
            nameof(PreRollSeconds) or nameof(VideoBitrateKbps) or nameof(AudioBitrateKbps) or nameof(AudioChannels))
        { previewRefresh.Stop(); previewRefresh.Start(); }
    }
    public async Task InitializeAsync()
    {
        if (initialized) return;
        try { loaded = await store.LoadAsync(); }
        catch (Exception ex) { Error = "Настройки не загружены: " + ex.Message; }
        OutputDirectory = loaded.OutputDirectory; PreRollSeconds = loaded.PreRollSeconds; SilenceSeconds = loaded.SilenceSeconds;
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
        Busy = true;
        try
        {
            var catalog = await devices.EnumerateAsync(); Cameras.Clear(); Microphones.Clear();
            foreach (var device in catalog.Cameras) Cameras.Add(device);
            foreach (var device in catalog.Microphones) Microphones.Add(device);
            SelectedCamera = Cameras.FirstOrDefault(d => d.Id == loaded.CameraId) ?? Cameras.FirstOrDefault();
            SelectedMicrophone = Microphones.FirstOrDefault(d => d.Id == loaded.MicrophoneId) ?? Microphones.FirstOrDefault();
            Status = Cameras.Count == 0 || Microphones.Count == 0 ? "Подключите камеру и микрофон" : "Выберите параметры и начните наблюдение";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
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
                await coordinator.StopAsync(); Status = "Выберите камеру, микрофон и режим для предпросмотра"; return;
            }
            await coordinator.StartPreviewAsync(ReadSettings()); Error = "";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
    private RecorderSettings ReadSettings()
    {
        static int Integer(double value) => double.IsFinite(value) && value == Math.Truncate(value) ? checked((int)value) : throw new ArgumentException("Введите целые значения параметров.");
        var options = loaded with { CameraId = SelectedCamera?.Id ?? "", MicrophoneId = SelectedMicrophone?.Id ?? "", VideoMode = SelectedMode,
            OutputDirectory = OutputDirectory, PreRollSeconds = Integer(PreRollSeconds), SilenceSeconds = Integer(SilenceSeconds), ThresholdDb = ThresholdDb,
            MinimumSoundMs = Integer(MinimumSoundMs), VideoBitrateKbps = Integer(VideoBitrateKbps), AudioBitrateKbps = Integer(AudioBitrateKbps), AudioChannels = Integer(AudioChannels) };
        options.Validate();
        if (!new[] {64,96,128,160,192,256,320}.Contains(options.AudioBitrateKbps)) throw new ArgumentException("Битрейт AAC: 64, 96, 128, 160, 192, 256 или 320 кбит/с.");
        return options;
    }
    [RelayCommand] public async Task StartAsync()
    {
        if (Busy || !CanConfigure) return; Busy = true; Error = "";
        previewRefresh.Stop(); previewSuspended = false;
        try { loaded = ReadSettings(); await store.SaveAsync(loaded); await coordinator.StartAsync(loaded); }
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
        try { await coordinator.StopAsync(); } catch (Exception ex) { Error = ex.Message; }
    }
    public void PrepareForExit() { previewSuspended = true; previewRefresh.Stop(); }
    private void Apply(RecorderSnapshot value)
    {
        CanConfigure = value.State is RecorderState.Idle or RecorderState.Faulted or RecorderState.Previewing or RecorderState.PreviewStarting;
        Status = value.State switch { RecorderState.Idle => "Камера и микрофон выключены", RecorderState.PreviewStarting => "Подключение камеры и микрофона…", RecorderState.Previewing => "Предпросмотр · запись по звуку выключена", RecorderState.Starting => "Запуск наблюдения…", RecorderState.Armed => "Наблюдение · ожидание звука", RecorderState.Recording => "● Идёт запись", RecorderState.Finalizing => "Сохранение файла…", RecorderState.Stopping => "Остановка…", _ => "Ошибка · устройства остановлены" };
        PeakDb = value.PeakDb; RmsDb = value.RmsDb; BufferPercent = Math.Clamp(value.BufferedSeconds / Math.Max(1, PreRollSeconds) * 100, 0, 100);
        OnPropertyChanged(nameof(Levels));
        Timers = $"Предзапись: {value.BufferedSeconds:0.0} с  ·  Записано: {TimeSpan.FromSeconds(value.RecordedSeconds):hh\\:mm\\:ss}  ·  До остановки: {value.RemainingSeconds:0} с";
        CurrentFile = value.CurrentFile; if (!string.IsNullOrEmpty(value.Error)) Error = value.Error;
        StateChanged?.Invoke(value.State);
    }
}
