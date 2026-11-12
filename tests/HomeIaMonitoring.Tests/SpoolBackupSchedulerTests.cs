using HomeIaMonitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class SpoolBackupSchedulerTests
{
    [Fact]
    public async Task Scheduler_does_nothing_when_disabled()
    {
        using var spool = new TempSpool();
        WriteMetadata(spool.Path);
        var backups = CreateService(spool.Path, System.IO.Path.Combine(spool.Root, "backups"));
        var scheduler = new SpoolBackupSchedulerService(backups,
            new SpoolBackupScheduleOptions(false, 24, 14),
            NullLogger<SpoolBackupSchedulerService>.Instance);

        Assert.Null(await scheduler.RunOnceAsync(CancellationToken.None));
        Assert.Empty(await backups.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Scheduler_creates_a_backup_and_prunes_to_the_retention_count()
    {
        using var spool = new TempSpool();
        WriteMetadata(spool.Path);
        var backups = CreateService(spool.Path, System.IO.Path.Combine(spool.Root, "backups"));
        var scheduler = new SpoolBackupSchedulerService(backups,
            new SpoolBackupScheduleOptions(true, 1, 2),
            NullLogger<SpoolBackupSchedulerService>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await backups.CreateAsync(CancellationToken.None);
            await Task.Delay(10);
        }

        var created = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.NotNull(created);
        var remaining = await backups.ListAsync(CancellationToken.None);
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, item => item.DirectoryName == created!.DirectoryName);
    }

    [Fact]
    public async Task Delete_removes_the_backup_directory()
    {
        using var spool = new TempSpool();
        WriteMetadata(spool.Path);
        var backups = CreateService(spool.Path, System.IO.Path.Combine(spool.Root, "backups"));
        var artifact = await backups.CreateAsync(CancellationToken.None);

        await backups.DeleteAsync(artifact.DirectoryName, CancellationToken.None);

        Assert.Empty(await backups.ListAsync(CancellationToken.None));
        Assert.False(Directory.Exists(System.IO.Path.Combine(spool.Root, "backups", artifact.DirectoryName)));
    }

    private static void WriteMetadata(string spool)
    {
        var path = System.IO.Path.Combine(spool, "clips/manifests/clip.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"schemaVersion":"1.0","clipId":"clip-1"}""");
    }

    private static SpoolBackupService CreateService(string spool, string backupDirectory)
    {
        var monitoring = new MonitoringOptions("garage-01", spool, 20);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SpoolBackupService.BackupDirectoryKey] = backupDirectory,
                [AesGcmBackupArtifactProtector.KeySetting] = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=",
                [AesGcmBackupArtifactProtector.KeyVersionSetting] = "test-1"
            })
            .Build();
        var protector = new AesGcmBackupArtifactProtector(configuration,
            NullLogger<AesGcmBackupArtifactProtector>.Instance);
        return new SpoolBackupService(monitoring, configuration, TimeProvider.System, protector,
            new SpoolBackupMetrics(), NullLogger<SpoolBackupService>.Instance);
    }

    private sealed class TempSpool : IDisposable
    {
        public TempSpool()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "home-ia-monitoring-spool-scheduler-tests", Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(Root, "spool");
            Directory.CreateDirectory(Path);
        }

        public string Root { get; }
        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
