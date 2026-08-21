namespace HyperVPanel.Services;

public sealed class AbuseMonitorService(
    PanelService panel,
    AppDatabase db,
    ILogger<AbuseMonitorService> logger) : BackgroundService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTime> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AbuseMonitorVmStatus> _vmStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    private bool _isRunning;
    private DateTime? _startedAt;
    private DateTime? _lastLoopAt;
    private DateTime? _lastScanAt;
    private string _lastMessage = "Automatic monitor has not started yet.";

    public AbuseMonitorRuntimeStatus GetStatus()
    {
        lock (_sync)
        {
            return new AbuseMonitorRuntimeStatus(
                _isRunning,
                _startedAt,
                _lastLoopAt,
                _lastScanAt,
                _lastMessage,
                _vmStatuses.Values.OrderBy(item => item.VmName).ToList());
        }
    }

    public void RequestImmediateScan()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A scan request is already queued.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetMonitorState(true, "Automatic monitor starting...");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(30);

                try
                {
                    var config = await db.GetAbuseConfigAsync();
                    delay = TimeSpan.FromSeconds(Math.Clamp(config.ScanIntervalSeconds, 10, 3600));
                    SetLastLoop($"Automatic monitor checked settings. Enabled={config.Enabled}, AutoScan={config.AutoScanEnabled}, interval={delay.TotalSeconds:0}s.");

                    if (config.Enabled && config.AutoScanEnabled)
                    {
                        await ScanAllRunningVmsAsync(config, stoppingToken);
                    }
                    else
                    {
                        SetLastLoop("Automatic monitor is idle because detection or automatic scanning is disabled.");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Automatic abuse monitor failed.");
                    SetLastLoop($"Automatic monitor failed: {ex.Message}");
                }

                try
                {
                    var delayTask = Task.Delay(delay, stoppingToken);
                    var wakeTask = _wakeSignal.WaitAsync(stoppingToken);
                    await Task.WhenAny(delayTask, wakeTask);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            SetMonitorState(false, "Automatic monitor stopped.");
        }
    }

    private async Task ScanAllRunningVmsAsync(AbuseConfig config, CancellationToken stoppingToken)
    {
        var vms = await panel.GetVmsAsync();
        var running = vms.Where(IsRunningVm).ToList();
        var now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromMinutes(Math.Clamp(config.CooldownMinutes, 1, 1440));

        if (vms.Count == 0)
        {
            SetLastScan("Automatic monitor found no running virtual servers to scan.");
            return;
        }

        if (running.Count == 0)
        {
            SetLastScan($"Automatic monitor found {vms.Count} VM(s), but none looked running. States: {string.Join(", ", vms.Select(vm => $"{vm.Name}={vm.State}"))}.");
            foreach (var vm in vms)
            {
                SetVmStatus(new AbuseMonitorVmStatus(vm.Name, DateTime.UtcNow, $"Skipped because state is '{vm.State}'.", 0, false, null));
            }
            return;
        }

        SetLastScan($"Automatic monitor scanning {running.Count} running VM(s) out of {vms.Count} total VM(s)...");

        foreach (var vm in running)
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                if (_cooldowns.TryGetValue(vm.Name, out var until) && until > now)
                {
                    SetVmStatus(new AbuseMonitorVmStatus(vm.Name, DateTime.UtcNow, $"Cooldown active until {until.ToLocalTime():yyyy-MM-dd HH:mm:ss}.", 0, false, until));
                    continue;
                }

                SetVmStatus(new AbuseMonitorVmStatus(vm.Name, DateTime.UtcNow, $"Scanning now. State={vm.State}.", 0, false, null));

                var result = await panel.AutoScanVmForOutboundAbuseAsync(vm.Name);
                logger.LogInformation("Auto abuse scan for {VmName}: {Result}", vm.Name, result.Message);

                DateTime? cooldownUntil = null;
                if (result.Detected)
                {
                    cooldownUntil = DateTime.UtcNow.Add(cooldown);
                    _cooldowns[vm.Name] = cooldownUntil.Value;
                }

                SetVmStatus(new AbuseMonitorVmStatus(
                    vm.Name,
                    result.CreatedAt,
                    result.Message,
                    result.OutboundMbps,
                    result.Detected,
                    cooldownUntil));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Automatic abuse scan failed for {VmName}.", vm.Name);
                SetVmStatus(new AbuseMonitorVmStatus(vm.Name, DateTime.UtcNow, $"Automatic scan failed: {ex.Message}", 0, false, null));
            }
        }

        foreach (var expired in _cooldowns.Where(item => item.Value <= DateTime.UtcNow).Select(item => item.Key).ToList())
        {
            _cooldowns.Remove(expired);
        }
    }

    private static bool IsRunningVm(VmInfo vm)
    {
        var state = (vm.State ?? string.Empty).Trim();
        return state.Equals("Running", StringComparison.OrdinalIgnoreCase)
            || state.Equals("2", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Running", StringComparison.OrdinalIgnoreCase);
    }

    private void SetMonitorState(bool running, string message)
    {
        lock (_sync)
        {
            _isRunning = running;
            _startedAt ??= DateTime.UtcNow;
            _lastMessage = message;
        }
    }

    private void SetLastLoop(string message)
    {
        lock (_sync)
        {
            _lastLoopAt = DateTime.UtcNow;
            _lastMessage = message;
        }
    }

    private void SetLastScan(string message)
    {
        lock (_sync)
        {
            _lastScanAt = DateTime.UtcNow;
            _lastMessage = message;
        }
    }

    private void SetVmStatus(AbuseMonitorVmStatus status)
    {
        lock (_sync)
        {
            _vmStatuses[status.VmName] = status;
            _lastScanAt = status.LastScanAt;
            _lastMessage = status.Result;
        }
    }
}
