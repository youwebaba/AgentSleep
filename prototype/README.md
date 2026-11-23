# AgentSleep prototype v0: read-only discovery

A small Python 3 CLI reads real Windows DisplayConfig paths, resolves monitor device
paths, and prints a dry-run plan. It does not set display configuration, install drivers,
change power, register global hotkeys, unlock Windows, or launch another process.
No dependencies beyond a Windows Python installation.

Run in the interactive Windows desktop session:

    python agentsleep.py status
    python agentsleep.py discover
    python agentsleep.py plan --target-device-path '<exact device_path from discover>'
    python -B -m unittest -v

PowerShell: copy the JSON field's value after decoding JSON (rather than copying the
JSON-escaped backslashes). For example, use `ConvertFrom-Json` to inspect the output.
Choose a known driver explicitly. Output technology and friendly names are advisory:
even an indirect display can be physical. Device paths may identify equipment; keep
discovery JSON local unless you have reviewed it.

The plan is a conceptual baseline/target summary, not a native Windows restore blob.
No command applies it. Inactive paths can be ambiguous and will be refused; the
planner favors a known active path only when that choice is unambiguous. A positive
plan does not establish no-restart behavior, capture/input compatibility, or rollback.

Next controlled milestone: use an independently identified, already-installed virtual
driver, validate a native supplied topology through Windows without applying it, then
persist an exact same-session replay snapshot and verify a separate rollback helper
is ready before attempting any nonpersistent switch. Trial only with local supervision
and a manual recovery path; reserve machine boot/session privileges as-is. Capture
actual successful restoration after timeout, explicit restore, and parent crash before
considering real use.

API: https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-querydisplayconfig
