using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeIaMonitoring;

/// <summary>
/// Scheduling of the verified spool metadata backup. Disabled by default; when enabled, a daily
/// run creates one artifact and prunes the oldest artifacts beyond <see cref="RetentionCount"/>.
/// </summary>
public sealed record SpoolBackupScheduleOptions(bool Enabled, int IntervalHours, int RetentionCount)
{
    public const string EnabledKey = "MONITORING_BACKUP_SCHEDULE_ENABLED";
    public const string IntervalHoursKey = "MONITORING_BACKUP_SCHEDULE_INTERVAL_HOURS";
    public const string RetentionCountKey = "MONITORING_BACKUP_SCHEDULE_RETENTION_COUNT";

    public static SpoolBackupScheduleOptions FromConfiguration(IConfiguration configuration)
    {
        var enabled = false;
        if (configuration[EnabledKey] is { } raw && !bool.TryParse(raw, out enabled))
            throw new InvalidOperationException($"{EnabledKey} deve ser true ou false.");
        return new SpoolBackupScheduleOptions(enabled,
            Bounded(configuration[IntervalHoursKey], 24, 1, 168, IntervalHoursKey),
            Bounded(configuration[RetentionCountKey], 14, 1, 100, RetentionCountKey));
    }

    private static int Bounded(string? raw, int fallback, int minimum, int maximum, string name)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} fora do limite permitido.");
        return value;
    }
}

public sealed class SpoolBackupSchedulerService(
    ISpoolBackupService backups,
    SpoolBackupScheduleOptions options,
    ILogger<SpoolBackupSchedulerService> logger)
{
    public async Task<SpoolBackupArtifact?> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) return null;

        var artifact = await backups.CreateAsync(cancellationToken);
        await PruneAsync(cancellationToken);
        logger.LogInformation("Backup agendado do spool criado: {DirectoryName} ({SizeBytes} bytes).",
            artifact.DirectoryName, artifact.SizeBytes);
        return artifact;
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        var artifacts = await backups.ListAsync(cancellationToken);
        var excess = artifacts
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.DirectoryName, StringComparer.Ordinal)
            .Take(Math.Max(0, artifacts.Count - options.RetentionCount))
            .ToArray();
        foreach (var item in excess)
        {
            await backups.DeleteAsync(item.DirectoryName, cancellationToken);
            logger.LogInformation("Backup do spool removido por retenção: {DirectoryName}.", item.DirectoryName);
        }
    }
}

public sealed class SpoolBackupSchedulerWorker(
    IServiceScopeFactory scopeFactory,
    SpoolBackupScheduleOptions options,
    ILogger<SpoolBackupSchedulerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;

        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.IntervalHours));
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SpoolBackupSchedulerService>()
                    .RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Falha no backup agendado do spool; nova tentativa no próximo ciclo.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
