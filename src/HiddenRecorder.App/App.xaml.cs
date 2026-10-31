using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using HiddenRecorder.Infrastructure;

namespace HiddenRecorder.App;
public partial class App : Application
{
    private Mutex? instance;
    private MainWindow? window;
    private readonly RollingLog log = new();
    public App()
    {
        UnhandledException += (_, args) => log.Write("Unhandled WinUI exception: " + args.Message, args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => log.Write("Unhandled runtime exception", args.ExceptionObject as Exception);
        log.Write("Application startup: " + AppContext.BaseDirectory);
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        log.Write("Creating main window");
        instance = new Mutex(true, "Local\\HiddenRecorder.Desktop", out var first);
        if (!first) { MessageBoxW(0, "HiddenRecorder уже запущен. Откройте его через значок в трее.", "HiddenRecorder", 0x40); Exit(); return; }
        window = new MainWindow(); log.Write("Activating main window"); window.Activate(); log.Write("Main window activated");
    }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint window, string text, string caption, uint type);
}
