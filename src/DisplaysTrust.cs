using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentSleep
{
    internal enum DisplayKind : byte { Unknown, Physical, Virtual }

    internal static class DisplayTrust
    {
        private static readonly Guid DisplayClass = new Guid("4D36E968-E325-11CE-BFC1-08002BE10318");
        private static readonly Guid MonitorClass = new Guid("4D36E96E-E325-11CE-BFC1-08002BE10318");
        private static readonly Guid Device = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
        private static readonly Guid Driver = new Guid("A8B865DD-2E3D-4094-AD97-E593A70C75D6");
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        // Exact upstream 25.7.23 driver-only packages (amd64 and arm64). The signed
        // SignPath Foundation catalogs were verified over both the INF and DLL.
        private static readonly string[][] ApprovedPackages =
        {
            new[] {
                "550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D",
                "08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6",
                "C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05"
            },
            new[] {
                "A8B8D1C939EE49BB44B4CC3EF9A820B9943B1DBFA456D5B6880B6F106B51011B",
                "9E62DD01DE5D80773819E6C92C76DACB175B4632E0D551026975DA5F815B5A97",
                "9BD5429A71F2103309B291C18D97DAA5E2D8E1A35794F375AE718F1F1EB27C4F"
            }
        };

        internal static DisplayKind Adapter(string adapterPath)
        {
            return WithDevice(adapterPath, delegate(IntPtr set, DeviceInfo info)
            {
                if (info.ClassGuid != DisplayClass) return DisplayKind.Unknown;
                string bus = Property(set, info, Device, 24, 0x12);
                if (Equal(bus, "PCI")) return DisplayKind.Physical;
                string hardware = Property(set, info, Device, 3, 0x2012);
                if (hardware == null || !hardware.Split('\0').Any(IsApprovedHardware))
                    return DisplayKind.Unknown;
                string match = Property(set, info, Driver, 8, 0x12);
                string provider = Property(set, info, Driver, 9, 0x12);
                if (!IsApprovedHardware(match) || !Equal(provider, "MikeTheTech"))
                    return DisplayKind.Unknown;
                string inf = Property(set, info, Driver, 5, 0x12);
                return ApprovedPackage(inf) ? DisplayKind.Virtual : DisplayKind.Unknown;
            });
        }

        internal static bool IsMonitor(string devicePath)
        {
            return WithDevice(devicePath, delegate(IntPtr set, DeviceInfo info) { return info.ClassGuid == MonitorClass; });
        }

        internal static bool IsApprovedHardware(string id)
        {
            return Equal(id, @"Root\MttVDD") || Equal(id, "MttVDD");
        }

        internal static bool IsIndirect(int technology) { return technology == 16 || technology == 17; }

        internal static bool IsDirect(int technology)
        {
            return technology >= 0 && technology <= 14 || technology == 18 || technology == int.MinValue;
        }

        internal static DisplayKind Classify(DisplayKind adapter, int technology, bool edidMonitor, bool forced)
        {
            if (forced) return DisplayKind.Unknown;
            if (adapter == DisplayKind.Virtual && (technology == 5 || IsIndirect(technology)))
                return DisplayKind.Virtual;
            return adapter == DisplayKind.Physical && IsDirect(technology) && edidMonitor
                ? DisplayKind.Physical : DisplayKind.Unknown;
        }

        internal static string StableId(string adapter, Ccd.MonitorInfo monitor)
        {
            if (string.IsNullOrWhiteSpace(adapter) || string.IsNullOrWhiteSpace(monitor.DevicePath)) return null;
            var bytes = Encoding.UTF8.GetBytes(adapter.ToUpperInvariant() + "\0" + monitor.DevicePath.ToUpperInvariant());
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(bytes));
        }

        private static bool ApprovedPackage(string inf)
        {
            if (inf == null || !Regex.IsMatch(inf, @"\Aoem[0-9]+\.inf\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return false;
            string published = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", inf);
            var storePath = new StringBuilder(1024);
            uint required;
            if (!SetupGetInfDriverStoreLocation(published, IntPtr.Zero, null, storePath, (uint)storePath.Capacity, out required))
                throw PnpError("resolve the installed virtual display driver package", Marshal.GetLastWin32Error());
            string storeInf = storePath.ToString();
            if (!Equal(Path.GetFileName(storeInf), "MttVDD.inf")) return false;

            string infHash = FileHash(storeInf);
            var package = ApprovedPackages.FirstOrDefault(p => Equal(infHash, p[0]));
            if (package == null) return false;
            string folder = Path.GetDirectoryName(storeInf);
            return Equal(FileHash(Path.Combine(folder, "MttVDD.cat")), package[1]) &&
                   Equal(FileHash(Path.Combine(folder, "MttVDD.dll")), package[2]);
        }

        private static string FileHash(string file)
        {
            try
            {
                using (var input = File.OpenRead(file))
                using (var sha = SHA256.Create())
                    return input.Length <= 4 * 1024 * 1024 ? Hex(sha.ComputeHash(input)) : null;
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException) { throw new InvalidOperationException("Windows could not read the installed virtual display package. Check its installation."); }
            catch (UnauthorizedAccessException) { throw new InvalidOperationException("Windows denied access to the installed virtual display package."); }
        }

        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }
        private static bool Equal(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        private static T WithDevice<T>(string devicePath, Func<IntPtr, DeviceInfo, T> inspect)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == InvalidHandle)
                throw PnpError("open Windows device information", Marshal.GetLastWin32Error());
            try
            {
                var iface = new InterfaceInfo { Size = (uint)Marshal.SizeOf(typeof(InterfaceInfo)) };
                if (!SetupDiOpenDeviceInterface(set, devicePath, 0, ref iface))
                    throw PnpError("bind a display path to its Windows device", Marshal.GetLastWin32Error());
                uint required;
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf(typeof(DeviceInfo)) };
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out required, ref info);
                int error = Marshal.GetLastWin32Error();
                if (error != 122 || required < 8 || required > 8192)
                    throw PnpError("size the display device interface", error);
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, out required, ref info))
                        throw PnpError("read the display device interface", Marshal.GetLastWin32Error());
                    return inspect(set, info);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        private static string Property(IntPtr set, DeviceInfo info, Guid category, uint index, uint expected)
        {
            var key = new PropertyKey { Category = category, Index = index };
            uint type, size;
            var bytes = new byte[4096];
            if (!SetupDiGetDeviceProperty(set, ref info, ref key, out type, bytes, (uint)bytes.Length, out size, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1168) return null;
                throw PnpError("read installed display driver metadata", error);
            }
            if (type != expected || size < 2 || size > bytes.Length || (size & 1) != 0) return null;
            return Encoding.Unicode.GetString(bytes, 0, (int)size).TrimEnd('\0');
        }

        private static InvalidOperationException PnpError(string action, int code)
        {
            return new InvalidOperationException("Windows could not " + action + " (error " + code + "). Retry when displays are connected and stable.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InterfaceInfo { public uint Size; public Guid ClassGuid; public uint Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Category; public uint Index; }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parent);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiOpenDeviceInterface(IntPtr set, string path, uint flags, ref InterfaceInfo info);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceInfo iface,
            IntPtr detail, uint detailSize, out uint required, ref DeviceInfo info);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref DeviceInfo info,
            ref PropertyKey key, out uint type, [Out] byte[] buffer, uint size, out uint required, uint flags);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupGetInfDriverStoreLocation(string inf, IntPtr alternatePlatform,
            string locale, StringBuilder storePath, uint size, out uint required);
    }
}
