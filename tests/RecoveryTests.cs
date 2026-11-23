using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;

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

            Case("prerequisites and arm failure", (path, scope, storage, displays) =>
            {
                using (var controller = new RecoveryController(displays, storage, id => null, 30))
                {
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

            Case("timed restore and concurrent entrant", (path, scope, storage, displays) =>
            {
                using (var controller = Controller(path, scope, storage, displays))
                {
                    controller.Start(Target, true);
                    Check(controller.State == "trial", controller.Detail);
                    using (var competing = Controller(path, scope, storage, displays))
                    {
                        competing.Start(Target, true);
                        Check(competing.Pending, "competing recovery did not adopt live snapshot");
                    }
                    Until(() => Poll(controller, "timed-restore-exact"), "timed exact");
                    Check(displays.History == "capture,validate,extend,isolate,readback,restore", displays.History);
                    Check(!storage.Read().Pending && displays.Physical, "exact restore did not compact snapshot");
                    Check(RecoveryWorker.RestoreCurrent(storage, displays) == 0 && displays.RestoreCount == 1, "repeated restore");
                }
                Check(storage.Read() == null, "consumed trial receipt remained");
            });

            Case("manual restore", (path, scope, storage, displays) =>
            {
                using (var controller = Controller(path, scope, storage, displays))
                {
                    controller.Start(Target, true);
                    Check(controller.State == "trial", controller.Detail);
                    controller.Restore();
                    Check(RecoveryWorker.RestoreCurrent(storage, displays) == 0, "manual restore exit");
                    Until(() => Poll(controller, "restore-exact"), "manual exact");
                    Check(storage.Read().Reason == RecoveryReason.Manual && displays.RestoreCount == 1, "manual race");
                }
            });

            Case("activation failure with rollback", (path, scope, storage, displays) =>
            {
                using (var controller = Controller(path, scope, storage, displays))
                {
                    displays.Set("bad-readback");
                    controller.Start(Target, false);
                    Until(() => Poll(controller, "activation-failed-restored-exact"), "activation failure");
                    Check(controller.State == "failed" && displays.Physical && !controller.Pending, "activation state");
                }
            });

            Case("fallback and explicit retry", (path, scope, storage, displays) =>
            {
                using (var controller = Controller(path, scope, storage, displays))
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

            Case("failed rollback keeps snapshot", (path, scope, storage, displays) =>
            {
                using (var controller = Controller(path, scope, storage, displays))
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

            Case("prior logon and corrupt snapshot", (path, scope, storage, displays) =>
            {
                using (var process = Process.GetCurrentProcess())
                    storage.Write(new RecoveryRecord
                    {
                        Id = Guid.NewGuid(), OwnerPid = process.Id, OwnerStart = RecoveryOwner.ReadStart(process.Id),
                        Phase = RecoveryPhase.Complete, Baseline = new byte[0]
                    });
                var other = RecoveryStorage.ForTest(path, scope, 2);
                try
                {
                    RecoveryWorker.RestoreCurrent(other, displays);
                    throw new InvalidOperationException("prior logon replay allowed");
                }
                catch (RecoveryStaleException) { }

                using (var controller = new RecoveryController(displays, other, id => null, 30))
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

            Case("independent restore after parent loss", (path, scope, storage, displays) =>
            {
                using (var owner = Launch("--recover-test-owner", path, scope, "crash"))
                {
                    Until(() => displays.Has("owner-isolated"), "parent reached isolation");
                    owner.Kill();
                    Check(owner.WaitForExit(5000), "parent did not exit");
                    Until(() => Complete(storage, RecoveryReason.OwnerExit), "parent exit restore");
                    Check(displays.RestoreCount == 1 && displays.Physical, "parent exit topology");
                }
            });

            Case("independent restore after owner hang", (path, scope, storage, displays) =>
            {
                using (var owner = Launch("--recover-test-owner", path, scope, "hang"))
                {
                    Until(() => displays.Has("owner-hung"), "parent began native-call simulation");
                    Check(owner.WaitForExit(8000) && owner.ExitCode == 2, "hung mutation owner was not fenced");
                    Until(() => Complete(storage, RecoveryReason.OwnerHang), "hung parent restore");
                    Check(displays.RestoreCount == 1 && displays.Physical, "hung mutation topology");
                }
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
                    exitCode = RecoveryWorker.Watch(storage, displays, id, TestTrial);
                return true;
            }

            if (args[3] == "hang") displays.Set("hang-extend");
            using (var controller = Controller(args[1], scope, storage, displays))
            {
                controller.Start(Target, false);
                Check(controller.State == "active", controller.Detail);
                displays.Set("owner-isolated");
                while (true)
                {
                    controller.Poll();
                    Thread.Sleep(30);
                }
            }
        }

        private static RecoveryController Controller(string path, Guid scope, RecoveryStorage storage, FakeDisplays displays)
        {
            return new RecoveryController(displays, storage,
                id => Launch("--recover-test-watch", path, scope, id.ToString("N")), 5000);
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

        private static bool Poll(RecoveryController controller, string detail)
        {
            controller.Poll();
            return controller.Detail == detail;
        }

        private static void Case(string name, Action<string, Guid, RecoveryStorage, FakeDisplays> test)
        {
            Guid id = Guid.NewGuid();
            string path = Path.Combine(Path.GetTempPath(), "AgentSleepRecoveryTests-" + id.ToString("N"));
            try
            {
                var storage = RecoveryStorage.ForTest(path, id, 1);
                test(path, id, storage, new FakeDisplays(path));
                Console.WriteLine("PASS recovery: " + name);
            }
            finally
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
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
            public byte[] Capture()
            {
                Record("capture");
                return new byte[] { 42 };
            }

            public void Validate(byte[] baseline, string id)
            {
                Record("validate");
                Check(id == Target && !Has("missing") && baseline[0] == 42, "invalid target");
            }

            public void Extend(byte[] baseline, string id)
            {
                Record("extend");
                Topology("extended");
                if (Has("hang-extend"))
                {
                    Set("owner-hung");
                    Thread.Sleep(Timeout.Infinite);
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
                Record("restore");
                Check(baseline.Length == 1 && baseline[0] == 42 && !Has("restore-fails"), "invalid restore");
                Topology("physical");
                return Has("fallback") ? "physical-fallback" : "exact";
            }
        }
    }
}
