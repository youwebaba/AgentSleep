using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace AgentSleep
{
    internal sealed partial class BrowserInventory
    {
        private const int NormalShow = 1, MinimizedShow = 2, MaximizedShow = 3;
        private const uint AsyncPlacement = 4;
        private readonly Dictionary<IntPtr, SavedPlacement> physicalPlacements = new Dictionary<IntPtr, SavedPlacement>();
        private bool physicalCaptureFailed;

        internal static BrowserInventory CapturePhysical(IDisplays displays)
        {
            var snapshot = new BrowserInventory();
            if (!TrayWindow.IsUnlockedConsole()) { snapshot.physicalCaptureFailed = true; return snapshot; }
            try
            {
                BrowserInventory windows = Read();
                if (windows.Visible.Count == 0) return snapshot;
                Dictionary<Rectangle, string> physical = PhysicalLayout(displays);
                foreach (KeyValuePair<IntPtr, BrowserWindow> browser in windows.Visible)
                {
                    if (!TrayWindow.IsUnlockedConsole()) { snapshot.physicalCaptureFailed = true; break; }
                    string displayId;
                    Monitor monitor;
                    if (!CurrentWindow(browser.Key, browser.Value) || !CanPlaceBrowser(browser.Value.ProcessId) ||
                        !ReadMonitor(MonitorFromWindow(browser.Key, 0), out monitor) ||
                        !physical.TryGetValue(monitor.Bounds.Rectangle, out displayId)) continue;
                    SavedPlacement saved = ReadPlacement(browser.Key, monitor, displayId);
                    if (saved == null)
                    {
                        if (CurrentWindow(browser.Key, browser.Value)) snapshot.physicalCaptureFailed = true;
                        continue;
                    }
                    Rectangle visible = Rectangle.Intersect(saved.Geometry.Frame, saved.DisplayBounds);
                    if (saved.Geometry.Show == MinimizedShow || visible.Width < 120 || visible.Height < 80) continue;
                    saved.Identity = browser.Value;
                    snapshot.physicalPlacements.Add(browser.Key, saved);
                }
            }
            catch (Exception) { snapshot.physicalCaptureFailed = true; }
            return snapshot;
        }

        internal bool RepairAfterExact(IDisplays displays)
        {
            if (physicalPlacements.Count == 0) return !physicalCaptureFailed;
            if (!TrayWindow.IsUnlockedConsole()) return false;
            try
            {
                var clock = Stopwatch.StartNew();
                var focus = new ForegroundGuard();
                var relocations = new Dictionary<IntPtr, MaximizedRelocation>();
                do
                {
                    if (!TrayWindow.IsUnlockedConsole()) return false;
                    Dictionary<Rectangle, string> physical = PhysicalLayout(displays);
                    if (!TrayWindow.IsUnlockedConsole()) return false;
                    BrowserInventory live = Read();
                    bool verified = true;
                    foreach (KeyValuePair<IntPtr, SavedPlacement> entry in physicalPlacements)
                    {
                        if (clock.ElapsedMilliseconds >= 2000 || !TrayWindow.IsUnlockedConsole())
                        {
                            focus.Preserve();
                            return false;
                        }
                        IntPtr handle = entry.Key;
                        SavedPlacement original = entry.Value;
                        BrowserWindow observed;
                        if (!live.Visible.TryGetValue(handle, out observed) && !live.Minimized.TryGetValue(handle, out observed))
                        {
                            if (CurrentWindow(handle, original.Identity)) verified = false;
                            continue;
                        }
                        if (!original.Identity.Same(observed) || !CanPlaceBrowser(observed.ProcessId)) continue;
                        if (!VerifyOrRepair(handle, original, physical, focus, relocations)) verified = false;
                    }
                    focus.Preserve();
                    if (verified) return !physicalCaptureFailed;
                    Thread.Sleep(125);
                }
                while (clock.ElapsedMilliseconds < 2000);
            }
            catch (Exception) { }
            return false;
        }

        private static bool VerifyOrRepair(IntPtr handle, SavedPlacement original, Dictionary<Rectangle, string> physical,
            ForegroundGuard focus, Dictionary<IntPtr, MaximizedRelocation> relocations)
        {
            string displayId;
            Rect targetBounds = Rect.From(original.DisplayBounds);
            Monitor target;
            if (!physical.TryGetValue(original.DisplayBounds, out displayId) || displayId != original.Geometry.DisplayId ||
                !ReadMonitor(MonitorFromRect(ref targetBounds, 0), out target) || target.Bounds.Rectangle != original.DisplayBounds) return false;
            if (!CurrentWindow(handle, original.Identity)) return true;

            Monitor actual;
            if (!ReadMonitor(MonitorFromWindow(handle, 2), out actual)) return false;
            string actualId;
            physical.TryGetValue(actual.Bounds.Rectangle, out actualId);
            SavedPlacement observed = ReadPlacement(handle, actual, actualId);
            if (observed == null) return false;

            Point adjustment = WorkspaceAdjustment(original.DisplayBounds, original.Work, target.Bounds.Rectangle, target.Work.Rectangle);
            PlacementGeometry expected = original.Geometry;
            expected.Normal.Offset(adjustment);
            PlacementAction action = DecidePlacement(expected, observed.Geometry);
            if (action == PlacementAction.Verified) return CurrentWindow(handle, original.Identity);
            if (!TrayWindow.IsUnlockedConsole() || !CurrentWindow(handle, original.Identity) || !IsWindowVisible(handle) || IsCloaked(handle)) return false;

            if (action == PlacementAction.Normalize || action == PlacementAction.MoveNormal || action == PlacementAction.Maximize)
            {
                Rectangle normalScreen = expected.Normal;
                normalScreen.Offset(target.Work.Left - target.Bounds.Left, target.Work.Top - target.Bounds.Top);
                Rect anchor = Rect.From(normalScreen);
                Monitor normalMonitor;
                if (normalScreen.Width <= 0 || normalScreen.Height <= 0 ||
                    !ReadMonitor(MonitorFromRect(ref anchor, 0), out normalMonitor) || normalMonitor.Bounds.Rectangle != original.DisplayBounds) return false;

                MaximizedRelocation progress;
                if (!relocations.TryGetValue(handle, out progress))
                {
                    progress = new MaximizedRelocation();
                    relocations.Add(handle, progress);
                }
                if (!progress.TryBegin(action)) return false;
                if (action == PlacementAction.MoveNormal) MoveFrame(handle, normalScreen);
                if (action == PlacementAction.Maximize && ShowWindowAsync(handle, MaximizedShow))
                {
                    progress.MaximizePosted = true;
                    focus.Watch(handle);
                }
            }
            if (action == PlacementAction.Show) RestoreWithoutActivating(handle, original.Identity);
            if (action == PlacementAction.Move) MoveFrame(handle, original.Outer);
            if (action == PlacementAction.Place || action == PlacementAction.Normalize)
            {
                NativePlacement placement = original.Native;
                placement.Flags = AsyncPlacement;
                placement.Show = ShowWithoutActivation;
                placement.Normal = Rect.From(expected.Normal);
                if (placement.Max != new Point(-1, -1)) placement.Max.Offset(adjustment);
                SetWindowPlacement(handle, ref placement);
            }
            // Queued window changes require fresh native readback.
            return false;
        }

        private static void MoveFrame(IntPtr handle, Rectangle bounds)
        {
            SetWindowPos(handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                MoveAsynchronouslyWithoutActivationOrReordering);
        }

        internal static PlacementAction DecidePlacement(PlacementGeometry expected, PlacementGeometry observed)
        {
            if (expected.DisplayId == observed.DisplayId && expected.Frame == observed.Frame && expected.Dpi == observed.Dpi &&
                expected.Fullscreen == observed.Fullscreen && expected.Show == observed.Show &&
                (expected.Show != MaximizedShow || expected.Normal == observed.Normal)) return PlacementAction.Verified;
            if (expected.Fullscreen)
                return observed.Show == MinimizedShow ? PlacementAction.Show : observed.Borderless ? PlacementAction.Move : PlacementAction.Unavailable;
            if (observed.Fullscreen) return PlacementAction.Unavailable;
            if (expected.Show == MaximizedShow)
            {
                if (observed.Show != NormalShow) return PlacementAction.Normalize;
                return expected.DisplayId == observed.DisplayId && expected.Normal == observed.Normal && expected.Dpi == observed.Dpi
                    ? PlacementAction.Maximize : PlacementAction.MoveNormal;
            }
            return expected.Show == NormalShow && observed.Show == NormalShow ? PlacementAction.Move : PlacementAction.Place;
        }

        internal static Point WorkspaceAdjustment(Rectangle savedBounds, Rectangle savedWork, Rectangle currentBounds, Rectangle currentWork)
        {
            return new Point(savedWork.Left - savedBounds.Left - (currentWork.Left - currentBounds.Left),
                savedWork.Top - savedBounds.Top - (currentWork.Top - currentBounds.Top));
        }

        private static Dictionary<Rectangle, string> PhysicalLayout(IDisplays displays)
        {
            return DisplayBaseline.Decode(displays.Capture()).Where(layout => layout.Kind == DisplayKind.Physical)
                .ToDictionary(layout => new Rectangle(layout.Source.X, layout.Source.Y, (int)layout.Source.Width, (int)layout.Source.Height),
                    layout => layout.Id);
        }

        private static SavedPlacement ReadPlacement(IntPtr handle, Monitor monitor, string displayId)
        {
            var placement = new NativePlacement { Size = (uint)Marshal.SizeOf(typeof(NativePlacement)) };
            Rect raw, frame;
            if (!GetWindowPlacement(handle, ref placement) || !GetWindowRect(handle, out raw)) return null;
            if (DwmGetWindowAttribute(handle, 9, out frame, Marshal.SizeOf(typeof(Rect))) != 0) frame = raw;
            bool borderless = (GetWindowLongPtr(handle, -16).ToInt64() & 0x00c00000L) == 0;
            uint dpi = GetDpiForWindow(handle);
            if (dpi == 0) return null;
            return new SavedPlacement {
                Native = placement, Outer = raw.Rectangle, DisplayBounds = monitor.Bounds.Rectangle, Work = monitor.Work.Rectangle,
                Geometry = new PlacementGeometry {
                    Frame = frame.Rectangle, Normal = placement.Normal.Rectangle, Dpi = dpi,
                    DisplayId = displayId, Show = placement.Show, Borderless = borderless,
                    Fullscreen = borderless && placement.Show != MinimizedShow && frame.Rectangle == monitor.Bounds.Rectangle
                }
            };
        }

        private static bool ReadMonitor(IntPtr handle, out Monitor monitor)
        {
            monitor = new Monitor { Size = (uint)Marshal.SizeOf(typeof(Monitor)) };
            return handle != IntPtr.Zero && GetMonitorInfo(handle, ref monitor);
        }

        private static bool IsCloaked(IntPtr handle)
        {
            int cloaked;
            return DwmGetWindowAttribute(handle, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0;
        }

        private static bool CanPlaceBrowser(uint processId)
        {
            IntPtr process = OpenProcess(0x1000, false, processId);
            if (process == IntPtr.Zero) return false;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(process, 8, out token)) return false;
                try
                {
                    int elevated, used;
                    return GetTokenInformation(token, 20, out elevated, sizeof(int), out used) && elevated == 0;
                }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(process); }
        }

        private sealed class ForegroundGuard
        {
            private readonly IntPtr original = GetForegroundWindow();
            private readonly uint process, ownerThread, input;
            private readonly HashSet<IntPtr> requested = new HashSet<IntPtr>();
            private readonly bool inputKnown;

            internal ForegroundGuard()
            {
                ownerThread = GetWindowThreadProcessId(original, out process);
                uint tick;
                inputKnown = TrayWindow.TryGetLastInput(out tick, out input);
            }

            internal void Watch(IntPtr window) { requested.Add(window); }

            internal void Preserve()
            {
                if (!TrayWindow.IsUnlockedConsole()) return;
                uint tick, lastInput, currentProcess;
                IntPtr foreground = GetForegroundWindow();
                if (inputKnown && ownerThread != 0 && foreground != original && requested.Contains(foreground) &&
                    TrayWindow.TryGetLastInput(out tick, out lastInput) && input == lastInput &&
                    GetWindowThreadProcessId(original, out currentProcess) == ownerThread && process == currentProcess &&
                    IsWindowVisible(original) && !IsIconic(original)) SetForegroundWindow(original);
            }
        }

        internal enum PlacementAction { Verified, Move, Place, Show, Unavailable, Normalize, MoveNormal, Maximize }

        internal sealed class MaximizedRelocation
        {
            private int normalizations, moves, maximizations;
            internal bool MaximizePosted;

            internal bool TryBegin(PlacementAction action)
            {
                if (MaximizePosted) return false;
                switch (action)
                {
                    case PlacementAction.Normalize: return normalizations++ < 2;
                    case PlacementAction.MoveNormal: return moves++ < 2;
                    case PlacementAction.Maximize: return maximizations++ < 2;
                    default: return false;
                }
            }
        }
        internal struct PlacementGeometry
        {
            internal string DisplayId;
            internal Rectangle Frame, Normal;
            internal uint Dpi;
            internal int Show;
            internal bool Borderless, Fullscreen;
        }

        private sealed class SavedPlacement
        {
            internal BrowserWindow Identity;
            internal PlacementGeometry Geometry;
            internal NativePlacement Native;
            internal Rectangle Outer, DisplayBounds, Work;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePlacement { internal uint Size, Flags; internal int Show; internal Point Min, Max; internal Rect Normal; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Monitor { internal uint Size; internal Rect Bounds, Work; internal uint Flags; }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr handle, ref Monitor monitor);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr handle, ref NativePlacement placement);
        [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr handle, ref NativePlacement placement);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int type, out int value, int size, out int used);
    }
}
