using System.Globalization;
using System.Net;

namespace HyperVPanel.Services;

public sealed class VmSelfHealingService(
    LiveMonitoringService monitoring,
    VmAvailabilityService availability,
    PowerShellService powerShell,
    AppDatabase db,
    EmailService email,
    IConfiguration configuration,
    ILogger<VmSelfHealingService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _evaluationLock = new(1, 1);
    private readonly object _sync = new();
    private bool _isRunning;
    private DateTime? _startedAt;
    private DateTime? _lastEvaluationAt;
    private string _lastMessage = "Self-healing has not started yet.";
    private int _eligibleVmCount;
    private int _activeIncidentCount;

    public VmSelfHealingRuntimeStatus GetStatus()
    {
        lock (_sync)
        {
            return new VmSelfHealingRuntimeStatus(
                _isRunning, _startedAt, _lastEvaluationAt, _lastMessage,
                _eligibleVmCount, _activeIncidentCount);
        }
    }

    public async Task EvaluateNowAsync(CancellationToken cancellationToken = default) =>
        await EvaluateSerializedAsync(cancellationToken);

    public async Task SetSafetyHoldAsync(string vmName, string reason, TimeSpan duration)
    {
        await _evaluationLock.WaitAsync();
        try
        {
            await db.SetVmSelfHealingHoldAsync(vmName, DateTime.UtcNow.Add(duration), reason);
            var state = await db.GetVmSelfHealingStateAsync(vmName);
            if (state is not null && (state.Status is "waiting_confirmation" or "cooldown" or "exhausted" or "detected"))
            {
                var message = $"Automatic recovery was cancelled by safety hold: {reason}";
                await db.SaveVmSelfHealingStateAsync(state with
                {
                    Status = "cancelled",
                    NextAttemptAt = null,
                    LastResult = message,
                    UpdatedAt = DateTime.UtcNow
                });
                var eventId = await db.AddVmSelfHealingEventAsync(vmName, state.IncidentType, "recovery_cancelled", "suppressed", state.AttemptCount, message);
                await db.AddAuditAsync("VM_SELF_HEALING_RECOVERY_CANCELLED", "vm", vmName, $"Event #{eventId}: {message}");
            }
        }
        finally
        {
            _evaluationLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetRuntime(true, "Self-healing monitor starting.", 0, 0);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
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
                    logger.LogError(ex, "VPS self-healing evaluation failed.");
                    SetRuntime(true, $"Self-healing evaluation failed: {ex.Message}", _eligibleVmCount, _activeIncidentCount);
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("SelfHealing:EvaluationSeconds", 10), 5, 60)),
                    stoppingToken);
            }
        }
        finally
        {
            SetRuntime(false, "Self-healing monitor stopped.", _eligibleVmCount, _activeIncidentCount);
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
        var config = await db.GetVmSelfHealingConfigAsync();
        if (!config.Enabled)
        {
            SetRuntime(true, "Self-healing is safely disabled by the master switch.", 0, 0);
            return;
        }
        if (!monitoring.Enabled)
        {
            SetRuntime(true, "Self-healing is idle because live monitoring is disabled.", 0, 0);
            return;
        }

        var snapshot = monitoring.GetSnapshot();
        if (snapshot.IsStale)
        {
            SetRuntime(true, "Self-healing will not act on a stale monitoring sample.", 0, 0);
            return;
        }

        var preferences = await db.GetEnabledVmSelfHealingPreferencesAsync();
        var availabilityStates = (await db.GetVmAvailabilityStatesAsync())
            .ToDictionary(item => item.VmName, StringComparer.OrdinalIgnoreCase);
        var healingStates = await db.GetVmSelfHealingStatesAsync();
        var liveVms = snapshot.Vms.ToDictionary(item => item.VmName, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var activeCount = 0;

        foreach (var preference in preferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!liveVms.TryGetValue(preference.VmName, out var vm)
                || !availabilityStates.TryGetValue(preference.VmName, out var availabilityState))
            {
                continue;
            }

            healingStates.TryGetValue(preference.VmName, out var state);
            var active = state is not null && (state.Status is "waiting_confirmation" or "cooldown" or "exhausted");
            if (active) activeCount++;

            if (preference.IsHeld)
            {
                continue;
            }

            var selfHealingWindow = availabilityState.IsPlannedNow
                && availabilityState.PlannedReason.StartsWith("Automatic self-healing", StringComparison.OrdinalIgnoreCase);

            if (active && state is not null)
            {
                var recovered = IsRecovered(state.IncidentType, vm);
                if (recovered)
                {
                    if (state.Status == "waiting_confirmation"
                        && state.NextAttemptAt is not null
                        && state.NextAttemptAt > now)
                    {
                        continue;
                    }
                    var message = state.IncidentType == "unexpected_stop"
                        ? $"{vm.VmName} is Running after automatic Start."
                        : $"{vm.VmName} has a healthy heartbeat after automatic recovery.";
                    var recoveredState = state with
                    {
                        Status = "recovered",
                        NextAttemptAt = null,
                        LastResult = message,
                        UpdatedAt = now
                    };
                    await db.SaveVmSelfHealingStateAsync(recoveredState);
                    if (state.IncidentType == "guest_unresponsive" && selfHealingWindow)
                    {
                        try
                        {
                            await availability.ClearPlannedDowntimeAsync(vm.VmName, "Automatic self-healing confirmed");
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Could not clear the completed self-healing window for {VmName}.", vm.VmName);
                        }
                    }
                    await RecordAndNotifyAsync(vm.VmName, state.IncidentType, "recovery_succeeded", "recovered", state.AttemptCount, message, config, cancellationToken);
                    continue;
                }

                if (state.Status == "exhausted")
                {
                    continue;
                }

                if (availabilityState.IsPlannedNow && !selfHealingWindow)
                {
                    await CancelForPlannedActionAsync(state, availabilityState.PlannedReason);
                    continue;
                }
                if (availabilityState.DownWasPlanned && state.IncidentType == "unexpected_stop")
                {
                    await CancelForPlannedActionAsync(state, "A planned power action took ownership of the outage.");
                    continue;
                }

                if (state.NextAttemptAt is not null && state.NextAttemptAt > now)
                {
                    continue;
                }

                if (state.Status == "waiting_confirmation")
                {
                    var failedMessage = $"Attempt {state.AttemptCount} did not confirm recovery within {config.ConfirmationMinutes} minute(s).";
                    if (state.AttemptCount >= config.MaxAttempts)
                    {
                        await ExhaustAsync(state, failedMessage, config, cancellationToken);
                    }
                    else
                    {
                        var retryAt = (state.LastAttemptAt ?? now).AddMinutes(config.CooldownMinutes);
                        var cooldownState = state with
                        {
                            Status = "cooldown",
                            NextAttemptAt = retryAt,
                            LastResult = failedMessage,
                            UpdatedAt = now
                        };
                        await db.SaveVmSelfHealingStateAsync(cooldownState);
                        await RecordAndNotifyAsync(vm.VmName, state.IncidentType, "recovery_not_confirmed", "warning", state.AttemptCount,
                            $"{failedMessage} The next attempt is allowed after {retryAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}.", config, cancellationToken);
                    }
                    continue;
                }

                await AttemptRecoveryAsync(vm, state, config, cancellationToken);
                continue;
            }

            if (availabilityState.IsPlannedNow || availabilityState.DownWasPlanned)
            {
                continue;
            }

            var incident = FindIncident(vm, availabilityState, preference, config, now);
            if (incident is null)
            {
                continue;
            }

            var incidentKey = $"{incident.Value.Type}:{incident.Value.StartedAt.Ticks.ToString(CultureInfo.InvariantCulture)}";
            if (state is not null
                && string.Equals(state.IncidentKey, incidentKey, StringComparison.Ordinal)
                && (state.Status is "cancelled" or "recovered" or "exhausted"))
            {
                continue;
            }
            var newState = state is not null && string.Equals(state.IncidentKey, incidentKey, StringComparison.Ordinal)
                ? state
                : new VmSelfHealingState(
                    vm.VmName, incidentKey, incident.Value.Type, incident.Value.StartedAt,
                    state?.AttemptCount ?? 0, state?.WindowStartedAt ?? now,
                    null, null, "detected", "Recovery condition confirmed.", now);

            if (newState.WindowStartedAt is null || now - newState.WindowStartedAt.Value >= TimeSpan.FromMinutes(config.AttemptWindowMinutes))
            {
                newState = newState with { AttemptCount = 0, WindowStartedAt = now, Status = "detected", NextAttemptAt = null, UpdatedAt = now };
            }

            await db.SaveVmSelfHealingStateAsync(newState);
            await AttemptRecoveryAsync(vm, newState, config, cancellationToken);
        }

        SetRuntime(true,
            $"Evaluated {preferences.Count} opted-in VPS(s) from the {snapshot.SampledAt.ToLocalTime():HH:mm:ss} sample.",
            preferences.Count, activeCount);
    }

    private static (string Type, DateTime StartedAt)? FindIncident(
        VmLiveMonitoringSnapshot vm,
        VmAvailabilityState state,
        VmSelfHealingPreference preference,
        VmSelfHealingConfig config,
        DateTime now)
    {
        if (!vm.IsRunning
            && IsStartRecoverableState(vm.State)
            && config.UnexpectedStopRecovery
            && preference.RecoverUnexpectedStop
            && state.WasObservedRunning
            && state.DownSince is not null
            && now - state.DownSince.Value >= TimeSpan.FromMinutes(config.UnexpectedStopDelayMinutes))
        {
            return ("unexpected_stop", state.DownSince.Value);
        }

        if (vm.IsRunning
            && !vm.HeartbeatHealthy
            && config.HeartbeatRecovery
            && preference.RecoverUnresponsive
            && state.UnresponsiveSince is not null
            && now - state.UnresponsiveSince.Value >= TimeSpan.FromMinutes(config.HeartbeatDelayMinutes))
        {
            return ("guest_unresponsive", state.UnresponsiveSince.Value);
        }

        return null;
    }

    private async Task AttemptRecoveryAsync(
        VmLiveMonitoringSnapshot vm,
        VmSelfHealingState state,
        VmSelfHealingConfig config,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (state.IncidentType == "unexpected_stop" && !vm.IsRunning && !IsStartRecoverableState(vm.State))
        {
            await ExhaustAsync(state, $"The current Hyper-V state '{vm.State}' is not safe for automatic Start.", config, cancellationToken);
            return;
        }
        if (state.AttemptCount >= config.MaxAttempts)
        {
            await ExhaustAsync(state, "The configured recovery attempt limit was reached.", config, cancellationToken);
            return;
        }

        var attempt = state.AttemptCount + 1;
        var action = state.IncidentType == "unexpected_stop" || !vm.IsRunning ? "Start" : "Restart";
        var startedMessage = $"Automatic self-healing is performing {action} attempt {attempt}/{config.MaxAttempts} for {vm.VmName}.";
        await RecordAndNotifyAsync(vm.VmName, state.IncidentType, "recovery_started", "warning", attempt, startedMessage, config, cancellationToken);

        try
        {
            if (action == "Restart")
            {
                await availability.MarkPlannedDowntimeAsync(vm.VmName, "Automatic self-healing heartbeat restart", TimeSpan.FromMinutes(15));
                await powerShell.RestartVmAsync(vm.VmName);
            }
            else
            {
                await powerShell.StartVmAsync(vm.VmName);
            }

            await db.SaveVmSelfHealingStateAsync(state with
            {
                AttemptCount = attempt,
                LastAttemptAt = now,
                NextAttemptAt = now.AddMinutes(config.ConfirmationMinutes),
                Status = "waiting_confirmation",
                LastResult = $"{action} command completed; waiting for monitoring confirmation.",
                UpdatedAt = now
            });
            await db.AddAuditAsync("VM_SELF_HEALING_ACTION", "vm", vm.VmName, $"{action} attempt {attempt}/{config.MaxAttempts}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automatic self-healing {Action} failed for {VmName}.", action, vm.VmName);
            var failedMessage = $"Automatic {action} attempt {attempt}/{config.MaxAttempts} failed: {ex.Message}";
            var exhausted = attempt >= config.MaxAttempts;
            await db.SaveVmSelfHealingStateAsync(state with
            {
                AttemptCount = attempt,
                LastAttemptAt = now,
                NextAttemptAt = exhausted ? null : now.AddMinutes(config.CooldownMinutes),
                Status = exhausted ? "exhausted" : "cooldown",
                LastResult = failedMessage,
                UpdatedAt = now
            });
            await RecordAndNotifyAsync(vm.VmName, state.IncidentType,
                exhausted ? "recovery_exhausted" : "recovery_failed",
                exhausted ? "critical" : "warning", attempt, failedMessage, config, cancellationToken);
        }
    }

    private async Task ExhaustAsync(VmSelfHealingState state, string reason, VmSelfHealingConfig config, CancellationToken cancellationToken)
    {
        if (state.Status == "exhausted") return;
        var message = $"Automatic recovery stopped after {state.AttemptCount} attempt(s). {reason} Manual review is required.";
        await db.SaveVmSelfHealingStateAsync(state with
        {
            Status = "exhausted",
            NextAttemptAt = null,
            LastResult = message,
            UpdatedAt = DateTime.UtcNow
        });
        await RecordAndNotifyAsync(state.VmName, state.IncidentType, "recovery_exhausted", "critical", state.AttemptCount, message, config, cancellationToken);
    }

    private async Task CancelForPlannedActionAsync(VmSelfHealingState state, string reason)
    {
        var message = $"Automatic recovery was cancelled because PenguinPanel detected planned or manual maintenance. {reason}";
        await db.SaveVmSelfHealingStateAsync(state with
        {
            Status = "cancelled",
            NextAttemptAt = null,
            LastResult = message,
            UpdatedAt = DateTime.UtcNow
        });
        var eventId = await db.AddVmSelfHealingEventAsync(state.VmName, state.IncidentType, "recovery_cancelled", "suppressed", state.AttemptCount, message);
        await db.AddAuditAsync("VM_SELF_HEALING_RECOVERY_CANCELLED", "vm", state.VmName, $"Event #{eventId}: {message}");
    }

    private async Task RecordAndNotifyAsync(
        string vmName,
        string incidentType,
        string eventType,
        string status,
        int attempt,
        string message,
        VmSelfHealingConfig config,
        CancellationToken cancellationToken)
    {
        var eventId = await db.AddVmSelfHealingEventAsync(vmName, incidentType, eventType, status, attempt, message);
        await db.AddAuditAsync("VM_SELF_HEALING_" + eventType.ToUpperInvariant(), "vm", vmName, $"Event #{eventId}: {message}");
        if (!email.IsEnabled) return;

        var recipients = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config.ClientEmailEnabled)
        {
            foreach (var recipient in await db.GetVmAvailabilityRecipientsAsync(vmName))
            {
                if (recipient.Preference.Enabled
                    && WantsClientNotification(recipient.Preference, incidentType, eventType))
                {
                    recipients[recipient.Email] = recipient.Username;
                }
            }
        }
        if (config.NotifyAdmins)
        {
            foreach (var adminEmail in await db.GetAdminEmailsAsync())
            {
                recipients.TryAdd(adminEmail, "PenguinPanel administrator");
            }
        }

        foreach (var recipient in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subject = status == "recovered"
                ? $"PenguinPanel: {vmName} recovered automatically"
                : status == "critical"
                    ? $"PenguinPanel: {vmName} needs manual recovery"
                    : $"PenguinPanel: automatic recovery for {vmName}";
            var body = $"Hello {recipient.Value},\n\n{message}\n\nVPS: {vmName}\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\nOpen PenguinPanel to review the VPS and its self-healing history.";
            try
            {
                await email.SendAsync(recipient.Key, subject, body, BuildEmailHtml(recipient.Value, vmName, status, message));
                await db.AddAuditAsync("VM_SELF_HEALING_EMAIL_SENT", "email", recipient.Key, $"{eventType}: {vmName}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not send self-healing email to {Recipient}.", recipient.Key);
                await db.AddAuditAsync("VM_SELF_HEALING_EMAIL_FAILED", "email", recipient.Key, ex.Message);
            }
        }
    }

    private static bool IsRecovered(string incidentType, VmLiveMonitoringSnapshot vm) =>
        incidentType == "guest_unresponsive"
            ? vm.IsRunning && vm.HeartbeatHealthy
            : vm.IsRunning;

    private static bool WantsClientNotification(VmAvailabilityPreference preference, string incidentType, string eventType)
    {
        if (eventType == "recovery_succeeded") return preference.NotifyRecovery;
        return incidentType == "guest_unresponsive"
            ? preference.NotifyGuestUnresponsive
            : preference.NotifyUnexpectedDown;
    }

    private static bool IsStartRecoverableState(string state)
    {
        var value = (state ?? string.Empty).Trim();
        return value.Equals("Off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Stopped", StringComparison.OrdinalIgnoreCase)
            || value.Equals("3", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEmailHtml(string username, string vmName, string status, string message)
    {
        var color = status == "recovered" ? "#4ade80" : status == "critical" ? "#fb7185" : "#fbbf24";
        return $"""
            <!doctype html><html><body style="margin:0;background:#0f1e2e;color:#eaf2f8;font-family:Segoe UI,Arial,sans-serif">
            <div style="max-width:620px;margin:30px auto;padding:26px;background:#172a3d;border:1px solid #29445d;border-radius:14px">
            <div style="color:{color};font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:.12em">PenguinPanel · Safe self-healing</div>
            <h1 style="font-size:24px;margin:12px 0">Automatic VPS recovery</h1>
            <p>Hello {WebUtility.HtmlEncode(username)},</p>
            <p>VPS: <strong>{WebUtility.HtmlEncode(vmName)}</strong></p>
            <div style="padding:15px;background:#102234;border-left:4px solid {color};border-radius:8px">{WebUtility.HtmlEncode(message)}</div>
            <p style="color:#7890a4;font-size:12px;margin-top:22px">PenguinPanel never exceeds the configured attempt limit.</p>
            </div></body></html>
            """;
    }

    private void SetRuntime(bool running, string message, int eligibleVmCount, int activeIncidentCount)
    {
        lock (_sync)
        {
            _isRunning = running;
            _startedAt ??= DateTime.UtcNow;
            _lastEvaluationAt = DateTime.UtcNow;
            _lastMessage = message;
            _eligibleVmCount = eligibleVmCount;
            _activeIncidentCount = activeIncidentCount;
        }
    }
}
