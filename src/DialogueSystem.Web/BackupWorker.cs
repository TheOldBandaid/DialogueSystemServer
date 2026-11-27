using DialogueSystem.Infrastructure;

namespace DialogueSystem.Web;

internal sealed class BackupWorker(DatabaseBackups backups, IConfiguration configuration, ILogger<BackupWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("DialogueSystem:Backups:Enabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await backups.EnsureDailyAsync(stoppingToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "Не удалось создать ежедневную резервную копию базы.");
            }
        }
    }
}
