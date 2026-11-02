using HomeIaMonitoring;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class SpoolWriterTests
{
    [Fact]
    public async Task Enabled_writer_persists_segment_and_manifest_with_checksum()
    {
        using var spool = new TempSpool();
        var writer = Writer(spool.Path, enabled: true);
        await using var source = new MemoryStream([0x47, 0x40, 0x11, 0x10]);
        var manifest = await writer.StoreAsync(source, DateTimeOffset.UtcNow.AddSeconds(-2),
            DateTimeOffset.UtcNow, "epoch-1");

        Assert.Equal("garage-01", manifest.CameraId);
        Assert.Equal("epoch-1", manifest.StreamEpoch);
        Assert.Equal(4, manifest.SizeBytes);
        Assert.Equal(64, manifest.Sha256.Length);
        Assert.True(File.Exists(Path.Combine(spool.Path, "segments", manifest.FileName)));
        Assert.True(File.Exists(Path.Combine(spool.Path, "manifests", manifest.SegmentId + ".json")));
        Assert.Single(writer.ReadManifests());
    }

    [Fact]
    public async Task Disabled_writer_fails_closed()
    {
        using var spool = new TempSpool();
        var writer = Writer(spool.Path, enabled: false);
        await using var source = new MemoryStream([0x47]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.StoreAsync(source,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "epoch-1"));
        Assert.Empty(writer.ReadManifests());
    }

    [Fact]
    public async Task Writer_rejects_segment_above_size_limit()
    {
        using var spool = new TempSpool();
        var writer = Writer(spool.Path, enabled: true, maximumBytes: 1_048_576);
        await using var source = new MemoryStream(new byte[1_048_577]);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.StoreAsync(source,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "epoch-1"));
        Assert.Empty(writer.ReadManifests());
    }

    private static SpoolWriter Writer(string spool, bool enabled, long maximumBytes = 67_108_864)
    {
        var monitoring = new MonitoringOptions("garage-01", spool, 20);
        var options = new CaptureWriterOptions(enabled, 5, 120, maximumBytes);
        var settings = new CaptureSettingsStore(monitoring, options,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        return new SpoolWriter(monitoring, settings);
    }

    private sealed class TempSpool : IDisposable
    {
        public TempSpool()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "home-ia-monitoring-writer-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
