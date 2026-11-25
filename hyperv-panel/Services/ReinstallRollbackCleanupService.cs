namespace HyperVPanel.Services;

public sealed class ReinstallRollbackCleanupService(
    PanelService panel,
    IConfiguration configuration,
    ILogger<ReinstallRollbackCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupDelay = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("ReinstallRollback:CleanupStartupDelaySeconds", 20),
            0,
            300));

        if (startupDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(startupDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var enabled = configuration.GetValue(
                "ReinstallRollback:CleanupEnabled",
                true);
            var interval = TimeSpan.FromMinutes(Math.Clamp(
                configuration.GetValue("ReinstallRollback:CleanupIntervalMinutes", 15),
                1,
                1440));

            if (enabled)
            {
                try
                {
                    var result = await panel.CleanupExpiredReinstallRollbacksAsync(
                        stoppingToken);

                    if (result.Scanned > 0 || result.Failed > 0)
                    {
                        logger.LogInformation(
                            "Expired reinstall rollback cleanup scanned {Scanned}, deleted {Deleted}, failed {Failed}, and released approximately {FreedBytes} bytes.",
                            result.Scanned,
                            result.Deleted,
                            result.Failed,
                            result.FreedBytes);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Expired reinstall rollback cleanup failed. It will retry after {IntervalMinutes} minute(s).",
                        interval.TotalMinutes);
                }
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
