using System.Globalization;
using System.Net;

namespace HyperVPanel.Services;

public sealed class VmAvailabilityService(
    LiveMonitoringService monitoring,
    AppDatabase db,
    EmailService email,
    IConfiguration configuration,
    ILogger<VmAvailabilityService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _evaluationLock = new(1, 1);
    private readonly object _sync = new();
    private bool _isRunning;
    private DateTime? _startedAt;
    private DateTime? _lastEvaluationAt;
    private string _lastMessage = "Availability monitor has not started yet.";
    private int _trackedVmCount;

    public VmAvailabilityRuntimeStatus GetStatus()
    {
        lock (_sync)
        {
            return new VmAvailabilityRuntimeStatus(_isRunning, _startedAt, _lastEvaluationAt, _lastMessage, _trackedVmCount);
        }
    }

    public async Task MarkPlannedDowntimeAsync(string vmName, string reason, TimeSpan duration)
    {
        await _evaluationLock.WaitAsync();
        try
        {
            await db.MarkVmPlannedDowntimeAsync(vmName, DateTime.UtcNow.Add(duration), reason);
        }
        finally
        {
            _evaluationLock.Release();
        }
    }

    public async Task ClearPlannedDowntimeAsync(string vmName, string username)
    {
        await _evaluationLock.WaitAsync();
        try
        {
            await db.ClearVmPlannedDowntimeAsync(vmName, username);
        }
        finally
        {
            _evaluationLock.Release();
        }
    }

    public async Task EvaluateNowAsync(CancellationToken cancellationToken = default) =>
        await EvaluateSerializedAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetRuntime(true, "Availability monitor starting.", 0);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EvaluateSerializedAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "VPS availability evaluation failed.");
                    SetRuntime(true, $"Availability evaluation failed: {ex.Message}", _trackedVmCount);
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Availability:EvaluationSeconds", 5), 5, 60)),
                    stoppingToken);
            }
        }
        finally
        {
            SetRuntime(false, "Availability monitor stopped.", _trackedVmCount);
        }
    }

    private async Task EvaluateSerializedAsync(CancellationToken cancellationToken)
    {
        await _evaluationLock.WaitAsync(cancellationToken);
        try
        {
            await EvaluateAsync(cancellationToken);
        }
        finally
        {
            _evaluationLock.Release();
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var config = await db.GetVmAvailabilityConfigAsync();
        if (!config.Enabled)
        {
            SetRuntime(true, "Client availability notifications are disabled.", _trackedVmCount);
            return;
        }
        if (!monitoring.Enabled)
        {
            SetRuntime(true, "Availability notifications are idle because live monitoring is disabled.", _trackedVmCount);
            return;
        }

        var snapshot = monitoring.GetSnapshot();
        if (snapshot.IsStale)
        {
            SetRuntime(true, "Availability notifications are waiting for a fresh monitoring sample.", _trackedVmCount);
            return;
        }

        var states = (await db.GetVmAvailabilityStatesAsync())
            .ToDictionary(item => item.VmName, StringComparer.OrdinalIgnoreCase);
        var now = snapshot.SampledAt == DateTime.MinValue ? DateTime.UtcNow : snapshot.SampledAt;

        foreach (var vm in snapshot.Vms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!states.TryGetValue(vm.VmName, out var state))
            {
                var baseline = new VmAvailabilityState(
                    vm.VmName, vm.State, vm.HeartbeatHealthy, vm.IsRunning, false, now,
                    vm.IsRunning ? null : now,
                    vm.IsRunning && !vm.HeartbeatHealthy ? now : null,
                    null, string.Empty, null, null, null, DateTime.UtcNow);
                await db.SaveVmAvailabilityStateAsync(baseline);
                continue;
            }

            var plannedUntil = state.PlannedUntil is not null && state.PlannedUntil > now ? state.PlannedUntil : null;
            var plannedReason = plannedUntil is null ? string.Empty : state.PlannedReason;
            var downSince = state.DownSince;
            var unresponsiveSince = state.UnresponsiveSince;
            var unexpectedNotifiedAt = state.UnexpectedNotifiedAt;
            var extendedNotifiedAt = state.ExtendedNotifiedAt;
            var unresponsiveNotifiedAt = state.UnresponsiveNotifiedAt;
            var wasObservedRunning = state.WasObservedRunning || vm.IsRunning;
            var downWasPlanned = state.DownWasPlanned;

            if (vm.IsRunning)
            {
                var recoveredFromDown = downSince is not null;
                if (downSince is not null && (unexpectedNotifiedAt is not null || extendedNotifiedAt is not null))
                {
                    var duration = now - downSince.Value;
                    var title = $"{vm.VmName} is online again";
                    var message = $"The VPS recovered after {FormatDuration(duration)} of downtime.";
                    await RecordAndNotifyAsync(vm.VmName, "recovered", "recovered", title, message, downSince.Value, config, cancellationToken);
                }

                downSince = null;
                unexpectedNotifiedAt = null;
                extendedNotifiedAt = null;
                downWasPlanned = false;
                if (recoveredFromDown)
                {
                    plannedUntil = null;
                    plannedReason = string.Empty;
                }

                if (!vm.HeartbeatHealthy)
                {
                    if (plannedUntil is not null)
                    {
                        unresponsiveSince = null;
                        unresponsiveNotifiedAt = null;
                    }
                    else
                    {
                        unresponsiveSince ??= now;
                        if (unresponsiveNotifiedAt is null
                            && now - unresponsiveSince.Value >= TimeSpan.FromMinutes(config.GuestUnresponsiveMinutes))
                        {
                            var title = $"{vm.VmName} may be unresponsive";
                            var message = $"Hyper-V still reports the VPS as Running, but its heartbeat has been unhealthy for {FormatDuration(now - unresponsiveSince.Value)}.";
                            await RecordAndNotifyAsync(vm.VmName, "guest_unresponsive", "critical", title, message, unresponsiveSince.Value, config, cancellationToken);
                            unresponsiveNotifiedAt = now;
                        }
                    }
                }
                else
                {
                    if (unresponsiveSince is not null && unresponsiveNotifiedAt is not null)
                    {
                        var title = $"{vm.VmName} heartbeat recovered";
                        var message = $"The guest heartbeat recovered after {FormatDuration(now - unresponsiveSince.Value)}.";
                        await RecordAndNotifyAsync(vm.VmName, "guest_recovered", "recovered", title, message, unresponsiveSince.Value, config, cancellationToken);
                    }
                    unresponsiveSince = null;
                    unresponsiveNotifiedAt = null;
                }
            }
            else
            {
                var justWentDown = downSince is null;
                downSince ??= now;
                unresponsiveSince = null;
                unresponsiveNotifiedAt = null;
                var planned = plannedUntil is not null && plannedUntil > now;
                if (justWentDown && planned)
                {
                    downWasPlanned = true;
                }

                if (wasObservedRunning
                    && !downWasPlanned
                    && unexpectedNotifiedAt is null
                    && now - downSince.Value >= TimeSpan.FromMinutes(config.UnexpectedDownMinutes))
                {
                    var title = $"{vm.VmName} unexpectedly stopped";
                    var message = $"The VPS changed from Running to {PrettyState(vm.State)} without an active PenguinPanel maintenance window.";
                    await RecordAndNotifyAsync(vm.VmName, "unexpected_down", "critical", title, message, downSince.Value, config, cancellationToken);
                    unexpectedNotifiedAt = now;
                }

                if (!planned
                    && extendedNotifiedAt is null
                    && now - downSince.Value >= TimeSpan.FromMinutes(config.ExtendedDowntimeMinutes))
                {
                    var title = $"{vm.VmName} has been offline for {FormatDuration(now - downSince.Value)}";
                    var message = $"The VPS remains {PrettyState(vm.State)}. It can be started from PenguinPanel or reviewed by PenguinHosting support.";
                    await RecordAndNotifyAsync(vm.VmName, "extended_downtime", "warning", title, message, downSince.Value, config, cancellationToken);
                    extendedNotifiedAt = now;
                }
            }

            var updatedState = new VmAvailabilityState(
                vm.VmName, vm.State, vm.HeartbeatHealthy, wasObservedRunning, downWasPlanned, now,
                downSince, unresponsiveSince, plannedUntil, plannedReason,
                unexpectedNotifiedAt, extendedNotifiedAt, unresponsiveNotifiedAt, DateTime.UtcNow);
            if (HasMeaningfulChange(state, updatedState) || now - state.LastSampleAt >= TimeSpan.FromMinutes(1))
            {
                await db.SaveVmAvailabilityStateAsync(updatedState);
            }
        }

        SetRuntime(true, $"Tracked {snapshot.Vms.Count} VPS(s) from the {now.ToLocalTime():HH:mm:ss} sample.", snapshot.Vms.Count);
    }

    private async Task RecordAndNotifyAsync(
        string vmName,
        string eventType,
        string severity,
        string title,
        string message,
        DateTime startedAt,
        VmAvailabilityConfig config,
        CancellationToken cancellationToken)
    {
        var eventId = await db.AddVmAvailabilityEventAsync(vmName, eventType, severity, title, message, startedAt);
        await db.AddAuditAsync("VM_AVAILABILITY_" + eventType.ToUpperInvariant(), "vm", vmName, $"Event #{eventId}: {message}");

        if (!config.RecoveryNotifications && (eventType is "recovered" or "guest_recovered")) return;
        if (!email.IsEnabled) return;

        var deliveries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config.ClientEmailEnabled)
        {
            var clientDeliveries = 0;
            foreach (var recipient in await db.GetVmAvailabilityRecipientsAsync(vmName))
            {
                if (recipient.Preference.Enabled && WantsEvent(recipient.Preference, eventType))
                {
                    deliveries[recipient.Email] = recipient.Username;
                    clientDeliveries++;
                }
            }

            if (clientDeliveries == 0)
            {
                logger.LogWarning(
                    "No enabled customer email recipient is linked to VPS {VmName} for availability event {EventType}.",
                    vmName,
                    eventType);
                await db.AddAuditAsync(
                    "VM_AVAILABILITY_CLIENT_RECIPIENT_MISSING",
                    "vm",
                    vmName,
                    $"{eventType}: assign the VPS to a non-admin panel user with an email address or link it to an active WHMCS service.");
            }
        }
        if (config.NotifyAdmins)
        {
            foreach (var adminEmail in await db.GetAdminEmailsAsync())
            {
                deliveries.TryAdd(adminEmail, "PenguinPanel administrator");
            }
        }

        foreach (var delivery in deliveries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = $"Hello {delivery.Value},\n\n{title}\n\n{message}\n\nDetected: {startedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nCurrent update: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\nOpen PenguinPanel to view the VPS and recent availability events.";
            try
            {
                await email.SendAsync(delivery.Key, $"PenguinPanel: {title}", body, BuildEmailHtml(delivery.Value, title, severity, vmName, message, startedAt));
                await db.AddAuditAsync("VM_AVAILABILITY_EMAIL_SENT", "email", delivery.Key, $"{eventType}: {vmName}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not send VPS availability email to {Recipient}.", delivery.Key);
                await db.AddAuditAsync("VM_AVAILABILITY_EMAIL_FAILED", "email", delivery.Key, ex.Message);
            }
        }
    }

    private static bool WantsEvent(VmAvailabilityPreference preference, string eventType) => eventType switch
    {
        "unexpected_down" => preference.NotifyUnexpectedDown,
        "extended_downtime" => preference.NotifyExtendedDowntime,
        "guest_unresponsive" => preference.NotifyGuestUnresponsive,
        "recovered" or "guest_recovered" => preference.NotifyRecovery,
        _ => false
    };

    private static string BuildEmailHtml(string username, string title, string severity, string vmName, string message, DateTime startedAt)
    {
        var color = severity == "critical" ? "#fb7185" : severity == "recovered" ? "#4ade80" : "#fbbf24";
        return $"""
            <!doctype html><html><body style="margin:0;background:#0f1e2e;color:#eaf2f8;font-family:Segoe UI,Arial,sans-serif">
            <div style="max-width:620px;margin:30px auto;padding:26px;background:#172a3d;border:1px solid #29445d;border-radius:14px">
            <div style="color:{color};font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:.12em">PenguinPanel · VPS availability</div>
            <h1 style="font-size:24px;margin:12px 0">{WebUtility.HtmlEncode(title)}</h1>
            <p>Hello {WebUtility.HtmlEncode(username)},</p>
            <p>VPS: <strong>{WebUtility.HtmlEncode(vmName)}</strong></p>
            <div style="padding:15px;background:#102234;border-left:4px solid {color};border-radius:8px">{WebUtility.HtmlEncode(message)}</div>
            <p style="color:#7890a4;font-size:12px;margin-top:22px">Condition began {startedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}.</p>
            </div></body></html>
            """;
    }

    private static string PrettyState(string state) => state switch
    {
        "2" => "Running", "3" => "Off", "32768" => "Paused", "32769" => "Suspended", _ => state
    };

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours}h {value.Minutes}m";
        return $"{Math.Max(1, (int)value.TotalMinutes)} minute(s)";
    }

    private static bool HasMeaningfulChange(VmAvailabilityState previous, VmAvailabilityState current) =>
        !string.Equals(previous.LastState, current.LastState, StringComparison.OrdinalIgnoreCase)
        || previous.LastHeartbeatHealthy != current.LastHeartbeatHealthy
        || previous.WasObservedRunning != current.WasObservedRunning
        || previous.DownWasPlanned != current.DownWasPlanned
        || previous.DownSince != current.DownSince
        || previous.UnresponsiveSince != current.UnresponsiveSince
        || previous.PlannedUntil != current.PlannedUntil
        || !string.Equals(previous.PlannedReason, current.PlannedReason, StringComparison.Ordinal)
        || previous.UnexpectedNotifiedAt != current.UnexpectedNotifiedAt
        || previous.ExtendedNotifiedAt != current.ExtendedNotifiedAt
        || previous.UnresponsiveNotifiedAt != current.UnresponsiveNotifiedAt;

    private void SetRuntime(bool running, string message, int trackedVmCount)
    {
        lock (_sync)
        {
            _isRunning = running;
            _startedAt ??= DateTime.UtcNow;
            _lastEvaluationAt = DateTime.UtcNow;
            _lastMessage = message;
            _trackedVmCount = trackedVmCount;
        }
    }
}
