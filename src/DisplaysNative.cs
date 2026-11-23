using System;
using System.Runtime.InteropServices;

namespace AgentSleep
{
    internal static class Ccd
    {
        internal const uint Active = 1, VirtualMode = 8, BoostRefresh = 16;
        internal const uint None = uint.MaxValue;
        private const uint QueryAll = 0x51, QueryActive = 0x52;
        private const uint Supplied = 0x28020, Validate = 0x40, Apply = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Luid { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Endpoint
        {
            public Luid Adapter;
            public uint Id;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Ratio
        {
            public uint Numerator, Denominator;

            internal bool Same(Ratio other)
            {
                if (Denominator == 0 || other.Denominator == 0)
                    return Numerator == other.Numerator && Denominator == other.Denominator;
                return (ulong)Numerator * other.Denominator == (ulong)other.Numerator * Denominator;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SourcePath
        {
            public Endpoint Key;
            public uint Index, Status;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TargetPath
        {
            public Endpoint Key;
            public uint Index;
            public int Technology, Rotation, Scaling;
            public Ratio Refresh;
            public int Scan, Available;
            public uint Status;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Path
        {
            public SourcePath Source;
            public TargetPath Target;
            public uint Flags;

            internal bool IsActive { get { return (Flags & Active) != 0; } }
            internal bool IsAvailable { get { return Target.Available != 0; } }
            internal bool HasVirtualMode { get { return (Flags & VirtualMode) != 0; } }
            internal uint SourceIndex { get { return HasVirtualMode ? Decode(Source.Index >> 16) : Source.Index; } }
            internal uint TargetIndex { get { return HasVirtualMode ? Decode(Target.Index >> 16) : Target.Index; } }
            internal uint DesktopIndex { get { return HasVirtualMode ? Decode(Target.Index & 0xffff) : None; } }

            private static uint Decode(uint index) { return index == 0xffff ? None : index; }

            internal void SetIndices(uint source, uint target, uint desktop)
            {
                Source.Index = HasVirtualMode ? (source << 16) | 0xffff : source;
                Target.Index = HasVirtualMode ? (target << 16) | (desktop & 0xffff) : target;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SourceMode
        {
            public uint Width, Height, Format;
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Signal
        {
            public ulong PixelRate;
            public Ratio HSync, VSync;
            public uint Width, Height, TotalWidth, TotalHeight, AdditionalInfo, Scan;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DesktopImage
        {
            public int Width, Height, Left, Top, Right, Bottom, ClipLeft, ClipTop, ClipRight, ClipBottom;
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        internal struct Mode
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(4)] private uint id;
            [FieldOffset(8)] private Luid adapter;
            [FieldOffset(16)] public SourceMode Source;
            [FieldOffset(16)] public Signal Signal;
            [FieldOffset(16)] public DesktopImage Desktop;
            [FieldOffset(16)] public ulong Data0;
            [FieldOffset(24)] public ulong Data1;
            [FieldOffset(32)] public ulong Data2;
            [FieldOffset(40)] public ulong Data3;
            [FieldOffset(48)] public ulong Data4;
            [FieldOffset(56)] public ulong Data5;

            public Endpoint Key
            {
                get { return new Endpoint { Adapter = adapter, Id = id }; }
                set { adapter = value.Adapter; id = value.Id; }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Header
        {
            public uint Type, Size;
            public Endpoint Key;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MonitorInfo
        {
            public Header Header;
            public uint Flags;
            public int Technology;
            public ushort Manufacturer, Product;
            public uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Friendly;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterInfo
        {
            public Header Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PreferredMode
        {
            public Header Header;
            public uint Width, Height;
            public Signal Signal;
        }

        internal static void CheckAbi()
        {
            if (Marshal.SizeOf(typeof(Path)) != 72 || Marshal.SizeOf(typeof(Mode)) != 64 ||
                Marshal.SizeOf(typeof(MonitorInfo)) != 420 || Marshal.SizeOf(typeof(AdapterInfo)) != 276 ||
                Marshal.SizeOf(typeof(PreferredMode)) != 80)
                throw new InvalidOperationException("The Windows display API layout is unsupported by this build.");
        }

        internal static void Query(bool all, out Path[] paths, out Mode[] modes)
        {
            uint flags = all ? QueryAll : QueryActive;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint pathCount, modeCount;
                Check(GetDisplayConfigBufferSizes(flags, out pathCount, out modeCount), "size the display configuration");
                if (pathCount > 4096 || modeCount > 4096)
                    throw new InvalidOperationException("Windows reported too many display paths or modes.");
                paths = new Path[Math.Max(pathCount, 1)];
                modes = new Mode[Math.Max(modeCount, 1)];
                int status = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (status == 122) continue;
                Check(status, "read the display configuration");
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
                return;
            }
            throw new InvalidOperationException("The display configuration changed while reading it. Retry when it settles.");
        }

        internal static MonitorInfo GetMonitor(Endpoint key)
        {
            var info = new MonitorInfo { Header = Packet<MonitorInfo>(2, key) };
            Check(GetMonitorInfo(ref info), "identify a connected monitor");
            return info;
        }

        internal static string GetAdapter(Luid adapter)
        {
            var info = new AdapterInfo { Header = Packet<AdapterInfo>(4, new Endpoint { Adapter = adapter }) };
            Check(GetAdapterInfo(ref info), "identify a display adapter");
            return info.DevicePath;
        }

        internal static SourceMode GetPreferred(Endpoint key, int x, int y)
        {
            var info = new PreferredMode { Header = Packet<PreferredMode>(3, key) };
            Check(GetPreferredMode(ref info), "read the display's preferred mode");
            return new SourceMode { Width = info.Width, Height = info.Height, Format = 4, X = x, Y = y };
        }

        private static Header Packet<T>(uint type, Endpoint key)
        {
            return new Header { Type = type, Size = (uint)Marshal.SizeOf(typeof(T)), Key = key };
        }

        internal static int Set(Path[] paths, Mode[] modes, bool apply)
        {
            return SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, Supplied | (apply ? Apply : Validate));
        }

        internal static void Check(int status, string action)
        {
            if (status != 0)
                throw new InvalidOperationException("Windows could not " + action + " (error " + status + "). Check the local display session and connections.");
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] Path[] paths,
            ref uint modeCount, [Out] Mode[] modes, IntPtr topology);
        [DllImport("user32.dll")]
        private static extern int SetDisplayConfig(uint pathCount, [In] Path[] paths,
            uint modeCount, [In] Mode[] modes, uint flags);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int GetMonitorInfo(ref MonitorInfo packet);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int GetAdapterInfo(ref AdapterInfo packet);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int GetPreferredMode(ref PreferredMode packet);
    }
}
