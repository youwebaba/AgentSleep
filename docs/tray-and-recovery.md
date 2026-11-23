# Tray controls and recovery

## Prepare at the PC

Use the local, unlocked Windows 11 x64 desktop. Sign in normally. Run the [read-only check](../README.md#read-only-build) first. The tray app runs with your current Windows account. Driver installation is a separate operator task.

1. Put [AgentSleep.exe](../builds/windows-x64/AgentSleep.exe) somewhere you can keep it. Write down its full path and the restore hotkey. The independent watchdog starts another copy of this executable from the same location.
2. Open the app and find **AgentSleep** in the system tray. Its icon may be under the overflow arrow. Open **Settings** to choose a verified virtual target, an idle timeout and a restore hotkey. Start with the idle timer and startup at sign-in off.
3. Use **Status** to check eligibility and the registered hotkey. Multiple verified virtual targets require a selection. Cloned desktops, an unidentifiable active target or an ambiguous primary can prevent the app from recording an exact recovery plan.
4. Open the [local fixture](../fixture/index.html) in a visible browser window. With someone at the PC to observe every monitor, choose **Start now** and approve the timed trial. Let the watchdog restore the display layout. The 25-second budget starts when the helper acknowledges the snapshot and includes the switch itself.
5. Inspect the restored layout and primary. Confirm the successful attended trial only if the observed result agrees. Ordinary sessions and the optional idle timer depend on qualification for the selected target. Replacing the driver or selecting a new target can require another trial.

## What happens during a session

The normal sequence is **Normal → Entering → Virtual-only → Restoring → Normal**. A failed switch requests rollback. Status reports whether the original layout returned, a physical fallback is usable, or recovery still needs attention.

AgentSleep reads the active display paths, validates a recovery snapshot and arms the independent helper. The helper keeps the system awake through the session and recovery, including after the tray exits. The tray activates the selected virtual monitor alongside the original screens, verifies the change, then switches the desktop to that virtual monitor. All switches use temporary Windows display settings. [Windows display configuration API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig)

On restore, AgentSleep reconnects the physical monitors while the virtual screen is still usable. It reads back the saved monitor identities, primary, positions, modes and rotations. Once those match, it disconnects the selected virtual display from the desktop and verifies the result. This also handles a driver installer that had already extended the normal desktop onto its virtual display. The driver stays available for the next session. If AgentSleep cannot verify the physical layout, it keeps a usable output and reports the recovery status. If the selected virtual monitor is your main display, choose another main display in Windows before entry.

Windows handles window placement when it sees the virtual display disconnect. Its **Remember window locations based on monitor connection** and **Minimize windows when a monitor is disconnected** options can affect what you see. Verify the windows you use after the first restore. [Windows multiple-display settings](https://support.microsoft.com/en-us/windows/hardware/display-graphics/how-to-use-multiple-monitors-in-windows)

The tray checks visible top-level windows belonging to supported Chromium and Firefox browsers. It tries to bring off-screen windows into the virtual work area and checks their bounds again. Open the fixture window before entry and leave it visible. Minimized windows, other Windows virtual desktops, elevated apps and other capture/input backends require separate testing. Only the [attended fixture procedure](desktop-trial.md) can establish that the agent can see and operate your browser in darkness.

An enabled timer uses the current session’s Windows last-input time. Input from an agent can postpone entry if Windows counts that input as activity. Once virtual-only, ordinary pointer and keyboard activity do not request restoration. Use **Restore displays** or the configured hotkey. An eligible timer and an active session request that Windows keep the system and display rendering awake. Windows lock, explicit sleep and sign-in remain under Windows control. [Windows idle input](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo) · [Windows execution-state requests](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate)

**Pause timer** suspends automatic entry for the current app run. Turn off the timer in **Settings** to keep it off across restarts. **Exit** requests restoration. Startup at sign-in is opt-in. The status of a display path describes the video signal. Whether each disconnected panel enters standby and stays dark depends on that monitor’s firmware, inputs and settings. Monitor-specific power control is outside this build.

## Restore using the keyboard

1. Press the registered restore hotkey. The default is **Ctrl + Alt + F11**. During the first trial, the watchdog also attempts restoration automatically. Leave it running if the tray process is lost.
2. If the tray is gone or the hotkey is unavailable, press **Win + R** and type the quoted full path to `AgentSleep.exe` followed by ` --restore`. Press **Enter**. From a PowerShell prompt in a checkout, use the following command to wait for the result.

   ```powershell
   (Start-Process -FilePath .\builds\windows-x64\AgentSleep.exe -ArgumentList '--restore' -Wait -PassThru).ExitCode
   ```

   `0` means exact restoration or no outstanding snapshot. `3` means a currently available physical display was recovered and the saved layout still needs attention. `2` means the requested operation failed. Repeat the command after reconnecting the original monitors. Keep the recovery file while a restore is pending.

3. If a physical panel stays dark, check its power and input, reconnect an original monitor, and retry. Once a screen is usable, open **Settings → System → Display** to confirm the layout and primary. If Windows is locked, complete normal sign-in before using desktop commands. If Windows or the graphics driver stalls inside the watchdog’s own restore call, normal Windows recovery or restart may be necessary. AgentSleep uses temporary display changes.

Settings are stored under `%LOCALAPPDATA%\AgentSleep`. The recovery subdirectory holds the same-session snapshot while needed and a small diagnostic log of transition times and reason codes. The fixture keeps its test input and event timing separately in the local page. The app does not collect screenshots, window titles, URLs, keystrokes or clipboard contents.
