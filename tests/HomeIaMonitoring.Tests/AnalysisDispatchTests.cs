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

public sealed class AnalysisDispatchTests
{
    [Fact]
    public async Task Policy_updates_are_versioned_and_validated()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();

        using var missing = await client.GetAsync("/api/v1/monitoring/analysis/policies/garage-01");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var created = await client.PutAsJsonAsync("/api/v1/monitoring/analysis/policies/garage-01",
            new { contextId = "garage", expectedVersion = 0, policy = "always", actorId = "actor-1" });
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var policy = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, policy.GetProperty("version").GetInt64());

        using var current = await client.GetAsync("/api/v1/monitoring/analysis/policies/garage-01");
        Assert.Equal("always",
            (await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("policy").GetString());

        using var stale = await client.PutAsJsonAsync("/api/v1/monitoring/analysis/policies/garage-01",
            new { contextId = "garage", expectedVersion = 0, policy = "never", actorId = "actor-1" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var invalid = await client.PutAsJsonAsync("/api/v1/monitoring/analysis/policies/garage-01",
            new { contextId = "garage", expectedVersion = 1, policy = "sometimes", actorId = "actor-1" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Enqueue_is_idempotent_capacity_bounded_and_policy_aware()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path, queueCapacity: 1);
        using var client = factory.CreateClient();

        using var allowed = await client.PutAsJsonAsync("/api/v1/monitoring/analysis/policies/garage-01",
            new { contextId = "garage", expectedVersion = 0, policy = "always", actorId = "actor-1" });
        Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync());

        var request = new
        {
            clipId = "clip-1",
            clipPublicId = "CLP000000000001",
            cameraId = "garage-01",
            contextId = "garage",
            idempotencyKey = Guid.NewGuid(),
            analysisVersion = "analysis-v1",
            requestKind = "manual"
        };
        using var created = await client.PostAsJsonAsync("/api/v1/monitoring/analysis/jobs", request);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var job = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("analysis_pending", job.GetProperty("state").GetString());
        Assert.False(job.GetProperty("existing").GetBoolean());

        using var duplicate = await client.PostAsJsonAsync("/api/v1/monitoring/analysis/jobs", request);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.True((await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("existing").GetBoolean());

        using var full = await client.PostAsJsonAsync("/api/v1/monitoring/analysis/jobs", new
        {
            clipId = "clip-2",
            clipPublicId = "CLP000000000002",
            cameraId = "garage-01",
            contextId = "garage",
            idempotencyKey = Guid.NewGuid(),
            analysisVersion = "analysis-v1",
            requestKind = "manual"
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);

        using var denied = await client.PutAsJsonAsync("/api/v1/monitoring/analysis/policies/garage-01",
            new { contextId = "garage", expectedVersion = 1, policy = "never", actorId = "actor-1" });
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);

        using var rejected = await client.PostAsJsonAsync("/api/v1/monitoring/analysis/jobs", new
        {
            clipId = "clip-3",
            clipPublicId = "CLP000000000003",
            cameraId = "garage-01",
            contextId = "garage",
            idempotencyKey = Guid.NewGuid(),
            analysisVersion = "analysis-v1",
            requestKind = "manual"
        });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("analysis_not_allowed",
            (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Claim_leases_jobs_and_reconciles_expired_leases()
    {
        using var spool = new TempSpool();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-02T12:00:00+00:00"));
        await using var factory = Factory(spool.Path, clock: clock);
        using var client = factory.CreateClient();

        using var job = await client.PostAsJsonAsync("/api/v1/monitoring/analysis/jobs", new
        {
            clipId = "clip-1",
            clipPublicId = "CLP000000000001",
            cameraId = "garage-01",
            contextId = "garage",
            idempotencyKey = Guid.NewGuid(),
            analysisVersion = "analysis-v1",
            requestKind = "manual"
        });
        Assert.True(job.IsSuccessStatusCode, await job.Content.ReadAsStringAsync());

        using var first = await client.PostAsync("/api/v1/monitoring/analysis/jobs/claim", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var claimed = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("processing", claimed.GetProperty("state").GetString());

        using var second = await client.PostAsync("/api/v1/monitoring/analysis/jobs/claim", null);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        clock.Now = clock.Now.AddSeconds(31);
        using var reclaimed = await client.PostAsync("/api/v1/monitoring/analysis/jobs/claim", null);
        Assert.Equal(HttpStatusCode.OK, reclaimed.StatusCode);
        var resumed = await reclaimed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(claimed.GetProperty("publicId").GetString(), resumed.GetProperty("publicId").GetString());

        var stored = Directory.EnumerateFiles(Path.Combine(spool.Path, "analysis", "jobs"), "*.json").Single();
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(stored));
        Assert.Equal(2, document.RootElement.GetProperty("attemptCount").GetInt32());
    }

    [Fact]
    public async Task Recovery_reports_corrupt_files_and_capability_is_published()
    {
        using var spool = new TempSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();

        Directory.CreateDirectory(Path.Combine(spool.Path, "analysis", "jobs"));
        await File.WriteAllTextAsync(Path.Combine(spool.Path, "analysis", "jobs", "JOB000000000001.json"),
            "{not-json");
        await File.WriteAllTextAsync(Path.Combine(spool.Path, "analysis", "jobs", "orphan.tmp"), "tmp");

        using var recovery = await client.GetAsync("/api/v1/monitoring/analysis/recovery");
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        var result = await recovery.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("invalidFiles").GetInt32());
        Assert.False(File.Exists(Path.Combine(spool.Path, "analysis", "jobs", "orphan.tmp")));

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/v1/monitoring/capabilities");
        Assert.Contains("analysis.dispatch",
            capabilities.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
    }

    private static WebApplicationFactory<Program> Factory(string spool, int queueCapacity = 8,
        MutableTimeProvider? clock = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MONITORING_CAMERA_ID"] = "garage-01",
                    ["MONITORING_SPOOL_DIRECTORY"] = spool,
                    ["MONITORING_ANALYSIS_QUEUE_CAPACITY"] = queueCapacity.ToString(),
                    ["MONITORING_ANALYSIS_LEASE_SECONDS"] = "30"
                }));
            if (clock is not null)
                builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
        });
        return factory;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TempSpool : IDisposable
    {
        public TempSpool()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "home-ia-monitoring-analysis",
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
