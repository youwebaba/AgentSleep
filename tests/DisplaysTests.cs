using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentSleep
{
    internal static class DisplayTests
    {
        internal static void Run()
        {
            Ccd.CheckAbi();
#if DRY_RUN_ONLY
            VerifyDryRunGuard();
#endif
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
            var alternate = Route(original[1], 64, 11, 204);
            var unknown = Route(original[2], 64, 9, 205);
            unknown.Kind = DisplayKind.Unknown;
            var attempted = new HashSet<string>();
            var physical = new[] { unknown, replacement, alternate, route };
            Require(NativeDisplays.PhysicalFallbacks(physical, original, attempted).First() == route,
                "physical fallback prefers the saved primary");
            attempted.Add(route.Id);
            Require(NativeDisplays.PhysicalFallbacks(physical, original, attempted).SequenceEqual(new[] { alternate, replacement }),
                "physical fallback skips a failed panel, tries another original, and refuses unknown outputs");

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

            SelectedRestore(original);
        }

        private static void SelectedRestore(DisplayLayout[] physical)
        {
            DisplayLayout selected = Layout('E', 7680, 0);
            selected.Kind = DisplayKind.Virtual;
            DisplayLayout unrelated = Layout('D', 5120, 0);
            unrelated.Kind = DisplayKind.Virtual;
            var saved = physical.Concat(new[] { unrelated }).ToArray();
            string selectedId;
            DisplayBaseline.Decode(DisplayBaseline.Encode(saved), out selectedId);
            Require(selectedId == null, "legacy baseline keeps its original exact restore semantics");
            foreach (bool selectedWasActive in new[] { false, true })
            {
                DisplayLayout[] prior = selectedWasActive ? saved.Concat(new[] { selected }).ToArray() : saved;
                DisplayLayout[] layouts = DisplayBaseline.Decode(DisplayBaseline.Encode(prior, selected.Id), out selectedId);
                Require(selectedId == selected.Id && DisplayLayout.Exact(prior, layouts), "target binding round trip");
                var added = Layout('F', 10240, 0);
                var expected = saved.Concat(new[] { added }).ToArray();
                var current = Observed(new[] { selected.AtPosition(0, 0), added });
                int phases = 0;
                bool settled = false;
                string result = NativeDisplays.RestoreSelected(layouts, selectedId, () => current,
                    (before, proposed, verify, action) =>
                    {
                        Require(before == current, "restore transitions use freshly read state");
                        if (++phases == 1)
                        {
                            Require(DisplayLayout.Exact(expected, proposed.Where(l => l.Id != selectedId).ToArray()) &&
                                proposed.Count(l => l.Id == selectedId && !l.Origin) == 1,
                                "phase one restores original monitors and keeps the selected and unrelated outputs");
                        }
                        else
                        {
                            Require(settled && DisplayLayout.Exact(expected, proposed) &&
                                before.Layouts.Any(l => l.Id == selectedId),
                                "phase two detaches only the selected target after verified physical readback");
                        }
                        current = Observed(proposed);
                        Require(verify(current), "phase readback predicate");
                        return current;
                    }, () => { settled = true; });
                Require(result == "exact" && phases == 2 && DisplayLayout.Exact(expected, current.Layouts),
                    "two-phase exact rollback from virtual-only with selected originally active or inactive");
            }

            Reject(() => DisplayBaseline.Encode(new[] { selected.AtPosition(0, 0), physical[1] }, selected.Id),
                "selected virtual primary cannot be detached while preserving the original primary");
            var otherPrimary = new[] { unrelated.AtPosition(0, 0), physical[1] };
            Require(DisplayBaseline.Decode(DisplayBaseline.Encode(otherPrimary, selected.Id), out selectedId).Length == 2,
                "unrelated primary remains allowed");
            var alternatePrimary = Observed(new[] { selected.AtPosition(0, 0) });
            Require(NativeDisplays.RestoreSelected(otherPrimary, selected.Id, () => alternatePrimary,
                (before, proposed, verify, action) =>
                {
                    alternatePrimary = Observed(proposed);
                    Require(verify(alternatePrimary), "unrelated primary and original physical monitor verified");
                    return alternatePrimary;
                }, () => { }) == "exact" && alternatePrimary.Layouts.Single(l => l.Origin).Id == unrelated.Id,
                "two-stage restoration preserves a nonselected virtual primary");

            var unavailable = Observed(new[] { selected.AtPosition(0, 0) });
            int tried = 0;
            try
            {
                NativeDisplays.RestoreSelected(saved, selected.Id, () => unavailable, (before, proposed, verify, action) =>
                {
                    tried++;
                    Require(proposed.Any(l => l.Id == selected.Id) && !verify(unavailable),
                        "no detach without physically available originals");
                    return null;
                }, () => { throw new Exception("Unverified physical phase must not reach detach."); });
                throw new Exception("No physical output must leave rollback pending.");
            }
            catch (InvalidOperationException) { Require(tried == 1, "unavailable physical layout attempts bridge only"); }

            var existing = Observed(saved.Concat(new[] { selected }).ToArray());
            string fallback = NativeDisplays.RestoreSelected(saved, selected.Id, () => existing,
                (before, proposed, verify, action) =>
                {
                    Require(DisplayLayout.Exact(saved, proposed), "detach attempts only the verified original layout");
                    Require(!verify(existing), "failed removal cannot report exact success");
                    return null;
                }, () => { });
            Require(fallback == "physical-fallback" && existing.Layouts.Any(l => l.Id == selected.Id),
                "failed detach retains the active virtual alongside verified physical displays for retry");

            NativeDisplays.State afterUnplug = existing;
            fallback = NativeDisplays.RestoreSelected(saved, selected.Id, () => afterUnplug,
                (before, proposed, verify, action) => { throw new Exception("Hot-unplug must abort detach."); },
                () => { afterUnplug = Observed(new[] { physical[0], selected, unrelated }); });
            Require(fallback == "physical-fallback", "fresh read fails closed after a monitor unplugs before detach");

            var conflictingPrimary = Observed(new[] { Layout('F', 0, 0), selected });
            Require(NativeDisplays.RestoreSelected(saved, selected.Id, () => conflictingPrimary,
                (before, proposed, verify, action) => { throw new Exception("Conflicting new primary must remain active."); },
                () => { }) == "physical-fallback", "unrelated output at the saved primary position causes safe fallback");

            var alreadyNormal = Observed(saved);
            Require(NativeDisplays.RestoreSelected(saved, selected.Id, () => alreadyNormal,
                (before, proposed, verify, action) => { throw new Exception("Idempotent recovery must not switch."); },
                () => { throw new Exception("No virtual target to detach."); }) == "exact",
                "already inactive target is not reactivated");

            var onlyPrimary = Observed(new[] { physical[0] });
            Require(NativeDisplays.RestoreSelected(saved, selected.Id, () => onlyPrimary,
                (before, proposed, verify, action) =>
                {
                    Require(DisplayLayout.Exact(saved, proposed), "inactive selected target is never reactivated for recovery");
                    onlyPrimary = Observed(proposed);
                    Require(verify(onlyPrimary), "readback after restoration without selected target");
                    return onlyPrimary;
                }, () => { throw new Exception("No virtual phase is needed if the target disappeared."); }) == "exact",
                "restore missing originals directly if the selected target is already detached");
        }

        private static NativeDisplays.State Observed(DisplayLayout[] layouts)
        {
            return new NativeDisplays.State(layouts.Select(l =>
            {
                uint address = l.Id[0];
                var route = Route(l, address, 1, 2);
                route.Path.Flags |= Ccd.Active;
                route.Layout = l;
                return route;
            }).ToArray());
        }

#if DRY_RUN_ONLY
        private static void VerifyDryRunGuard()
        {
            // Null buffers keep this regression test from reaching user32 even if the guard is removed.
            try { Ccd.Set(null, null, true); }
            catch (NotSupportedException) { return; }
            throw new Exception("The read-only build did not reject display apply.");
        }
#endif

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
