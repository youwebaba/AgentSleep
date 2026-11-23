using System;
using System.IO;
using System.Linq;

namespace AgentSleep
{
    internal static class DisplayTests
    {
        internal static void Run()
        {
            Ccd.CheckAbi();
            var original = new[] { Layout('A', 0, 0), Layout('B', 2560, 0), Layout('C', -2560, 1) };
            byte[] baseline = DisplayBaseline.Encode(original);
            DisplayLayout[] decoded = DisplayBaseline.Decode(baseline);
            Require(DisplayLayout.Exact(original, decoded.Reverse().ToArray()), "reordered exact layout");
            Require(decoded[2].Source.X == -2560 && decoded[2].Source.Y == 1, "negative x and one-pixel y preserved");
            Require(decoded.Single(l => l.Origin).Id == original[0].Id, "primary identity preserved");

            decoded[2].Source.Y = 0;
            Require(!DisplayLayout.Exact(original, decoded), "one-pixel drift rejected");
            decoded[2].Source.Y = 1;
            decoded[2].Rotation = 2;
            Require(!DisplayLayout.Exact(original, decoded), "rotation drift rejected");
            decoded[2].Rotation = 1;
            decoded[2].Signal.VSync = new Ccd.Ratio { Numerator = 60000, Denominator = 1000 };
            Require(DisplayLayout.Exact(original, decoded), "equivalent refresh rationals accepted");
            decoded[2].Signal.VSync.Numerator = 59940;
            Require(!DisplayLayout.Exact(original, decoded), "refresh drift rejected");
            decoded[2].Signal.VSync = original[2].Signal.VSync;
            decoded[2].Id = new string('D', 64);
            Require(!DisplayLayout.Exact(original, decoded), "different monitor at same position rejected");
            decoded[2].Id = original[0].Id;
            Require(!DisplayLayout.Exact(original, decoded), "duplicate monitor does not match");
            Reject(() => DisplayBaseline.Encode(decoded), "duplicate saved stable identity");
            decoded = DisplayBaseline.Decode(baseline);
            decoded[2].Source = decoded[1].Source;
            Reject(() => DisplayBaseline.Encode(decoded), "cloned desktop position");
            Reject(() => DisplayBaseline.Decode(baseline.Take(baseline.Length - 1).ToArray()), "truncated snapshot");
            byte[] wrongVersion = (byte[])baseline.Clone();
            wrongVersion[3] ^= 1;
            Reject(() => DisplayBaseline.Decode(wrongVersion), "unknown snapshot version");

            var route = Route(original[0], 63, 9, 99);
            var replacement = Route(Layout('D', 0, 0), 63, 9, 99);
            var staleAddress = Route(original[0], 22, 1, 12);
            staleAddress.Path.Target.Available = 0;
            var routes = NativeDisplays.RestorableRoutes(new[] { replacement, staleAddress, route }, original[0]);
            Require(routes.Length == 1 && routes[0] == route, "restore binds only the saved identity on a current available route");
            var plan = NativeDisplays.Build(routes, new[] { original[0] });
            Require(plan.Paths[0].SourceIndex == 0 && plan.Paths[0].TargetIndex == 1 &&
                plan.Paths[0].DesktopIndex == 2 && plan.Modes[0].Key.Equals(route.Path.Source.Key) &&
                plan.Modes[1].Key.Equals(route.Path.Target.Key) && plan.Modes[2].Key.Equals(route.Path.Target.Key),
                "source, target and desktop modes use current addresses");
            var conflict = Route(original[0], 63, 9, 100);
            var noId = Route(original[0], 63, 9, 101);
            noId.Id = null;
            route.Path.Flags |= Ccd.Active;
            conflict.Path.Flags |= Ccd.Active;
            noId.Path.Flags |= Ccd.Active;
            var summaries = NativeDisplays.Summarize(new[] { route, conflict, noId });
            Require(summaries.Length == 3 && summaries.All(d => d.Active && d.Id == null && !d.IsVirtual && !d.IsPhysical),
                "unclassified active targets remain visible without selectable identities");

            Require(DisplayTrust.IsApprovedHardware(@"ROOT\MTTVDD") && DisplayTrust.IsApprovedHardware("MttVDD") &&
                !DisplayTrust.IsApprovedHardware(@"ROOT\MTTVDD_EXTRA"), "exact VDD hardware allowlist");
            Require(DisplayTrust.Classify(DisplayKind.Virtual, 5, false, false) == DisplayKind.Virtual,
                "signed and bound upstream VDD HDMI endpoint");
            Require(DisplayTrust.Classify(DisplayKind.Unknown, 5, true, false) == DisplayKind.Unknown,
                "unknown software HDMI is not promoted");
            Require(DisplayTrust.Classify(DisplayKind.Physical, 5, true, false) == DisplayKind.Physical,
                "EDID-backed PCI HDMI retained as physical");
            Require(DisplayTrust.Classify(DisplayKind.Physical, 16, true, false) == DisplayKind.Unknown &&
                DisplayTrust.Classify(DisplayKind.Physical, 5, false, false) == DisplayKind.Unknown &&
                DisplayTrust.Classify(DisplayKind.Physical, 5, true, true) == DisplayKind.Unknown,
                "indirect, unbound, and force-projected physical fallback refused");
            var monitor = new Ccd.MonitorInfo { DevicePath = "MONITOR-INTERFACE", Friendly = "Vendor Name" };
            string id = DisplayTrust.StableId("ADAPTER-INTERFACE", monitor);
            monitor.Friendly = "Changed Name";
            monitor.DevicePath = monitor.DevicePath.ToLowerInvariant();
            Require(id == DisplayTrust.StableId("adapter-interface", monitor) &&
                id != DisplayTrust.StableId("different-adapter", monitor), "identity comes from both device interfaces");
        }

