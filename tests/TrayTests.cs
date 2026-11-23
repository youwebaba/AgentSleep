using System;
using System.Drawing;

namespace AgentSleep
{
    internal static class TrayTests
    {
        internal static void Run()
        {
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

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("Tray check failed: " + label);
        }
    }
}
