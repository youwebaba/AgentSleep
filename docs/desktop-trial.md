# Attended desktop trial

Keep the operator at the PC for every display change in this procedure. Start with the read-only inventory, a working restore hotkey and the standalone restore command within keyboard reach. Run the first switch as a timed trial. Let the watchdog return the desktop once before testing longer sessions.

## Set up one browser session

1. Record which physical monitor Windows marks as primary in **Settings → System → Display**. Note each monitor’s position, orientation, resolution and refresh rate. A small vertical offset matters. Keep any equipment identifiers and screenshots on the machine.
2. Open [the local fixture](../fixture/index.html) in a normal, visible browser window on the primary screen. Use the same tab, browser profile, desktop agent, screenshot method and input method for all three runs. Record which agent/browser pair you used. Keep private pages out of view.
3. Start a fresh fixture run before each condition. It supplies a different test string and a visible clock. Have the operator record the UTC time of the last panel entering standby and the first panel waking. Use the monitors’ standby indicators or another independent observation. Record “dark, power state unknown” if the hardware offers no way to tell.

## Run the three conditions

| Condition | Prepare the desktop | What the operator observes |
| --- | --- | --- |
| Physical panels on | Exit AgentSleep. Keep all physical panels on. | Confirm that the normal agent can see the current screen and that the page receives its click and text. |
| Normal Windows idle-off | Keep AgentSleep closed and let Windows reach its own display idle timeout. If it is set to **Never**, an operator may choose a finite display timeout for this control and put the original value back afterward. | Note whether the agent wakes a panel, whether input arrives while all panels are dark, and whether any event first appears after wake. |
| AgentSleep virtual-only | With approval at the PC, start an attended trial with the same browser tab already open. Wait for all physical panels to enter standby. | Remain by the panels through both captures, the click and text entry. Note any panel that stays lit, shows a no-signal message, wakes, or requires its power button. |

In every run, have the agent take a desktop screenshot and read the current clock and challenge from the pixels. Have it click the fixture’s counter, focus the input and type that exact challenge. Take a second desktop screenshot after the clock advances. Verify a new clock reading, the advanced counter and a text match. Record the fixture’s click-received and text-match times. Compare those times with the operator’s observation interval. If time expires or a panel wakes first, keep that outcome and start a new run with a new challenge.

A virtual-only pass requires both browser events and a fresh capture while **every** physical panel is still dark. A delayed event timestamp after wake directly answers the reported symptom. Headless runs, DOM automation, script-generated events and a screenshot by itself cannot establish this result. A run requiring a monitor’s power button can establish input compatibility, but leaves automatic standby on signal loss unverified for that monitor.

## Verify recovery

After the automatic rollback, compare the original primary, all three physical targets, positions, modes and orientations against the baseline. In separate approved trials, exercise the restore hotkey, the standalone restore command and loss of the tray process while leaving the watchdog running. Record whether recovery was exact or whether only one available physical screen returned. A physical fallback keeps a screen usable and requires a separate check of the original layout.

| Condition | Run ID | Clock before / after | Click received UTC | Text matched UTC | All dark from / until UTC | Standby / wake observation | Original layout and primary |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Panels on | `2EDB80454515AD82` | 23:10:31.160 / 23:11:10.148 | 23:10:35.933 | 23:11:10.056 | N/A | Operator confirmed all three panels stayed on | Original physical layout recorded before driver installation |
| Windows idle-off, first observation | Fixture still on baseline run | 23:18:46.958 on both off-state captures | No new counter event | No new match | Windows reported off at 23:18:47.005. Physical times unrecorded. | Operator saw all three dark. The agent could not activate Edge before or during this interval. | All three identities, modes, positions and primary matched afterward |
| AgentSleep virtual-only | Pending | — | — | — | — | — | — |

The November 23, 2026 baseline used Codex Windows Computer Use with Windows.Graphics.Capture and desktop input in a headed Microsoft Edge window. The counter advanced from zero to one and the fresh screenshot showed the exact challenge match. Times in the table are UTC. Keep screenshots and private run notes outside the repository. One agent/browser/capture combination establishes a result for that combination.

For the first idle-off observation, AgentSleep was closed. An independent guard acknowledged readiness, then the AC display timeout changed from Never to 60 seconds. Windows reported off almost immediately because the desktop was already idle. The clock visible in both captures was the last frame before that notification. The agent reported “failed to activate captured window” with the panels on and again during idle-off. A fresh fixture run could not be started, so this interval supplies no click or text latency measurement. The guard restored the original timeout at 23:19:38.805 UTC. Both AC and DC read back as Never. A separate display-wake request brought the session display status back to on.
