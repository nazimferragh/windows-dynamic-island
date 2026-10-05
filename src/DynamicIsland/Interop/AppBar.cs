using System;
using System.Runtime.InteropServices;

namespace DynamicIsland.Interop;

/// <summary>
/// A Windows "app bar": reserves a strip along the top of a monitor, the same mechanism the taskbar
/// uses, so maximized windows are laid out below it instead of under the island (like the Mac menu bar).
/// </summary>
internal sealed class AppBar
{
    private const uint ABM_NEW = 0x0, ABM_REMOVE = 0x1, ABM_QUERYPOS = 0x2, ABM_SETPOS = 0x3;
    private const uint ABE_TOP = 1;
    public const int ABN_POSCHANGED = 0x1;
    public const int ABN_FULLSCREENAPP = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public WindowApi.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    private readonly IntPtr _hwnd;

    public AppBar(IntPtr hwnd)
    {
        _hwnd = hwnd;
        CallbackMessage = RegisterWindowMessage("DynamicIsland.AppBarCallback");
    }

    /// <summary>Broadcast by Explorer when it (re)starts; every app bar registration was lost with it.</summary>
    public static uint TaskbarCreatedMessage { get; } = RegisterWindowMessage("TaskbarCreated");

    /// <summary>After an Explorer restart our registration no longer exists; forget it so Register() redoes it.</summary>
    public void ForgetRegistration() => IsRegistered = false;

    /// <summary>Windows sends this message to the app bar window with an ABN_* code in wParam.</summary>
    public uint CallbackMessage { get; }

    public bool IsRegistered { get; private set; }

    public void Register()
    {
        if (IsRegistered) return;
        var data = NewData();
        data.uCallbackMessage = CallbackMessage;
        IsRegistered = SHAppBarMessage(ABM_NEW, ref data) != UIntPtr.Zero;
    }

    public void Unregister()
    {
        if (!IsRegistered) return;
        var data = NewData();
        SHAppBarMessage(ABM_REMOVE, ref data);
        IsRegistered = false;
    }

    /// <summary>Reserves <paramref name="height"/> physical pixels at the top of the monitor and returns the granted rect.</summary>
    public WindowApi.RECT ReserveTop(WindowApi.RECT monitor, int height)
    {
        var data = NewData();
        data.uEdge = ABE_TOP;
        data.rc = new WindowApi.RECT { Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Top + height };
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        data.rc.Bottom = data.rc.Top + height;
        SHAppBarMessage(ABM_SETPOS, ref data);
        return data.rc;
    }

    private APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
}
