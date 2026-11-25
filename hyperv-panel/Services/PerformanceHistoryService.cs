namespace HyperVPanel.Services;

public sealed class PerformanceHistoryService(
    LiveMonitoringService monitoring,
    AppDatabase db,
    IConfiguration configuration,
    ILogger<PerformanceHistoryService> logger) : BackgroundService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _collectionLock = new(1, 1);
    private bool _isRunning;
    private DateTime? _lastStoredAt;
    private DateTime? _lastCleanupAt;
    private DateTime? _lastCountRefreshAt;
    private string _lastMessage = "Performance history has not started yet.";
    private long _hostSampleCount;
    private long _vmSampleCount;

    public int CollectionSeconds => Math.Clamp(configuration.GetValue("PerformanceHistory:CollectionSeconds", 60), 60, 900);

    public PerformanceHistoryRuntimeStatus GetStatus()
    {
        lock (_sync)
        {
            return new PerformanceHistoryRuntimeStatus(_isRunning, _lastStoredAt, _lastCleanupAt, _lastMessage,
                CollectionSeconds, _hostSampleCount, _vmSampleCount);
        }
    }

    public async Task CollectNowAsync(CancellationToken cancellationToken = default)
    {
        await _collectionLock.WaitAsync(cancellationToken);
        try { await CollectAsync(cancellationToken); }
        finally { _collectionLock.Release(); }
    }

    public async Task CleanupNowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await db.GetPerformanceHistoryConfigAsync();
        var deleted = await db.CleanupPerformanceHistoryAsync(config.RetentionDays);
        var counts = await db.GetPerformanceHistoryCountsAsync();
        lock (_sync)
        {
            _lastCleanupAt = DateTime.UtcNow;
            _hostSampleCount = counts.HostSamples;
            _vmSampleCount = counts.VmSamples;
            _lastCountRefreshAt = DateTime.UtcNow;
            _lastMessage = deleted.HostDeleted + deleted.VmDeleted == 0
                ? $"History is healthy; retaining {config.RetentionDays} days."
                : $"Retention cleanup removed {deleted.HostDeleted} host and {deleted.VmDeleted} VPS samples.";
        }
    }

    public async Task<(int HostDeleted, int VmDeleted)> ClearNowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deleted = await db.ClearPerformanceHistoryAsync();
        lock (_sync)
        {
            _hostSampleCount = 0;
            _vmSampleCount = 0;
            _lastMessage = $"History was cleared: {deleted.HostDeleted} host and {deleted.VmDeleted} VPS samples removed.";
        }
        return deleted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetRunning(true, "Performance history is starting.");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken);
            await CleanupNowAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await CollectNowAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Performance history collection failed.");
                    SetRunning(true, $"History collection failed: {ex.Message}");
                }
                await Task.Delay(TimeSpan.FromSeconds(CollectionSeconds), stoppingToken);
            }
        }
        finally { SetRunning(false, "Performance history stopped."); }
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        var config = await db.GetPerformanceHistoryConfigAsync();
        if (_lastCleanupAt is null || DateTime.UtcNow - _lastCleanupAt.Value >= TimeSpan.FromHours(6))
        {
            await CleanupNowAsync(cancellationToken);
        }
        if (!config.Enabled)
        {
            SetRunning(true, "Performance history is disabled in Alerts & Notifications.");
            return;
        }
        if (!monitoring.Enabled)
        {
            SetRunning(true, "Performance history is idle because live monitoring is disabled.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = monitoring.GetSnapshot();
        if (!snapshot.HasSample || snapshot.IsStale || snapshot.Host is null)
        {
            SetRunning(true, "Waiting for a fresh live-monitoring sample; stale data was not stored.");
            return;
        }

        await db.StorePerformanceSnapshotAsync(snapshot);
        var refreshCounts = _lastCountRefreshAt is null || DateTime.UtcNow - _lastCountRefreshAt.Value >= TimeSpan.FromMinutes(15);
        var counts = refreshCounts
            ? await db.GetPerformanceHistoryCountsAsync()
            : (HostSamples: _hostSampleCount, VmSamples: _vmSampleCount);
        lock (_sync)
        {
            _lastStoredAt = DateTime.UtcNow;
            _hostSampleCount = counts.HostSamples;
            _vmSampleCount = counts.VmSamples;
            if (refreshCounts) _lastCountRefreshAt = DateTime.UtcNow;
            _lastMessage = $"Stored the {snapshot.SampledAt.ToLocalTime():HH:mm} sample for the host and {snapshot.Vms.Count} VPS.";
        }

    }

    private void SetRunning(bool running, string message)
    {
        lock (_sync) { _isRunning = running; _lastMessage = message; }
    }
}
