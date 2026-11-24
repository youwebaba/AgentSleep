using System;
using System.Drawing;

namespace AgentSleep
{
    internal static class TrayTests
    {
        internal static void Run()
        {
            CheckPrompts();
            CheckMinimizedBrowsers();
            CheckRestoredBrowserPlacement();
            var idle = new IdleClock();
            idle.Reset(0);
            Check(!idle.IsDue(400000, 100000, 0, 360000), "first idle sample");
            Check(!idle.IsDue(459999, 100000, 59999, 360000), "initial grace");
            Check(idle.IsDue(460000, 100000, 60000, 360000), "preserve previously observed idle time");
            Check(!idle.IsDue(460001, 5000, 60001, 360000), "old injected timestamp resets idle");
            Check(!idle.IsDue(820000, 5000, 420000, 360000), "wait for full interval after observed input");
            Check(idle.IsDue(820001, 5000, 420001, 360000), "idle after last observed activity");

            idle.Reset(420001);
            uint beforeWrap = UInt32.MaxValue - 120000;
            Check(!idle.IsDue(80000, beforeWrap, 420001, 60000), "resume grace even after tick wrap");
            Check(idle.IsDue(140000, beforeWrap, 480001, 60000), "32-bit idle tick wraps");
            Check(!idle.IsDue(140001, 145000, 480002, 60000), "future tick fails closed");
            Check(!idle.IsDue(145000, 145000, 485001, 60000), "future tick does not trigger after clock catches up");

            var resume = new ResumeRestore();
            resume.Interrupt(true);
            Check(!resume.ShouldRetry(false, true, "failed"), "wait while locked");
            Check(!resume.ShouldRetry(true, true, "restoring"), "wait for in-flight restore on unlock");
            Check(resume.ShouldRetry(true, true, "failed"), "retry failed restore once on unlocked desktop");
            Check(!resume.ShouldRetry(true, true, "fallback"), "no repeating restore retry loop");
            resume.Interrupt(true);
            Check(!resume.ShouldRetry(true, false, "restored"), "clear retry after exact restore");
            Check(!resume.ShouldRetry(true, true, "failed"), "prior interruption cannot affect new record");

            Check(!TrayContext.SessionPowerMustRelease(true, false, "restoring"), "owned pending restore retains existing power");
            Check(TrayContext.SessionPowerMustRelease(false, false, "restoring"), "unowned restore does not retain power");
            Check(TrayContext.SessionPowerMustRelease(true, true, "restoring"), "explicit exit releases power");
            Check(TrayContext.SessionPowerMustRelease(true, false, "fallback"), "fallback releases power");
            Check(TrayContext.SessionPowerMustRelease(true, false, "failed"), "failed restore releases power");

            var first = new DisplayTarget { Id = "first", Available = true, IsVirtual = true };
            var second = new DisplayTarget { Id = "second", Available = true, IsVirtual = true };
            Check(TrayContext.SelectForStart(new[] { first }, "", true) == first, "one target may be offered for manual trial");
            Check(TrayContext.SelectForStart(new[] { first }, "", false) == null, "idle start needs saved target");
            Check(TrayContext.SelectForStart(new[] { first, second }, "", true) == null, "multiple targets need selection");
            Check(TrayContext.SelectForStart(new[] { first }, "stale", true) == null, "stale selection cannot fall back");

            var area = new Rectangle(-1920, 0, 1920, 1040);
            var screen = new Rectangle(-1920, 0, 1920, 1080);
            Check(BrowserInventory.IsReachable(new Rectangle(-1500, 100, 800, 600), area, screen), "reachable on a negative-origin monitor");
            Check(!BrowserInventory.IsReachable(new Rectangle(40, 100, 800, 600), area, screen), "entirely off-screen window");
            Check(!BrowserInventory.IsReachable(new Rectangle(-1200, -500, 800, 750), area, screen), "off-screen title bar");
            Check(!BrowserInventory.IsReachable(new Rectangle(-400, 10, 800, 750), area, screen), "partly off-screen on the right");
            Check(!BrowserInventory.IsReachable(new Rectangle(-2020, 10, 800, 750), area, screen), "partly off-screen on the left");
            Check(!BrowserInventory.IsReachable(new Rectangle(-500, 10, 100, 60), area, screen), "insufficient usable browser area");
            Check(BrowserInventory.IsReachable(screen, area, screen), "fullscreen browser over the taskbar");
            var primary = new Rectangle(0, 0, 1920, 1040);
            Check(!BrowserInventory.IsReachable(new Rectangle(1600, 10, 1000, 900), primary, primary), "mostly hidden on the primary screen");
        }

