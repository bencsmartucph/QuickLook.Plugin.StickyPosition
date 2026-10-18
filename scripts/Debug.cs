// Placement logging for debug builds only: build-debug.ps1 compiles this next to
// Plugin.cs and wires it into WndProc. Never part of a release. Logs to
// Public\Documents\sp-debug.log.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace QuickLook.Plugin.StickyPosition
{
    internal static class Dbg
    {
        [StructLayout(LayoutKind.Sequential)] public struct R { public int l, t, r, b; }
        [StructLayout(LayoutKind.Sequential)] private struct WP { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }
        private delegate bool EnumProc(IntPtr h, IntPtr d);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr d);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out R r);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out R r);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        private static readonly string LogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "sp-debug.log");

        public static void Log(string s)
        {
            try { File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n"); }
            catch { }
        }

        public static string Pos(IntPtr lParam)
        {
            var p = (WP)Marshal.PtrToStructure(lParam, typeof(WP));
            return string.Format("{0},{1} {2}x{3} flags={4:X}", p.x, p.y, p.cx, p.cy, p.flags);
        }

        // Coordinates are as QuickLook sees them: DPI-virtualised when QuickLook
        // is not per-monitor aware. scripts\preview-steps.ps1 'tree' gives physical ones.
        public static string State(IntPtr hwnd)
        {
            R r;
            GetWindowRect(hwnd, out r);
            double scale = -1;
            try
            {
                var source = HwndSource.FromHwnd(hwnd);
                if (source != null) scale = source.CompositionTarget.TransformToDevice.M11;
            }
            catch { }
            return string.Format("vis={0} dpi={1} wpfscale={2} rect={3},{4} {5}x{6}",
                IsWindowVisible(hwnd) ? 1 : 0, GetDpiForWindow(hwnd), scale, r.l, r.t, r.r - r.l, r.b - r.t);
        }

        public static void Dump(IntPtr hwnd, string tag)
        {
            var sb = new StringBuilder("DUMP " + tag + " " + State(hwnd));
            R c;
            GetClientRect(hwnd, out c);
            sb.AppendFormat(" client={0}x{1}", c.r, c.b);
            Window w = null;
            try
            {
                var source = HwndSource.FromHwnd(hwnd);
                if (source != null) w = source.RootVisual as Window;
            }
            catch { }
            if (w != null) sb.AppendFormat(" wpf={0:0.#}x{1:0.#}", w.ActualWidth, w.ActualHeight);
            EnumChildWindows(hwnd, delegate(IntPtr h, IntPtr d)
            {
                if (!IsWindowVisible(h)) return true;
                var cls = new StringBuilder(128);
                GetClassName(h, cls, 128);
                R r;
                GetWindowRect(h, out r);
                sb.AppendFormat("\r\n    child [{0}] dpi={1} {2},{3} {4}x{5}", cls, GetDpiForWindow(h), r.l, r.t, r.r - r.l, r.b - r.t);
                return true;
            }, IntPtr.Zero);
            Log(sb.ToString());
        }

        public static void Later(IntPtr hwnd, int ms, string tag)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { t.Stop(); Dump(hwnd, tag); };
            t.Start();
        }

        // Called from WndProc after the plugin's own handling.
        public static void Message(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, string before)
        {
            switch (msg)
            {
                case 0x0046:                            // WM_WINDOWPOSCHANGING
                    Log("POSCHANGING " + State(hwnd) + " asked " + before + " -> " + Pos(lParam));
                    break;
                case 0x02E0:                            // WM_DPICHANGED
                    var s = (R)Marshal.PtrToStructure(lParam, typeof(R));
                    Log("DPICHANGED dpi=" + ((long)wParam & 0xFFFF) + " suggested " + s.l + "," + s.t + " " + (s.r - s.l) + "x" + (s.b - s.t) + " " + State(hwnd));
                    break;
                case 0x0018:                            // WM_SHOWWINDOW
                    Log("SHOWWINDOW " + wParam + " " + State(hwnd));
                    if (wParam != IntPtr.Zero)
                    {
                        Later(hwnd, 400, "+0.4s");
                        Later(hwnd, 2500, "+2.5s");
                    }
                    break;
                case 0x0005:                            // WM_SIZE
                    Log("SIZE " + ((long)lParam & 0xFFFF) + "x" + (((long)lParam >> 16) & 0xFFFF) + " " + State(hwnd));
                    break;
            }
        }
    }
}
