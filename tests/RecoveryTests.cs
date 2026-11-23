using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace AgentSleep
{
    internal static class RecoveryTests
    {
        private const int TestTrial = 1500;
        private const string Target = "known-virtual";

        internal static void Run()
        {
            using (var process = Process.GetCurrentProcess())
            using (var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
                Check(RecoveryLogon.Read(identity.Token, process.SessionId).Time > 0, "logon binding");

            Case("prerequisites and arm failure", (fixture, storage, displays) =>
            {
                string unfinished = storage.JournalPath + ".tmp";
                File.WriteAllText(unfinished, "incomplete baseline");
                using (var controller = fixture.Controller(armWatchdog: false))
                {
                    Check(!File.Exists(unfinished), "interrupted write remained");
                    controller.Start("", true);
                    Check(controller.Detail == "target-required" && displays.History == "", "blank target");
                    controller.Start("ambiguous", true);
                    Check(controller.State == "failed" && displays.History == "capture,validate", "ambiguous target");
                    displays.Set("missing");
                    controller.Start(Target, true);
                    Check(!controller.Pending, "missing target remained pending");
                    displays.Clear("missing");
                    controller.Start(Target, true);
                    Until(() => Poll(controller, "watchdog-arm-failed") && !controller.Pending, "arm failure");
                    Check(storage.Read().Phase == RecoveryPhase.Aborted && !displays.WasChanged, "arm failure mutated");
                }
            });

            Case("timed restore and concurrent entrant", (fixture, storage, displays) =>
            {
                displays.Set("require-wake");
                using (var controller = fixture.Controller())
                {
                    controller.Start(Target, true);
                    Check(controller.State == "trial", controller.Detail);
                    using (var competing = fixture.Controller())
                    {
                        competing.Start(Target, true);
                        Check(competing.Pending, "competing recovery did not adopt live snapshot");
                    }
                    Until(() => Poll(controller, "timed-restore-exact"), "timed exact");
                    Until(() => displays.Has("wake-released"), "watchdog released the system request");
                    Check(displays.History == "capture,validate,extend,isolate,readback,restore", displays.History);
                    Check(!storage.Read().Pending && displays.Physical, "exact restore did not compact snapshot");
                    Check(RecoveryWorker.RestoreCurrent(storage, displays) == 0 && displays.RestoreCount == 1, "repeated restore");
                }
                Check(storage.Read() == null, "consumed trial receipt remained");
            });

            Case("watchdog refuses to arm without system request", (fixture, storage, displays) =>
            {
                displays.Set("wake-denied");
                using (var controller = fixture.Controller())
                {
                    controller.Start(Target, true);
                    Until(() => Poll(controller, "watchdog-arm-failed") && !controller.Pending, "missing system request");
                    Check(displays.Has("wake-attempted") && !displays.Has("wake-acquired") && !displays.WasChanged,
                        "denied power request changed the topology");
                }
            });

            Case("manual restore", (fixture, storage, displays) =>
            {
                using (var controller = fixture.Controller())
                {
                    controller.Start(Target, true);
                    Check(controller.State == "trial", controller.Detail);
                    controller.Restore();
                    Check(RecoveryWorker.RestoreCurrent(storage, displays) == 0, "manual restore exit");
                    Until(() => Poll(controller, "restore-exact"), "manual exact");
                    Check(storage.Read().Reason == RecoveryReason.Manual && displays.RestoreCount == 1, "manual race");
                }
            });

            Case("restore fences the next mutation", (fixture, storage, displays) =>
            {
                Process owner = fixture.StartOwner("fence");
                Until(() => displays.Has("owner-paused"), "owner paused during extension");
                using (var signals = RecoverySignals.Open(storage, storage.Read().Id, false))
                {
                    Task<int> manual = fixture.StartManualRestore();
                    Check(signals.Manual.WaitOne(1000), "standalone restore did not request cancellation");
                    displays.Set("release-extend");
                    Check(manual.Wait(5000) && manual.Result == 0, "standalone restore did not finish");
                }
                Check(owner.WaitForExit(5000) && owner.ExitCode == 0, "owner did not stop after cancellation");
                Check(displays.History == "capture,validate,extend,restore" && displays.Physical, "late isolate after recovery");
            });

            Case("activation failure with rollback", (fixture, storage, displays) =>
            {
                using (var controller = fixture.Controller())
                {
                    displays.Set("bad-readback");
                    displays.Set("hold-wake-release");
                    controller.Start(Target, false);
                    Until(() => Poll(controller, "activation-failed-restored-exact"), "activation failure");
                    Check(controller.State == "failed" && displays.Physical && !controller.Pending, "activation state");
                    Until(() => displays.Has("wake-release-started"), "watchdog reached power release after publishing terminal status");
                    Check(!displays.Has("wake-released"), "watchdog released before the fixture allowed it");
                }
            });

            Case("fallback and explicit retry", (fixture, storage, displays) =>
            {
                using (var controller = fixture.Controller())
                {
                    displays.Set("fallback");
                    controller.Start(Target, false);
                    Check(controller.State == "active", controller.Detail);
                    controller.Restore();
                    Until(() => Poll(controller, "physical-fallback"), "physical fallback");
                    Check(controller.Pending && displays.RestoreCount == 1 && displays.Physical, "fallback snapshot");
                    displays.Clear("fallback");
                    controller.Restore();
                    Until(() => Poll(controller, "restore-exact"), "retry exact");
                    Check(!storage.Read().Pending && displays.RestoreCount == 2, "fallback retry");
                }
            });

            Case("failed rollback keeps snapshot", (fixture, storage, displays) =>
            {
                using (var controller = fixture.Controller())
                {
                    displays.Set("restore-fails");
                    controller.Start(Target, false);
                    Check(controller.State == "active", controller.Detail);
                    controller.Restore();
                    Until(() => Poll(controller, "restore-failed"), "failed restore");
                    Check(controller.Pending && storage.Read().Pending && !displays.Physical, "failed recovery snapshot");
                    displays.Clear("restore-fails");
                    controller.Restore();
                    Until(() => Poll(controller, "restore-exact"), "failed retry exact");
                }
            });

            Case("prior logon and corrupt snapshot", (fixture, storage, displays) =>
            {
                using (var process = Process.GetCurrentProcess())
                    storage.Write(new RecoveryRecord
                    {
                        Id = Guid.NewGuid(), OwnerPid = process.Id, OwnerStart = RecoveryOwner.ReadStart(process.Id),
                        Phase = RecoveryPhase.Complete, Baseline = new byte[0]
                    });
                var other = RecoveryStorage.ForTest(fixture.Folder, fixture.Scope, 2);
                try
                {
                    RecoveryWorker.RestoreCurrent(other, displays);
                    throw new InvalidOperationException("prior logon replay allowed");
                }
                catch (RecoveryStaleException) { }

                using (var controller = fixture.Controller(other, armWatchdog: false))
                {
                    Check(controller.Detail == "prior-logon-snapshot", "stale state");
                    controller.Start(Target, true);
                    Until(() => Poll(controller, "watchdog-arm-failed") && !controller.Pending, "stale new trial");
                    Check(!displays.WasChanged, "stale topology applied");
                }

                using (var process = Process.GetCurrentProcess())
                    storage.Write(new RecoveryRecord
                    {
                        Id = Guid.NewGuid(), OwnerPid = process.Id, OwnerStart = RecoveryOwner.ReadStart(process.Id),
                        Phase = RecoveryPhase.Isolated, Baseline = new byte[] { 42 }
                    });
                byte[] corrupt = File.ReadAllBytes(storage.JournalPath);
                corrupt[10] ^= 1;
                File.WriteAllBytes(storage.JournalPath, corrupt);
                try
                {
                    RecoveryWorker.RestoreCurrent(storage, displays);
                    throw new InvalidOperationException("corrupt replay allowed");
                }
                catch (InvalidDataException) { }
            });

            Case("independent restore after parent loss", (fixture, storage, displays) =>
            {
                Process owner = fixture.StartOwner("crash");
                Until(() => displays.Has("owner-isolated"), "parent reached isolation");
                StopChild(owner);
                Until(() => Complete(storage, RecoveryReason.OwnerExit), "parent exit restore");
                Check(displays.RestoreCount == 1 && displays.Physical, "parent exit topology");
            });

            Case("independent restore after owner hang", (fixture, storage, displays) =>
            {
                Process owner = fixture.StartOwner("hang");
                Until(() => displays.Has("owner-hung"), "parent began native-call simulation");
                Check(owner.WaitForExit(8000) && owner.ExitCode == 2, "hung mutation owner was not fenced");
                Until(() => Complete(storage, RecoveryReason.OwnerHang), "hung parent restore");
                Check(displays.RestoreCount == 1 && displays.Physical, "hung mutation topology");
            });

            Case("independent restore when heartbeat stops", (fixture, storage, displays) =>
            {
                Process owner = fixture.StartOwner("heartbeat");
                Until(() => displays.Has("owner-isolated"), "owner went unresponsive after isolation");
                Until(() =>
                {
                    RecoveryRecord record = storage.Read();
                    return record != null && record.Phase == RecoveryPhase.Complete && record.Reason == RecoveryReason.OwnerHang;
                }, "heartbeat recovery");
                Check(!owner.HasExited && displays.RestoreCount == 1 && displays.Physical, "heartbeat recovery stopped an idle owner");
            });
        }

        internal static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 2;
            if (args.Length == 0 || (args[0] != "--recover-test-watch" && args[0] != "--recover-test-owner"))
                return false;
            Guid scope;
            if (args.Length < 4 || !Guid.TryParseExact(args[2], "N", out scope)) return true;

            var storage = RecoveryStorage.ForTest(args[1], scope, 1);
            var displays = new FakeDisplays(args[1]);
            if (args[0] == "--recover-test-watch")
            {
                Guid id;
                if (args.Length == 4 && Guid.TryParseExact(args[3], "N", out id))
                    exitCode = RecoveryWorker.Watch(storage, displays, id, () => FakeWakeRequest.Acquire(storage, id, displays), TestTrial, 1000);
                return true;
            }

            if (args[3] == "hang") displays.Set("hang-extend");
            if (args[3] == "fence") displays.Set("pause-extend");
            string handoff = Path.Combine(args[1], "watchdog-pid");
            using (var controller = new RecoveryController(displays, storage, id =>
            {
                Until(() => File.Exists(handoff), "test driver published the watchdog process");
                return Process.GetProcessById(Int32.Parse(File.ReadAllText(handoff), CultureInfo.InvariantCulture));
            }, 5000))
            {
                controller.Start(Target, false);
                if (args[3] == "fence")
                {
                    Check(controller.State != "active", "isolated after standalone restore");
                    exitCode = 0;
                    return true;
                }
                Check(controller.State == "active", controller.Detail);
                displays.Set("owner-isolated");
                if (args[3] == "heartbeat") Thread.Sleep(Timeout.Infinite);
                while (true)
                {
                    controller.Poll();
                    Thread.Sleep(30);
                }
            }
        }

        private static Process Launch(string command, string path, Guid scope, string argument)
        {
            using (var process = Process.GetCurrentProcess())
                return Process.Start(new ProcessStartInfo(process.MainModule.FileName,
                    command + " \"" + path + "\" " + scope.ToString("N") + " " + argument)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
        }

        private static bool Complete(RecoveryStorage storage, RecoveryReason reason)
        {
            return storage.Read() == null && File.ReadAllText(Path.ChangeExtension(storage.JournalPath, ".log"))
                .Contains("Complete-" + reason);
        }

        private static void StopChild(Process owner)
        {
            try { if (!owner.HasExited) owner.Kill(); }
            catch (InvalidOperationException) { if (!owner.HasExited) throw; }
            catch (Win32Exception) { if (!owner.HasExited) throw; }
            Check(owner.WaitForExit(5000), "test owner did not exit");
        }

        private static bool Poll(RecoveryController controller, string detail)
        {
            controller.Poll();
            return controller.Detail == detail;
        }

        private static void Case(string name, Action<Fixture, RecoveryStorage, FakeDisplays> test)
        {
            var fixture = new Fixture();
            try
            {
                test(fixture, fixture.Storage, fixture.Displays);
            }
            catch
            {
                try { fixture.Cleanup(); }
                catch (Exception error) { Console.Error.WriteLine("Recovery fixture cleanup also failed: " + error.Message); }
                throw;
            }
            fixture.Cleanup();
            Console.WriteLine("PASS recovery: " + name);
        }

        private static void Until(Func<bool> complete, string detail)
        {
            var clock = Stopwatch.StartNew();
            while (!complete())
            {
                Check(clock.ElapsedMilliseconds < 8000, "timeout: " + detail);
                Thread.Sleep(30);
            }
        }

        private static void Check(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }

        private sealed class Fixture
        {
            internal readonly Guid Scope = Guid.NewGuid();
            internal readonly string Folder;
            internal readonly RecoveryStorage Storage;
            internal readonly FakeDisplays Displays;
            private readonly List<Process> owners = new List<Process>();
            private readonly List<Process> watchdogs = new List<Process>();
            private readonly List<RecoveryController> controllers = new List<RecoveryController>();
            private readonly List<Task<int>> restores = new List<Task<int>>();

            internal Fixture()
            {
                Folder = Path.Combine(Path.GetTempPath(), "AgentSleepRecoveryTests-" + Scope.ToString("N"));
                Storage = RecoveryStorage.ForTest(Folder, Scope, 1);
                Displays = new FakeDisplays(Folder);
            }

            internal RecoveryController Controller(RecoveryStorage storage = null, bool armWatchdog = true)
            {
                var controller = new RecoveryController(Displays, storage ?? Storage,
                    id => armWatchdog ? Process.GetProcessById(StartWatchdog(id).Id) : null,
                    armWatchdog ? 5000 : 30);
                controllers.Add(controller);
                return controller;
            }

            private Process StartWatchdog(Guid id)
            {
                Process watchdog = Launch("--recover-test-watch", Folder, Scope, id.ToString("N"));
                watchdogs.Add(watchdog);
                return watchdog;
            }

            internal Process StartOwner(string mode)
            {
                Process owner = Launch("--recover-test-owner", Folder, Scope, mode);
                owners.Add(owner);
                RecoveryRecord record = null;
                Until(() =>
                {
                    record = Storage.Read();
                    return record != null && record.Phase == RecoveryPhase.Prepared;
                }, "owner prepared the fixture snapshot");

                // The driver retains the watchdog handle even if the synthetic owner is terminated.
                Process watchdog = StartWatchdog(record.Id);
                string handoff = Path.Combine(Folder, "watchdog-pid");
                File.WriteAllText(handoff + ".tmp", watchdog.Id.ToString(CultureInfo.InvariantCulture));
                File.Move(handoff + ".tmp", handoff);
                return owner;
            }

            internal Task<int> StartManualRestore()
            {
                Task<int> restore = Task.Factory.StartNew(() => RecoveryWorker.RestoreCurrent(Storage, Displays));
                restores.Add(restore);
                return restore;
            }

            internal void Cleanup()
            {
                bool watchRelease = Displays.Has("hold-wake-release");
                if (watchRelease) Displays.Set("allow-wake-release");

                try
                {
                    foreach (var controller in controllers) controller.Dispose();
                    foreach (var owner in owners) StopChild(owner);
                    foreach (var watchdog in watchdogs)
                        Check(watchdog.WaitForExit(8000), "test watchdog did not exit");
                    foreach (var controller in controllers)
                        Until(() => !controller.QueuedRestorePending, "queued controller restore stopped writing");
                    foreach (var restore in restores)
                        Until(() => restore.IsCompleted, "standalone restore stopped writing");
                    if (watchRelease) Check(Displays.Has("wake-released"), "watchdog did not finish its final fixture write");
                }
                finally
                {
                    foreach (var owner in owners) owner.Dispose();
                    foreach (var watchdog in watchdogs) watchdog.Dispose();
                }

                Directory.Delete(Folder, true);
            }
        }

        private sealed class FakeWakeRequest : IDisposable
        {
            private readonly FakeDisplays displays;

            private FakeWakeRequest(FakeDisplays displays) { this.displays = displays; }

            internal static IDisposable Acquire(RecoveryStorage storage, Guid id, FakeDisplays displays)
            {
                using (var signals = RecoverySignals.Open(storage, id, false))
                    Check(!signals.Ready.WaitOne(0), "watchdog acknowledged before acquiring its wake request");
                displays.Set("wake-attempted");
                if (displays.Has("wake-denied")) return null;
                displays.Set("wake-acquired");
                return new FakeWakeRequest(displays);
            }

            public void Dispose()
            {
                if (displays.Has("hold-wake-release"))
                {
                    displays.Set("wake-release-started");
                    Until(() => displays.Has("allow-wake-release"), "fixture allowed late power release write");
                }
                displays.Set("wake-released");
            }
        }

        private sealed class FakeDisplays : IDisplays
        {
            private readonly string path;
            internal FakeDisplays(string path) { this.path = path; }
            private string FilePath(string name) { return Path.Combine(path, name); }
            internal bool Has(string name) { return File.Exists(FilePath(name)); }
            internal void Set(string name) { File.WriteAllText(FilePath(name), ""); }
            internal void Clear(string name) { File.Delete(FilePath(name)); }
            internal bool Physical { get { return File.ReadAllText(FilePath("topology")) == "physical"; } }
            internal string History { get { return Has("history") ? String.Join(",", File.ReadAllLines(FilePath("history"))) : ""; } }
            internal bool WasChanged { get { return History.Contains("extend") || History.Contains("restore"); } }
            internal int RestoreCount { get { return History.Split(new[] { "restore" }, StringSplitOptions.None).Length - 1; } }

            private void Record(string action) { File.AppendAllText(FilePath("history"), action + Environment.NewLine); }
            private void Topology(string topology) { File.WriteAllText(FilePath("topology"), topology); }

            public DisplayTarget[] Discover() { return new DisplayTarget[0]; }
            public byte[] Capture(string selectedId = null)
            {
                Record("capture");
                return new byte[] { 42, selectedId == Target ? (byte)1 : (byte)0 };
            }

            public void Validate(byte[] baseline, string id)
            {
                Record("validate");
                Check(id == Target && !Has("missing") && HasSelectedTarget(baseline), "invalid target");
            }

            public void Extend(byte[] baseline, string id)
            {
                CheckWakeRequest();
                Record("extend");
                Topology("extended");
                if (Has("hang-extend"))
                {
                    Set("owner-hung");
                    Thread.Sleep(Timeout.Infinite);
                }
                if (Has("pause-extend"))
                {
                    Set("owner-paused");
                    Until(() => Has("release-extend"), "release simulated display call");
                }
            }

            public void Isolate(string id)
            {
                Record("isolate");
                Topology("virtual");
            }

            public bool IsOnly(string id)
            {
                Record("readback");
                return !Has("bad-readback");
            }

            public string Restore(byte[] baseline)
            {
                CheckWakeRequest();
                Record("restore");
                Check(HasSelectedTarget(baseline) && !Has("restore-fails"), "invalid or unbound restore snapshot");
                Topology("physical");
                return Has("fallback") ? "physical-fallback" : "exact";
            }

            private void CheckWakeRequest()
            {
                if (Has("require-wake"))
                    Check(Has("wake-acquired") && !Has("wake-released"), "watchdog lacked its system request during display work");
            }

            private static bool HasSelectedTarget(byte[] baseline)
            {
                return baseline.Length == 2 && baseline[0] == 42 && baseline[1] == 1;
            }
        }
    }
}
