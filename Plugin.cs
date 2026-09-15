// QuickLook.Plugin.StickyPosition
// Copyright (C) 2026 Ben Smart. Licensed under the GNU GPL v3 or later (see LICENSE).
//
// Remembers where you put the QuickLook preview window, per file type, and
// opens it there every time -- with no jump.
//
// QuickLook sizes and places its viewer window (PositionWindow) *before* it
// calls Show(), so rewriting WM_WINDOWPOSCHANGING on the viewer's HWND makes
// that placement land where we want: the window is never drawn anywhere else.
// Only QuickLook's own placement of a new file is rewritten -- drags, snaps,
// maximise and fullscreen of a visible preview pass through untouched.
//
// QuickLook replaces its viewer window every time one closes, so the hook
// re-attaches on Closed. Written in C# 5 so it builds with the csc that ships
// with the .NET Framework -- see build.ps1.

using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Common.Plugin.MoreMenu;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace QuickLook.Plugin.StickyPosition
{
    public class Plugin : IViewer, IMoreMenuExtended
    {
        private const string Domain = "QuickLook.Plugin.StickyPosition";

        // Used for file types you haven't placed yet: a tall column on the right.
        // Fractions of the monitor's work area: left, top, width, height.
        // Override with a <DefaultPosition> element in the config file.
        private const string BuiltInDefault = "0.545,0.03,0.42,0.92";

        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private static Window _hooked;
        private static FieldInfo _pathField;
        private static readonly HashSet<IntPtr> Moving = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, string> PlacedPath = new Dictionary<IntPtr, string>();
        private static readonly Dictionary<IntPtr, Window> Windows = new Dictionary<IntPtr, Window>();

        public int Priority { get { return int.MinValue; } }

        public void Init()
        {
            // Init runs before QuickLook creates its ViewWindowManager; attach once
            // the dispatcher is free and the first viewer window exists.
            var app = Application.Current;
            if (app != null)
                app.Dispatcher.BeginInvoke(new Action(Attach), DispatcherPriority.ApplicationIdle);
        }

        public bool CanHandle(string path) { return false; }
        public void Prepare(string path, ContextObject context) { }
        public void View(string path, ContextObject context) { }
        public void Cleanup() { }

        // QuickLook asks every plugin for "..." menu items on each preview. Also a
        // fallback attach, for the window QuickLook creates when one is pinned.
        public IEnumerable<IMenuItem> MenuItems
        {
            get
            {
                Attach();
                return new IMenuItem[]
                {
                    new MoreMenuItem
                    {
                        Header = "Forget position for this file type",
                        Icon = "",
                        Command = new ForgetCommand(),
                    },
                };
            }
        }

        #region Hook

        private static void Attach()
        {
            try
            {
                var window = FindViewerWindow();
                if (window == null || ReferenceEquals(window, _hooked))
                    return;

                if (_pathField == null)
                    _pathField = window.GetType().GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic);

                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                var source = HwndSource.FromHwnd(hwnd);
                if (source == null)
                    return;
                Windows[hwnd] = window;
                source.AddHook(WndProc);
                _hooked = window;

                // QuickLook swaps in a new viewer window from its own Closed
                // handler, subscribed before ours; re-attach after it has run.
                window.Closed += delegate
                {
                    Moving.Remove(hwnd);
                    PlacedPath.Remove(hwnd);
                    Windows.Remove(hwnd);
                    window.Dispatcher.BeginInvoke(new Action(Attach), DispatcherPriority.ApplicationIdle);
                };
            }
            catch { }
        }

        // QuickLook.ViewWindowManager.GetInstance()._viewerWindow
        private static Window FindViewerWindow()
        {
            Type type = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType("QuickLook.ViewWindowManager");
                if (type != null) break;
            }
            if (type == null) return null;

            var getInstance = type.GetMethod("GetInstance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var field = type.GetField("_viewerWindow", BindingFlags.Instance | BindingFlags.NonPublic);
            if (getInstance == null || field == null) return null;

            var manager = getInstance.Invoke(null, null);
            return manager == null ? null : field.GetValue(manager) as Window;
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                switch (msg)
                {
                    case WM_ENTERSIZEMOVE:
                        Moving.Add(hwnd);
                        break;
                    case WM_EXITSIZEMOVE:
                        Moving.Remove(hwnd);
                        Save(hwnd);
                        break;
                    case WM_WINDOWPOSCHANGING:
                        if (!Moving.Contains(hwnd))
                            Place(hwnd, lParam);
                        break;
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        // Rewrite QuickLook's placement: while the window is hidden, or when a
        // visible window has just been handed a new file. Anything else (a snap,
        // maximise, fullscreen, a restore) is the user's and passes through.
        private static void Place(IntPtr hwnd, IntPtr lParam)
        {
            var pos = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
            if ((pos.flags & SWP_NOMOVE) != 0 && (pos.flags & SWP_NOSIZE) != 0)
                return;                                 // z-order / topmost only
            if (IsZoomed(hwnd) || IsIconic(hwnd))
                return;

            var path = CurrentPath(hwnd);
            if (string.IsNullOrEmpty(path))
                return;
            string placed;
            if (IsWindowVisible(hwnd) && PlacedPath.TryGetValue(hwnd, out placed) && placed == path)
                return;

            RECT work;
            if (!WorkArea(hwnd, out work))
                return;
            var f = Parse(SettingHelper.Get(Key(path), string.Empty, Domain))
                    ?? Parse(SettingHelper.Get("DefaultPosition", string.Empty, Domain))
                    ?? Parse(BuiltInDefault);

            int ww = work.right - work.left, wh = work.bottom - work.top;
            pos.x = work.left + (int)Math.Round(f[0] * ww);
            pos.y = work.top + (int)Math.Round(f[1] * wh);
            pos.cx = (int)Math.Round(f[2] * ww);
            pos.cy = (int)Math.Round(f[3] * wh);
            pos.flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
            Marshal.StructureToPtr(pos, lParam, false);

            PlacedPath[hwnd] = path;
        }

        // End of a drag or resize: remember it for this file type.
        private static void Save(IntPtr hwnd)
        {
            if (IsZoomed(hwnd) || IsIconic(hwnd))
                return;
            var path = CurrentPath(hwnd);
            RECT work, r;
            if (string.IsNullOrEmpty(path) || !WorkArea(hwnd, out work) || !GetWindowRect(hwnd, out r))
                return;

            double ww = work.right - work.left, wh = work.bottom - work.top;
            var value = string.Format(CultureInfo.InvariantCulture, "{0:0.####},{1:0.####},{2:0.####},{3:0.####}",
                (r.left - work.left) / ww, (r.top - work.top) / wh, (r.right - r.left) / ww, (r.bottom - r.top) / wh);
            SettingHelper.Set(Key(path), value, Domain);
        }

        #endregion

        #region Settings

        // One setting per file type: Pos_md, Pos_pdf, Pos_folder, Pos_noext.
        private static string Key(string path)
        {
            if (Directory.Exists(path))
                return "Pos_folder";
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (ext.Length == 0)
                return "Pos_noext";
            var sb = new StringBuilder("Pos_");
            foreach (var c in ext)
                sb.Append((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_');
            return sb.ToString();
        }

        private static double[] Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return null;
            var parts = s.Split(',');
            if (parts.Length != 4)
                return null;
            var f = new double[4];
            for (var i = 0; i < 4; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i]))
                    return null;
            if (f[2] < 0.05 || f[3] < 0.05 || f[2] > 1.2 || f[3] > 1.2)
                return null;                            // nonsense; ignore it
            return f;
        }

        private static string CurrentPath(IntPtr hwnd)
        {
            Window window;
            if (!Windows.TryGetValue(hwnd, out window) || _pathField == null)
                return null;
            return _pathField.GetValue(window) as string;
        }

        private static bool WorkArea(IntPtr hwnd, out RECT work)
        {
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            var ok = GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* NEAREST */), ref mi);
            work = mi.rcWork;
            return ok;
        }

        private class ForgetCommand : ICommand
        {
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) { return true; }

            public void Execute(object parameter)
            {
                var window = _hooked;
                if (window == null)
                    return;
                var hwnd = new WindowInteropHelper(window).Handle;
                var path = CurrentPath(hwnd);
                if (string.IsNullOrEmpty(path))
                    return;
                SettingHelper.Set(Key(path), string.Empty, Domain);

                // Put it back at the default now, through the hook.
                PlacedPath.Remove(hwnd);
                RECT r;
                if (GetWindowRect(hwnd, out r))
                    SetWindowPos(hwnd, IntPtr.Zero, r.left, r.top, r.right - r.left, r.bottom - r.top,
                        SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        #endregion

        #region Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);

        #endregion
    }
}
