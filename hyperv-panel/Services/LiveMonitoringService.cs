namespace HyperVPanel.Services;

public sealed class LiveMonitoringService(
    PowerShellService powerShell,
    IConfiguration configuration,
    ILogger<LiveMonitoringService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dictionary<string, VmMonitoringMeterSample> _previousSamples = new(StringComparer.OrdinalIgnoreCase);
    private LiveMonitoringOverview _snapshot = new(null, [], DateTime.MinValue, 5, 20, string.Empty);

    public bool Enabled => configuration.GetValue("Monitoring:Enabled", true);
    public int PollSeconds => Math.Clamp(configuration.GetValue("Monitoring:PollSeconds", 5), 3, 60);
    public int StaleAfterSeconds => Math.Clamp(configuration.GetValue("Monitoring:StaleAfterSeconds", 20), PollSeconds * 2, 300);
    private bool AutoEnableResourceMetering => configuration.GetValue("Monitoring:AutoEnableResourceMetering", true);

    public LiveMonitoringOverview GetSnapshot() => Volatile.Read(ref _snapshot);

    public async Task<LiveMonitoringOverview> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var current = GetSnapshot();
        if (Enabled && (!current.HasSample || current.IsStale))
        {
            await RefreshNowAsync(cancellationToken);
        }

        return GetSnapshot();
    }

    public async Task<LiveMonitoringOverview> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            var disabled = GetSnapshot() with
            {
                PollSeconds = PollSeconds,
                StaleAfterSeconds = StaleAfterSeconds,
                LastError = "Live monitoring is disabled in appsettings."
            };
            Volatile.Write(ref _snapshot, disabled);
            return disabled;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var batch = await powerShell.GetLiveMonitoringBatchAsync(AutoEnableResourceMetering);
            var vms = new List<VmLiveMonitoringSnapshot>(batch.Vms.Count);

            foreach (var sample in batch.Vms)
            {
                _previousSamples.TryGetValue(sample.VmName, out var previous);
                var elapsedSeconds = previous is null
                    ? 0
                    : Math.Max(0, (sample.SampledAt - previous.SampledAt).TotalSeconds);

                vms.Add(new VmLiveMonitoringSnapshot(
                    sample.VmName,
                    sample.State,
                    sample.Status,
                    sample.CpuUsagePercent,
                    sample.MemoryAssignedBytes,
                    sample.MemoryDemandBytes,
                    sample.Uptime,
                    sample.HeartbeatStatus,
                    sample.HeartbeatHealthy,
                    sample.NetworkAdapterCount,
                    sample.ConnectedNetworkAdapterCount,
                    sample.ResourceMeteringEnabled,
                    Rate(sample.NetworkReceivedBytes, previous?.NetworkReceivedBytes, elapsedSeconds),
                    Rate(sample.NetworkSentBytes, previous?.NetworkSentBytes, elapsedSeconds),
                    Rate(sample.DiskReadBytes, previous?.DiskReadBytes, elapsedSeconds),
                    Rate(sample.DiskWrittenBytes, previous?.DiskWrittenBytes, elapsedSeconds),
                    sample.SampledAt));

                _previousSamples[sample.VmName] = sample;
            }

            var currentNames = batch.Vms.Select(item => item.VmName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var removedName in _previousSamples.Keys.Where(name => !currentNames.Contains(name)).ToList())
            {
                _previousSamples.Remove(removedName);
            }

            var next = new LiveMonitoringOverview(
                batch.Host,
                vms.OrderBy(item => item.VmName, StringComparer.OrdinalIgnoreCase).ToList(),
                batch.SampledAt,
                PollSeconds,
                StaleAfterSeconds,
                string.Empty);
            Volatile.Write(ref _snapshot, next);
            return next;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hyper-V live monitoring refresh failed.");
            var failed = GetSnapshot() with
            {
                PollSeconds = PollSeconds,
                StaleAfterSeconds = StaleAfterSeconds,
                LastError = ex.Message
            };
            Volatile.Write(ref _snapshot, failed);
            return failed;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            await RefreshNowAsync(stoppingToken);
            return;
        }

        await RefreshNowAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(PollSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshNowAsync(stoppingToken);
        }
    }

    private static double Rate(long current, long? previous, double elapsedSeconds)
    {
        if (previous is null || elapsedSeconds <= 0 || current < previous.Value)
        {
            return 0;
        }

        return (current - previous.Value) / elapsedSeconds;
    }
}
