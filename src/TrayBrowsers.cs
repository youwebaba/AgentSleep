using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AgentSleep
{
    internal sealed class BrowserInventory
    {
        private static readonly HashSet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "chromium", "waterfox", "librewolf"
        };

        internal readonly Dictionary<IntPtr, uint> Visible = new Dictionary<IntPtr, uint>();
        internal int Minimized;

        internal static BrowserInventory Read()
        {
            var result = new BrowserInventory();
            var processCache = new Dictionary<uint, bool>();
            EnumWindowCallback inspect = delegate(IntPtr hwnd, IntPtr ignored)
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero) return true;
                var name = new StringBuilder(64);
                GetClassName(hwnd, name, name.Capacity);
                string windowClass = name.ToString();
                if (windowClass != "Chrome_WidgetWin_1" && windowClass != "MozillaWindowClass") return true;

                int cloaked;
                if (DwmGetWindowAttribute(hwnd, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                uint owner;
                if (GetWindowThreadProcessId(hwnd, out owner) == 0) return true;
                bool browser;
                if (!processCache.TryGetValue(owner, out browser))
                {
                    try
                    {
                        using (Process process = Process.GetProcessById((int)owner))
                            browser = Browsers.Contains(process.ProcessName);
                    }
                    catch (ArgumentException) { browser = false; }
                    catch (InvalidOperationException) { browser = false; }
                    catch (System.ComponentModel.Win32Exception) { browser = false; }
                    processCache[owner] = browser;
                }
                if (!browser) return true;
                if (IsIconic(hwnd)) result.Minimized++;
                else result.Visible[hwnd] = owner;
                return true;
            };
            if (!EnumWindows(inspect, IntPtr.Zero)) throw new InvalidOperationException("Browser windows could not be inspected.");
            return result;
        }

        internal string Summary()
        {
            string status = Visible.Count == 0 ? "No supported visible browser windows were available to verify." :
                String.Format("Verified desktop bounds for {0} supported browser window(s).", Visible.Count);
            if (Minimized != 0) status += String.Format(" {0} minimized window(s) were left minimized.", Minimized);
            return status;
        }

        internal bool Verify(BrowserInventory previous, Screen screen)
        {
            if (previous != null)
                foreach (KeyValuePair<IntPtr, uint> browser in previous.Visible)
                {
                    uint owner;
                    if (!Visible.TryGetValue(browser.Key, out owner) || owner != browser.Value) return false;
                }

            bool allReachable = true;
            Rectangle area = screen.WorkingArea;
            foreach (IntPtr hwnd in Visible.Keys)
            {
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
                SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x4014);
                // Recheck actual bounds after the browser processes the asynchronous request.
                allReachable = false;
            }
            return allReachable;
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
        }

        private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr data);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
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
