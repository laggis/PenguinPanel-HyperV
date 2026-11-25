# Step 3.4.0 — Live VPS Monitoring

PenguinPanel now collects one shared, read-only Hyper-V monitoring sample for the host and all virtual machines. The sample is kept in memory and reused by every connected browser, so opening extra dashboard or VPS tabs does not start extra PowerShell collectors.

## Views

- **Admin → Live Monitoring** shows host CPU, memory, disk and Hyper-V service state plus every VPS.
- **Virtual Server → Monitoring** shows the authorized user's VPS CPU, memory demand, heartbeat, adapter state, uptime, and current network/disk rates.
- Non-admin users can only read monitoring data for VMs assigned to their account, and the existing Resources permission controls access to the Monitoring tab.

## Collection model

The background service polls once every `Monitoring:PollSeconds` (default 5 seconds). Hyper-V's cumulative resource-metering counters are compared with the previous sample to calculate bytes per second. The first sample therefore displays `0 B/s`; rates appear after the next sample.

Step 3.4.0 intentionally keeps no performance-history database. Step 3.4.1 now evaluates this shared sample for sustained alerts and notifications, while Step 3.4.3 remains responsible for performance history and graphs.

## Configuration

```json
"Monitoring": {
  "Enabled": true,
  "PollSeconds": 5,
  "UiRefreshSeconds": 5,
  "StaleAfterSeconds": 20,
  "AutoEnableResourceMetering": true
}
```

- `PollSeconds`: shared host collection interval, clamped to 3–60 seconds.
- `UiRefreshSeconds`: how often an open per-VPS tab reads the cached sample, clamped to 3–60 seconds.
- `StaleAfterSeconds`: marks old data as stale, at least twice the polling interval.
- `AutoEnableResourceMetering`: calls `Enable-VMResourceMetering` for VMs where metering is off. Disable it only if metering is managed separately; network and disk rates may then remain unavailable.

## Deployment check

1. Build the panel on the Windows Hyper-V host with `dotnet build`.
2. Restart the panel service/process.
3. Open **Admin → Live Monitoring** and wait for two samples.
4. Open a VPS and select **Monitoring**.
5. Confirm CPU and memory update, a running Windows guest reports a healthy heartbeat, and active traffic produces network/disk rates.

The panel process must retain its existing permission to query Hyper-V, CIM, VM integration services, adapters, and resource metering.
