using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AgentSleep
{
    internal enum RecoveryPhase
    {
        Prepared, Extending, Isolating, Isolated, Restoring, Failed, Fallback, Complete, Aborted
    }

    internal enum RecoveryReason
    {
        None, Manual, Deadline, OwnerExit, OwnerHang, Activation, Arm, WatchdogUnavailable
    }

    internal enum RecoveryRequest
    {
        Unspecified, Hotkey, TrayRestore, StandaloneRestore, DesktopCheck, SessionInterrupted,
        PowerUnavailable, HotkeyUnavailable, TopologyChanged, TrayExit
    }

    internal sealed class RecoveryRecord
    {
        internal Guid Id;
        internal int OwnerPid;
        internal long OwnerStart;
        internal int Revision;
        internal bool Timed;
        internal RecoveryPhase Phase;
        internal RecoveryReason Reason;
        internal byte[] Baseline;

        internal bool Pending { get { return Baseline.Length > 0; } }
    }

    internal sealed class RecoveryStorage
    {
        private const int Magic = 0x31525341;
        private const int DigestSize = 32;
        private const int MaxBaseline = 1024 * 1024;
        private readonly SecurityIdentifier user;
        private readonly long logonId;
        private readonly long logonTime;
        private readonly string name;
        private readonly string logPath;
        internal readonly string JournalPath;
        internal readonly int Session;

        private RecoveryStorage(string path, string suffix, long logonId, long logonTime, SecurityIdentifier user, int session)
        {
            this.user = user;
            this.logonId = logonId;
            this.logonTime = logonTime;
            Session = session;
            name = @"Local\AgentSleep.Recovery." + user.Value + suffix;
            JournalPath = Path.Combine(path, "recovery-" + session + ".bin");
            logPath = Path.Combine(path, "recovery-" + session + ".log");

            var access = new DirectorySecurity();
            access.SetAccessRuleProtection(true, false);
            access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(path, access);
        }

        internal static RecoveryStorage Open()
        {
            using (var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
            using (var owner = Process.GetCurrentProcess())
            {
                RecoveryLogon logon = RecoveryLogon.Read(identity.Token, owner.SessionId);
                string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (String.IsNullOrWhiteSpace(localData)) throw new IOException("recovery-local-data");
                string path = Path.Combine(localData, "AgentSleep", "Recovery");
                return new RecoveryStorage(path, "", logon.Id, logon.Time, identity.User, owner.SessionId);
            }
        }

        internal static RecoveryStorage ForTest(string path, Guid scope, long testLogon)
        {
            using (var identity = WindowsIdentity.GetCurrent())
            using (var owner = Process.GetCurrentProcess())
                return new RecoveryStorage(path, "." + scope.ToString("N"), 1, testLogon, identity.User, owner.SessionId);
        }

        internal Mutex OpenGate()
        {
            bool created;
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
            return new Mutex(false, name, out created, security);
        }

        internal string EventName(Guid id, string kind) { return name + "." + id.ToString("N") + "." + kind; }

        internal EventWaitHandle CreateEvent(Guid id, string kind, EventResetMode mode)
        {
            bool created;
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            var handle = new EventWaitHandle(false, mode, EventName(id, kind), out created, security);
            if (created) return handle;
            handle.Dispose();
            throw new IOException("recovery-event-exists");
        }

        internal RecoveryRecord Read()
        {
            byte[] bytes;
            try
            {
                using (var stream = File.Open(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 95 || stream.Length > MaxBaseline + 128) throw new InvalidDataException();
                    bytes = reader.ReadBytes((int)stream.Length);
                }
            }
            catch (FileNotFoundException) { return null; }

            int dataLength = bytes.Length - DigestSize;
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes, 0, dataLength);
                for (int i = 0; i < DigestSize; i++)
                    if (digest[i] != bytes[dataLength + i]) throw new InvalidDataException();
            }

            using (var reader = new BinaryReader(new MemoryStream(bytes, 0, dataLength, false)))
            {
                if (reader.ReadInt32() != Magic) throw new InvalidDataException();
                int fileSession = reader.ReadInt32();
                long fileLogonId = reader.ReadInt64();
                long fileLogonTime = reader.ReadInt64();
                var record = new RecoveryRecord
                {
                    Id = new Guid(reader.ReadBytes(16)),
                    OwnerPid = reader.ReadInt32(),
                    OwnerStart = reader.ReadInt64(),
                    Revision = reader.ReadInt32(),
                };
                byte timed = reader.ReadByte();
                record.Timed = timed == 1;
                record.Phase = (RecoveryPhase)reader.ReadByte();
                record.Reason = (RecoveryReason)reader.ReadByte();
                int baselineLength = reader.ReadInt32();
                bool terminal = record.Phase == RecoveryPhase.Complete || record.Phase == RecoveryPhase.Aborted;
                if (record.Id == Guid.Empty || record.OwnerPid <= 0 || record.OwnerStart <= 0 || record.Revision < 1 || timed > 1
                    || !Enum.IsDefined(typeof(RecoveryPhase), record.Phase)
                    || !Enum.IsDefined(typeof(RecoveryReason), record.Reason)
                    || baselineLength < 0 || baselineLength > MaxBaseline || terminal != (baselineLength == 0)
                    || reader.BaseStream.Length - reader.BaseStream.Position != baselineLength)
                    throw new InvalidDataException();
                if (fileSession != Session || fileLogonId != logonId || fileLogonTime != logonTime)
                    throw new RecoveryStaleException(record.OwnerPid, record.OwnerStart, terminal);
                record.Baseline = reader.ReadBytes(baselineLength);
                return record;
            }
        }

        internal static bool IsSharingViolation(IOException error)
        {
            return error.HResult == unchecked((int)0x80070020) || error.HResult == unchecked((int)0x80070021);
        }

        internal void Write(RecoveryRecord record)
        {
            if (record.Baseline.Length > MaxBaseline) throw new InvalidDataException();
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data))
            {
                writer.Write(Magic);
                writer.Write(Session);
                writer.Write(logonId);
                writer.Write(logonTime);
                writer.Write(record.Id.ToByteArray());
                writer.Write(record.OwnerPid);
                writer.Write(record.OwnerStart);
                writer.Write(record.Revision = checked(record.Revision + 1));
                writer.Write(record.Timed);
                writer.Write((byte)record.Phase);
                writer.Write((byte)record.Reason);
                writer.Write(record.Baseline.Length);
                writer.Write(record.Baseline);
                writer.Flush();

                byte[] payload = data.ToArray();
                string temp = JournalPath + ".tmp";
                using (var sha = SHA256.Create())
                using (var output = File.Open(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    output.Write(payload, 0, payload.Length);
                    byte[] digest = sha.ComputeHash(payload);
                    output.Write(digest, 0, digest.Length);
                    output.Flush(true);
                }
                if (File.Exists(JournalPath)) File.Replace(temp, JournalPath, null);
                else File.Move(temp, JournalPath);
            }
            Log(record.Phase + "-" + record.Reason);
        }

        internal void RemoveReceipt(RecoveryRecord record, bool onlyIfOwnerExited)
        {
            if (record.Pending) return;
            RemoveReceipt(record.OwnerPid, record.OwnerStart, onlyIfOwnerExited);
        }

        internal void RemoveReceipt(int ownerPid, long ownerStart, bool onlyIfOwnerExited)
        {
            try
            {
                using (var owner = onlyIfOwnerExited ? RecoveryOwner.Open(ownerPid, ownerStart, Session) : null)
                    if (owner == null || owner.Exited) File.Delete(JournalPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal void RemoveInterruptedWrite()
        {
            try { File.Delete(JournalPath + ".tmp"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal void LogRequest(RecoveryRequest request)
        {
            if (request != RecoveryRequest.Unspecified && Enum.IsDefined(typeof(RecoveryRequest), request))
                Log("Requested-" + request);
        }

        internal void LogBrowserCaptureFailure() { Log("BrowserCapture-Unavailable"); }
        internal void LogBrowserRepair(bool verified) { Log(verified ? "BrowserRepair-Verified" : "BrowserRepair-Incomplete"); }

        private void Log(string code)
        {
            try
            {
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 4096)
                    File.WriteAllText(logPath, "");
                File.AppendAllText(logPath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    + " " + code + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
    }

    internal sealed class RecoveryStaleException : IOException
    {
        internal readonly int OwnerPid;
        internal readonly long OwnerStart;
        internal readonly bool Terminal;

        internal RecoveryStaleException(int ownerPid, long ownerStart, bool terminal)
        {
            OwnerPid = ownerPid;
            OwnerStart = ownerStart;
            Terminal = terminal;
        }
    }

    internal sealed class RecoverySignals : IDisposable
    {
        internal EventWaitHandle Ready, Manual, Fence, Mutation, Pulse, Failed;
        internal EventWaitHandle ExactAfterIsolation;
        private RecoverySignals() { }

        internal static RecoverySignals Open(RecoveryStorage storage, Guid id, bool create)
        {
            var result = new RecoverySignals();
            try
            {
                result.Ready = Open(storage, id, "ready", create);
                result.Manual = Open(storage, id, "manual", create);
                result.Fence = Open(storage, id, "fence", create);
                result.Mutation = Open(storage, id, "mutation", create);
                result.Pulse = Open(storage, id, "pulse", create, EventResetMode.AutoReset);
                result.Failed = Open(storage, id, "failed", create);
                try
                {
                    if (create) result.ExactAfterIsolation = Open(storage, id, "exact", true);
                    else EventWaitHandle.TryOpenExisting(storage.EventName(id, "exact"), out result.ExactAfterIsolation);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (WaitHandleCannotBeOpenedException) { }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static EventWaitHandle Open(RecoveryStorage storage, Guid id, string kind, bool create,
            EventResetMode mode = EventResetMode.ManualReset)
        {
            return create ? storage.CreateEvent(id, kind, mode)
                : EventWaitHandle.OpenExisting(storage.EventName(id, kind));
        }

        internal bool Cancelled { get { return Manual.WaitOne(0) || Fence.WaitOne(0); } }

        internal void MarkExactAfterIsolation()
        {
            try { if (ExactAfterIsolation != null) ExactAfterIsolation.Set(); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            foreach (var signal in new[] { Ready, Manual, Fence, Mutation, Pulse, Failed, ExactAfterIsolation })
                if (signal != null) signal.Dispose();
        }
    }

    internal struct RecoveryLogon
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Luid { internal uint Low; internal int High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenStats
        {
            internal Luid TokenId, AuthenticationId;
            internal long Expiration;
            internal int Type, Impersonation;
            internal uint Charged, Available, Groups, Privileges;
            internal Luid Modified;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LsaString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LogonData
        {
            internal uint Size;
            internal Luid Id;
            internal LsaString User, Domain, Package;
            internal uint Type, Session;
            internal IntPtr Sid;
            internal long Time;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr token, int type, out TokenStats stats, int size, out int length);
        [DllImport("secur32.dll")]
        private static extern uint LsaGetLogonSessionData(ref Luid id, out IntPtr data);
        [DllImport("secur32.dll")]
        private static extern uint LsaFreeReturnBuffer(IntPtr data);

        internal long Id, Time;

        internal static RecoveryLogon Read(IntPtr token, int session)
        {
            TokenStats stats;
            int written;
            IntPtr data;
            if (!GetTokenInformation(token, 10, out stats, Marshal.SizeOf(typeof(TokenStats)), out written)
                || LsaGetLogonSessionData(ref stats.AuthenticationId, out data) != 0 || data == IntPtr.Zero)
                throw new IOException("recovery-logon");
            try
            {
                if (Marshal.ReadInt32(data) < Marshal.SizeOf(typeof(LogonData))) throw new IOException("recovery-logon");
                var logon = (LogonData)Marshal.PtrToStructure(data, typeof(LogonData));
                if (logon.Time <= 0 || logon.Session != session) throw new IOException("recovery-logon");
                return new RecoveryLogon { Id = ((long)logon.Id.High << 32) | logon.Id.Low, Time = logon.Time };
            }
            finally { LsaFreeReturnBuffer(data); }
        }
    }

    internal sealed class RecoveryOwner : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeWaitHandle OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeWaitHandle process, out long start, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(SafeWaitHandle process, uint code);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeWaitHandle process, int flags, StringBuilder fileName, ref int size);

        private readonly SafeWaitHandle handle;
        internal readonly long Start;

        private RecoveryOwner(int pid)
        {
            const uint access = 0x00100000 | 0x1000 | 1; // Synchronize, query limited information, terminate.
            handle = OpenProcess(access, false, pid);
            long unused;
            if (handle.IsInvalid || !GetProcessTimes(handle, out Start, out unused, out unused, out unused))
            {
                handle.Dispose();
                throw new IOException("recovery-owner");
            }
        }

        internal static long ReadStart(int pid)
        {
            using (var owner = new RecoveryOwner(pid)) return owner.Start;
        }

        internal static RecoveryOwner Open(RecoveryRecord record, int session)
        {
            return Open(record.OwnerPid, record.OwnerStart, session);
        }

        internal static RecoveryOwner Open(int pid, long start, int session)
        {
            RecoveryOwner owner;
            try
            {
                using (var process = Process.GetProcessById(pid))
                    if (process.SessionId != session) return null;
                owner = new RecoveryOwner(pid);
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (IOException) { return null; }

            if (owner.Start == start) return owner;
            owner.Dispose();
            return null;
        }

        internal bool Exited
        {
            get
            {
                uint result = WaitForSingleObject(handle, 0);
                if (result == 0) return true;
                if (result == 258) return false;
                throw new IOException("recovery-owner-wait");
            }
        }

        internal void Stop()
        {
            if (!MatchesCurrentImage()) throw new IOException("recovery-owner-image");
            if (!Exited && !TerminateProcess(handle, 2) && !Exited) throw new IOException("recovery-owner-stop");
            if (WaitForSingleObject(handle, 3000) != 0) throw new IOException("recovery-owner-wait");
        }

        internal bool MatchesCurrentImage()
        {
            using (var current = Process.GetCurrentProcess())
            using (var ownImage = new RecoveryOwner(current.Id))
                return String.Equals(Image(), ownImage.Image(), StringComparison.OrdinalIgnoreCase);
        }

        private string Image()
        {
            var name = new StringBuilder(32768);
            int count = name.Capacity;
            if (!QueryFullProcessImageName(handle, 0, name, ref count)) throw new IOException("recovery-owner-image");
            return name.ToString();
        }

        public void Dispose() { handle.Dispose(); }
    }
}
