using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AgentSleep
{
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly IDisplays displays;
        private readonly RecoveryController recovery;
        private readonly TrayWindow window;
        private readonly NotifyIcon icon;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem start, restore, pause, settingsMenu;
        private readonly Timer timer;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly IdleClock idle = new IdleClock();
        private readonly ResumeRestore resumeRestore = new ResumeRestore();
        private TraySettings settings;
        private bool paused;
        private bool dialogOpen;
        private bool exitRequested;
        private bool restoreRequested;
        private bool powerFailed;
        private bool waitingForInput;
        private uint? restoreInput;
        private long inventoryAfter;
        private DisplayTarget[] inventory = new DisplayTarget[0];
        private string inventoryError = "";
        private string notice = "";
        private string browserStatus = "";
        private string sessionTarget;
        private bool ownTrial;
        private bool qualifyingTrial;
        private bool desktopVerified;
        private bool observedBrowserVerification;
        private long checkAfter;
        private long verifyBy;
        private BrowserInventory before;
        private Rectangle? verifiedBounds;
        private string verifiedScreen;

        internal TrayContext(IDisplays displays)
        {
            this.displays = displays;
            recovery = new RecoveryController(new LocalEntryDisplays(displays, RequireEntryPower));
            try { settings = TraySettings.Load(); }
            catch (Exception error)
            {
                settings = new TraySettings();
                notice = error.Message;
                paused = true;
            }

            window = new TrayWindow();
            window.TryRegister(settings.Modifiers, settings.Key);
            window.Hotkey = delegate { RequestRestore("Restore hotkey received."); };
            window.Interrupted = delegate { Interrupt("Session locked, disconnected, or suspended."); };
            window.Resumed = ResetAfterInterruption;
            window.DisplayChanged = delegate { inventoryAfter = 0; };

            menu = new ContextMenuStrip();
            start = Add("Start now", delegate { StartSession(false); });
            restore = Add("Restore displays", delegate { RequestRestore("Restore requested from the tray."); });
            pause = Add("Pause timer", TogglePause);
            settingsMenu = Add("Settings", ShowSettings);
            Add("Status", ShowStatus);
            Add("Exit", ExitFromMenu);
            menu.Opening += delegate
            {
                start.Enabled = !recovery.Pending && !dialogOpen;
                restore.Enabled = recovery.Pending;
                pause.Enabled = settings.Timer && !recovery.Pending;
                pause.Checked = paused;
                settingsMenu.Enabled = !recovery.Pending && !dialogOpen;
            };
            icon = new NotifyIcon { Text = "AgentSleep", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
            icon.DoubleClick += delegate { ShowStatus(); };
            timer = new Timer { Interval = 500, Enabled = true };
            timer.Tick += delegate { Tick(); };
            idle.Reset(clock.ElapsedMilliseconds);
        }

        private ToolStripMenuItem Add(string text, Action action)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += delegate { action(); };
            menu.Items.Add(item);
            return item;
        }

        private void Tick()
        {
            try
            {
                recovery.Poll();
                if (!recovery.Pending && sessionTarget != null) FinishSession();
                if (exitRequested)
                {
                    if (!recovery.Pending) { ExitThread(); return; }
                    if ((recovery.State == "failed" || recovery.State == "fallback") && !dialogOpen && window.IsLocalDesktop())
                    {
                        exitRequested = false;
                        if (Ask(DescribeRecovery() + "\n\nExit AgentSleep and leave the saved layout pending for --restore?") == DialogResult.Yes)
                        {
                            ExitThread();
                            return;
                        }
                    }
                }
                window.TryEnableSessionNotifications();
                bool unlocked = window.IsLocalDesktop();
                if (resumeRestore.ShouldRetry(unlocked, recovery.Pending, recovery.State))
                {
                    RequestRestore("Retrying restoration on the unlocked local desktop.");
                    return;
                }
                if (!unlocked)
                {
                    Interrupt("An unlocked local desktop is required.");
                    return;
                }

                if (recovery.Pending)
                {
                    if (SessionPowerMustRelease())
                    {
                        SetPower(false);
                        return;
                    }
                    if (restoreRequested || (recovery.State != "trial" && recovery.State != "active")) return;
                    if (!HasRestoreHotkey() || !SetPower(true))
                    {
                        RequestRestore("The restore hotkey or Windows power request became unavailable.");
                        return;
                    }
                    VerifySession();
                    return;
                }

                if (qualifyingTrial && !dialogOpen) ConfirmTrial();
                if (!settings.Timer || paused || dialogOpen || !settings.Qualified || !HasRestoreHotkey())
                {
                    SetPower(false);
                    return;
                }

                uint tick, input;
                long now = clock.ElapsedMilliseconds;
                if (!TrayWindow.TryGetLastInput(out tick, out input) || EligibilityError() != "")
                {
                    idle.Reset(now);
                    SetPower(false);
                    return;
                }
                if (waitingForInput)
                {
                    if (!restoreInput.HasValue) restoreInput = input;
                    if (restoreInput.Value == input) { SetPower(false); return; }
                    waitingForInput = false;
                    idle.Reset(now);
                }
                if (SetPower(true) && idle.IsDue(tick, input, now, settings.Minutes * 60000)) StartSession(true);
            }
            catch (Exception error)
            {
                paused = true;
                notice = error.Message;
                if (!restoreRequested && recovery.Pending) RequestRestore("An unexpected session check failed. " + error.Message);
                else if (!recovery.Pending || SessionPowerMustRelease()) SetPower(false);
            }
        }

        private bool SetPower(bool required)
        {
            if (window.RequestPower(required)) return true;
            powerFailed = true;
            paused = true;
            notice = "Windows rejected the display and system power request. The idle timer is paused.";
            return false;
        }

        private bool SessionPowerMustRelease()
        {
            return SessionPowerMustRelease(sessionTarget != null && recovery.OwnsRecord, exitRequested, recovery.State);
        }

        internal static bool SessionPowerMustRelease(bool ownsSession, bool exiting, string state)
        {
            return !ownsSession || exiting || state == "fallback" || state == "failed";
        }

        private void RequireEntryPower()
        {
            if (!window.IsLocalDesktop() || !HasRestoreHotkey() || !SetPower(true))
                throw new InvalidOperationException("The local desktop, restore hotkey, or Windows power request is unavailable.");
        }

        private void Interrupt(string reason)
        {
            resumeRestore.Interrupt(recovery.Pending);
            ResetAfterInterruption();
            if (!restoreRequested && recovery.Pending) RequestRestore(reason);
        }

        private void ResetAfterInterruption()
        {
            SetPower(false);
            idle.Reset(clock.ElapsedMilliseconds);
            inventoryAfter = 0;
        }

        private void RefreshInventory(bool force)
        {
            long now = clock.ElapsedMilliseconds;
            if (!force && now < inventoryAfter) return;
            inventoryAfter = now + 3000;
            try
            {
                inventory = displays.Discover();
                inventoryError = "";
            }
            catch (Exception error)
            {
                inventory = new DisplayTarget[0];
                inventoryError = "Display discovery failed. " + error.Message;
            }
        }

        private string EligibilityError(bool offerSoleTarget = false)
        {
            if (!window.SessionNotifications) return "Windows session notifications are unavailable. Wait for Windows sign-in to finish and retry.";
            if (!window.IsLocalDesktop()) return "An unlocked local desktop is required.";
            if (!HasRestoreHotkey()) return "The restore hotkey is unavailable. Choose another shortcut in Settings.";
            if (recovery.Detail == "recovery-storage-unavailable" || recovery.Detail == "recovery-journal-unreadable")
                return "AgentSleep cannot access its recovery records. Fix access to its local data and restart the tray.";
            RefreshInventory(false);
            if (inventoryError != "") return inventoryError;
            if (!inventory.Any(TrayDryRun.IsVirtualCandidate)) return "Install and enable a supported virtual display driver separately.";
            if (SelectForStart(inventory, settings.TargetId, offerSoleTarget) == null)
                return "Select one available verified virtual display in Settings.";
            if (!inventory.Any(display => display.Active && display.IsPhysical && !display.IsVirtual) ||
                inventory.Any(display => display.Active && display.IsPhysical == display.IsVirtual))
                return "A classifiable active physical display layout is required.";
            return "";
        }

        internal static DisplayTarget SelectForStart(DisplayTarget[] inventory, string id, bool offerSoleTarget)
        {
            if (!offerSoleTarget || id.Length != 0) return TrayDryRun.Select(inventory, id);
            DisplayTarget[] candidates = inventory.Where(TrayDryRun.IsVirtualCandidate).Take(2).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }

        private bool HasRestoreHotkey()
        {
            return window.HasHotkey(settings.Modifiers, settings.Key);
        }

        private void StartSession(bool automated)
        {
            if (dialogOpen || recovery.Pending || qualifyingTrial) return;
            inventoryAfter = 0;
            string problem = EligibilityError(!automated);
            if (problem != "") { StartFailed(automated, problem); return; }

            DisplayTarget target = SelectForStart(inventory, settings.TargetId, !automated);
            bool trial = !settings.Qualified;
            if (automated && trial) return;
            if (trial)
            {
                string prompt = "Confirm in Windows that " + target.Label + " [" +
                    target.Id.Substring(0, Math.Min(8, target.Id.Length)) + "] is the intended virtual driver display. " +
                    "Stay at the PC with the physical monitors powered on. AgentSleep will arm a 25-second rollback timer before isolating this target.\n\n" +
                    "Restore early with " + settings.Shortcut + ". Wait for the timeout to qualify the target for future sessions.\n\nStart the attended trial?";
                if (Ask(prompt) != DialogResult.Yes) return;
                inventoryAfter = 0;
                problem = EligibilityError(true);
                if (problem != "") { StartFailed(false, problem); return; }
                DisplayTarget confirmed = SelectForStart(inventory, settings.TargetId, true);
                if (confirmed == null || !String.Equals(target.Id, confirmed.Id, StringComparison.OrdinalIgnoreCase))
                {
                    StartFailed(false, "The offered virtual display changed. Review the current target and try again.");
                    return;
                }
                settings.TargetId = confirmed.Id;
            }

            try
            {
                before = BrowserInventory.Read();
                verifiedBounds = null;
                verifiedScreen = null;
                desktopVerified = false;
                observedBrowserVerification = false;
                browserStatus = "Waiting to verify browser window bounds.";
                ownTrial = trial;
                restoreRequested = false;
                sessionTarget = target.Id;
                checkAfter = verifyBy = 0;
                notice = "";
                recovery.Start(target.Id, trial);
                if (!recovery.Pending)
                {
                    FinishSession();
                    if (recovery.State != "restored") StartFailed(automated, DescribeRecovery());
                }
            }
            catch (Exception error)
            {
                if (recovery.Pending) RequestRestore("The session did not start safely. " + error.Message);
                else SetPower(false);
                StartFailed(automated, "The session could not start. " + error.Message);
            }
        }

        private void StartFailed(bool automated, string message)
        {
            notice = message;
            idle.Reset(clock.ElapsedMilliseconds);
            if (automated)
            {
                paused = true;
                if (!recovery.Pending || SessionPowerMustRelease()) SetPower(false);
                icon.ShowBalloonTip(3000, "AgentSleep timer paused", message, ToolTipIcon.Warning);
            }
            else ShowMessage(message);
        }

        private void VerifySession()
        {
            if (restoreRequested) return;
            long now = clock.ElapsedMilliseconds;
            if (verifyBy == 0)
            {
                checkAfter = now + 1500;
                verifyBy = now + 5500;
            }
            if (now < checkAfter) return;
            checkAfter = now + 750;
            if (!displays.IsOnly(sessionTarget))
            {
                recovery.Poll();
                if (recovery.State == "active" || recovery.State == "trial")
                    RequestRestore("The selected display is no longer the only active display.");
                return;
            }

            Screen screen = BrowserInventory.ReadSingleScreen();
            if (verifiedBounds.HasValue && (screen == null || verifiedBounds.Value != screen.Bounds || verifiedScreen != screen.DeviceName))
            {
                RequestRestore("The virtual desktop geometry changed during the session.");
                return;
            }
            BrowserInventory current = BrowserInventory.Read();
            if (screen != null && current.Verify(before, screen))
            {
                before = null;
                verifiedBounds = screen.Bounds;
                verifiedScreen = screen.DeviceName;
                browserStatus = current.Summary();
                desktopVerified = true;
                observedBrowserVerification |= current.Visible.Count > 0;
                verifyBy = now + 4000;
            }
            else
            {
                desktopVerified = false;
                if (now >= verifyBy) RequestRestore("A usable virtual desktop or a supported browser window could not be verified.");
            }
        }

        private void RequestRestore(string reason)
        {
            if (!recovery.Pending) return;
            restoreRequested = true;
            qualifyingTrial = false;
            notice = reason;
            if (SessionPowerMustRelease() || !window.IsLocalDesktop()) SetPower(false);
            try { recovery.Restore(); }
            catch (Exception error)
            {
                SetPower(false);
                notice = reason + " " + error.Message;
            }
        }

        private void FinishSession()
        {
            qualifyingTrial = ownTrial && desktopVerified && !restoreRequested && recovery.State == "restored" &&
                recovery.Detail == "timed-restore-exact" && String.Equals(sessionTarget, settings.TargetId, StringComparison.OrdinalIgnoreCase);
            sessionTarget = null;
            before = null;
            idle.Reset(clock.ElapsedMilliseconds);
            waitingForInput = true;
            uint tick, input;
            restoreInput = TrayWindow.TryGetLastInput(out tick, out input) ? (uint?)input : null;
            inventoryAfter = 0;
            SetPower(false);
        }

        private void ConfirmTrial()
        {
            qualifyingTrial = false;
            string browser = observedBrowserVerification ? "Supported browser window bounds were checked." :
                "This was a recovery-only trial with no supported visible browser window to check.";
            if (Ask("The timed rollback verified the original display layout. " + browser + "\n\n" +
                "Confirm every physical monitor and the original primary display are back in their expected arrangement. " +
                "Verify agent screenshots, clicks, and panel darkness separately with the attended test page.") == DialogResult.Yes)
            {
                settings.QualifiedId = settings.TargetId;
                try { settings.Save(); notice = "Attended trial confirmed for the selected target."; }
                catch (Exception error) { settings.QualifiedId = ""; notice = "Trial confirmation could not be saved. " + error.Message; }
            }
            else notice = "Trial confirmation is still required before a normal session.";
        }

        private void TogglePause()
        {
            if (recovery.Pending) return;
            paused = !paused;
            if (paused) SetPower(false);
            else { powerFailed = false; waitingForInput = false; }
            idle.Reset(clock.ElapsedMilliseconds);
        }

        private void ShowSettings()
        {
            if (dialogOpen || recovery.Pending) return;
            RefreshInventory(true);
            DisplayTarget[] candidates = inventory.Where(TrayDryRun.IsVirtualCandidate)
                .Where(display => TrayDryRun.Select(inventory, display.Id) != null).ToArray();
            bool wasStartup;
            try { wasStartup = TraySettings.StartsWithWindows(); }
            catch (Exception error) { ShowMessage("Windows startup settings could not be read. " + error.Message); return; }

            bool startup;
            TraySettings updated;
            dialogOpen = true;
            try { updated = TraySettings.Edit(settings, candidates, wasStartup, out startup); }
            finally { dialogOpen = false; }
            if (updated == null) return;
            if (!window.TryRegister(updated.Modifiers, updated.Key))
            {
                ShowMessage("Windows could not register " + updated.Shortcut + ". Choose another restore hotkey.");
                return;
            }

            try { updated.Save(); }
            catch (Exception error)
            {
                window.TryRegister(settings.Modifiers, settings.Key);
                paused = true;
                ShowMessage("Settings could not be saved. " + error.Message);
                return;
            }
            paused = paused && settings.Timer && updated.Timer;
            settings = updated;
            powerFailed = false;
            waitingForInput = false;
            notice = "";
            idle.Reset(clock.ElapsedMilliseconds);
            if (startup != wasStartup)
            {
                try { TraySettings.SetStartup(startup); }
                catch (Exception error) { ShowMessage("The Windows startup setting could not be changed. " + error.Message); }
            }
        }

        private string DescribeRecovery()
        {
            switch (recovery.State)
            {
                case "arming": return "Preparing independent rollback.";
                case "trial": return "Attended virtual-display trial running.";
                case "active": return "Virtual-display session active.";
                case "restoring": return "Restoring the saved physical display layout.";
                case "restored": return recovery.Detail == "stopped-before-display-change" ?
                    "The session stopped before any display change." : "Original display layout verified.";
                case "fallback": return "Windows restored a usable fallback; the original layout still needs recovery.";
                case "failed": return "Recovery or startup failed. " + recovery.Detail;
                default: return "No display session is active.";
            }
        }

        private void ShowStatus()
        {
            RefreshInventory(true);
            int physical = inventory.Count(display => display.Active && display.IsPhysical && !display.IsVirtual);
            int virtuals = inventory.Count(TrayDryRun.IsVirtualCandidate);
            DisplayTarget selected = TrayDryRun.Select(inventory, settings.TargetId);
            DisplayTarget sole = SelectForStart(inventory, settings.TargetId, true);
            string details = DescribeRecovery() + "\n\n" +
                (inventoryError == "" ? String.Format("Active physical displays: {0}\nAvailable verified virtual targets: {1}\n", physical, virtuals) : inventoryError + "\n") +
                "Selected target: " + (selected != null ? selected.Label : sole != null ? sole.Label + " is available for attended setup" :
                    "Select an available driver display in Settings") + "\n" +
                "Attended trial: " + (settings.Qualified ? "Confirmed" : "Required for this target") + "\n" +
                "Restore hotkey: " + settings.Shortcut + (HasRestoreHotkey() ? " (registered)" : " (unavailable)") + "\n" +
                "Idle timer: " + (!settings.Timer ? "Off" : paused ? "Paused" : waitingForInput ? "Waiting for new input" :
                    settings.Qualified ? settings.Minutes + " minute(s)" : "Waiting for trial confirmation");
            if (powerFailed) details += "\nWindows power request failed.";
            if (browserStatus != "") details += "\n\n" + browserStatus;
            if (notice != "") details += "\n\n" + notice;
            if (!recovery.Pending && inventoryError == "")
            {
                string problem = EligibilityError(true);
                if (problem != "" && problem != notice) details += "\n\n" + problem;
            }
            ShowMessage(details);
        }

        private DialogResult Ask(string question)
        {
            dialogOpen = true;
            try { return MessageBox.Show(question, "AgentSleep", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2); }
            finally { dialogOpen = false; }
        }

        private void ShowMessage(string message)
        {
            if (dialogOpen) return;
            dialogOpen = true;
            try { MessageBox.Show(message, "AgentSleep", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            finally { dialogOpen = false; }
        }

        private void ExitFromMenu()
        {
            if (!recovery.Pending) { ExitThread(); return; }
            if (exitRequested) return;
            exitRequested = true;
            SetPower(false);
            RequestRestore("Restoring displays before AgentSleep exits.");
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            SetPower(false);
            if (recovery.Pending && !restoreRequested) RequestRestore("AgentSleep is exiting.");
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                icon.Visible = false;
                icon.Dispose();
                menu.Dispose();
                window.Dispose();
                recovery.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class LocalEntryDisplays : IDisplays
        {
            private readonly IDisplays displays;
            private readonly Action requireEntryPower;

            internal LocalEntryDisplays(IDisplays displays, Action requireEntryPower)
            {
                this.displays = displays;
                this.requireEntryPower = requireEntryPower;
            }
            public DisplayTarget[] Discover() { return displays.Discover(); }
            public byte[] Capture() { return displays.Capture(); }
            public void Validate(byte[] baseline, string id) { displays.Validate(baseline, id); }
            public bool IsOnly(string id) { return displays.IsOnly(id); }
            public string Restore(byte[] baseline) { return displays.Restore(baseline); }

            public void Extend(byte[] baseline, string id)
            {
                requireEntryPower();
                displays.Extend(baseline, id);
            }

            public void Isolate(string id)
            {
                requireEntryPower();
                displays.Isolate(id);
            }
        }
    }
}
