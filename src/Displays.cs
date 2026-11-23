using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AgentSleep
{
    internal sealed class NativeDisplays : IDisplays
    {
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

        public byte[] Capture()
        {
            State current = Read(true);
            DisplayLayout[] layouts = CaptureLayouts(current);
            byte[] baseline = DisplayBaseline.Encode(layouts);
            ValidateCurrentRecovery(current, layouts);
            return baseline;
        }

        public void Validate(byte[] baseline, string id)
        {
            DisplayLayout[] saved = DisplayBaseline.Decode(baseline);
            State current = Read(true);
            PrepareEntry(current, saved, id);
        }

        public void Extend(byte[] baseline, string id)
        {
            extended = null;
            extendedTarget = null;
            DisplayLayout[] saved = DisplayBaseline.Decode(baseline);
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
            DisplayLayout[] saved = DisplayBaseline.Decode(baseline);
            State current = Read(true);
            if (DisplayLayout.Exact(saved, current.Layouts)) return "exact";

            Plan exact = FindExactPlan(current, saved);
            if (exact != null)
            {
                try
                {
                    Apply(exact, current, "restore the saved desktop");
                    WaitFor(s => DisplayLayout.Exact(saved, s.Layouts));
                    return "exact";
                }
                catch (InvalidOperationException)
                {
                    current = Read(true);
                    if (DisplayLayout.Exact(saved, current.Layouts)) return "exact";
                }
            }

            if (HasUsablePhysicalDesktop(current)) return "physical-fallback";
            foreach (Route route in PhysicalFallbacks(current, saved))
            {
                Plan fallback;
                try { fallback = SingleAtOrigin(route, route.Layout); }
                catch (InvalidOperationException) { continue; }
                if (fallback.Set(false) != 0) continue;
                Apply(fallback, current, "bring back a connected physical display");
                WaitFor(s => IsOnly(s, route.Id, DisplayKind.Physical));
                return "physical-fallback";
            }
            throw new InvalidOperationException("Windows could not restore the saved layout or validate a connected physical display. Wake or reconnect a monitor and retry Restore.");
        }

        private static Plan PrepareEntry(State current, DisplayLayout[] saved, string id)
        {
            if (!DisplayLayout.Exact(saved, CaptureLayouts(current)))
                throw new InvalidOperationException("The saved display layout no longer matches the desktop. Start a new AgentSleep session.");
            Route[] candidates = SelectVirtual(current, id);
            Route active = candidates.SingleOrDefault(r => r.Path.IsActive);
            ValidateCurrentRecovery(current, current.Layouts);
            if (active != null)
            {
                Ccd.Check(SingleAtOrigin(active, active.Layout).Set(false), "validate the virtual-only desktop");
                return null;
            }

            var anchor = current.Layouts.OrderByDescending(l => l.Source.X + (long)l.Source.Width).First().Source;
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
                layout = existing.AtOrigin();
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

        private static IEnumerable<Route> PhysicalFallbacks(State s, DisplayLayout[] saved)
        {
            var original = new HashSet<string>(saved.Where(l => l.Kind == DisplayKind.Physical).Select(l => l.Id));
            string primary = saved.Single(l => l.Origin).Id;
            return s.Routes.Where(r => r.Available && r.Kind == DisplayKind.Physical && r.Id != null)
                .GroupBy(r => r.Id).Where(g => g.Select(r => r.Path.Target.Key).Distinct().Count() == 1)
                .OrderByDescending(g => g.Key == primary).ThenByDescending(g => original.Contains(g.Key))
                .ThenByDescending(g => g.Any(r => r.Path.IsActive)).SelectMany(g => g);
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

        private sealed class State
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
