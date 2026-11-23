using System;
using System.IO;

namespace AgentSleep
{
    internal static class TrayDryRunTests
    {
        internal static void Run()
        {
            var fake = new Inventory();
            var physical = new DisplayTarget { Id = "physical", Active = true, Available = true, IsPhysical = true };
            var unknown = new DisplayTarget { Id = "unknown", Active = true, Available = true };
            var first = new DisplayTarget { Id = "virtual-a", Label = "Driver A", Available = true, IsVirtual = true };
            var second = new DisplayTarget { Id = "virtual-b", Label = "Driver B", Available = true, IsVirtual = true };

            fake.Targets = new[] { physical };
            Check(fake, new string[0], 0, "Available verified virtual targets: 0");
            Check(fake, new string[0], 0, "Install and enable");
            fake.Targets = new[] { physical, first, second };
            Check(fake, new string[0], 0, "Available verified virtual targets: 2");
            Check(fake, new string[0], 0, "rerun with --target");
            Check(fake, new[] { "--target", "stale" }, 3, "one exact verified target");
            Check(fake, new[] { "--target", "physical" }, 3, "one exact verified target");
            fake.Targets = new[] { physical, unknown, first, second };
            Check(fake, new[] { "--target", "unknown" }, 3, "one exact verified target");
            Check(fake, new[] { "--target", "virtual-a" }, 3, "classifiable physical layout");
            fake.Targets = new[] { physical, first, first };
            Check(fake, new[] { "--target", "virtual-a" }, 3, "one exact verified target");
            Check(fake, new[] { "--restore" }, 2, "Usage");
            if (fake.Validations != 0) throw new Exception("Dry-run validated a target before selection was eligible.");

            fake.Targets = new[] { physical, first, second };
            Check(fake, new[] { "--target", "VIRTUAL-A" }, 0, "Windows accepted topology validation");
            if (fake.Validations != 1) throw new Exception("Dry-run skipped the selected target's native validation.");
        }

        private static void Check(IDisplays displays, string[] arguments, int expectedCode, string expectedText)
        {
            using (var output = new StringWriter())
            {
                int code = TrayDryRun.Run(arguments, output, displays);
                if (code != expectedCode || !output.ToString().Contains(expectedText))
                    throw new Exception("Unexpected dry-run result: " + output);
            }
        }

        private sealed class Inventory : IDisplays
        {
            internal DisplayTarget[] Targets;
            internal int Validations;
            public DisplayTarget[] Discover() { return Targets; }
            public byte[] Capture() { return new byte[] { 17 }; }
            public void Validate(byte[] baseline, string targetId)
            {
                if (baseline.Length != 1 || baseline[0] != 17 || targetId != "virtual-a")
                    throw new Exception("Dry-run validated the wrong topology.");
                Validations++;
            }
            public void Extend(byte[] baseline, string targetId) { throw new Exception("Dry-run attempted to extend."); }
            public void Isolate(string targetId) { throw new Exception("Dry-run attempted to isolate."); }
            public bool IsOnly(string targetId) { throw new Exception("Dry-run queried a switched target."); }
            public string Restore(byte[] baseline) { throw new Exception("Dry-run attempted to restore."); }
        }
    }
}
