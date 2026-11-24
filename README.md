# AgentSleep

AgentSleep is a standalone Windows 11 tray app for desktop agents. It moves the unlocked desktop onto a signed virtual display so physical monitors can enter standby while an agent continues taking screenshots, clicking and typing.

**Start with the read-only build.** On a three-monitor PC, all three physical panels stayed in standby while Codex clicked and typed in Edge. The first trial also showed why recovery must disconnect the virtual display after the physical monitors return. [Attended hardware results](docs/desktop-trial.md)

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

After reviewing the dry run, open [AgentSleep.exe](builds/windows-x64/AgentSleep.exe). **Start now** begins manual setup. The first session for a target requires local confirmation and a timed rollback. **Restore displays**, the restore hotkey and the independent watchdog all bring the physical monitors back first, verify their layout, then disconnect the selected virtual display. The driver stays installed. Startup with Windows and idle entry both default to off.

To open **Start now** on your primary screen, run `AgentSleep.exe --prompt`. Opening the prompt leaves the displays as they are until you choose **Start now**. Escape cancels.

To build the tray app and run all native-project tests from the repository root, use PowerShell. The executable is `build\AgentSleep.exe`. The build needs no NuGet packages or .NET SDK.

```powershell
.\build.ps1 -Test
```

Read the [tray and keyboard recovery guide](docs/tray-and-recovery.md) before the first switch. The [three-condition trial](docs/desktop-trial.md) uses a [self-contained browser fixture](fixture/index.html). Open the HTML file from a local checkout so the browser runs it as a page.

## Driver prerequisite

The switch requires an already-installed, enabled, supported signed indirect display driver with a virtual monitor available in the interactive session. AgentSleep recognizes the **VirtualDrivers Virtual Display Driver 25.7.23 driver-only package**. For x64 Windows the upstream asset is named `VirtualDisplayDriver-x86.Driver.Only.zip`. Keep Windows signature enforcement enabled. AgentSleep binds the monitor to its adapter and checks its registered device and the hashes of the known signed package. This driver can report its connector as HDMI. Other driver versions and indirect USB monitors require separate verification. [Upstream signed package](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23) · [Driver source](https://github.com/VirtualDrivers/Virtual-Display-Driver/blob/25.7.23/Virtual%20Display%20Driver%20%28HDR%29/MttVDD/Driver.cpp)

The original Python discovery tool and its tests remain under [prototype](prototype/README.md). It is useful for inspecting raw Windows paths. The native build checks the installed driver before identifying a virtual target.

## Results

| Check | Result on November 23, 2026 |
| --- | --- |
| Native Windows inventory and recovery preflight | **Verified before driver installation.** The read-only build found three active physical DisplayPort targets and zero verified virtual targets. Windows accepted the reconstructed three-monitor layout as a rollback candidate using `SDC_VALIDATE`. |
| Inert tray and standalone restore | **Verified on Windows without elevation.** A harness opened the production tray context, found all six commands, checked hotkey registration and the missing-driver refusal, and exited through the menu. A separate `--restore` with no pending snapshot returned `0`. The active layout matched before and after. |
| Prerequisites and layout checks | **Passed with mocks.** Missing, ambiguous, stale and untrusted targets were refused. Synthetic layout checks covered primary identity, negative coordinates, a one-pixel offset, rotation and refresh. |
| Watchdog and restore | **16 recovery cases passed with real helper processes and simulated displays and power requests.** These cover arming, timed rollback, tray or watchdog loss, stalled heartbeat, hung owner, interrupted journal publication, cancellation, failed activation, manual restore, fallback and retry. Logging tests distinguish who requested a restore and exercise recovery with the log unavailable. |
| Browser fixture | **Verified in isolation.** The page generates fresh challenges and records click and exact text-match times. |
| Desktop-agent baseline with panels on | **Verified on three physical monitors.** The operator observed all three on. In headed Edge the agent read the live clock, clicked the counter and typed the fresh challenge. The browser received both events. [Trial record](docs/desktop-trial.md) |
| Windows idle-off | **Click and exact text received in Edge.** The operator saw all three panels stay dark through typing. Windows reported the session display on 21 ms before the browser received the click. The pre-click capture showed the clock at the off notification. Both timeout values returned to Never. [Trial record](docs/desktop-trial.md) |
| Signed driver and switch preflight | **Verified on hardware.** After the operator installed the supported driver, AgentSleep identified one active virtual target. Windows accepted the proposed virtual-only topology and the four-target rollback snapshot. The three original physical monitor identities, positions, modes and primary matched the pre-install baseline. |
| AgentSleep virtual-only | **Passed on this PC with Codex and Edge.** The same Edge window on the 800 × 600 virtual desktop received a click and matched a fresh challenge. The agent captured an advancing clock and the result. The operator confirmed all three physical panels stayed dark with standby indicators throughout the inputs. [Trial record](docs/desktop-trial.md) |
| First timed hardware recovery, earlier build | The watchdog restored the original physical layout and also reactivated the installer's fourth display. Edge was out of reach on the physical desktop. Disconnecting that virtual display left all three original physical monitors active and both Edge windows visible on physical screens. |
| Physical-first restore and the five-second return | **Observed on hardware.** Windows reported all three original monitors and the original primary before the selected virtual path disconnected. In a subsequent run Windows minimized both Edge windows on entry, triggering the earlier build's browser-check timeout. Both regained their exact physical placements on return. The updated tray reopens previously visible browsers on the virtual screen and verifies their bounds. [Recovery observations](docs/desktop-trial.md#follow-up-recovery-checks) |
| Keyboard-only restore, standalone recovery and loss of the tray process on hardware | Separate attended checks pending. |

The previously reported clicks that appeared to take effect at wake, and a separate successful screenshot, are observations from before this build. The fixture records when the browser actually receives input so those timestamps can be compared with physical panel observations.
