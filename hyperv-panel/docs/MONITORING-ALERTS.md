# Step 3.4.1 — Alerts and Notifications

PenguinPanel evaluates the shared Step 3.4.0 live sample and opens persistent alerts for sustained problems. It does not run another Hyper-V or PowerShell collector.

## Alert rules

- Host CPU at or above its warning threshold.
- Host memory at or above its critical threshold.
- Host system-disk free space at or below its critical threshold.
- Hyper-V Virtual Machine Management service not running.
- Live monitoring data stale or failing.
- Running VPS CPU at or above its warning threshold.
- Running VPS memory demand at or above its assigned-memory threshold.
- Running VPS heartbeat unhealthy.
- Running VPS network adapter disconnected.

The default rule requires three consecutive bad samples. With five-second live monitoring, a condition normally remains bad for about 15 seconds before an alert opens. A single short spike therefore does not notify administrators.

## Lifecycle

1. **Active:** The condition was confirmed and a new alert was stored.
2. **Acknowledged:** An administrator has seen it. Reminder emails stop, but the alert remains open.
3. **Recovered:** A fresh monitoring sample confirms that the condition has cleared. PenguinPanel resolves the alert automatically and can send a recovery email.

Unacknowledged active alerts may send reminder emails after the configured cooldown. Stale monitoring data never resolves older host or VPS alerts because it cannot prove recovery.

## Email delivery

Alert and recovery emails use the existing SMTP configuration. Every panel account with the `admin` role and a non-empty email address receives notifications. Delivery successes and failures are written to Audit Logs.

If SMTP or alert email delivery is disabled, alerts are still detected, stored, displayed, and recover normally.

## Configuration

Open **Admin → Alerts & Notifications** to change thresholds, required consecutive samples, reminder cooldown, heartbeat/adapter rules, and email options. Settings are stored in the existing MySQL `settings` table.

The optional `MonitoringAlerts:EvaluationSeconds` value in `appsettings.json` controls how frequently the alert engine evaluates the cached live sample. It defaults to five seconds and is clamped to 5–60 seconds.

## Database migration

On the first startup after updating, `AppDatabase.InitializeAsync()` automatically creates the `monitoring_alerts` table and the default alert settings. No manual SQL import is required.

Client-facing outage, heartbeat, extended-downtime, and recovery messages are implemented separately in [CLIENT-AVAILABILITY-NOTIFICATIONS.md](CLIENT-AVAILABILITY-NOTIFICATIONS.md).
