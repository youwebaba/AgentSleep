# AgentSleep

AgentSleep is a standalone Windows 11 utility for screen-based agents. Its goal is to let physical monitors enter standby while an unlocked Windows desktop stays available for screenshots, clicks and typing. Neither Faber nor a streaming service is required.

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

## Driver prerequisite

The switch requires an already-installed, enabled, supported signed indirect display driver with a virtual monitor available in the interactive session. AgentSleep recognizes the **VirtualDrivers Virtual Display Driver 25.7.23 driver-only package**. For x64 Windows the upstream asset is named `VirtualDisplayDriver-x86.Driver.Only.zip`. Keep Windows signature enforcement enabled. AgentSleep binds the monitor to its adapter and checks its registered device and the hashes of the known signed package. This driver can report its connector as HDMI. Other driver versions and indirect USB monitors require separate verification. [Upstream signed package](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23) · [Driver source](https://github.com/VirtualDrivers/Virtual-Display-Driver/blob/25.7.23/Virtual%20Display%20Driver%20%28HDR%29/MttVDD/Driver.cpp)

The original Python discovery tool and its tests remain under [prototype](prototype/README.md). It is useful for inspecting raw Windows paths. The native build checks the installed driver before identifying a virtual target.

## Hardware check

On the three-monitor Windows 11 PC, the native build reported three active physical targets and zero verified virtual targets. Windows accepted a read-only validation of the reconstructed physical topology. The reported browser interactions that took effect after panel wake and the separate successful screenshot motivate the controlled test. Physical standby, live browser input in darkness and exact recovery on this hardware await an attended trial.
