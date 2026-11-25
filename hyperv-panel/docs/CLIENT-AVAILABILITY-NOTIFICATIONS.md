# Step 3.4.1.1 — Client VPS Availability Notifications

PenguinPanel now turns the shared live-monitoring sample into client-facing VPS availability incidents. The state is persisted in MySQL, so restarting the panel does not forget an active outage or send a second first-alert email.

## Default notification rules

- **Unexpected shutdown:** email after the VPS has unexpectedly remained off for 1 minute.
- **Guest OS unresponsive:** email after Hyper-V reports the VPS as Running but its heartbeat remains unhealthy for 10 minutes.
- **Extended downtime:** email when the VPS has remained off for 180 minutes (3 hours), including an intentionally stopped VPS.
- **Recovery:** email when a previously notified outage or unhealthy heartbeat recovers, including the incident duration.

The first sample is treated as a baseline. If PenguinPanel starts while a VPS is already off, it does not claim that the VPS crashed. The extended-downtime reminder can still be sent if the VPS stays off.

## Planned operations

Stop, Restart, Suspend, Smart Reinstall, Full Backup, Full Backup Restore, and Quick Restore Point Restore create a planned downtime window before Hyper-V is changed. This prevents those actions from being reported as unexpected crashes.

An intentional Stop is remembered for the whole outage, so it never becomes an unexpected-crash alert after its short operation window expires. The 3-hour extended-downtime reminder remains available. Administrators can also create or end a maintenance silence from the VPS Monitoring tab.

## Client controls and incident history

Clients can open their VPS and select **Monitoring** to enable or disable these email categories for their own account and that VPS:

- unexpected shutdown;
- extended downtime;
- guest OS unresponsive;
- recovery.

The same page displays current availability state and the ten most recent availability events. Events are stored even when SMTP or client email delivery is disabled.

## Administration

Open **Admin → Alerts & Notifications** to enable or disable the availability engine and client delivery, choose whether administrators also receive the emails, and change each delay. The defaults are 1, 10, and 180 minutes.

The optional `Availability:EvaluationSeconds` setting in `appsettings.json` controls how often the engine evaluates the cached monitoring sample. It defaults to 5 seconds and is clamped to 5–60 seconds. All notification delays and delivery switches are stored in MySQL and managed in the UI.

## Database migration

On first startup after updating, PenguinPanel automatically creates `vm_availability_state`, `vm_availability_events`, and `vm_availability_preferences`, adds the default availability settings, and safely adds the planned-outage marker if necessary. No manual SQL import is required.

## Installation check

1. Replace the update files and restart PenguinPanel on the Windows Hyper-V host.
2. Run `dotnet build` before restarting if the host deploy process does not already build the project.
3. Confirm **Admin → Alerts & Notifications → Client VPS availability notifications** shows Running.
4. Open a client VPS → **Monitoring** and save its notification choices.
5. Use an admin maintenance silence for controlled testing, or temporarily lower a delay in the admin page. Do not stop a production VPS only to test notifications.

Optional capped automatic recovery is documented separately in [SAFE-SELF-HEALING.md](SAFE-SELF-HEALING.md). Availability detection and notifications continue working when self-healing is disabled.
