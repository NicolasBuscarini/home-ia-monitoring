using HomeIaMonitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class CaptureWorkerTests
{
    [Fact]
    public async Task Enabled_worker_persists_segments_and_reports_capturing()
    {
        using var spool = new TempSpool();
        var writerOptions = new CaptureWriterOptions(true, 1, 10, 1_048_576);
        var monitoring = new MonitoringOptions("garage-01", spool.Path, 60);
        var capture = Capture(spool.Path, writerOptions);
        var settings = Store(monitoring, writerOptions);
        var adapter = new ScriptedAdapter();
        var worker = new CaptureWorker(capture, new StaticFactory(adapter), settings,
            new SpoolWriter(monitoring, settings), NullLogger<CaptureWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await adapter.SegmentProduced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manifest = await WaitForManifestAsync(spool.Path, TimeSpan.FromSeconds(5));
        Assert.NotNull(manifest);
        Assert.Equal("garage-01", manifest.CameraId);
        Assert.Equal(4, manifest.SizeBytes);
        Assert.Equal(64, manifest.Sha256.Length);
        Assert.Equal("capturing", worker.Status.State);
        Assert.Equal(1, worker.Status.Segments);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disabled_worker_does_not_capture_and_stays_disabled()
    {
        using var spool = new TempSpool();
        var writerOptions = new CaptureWriterOptions(false, 1, 10, 1_048_576);
        var monitoring = new MonitoringOptions("garage-01", spool.Path, 60);
        var capture = Capture(spool.Path, writerOptions);
        var adapter = new ScriptedAdapter();
        var settings = Store(monitoring, writerOptions);
        var worker = new CaptureWorker(capture, new StaticFactory(adapter), settings,
            new SpoolWriter(monitoring, settings), NullLogger<CaptureWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await WaitForStateAsync(worker, "disabled", TimeSpan.FromSeconds(5));
        Assert.Empty(Directory.GetFiles(Path.Combine(spool.Path, "manifests")));
        Assert.False(adapter.Started.Task.IsCompleted);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_records_a_gap_when_the_adapter_fails()
    {
        using var spool = new TempSpool();
        var writerOptions = new CaptureWriterOptions(true, 1, 10, 1_048_576);
        var monitoring = new MonitoringOptions("garage-01", spool.Path, 60);
        var settings = Store(monitoring, writerOptions);
        var worker = new CaptureWorker(Capture(spool.Path, writerOptions),
            new StaticFactory(new FailingAdapter()), settings,
            new SpoolWriter(monitoring, settings), NullLogger<CaptureWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await WaitForFileAsync(Path.Combine(spool.Path, "gaps"), "*.json", TimeSpan.FromSeconds(5));
        Assert.Equal("reconnecting", worker.Status.State);
        Assert.Equal("capture_source_unavailable", worker.Status.LastErrorCode);
        Assert.True(worker.Status.Gaps >= 1);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Settings_update_controls_the_runtime_worker()
    {
        using var spool = new TempSpool();
        var writerOptions = new CaptureWriterOptions(false, 1, 10, 1_048_576);
        var monitoring = new MonitoringOptions("garage-01", spool.Path, 60);
        var settings = Store(monitoring, writerOptions);
        var adapter = new ScriptedAdapter();
        var worker = new CaptureWorker(Capture(spool.Path, writerOptions),
            new StaticFactory(adapter), settings, new SpoolWriter(monitoring, settings),
            NullLogger<CaptureWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await WaitForStateAsync(worker, "disabled", TimeSpan.FromSeconds(5));
        await settings.UpdateAsync(true, 2, 20, 2_097_152);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(worker, "capturing", TimeSpan.FromSeconds(5));
        var persisted = await File.ReadAllTextAsync(settings.SettingsPath);
        Assert.Contains("\"segmentSeconds\":2", persisted);
        await worker.StopAsync(CancellationToken.None);
    }

    private static CaptureOptions Capture(string spool, CaptureWriterOptions writer) =>
        new("synthetic", "garage-01", spool, writer.SegmentSeconds, writer.MaximumSegments,
            writer.MaximumSegmentBytes, null, "ffmpeg", "ffprobe", false, 0);

    private static CaptureSettingsStore Store(MonitoringOptions monitoring, CaptureWriterOptions writer) =>
        new(monitoring, writer, new ConfigurationBuilder().Build());

    private static async Task<SegmentManifestV1> WaitForManifestAsync(string spool, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var directory = Path.Combine(spool, "manifests");
            var path = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.json").FirstOrDefault()
                : null;
            if (path is not null)
            {
                await Task.Delay(50);
                return System.Text.Json.JsonSerializer.Deserialize<SegmentManifestV1>(
                    await File.ReadAllTextAsync(path),
                    new System.Text.Json.JsonSerializerOptions(
                        System.Text.Json.JsonSerializerDefaults.Web))!;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("manifest_not_written");
    }

    private static async Task WaitForStateAsync(CaptureWorker worker, string state, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (worker.Status.State == state) return;
            await Task.Delay(50);
        }
        Assert.Equal(state, worker.Status.State);
    }

    private static async Task WaitForFileAsync(string directory, string pattern, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Directory.Exists(directory) && Directory.GetFiles(directory, pattern).Length > 0) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("file_not_written");
    }

    private sealed class StaticFactory(ISegmentCaptureAdapter adapter) : ICaptureAdapterFactory
    {
        public ISegmentCaptureAdapter Create(CaptureRuntimeSettings settings) => adapter;
        public ISegmentCaptureAdapter Describe() => adapter;
    }

    private sealed class ScriptedAdapter : ISegmentCaptureAdapter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SegmentProduced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "scripted";
        public string Version => "1.0";
        public IReadOnlyList<string> Capabilities => ["synthetic-video"];

        public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(workDirectory);
            Started.TrySetResult();
            var path = Path.Combine(workDirectory, "segment_00000000.ts");
            await File.WriteAllBytesAsync(path, [0x47, 0x40, 0x11, 0x10], cancellationToken);
            yield return new CapturedSegment(path, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow);
            SegmentProduced.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingAdapter : ISegmentCaptureAdapter
    {
        public string Name => "failing";
        public string Version => "1.0";
        public IReadOnlyList<string> Capabilities => [];

        public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("capture_source_unavailable");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class TempSpool : IDisposable
    {
        public TempSpool()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "home-ia-monitoring-worker-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "manifests"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "gaps"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
