# AgentSleep

Keep your agents working with your monitors off.

AgentSleep is a planned standalone Windows utility for screen-based AI agents and desktop automation. The goal is to let every physical monitor turn fully off while Windows continues rendering an interactive desktop that a compatible agent can screenshot and click. No dimming, black overlay, or streaming service required.

**Status:** Design stage. There is no installer or working release yet.

## How it would work

1. Save the current physical display layout.
2. Switch Windows to an active virtual display. The physical monitors can then enter standby or be switched off without removing the agent's working display.
3. Restore the physical layout on demand or when the session timer expires.

The first prototype will test an existing [virtual display driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), rather than write a new one. AgentSleep will not require Faber or any specific agent. It will not unlock Windows, bypass security policy, or make a sleeping PC available for desktop automation. Turning off the panels does not secure an unlocked session.

## First milestone

- On one Windows 11 PC, start with a harmless local page that visibly changes when a button is clicked.
- Switch to the virtual display and physically turn off every monitor.
- Have the target agent take a fresh screenshot, click the button, and verify the change in a second screenshot.
- Confirm the original monitor layout comes back with the restore hotkey and after a timed rollback. Test recovery if the app exits unexpectedly.

Agent, capture method, GPU, and monitor compatibility are not yet verified.
