# Step 3.4.2 — Safe Automatic Self-Healing

PenguinPanel can now perform tightly limited recovery actions after the availability monitor confirms a real VPS incident. The system is deliberately opt-in at two levels: the global master switch defaults to disabled, and every VPS defaults to not opted in.

## Recovery actions

- **Unexpected stop:** issues `Start-VM` after the VPS has remained unexpectedly Off/Stopped for 3 minutes by default. Paused, Saved, suspended, and unknown Hyper-V states require manual review.
- **Guest OS unresponsive:** optionally issues a forced `Restart-VM` after the VPS has remained Running with an unhealthy Hyper-V heartbeat for 20 minutes by default.

Heartbeat Restart is disabled globally and per VPS by default because it is more disruptive than starting an unexpectedly stopped VPS.

## Safety rules

- Live monitoring must be enabled and fresh. Stale or failed monitoring data never triggers an action.
- The global master switch and the selected VPS's opt-in switch must both be enabled.
- Planned maintenance and intentional Stop, Restart, Suspend, Reinstall, Backup, and Restore operations suppress or cancel recovery.
- An abuse auto-suspension creates a ten-year security hold before suspending the VPS. Only an administrator can clear it.
- The default maximum is 2 attempts in a 6-hour window.
- Attempts are separated by a 30-minute cooldown.
- A Start/Restart must remain healthy for the 3-minute confirmation period before it is recorded as recovered.
- Reaching the attempt limit changes the incident to **Manual review required**. No restart loop is possible.
- Disabling the global master switch or a VPS opt-in cancels pending retries; enabling it again does not silently resume the same incident.
- Applying a safety hold also cancels the current recovery. Clearing the hold does not resume that same incident unless an administrator deliberately resets its incident state.
- Administrators can place a timed per-VPS safety hold or reset an exhausted incident from VPS → Monitoring.

## Notifications and history

Recovery start, confirmation failure, success, cancellation, and exhausted-attempt events are stored in MySQL and displayed in VPS → Monitoring. Assigned clients and administrators can receive email updates through the existing SMTP service. The client's main availability switch and the matching unexpected-stop, guest-unresponsive, or recovery preference are respected.

## Administration

Open **Admin → Alerts & Notifications → Safe automatic self-healing** to configure the global rules. Then open each VPS → **Monitoring → Safe automatic self-healing** and explicitly opt in the VPS.

Recommended rollout:

1. Keep the global master switch disabled.
2. Opt in one disposable/test VPS for unexpected-stop Start recovery only.
3. Enable the global master switch and verify one controlled incident.
4. Review the self-healing history and Audit Logs.
5. Add production VPSs gradually.
6. Enable heartbeat Restart only for VPSs where Hyper-V heartbeat is known to be reliable.

## Configuration and database

The optional `SelfHealing:EvaluationSeconds` value in `appsettings.json` defaults to 10 seconds and is clamped to 5–60 seconds. All behavior switches, delays, attempt limits, per-VPS permissions, holds, state, and history are stored in MySQL.

On startup PenguinPanel automatically creates:

- `vm_self_healing_preferences`
- `vm_self_healing_state`
- `vm_self_healing_events`

It also inserts the safe default settings. No manual SQL import is required.
