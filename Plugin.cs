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
// maximise and fullscreen of a visible preview pass through untouched. A file
// type with no saved position is left to QuickLook.
//
// QuickLook replaces its viewer window every time one closes or is pinned, so
// the hook re-attaches on Closed and when Pinned changes. Written in C# 5 so it
// builds with the csc that ships with the .NET Framework -- see build.ps1.

using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Common.Plugin.MoreMenu;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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

        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        // Pixels a new preview is shifted by when it would open exactly on top
        // of another preview window (a pinned one, usually).
        private const int OverlapOffset = 32;

        private static FieldInfo _pathField;
        private static PropertyInfo _pinnedProperty;
        private static PropertyInfo _contextProperty;
        private static bool _loggedMissing;
        private static readonly Dictionary<IntPtr, RECT> Moving = new Dictionary<IntPtr, RECT>();
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

        // QuickLook asks every plugin for "..." menu items on each preview, after
        // the file's path is set. Also a fallback attach.
        public IEnumerable<IMenuItem> MenuItems
        {
            get
            {
                Attach();

                Window window = null;
                var type = "this file type";
                var locked = false;
                try
                {
                    window = FindViewerWindow();
                    var path = window == null ? null : CurrentPath(new WindowInteropHelper(window).Handle);
                    if (!string.IsNullOrEmpty(path))
                    {
                        type = TypeLabel(path);
                        locked = IsLocked(path);
                    }
                }
                catch { }

                var lockItem = new MoreMenuItem { Icon = "" };
                lockItem.Header = (locked ? "Unlock position for " : "Lock position for ") + type;
                lockItem.Command = new LockCommand(window, lockItem);

                return new IMenuItem[]
                {
                    lockItem,
                    new MoreMenuItem
                    {
                        Header = "Forget position for " + type,
                        Icon = "",
                        Command = new ForgetCommand(window),
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
                if (window == null || Windows.ContainsValue(window))
                    return;

                var type = window.GetType();
                if (_pathField == null)
                    _pathField = type.GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_pinnedProperty == null)
                    _pinnedProperty = type.GetProperty("Pinned", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_contextProperty == null)
                    _contextProperty = type.GetProperty("ContextObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_pathField == null)
                {
                    LogMissing("ViewerWindow._path");
                    return;
                }

                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                var source = HwndSource.FromHwnd(hwnd);
                if (source == null)
                    return;
                Windows[hwnd] = window;
                source.AddHook(WndProc);

                // Hooked late (the MenuItems fallback): the file on screen has
                // already been placed by QuickLook, so leave it where it is.
                if (IsWindowVisible(hwnd))
                {
                    var shown = CurrentPath(hwnd);
                    if (!string.IsNullOrEmpty(shown))
                        PlacedPath[hwnd] = shown;
                }

                // Pinning hands this window to the user and makes QuickLook create
                // a new one straight away, with no Closed event. Hook the new one
                // before its first preview.
                var notify = window as INotifyPropertyChanged;
                if (notify != null)
                    notify.PropertyChanged += delegate(object sender, PropertyChangedEventArgs e)
                    {
                        if (e.PropertyName == "Pinned")
                            window.Dispatcher.BeginInvoke(new Action(Attach), DispatcherPriority.ApplicationIdle);
                    };

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
            if (type == null)
            {
                LogMissing("QuickLook.ViewWindowManager");
                return null;
            }

            var getInstance = type.GetMethod("GetInstance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var field = type.GetField("_viewerWindow", BindingFlags.Instance | BindingFlags.NonPublic);
            if (getInstance == null || field == null)
            {
                LogMissing("ViewWindowManager.GetInstance / _viewerWindow");
                return null;
            }

            var manager = getInstance.Invoke(null, null);
            return manager == null ? null : field.GetValue(manager) as Window;
        }

        // A QuickLook release that renames what we reach by reflection switches
        // the plugin off; say so once in QuickLook's log.
        private static void LogMissing(string what)
        {
            if (_loggedMissing)
                return;
            _loggedMissing = true;
            try { ProcessHelper.WriteLog("StickyPosition: " + what + " not found in this QuickLook version; the plugin is inactive."); }
            catch { }
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                switch (msg)
                {
                    case WM_ENTERSIZEMOVE:
                        RECT start;
                        GetWindowRect(hwnd, out start);
                        Moving[hwnd] = start;
                        break;
                    case WM_EXITSIZEMOVE:
                        RECT before, after;
                        var moved = !Moving.TryGetValue(hwnd, out before) || !GetWindowRect(hwnd, out after) || !Same(before, after);
                        Moving.Remove(hwnd);
                        if (moved)                      // a click on the title bar is not a placement
                            Save(hwnd, false);
                        break;
                    case WM_WINDOWPOSCHANGING:
                        if (!Moving.ContainsKey(hwnd))
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
            PlacedPath[hwnd] = path;

            var f = Parse(SettingHelper.Get("Pos_" + TypeId(path), string.Empty, Domain))
                    ?? Parse(SettingHelper.Get("DefaultPosition", string.Empty, Domain));
            if (f == null)
                return;                                 // never placed: QuickLook's own position

            // What QuickLook asked for. A hidden window has not been anywhere yet,
            // so the monitor comes from this rectangle, not from the window.
            RECT current;
            GetWindowRect(hwnd, out current);
            int x = (pos.flags & SWP_NOMOVE) != 0 ? current.left : pos.x;
            int y = (pos.flags & SWP_NOMOVE) != 0 ? current.top : pos.y;
            int cx = (pos.flags & SWP_NOSIZE) != 0 ? current.right - current.left : pos.cx;
            int cy = (pos.flags & SWP_NOSIZE) != 0 ? current.bottom - current.top : pos.cy;

            RECT work;
            var asked = new RECT { left = x, top = y, right = x + cx, bottom = y + cy };
            if (!WorkArea(MonitorFromRect(ref asked, MONITOR_DEFAULTTONEAREST), out work))
                return;

            int ww = work.right - work.left, wh = work.bottom - work.top;
            x = work.left + (int)Math.Round(f[0] * ww);
            y = work.top + (int)Math.Round(f[1] * wh);
            if (CanResize(hwnd))                        // fixed-size previews keep QuickLook's size
            {
                cx = (int)Math.Round(f[2] * ww);
                cy = (int)Math.Round(f[3] * wh);
            }

            // Not exactly on top of another preview window.
            for (var i = 0; i < 8 && Covers(hwnd, x, y); i++)
            {
                x += OverlapOffset;
                y += OverlapOffset;
            }

            // Keep the title bar reachable, whatever the config says.
            x = Math.Max(work.left - cx + 120, Math.Min(x, work.right - 120));
            y = Math.Max(work.top - 10, Math.Min(y, work.bottom - 60));

            pos.x = x;
            pos.y = y;
            pos.cx = cx;
            pos.cy = cy;
            pos.flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
            Marshal.StructureToPtr(pos, lParam, false);
        }

        private static bool Covers(IntPtr self, int x, int y)
        {
            foreach (var other in Windows.Keys)
            {
                RECT r;
                if (other == self || !IsWindowVisible(other) || !GetWindowRect(other, out r))
                    continue;
                if (Math.Abs(r.left - x) < 8 && Math.Abs(r.top - y) < 8)
                    return true;
            }
            return false;
        }

        // End of a drag or resize: remember it for this file type, unless the type
        // is locked or the window is pinned (moved out of the way, not placed).
        // force: the user asked for this position by locking it.
        private static void Save(IntPtr hwnd, bool force)
        {
            if (IsZoomed(hwnd) || IsIconic(hwnd))
                return;
            var path = CurrentPath(hwnd);
            RECT work, r;
            if (string.IsNullOrEmpty(path) || !GetWindowRect(hwnd, out r))
                return;
            if (!force && (IsLocked(path) || IsPinned(hwnd)))
                return;
            if (!WorkArea(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), out work))
                return;

            double ww = work.right - work.left, wh = work.bottom - work.top;
            var value = string.Format(CultureInfo.InvariantCulture, "{0:0.####},{1:0.####},{2:0.####},{3:0.####}",
                (r.left - work.left) / ww, (r.top - work.top) / wh, (r.right - r.left) / ww, (r.bottom - r.top) / wh);
            SettingHelper.Set("Pos_" + TypeId(path), value, Domain);
        }

        #endregion

        #region Settings

        // One id per file type: md, pdf, folder, noext. Settings are Pos_<id>
        // (left,top,width,height as fractions of the work area) and Lock_<id>.
        private static string TypeId(string path)
        {
            if (Directory.Exists(path))
                return "folder";
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (ext.Length == 0)
                return "noext";
            var sb = new StringBuilder();
            foreach (var c in ext)
                sb.Append((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_');
            return sb.ToString();
        }

        private static string TypeLabel(string path)
        {
            var id = TypeId(path);
            if (id == "folder")
                return "folders";
            if (id == "noext")
                return "files with no extension";
            return Path.GetExtension(path).ToLowerInvariant();
        }

        private static bool IsLocked(string path)
        {
            return SettingHelper.Get("Lock_" + TypeId(path), string.Empty, Domain) == "1";
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

        private static bool IsPinned(IntPtr hwnd)
        {
            Window window;
            if (!Windows.TryGetValue(hwnd, out window) || _pinnedProperty == null)
                return false;
            var pinned = _pinnedProperty.GetValue(window, null);
            return pinned is bool && (bool)pinned;
        }

        // ContextObject.CanResize is false for previews with a fixed size: the
        // info panel (folders, unknown files), the plugin installer, audio.
        private static bool CanResize(IntPtr hwnd)
        {
            Window window;
            if (!Windows.TryGetValue(hwnd, out window) || _contextProperty == null)
                return true;
            var context = _contextProperty.GetValue(window, null) as ContextObject;
            return context == null || context.CanResize;
        }

        private static bool WorkArea(IntPtr monitor, out RECT work)
        {
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            var ok = GetMonitorInfo(monitor, ref mi);
            work = mi.rcWork;
            return ok;
        }

        private static bool Same(RECT a, RECT b)
        {
            return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;
        }

        // The window whose menu was clicked: the active one, else the one that was
        // current when the menu was built.
        private static IntPtr MenuWindow(Window built)
        {
            foreach (var pair in Windows)
                if (pair.Value.IsActive)
                    return pair.Key;
            return built == null ? IntPtr.Zero : new WindowInteropHelper(built).Handle;
        }

        private class LockCommand : ICommand
        {
            private readonly Window _built;
            private readonly MoreMenuItem _item;

            public LockCommand(Window built, MoreMenuItem item)
            {
                _built = built;
                _item = item;
            }

            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) { return true; }

            public void Execute(object parameter)
            {
                try
                {
                    var hwnd = MenuWindow(_built);
                    var path = CurrentPath(hwnd);
                    if (string.IsNullOrEmpty(path))
                        return;

                    var locking = !IsLocked(path);
                    if (locking)
                        Save(hwnd, true);               // lock what is on screen now
                    SettingHelper.Set("Lock_" + TypeId(path), locking ? "1" : string.Empty, Domain);
                    _item.Header = (locking ? "Unlock position for " : "Lock position for ") + TypeLabel(path);
                }
                catch { }
            }
        }

        private class ForgetCommand : ICommand
        {
            private readonly Window _built;

            public ForgetCommand(Window built) { _built = built; }

            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) { return true; }

            public void Execute(object parameter)
            {
                try
                {
                    var hwnd = MenuWindow(_built);
                    var path = CurrentPath(hwnd);
                    if (string.IsNullOrEmpty(path))
                        return;
                    SettingHelper.Set("Pos_" + TypeId(path), string.Empty, Domain);
                    SettingHelper.Set("Lock_" + TypeId(path), string.Empty, Domain);

                    // Through the hook again: to DefaultPosition if one is set,
                    // otherwise the window stays where it is.
                    PlacedPath.Remove(hwnd);
                    RECT r;
                    if (GetWindowRect(hwnd, out r))
                        SetWindowPos(hwnd, IntPtr.Zero, r.left, r.top, r.right - r.left, r.bottom - r.top,
                            SWP_NOZORDER | SWP_NOACTIVATE);
                }
                catch { }
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
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);

        #endregion
    }
}
