using HiddenRecorder.Core;
using HiddenRecorder.Infrastructure;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HiddenRecorder.App;
public sealed class MainWindow : Window
{
    private readonly Grid root = new() { Padding = new Thickness(24), ColumnSpacing = 24, RowSpacing = 16, RequestedTheme = ElementTheme.Dark,
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 22, 29)) };
    private readonly Border preview = new() { Background = new SolidColorBrush(Colors.Black), MinHeight = 220, CornerRadius = new CornerRadius(8) };
    private readonly RollingLog log = new();
    private readonly PowerGuard power = new();
    private readonly FragmentRecovery recovery = new();
    private GStreamerMedia? media;
    private RecordingCoordinator? coordinator;
    private DesktopIntegration? desktop;
    private MainViewModel? viewModel;
    private readonly DispatcherTimer layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool exiting, hidden, dialogOpen;
    public MainWindow()
    {
        Title = "HiddenRecorder — запись по звуку";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 880));
        Content = root;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            desktop = new(hwnd, DispatcherQueue);
            media = new(); media.Attach(desktop.PreviewWindow);
            coordinator = new(media, media, new StorageGuard(), desktop, power, log);
            viewModel = new(media, new JsonSettingsStore(), coordinator, DispatcherQueue);
            root.DataContext = viewModel;
            BuildContent();
            viewModel.StateChanged = state => { desktop.SetState(state); UpdatePreview(); };
            desktop.ShowRequested += Show;
            desktop.StartRequested += async () => await viewModel.StartAsync();
            desktop.StopRequested += async () => await viewModel.StopAsync();
            desktop.ExitRequested += async () => await ExitAsync();
            desktop.SuspendRequested += async () => await viewModel.SuspendAsync();
            AppWindow.Closing += (_, args) => { if (!exiting) { args.Cancel = true; hidden = true; desktop.ShowPreview(false); AppWindow.Hide(); } };
            Closed += (_, _) => { layoutTimer.Stop(); desktop.Dispose(); power.Dispose(); };
            Activated += (_, _) => UpdatePreview();
            layoutTimer.Tick += (_, _) => UpdatePreview(); layoutTimer.Start();
            root.Loaded += async (_, _) => { await viewModel.InitializeAsync(); await OfferRecoveryAsync(); };
        }
        catch (Exception ex)
        {
            log.Write("Startup failed", ex);
            root.Children.Add(new TextBlock { Text = "Не удалось запустить приложение\n\n" + ex.Message, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.White) });
            desktop?.Dispose(); desktop = null;
        }
    }
    private static void Bind(FrameworkElement element, DependencyProperty property, string path, bool twoWay = false) => element.SetBinding(property, new Binding { Path = new PropertyPath(path), Mode = twoWay ? BindingMode.TwoWay : BindingMode.OneWay });
    private static TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private static Button Button(string caption, RoutedEventHandler click) { var button = new Button { Content = caption, Padding = new Thickness(16, 10, 16, 10) }; button.Click += click; return button; }
    private void BuildContent()
    {
        var vm = viewModel!;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); root.ColumnDefinitions.Add(new() { Width = new GridLength(340) });
        var heading = new StackPanel { Spacing = 6 };
        heading.Children.Add(Text("HiddenRecorder", 30)); heading.Children.Add(Text("Камера и звук · запись при превышении порога", 14));
        Grid.SetColumnSpan(heading, 2); root.Children.Add(heading);
        var left = new Grid { RowSpacing = 16 };
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto }) left.RowDefinitions.Add(new() { Height = h });
        Grid.SetRow(left, 1); root.Children.Add(left);
        var status = Text("", 20); Bind(status, TextBlock.TextProperty, nameof(vm.Status)); left.Children.Add(status);
        var videoArea = new Grid { ColumnSpacing = 8 };
        videoArea.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        videoArea.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        videoArea.Children.Add(preview);
        var meter = new AudioLevelMeter(); Bind(meter, AudioLevelMeter.LevelProperty, nameof(vm.PeakDb)); Bind(meter, AudioLevelMeter.ThresholdProperty, nameof(vm.ThresholdDb));
        Grid.SetColumn(meter, 1); videoArea.Children.Add(meter);
        Grid.SetRow(videoArea, 1); left.Children.Add(videoArea);
        var meters = new StackPanel { Spacing = 8 };
        var levels = Text(""); Bind(levels, TextBlock.TextProperty, nameof(vm.Levels)); meters.Children.Add(levels);
        var threshold = Text(""); Bind(threshold, TextBlock.TextProperty, nameof(vm.ThresholdDb));
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; label.Children.Add(Text("Оранжевая отметка — порог:")); label.Children.Add(threshold); label.Children.Add(Text("dBFS")); meters.Children.Add(label);
        meters.Children.Add(Text("Буфер предзаписи")); var progress = new ProgressBar { Minimum = 0, Maximum = 100 }; Bind(progress, ProgressBar.ValueProperty, nameof(vm.BufferPercent)); meters.Children.Add(progress);
        var timers = Text(""); Bind(timers, TextBlock.TextProperty, nameof(vm.Timers)); meters.Children.Add(timers);
        Grid.SetRow(meters, 2); left.Children.Add(meters);
        var file = Text(""); Bind(file, TextBlock.TextProperty, nameof(vm.CurrentFile)); Grid.SetRow(file, 3); left.Children.Add(file);
        var error = Text(""); error.Foreground = new SolidColorBrush(Colors.Salmon); Bind(error, TextBlock.TextProperty, nameof(vm.Error)); Grid.SetRow(error, 4); left.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var start = Button("Начать наблюдение", async (_, _) => await vm.StartAsync()); Bind(start, Control.IsEnabledProperty, nameof(vm.CanEditSettings)); actions.Children.Add(start);
        actions.Children.Add(Button("Остановить запись", async (_, _) => await vm.StopAsync()));
        actions.Children.Add(Button("Выход", async (_, _) => await ExitAsync())); Grid.SetRow(actions, 5); left.Children.Add(actions);
        var settings = new StackPanel { Spacing = 14 };
        settings.Children.Add(Text("Настройки", 22));
        settings.Children.Add(Text("MP4 · H.264 + AAC\nЧасти по 60 минут · резерв диска 1 ГиБ"));
        ComboBox Combo(string title, object items, string property, string? member = null)
        {
            var control = new ComboBox { Header = title, ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (member is not null) control.DisplayMemberPath = member;
            Bind(control, ComboBox.SelectedItemProperty, property, true); settings.Children.Add(control); return control;
        }
        Combo("Камера", vm.Cameras, nameof(vm.SelectedCamera), "Name"); Combo("Режим видео", vm.Modes, nameof(vm.SelectedMode)); Combo("Микрофон", vm.Microphones, nameof(vm.SelectedMicrophone), "Name");
        settings.Children.Add(Button("Обновить устройства", async (_, _) => await vm.RefreshCommand.ExecuteAsync(null)));
        void Number(string title, string property, double minimum, double maximum)
        {
            var input = new NumberBox { Header = title, Minimum = minimum, Maximum = maximum, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
            Bind(input, NumberBox.ValueProperty, property, true); settings.Children.Add(input);
        }
        Number("Предзапись, секунды", nameof(vm.PreRollSeconds), 1, 30);
        Number("Остановка после тишины, секунды", nameof(vm.SilenceSeconds), 1, 86400);
        Number("Порог громкости, dBFS", nameof(vm.ThresholdDb), -60, 0);
        Number("Минимальный звук, мс (0 — любой)", nameof(vm.MinimumSoundMs), 0, 1000);
        Number("Битрейт видео, кбит/с", nameof(vm.VideoBitrateKbps), 250, 20000);
        Combo("Битрейт AAC, кбит/с", new double[] { 64, 96, 128, 160, 192, 256, 320 }, nameof(vm.AudioBitrateKbps));
        var channels = new ComboBox { Header = "Звук", ItemsSource = new[] { "Моно (1 канал)", "Стерео (2 канала)" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        Bind(channels, ComboBox.SelectedIndexProperty, nameof(vm.AudioChannelIndex), true); settings.Children.Add(channels);
        var folder = new TextBox { Header = "Папка записи", TextWrapping = TextWrapping.Wrap }; Bind(folder, TextBox.TextProperty, nameof(vm.OutputDirectory), true); settings.Children.Add(folder);
        settings.Children.Add(Button("Выбрать папку…", async (_, _) =>
        {
            try { var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this)); var selected = await picker.PickSingleFolderAsync(); if (selected is not null) vm.OutputDirectory = selected.Path; }
            catch (Exception ex) { vm.Error = ex.Message; }
        }));
        settings.Children.Add(Button("Восстановить незавершённые…", async (_, _) => await OfferRecoveryAsync(true)));
        settings.Children.Add(Text("Закрытие окна сворачивает приложение в трей. Для завершения используйте «Выход»."));
        var settingsContainer = new UserControl { Content = settings };
        Bind(settingsContainer, Control.IsEnabledProperty, nameof(vm.CanEditSettings));
        var scroll = new ScrollViewer { Content = settingsContainer, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
        Grid.SetRow(scroll, 1); Grid.SetColumn(scroll, 1); root.Children.Add(scroll);
    }
    private void UpdatePreview()
    {
        if (desktop is null || media is null || root.XamlRoot is null) return;
        var position = preview.TransformToVisual(root).TransformPoint(new Point(0, 0)); var scale = root.XamlRoot.RasterizationScale;
        int w = Math.Max(1, (int)(preview.ActualWidth * scale)), h = Math.Max(1, (int)(preview.ActualHeight * scale));
        desktop.PositionPreview((int)(position.X * scale), (int)(position.Y * scale), w, h);
        media.Resize(w, h);
        desktop.ShowPreview(!hidden && !dialogOpen && coordinator?.Snapshot.State is RecorderState.PreviewStarting or RecorderState.Previewing or RecorderState.Starting or RecorderState.Armed or RecorderState.Recording or RecorderState.Finalizing);
    }
    private void Show() { hidden = false; AppWindow.Show(); Activate(); UpdatePreview(); }
    private async Task OfferRecoveryAsync(bool explicitRequest = false)
    {
        if (viewModel is null || !viewModel.CanConfigure || dialogOpen) return;
        IReadOnlyList<string> files;
        try { files = recovery.Find(viewModel.OutputDirectory); }
        catch (Exception ex) { viewModel.Error = ex.Message; return; }
        if (files.Count == 0 && !explicitRequest) return;
        dialogOpen = true; UpdatePreview();
        try
        {
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "Незавершённые записи",
                Content = files.Count == 0 ? "Незавершённых записей нет." : $"Найдено файлов: {files.Count}. Скопировать завершённые MP4-фрагменты в новые файлы? Оригиналы сохранятся.",
                PrimaryButtonText = files.Count > 0 ? "Восстановить" : "", CloseButtonText = "Закрыть" };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var count = 0; var errors = new List<string>();
                foreach (var file in files) try { await recovery.RecoverAsync(file); count++; } catch (Exception ex) { errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
                viewModel.Error = $"Восстановлено: {count}. " + string.Join("\n", errors);
            }
        }
        catch (Exception ex) { viewModel.Error = ex.Message; }
        finally { dialogOpen = false; UpdatePreview(); }
    }
    private async Task ExitAsync()
    {
        if (exiting) return; exiting = true;
        viewModel?.PrepareForExit();
        layoutTimer.Stop();
        try { if (coordinator is not null) await coordinator.DisposeAsync(); }
        catch (Exception ex) { log.Write("Shutdown failed", ex); }
        Close(); Application.Current.Exit();
    }
}
