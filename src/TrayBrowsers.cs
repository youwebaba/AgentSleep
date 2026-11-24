using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AgentSleep
{
    internal sealed partial class BrowserInventory
    {
        private const int ShowWithoutActivation = 4;
        private const uint MoveAsynchronouslyWithoutActivationOrReordering = 0x4014;
        private static readonly HashSet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "chromium", "waterfox", "librewolf"
        };

        internal readonly Dictionary<IntPtr, BrowserWindow> Visible = new Dictionary<IntPtr, BrowserWindow>();
        internal readonly Dictionary<IntPtr, BrowserWindow> Minimized = new Dictionary<IntPtr, BrowserWindow>();
        private readonly HashSet<IntPtr> showRequested = new HashSet<IntPtr>();

        internal static BrowserInventory Read()
        {
            var result = new BrowserInventory();
            var processCache = new Dictionary<uint, long>();
            EnumWindowCallback inspect = delegate(IntPtr hwnd, IntPtr ignored)
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero) return true;
                var name = new StringBuilder(64);
                GetClassName(hwnd, name, name.Capacity);
                string windowClass = name.ToString();
                if (windowClass != "Chrome_WidgetWin_1" && windowClass != "MozillaWindowClass") return true;

                if (IsCloaked(hwnd)) return true;
                uint owner;
                uint thread = GetWindowThreadProcessId(hwnd, out owner);
                if (thread == 0) return true;
                long started;
                if (!processCache.TryGetValue(owner, out started))
                {
                    started = ReadBrowserStart(owner);
                    processCache[owner] = started;
                }
                if (started != 0)
                    (IsIconic(hwnd) ? result.Minimized : result.Visible)[hwnd] = new BrowserWindow(owner, thread, started);
                return true;
            };
            if (!EnumWindows(inspect, IntPtr.Zero)) throw new InvalidOperationException("Browser windows could not be inspected.");
            return result;
        }

        private static long ReadBrowserStart(uint processId)
        {
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                    if (Browsers.Contains(process.ProcessName)) return process.StartTime.ToUniversalTime().Ticks;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return 0;
        }

        internal string Summary()
        {
            string status = Visible.Count == 0 ? "No supported visible browser windows were available to verify." :
                String.Format("Verified desktop bounds for {0} supported browser window(s).", Visible.Count);
            if (Minimized.Count != 0) status += String.Format(" {0} minimized window(s) were left minimized.", Minimized.Count);
            return status;
        }

        internal bool VerifyInitialVisibility(BrowserInventory previous, Func<IntPtr, BrowserWindow, bool> tryShow)
        {
            if (previous == null) return true;
            bool allVisible = true;
            foreach (KeyValuePair<IntPtr, BrowserWindow> browser in previous.Visible)
            {
                BrowserWindow observed;
                if (Visible.TryGetValue(browser.Key, out observed) && browser.Value.Same(observed)) continue;
                allVisible = false;
                if (!previous.showRequested.Contains(browser.Key) && Minimized.TryGetValue(browser.Key, out observed) &&
                    browser.Value.Same(observed) && tryShow(browser.Key, observed)) previous.showRequested.Add(browser.Key);
            }
            return allVisible;
        }

        private static bool CurrentWindow(IntPtr hwnd, BrowserWindow browser)
        {
            uint processId;
            return GetWindowThreadProcessId(hwnd, out processId) == browser.ThreadId && processId == browser.ProcessId &&
                ReadBrowserStart(processId) == browser.ProcessStart;
        }

        private static bool RestoreWithoutActivating(IntPtr hwnd, BrowserWindow browser)
        {
            return CurrentWindow(hwnd, browser) && IsWindowVisible(hwnd) && IsIconic(hwnd) &&
                ShowWindowAsync(hwnd, ShowWithoutActivation);
        }

        internal bool Verify(BrowserInventory previous, Screen screen)
        {
            if (!VerifyInitialVisibility(previous, RestoreWithoutActivating)) return false;

            bool allReachable = true;
            Rectangle area = screen.WorkingArea;
            foreach (KeyValuePair<IntPtr, BrowserWindow> browser in Visible)
            {
                IntPtr hwnd = browser.Key;
                BrowserWindow previouslyMinimized;
                if (previous != null && previous.Minimized.TryGetValue(hwnd, out previouslyMinimized) &&
                    browser.Value.Same(previouslyMinimized)) continue;
                if (!CurrentWindow(hwnd, browser.Value) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
                Rect bounds;
                bool haveBounds = DwmGetWindowAttribute(hwnd, 9, out bounds, Marshal.SizeOf(typeof(Rect))) == 0;
                if (!haveBounds && !GetWindowRect(hwnd, out bounds)) return false;
                if (IsReachable(bounds.Rectangle, area, screen.Bounds)) continue;

                if (!GetWindowRect(hwnd, out bounds) || bounds.Rectangle.Width <= 0 || bounds.Rectangle.Height <= 0)
                    return false;
                int width = Math.Min(bounds.Rectangle.Width, area.Width);
                int height = Math.Min(bounds.Rectangle.Height, area.Height);
                int x = area.Left + (area.Width - width) / 2;
                int y = area.Top + (area.Height - height) / 2;
                if (CurrentWindow(hwnd, browser.Value) && IsWindowVisible(hwnd) && !IsIconic(hwnd))
                    SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, MoveAsynchronouslyWithoutActivationOrReordering);
                // Recheck actual bounds after the browser processes the asynchronous request.
                allReachable = false;
            }
            return allReachable;
        }

        internal sealed class BrowserWindow
        {
            internal readonly uint ProcessId, ThreadId;
            internal readonly long ProcessStart;

            internal BrowserWindow(uint processId, uint threadId, long processStart)
            {
                ProcessId = processId;
                ThreadId = threadId;
                ProcessStart = processStart;
            }

            internal bool Same(BrowserWindow other)
            {
                return ProcessId == other.ProcessId && ThreadId == other.ThreadId && ProcessStart == other.ProcessStart;
            }
        }

        internal static bool IsReachable(Rectangle bounds, Rectangle area, Rectangle screen)
        {
            Rectangle visible = Rectangle.Intersect(bounds, screen);
            if (visible.Width < 120 || visible.Height < 80) return false;
            // Account for invisible window frames and allow fullscreen bounds over the taskbar.
            const int frame = 12;
            Rectangle usable = area;
            usable.Inflate(frame, frame);
            if (usable.Contains(bounds)) return true;
            return Math.Abs(bounds.Left - screen.Left) <= frame && Math.Abs(bounds.Top - screen.Top) <= frame &&
                Math.Abs(bounds.Right - screen.Right) <= frame && Math.Abs(bounds.Bottom - screen.Bottom) <= frame;
        }

        internal static Screen ReadSingleScreen()
        {
            Screen[] screens = Screen.AllScreens;
            return screens.Length == 1 && screens[0].Primary && screens[0].WorkingArea.Width >= 240 &&
                screens[0].WorkingArea.Height >= 160 ? screens[0] : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            internal int Left, Top, Right, Bottom;
            internal Rectangle Rectangle { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
            internal static Rect From(Rectangle rectangle)
            {
                return new Rect { Left = rectangle.Left, Top = rectangle.Top, Right = rectangle.Right, Bottom = rectangle.Bottom };
            }
        }

        private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr data);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out Rect value, int size);
    }
}
