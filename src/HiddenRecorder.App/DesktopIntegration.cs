using System.Runtime.InteropServices;
using HiddenRecorder.Core;
using Microsoft.UI.Dispatching;

namespace HiddenRecorder.App;
public sealed class DesktopIntegration : IUserNotifier, IDisposable
{
    private readonly nint window;
    private readonly DispatcherQueue dispatcher;
    private readonly WindowProc procedure;
    private readonly nint previous;
    private readonly ApplicationIcon trayIcon;
    private NotifyData data;
    private RecorderState? lastState;
    private bool previewVisible;
    private (int X, int Y, int Width, int Height)? previewBounds;
    public event Action? ShowRequested, StartRequested, StopRequested, ExitRequested, SuspendRequested;
    public nint PreviewWindow { get; }
    public DesktopIntegration(nint window, DispatcherQueue dispatcher)
    {
        this.window = window; this.dispatcher = dispatcher;
        trayIcon = ApplicationIcon.LoadTrayIcon();
        procedure = WndProc;
        previous = SetWindowLongPtrW(window, -4, Marshal.GetFunctionPointerForDelegate(procedure));
        PreviewWindow = CreateWindowExW(0, "STATIC", "", 0x50000000 | 0x04000000, 0, 0, 1, 1, window, 0, 0, 0);
        data = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = window, Id = 1, Flags = 1 | 2 | 4, Callback = 0x8001,
            Icon = trayIcon.DangerousGetHandle(), Tip = "HiddenRecorder · monitoring off", Info = "", InfoTitle = "" };
        Shell_NotifyIconW(0, ref data);
    }
    public void SetState(RecorderState state)
    {
        if (lastState == state) return; lastState = state;
        data.Flags = 4; data.Tip = state switch
        {
            RecorderState.Recording => "HiddenRecorder · recording",
            RecorderState.Armed => "HiddenRecorder · monitoring",
            RecorderState.Faulted => "HiddenRecorder · error",
            RecorderState.Previewing => "HiddenRecorder · preview, recording off",
            RecorderState.PreviewStarting => "HiddenRecorder · connecting devices",
            _ => "HiddenRecorder · " + state
        };
        Shell_NotifyIconW(1, ref data);
    }
    public void Notify(string title, string message) => dispatcher.TryEnqueue(() =>
    {
        data.Flags = 16; data.InfoTitle = title[..Math.Min(title.Length, 63)]; data.Info = message[..Math.Min(message.Length, 255)]; data.InfoFlags = 2;
        Shell_NotifyIconW(1, ref data);
    });
    private nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x8001)
        {
            if ((uint)lParam is 0x203 or 0x202) ShowRequested?.Invoke();
            if ((uint)lParam == 0x205)
            {
                var menu = CreatePopupMenu();
                AppendMenuW(menu, 0, 1, "Open"); AppendMenuW(menu, 0, 2, "Start monitoring"); AppendMenuW(menu, 0, 3, "Stop monitoring"); AppendMenuW(menu, 0, 4, "Exit");
                GetCursorPos(out var point); SetForegroundWindow(window);
                var command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, window, 0); DestroyMenu(menu);
                switch (command) { case 1: ShowRequested?.Invoke(); break; case 2: StartRequested?.Invoke(); break; case 3: StopRequested?.Invoke(); break; case 4: ExitRequested?.Invoke(); break; }
            }
            return 0;
        }
        if (message == 0x218 && wParam == 4 || message == 0x11) SuspendRequested?.Invoke();
        return CallWindowProcW(previous, hwnd, message, wParam, lParam);
    }
    public void PositionPreview(int x, int y, int width, int height)
    {
        if (previewBounds == (x, y, width, height)) return;
        previewBounds = (x, y, width, height);
        // XAML creates its child HWND after our preview host. Keep the video above that sibling.
        SetWindowPos(PreviewWindow, 0, x, y, width, height, 0x0010);
    }
    public void ShowPreview(bool visible)
    {
        if (previewVisible == visible) return;
        previewVisible = visible;
        if (visible) SetWindowPos(PreviewWindow, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
        else ShowWindow(PreviewWindow, 0);
    }
    public void Dispose() { Shell_NotifyIconW(2, ref data); trayIcon.Dispose(); SetWindowLongPtrW(window, -4, previous); DestroyWindow(PreviewWindow); }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyData data);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint CallWindowProcW(nint previous, nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32")] private static extern nint CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string text);
    [DllImport("user32")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32")] private static extern bool SetForegroundWindow(nint hwnd);
}
