using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HiddenRecorder.App;

/// <summary>Owns the tray HICON loaded from the same asset used by the EXE and window.</summary>
internal sealed class ApplicationIcon : SafeHandleZeroOrMinusOneIsInvalid
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Assets", "HiddenRecorder.ico");
    private ApplicationIcon() : base(true) { }
    public static ApplicationIcon LoadTrayIcon()
    {
        var icon = LoadImageW(0, FilePath, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
        if (icon.IsInvalid) { var error = Marshal.GetLastWin32Error(); icon.Dispose(); throw new Win32Exception(error, "Could not load the application icon."); }
        return icon;
    }
    protected override bool ReleaseHandle() => DestroyIcon(handle);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ApplicationIcon LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
}
