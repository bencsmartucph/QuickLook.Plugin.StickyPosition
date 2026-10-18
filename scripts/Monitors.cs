// Shared by the test scripts: monitors in physical pixels, primary first, and
// the visible QuickLook viewer window. The calling process is made per-monitor
// aware so nothing is DPI-virtualised.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class QL
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] struct MI { public int cb; public RECT mon; public RECT work; public uint flags; }
    delegate bool MonProc(IntPtr h, IntPtr dc, ref RECT r, IntPtr d);
    delegate bool WinProc(IntPtr h, IntPtr d);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonProc cb, IntPtr d);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MI mi);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr h, int t, out uint x, out uint y);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] static extern bool EnumWindows(WinProc cb, IntPtr d);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, WinProc cb, IntPtr d);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);

    public class Monitor { public RECT Work; public uint Dpi; public bool Primary; }

    public static void Aware() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }

    public static List<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr dc, ref RECT r, IntPtr d)
        {
            var mi = new MI(); mi.cb = Marshal.SizeOf(typeof(MI)); GetMonitorInfo(h, ref mi);
            uint x, y; GetDpiForMonitor(h, 0, out x, out y);
            var m = new Monitor { Work = mi.work, Dpi = x, Primary = (mi.flags & 1) != 0 };
            if (m.Primary) list.Insert(0, m); else list.Add(m);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // The largest visible top-level window of QuickLook: the preview.
    public static IntPtr Viewer(uint pid)
    {
        IntPtr best = IntPtr.Zero; int area = 0;
        EnumWindows(delegate(IntPtr h, IntPtr d)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h))
            {
                RECT r; GetWindowRect(h, out r);
                int a = (r.r - r.l) * (r.b - r.t);
                if (a > area) { area = a; best = h; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // The window and its visible children (the WebView2 HWNDs) with rect and DPI.
    public static string Tree(IntPtr hwnd)
    {
        var sb = new StringBuilder(Describe(hwnd));
        EnumChildWindows(hwnd, delegate(IntPtr c, IntPtr d)
        {
            if (IsWindowVisible(c)) sb.Append("\n    child " + Describe(c));
            return true;
        }, IntPtr.Zero);
        return sb.ToString();
    }

    static string Describe(IntPtr h)
    {
        var c = new StringBuilder(128); GetClassName(h, c, 128);
        RECT r; GetWindowRect(h, out r);
        return string.Format("[{0}] dpi={1} {2},{3} {4}x{5}", c, GetDpiForWindow(h), r.l, r.t, r.r - r.l, r.b - r.t);
    }
}