        private static void CheckRestoredBrowserPlacement()
        {
            var expected = new BrowserInventory.PlacementGeometry {
                DisplayId = "original-right", Show = 1, Dpi = 144,
                Frame = new Rectangle(2640, 70, 1300, 900), Normal = new Rectangle(2632, 70, 1316, 908)
            };
            var observed = expected;
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.Verified,
                "idempotent exact browser placement");
            observed.DisplayId = "wrong-physical";
            observed.Frame = new Rectangle(24, 40, 784, 543);
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.Move,
                "normal browser moves back to its original monitor and size");
            observed = expected;
            observed.Dpi = 96;
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.Move,
                "geometry is not verified until the original DPI is observed");

            expected.Show = 3;
            expected.Frame = new Rectangle(2560, 0, 2560, 1392);
            observed = expected;
            observed.Show = 2;
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.Normalize,
                "originally maximized browser is normalized before relocating from minimized");
            observed = expected;
            observed.Normal.Offset(-2500, 0);
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.Normalize,
                "maximized rectangle alone does not verify its normal placement");
            observed = expected;
            observed.DisplayId = "wrong-physical";
            observed.Frame.Offset(-2560, 0);
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.Normalize,
                "a maximized window on the wrong display must normalize even with the right stored normal rectangle");
            observed.Show = 1;
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.MoveNormal,
                "matching native normal placement cannot hide a wrong visible monitor");
            observed.DisplayId = expected.DisplayId;
            observed.Normal.Offset(-24, 0);
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.MoveNormal,
                "right monitor alone does not authorize maximizing");
            observed.Normal = expected.Normal;
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.Maximize,
                "only confirmed target and native normal placement allow maximizing");
            observed = expected;
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.Verified,
                "fresh visible maximized frame verifies the completed relocation");
            var progress = new BrowserInventory.MaximizedRelocation();
            Check(progress.TryBegin(BrowserInventory.PlacementAction.Normalize) && progress.TryBegin(BrowserInventory.PlacementAction.Normalize) &&
                !progress.TryBegin(BrowserInventory.PlacementAction.Normalize), "normalize produces bounded native requests");
            Check(progress.TryBegin(BrowserInventory.PlacementAction.MoveNormal) && progress.TryBegin(BrowserInventory.PlacementAction.Maximize),
                "successful normalization still permits a move and maximize");
            progress.MaximizePosted = true;
            Check(!progress.TryBegin(BrowserInventory.PlacementAction.Maximize) && !progress.TryBegin(BrowserInventory.PlacementAction.Normalize),
                "posted maximize waits for readback without repeating or cycling");

            expected.Show = 1;
            expected.Fullscreen = expected.Borderless = true;
            observed = expected;
            observed.Fullscreen = observed.Borderless = false;
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.Unavailable,
                "exited native fullscreen is not recreated with browser input");
            observed.Borderless = true;
            Check(BrowserInventory.DecidePlacement(expected, observed) == BrowserInventory.PlacementAction.Move,
                "a still borderless fullscreen browser can move to its saved monitor");

            var monitor = new Rectangle(-2560, 0, 2560, 1440);
            var workBefore = new Rectangle(-2560, 0, 2560, 1392);
            var workAfter = new Rectangle(-2512, 0, 2512, 1440);
            Check(BrowserInventory.WorkspaceAdjustment(monitor, workBefore, monitor, workAfter) == new Point(-48, 0),
                "workspace rectangle shifts against a taskbar inset on a negative origin monitor");
        }

        private static void CheckMinimizedBrowsers()
        {
            var wasVisible = new IntPtr(101);
            var wasMinimized = new IntPtr(102);
            var newWindow = new IntPtr(103);
            var browser = new BrowserInventory.BrowserWindow(10, 20, 30);
            var before = new BrowserInventory();
            before.Visible[wasVisible] = browser;
            before.Minimized[wasMinimized] = browser;

            var after = new BrowserInventory();
            after.Minimized[wasVisible] = new BrowserInventory.BrowserWindow(10, 20, 30);
            after.Minimized[wasMinimized] = browser;
            after.Minimized[newWindow] = browser;
            int attempts = 0;
            Func<IntPtr, BrowserInventory.BrowserWindow, bool> show = delegate(IntPtr window, BrowserInventory.BrowserWindow observed)
            {
                Check(window == wasVisible && observed.Same(browser), "only the same previously visible browser gets shown");
                return ++attempts > 1;
            };
            Check(!after.VerifyInitialVisibility(before, show) && attempts == 1, "failed async request waits for retry");
            Check(!after.VerifyInitialVisibility(before, show) && attempts == 2, "accepted async request still needs fresh readback");
            Check(!after.VerifyInitialVisibility(before, show) && attempts == 2, "accepted requests are not posted again");
            var readback = new BrowserInventory();
            readback.Visible[wasVisible] = browser;
            readback.Minimized[wasMinimized] = browser;
            Check(readback.VerifyInitialVisibility(before, show) && attempts == 2, "fresh visible readback completes entry verification");

            var original = new BrowserInventory();
            original.Visible[wasVisible] = browser;
            var replacement = new BrowserInventory();
            foreach (BrowserInventory.BrowserWindow reused in new[] {
                new BrowserInventory.BrowserWindow(11, 20, 30),
                new BrowserInventory.BrowserWindow(10, 21, 30),
                new BrowserInventory.BrowserWindow(10, 20, 31) })
            {
                replacement.Minimized[wasVisible] = reused;
                Check(!replacement.VerifyInitialVisibility(original, show) && attempts == 2, "reused HWND does not get shown or match readback");
            }
            Check(!new BrowserInventory().VerifyInitialVisibility(original, show) && attempts == 2, "closed window stays a verification failure");
        }

        private static void CheckPrompts()
        {
            PromptMode mode;
            Check(TrayPrompt.TryParse(new[] { "--prompt" }, out mode) && mode == PromptMode.General, "generic prompt entry");
            Check(TrayPrompt.TryParse(new[] { "--prompt", "hotkey" }, out mode) && mode == PromptMode.Hotkey, "hotkey prompt entry");
            Check(TrayPrompt.TryParse(new[] { "--prompt", "standalone" }, out mode) && mode == PromptMode.Standalone, "standalone prompt entry");
            Check(TrayPrompt.TryParse(new[] { "--prompt", "tray-loss" }, out mode) && mode == PromptMode.TrayLoss, "tray-loss prompt entry");
            Check(!TrayPrompt.TryParse(new[] { "--prompt", "hotkey", "--start" }, out mode), "reject automatic start argument");
            Check(!TrayPrompt.HasExpired(PromptMode.General, 80000) && !TrayPrompt.HasExpired(PromptMode.Hotkey, 79999) &&
                TrayPrompt.HasExpired(PromptMode.Hotkey, 80000), "test prompts expire and generic prompts stay open");

            int starts = 0;
            Action accept = delegate { starts++; };
            var work = new Rectangle(0, 0, 1920, 1040);
            using (var prompt = new TrayPrompt(PromptMode.General, work, "Ctrl+Alt+F11", "", accept))
            {
                Check(prompt.TopMost && object.ReferenceEquals(prompt.AcceptButton, prompt.CancelButton), "foreground prompt defaults to Cancel");
                prompt.Activate();
                prompt.Activate();
                Check(starts == 0, "re-presenting the same prompt does not start a session");
                prompt.Choose(false);
                Check(starts == 0, "Cancel has no start effect");
            }
            using (var prompt = new TrayPrompt(PromptMode.General, work, "Ctrl+Alt+F11", "Target unavailable.", accept))
                prompt.Choose(true);
            Check(starts == 0, "unavailable target cannot start from the prompt");
            using (var prompt = new TrayPrompt(PromptMode.General, work, "Ctrl+Alt+F11", "", accept))
            {
                prompt.Choose(true);
                prompt.Choose(true);
            }
            Check(starts == 1, "affirmative action dispatches exactly once");
        }

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("Tray check failed: " + label);
        }
    }
}
