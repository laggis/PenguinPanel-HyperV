using System.Globalization;
using System.Net;

namespace HyperVPanel.Services;

public sealed class MonitoringAlertService(
    LiveMonitoringService monitoring,
    AppDatabase db,
    EmailService email,
    IConfiguration configuration,
    ILogger<MonitoringAlertService> logger) : BackgroundService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _evaluationLock = new(1, 1);
    private readonly Dictionary<string, int> _consecutiveSamples = new(StringComparer.OrdinalIgnoreCase);
    private bool _isRunning;
    private DateTime? _startedAt;
    private DateTime? _lastEvaluationAt;
    private string _lastMessage = "Alert monitor has not started yet.";
    private int _openAlertCount;
    private int _pendingConditionCount;

    public MonitoringAlertRuntimeStatus GetStatus()
    {
        lock (_sync)
        {
            return new MonitoringAlertRuntimeStatus(
                _isRunning, _startedAt, _lastEvaluationAt, _lastMessage, _openAlertCount, _pendingConditionCount);
        }
    }

    public async Task EvaluateNowAsync(CancellationToken cancellationToken = default) =>
        await EvaluateSerializedAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetRuntime(true, "Alert monitor starting.", 0, 0);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
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
                    logger.LogError(ex, "Monitoring alert evaluation failed.");
                    SetRuntime(true, $"Alert evaluation failed: {ex.Message}", _openAlertCount, _pendingConditionCount);
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MonitoringAlerts:EvaluationSeconds", 5), 5, 60)), stoppingToken);
            }
        }
        finally
        {
            SetRuntime(false, "Alert monitor stopped.", _openAlertCount, _pendingConditionCount);
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
        var config = await db.GetMonitoringAlertConfigAsync();
        var openAlerts = await db.GetOpenMonitoringAlertsAsync();
        if (!config.Enabled)
        {
            SetRuntime(true, "Alert detection is disabled in Alerts & Notifications.", openAlerts.Count, 0);
            return;
        }
        if (!monitoring.Enabled)
        {
            SetRuntime(true, "Alert detection is idle because live monitoring is disabled.", openAlerts.Count, 0);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = monitoring.GetSnapshot();
        var conditions = BuildConditions(snapshot, config);
        var currentKeys = conditions.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var openByKey = openAlerts
            .GroupBy(item => item.AlertKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Id).First(), StringComparer.OrdinalIgnoreCase);

        foreach (var condition in conditions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _consecutiveSamples[condition.Key] = _consecutiveSamples.GetValueOrDefault(condition.Key) + 1;

            if (openByKey.TryGetValue(condition.Key, out var existing))
            {
                await db.TouchMonitoringAlertAsync(existing.Id, condition.Message, condition.CurrentValue, condition.ThresholdValue);
                var reminderDue = existing.Status == "active"
                    && (existing.LastNotifiedAt is null || DateTime.UtcNow - existing.LastNotifiedAt.Value >= TimeSpan.FromMinutes(config.ReminderMinutes));
                if (reminderDue)
                {
                    await SendAlertEmailAsync(existing.Id, condition, "Reminder", config.EmailEnabled, cancellationToken);
                }
                continue;
            }

            if (_consecutiveSamples[condition.Key] < config.RequiredConsecutiveSamples)
            {
                continue;
            }

            var alertId = await db.CreateMonitoringAlertAsync(
                condition.Key, condition.ScopeType, condition.ScopeName, condition.AlertType,
                condition.Severity, condition.Title, condition.Message, condition.CurrentValue, condition.ThresholdValue);
            await db.AddAuditAsync("MONITORING_ALERT_TRIGGERED", condition.ScopeType, condition.ScopeName, $"{condition.Severity}: {condition.Title}. {condition.Message}");
            await SendAlertEmailAsync(alertId, condition, "New alert", config.EmailEnabled, cancellationToken);
        }

        foreach (var oldKey in _consecutiveSamples.Keys.Where(key => !currentKeys.Contains(key)).ToList())
        {
            _consecutiveSamples.Remove(oldKey);
        }

        // Stale data cannot prove that earlier VM/host conditions recovered. Keep those alerts open
        // until a fresh sample is available; only a fresh sample may resolve them.
        if (!snapshot.IsStale)
        {
            foreach (var alert in openAlerts.Where(item => !currentKeys.Contains(item.AlertKey)))
            {
                var resolution = $"Recovered automatically at {DateTime.UtcNow:u}; the condition is no longer present.";
                await db.ResolveMonitoringAlertAsync(alert.Id, resolution);
                await db.AddAuditAsync("MONITORING_ALERT_RESOLVED", alert.ScopeType, alert.ScopeName, alert.Title);
                if (config.RecoveryEmails)
                {
                    await SendRecoveryEmailAsync(alert, config.EmailEnabled, cancellationToken);
                }
            }
        }

        var refreshedOpen = await db.GetOpenMonitoringAlertsAsync();
        var pending = _consecutiveSamples.Count(item => item.Value < config.RequiredConsecutiveSamples);
        SetRuntime(true,
            $"Evaluated {snapshot.Vms.Count} VPS(s); {refreshedOpen.Count} open alert(s), {pending} condition(s) awaiting confirmation.",
            refreshedOpen.Count,
            pending);
    }

    private static IReadOnlyList<AlertCondition> BuildConditions(LiveMonitoringOverview snapshot, MonitoringAlertConfig config)
    {
        var conditions = new List<AlertCondition>();
        if (snapshot.IsStale)
        {
            var detail = string.IsNullOrWhiteSpace(snapshot.LastError)
                ? $"The last monitoring sample is older than {snapshot.StaleAfterSeconds} seconds."
                : $"The live collector is stale. Last error: {snapshot.LastError}";
            conditions.Add(new("host:monitoring_stale", "host", "Hyper-V host", "monitoring_stale", "critical", "Live monitoring data is stale", detail, null, snapshot.StaleAfterSeconds));
            return conditions;
        }

        if (snapshot.Host is not null)
        {
            var host = snapshot.Host;
            var memoryPercent = Percent(host.MemoryUsedBytes, host.MemoryTotalBytes);
            var diskFreePercent = Percent(host.DiskFreeBytes, host.DiskTotalBytes);
            AddHigh(conditions, "host:cpu", "host", "Hyper-V host", "host_cpu", "warning", "High host CPU", host.CpuPercent, config.HostCpuPercent);
            if (host.MemoryTotalBytes > 0)
            {
                AddHigh(conditions, "host:memory", "host", "Hyper-V host", "host_memory", "critical", "High host memory usage", memoryPercent, config.HostMemoryPercent);
            }
            if (host.DiskTotalBytes > 0)
            {
                AddLow(conditions, "host:disk_free", "host", "Hyper-V host", "host_disk_free", "critical", "Low system disk space", diskFreePercent, config.HostDiskFreePercent);
            }
            if (!string.Equals(host.HyperVServiceStatus, "Running", StringComparison.OrdinalIgnoreCase))
            {
                conditions.Add(new("host:vmms", "host", "Hyper-V host", "hyperv_service", "critical", "Hyper-V service is not running", $"VMMS reports '{host.HyperVServiceStatus}'.", null, null));
            }
        }

        foreach (var vm in snapshot.Vms.Where(item => item.IsRunning))
        {
            AddHigh(conditions, $"vm:{vm.VmName}:cpu", "vm", vm.VmName, "vm_cpu", "warning", $"High CPU on {vm.VmName}", vm.CpuUsagePercent, config.VmCpuPercent);
            AddHigh(conditions, $"vm:{vm.VmName}:memory", "vm", vm.VmName, "vm_memory", "warning", $"High memory demand on {vm.VmName}", vm.MemoryPressurePercent, config.VmMemoryPressurePercent);
            if (config.HeartbeatAlerts && !vm.HeartbeatHealthy)
            {
                conditions.Add(new($"vm:{vm.VmName}:heartbeat", "vm", vm.VmName, "heartbeat", "critical", $"Heartbeat lost on {vm.VmName}", $"Hyper-V heartbeat reports '{vm.HeartbeatStatus}'.", null, null));
            }
            if (config.AdapterAlerts && vm.NetworkAdapterCount > 0 && vm.ConnectedNetworkAdapterCount < vm.NetworkAdapterCount)
            {
                conditions.Add(new($"vm:{vm.VmName}:adapters", "vm", vm.VmName, "network_adapter", "critical", $"Network adapter disconnected on {vm.VmName}", $"{vm.ConnectedNetworkAdapterCount} of {vm.NetworkAdapterCount} adapter(s) are connected.", vm.ConnectedNetworkAdapterCount, vm.NetworkAdapterCount));
            }
        }

        return conditions;
    }

    private static void AddHigh(List<AlertCondition> target, string key, string scopeType, string scopeName, string type, string severity, string title, double value, double threshold)
    {
        if (value >= threshold)
        {
            target.Add(new(key, scopeType, scopeName, type, severity, title, $"Current value {value:0.#}% is at or above the {threshold:0.#}% threshold.", value, threshold));
        }
    }

    private static void AddLow(List<AlertCondition> target, string key, string scopeType, string scopeName, string type, string severity, string title, double value, double threshold)
    {
        if (value <= threshold)
        {
            target.Add(new(key, scopeType, scopeName, type, severity, title, $"Current value {value:0.#}% is at or below the {threshold:0.#}% threshold.", value, threshold));
        }
    }

    private async Task SendAlertEmailAsync(long alertId, AlertCondition condition, string kind, bool emailEnabled, CancellationToken cancellationToken)
    {
        if (emailEnabled && email.IsEnabled)
        {
            var recipients = (await db.GetAdminEmailsAsync()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var recipient in recipients)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = $"{kind}: {condition.Title}\n\nSeverity: {condition.Severity}\nScope: {condition.ScopeName}\nDetails: {condition.Message}\nTime: {DateTime.UtcNow:u}\n\nOpen PenguinPanel Alerts & Notifications to acknowledge this alert.";
                try
                {
                    await email.SendAsync(recipient, $"PenguinPanel {condition.Severity.ToUpperInvariant()}: {condition.Title}", body, BuildEmailHtml(kind, condition.Title, condition.Severity, condition.ScopeName, condition.Message));
                    await db.AddAuditAsync("MONITORING_ALERT_EMAIL_SENT", "email", recipient, condition.Title);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not send monitoring alert email to {Recipient}.", recipient);
                    await db.AddAuditAsync("MONITORING_ALERT_EMAIL_FAILED", "email", recipient, ex.Message);
                }
            }
        }

        await db.MarkMonitoringAlertNotifiedAsync(alertId);
    }

    private async Task SendRecoveryEmailAsync(MonitoringAlert alert, bool emailEnabled, CancellationToken cancellationToken)
    {
        if (!emailEnabled || !email.IsEnabled) return;
        foreach (var recipient in (await db.GetAdminEmailsAsync()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = $"Recovered: {alert.Title}\n\nScope: {alert.ScopeName}\nThe condition is no longer present.\nTime: {DateTime.UtcNow:u}";
            try
            {
                await email.SendAsync(recipient, $"PenguinPanel RECOVERED: {alert.Title}", body,
                    BuildEmailHtml("Recovered", alert.Title, "recovered", alert.ScopeName, "The condition is no longer present."));
                await db.AddAuditAsync("MONITORING_RECOVERY_EMAIL_SENT", "email", recipient, alert.Title);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not send monitoring recovery email to {Recipient}.", recipient);
                await db.AddAuditAsync("MONITORING_RECOVERY_EMAIL_FAILED", "email", recipient, ex.Message);
            }
        }
    }

    private static string BuildEmailHtml(string kind, string title, string severity, string scope, string message)
    {
        var color = severity == "critical" ? "#fb7185" : severity == "recovered" ? "#4ade80" : "#fbbf24";
        return $"""
            <!doctype html><html><body style="margin:0;background:#0f1e2e;color:#eaf2f8;font-family:Segoe UI,Arial,sans-serif">
            <div style="max-width:620px;margin:30px auto;padding:26px;background:#172a3d;border:1px solid #29445d;border-radius:14px">
            <div style="color:{color};font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:.12em">PenguinPanel · {WebUtility.HtmlEncode(kind)}</div>
            <h1 style="font-size:24px;margin:12px 0">{WebUtility.HtmlEncode(title)}</h1>
            <p style="color:#9fb3c5">Scope: <strong style="color:#fff">{WebUtility.HtmlEncode(scope)}</strong></p>
            <div style="padding:15px;background:#102234;border-left:4px solid {color};border-radius:8px">{WebUtility.HtmlEncode(message)}</div>
            <p style="color:#7890a4;font-size:12px;margin-top:22px">Generated {DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture)} by PenguinPanel.</p>
            </div></body></html>
            """;
    }

    private static double Percent(long value, long total) => total <= 0 ? 0 : value * 100d / total;

    private void SetRuntime(bool running, string message, int openCount, int pendingCount)
    {
        lock (_sync)
        {
            _isRunning = running;
            _startedAt ??= DateTime.UtcNow;
            _lastEvaluationAt = DateTime.UtcNow;
            _lastMessage = message;
            _openAlertCount = openCount;
            _pendingConditionCount = pendingCount;
        }
    }

    private sealed record AlertCondition(
        string Key, string ScopeType, string ScopeName, string AlertType, string Severity,
        string Title, string Message, double? CurrentValue, double? ThresholdValue);
}
