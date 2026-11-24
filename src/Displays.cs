using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace AgentSleep
{
    internal sealed class NativeDisplays : IDisplays
    {
        private const int RestorePollMs = 250;
        private const int RestoreStableMs = 5500;
        private const int RestoreDeadlineMs = 35000;
        private const int RestoreMaximumSampleGapMs = 1000;
        private const int RestoreDetachAttempts = 2;
        private DisplayLayout[] extended;
        private string extendedTarget;

        internal NativeDisplays() { Ccd.CheckAbi(); }

        public DisplayTarget[] Discover()
        {
            return Summarize(Read(true).Routes);
        }

        internal static DisplayTarget[] Summarize(Route[] routes)
        {
            var ambiguous = new HashSet<string>(routes.Where(r => r.Id != null).GroupBy(r => r.Id)
                .Where(g => g.Select(r => r.Path.Target.Key).Distinct().Count() != 1).Select(g => g.Key));
            DisplayTarget[] displays = routes.GroupBy(r => r.Path.Target.Key).Select(g =>
            {
                Route[] active = g.Where(r => r.Path.IsActive).ToArray();
                Route display = active.FirstOrDefault() ?? g.First();
                bool identityKnown = active.Length < 2 && display.Id != null && !ambiguous.Contains(display.Id);
                return new DisplayTarget
                {
                    Id = identityKnown ? display.Id : null,
                    Label = display.Label,
                    Active = active.Length != 0,
                    Available = g.Any(r => r.Path.IsAvailable),
                    IsVirtual = identityKnown && display.Available && display.Kind == DisplayKind.Virtual,
                    IsPhysical = identityKnown && display.Available && display.Kind == DisplayKind.Physical
                };
            }).Where(d => d.Id != null || d.Active).ToArray();
            foreach (var duplicates in displays.Where(d => d.IsVirtual).GroupBy(d => d.Label).Where(g => g.Count() > 1))
            {
                foreach (DisplayTarget display in duplicates)
                    display.Label += " (" + display.Id.Substring(0, 8) + ")";
            }
            return displays;
        }

        public byte[] Capture(string selectedId = null)
        {
            State current = Read(true);
            DisplayLayout[] layouts = CaptureLayouts(current);
            byte[] baseline = DisplayBaseline.Encode(layouts, selectedId);
            if (selectedId != null) SelectVirtual(current, selectedId);
            ValidateCurrentRecovery(current, layouts);
            return baseline;
        }

        public void Validate(byte[] baseline, string id)
        {
            DisplayLayout[] saved = EntryLayouts(baseline, id);
            State current = Read(true);
            PrepareEntry(current, saved, id);
        }

        public void Extend(byte[] baseline, string id)
        {
            extended = null;
            extendedTarget = null;
            DisplayLayout[] saved = EntryLayouts(baseline, id);
            State current = Read(true);
            Plan extension = PrepareEntry(current, saved, id);
            if (extension != null) Apply(extension, current, "extend the desktop to the virtual display");
            State verified = WaitFor(s => HasExtended(s, saved, id));
            extended = CaptureLayouts(verified);
            extendedTarget = id;
        }

        public void Isolate(string id)
        {
            if (extended == null || id == null || id != extendedTarget)
                throw new InvalidOperationException("First extend to the selected virtual display with the physical desktop still active.");
            State current = Read(true);
            if (!DisplayLayout.Exact(extended, CaptureLayouts(current)))
                throw new InvalidOperationException("The extended desktop changed before isolation. Restore the display layout and retry.");
            Route target = SelectVirtual(current, id).Single(r => r.Path.IsActive);
            Plan isolation = SingleAtOrigin(target, target.Layout);
            Ccd.Check(isolation.Set(false), "validate the virtual-only desktop");
            Apply(isolation, current, "switch to the virtual-only desktop");
            WaitFor(s => IsOnly(s, id, DisplayKind.Virtual));
            extended = null;
            extendedTarget = null;
        }

        public bool IsOnly(string id) { return IsOnly(Read(false), id, DisplayKind.Virtual); }

        public string Restore(byte[] baseline)
        {
            extended = null;
            extendedTarget = null;
            string selectedId;
            DisplayLayout[] saved = DisplayBaseline.Decode(baseline, out selectedId);
            if (selectedId != null)
            {
                Stopwatch clock = Stopwatch.StartNew();
                return RestoreSelected(saved, selectedId, () => Read(true), SwitchAndVerify, () => clock.ElapsedMilliseconds, Thread.Sleep);
            }

            State current = Read(true);
            if (NormalRestored(current, saved)) return VerifyLegacyRestore(current, saved);

            Plan exact = FindExactPlan(current, saved);
            if (exact != null)
            {
                try
                {
                    Apply(exact, current, "restore the saved desktop");
                    current = WaitFor(s => NormalRestored(s, saved));
                }
                catch (InvalidOperationException)
                {
                    current = Read(true);
                }
                if (NormalRestored(current, saved)) return VerifyLegacyRestore(current, saved);
            }

            var attempted = new HashSet<string>();
            while (attempted.Count < 8)
            {
                if (NormalRestored(current, saved)) return VerifyLegacyRestore(current, saved);
                if (HasUsablePhysicalDesktop(current)) return "physical-fallback";
                Plan fallback = FindPhysicalPlan(current, saved, attempted);
                if (fallback == null) break;
                attempted.Add(fallback.Routes[0].Id);
                try
                {
                    Apply(fallback, current, "bring back a connected physical display");
                    current = WaitFor(HasUsablePhysicalDesktop);
                }
                catch (InvalidOperationException)
                {
                    current = Read(true);
                    continue;
                }
                return NormalRestored(current, saved) ? VerifyLegacyRestore(current, saved) : "physical-fallback";
            }
            if (NormalRestored(current, saved)) return VerifyLegacyRestore(current, saved);
            if (HasUsablePhysicalDesktop(current)) return "physical-fallback";
            throw new InvalidOperationException("Windows could not restore the saved layout or validate a connected physical display. Wake or reconnect a monitor and retry Restore.");
        }

        private static DisplayLayout[] EntryLayouts(byte[] baseline, string id)
        {
            string boundId;
            DisplayLayout[] layouts = DisplayBaseline.Decode(baseline, out boundId);
            if (boundId != null && boundId != id)
                throw new InvalidOperationException("The saved layout belongs to a different virtual display.");
            return layouts;
        }

        internal static string RestoreSelected(DisplayLayout[] saved, string selectedId, Func<State> read,
            Func<State, DisplayLayout[], Func<State, bool>, string, State> change, Func<long> now, Action<int> pause)
        {
            long deadline = now() + RestoreDeadlineMs;
            State current = ReadDuringRestore(read);
            while (now() < deadline && (current == null || current.Layouts.Any(l => l == null) || !SeparateSources(current)))
            {
                pause(RestorePollMs);
                current = ReadDuringRestore(read);
            }
            if (now() >= deadline) return UnstableRestore(current);

            DisplayLayout[] live = current.Layouts;
            DisplayLayout[] final = saved.Where(l => l.Id != selectedId)
                .Concat(live.Where(l => l.Id != selectedId && !saved.Any(s => s.Id == l.Id))).ToArray();
            if (final.GroupBy(l => new { l.Source.X, l.Source.Y }).Any(g => g.Count() != 1))
                return RestoreAlongsideFallback(current, saved, read, change);

            if (!NormalRestored(current, final) && !ReadyToDetach(current, final, selectedId))
            {
                if (current.Active.Any(r => r.Id == selectedId))
                {
                    Route target = SelectVirtual(current, selectedId).Single(r => r.Path.IsActive);
                    Ccd.SourceMode anchor = Rightmost(final);
                    DisplayLayout relocated = target.Layout.AtPosition(checked(anchor.X + (int)anchor.Width), anchor.Y);
                    DisplayLayout[] bridge = final.Concat(new[] { relocated }).ToArray();
                    change(current, bridge, s => ReadyToDetach(s, final, selectedId),
                        "restore the saved desktop alongside the virtual display");
                }
                else
                {
                    change(current, final, s => NormalRestored(s, final), "restore the saved desktop");
                }
                current = ReadDuringRestore(read);
            }

            return WaitForRestoreStability(current, final, selectedId, read, change, now, pause, deadline);
        }

        private static string VerifyLegacyRestore(State current, DisplayLayout[] final)
        {
            var clock = Stopwatch.StartNew();
            return WaitForRestoreStability(current, final, null, () => Read(true), SwitchAndVerify,
                () => clock.ElapsedMilliseconds, Thread.Sleep, RestoreDeadlineMs);
        }

        private static string WaitForRestoreStability(State current, DisplayLayout[] final, string selectedId, Func<State> read,
            Func<State, DisplayLayout[], Func<State, bool>, string, State> change, Func<long> now, Action<int> pause, long deadline)
        {
            long since = 0, previousSample = 0;
            RestoreObservation previous = RestoreObservation.Unknown;
            int detaches = 0;
            while (now() <= deadline)
            {
                long sample = now();
                RestoreObservation observation = ObserveRestore(current, final, selectedId);
                if (observation != previous || sample - previousSample > RestoreMaximumSampleGapMs) since = sample;
                previous = observation;
                previousSample = sample;
                if (sample - since >= RestoreStableMs)
                {
                    if (observation == RestoreObservation.Final) return "exact";
                    if (observation == RestoreObservation.WithVirtual && detaches < RestoreDetachAttempts &&
                        deadline - sample >= RestoreStableMs)
                    {
                        detaches++;
                        change(current, final, s => NormalRestored(s, final),
                            "finish restoring the saved desktop with the selected virtual display detached");
                        previous = RestoreObservation.Unknown;
                    }
                }
                long remaining = deadline - now();
                if (remaining <= 0) break;
                pause((int)Math.Min(RestorePollMs, remaining));
                current = ReadDuringRestore(read);
            }
            return UnstableRestore(current);
        }

        private enum RestoreObservation { Unknown, WithVirtual, Final }

        private static RestoreObservation ObserveRestore(State current, DisplayLayout[] final, string selectedId)
        {
            if (current == null) return RestoreObservation.Unknown;
            if (NormalRestored(current, final)) return RestoreObservation.Final;
            if (selectedId != null && ReadyToDetach(current, final, selectedId))
            {
                try
                {
                    if (SelectVirtual(current, selectedId).Count(r => r.Path.IsActive) == 1)
                        return RestoreObservation.WithVirtual;
                }
                catch (InvalidOperationException) { }
            }
            return RestoreObservation.Unknown;
        }

        private static State ReadDuringRestore(Func<State> read)
        {
            try { return read(); }
            catch (InvalidOperationException) { return null; }
        }

        private static string UnstableRestore(State current)
        {
            if (current != null && current.Layouts.Any(l => l != null && l.Kind == DisplayKind.Physical))
                return "physical-fallback";
            throw new InvalidOperationException("Windows did not verify a stable restored desktop. Wake or reconnect the original monitors and retry Restore.");
        }

        private static bool NormalRestored(State state, DisplayLayout[] expected)
        {
            return SeparateSources(state) && DisplayLayout.Exact(expected, state.Layouts);
        }

        private static bool ReadyToDetach(State state, DisplayLayout[] expected, string selectedId)
        {
            return SeparateSources(state) &&
                state.Layouts.Count(l => l != null && l.Id == selectedId && l.Kind == DisplayKind.Virtual && !l.Origin) == 1 &&
                state.Layouts.Any(l => l != null && l.Kind == DisplayKind.Physical) &&
                DisplayLayout.Exact(expected, state.Layouts.Where(l => l == null || l.Id != selectedId).ToArray());
        }

        private static State SwitchAndVerify(State before, DisplayLayout[] proposed, Func<State, bool> verify, string action)
        {
            Plan plan = FindExactPlan(before, proposed);
            if (plan == null) return null;
            try
            {
                Apply(plan, before, action);
                return WaitFor(verify);
            }
            catch (InvalidOperationException) { return null; }
        }

        private static string RestoreAlongsideFallback(State current, DisplayLayout[] saved, Func<State> read,
            Func<State, DisplayLayout[], Func<State, bool>, string, State> change)
        {
            var attempted = new HashSet<string>();
            while (attempted.Count < 8)
            {
                if (current.Layouts.Any(l => l != null && l.Kind == DisplayKind.Physical)) return "physical-fallback";
                DisplayLayout[] keep = CaptureLayouts(current);
                Route route = PhysicalFallbacks(current.Routes, saved, attempted).FirstOrDefault();
                if (route == null) break;
                attempted.Add(route.Id);
                Ccd.SourceMode source;
                try
                {
                    Ccd.SourceMode anchor = keep.Length == 0 ? default(Ccd.SourceMode) : Rightmost(keep);
                    source = Ccd.GetPreferred(route.Path.Target.Key, checked(anchor.X + (int)anchor.Width), anchor.Y);
                }
                catch (InvalidOperationException) { continue; }
                if (!DisplayBaseline.Usable(source)) continue;
                DisplayLayout[] plan = keep.Concat(new[] { Preferred(route, source) }).ToArray();
                State verified = change(current, plan, s =>
                    s.Layouts.Count(l => l != null && l.Id == route.Id && l.Kind == DisplayKind.Physical) == 1 &&
                    DisplayLayout.Exact(keep, s.Layouts.Where(l => l == null || l.Id != route.Id).ToArray()),
                    "bring back a connected physical display alongside the existing desktop");
                if (verified != null) return "physical-fallback";
                current = read();
            }
            if (current.Layouts.Any(l => l != null && l.Kind == DisplayKind.Physical)) return "physical-fallback";
            throw new InvalidOperationException("Windows could not restore the original physical layout while keeping the existing desktop active. Wake or reconnect a physical monitor and retry Restore.");
        }

        private static Ccd.SourceMode Rightmost(DisplayLayout[] layouts)
        {
            return layouts.OrderByDescending(l => l.Source.X + (long)l.Source.Width).First().Source;
        }

        private static Plan FindPhysicalPlan(State current, DisplayLayout[] saved, HashSet<string> attempted)
        {
            foreach (Route route in PhysicalFallbacks(current.Routes, saved, attempted).Take(128))
            {
                Plan fallback;
                try { fallback = SingleAtOrigin(route, route.Layout); }
                catch (InvalidOperationException) { continue; }
                if (fallback.Set(false) == 0) return fallback;
            }
            return null;
        }

        private static Plan PrepareEntry(State current, DisplayLayout[] saved, string id)
        {
            if (!DisplayLayout.Exact(saved, CaptureLayouts(current)))
                throw new InvalidOperationException("The saved display layout no longer matches the desktop. Start a new AgentSleep session.");
            if (saved.Any(l => l.Id == id && l.Origin))
                throw new InvalidOperationException("Choose a different primary display in Windows before starting AgentSleep. The selected virtual display is detached after recovery.");
            Route[] candidates = SelectVirtual(current, id);
            Route active = candidates.SingleOrDefault(r => r.Path.IsActive);
            ValidateCurrentRecovery(current, current.Layouts);
            if (active != null)
            {
                Route[] normal = current.Active.Where(r => r.Id != id).ToArray();
                Ccd.Check(Build(normal, normal.Select(r => r.Layout).ToArray()).Set(false),
                    "validate detaching the virtual display after the saved physical desktop returns");
                Ccd.Check(SingleAtOrigin(active, active.Layout).Set(false), "validate the virtual-only desktop");
                return null;
            }

            var anchor = Rightmost(current.Layouts);
            var size = Ccd.GetPreferred(candidates[0].Path.Target.Key, checked(anchor.X + (int)anchor.Width), anchor.Y);
            if (!DisplayBaseline.Usable(size))
                throw new InvalidOperationException("Windows reported an unusable preferred mode for the virtual display.");
            var occupied = new HashSet<Ccd.Endpoint>(current.Active.Select(r => r.Path.Source.Key));
            foreach (Route route in candidates.Where(r => !occupied.Contains(r.Path.Source.Key)))
            {
                var layout = Preferred(route, size);
                Plan extension = Build(current.Active.Concat(new[] { route }).ToArray(),
                    current.Layouts.Concat(new[] { layout }).ToArray());
                if (extension.Set(false) == 0 && SingleAtOrigin(route, layout).Set(false) == 0)
                    return extension;
            }
            throw new InvalidOperationException("Windows could not validate both the extended and virtual-only layouts. Check the virtual display settings and retry.");
        }

        private static bool HasExtended(State current, DisplayLayout[] saved, string id)
        {
            DisplayLayout[] layouts = current.Layouts;
            DisplayLayout[] original = saved.Any(l => l.Id == id) ? layouts : layouts.Where(l => l == null || l.Id != id).ToArray();
            return layouts.Count(l => l != null && l.Id == id && l.Kind == DisplayKind.Virtual) == 1 &&
                DisplayLayout.Exact(saved, original) && SeparateSources(current);
        }

        private static Plan SingleAtOrigin(Route route, DisplayLayout existing)
        {
            DisplayLayout layout;
            if (existing != null)
            {
                layout = existing.AtPosition(0, 0);
            }
            else
            {
                var preferred = Ccd.GetPreferred(route.Path.Target.Key, 0, 0);
                if (!DisplayBaseline.Usable(preferred))
                    throw new InvalidOperationException("Windows reported an unusable preferred mode for the display.");
                layout = Preferred(route, preferred);
            }
            return Build(new[] { route }, new[] { layout });
        }

        private static DisplayLayout Preferred(Route route, Ccd.SourceMode source)
        {
            return new DisplayLayout
            {
                Id = route.Id, Kind = route.Kind, VirtualMode = route.Path.HasVirtualMode,
                Rotation = 1, Scaling = 128, Source = source
            };
        }

        private static Route[] SelectVirtual(State current, string id)
        {
            var matches = current.Routes.Where(r => r.Id != null && r.Id == id).ToArray();
            if (matches.Length == 0)
                throw new InvalidOperationException("The selected virtual display was not found. Check the supported driver and choose the display again.");
            if (matches.Select(r => r.Path.Target.Key).Distinct().Count() != 1 || matches.Count(r => r.Path.IsActive) > 1)
                throw new InvalidOperationException("Windows returned ambiguous routes for the selected virtual display. Check the display setup.");
            var choices = matches.Where(r => r.Available && r.Kind == DisplayKind.Virtual).ToArray();
            if (choices.Length == 0 || matches.Any(r => r.Path.IsActive && !choices.Contains(r)))
                throw new InvalidOperationException("The selected display is unavailable or its installed virtual driver is unsupported.");
            return choices;
        }

        private static bool IsOnly(State current, string id, DisplayKind kind)
        {
            return current.Active.Length == 1 && id != null && current.Active[0].Id == id &&
                current.Active[0].Available && current.Active[0].Kind == kind &&
                current.Layouts[0] != null && current.Layouts[0].Origin;
        }

        private static bool HasUsablePhysicalDesktop(State s)
        {
            return s.Active.Length != 0 && s.Active.All(r => r.Available && r.Kind == DisplayKind.Physical) &&
                s.Layouts.All(l => l != null) && s.Layouts.Any(l => l.Origin);
        }

        internal static IEnumerable<Route> PhysicalFallbacks(Route[] routes, DisplayLayout[] saved, HashSet<string> attempted)
        {
            var original = new HashSet<string>(saved.Where(l => l.Kind == DisplayKind.Physical).Select(l => l.Id));
            string primary = saved.Single(l => l.Origin).Id;
            return routes.Where(r => r.Id != null && !attempted.Contains(r.Id)).GroupBy(r => r.Id)
                .Where(g => g.Select(r => r.Path.Target.Key).Distinct().Count() == 1)
                .OrderByDescending(g => g.Key == primary).ThenByDescending(g => original.Contains(g.Key))
                .ThenByDescending(g => g.Any(r => r.Path.IsActive)).SelectMany(g => g)
                .Where(r => r.Available && r.Kind == DisplayKind.Physical);
        }

        private static bool SeparateSources(State current)
        {
            return current.Active.Select(r => r.Path.Source.Key).Distinct().Count() == current.Active.Length;
        }

        private static DisplayLayout[] CaptureLayouts(State state)
        {
            if (state.Layouts.Any(l => l == null) || !SeparateSources(state))
                throw new InvalidOperationException("Windows reported a cloned, unavailable, or unsupported active display. Use separate supported displays and retry.");
            return state.Layouts;
        }

        private static void ValidateCurrentRecovery(State current, DisplayLayout[] layouts)
        {
            if (layouts.Any(layout => RestorableRoutes(current.Routes, layout).Length == 0))
                throw new InvalidOperationException("A saved display has no unambiguous recovery route. Check the monitor connections and retry.");
            Ccd.Check(Build(current.Active, layouts).Set(false), "validate recovery of the current display layout");
        }

        private static Plan FindExactPlan(State current, DisplayLayout[] saved)
        {
            var choices = saved.Select(layout => RestorableRoutes(current.Routes, layout)).ToArray();
            if (choices.Any(routes => routes.Length == 0)) return null;
            int budget = 256;
            return FindPlan(choices, saved, new Route[saved.Length], 0, ref budget);
        }

        internal static Route[] RestorableRoutes(Route[] routes, DisplayLayout layout)
        {
            Route[] matches = routes.Where(r => (r.Available || r.Path.IsActive) && r.Id == layout.Id).ToArray();
            if (matches.Select(r => r.Path.Target.Key).Distinct().Count() != 1) return new Route[0];
            return matches.Where(r => r.Available && r.Kind == layout.Kind && r.Path.HasVirtualMode == layout.VirtualMode).ToArray();
        }

        private static Plan FindPlan(Route[][] choices, DisplayLayout[] saved, Route[] selected, int index, ref int budget)
        {
            if (index == selected.Length)
            {
                Plan plan = Build(selected, saved);
                return plan.Set(false) == 0 ? plan : null;
            }
            foreach (Route route in choices[index])
            {
                if (--budget < 0) return null;
                if (selected.Take(index).Any(r => r.Path.Source.Key.Equals(route.Path.Source.Key))) continue;
                selected[index] = route;
                Plan plan = FindPlan(choices, saved, selected, index + 1, ref budget);
                if (plan != null) return plan;
            }
            return null;
        }

        private static void Apply(Plan plan, State before, string action)
        {
            State latest = Read(true);
            if (!before.Active.Select(r => r.Signature).SequenceEqual(latest.Active.Select(r => r.Signature)) ||
                !Enumerable.Range(0, before.Layouts.Length).All(i => before.Layouts[i] == null
                    ? latest.Layouts[i] == null : before.Layouts[i].Same(latest.Layouts[i])) ||
                !plan.Routes.All(expected => latest.Routes.Any(r => r.Available && r.Signature.Equals(expected.Signature)) &&
                    !latest.Routes.Any(r => r.Id == expected.Id && !r.Path.Target.Key.Equals(expected.Path.Target.Key))))
                throw new InvalidOperationException("Display connections or modes changed before applying the layout. Retry when they are stable.");
            Ccd.Check(plan.Set(true), action);
        }

        private static State WaitFor(Func<State, bool> verify)
        {
            InvalidOperationException lastError = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (attempt > 0) Thread.Sleep(200);
                    State current = Read(false);
                    if (verify(current)) return current;
                    lastError = null;
                }
                catch (InvalidOperationException error) { lastError = error; }
            }
            if (lastError != null) throw lastError;
            throw new InvalidOperationException("Windows did not confirm the requested display layout. Restore the desktop when connections are stable.");
        }

        private static State Read(bool all)
        {
            Ccd.Path[] paths;
            Ccd.Mode[] modes;
            Ccd.Query(all, out paths, out modes);
            var adapters = new Dictionary<Ccd.Luid, Tuple<string, DisplayKind>>();
            var metadata = new Dictionary<Ccd.Endpoint, Tuple<Ccd.MonitorInfo, string, DisplayKind>>();
            var routes = new List<Route>();
            foreach (Ccd.Path path in paths.Where(p => p.IsAvailable || p.IsActive))
            {
                Tuple<Ccd.MonitorInfo, string, DisplayKind> target;
                if (!metadata.TryGetValue(path.Target.Key, out target))
                {
                    Ccd.MonitorInfo monitor = Ccd.GetMonitor(path.Target.Key);
                    Tuple<string, DisplayKind> adapter;
                    if (!adapters.TryGetValue(path.Target.Key.Adapter, out adapter))
                    {
                        string device = Ccd.GetAdapter(path.Target.Key.Adapter);
                        adapter = Tuple.Create(device, string.IsNullOrWhiteSpace(device) ? DisplayKind.Unknown : DisplayTrust.Adapter(device));
                        adapters.Add(path.Target.Key.Adapter, adapter);
                    }
                    string id = DisplayTrust.StableId(adapter.Item1, monitor);
                    bool edidMonitor = id != null && adapter.Item2 == DisplayKind.Physical &&
                        DisplayTrust.IsDirect(monitor.Technology) && (monitor.Flags & 4) != 0 &&
                        DisplayTrust.IsMonitor(monitor.DevicePath);
                    var kind = id == null ? DisplayKind.Unknown : DisplayTrust.Classify(adapter.Item2, monitor.Technology, edidMonitor, false);
                    target = Tuple.Create(monitor, id, kind);
                    metadata.Add(path.Target.Key, target);
                }
                bool forced = (path.Target.Status & 0x3c) != 0 || (target.Item1.Flags & 2) != 0;
                DisplayKind kindForPath = forced ? DisplayKind.Unknown : target.Item3;
                var route = new Route
                {
                    Path = path, Id = target.Item2, Kind = kindForPath,
                    Label = string.IsNullOrWhiteSpace(target.Item1.Friendly) ?
                        (kindForPath == DisplayKind.Virtual ? "Virtual display" : "Display") : target.Item1.Friendly
                };
                if (path.IsActive && route.Available) route.Layout = CurrentLayout(route, modes);
                routes.Add(route);
            }
            return new State(routes.ToArray());
        }

        private static DisplayLayout CurrentLayout(Route route, Ccd.Mode[] modes)
        {
            Ccd.Path path = route.Path;
            if (route.Kind == DisplayKind.Unknown || path.SourceIndex >= modes.Length || path.TargetIndex >= modes.Length)
                return null;
            Ccd.Mode source = modes[path.SourceIndex], target = modes[path.TargetIndex];
            if (source.Type != 1 || !source.Key.Equals(path.Source.Key) || !DisplayBaseline.Usable(source.Source) ||
                target.Type != 2 || !target.Key.Equals(path.Target.Key) || target.Signal.Width == 0 || target.Signal.Height == 0)
                return null;
            var layout = new DisplayLayout
            {
                Id = route.Id, Kind = route.Kind, VirtualMode = path.HasVirtualMode, BoostRefresh = (path.Flags & Ccd.BoostRefresh) != 0,
                Rotation = path.Target.Rotation, Scaling = path.Target.Scaling, Scan = path.Target.Scan,
                Refresh = path.Target.Refresh, Source = source.Source, Signal = target.Signal
            };
            if (path.DesktopIndex != Ccd.None)
            {
                if (path.DesktopIndex >= modes.Length) return null;
                Ccd.Mode desktop = modes[path.DesktopIndex];
                if (desktop.Type != 3) return null;
                if (!desktop.Key.Equals(path.Target.Key))
                {
                    if (!desktop.Key.Equals(path.Source.Key)) return null;
                    layout.DesktopUsesSource = true;
                }
                layout.Desktop = desktop.Desktop;
            }
            return layout;
        }

        internal static Plan Build(Route[] routes, DisplayLayout[] layouts)
        {
            var paths = new Ccd.Path[routes.Length];
            var modes = new List<Ccd.Mode>();
            for (int index = 0; index < paths.Length; index++)
            {
                Ccd.Path path = routes[index].Path;
                DisplayLayout layout = layouts[index];
                path.Flags = Ccd.Active | (layout.VirtualMode ? Ccd.VirtualMode : 0) | (layout.BoostRefresh ? Ccd.BoostRefresh : 0);
                path.Target.Rotation = layout.Rotation;
                path.Target.Scaling = layout.Scaling;
                path.Target.Refresh = layout.Refresh;
                path.Target.Scan = layout.Scan;
                uint source = (uint)modes.Count, target = Ccd.None, desktop = Ccd.None;
                modes.Add(new Ccd.Mode { Type = 1, Key = path.Source.Key, Source = layout.Source });
                if (layout.Signal.Width != 0)
                {
                    target = (uint)modes.Count;
                    modes.Add(new Ccd.Mode { Type = 2, Key = path.Target.Key, Signal = layout.Signal });
                }
                if (layout.Desktop.HasValue)
                {
                    desktop = (uint)modes.Count;
                    modes.Add(new Ccd.Mode
                    {
                        Type = 3, Key = layout.DesktopUsesSource ? path.Source.Key : path.Target.Key,
                        Desktop = layout.Desktop.Value
                    });
                }
                path.SetIndices(source, target, desktop);
                paths[index] = path;
            }
            return new Plan { Routes = (Route[])routes.Clone(), Paths = paths, Modes = modes.ToArray() };
        }

        internal sealed class Route
        {
            internal Ccd.Path Path;
            internal string Id, Label;
            internal DisplayKind Kind;
            internal DisplayLayout Layout;
            internal bool Available { get { return Path.IsAvailable && Id != null; } }
            internal Tuple<string, DisplayKind, Ccd.Endpoint, Ccd.Endpoint, bool, int> Signature
            { get { return Tuple.Create(Id, Kind, Path.Source.Key, Path.Target.Key, Path.HasVirtualMode, Path.Target.Technology); } }
        }

        internal sealed class State
        {
            internal readonly Route[] Routes, Active;
            internal readonly DisplayLayout[] Layouts;
            internal State(Route[] routes)
            {
                Routes = routes;
                Active = routes.Where(r => r.Path.IsActive).ToArray();
                Layouts = Active.Select(r => r.Layout).ToArray();
            }
        }

        internal sealed class Plan
        {
            internal Route[] Routes;
            internal Ccd.Path[] Paths;
            internal Ccd.Mode[] Modes;
            internal int Set(bool apply) { return Ccd.Set(Paths, Modes, apply); }
        }
    }
}
