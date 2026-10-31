using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using HiddenRecorder.Infrastructure;

namespace HiddenRecorder.App;
public partial class App : Application
{
    private SingleInstance? instance;
    private MainWindow? window;
    private readonly RollingLog log = new();
    public App()
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        UnhandledException += (_, args) => log.Write("Unhandled WinUI exception: " + args.Message, args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => log.Write("Unhandled runtime exception", args.ExceptionObject as Exception);
        log.Write("Application startup: " + AppContext.BaseDirectory);
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        log.Write("Creating main window");
        instance = new SingleInstance();
        if (!instance.IsPrimary)
        {
            if (!instance.ShowExisting()) MessageBoxW(0, "Could not contact the running HiddenRecorder window. Please try again.", "HiddenRecorder", 0x40);
            instance.Dispose(); instance = null; Exit(); return;
        }
        window = new MainWindow(); log.Write("Activating main window"); window.Activate(); log.Write("Main window activated");
        instance.Listen(() => window.DispatcherQueue.TryEnqueue(() => { log.Write("Existing instance activation received"); window.Restore(); }));
        window.Closed += (_, _) => { instance?.Dispose(); instance = null; };
    }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint window, string text, string caption, uint type);
}
