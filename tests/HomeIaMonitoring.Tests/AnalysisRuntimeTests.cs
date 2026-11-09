using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeIaMonitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class AnalysisRuntimeTests
{
    [Fact]
    public void Yolox_decoder_maps_raw_rows_and_applies_class_nms()
    {
        const int width = 416;
        const int height = 416;
        var values = new float[3549 * 85];
        WriteYoloxRow(values, row: 0, x: 0.5f, y: 0.5f, boxWidth: 32, boxHeight: 32, score: 0.9f);
        WriteYoloxRow(values, row: 1, x: 0.5f, y: 0.5f, boxWidth: 32, boxHeight: 32, score: 0.8f);

        var detections = OnnxAnalysisDetector.DecodeYolox(values, width, height, 0.35f, 0.45f,
            AnalysisDetectorOptions.CocoLabels, 256);

        var detection = Assert.Single(detections);
        Assert.Equal("person", detection.ClassName);
        Assert.Equal(0.9f, detection.Score, 3);
        Assert.Equal(0, detection.X1, 3);
        Assert.Equal(0.0481f, detection.X2, 3);
    }

    [Fact]
    public void Detector_options_read_the_documented_onnx_io_keys_and_expand_coco_labels()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MONITORING_DETECTOR_ONNX_MODEL_PATH"] = "/models/yolox_nano.onnx",
            ["MONITORING_DETECTOR_ONNX_INPUT_NAME"] = "images",
            ["MONITORING_DETECTOR_ONNX_OUTPUT_NAME"] = "output",
            ["MONITORING_DETECTOR_ADAPTER"] = "yolox",
            ["MONITORING_DETECTOR_LABELS"] = "coco",
            ["MONITORING_DETECTOR_INPUT_WIDTH"] = "416",
            ["MONITORING_DETECTOR_INPUT_HEIGHT"] = "416"
        }).Build();

        var options = AnalysisDetectorOptions.FromConfiguration(configuration);

        Assert.Equal("images", options.InputName);
        Assert.Equal("output", options.OutputName);
        Assert.Equal(80, options.Labels.Length);
        Assert.Equal("person", options.Labels[0]);
    }

    [Fact]
    public void Yolox_decoder_rejects_a_flat_contract_output()
    {
        Assert.Throws<InvalidDataException>(() => OnnxAnalysisDetector.DecodeYolox(
            new float[6], 416, 416, 0.35f, 0.45f, AnalysisDetectorOptions.CocoLabels, 256));
    }

    private static void WriteYoloxRow(float[] values, int row, float x, float y, float boxWidth, float boxHeight,
        float score)
    {
        var offset = row * 85;
        values[offset] = x;
        values[offset + 1] = y;
        values[offset + 2] = MathF.Log(boxWidth / 8);
        values[offset + 3] = MathF.Log(boxHeight / 8);
        values[offset + 4] = 1;
        values[offset + 5] = score;
    }

    [Fact]
    public async Task Worker_persists_result_and_completes_job_with_lease()
    {
        using var spool = new TempSpool();
        var detector = new FakeDetector();
        var frames = new FakeFrameReader();
        await using var factory = Factory(spool.Path, detector, frames);
        using var client = factory.CreateClient();

        var job = await EnqueueAsync(client, "CLP000000000001");
        var worker = factory.Services.GetRequiredService<AnalysisWorker>();
        Assert.True(await worker.RunOnceAsync(CancellationToken.None));

        using var details = await client.GetAsync($"/api/v1/monitoring/analysis/jobs/{job}");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var view = await details.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", view.GetProperty("state").GetString());
        Assert.Equal(1, view.GetProperty("attemptCount").GetInt32());
        Assert.Equal(64, view.GetProperty("resultSha256").GetString()!.Length);

        using var result = await client.GetAsync("/api/v1/monitoring/analysis/results/CLP000000000001");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var payload = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, payload.GetProperty("framesAnalyzed").GetInt32());
        Assert.Equal("person", payload.GetProperty("objects")[0].GetProperty("className").GetString());
        Assert.Equal("analysis-worker-test", payload.GetProperty("modelVersion").GetString());

        using var byClip = await client.GetAsync(
            "/api/v1/monitoring/analysis/clips?clipPublicIds=CLP000000000001");
        Assert.Equal("completed",
            (await byClip.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("state").GetString());

        await Assert.ThrowsAsync<AnalysisConflictException>(() => factory.Services
            .GetRequiredService<AnalysisDispatchStore>()
            .CompleteAsync(job, Guid.NewGuid(), "completed", new string('a', 64), null, CancellationToken.None));
    }

    [Fact]
    public async Task Worker_retries_then_fails_and_completed_jobs_survive_restart()
    {
        using var spool = new TempSpool();
        var detector = new FakeDetector();
        var frames = new FakeFrameReader { Failing = true };
        string job;
        await using (var factory = Factory(spool.Path, detector, frames))
        {
            using var client = factory.CreateClient();
            job = await EnqueueAsync(client, "CLP000000000002");
            var worker = factory.Services.GetRequiredService<AnalysisWorker>();

            Assert.True(await worker.RunOnceAsync(CancellationToken.None));
            var pending = await GetJobAsync(client, job);
            Assert.Equal("analysis_pending", pending.GetProperty("state").GetString());
            Assert.Equal(1, pending.GetProperty("attemptCount").GetInt32());
            Assert.Equal("analysis_clip_playback_unavailable", pending.GetProperty("failureCode").GetString());

            Assert.True(await worker.RunOnceAsync(CancellationToken.None));
            var failed = await GetJobAsync(client, job);
            Assert.Equal("failed", failed.GetProperty("state").GetString());
            Assert.Equal("failed", failed.GetProperty("outcome").GetString());
        }

        detector = new FakeDetector();
        frames.Failing = false;
        await using (var restarted = Factory(spool.Path, detector, frames))
        {
            using var client = restarted.CreateClient();
            using var recovery = await client.GetAsync("/api/v1/monitoring/analysis/recovery");
            var summary = await recovery.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, summary.GetProperty("invalidFiles").GetInt32());
            Assert.Equal(1, summary.GetProperty("validJobs").GetInt32());
            var failed = await GetJobAsync(client, job);
            Assert.Equal("failed", failed.GetProperty("state").GetString());
        }
    }

    [Fact]
    public async Task Failed_job_can_be_retried_with_a_new_idempotency_key()
    {
        using var spool = new TempSpool();
        var frames = new FakeFrameReader { Failing = true };
        await using var factory = Factory(spool.Path, new FakeDetector(), frames);
        using var client = factory.CreateClient();
        var first = await EnqueueAsync(client, "CLP000000000004");
        var worker = factory.Services.GetRequiredService<AnalysisWorker>();
        Assert.True(await worker.RunOnceAsync(CancellationToken.None));
        Assert.True(await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal("failed", (await GetJobAsync(client, first)).GetProperty("state").GetString());

        var retried = await EnqueueAsync(client, "CLP000000000004");

        Assert.NotEqual(first, retried);
        Assert.Equal("analysis_pending", (await GetJobAsync(client, retried)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Disabled_runtime_does_not_run_or_publish_execute_capability()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path, new FakeDetector(), new FakeFrameReader(), enabled: false);
        using var client = factory.CreateClient();
        await EnqueueAsync(client, "CLP000000000003");
        var worker = factory.Services.GetRequiredService<AnalysisWorker>();

        Assert.False(await worker.RunOnceAsync(CancellationToken.None));
        using var capabilities = await client.GetAsync("/api/v1/monitoring/capabilities");
        var body = await capabilities.Content.ReadFromJsonAsync<JsonElement>();
        var names = body.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("analysis.read", names);
        Assert.DoesNotContain("analysis.execute", names);
    }

    private static async Task<string> EnqueueAsync(HttpClient client, string clipPublicId)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/monitoring/analysis/jobs", new
        {
            clipId = "clip-" + clipPublicId[^4..],
            clipPublicId,
            cameraId = "garage-01",
            contextId = "garage",
            idempotencyKey = Guid.NewGuid(),
            analysisVersion = "analysis-v1",
            requestKind = "manual"
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("publicId").GetString()!;
    }

    private static async Task<JsonElement> GetJobAsync(HttpClient client, string publicId)
    {
        using var response = await client.GetAsync($"/api/v1/monitoring/analysis/jobs/{publicId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static WebApplicationFactory<Program> Factory(string spool, IAnalysisDetector detector,
        IAnalysisFrameReader frames, bool enabled = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MONITORING_CAMERA_ID"] = "garage-01",
                    ["MONITORING_SPOOL_DIRECTORY"] = spool,
                    ["MONITORING_ANALYSIS_LEASE_SECONDS"] = "30",
                    ["MONITORING_ANALYSIS_MAX_FRAMES"] = "2",
                    ["MONITORING_ANALYSIS_MAX_ATTEMPTS"] = "2",
                    ["MONITORING_ANALYSIS_ENABLED"] = enabled ? "true" : "false",
                    ["MONITORING_DETECTOR_ONNX_MODEL_PATH"] = "/tmp/fake-analysis.onnx",
                    ["MONITORING_DETECTOR_MODEL_VERSION"] = "analysis-worker-test",
                    ["MONITORING_DETECTOR_LABELS"] = "person",
                    ["MONITORING_DETECTOR_INPUT_WIDTH"] = "32",
                    ["MONITORING_DETECTOR_INPUT_HEIGHT"] = "32"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAnalysisDetector>(detector);
                services.AddSingleton<IAnalysisFrameReader>(frames);
                services.AddSingleton<IClipPlaybackSource>(new FakeClipSource());
            });
        });

    private sealed class FakeDetector : IAnalysisDetector
    {
        public string ModelVersion => "analysis-worker-test";

        public ValueTask<IReadOnlyList<AnalysisDetection>> DetectAsync(AnalysisFrame frame,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AnalysisDetection>>([new AnalysisDetection("person", 0, 0.9f,
                0.1f, 0.1f, 0.9f, 0.9f)]);
    }

    private sealed class FakeFrameReader : IAnalysisFrameReader
    {
        public bool Failing { get; set; }

        public async IAsyncEnumerable<AnalysisFrame> ReadAsync(AnalysisClipDescriptor clip, int maximumFrames,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Failing) throw new InvalidOperationException("analysis_clip_playback_unavailable");
            for (var index = 0; index < maximumFrames; index++)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AnalysisFrame(index, clip.CameraId, clip.StreamEpoch,
                    clip.CapturedAtUtc.AddSeconds(index), 32, 32, new float[3 * 32 * 32]);
            }
        }
    }

    private sealed class FakeClipSource : IClipPlaybackSource
    {
        public Task<ClipLibraryItem?> GetAsync(string publicId, CancellationToken cancellationToken) =>
            Task.FromResult<ClipLibraryItem?>(new ClipLibraryItem(publicId, "clip-" + publicId[^4..], "garage-01",
                DateTimeOffset.Parse("2026-11-02T12:00:00+00:00"),
                DateTimeOffset.Parse("2026-11-02T12:00:10+00:00"), "ready", "not_requested", "manual", "v1",
                null, null, 0, DateTimeOffset.Parse("2026-11-02T12:00:10+00:00"), 1000));

        public Task<ClipScanPage> ListReadyPageAsync(int take, int offset, CancellationToken cancellationToken) =>
            Task.FromResult(new ClipScanPage([], offset, false));

        public Task<string?> GetPlaybackPathAsync(string publicId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("/tmp/playback.mp4");
    }

    private sealed class TempSpool : IDisposable
    {
        public TempSpool()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "home-ia-monitoring-runtime",
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