        private static DisplayLayout Layout(char key, int x, int y)
        {
            return new DisplayLayout
            {
                Id = new string(key, 64), Kind = DisplayKind.Physical, VirtualMode = true,
                Source = new Ccd.SourceMode { Width = 2560, Height = 1440, Format = 4, X = x, Y = y },
                Rotation = 1, Scaling = 1, Scan = 1,
                Refresh = new Ccd.Ratio { Numerator = 60, Denominator = 1 },
                Signal = new Ccd.Signal
                {
                    Width = 2560, Height = 1440, TotalWidth = 2640, TotalHeight = 1481, PixelRate = 234590400, Scan = 1,
                    VSync = new Ccd.Ratio { Numerator = 60, Denominator = 1 }
                },
                Desktop = new Ccd.DesktopImage { Width = 2560, Height = 1440, Right = 2560, Bottom = 1440, ClipRight = 2560, ClipBottom = 1440 }
            };
        }

        private static NativeDisplays.Route Route(DisplayLayout layout, uint adapter, uint source, uint target)
        {
            return new NativeDisplays.Route
            {
                Id = layout.Id, Kind = layout.Kind,
                Path = new Ccd.Path
                {
                    Flags = Ccd.VirtualMode,
                    Source = new Ccd.SourcePath { Key = new Ccd.Endpoint { Adapter = new Ccd.Luid { Low = adapter }, Id = source } },
                    Target = new Ccd.TargetPath
                    { Available = 1, Key = new Ccd.Endpoint { Adapter = new Ccd.Luid { Low = adapter }, Id = target } }
                }
            };
        }

        private static void Reject(Action test, string name)
        {
            try { test(); }
            catch (InvalidDataException) { return; }
            throw new Exception("Display test failed to reject " + name + ".");
        }

        private static void Require(bool result, string name)
        {
            if (!result) throw new Exception("Display test failed: " + name + ".");
        }
    }
}
