using System;
using System.IO;
using System.Linq;

namespace AgentSleep
{
    internal static class TrayDryRun
    {
        private static int Main(string[] args)
        {
            if (args.Length != 0 && args[0] == "--dry-run") args = args.Skip(1).ToArray();
            return Run(args, Console.Out);
        }

        internal static int Run(string[] args, TextWriter output, IDisplays displays = null)
        {
            if (args.Length != 0 && (args.Length != 2 || args[0] != "--target" || String.IsNullOrWhiteSpace(args[1])))
            {
                output.WriteLine("Usage: AgentSleep.DryRun.exe [--target TARGET-ID]");
                return 2;
            }

            try
            {
                if (displays == null) displays = new NativeDisplays();
                DisplayTarget[] targets = displays.Discover();
                DisplayTarget[] virtuals = targets.Where(IsVirtualCandidate).ToArray();
                DisplayTarget selected = args.Length == 0 ? null : Select(virtuals, args[1]);
                int physical = targets.Count(display => display.Active && display.IsPhysical && !display.IsVirtual);
                int unknown = targets.Count(display => display.Active && display.IsPhysical == display.IsVirtual);
                output.WriteLine("AgentSleep read-only display check");
                output.WriteLine("Active physical displays: {0}", physical);
                output.WriteLine("Available verified virtual targets: {0}", virtuals.Length);
                output.WriteLine("Active unclassified displays: {0}", unknown);
                foreach (DisplayTarget target in virtuals)
                    output.WriteLine("  {0} [{1}] {2}", target.Label, target.Active ? "active" : "inactive", target.Id);

                byte[] baseline = null;
                try
                {
                    if (physical == 0 || unknown != 0)
                        throw new InvalidOperationException("A classifiable active physical layout is required.");
                    baseline = displays.Capture(selected == null ? null : selected.Id);
                    output.WriteLine("Windows accepted read-only validation of the current layout as a rollback candidate.");
                }
                catch (Exception error)
                {
                    output.WriteLine("A rollback candidate could not be validated. {0}", error.Message);
                }

                if (virtuals.Length == 0)
                {
                    output.WriteLine("Install and enable a supported virtual display driver separately, then rerun this check in the local Windows desktop.");
                    return args.Length == 0 ? 0 : 3;
                }
                if (args.Length == 0)
                {
                    output.WriteLine("Identify the driver in Windows, then rerun with --target and the matching target ID to validate its current topology.");
                    return 0;
                }

                if (selected == null || baseline == null)
                {
                    output.WriteLine("Validation requires one exact verified target and an active, classifiable physical layout.");
                    return 3;
                }

                try
                {
                    displays.Validate(baseline, selected.Id);
                }
                catch (Exception error)
                {
                    output.WriteLine("Windows topology validation did not pass. {0}", error.Message);
                    return 3;
                }
                output.WriteLine("Windows accepted topology validation. Verify switching, rollback and agent input in an attended trial.");
                return 0;
            }
            catch (Exception error)
            {
                output.WriteLine("Display discovery failed. Run from the unlocked local Windows desktop. {0}", error.Message);
                return 2;
            }
        }

        internal static bool IsVirtualCandidate(DisplayTarget display)
        {
            return display.IsVirtual && !display.IsPhysical && display.Available && !String.IsNullOrWhiteSpace(display.Id);
        }

        internal static DisplayTarget Select(DisplayTarget[] targets, string id)
        {
            DisplayTarget[] matches = targets.Where(display => IsVirtualCandidate(display) &&
                String.Equals(display.Id, id, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
    }
}
