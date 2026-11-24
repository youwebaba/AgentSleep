using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AgentSleep
{
    internal static class RecoveryWorker
    {
        internal const int TrialMilliseconds = 25000;
        internal const int ArmMilliseconds = 5000;
        private const int HeartbeatMilliseconds = 8000;
        private const int GateGrace = 2000;

        internal static bool Enter(Mutex gate, int milliseconds)
        {
            try { return gate.WaitOne(milliseconds); }
            catch (AbandonedMutexException) { return true; }
        }

        private static bool TryReadUnderGate(RecoveryStorage storage, Mutex gate, int milliseconds, out RecoveryRecord record)
        {
            record = null;
            if (!Enter(gate, milliseconds)) return false;
            try { record = storage.Read(); return true; }
            finally { gate.ReleaseMutex(); }
        }

        private static RecoveryRecord ReadAvailableJournal(RecoveryStorage storage)
        {
            try { return storage.Read(); }
            catch (IOException error)
            {
                if (!RecoveryStorage.IsSharingViolation(error)) throw;
                return null;
            }
        }

        internal static int Watch(RecoveryStorage storage, IDisplays displays, Guid id,
            Func<IDisposable> keepSystemAwake, int trialMilliseconds, int heartbeatMilliseconds = HeartbeatMilliseconds)
        {
            using (var signals = RecoverySignals.Open(storage, id, false))
            using (var journalGate = storage.OpenGate())
            {
                try
                {
                    RecoveryRecord record;
                    if (!TryReadUnderGate(storage, journalGate, GateGrace, out record) || record == null ||
                        record.Id != id || record.Phase != RecoveryPhase.Prepared) return 2;
                    using (var owner = RecoveryOwner.Open(record, storage.Session))
                    {
                        if (owner == null || owner.Exited) return Restore(storage, displays, id, RecoveryReason.OwnerExit, signals);
                        if (!owner.MatchesCurrentImage()) return 2;
                        using (var wakeRequest = keepSystemAwake())
                        {
                            if (wakeRequest == null) throw new IOException("watchdog-power-request");
                            var clock = Stopwatch.StartNew();
                            bool isolated = false;
                            long pulse = 0;
                            signals.Ready.Set();

                            while (true)
                            {
                                // Confirm an unavailable read after publication. A writer cannot stop owner or deadline checks.
                                RecoveryRecord updated = ReadAvailableJournal(storage);
                                if (updated != null || TryReadUnderGate(storage, journalGate, 0, out updated)) record = updated;
                                if (record == null || record.Id != id || !record.Pending) return 0;
                                if (record.Phase == RecoveryPhase.Failed) return 2;
                                if (record.Phase == RecoveryPhase.Fallback) return 3;
                                if (signals.Manual.WaitOne(0))
                                    return Restore(storage, displays, id, RecoveryReason.Manual, signals, owner);
                                if (owner.Exited)
                                    return Restore(storage, displays, id, RecoveryReason.OwnerExit, signals, owner);

                                long now = clock.ElapsedMilliseconds;
                                if (record.Phase == RecoveryPhase.Isolated && !isolated)
                                {
                                    isolated = true;
                                    pulse = now;
                                }
                                if ((record.Timed || !isolated) && now >= trialMilliseconds)
                                    return Restore(storage, displays, id, RecoveryReason.Deadline, signals, owner);
                                if (isolated && now - pulse >= heartbeatMilliseconds)
                                    return Restore(storage, displays, id, RecoveryReason.OwnerHang, signals, owner);
                                if (signals.Pulse.WaitOne(60)) pulse = clock.ElapsedMilliseconds;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    signals.Failed.Set();
                    return 2;
                }
            }
        }

        internal static int RestoreCurrent(RecoveryStorage storage, IDisplays displays)
        {
            RecoveryRecord record = ReadAvailableJournal(storage);
            if (record == null)
            {
                using (var gate = storage.OpenGate())
                {
                    if (!TryReadUnderGate(storage, gate, GateGrace, out record))
                    {
                        record = storage.Read();
                        if (record == null) return 2;
                    }
                }
            }
            return record == null || !record.Pending ? 0 : Restore(storage, displays, record.Id, RecoveryReason.Manual,
                retryRevision: record.Revision, request: RecoveryRequest.StandaloneRestore);
        }

        internal static int Restore(RecoveryStorage storage, IDisplays displays, Guid id, RecoveryReason reason,
            RecoverySignals signals = null, RecoveryOwner owner = null, int retryRevision = 0,
            RecoveryRequest request = RecoveryRequest.Unspecified)
        {
            bool ownSignals = signals == null;
            bool ownOwner = owner == null;
            try
            {
                if (signals == null)
                {
                    try { signals = RecoverySignals.Open(storage, id, false); }
                    catch (WaitHandleCannotBeOpenedException) { }
                }
                bool logRequest = request != RecoveryRequest.Unspecified;
                if (logRequest && (signals == null || !signals.Manual.WaitOne(0)))
                {
                    storage.LogRequest(request);
                    logRequest = false;
                }
                if (signals != null)
                {
                    if (reason == RecoveryReason.Manual) signals.Manual.Set();
                    signals.Fence.Set();
                }
                using (var gate = storage.OpenGate())
                {
                    if (!Enter(gate, GateGrace))
                    {
                        // Only terminate the recorded app when its display operation holds the gate.
                        RecoveryRecord pending = storage.Read();
                        if (pending == null || pending.Id != id || !pending.Pending) return 0;
                        if (signals == null || !signals.Mutation.WaitOne(0)) return 2;
                        if (owner == null) owner = RecoveryOwner.Open(pending, storage.Session);
                        if (owner != null) owner.Stop();
                        if (!Enter(gate, GateGrace)) return 2;
                    }

                    try
                    {
                        RecoveryRecord record = storage.Read();
                        if (record == null || record.Id != id || !record.Pending) return 0;
                        if (record.Revision != retryRevision)
                        {
                            if (record.Phase == RecoveryPhase.Failed) return 2;
                            if (record.Phase == RecoveryPhase.Fallback) return 3;
                        }
                        else if (logRequest && (record.Phase == RecoveryPhase.Failed || record.Phase == RecoveryPhase.Fallback))
                            storage.LogRequest(request);
                        if (signals != null && signals.Manual.WaitOne(0)) reason = RecoveryReason.Manual;
                        if (record.Reason == RecoveryReason.Activation) reason = RecoveryReason.Activation;
                        if (reason == RecoveryReason.Deadline &&
                            (record.Phase != RecoveryPhase.Isolated || !record.Timed))
                            reason = RecoveryReason.OwnerHang;
                        record.Reason = reason;

                        if (record.Phase == RecoveryPhase.Prepared)
                        {
                            record.Phase = RecoveryPhase.Aborted;
                            record.Baseline = new byte[0];
                            storage.Write(record);
                            storage.RemoveReceipt(record, true);
                            return 0;
                        }

                        record.Phase = RecoveryPhase.Restoring;
                        storage.Write(record);
                        try
                        {
                            string result = displays.Restore(record.Baseline);
                            if (result == "physical-fallback")
                            {
                                record.Phase = RecoveryPhase.Fallback;
                                storage.Write(record);
                                return 3;
                            }
                            if (result != "exact") throw new InvalidDataException();
                        }
                        catch (Exception)
                        {
                            record.Phase = RecoveryPhase.Failed;
                            storage.Write(record);
                            return 2;
                        }

                        record.Phase = RecoveryPhase.Complete;
                        record.Baseline = new byte[0];
                        storage.Write(record);
                        storage.RemoveReceipt(record, true);
                        return 0;
                    }
                    finally { gate.ReleaseMutex(); }
                }
            }
            finally
            {
                if (ownOwner && owner != null) owner.Dispose();
                if (ownSignals && signals != null) signals.Dispose();
            }
        }
    }

    internal sealed class RecoveryController : IDisposable
    {
        private readonly IDisplays displays;
        private readonly RecoveryStorage storage;
        private readonly Func<Guid, Process> launch;
        private readonly int armMilliseconds;
        private Guid id;
        private RecoverySignals signals;
        private Process helper;
        private int queued;
        private int queueFinished;
        private const int QueuedSuccess = 1;
        private bool triedRecovery;
        private bool ownsRecord;
        private bool disposed;

        internal string State { get; private set; }
        internal string Detail { get; private set; }
        internal bool Pending { get; private set; }
        internal bool OwnsRecord { get { return ownsRecord; } }
        internal bool QueuedRestorePending { get { return Volatile.Read(ref queued) != 0; } }

        internal RecoveryController(IDisplays displays)
            : this(displays, OpenStorage(), RecoveryCommands.Launch, RecoveryWorker.ArmMilliseconds) { }

        internal RecoveryController(IDisplays displays, RecoveryStorage storage, Func<Guid, Process> launch, int armMilliseconds)
        {
            this.displays = displays;
            this.storage = storage;
            this.launch = launch;
            this.armMilliseconds = armMilliseconds;
            State = "idle";
            Detail = "ready";
            if (storage == null)
            {
                Set("failed", "recovery-storage-unavailable");
                return;
            }
            try
            {
                CleanupReceipt(Guid.Empty, true);
                RecoveryRecord record = storage.Read();
                if (record == null) return;
                if (!record.Pending) return;
                id = record.Id;
                Update(record);
                if (record.Phase != RecoveryPhase.Failed && record.Phase != RecoveryPhase.Fallback)
                {
                    using (var owner = RecoveryOwner.Open(record, storage.Session))
                        if (owner == null || owner.Exited) QueueRestore(RecoveryReason.OwnerExit);
                }
            }
            catch (RecoveryStaleException)
            {
                Set("idle", "prior-logon-snapshot");
            }
            catch (Exception) { Set("failed", "recovery-journal-unreadable"); }
        }

        private static RecoveryStorage OpenStorage()
        {
            try { return RecoveryStorage.Open(); }
            catch (Exception) { return null; }
        }

        internal void Start(string targetId, bool timedTrial)
        {
            if (disposed || storage == null || QueuedRestorePending || Pending) return;
            if (String.IsNullOrWhiteSpace(targetId))
            {
                Set("failed", "target-required");
                return;
            }

            Set("arming", "validating-target");
            bool recorded = false;
            bool armed = false;
            try
            {
                using (var gate = storage.OpenGate())
                {
                    if (!RecoveryWorker.Enter(gate, 0))
                    {
                        Set("failed", "recovery-busy");
                        return;
                    }
                    try
                    {
                        RecoveryRecord previous;
                        try { previous = storage.Read(); }
                        catch (RecoveryStaleException stale)
                        {
                            using (var owner = stale.Terminal ? null : RecoveryOwner.Open(stale.OwnerPid, stale.OwnerStart, storage.Session))
                            {
                                if (owner != null && !owner.Exited)
                                {
                                    Set("failed", "other-logon-recovery-pending");
                                    return;
                                }
                            }
                            previous = null;
                        }
                        if (previous != null && previous.Pending)
                        {
                            CloseHandles();
                            ownsRecord = false;
                            id = previous.Id;
                            Update(previous);
                            return;
                        }

                        CloseHandles();
                        if (ownsRecord && id != Guid.Empty && previous != null && previous.Id == id)
                            storage.RemoveReceipt(previous, false);
                        ownsRecord = false;
                        id = Guid.Empty;
                        byte[] baseline = displays.Capture(targetId);
                        if (baseline == null || baseline.Length == 0) throw new IOException();
                        displays.Validate(baseline, targetId);
                        id = Guid.NewGuid();
                        signals = RecoverySignals.Open(storage, id, true);
                        using (var process = Process.GetCurrentProcess())
                        {
                            var prepared = new RecoveryRecord
                            {
                                Id = id,
                                OwnerPid = process.Id,
                                OwnerStart = RecoveryOwner.ReadStart(process.Id),
                                Timed = timedTrial,
                                Phase = RecoveryPhase.Prepared,
                                Reason = RecoveryReason.None,
                                Baseline = baseline
                            };
                            storage.Write(prepared);
                            recorded = true;
                            ownsRecord = true;
                            if (storage.Read().Id != id) throw new IOException();
                        }
                        Pending = true;
                        triedRecovery = false;
                        Interlocked.Exchange(ref queueFinished, 0);
                    }
                    finally { gate.ReleaseMutex(); }
                }

                Set("arming", "arming-watchdog");
                helper = launch(id);
                if (helper == null || WaitHandle.WaitAny(new WaitHandle[] { signals.Ready, signals.Failed }, armMilliseconds) != 0
                    || helper.HasExited)
                    throw new IOException();
                armed = true;
                ApplyStep(RecoveryPhase.Prepared, RecoveryPhase.Extending,
                    record => displays.Extend(record.Baseline, targetId), false);
                ApplyStep(RecoveryPhase.Extending, RecoveryPhase.Isolating, record =>
                {
                    displays.Isolate(targetId);
                    if (!displays.IsOnly(targetId)) throw new IOException();
                }, true);
                Poll();
            }
            catch (Exception)
            {
                if (!recorded)
                {
                    CloseHandles();
                    Set("failed", "target-validation-failed");
                    return;
                }

                Pending = true;
                if (!armed)
                {
                    Set("failed", "watchdog-arm-failed");
                    QueueRestore(RecoveryReason.Arm);
                }
                else
                {
                    Set("restoring", "activation-failed");
                    MarkActivationFailed();
                    Restore();
                }
            }
        }

        private void ApplyStep(RecoveryPhase expected, RecoveryPhase next, Action<RecoveryRecord> apply, bool complete)
        {
            using (var gate = storage.OpenGate())
            {
                if (!RecoveryWorker.Enter(gate, 200)) throw new IOException();
                try
                {
                    signals.Mutation.Set();
                    RecoveryRecord record = storage.Read();
                    if (record == null || record.Id != id || record.Phase != expected
                        || signals.Cancelled || helper.HasExited) throw new IOException();
                    record.Phase = next;
                    storage.Write(record);
                    if (signals.Cancelled || helper.HasExited) throw new IOException();
                    apply(record);
                    if (complete && !signals.Cancelled)
                    {
                        record.Phase = RecoveryPhase.Isolated;
                        storage.Write(record);
                    }
                }
                finally
                {
                    try { signals.Mutation.Reset(); }
                    finally { gate.ReleaseMutex(); }
                }
            }
        }

        private void MarkActivationFailed()
        {
            try
            {
                using (var gate = storage.OpenGate())
                {
                    if (!RecoveryWorker.Enter(gate, 0)) return;
                    try
                    {
                        RecoveryRecord record = storage.Read();
                        if (record != null && record.Id == id && record.Pending && !signals.Cancelled)
                        {
                            record.Reason = RecoveryReason.Activation;
                            storage.Write(record);
                        }
                    }
                    finally { gate.ReleaseMutex(); }
                }
            }
            catch (Exception) { }
        }

        internal void Restore(RecoveryRequest request = RecoveryRequest.Unspecified)
        {
            if (storage == null || disposed) return;
            bool hasActiveHelper = State != "failed" && State != "fallback" &&
                helper != null && !helper.HasExited && signals != null && !signals.Failed.WaitOne(0);
            if (Pending)
            {
                bool retry = State == "failed" || State == "fallback";
                if (retry || (signals == null ? !QueuedRestorePending : !signals.Manual.WaitOne(0)))
                    storage.LogRequest(request);
                if (signals != null) signals.Manual.Set();
                int retryRevision = 0;
                if (retry)
                {
                    try
                    {
                        RecoveryRecord record = storage.Read();
                        if (record != null && record.Id == id) retryRevision = record.Revision;
                    }
                    catch (Exception) { }
                }
                Set("restoring", "restore-requested");
                if (!hasActiveHelper) QueueRestore(RecoveryReason.Manual, retryRevision);
            }
        }

        internal void Poll()
        {
            if (storage == null || disposed) return;
            try
            {
                RecoveryRecord record = storage.Read();
                if ((record == null || record.Id != id) && Pending && Volatile.Read(ref queued) == 0
                    && Volatile.Read(ref queueFinished) == QueuedSuccess)
                {
                    Pending = false;
                    Set("restored", "restore-exact");
                    CloseHandles();
                }
                if (id == Guid.Empty || record == null || record.Id != id) return;

                if (signals != null && record.Phase == RecoveryPhase.Isolated) signals.Pulse.Set();
                if (record.Pending && signals != null && (signals.Failed.WaitOne(0) || (helper != null && helper.HasExited))
                    && !triedRecovery && record.Phase != RecoveryPhase.Failed && record.Phase != RecoveryPhase.Fallback)
                    QueueRestore(RecoveryReason.WatchdogUnavailable);

                Update(record);
                if (record.Pending && Volatile.Read(ref queued) == 0 && Volatile.Read(ref queueFinished) == 2
                    && record.Phase != RecoveryPhase.Failed && record.Phase != RecoveryPhase.Fallback)
                    Set("failed", "restore-incomplete");
                if (!record.Pending) CloseHandles();
            }
            catch (RecoveryStaleException) { Set("failed", "prior-logon-snapshot"); }
            catch (IOException error)
            {
                if (!RecoveryStorage.IsSharingViolation(error)) Set("failed", "recovery-journal-unreadable");
            }
            catch (Exception) { Set("failed", "recovery-journal-unreadable"); }
        }

        private void QueueRestore(RecoveryReason reason, int retryRevision = 0)
        {
            if (Interlocked.Exchange(ref queued, 1) != 0) return;
            triedRecovery = true;
            Guid current = id;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int outcome = 2;
                try { outcome = RecoveryWorker.Restore(storage, displays, current, reason, retryRevision: retryRevision); }
                catch (Exception) { }
                finally
                {
                    Interlocked.Exchange(ref queueFinished, outcome == 0 ? QueuedSuccess : outcome);
                    Interlocked.Exchange(ref queued, 0);
                }
            });
        }

        private void Update(RecoveryRecord record)
        {
            Pending = record.Pending;
            switch (record.Phase)
            {
                case RecoveryPhase.Prepared:
                case RecoveryPhase.Extending:
                case RecoveryPhase.Isolating:
                    if (signals != null && signals.Cancelled) Set("restoring", "restore-requested");
                    else Set("arming", "entering-virtual-only");
                    break;
                case RecoveryPhase.Isolated:
                    if (signals != null && signals.Cancelled) Set("restoring", "restore-requested");
                    else Set(record.Timed ? "trial" : "active", "virtual-only-verified");
                    break;
                case RecoveryPhase.Restoring:
                    Set("restoring", "restoring-display"); break;
                case RecoveryPhase.Failed:
                    Set("failed", "restore-failed"); break;
                case RecoveryPhase.Fallback:
                    Set("fallback", "physical-fallback"); break;
                case RecoveryPhase.Aborted:
                    if (record.Reason == RecoveryReason.Arm) Set("failed", "watchdog-arm-failed");
                    else if (record.Reason == RecoveryReason.Activation) Set("failed", "activation-failed");
                    else Set("restored", "stopped-before-display-change");
                    break;
                case RecoveryPhase.Complete:
                    if (record.Reason == RecoveryReason.Activation) Set("failed", "activation-failed-restored-exact");
                    else Set("restored", record.Reason == RecoveryReason.Deadline ? "timed-restore-exact" : "restore-exact");
                    break;
            }
        }

        private void Set(string state, string detail)
        {
            State = state;
            Detail = detail;
        }

        private void CloseHandles()
        {
            if (signals != null) signals.Dispose();
            if (helper != null) helper.Dispose();
            signals = null;
            helper = null;
        }

        private void CleanupReceipt(Guid expectedId, bool onlyIfOwnerExited)
        {
            try
            {
                using (var gate = storage.OpenGate())
                {
                    if (!RecoveryWorker.Enter(gate, 0)) return;
                    try
                    {
                        storage.RemoveInterruptedWrite();
                        RecoveryRecord record = storage.Read();
                        if (record != null && (expectedId == Guid.Empty || expectedId == record.Id))
                            storage.RemoveReceipt(record, onlyIfOwnerExited);
                    }
                    catch (RecoveryStaleException stale)
                    {
                        if (expectedId == Guid.Empty && stale.Terminal)
                            storage.RemoveReceipt(stale.OwnerPid, stale.OwnerStart, true);
                    }
                    finally { gate.ReleaseMutex(); }
                }
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            if (Pending && ownsRecord) Restore(RecoveryRequest.TrayExit);
            if (ownsRecord && id != Guid.Empty) CleanupReceipt(id, false);
            CloseHandles();
            disposed = true;
        }
    }

    internal static class RecoveryCommands
    {
        private sealed class WatchdogPower : IDisposable
        {
            private const uint Continuous = 0x80000000;
            private const uint SystemRequired = 0x00000001;

            [DllImport("kernel32.dll")]
            private static extern uint SetThreadExecutionState(uint flags);

            internal static IDisposable Acquire()
            {
                return SetThreadExecutionState(Continuous | SystemRequired) == 0 ? null : new WatchdogPower();
            }

            public void Dispose() { SetThreadExecutionState(Continuous); }
        }

        internal static Process Launch(Guid id)
        {
            using (var process = Process.GetCurrentProcess())
                return Process.Start(new ProcessStartInfo(process.MainModule.FileName, "--watchdog " + id.ToString("N"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
        }

        internal static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 2;
            if (args.Length == 0 || (args[0] != "--watchdog" && args[0] != "--restore")) return false;
            if (args[0] == "--restore")
            {
                if (args.Length != 1) return true;
                try { exitCode = RecoveryWorker.RestoreCurrent(RecoveryStorage.Open(), new NativeDisplays()); }
                catch (Exception) { }
                return true;
            }

            Guid id;
            if (args.Length == 2 && Guid.TryParseExact(args[1], "N", out id))
            {
                try
                {
                    exitCode = RecoveryWorker.Watch(RecoveryStorage.Open(), new NativeDisplays(), id,
                        WatchdogPower.Acquire, RecoveryWorker.TrialMilliseconds);
                }
                catch (Exception) { }
            }
            return true;
        }
    }
}
