using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AgentSleep
{
    internal sealed class IdleClock
    {
        internal const int GraceMilliseconds = 60000;
        private long armTime;
        private long lastObservedTime;
        private uint lastInput;
        private bool observed;

        internal void Reset(long now)
        {
            armTime = now;
            observed = false;
        }

        internal bool IsDue(uint tick, uint input, long now, int idleMilliseconds)
        {
            uint elapsed = unchecked(tick - input);
            if (!observed)
            {
                observed = true;
                lastInput = input;
                lastObservedTime = elapsed <= Int32.MaxValue ? now - elapsed : now;
            }
            else if (input != lastInput)
            {
                lastInput = input;
                lastObservedTime = now;
            }
            return elapsed <= Int32.MaxValue && elapsed >= idleMilliseconds &&
                now - lastObservedTime >= idleMilliseconds && now - armTime >= GraceMilliseconds;
        }
    }

    internal sealed class ResumeRestore
    {
        private bool interrupted;

        internal void Interrupt(bool pending) { interrupted |= pending; }

        internal bool ShouldRetry(bool unlocked, bool pending, string state)
        {
            if (!pending) interrupted = false;
            if (!interrupted || !unlocked || (state != "failed" && state != "fallback")) return false;
            interrupted = false;
            return true;
        }
    }

    internal sealed class TrayWindow : NativeWindow, IDisposable
    {
        private const int QueryEndSession = 0x0011;
        private const int EndSession = 0x0016;
        private const int DisplayChange = 0x007E;
        private const int PowerBroadcast = 0x0218;
        private const int SessionChange = 0x02B1;
        private const int HotkeyMessage = 0x0312;
        private const uint NoRepeat = 0x4000;
        private const uint Continuous = 0x80000000;
        private const uint SystemAndDisplay = 0x00000003;

        private int shortcutId;
        private uint modifiers;
        private Keys key;
        private bool sessionLocked;
        private bool suspended;
        private bool executionRequired;

        internal Action Hotkey;
        internal Action DisplayChanged;
        internal Action Interrupted;
        internal Action Resumed;
        internal bool SessionNotifications { get; private set; }
        private bool HotkeyRegistered { get { return shortcutId != 0; } }

        internal bool HasHotkey(uint expectedModifiers, Keys expectedKey)
        {
            return HotkeyRegistered && modifiers == expectedModifiers && key == expectedKey;
        }

        internal TrayWindow()
        {
            CreateHandle(new CreateParams { Caption = "AgentSleep notifications" });
            TryEnableSessionNotifications();
        }

        internal void TryEnableSessionNotifications()
        {
            if (!SessionNotifications) SessionNotifications = WTSRegisterSessionNotification(Handle, 0);
        }

        internal bool TryRegister(uint requestedModifiers, Keys requestedKey)
        {
            if (HasHotkey(requestedModifiers, requestedKey)) return true;
            int next = shortcutId == 1 ? 2 : 1;
            if (!RegisterHotKey(Handle, next, requestedModifiers | NoRepeat, (uint)requestedKey)) return false;
            if (HotkeyRegistered) UnregisterHotKey(Handle, shortcutId);
            shortcutId = next;
            modifiers = requestedModifiers;
            key = requestedKey;
            return true;
        }

        internal bool IsLocalDesktop()
        {
            return SessionNotifications && !sessionLocked && !suspended && IsUnlockedConsole();
        }

        internal static bool IsUnlockedConsole()
        {
            uint session;
            if (!Environment.UserInteractive || GetSystemMetrics(0x1000) != 0 ||
                !ProcessIdToSessionId(GetCurrentProcessId(), out session) || session != WTSGetActiveConsoleSessionId())
                return false;

            IntPtr desktop = OpenInputDesktop(0, false, 0x0001);
            if (desktop == IntPtr.Zero) return false;
            try
            {
                var name = new StringBuilder(128);
                uint used;
                return GetUserObjectInformation(desktop, 2, name, 256, out used) &&
                    String.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseDesktop(desktop); }
        }

        internal static bool TryGetLastInput(out uint now, out uint input)
        {
            var info = new LastInputInfo { Size = 8 };
            bool ok = GetLastInputInfo(ref info);
            now = GetTickCount();
            input = info.Tick;
            return ok && unchecked(now - input) <= Int32.MaxValue;
        }

        internal bool RequestPower(bool required)
        {
            if (required == executionRequired) return true;
            if (SetThreadExecutionState(Continuous | (required ? SystemAndDisplay : 0u)) == 0) return false;
            executionRequired = required;
            return true;
        }

        protected override void WndProc(ref Message message)
        {
            int reason = unchecked((int)message.WParam.ToInt64());
            if (message.Msg == HotkeyMessage && HotkeyRegistered && reason == shortcutId && Hotkey != null) Hotkey();
            if (message.Msg == DisplayChange && DisplayChanged != null) DisplayChanged();
            if (message.Msg == PowerBroadcast)
            {
                if (reason == 4)
                {
                    suspended = true;
                    if (Interrupted != null) Interrupted();
                }
                else if (reason == 6 || reason == 7 || reason == 18)
                {
                    suspended = false;
                    if (Resumed != null) Resumed();
                }
            }
            if (message.Msg == SessionChange)
            {
                if (reason == 2 || reason == 3 || reason == 4 || reason == 6 || reason == 7)
                {
                    sessionLocked = true;
                    if (Interrupted != null) Interrupted();
                }
                else if (reason == 1 || reason == 5 || reason == 8)
                {
                    sessionLocked = false;
                    if (Resumed != null) Resumed();
                }
            }
            if (message.Msg == QueryEndSession)
            {
                suspended = true;
                if (Interrupted != null) Interrupted();
            }
            else if (message.Msg == EndSession && reason == 0)
            {
                suspended = false;
                if (Resumed != null) Resumed();
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            RequestPower(false);
            if (HotkeyRegistered) UnregisterHotKey(Handle, shortcutId);
            if (SessionNotifications) WTSUnRegisterSessionNotification(Handle);
            DestroyHandle();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo { internal uint Size, Tick; }

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(
            IntPtr handle, int index, StringBuilder value, uint length, out uint needed);
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
        [DllImport("kernel32.dll")] private static extern uint GetTickCount();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
        [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, uint flags);
        [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
    }
}
