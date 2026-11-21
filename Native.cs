using System.Runtime.InteropServices;

namespace Bro;

/// <summary>Small Win32 helpers the borderless window and the page clipping need.</summary>
static class Native
{
    public const int VK_LBUTTON = 0x01, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    public const int WM_GETMINMAXINFO = 0x24;
    const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int SetWindowRgn(nint hWnd, nint hRgn, bool redraw);
    [DllImport("gdi32.dll")] static extern nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Starts a native window move, as if the user grabbed a title bar.</summary>
    public static void BeginWindowDrag(nint hwnd)
    {
        if (!IsKeyDown(VK_LBUTTON)) return; // button already released: don't start a stuck move loop
        GetCursorPos(out var p);
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, (nint)((p.Y << 16) | (p.X & 0xFFFF)));
    }

    public static void BringToTop(nint hwnd)
    {
        if (hwnd != 0) SetWindowPos(hwnd, 0 /* HWND_TOP */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    public static void SendToBottom(nint hwnd)
    {
        if (hwnd != 0) SetWindowPos(hwnd, 1 /* HWND_BOTTOM */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    /// <summary>Rounds a child window's top corners (sizes in physical pixels). The bottom stays square.</summary>
    public static void ClipRounded(nint hwnd, int width, int height, int diameter)
    {
        if (hwnd == 0 || width <= 0 || height <= 0) return;
        SetWindowRgn(hwnd, diameter <= 0 ? 0 : CreateRoundRectRgn(0, 0, width + 1, height + 1 + diameter, diameter, diameter), true);
    }

    /// <summary>
    /// Answers WM_GETMINMAXINFO so a maximized borderless window fills the monitor's work area
    /// (the screen minus the taskbar) instead of covering the taskbar and hanging off the edges.
    /// In full screen it covers the whole monitor instead.
    /// </summary>
    public static void FitMaximized(nint hwnd, nint lParam, bool coverTaskbar)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return;

        var area = coverTaskbar ? info.Monitor : info.Work;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.MaxPosition = new POINT { X = area.Left - info.Monitor.Left, Y = area.Top - info.Monitor.Top };
        mmi.MaxSize = new POINT { X = area.Right - area.Left, Y = area.Bottom - area.Top };
        Marshal.StructureToPtr(mmi, lParam, false);
    }

    /// <summary>Dark frame, Windows 11 rounded corners and a subtle border colour.</summary>
    public static void StyleWindow(nint hwnd)
    {
        int on = 1, round = 2, border = 0x002E2A2A; // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));      // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));   // DWMWA_WINDOW_CORNER_PREFERENCE
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));  // DWMWA_BORDER_COLOR
    }
}
