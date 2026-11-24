# AgentSleep

AgentSleep is a standalone Windows 11 tray app for desktop agents. It moves the unlocked desktop onto a signed virtual display so physical monitors can enter standby while an agent continues taking screenshots, clicking and typing.

**Start with the read-only build.** On a three-monitor PC, all three physical panels stayed in standby while Codex clicked and typed in Edge. The first trial also showed why recovery must disconnect the virtual display after the physical monitors return. [Attended hardware results](docs/desktop-trial.md)

## Install on Windows

Download and run `AgentSleep-Setup-v1.0-windows-x64.exe` on Windows 11 x64. Setup installs AgentSleep for the current account in `%LOCALAPPDATA%\Programs\AgentSleep` and adds a Start menu entry. To uninstall, restore the displays, exit AgentSleep, then select **AgentSleep** in **Settings → Apps → Installed apps**. [AgentSleep v1.0 release](https://github.com/youwebaba/AgentSleep/releases/tag/v1.0)

Install and enable the supported signed virtual display driver separately, then run the included `AgentSleep.DryRun.exe` in PowerShell from the install folder and inspect its results before starting a session. [Driver prerequisite](#driver-prerequisite)

## Read-only build

Run [AgentSleep.DryRun.exe](builds/windows-x64/AgentSleep.DryRun.exe) from PowerShell on the local Windows desktop. The executable can inspect and validate a proposed display configuration. It has no command for applying one.

```powershell
.\builds\windows-x64\AgentSleep.DryRun.exe
```

Inventory reports active physical displays and available verified virtual targets. It asks Windows to validate reconstruction of the current physical layout even when no virtual target is installed. When a supported target is present, copy its full ID from inventory and pass it with `--target` to validate the proposed topology. The ID identifies a device independently of Windows’ monitor numbering. Treat discovery details as local equipment information. An inventory exit code of `0` means the check ran. Read its prerequisite and validation results before attempting a trial.

To build from source and run the mocked display and recovery tests, use a Windows 11 x64 checkout with the inbox .NET Framework C# compiler. Build output goes into `build`.

```powershell
.\build.ps1 -DryRunOnly -Test
```

## Tray build

After reviewing the dry run, launch **AgentSleep** from Start or open the [portable tray executable](builds/windows-x64/AgentSleep.exe). Right-click the tray icon and choose **Start now**. If Windows lists several verified virtual displays, choose one in **Settings**. The first session for a target requires local confirmation and a timed rollback. **Restore displays**, the restore hotkey and the independent watchdog bring the physical monitors back first, verify their layout, then disconnect the selected virtual display. The driver stays installed.

The restore hotkey defaults to **Ctrl+Alt+F11**. Idle entry and startup at sign-in are off by default. If you enable idle entry, its default interval is 10 minutes. Use **Settings** to change these options or **Pause timer** to suspend automatic entry for the current run.

To open **Start now** on your primary screen, run `AgentSleep.exe --prompt`. Opening the prompt leaves the displays as they are until you choose **Start now**. Escape cancels.

To build the tray app and run all native-project tests from the repository root, use PowerShell. The executable is `build\AgentSleep.exe`. The build needs no NuGet packages or .NET SDK.

```powershell
.\build.ps1 -Test
```

To build the v1.0 installer, run the packaging script from the repository root with PowerShell 7 and NSIS installed. Pass `-MakeNsis` with the path to `makensis.exe` if needed. The output is `build\AgentSleep-Setup-v1.0-windows-x64.exe`.

```powershell
pwsh -NoProfile -File .\installer\build-installer.ps1
```

Run `pwsh -NoProfile -File .\installer\test-installer.ps1` to smoke-test setup and uninstall from a Windows account with no existing AgentSleep installation, Start menu folder, or uninstall registration.

Read the [tray and keyboard recovery guide](docs/tray-and-recovery.md) before the first switch. The [three-condition trial](docs/desktop-trial.md) uses a [self-contained browser fixture](fixture/index.html). Open the HTML file from a local checkout so the browser runs it as a page.

## Driver prerequisite

The switch requires an already-installed, enabled, supported signed indirect display driver with a virtual monitor available in the interactive session. AgentSleep recognizes the **VirtualDrivers Virtual Display Driver 25.7.23 driver-only package**. For x64 Windows the upstream asset is named `VirtualDisplayDriver-x86.Driver.Only.zip`. Keep Windows signature enforcement enabled. AgentSleep binds the monitor to its adapter and checks its registered device and the hashes of the known signed package. This driver can report its connector as HDMI. Other driver versions and indirect USB monitors require separate verification. [Upstream signed package](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23) · [Driver source](https://github.com/VirtualDrivers/Virtual-Display-Driver/blob/25.7.23/Virtual%20Display%20Driver%20%28HDR%29/MttVDD/Driver.cpp)

The original Python discovery tool and its tests remain under [prototype](prototype/README.md). It is useful for inspecting raw Windows paths. The native build checks the installed driver before identifying a virtual target.

## Results

| Check | Evidence on the three-monitor Windows 11 PC |
| --- | --- |
| Prerequisites and recovery | Read-only checks saw zero virtual targets before driver installation and one verified target afterward. Windows validated both layouts. The tray exposed all six commands without elevation. Display and tray tests, 21 recovery cases, and eight prototype tests passed. |
| Physical panels on | Codex captured a fresh clock, clicked the fixture and typed a matching challenge in headed Edge. The operator saw all three panels on. |
| Normal Windows idle-off | Edge received both inputs on the repeat. All three panels stayed dark through typing. Windows reported the session display on 21 ms before the click. The earlier off-state capture showed a frozen clock. |
| AgentSleep virtual-only | The same Edge session received a click and exact text on the 800 × 600 virtual desktop. All three panels showed standby indicators during the first run. The repeat confirmed darkness through both events. |
| One-minute AgentSleep timer | Verified virtual-only at 61.9 seconds of Windows input idle. The safety deadline ended this run before a new challenge was completed. The watchdog restored the displays and both Edge windows. |
| Keyboard restore | The registered **Ctrl+Alt+F11** arrived 22.2 seconds into a four-display-start trial. The original three physical paths and primary returned with the virtual path inactive. A maximized window needed the subsequent placement correction. |
| Standalone restore | **Verified on the final build.** `--restore` returned `0`. The original three monitors, primary, and both Edge windows' locations and show states matched. The operator confirmed the restored desktop visually. The signed virtual display remained available and inactive. |
| Tray-process loss | After only the tray was stopped, its watchdog restored all three original physical monitors and both Edge windows to their original screens, rectangles and show states. |
| Remaining hardware coverage | Physical cable removal, lock, suspend/resume and other agent/browser combinations have not been exercised live. |

The [attended trial record](docs/desktop-trial.md) includes event times, operator observations, the five-second return, and window-placement corrections.

The previously reported clicks that appeared to take effect at wake, and a separate successful screenshot, are observations from before this build. The fixture records when the browser actually receives input so those timestamps can be compared with physical panel observations.

## License

AgentSleep is licensed under the PolyForm Noncommercial License 1.0.0. It permits use and modification for noncommercial purposes and the other uses it specifies, including use by certain organizations regardless of funding. Commercial use outside those permissions requires a separate license. Redistribution requires passing along the license text or its URL and any required notices. [License text](LICENSE) · [PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0)

The separately installed Virtual Display Driver project uses the MIT License. [Virtual Display Driver license](https://github.com/VirtualDrivers/Virtual-Display-Driver/blob/25.7.23/LICENSE)
