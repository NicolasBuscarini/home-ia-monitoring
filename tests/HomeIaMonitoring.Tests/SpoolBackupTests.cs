using HomeIaMonitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class SpoolBackupTests
{
    [Fact]
    public async Task Backup_copies_only_durable_metadata_and_restores_it_into_an_isolated_directory()
    {
        using var spool = new TempSpool();
        WriteMetadata(spool.Path);

        var backupDirectory = Path.Combine(spool.Root, "backups");
        var service = CreateService(spool.Path, backupDirectory);

        var artifact = await service.CreateAsync(CancellationToken.None);

        Assert.Equal(4, artifact.FileCount);
        Assert.True(artifact.SizeBytes > 0);
        Assert.Matches("^[0-9a-f]{64}$", artifact.Sha256);

        var backupRoot = Path.Combine(backupDirectory, artifact.DirectoryName);
        Assert.True(File.Exists(Path.Combine(backupRoot, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(backupRoot, "clips/manifests/clip.json")));
        Assert.True(File.Exists(Path.Combine(backupRoot, "events/event.json")));
        Assert.True(File.Exists(Path.Combine(backupRoot, "analysis/results/result.json")));
        Assert.True(File.Exists(Path.Combine(backupRoot, "capture-settings.json")));

        // Media and ephemeral capture state must not be backed up.
        Assert.False(File.Exists(Path.Combine(backupRoot, "clips/media/clip.ts")));
        Assert.False(File.Exists(Path.Combine(backupRoot, "segments/segment.ts")));
        Assert.False(File.Exists(Path.Combine(backupRoot, "manifests/segment.json")));
        Assert.False(File.Exists(Path.Combine(backupRoot, "biometrics/enrollments/bio.json")));

        var listed = Assert.Single(await service.ListAsync(CancellationToken.None));
        Assert.Equal(artifact.DirectoryName, listed.DirectoryName);
        Assert.Equal(artifact.Sha256, listed.Sha256);
        Assert.Equal(artifact.FileCount, listed.FileCount);
        Assert.Equal(artifact.SizeBytes, listed.SizeBytes);

        var targetDirectory = Path.Combine(spool.Root, "restored");
        var result = await service.RestoreAsync(artifact.DirectoryName, targetDirectory, CancellationToken.None);

        Assert.Equal(targetDirectory, result.RestoredDirectory);
        Assert.Equal(artifact.FileCount, result.FileCount);
        Assert.Equal(artifact.SizeBytes, result.SizeBytes);
        Assert.True(File.Exists(Path.Combine(targetDirectory, "clips/manifests/clip.json")));
        Assert.True(File.Exists(Path.Combine(targetDirectory, "events/event.json")));
        Assert.True(File.Exists(Path.Combine(targetDirectory, "analysis/results/result.json")));
        Assert.False(File.Exists(Path.Combine(targetDirectory, "clips/media/clip.ts")));
    }

    [Fact]
    public async Task Restore_refuses_when_a_backed_up_file_was_tampered()
    {
        using var spool = new TempSpool();
        WriteMetadata(spool.Path);

        var backupDirectory = Path.Combine(spool.Root, "backups");
        var service = CreateService(spool.Path, backupDirectory);
        var artifact = await service.CreateAsync(CancellationToken.None);

        var clipManifest = Path.Combine(backupDirectory, artifact.DirectoryName, "clips/manifests/clip.json");
        await File.AppendAllTextAsync(clipManifest, "tampered");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreAsync(artifact.DirectoryName, Path.Combine(spool.Root, "restored"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Restore_refuses_when_the_manifest_is_missing()
    {
        using var spool = new TempSpool();
        WriteMetadata(spool.Path);

        var backupDirectory = Path.Combine(spool.Root, "backups");
        var service = CreateService(spool.Path, backupDirectory);
        var artifact = await service.CreateAsync(CancellationToken.None);
        File.Delete(Path.Combine(backupDirectory, artifact.DirectoryName, "manifest.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreAsync(artifact.DirectoryName, Path.Combine(spool.Root, "restored"),
                CancellationToken.None));
    }

    private static void WriteMetadata(string spool)
    {
        Write(spool, "clips/manifests/clip.json", """{"schemaVersion":"1.0","clipId":"clip-1"}""");
        Write(spool, "events/event.json", """{"schemaVersion":"1.0","eventId":"evt-1"}""");
        Write(spool, "analysis/results/result.json", """{"schemaVersion":"1.0","clipPublicId":"CLP1"}""");
        Write(spool, "capture-settings.json", """{"enabled":true}""");

        // These must be ignored: media, ephemeral segments/manifests and biometric ciphertext.
        Write(spool, "clips/media/clip.ts", "media-bytes");
        Write(spool, "segments/segment.ts", "segment-bytes");
        Write(spool, "manifests/segment.json", """{"schemaVersion":"1.0"}""");
        Write(spool, "biometrics/enrollments/bio.json", "ciphertext");
    }

    private static void Write(string spool, string relative, string content)
    {
        var path = Path.Combine(spool, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static SpoolBackupService CreateService(string spool, string backupDirectory)
    {
        var monitoring = new MonitoringOptions("garage-01", spool, 20);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SpoolBackupService.BackupDirectoryKey] = backupDirectory
            })
            .Build();
        return new SpoolBackupService(monitoring, configuration, TimeProvider.System,
            NullLogger<SpoolBackupService>.Instance);
    }

    private sealed class TempSpool : IDisposable
    {
        public TempSpool()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "home-ia-monitoring-spool-backup-tests", Guid.NewGuid().ToString("N"));
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
