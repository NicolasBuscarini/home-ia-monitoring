using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeIaMonitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class MonitoringContractTests
{
    [Fact]
    public async Task Liveness_does_not_depend_on_capture()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("live", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Contract_exposes_status_read_only_mode_and_capabilities()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/monitoring/contract");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("1.0", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("monitoring", json.RootElement.GetProperty("moduleId").GetString());
        Assert.Equal("status-read-only", json.RootElement.GetProperty("executionMode").GetString());
        Assert.Contains("camera.status.read", json.RootElement.GetProperty("capabilities")
            .EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Readiness_reports_status_ready_without_claiming_capture()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("status-ready", json.RootElement.GetProperty("executionMode").GetString());
        Assert.DoesNotContain("capturing", json.RootElement.GetProperty("executionMode").GetString());
    }

    [Fact]
    public async Task Camera_status_is_unavailable_for_empty_spool_and_404_for_another_camera()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/monitoring/cameras/garage-01/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unavailable", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("no_segments", json.RootElement.GetProperty("reasonCode").GetString());

        using var notFound = await client.GetAsync("/api/v1/monitoring/cameras/side-entry/status");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task Camera_status_reports_capturing_and_stale_from_manifests()
    {
        using var spool = new TempSpool();
        await spool.WriteManifestAsync("garage-01", DateTimeOffset.UtcNow.AddSeconds(-2));
        await using (var factory = Factory(spool.Path))
        {
            using (var scope = factory.Services.CreateScope())
                Assert.Equal(spool.Path, scope.ServiceProvider.GetRequiredService<MonitoringOptions>().SpoolDirectory);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/monitoring/cameras/garage-01/status");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("capturing", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("epoch-1", json.RootElement.GetProperty("streamEpoch").GetString());
        }

        using var staleSpool = new TempSpool();
        await staleSpool.WriteManifestAsync("garage-01", DateTimeOffset.UtcNow.AddMinutes(-5));
        await using (var factory = Factory(staleSpool.Path, staleSeconds: 5))
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/monitoring/cameras/garage-01/status");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("stale", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("segment_stale", json.RootElement.GetProperty("reasonCode").GetString());
        }
    }

    [Fact]
    public async Task Capture_endpoint_exposes_effective_disabled_settings()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/monitoring/capture");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("synthetic", json.RootElement.GetProperty("profile").GetString());
        Assert.Equal("garage-01", json.RootElement.GetProperty("cameraId").GetString());
        Assert.NotEqual("capturing", json.RootElement.GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Capture_settings_update_is_validated_and_persisted()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        using var invalid = await client.PutAsJsonAsync("/api/v1/monitoring/capture/settings",
            new { enabled = true, segmentSeconds = 0, maximumSegments = 10, maximumSegmentBytes = 1_048_576 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var valid = await client.PutAsJsonAsync("/api/v1/monitoring/capture/settings",
            new { enabled = true, segmentSeconds = 2, maximumSegments = 20, maximumSegmentBytes = 2_097_152 });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var json = JsonDocument.Parse(await valid.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("segmentSeconds").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("revision").GetInt64());
        Assert.True(File.Exists(System.IO.Path.Combine(spool.Path, "capture-settings.json")));

        using var contract = await client.GetAsync("/api/v1/monitoring/contract");
        using var contractJson = JsonDocument.Parse(await contract.Content.ReadAsStringAsync());
        Assert.Equal("capture-enabled", contractJson.RootElement.GetProperty("executionMode").GetString());
    }

    private static WebApplicationFactory<Program> Factory(string spool, int staleSeconds = 60) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MONITORING_CAMERA_ID"] = "garage-01",
                    ["MONITORING_SPOOL_DIRECTORY"] = spool,
                    ["MONITORING_STATUS_STALE_SECONDS"] = staleSeconds.ToString()
                })));

    private sealed class TempSpool : IDisposable
    {
        public TempSpool()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "home-ia-monitoring-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "manifests"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "gaps"));
        }

        public string Path { get; }

        public Task WriteManifestAsync(string cameraId, DateTimeOffset endedAt)
        {
            var manifest = new
            {
                schemaVersion = "1.0",
                segmentId = Guid.NewGuid().ToString("N"),
                cameraId,
                streamEpoch = "epoch-1",
                startedAt = endedAt.AddSeconds(-1),
                endedAt,
                fileName = "segment.ts",
                sizeBytes = 1,
                sha256 = new string('a', 64)
            };
            return File.WriteAllTextAsync(
                System.IO.Path.Combine(Path, "manifests", manifest.segmentId + ".json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
