using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeIaMonitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class RetentionTests
{
    [Fact]
    public async Task Policy_is_versioned_persisted_and_rejects_stale_updates()
    {
        using var spool = new TempRetentionSpool();
        await using var factory = Factory(spool);
        using var client = factory.CreateClient();

        var initial = await client.GetFromJsonAsync<RetentionPolicyResult>(
            "/api/v1/monitoring/retention/cameras/garage-01");
        Assert.Equal(15, initial!.RetentionDays);
        Assert.Equal(0, initial.Version);

        using var updated = await client.PutAsJsonAsync("/api/v1/monitoring/retention/cameras/garage-01",
            new { expectedVersion = 0, retentionDays = 30, maximumStorageGigabytes = 5 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var policy = await updated.Content.ReadFromJsonAsync<RetentionPolicyResult>();
        Assert.Equal(30, policy!.RetentionDays);
        Assert.Equal(5, policy.MaximumStorageGigabytes);
        Assert.Equal(1, policy.Version);

        using var stale = await client.PutAsJsonAsync("/api/v1/monitoring/retention/cameras/garage-01",
            new { expectedVersion = 0, retentionDays = 7, maximumStorageGigabytes = (int?)null });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var invalid = await client.PutAsJsonAsync("/api/v1/monitoring/retention/cameras/garage-01",
            new { expectedVersion = 1, retentionDays = 0, maximumStorageGigabytes = (int?)null });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var persisted = await client.GetFromJsonAsync<RetentionPolicyResult>(
            "/api/v1/monitoring/retention/cameras/garage-01");
        Assert.Equal(30, persisted!.RetentionDays);
    }

    [Fact]
    public async Task Retention_archives_ready_clips_and_expires_only_unprotected_archive_entries()
    {
        using var spool = new TempRetentionSpool();
        var preserved = await spool.WriteClipAsync("garage-01", DateTimeOffset.UtcNow.AddDays(-10));
        var expired = await spool.WriteClipAsync("garage-01", DateTimeOffset.UtcNow.AddDays(-9));
        var fresh = await spool.WriteClipAsync("garage-01", DateTimeOffset.UtcNow);
        await using var factory = Factory(spool);
        using var client = factory.CreateClient();

        using var policy = await client.PutAsJsonAsync("/api/v1/monitoring/retention/cameras/garage-01",
            new { expectedVersion = 0, retentionDays = 1, maximumStorageGigabytes = (int?)null });
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);

        var archive = factory.Services.GetRequiredService<ClipArchiveService>();
        Assert.True(await archive.RunOnceAsync(CancellationToken.None));
        Assert.True(File.Exists(spool.ArchiveManifest("garage-01", preserved)));
        Assert.True(File.Exists(spool.ArchiveManifest("garage-01", expired)));
        Assert.Equal("archived", spool.ReadManifest(preserved).GetProperty("archiveStatus").GetString());

        using var preservedResponse = await client.PutAsJsonAsync(
            $"/api/v1/monitoring/retention/clips/{preserved.PublicId}/preservation",
            new { expectedVersion = 0, isPreserved = true });
        Assert.Equal(HttpStatusCode.OK, preservedResponse.StatusCode);
        var preservation = await preservedResponse.Content.ReadFromJsonAsync<PreservationResult>();
        Assert.True(preservation!.IsPreserved);
        Assert.Equal(1, preservation.Version);

        var worker = factory.Services.GetRequiredService<ClipRetentionWorker>();
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(spool.ArchiveManifest("garage-01", preserved)));
        Assert.False(Directory.Exists(spool.ArchiveDirectory("garage-01", expired)));
        Assert.True(Directory.Exists(spool.ArchiveDirectory("garage-01", fresh)));
        Assert.Equal("expired", spool.ReadManifest(expired).GetProperty("archiveStatus").GetString());
        Assert.Equal("archived", spool.ReadManifest(preserved).GetProperty("archiveStatus").GetString());

        var states = await client.GetFromJsonAsync<PreservationResult[]>(
            $"/api/v1/monitoring/retention/preservations?clipPublicIds={preserved.PublicId}");
        Assert.Single(states!);
        Assert.True(states![0].IsPreserved);

        using var released = await client.PutAsJsonAsync(
            $"/api/v1/monitoring/retention/clips/{preserved.PublicId}/preservation",
            new { expectedVersion = 1, isPreserved = false });
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.False(Directory.Exists(spool.ArchiveDirectory("garage-01", preserved)));
    }

    [Fact]
    public async Task Archive_worker_replaces_oldest_eligible_copy_to_make_room_for_new_clip()
    {
        using var spool = new TempRetentionSpool();
        var old = await spool.WriteClipAsync("garage-01", DateTimeOffset.UtcNow.AddDays(-20));
        await spool.WriteRawPolicyAsync("garage-01", retentionDays: 3650, maximumStorageBytes: 4096);
        await using var factory = Factory(spool);
        var archive = factory.Services.GetRequiredService<ClipArchiveService>();
        Assert.True(await archive.RunOnceAsync(CancellationToken.None));

        var incoming = await spool.WriteClipAsync("garage-01", DateTimeOffset.UtcNow);
        Assert.True(await archive.RunOnceAsync(CancellationToken.None));

        var worker = factory.Services.GetRequiredService<ClipRetentionWorker>();
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.False(Directory.Exists(spool.ArchiveDirectory("garage-01", old)));
        var status = spool.ReadManifest(incoming).GetProperty("archiveStatus").GetString();
        Assert.True(File.Exists(spool.ArchiveManifest("garage-01", incoming)),
            $"New archive status was {status}.");
        Assert.Equal("archived", status);
    }

    [Fact]
    public async Task Archive_worker_keeps_new_clip_when_age_expired_copy_already_frees_room()
    {
        using var spool = new TempRetentionSpool();
        var old = await spool.WriteClipAsync("garage-01", DateTimeOffset.UtcNow.AddDays(-10));
        await spool.WriteRawPolicyAsync("garage-01", retentionDays: 1, maximumStorageBytes: 4096);
        await using var factory = Factory(spool);
        var archive = factory.Services.GetRequiredService<ClipArchiveService>();
        Assert.True(await archive.RunOnceAsync(CancellationToken.None));

        var incoming = await spool.WriteClipAsync("garage-01", DateTimeOffset.UtcNow);
        Assert.True(await archive.RunOnceAsync(CancellationToken.None));

        var worker = factory.Services.GetRequiredService<ClipRetentionWorker>();
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.False(Directory.Exists(spool.ArchiveDirectory("garage-01", old)));
        var status = spool.ReadManifest(incoming).GetProperty("archiveStatus").GetString();
        Assert.True(File.Exists(spool.ArchiveManifest("garage-01", incoming)),
            $"New archive status was {status}.");
        Assert.Equal("archived", status);
    }

    [Fact]
    public async Task Archive_worker_waits_when_the_camera_quota_cannot_hold_the_new_clip()
    {
        using var spool = new TempRetentionSpool();
        var protectedClip = await spool.WriteClipAsync("garage-01", DateTimeOffset.UtcNow.AddDays(-20));
        await spool.WriteRawPolicyAsync("garage-01", retentionDays: 3650, maximumStorageBytes: 4096);
        await using var factory = Factory(spool);
        using var client = factory.CreateClient();
        var archive = factory.Services.GetRequiredService<ClipArchiveService>();
        Assert.True(await archive.RunOnceAsync(CancellationToken.None));
        using var preserved = await client.PutAsJsonAsync(
            $"/api/v1/monitoring/retention/clips/{protectedClip.PublicId}/preservation",
            new { expectedVersion = 0, isPreserved = true });
        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);

        var incoming = await spool.WriteClipAsync("garage-01", DateTimeOffset.UtcNow);
        Assert.False(await archive.RunOnceAsync(CancellationToken.None));

        Assert.False(Directory.Exists(spool.ArchiveDirectory("garage-01", incoming)));
        var manifest = spool.ReadManifest(incoming);
        Assert.Equal("retry_wait", manifest.GetProperty("archiveStatus").GetString());
        Assert.Equal(0, manifest.GetProperty("archiveAttemptCount").GetInt32());
        Assert.True(manifest.GetProperty("archiveNextAttemptAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        Assert.True(File.Exists(spool.ArchiveManifest("garage-01", protectedClip)));

        using var released = await client.PutAsJsonAsync(
            $"/api/v1/monitoring/retention/clips/{protectedClip.PublicId}/preservation",
            new { expectedVersion = 1, isPreserved = false });
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        spool.RewindArchiveRetry(incoming);
        Assert.True(await archive.RunOnceAsync(CancellationToken.None));
        Assert.Equal("archived", spool.ReadManifest(incoming).GetProperty("archiveStatus").GetString());

        var worker = factory.Services.GetRequiredService<ClipRetentionWorker>();
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.False(Directory.Exists(spool.ArchiveDirectory("garage-01", protectedClip)));
        Assert.True(File.Exists(spool.ArchiveManifest("garage-01", incoming)));
    }

    [Fact]
    public async Task Preservation_requires_an_archived_ready_clip_and_valid_public_id()
    {
        using var spool = new TempRetentionSpool();
        var clip = await spool.WriteClipAsync("garage-01", DateTimeOffset.UtcNow);
        await using var factory = Factory(spool);
        using var client = factory.CreateClient();

        using var notArchived = await client.PutAsJsonAsync(
            $"/api/v1/monitoring/retention/clips/{clip.PublicId}/preservation",
            new { expectedVersion = 0, isPreserved = true });
        Assert.Equal(HttpStatusCode.Conflict, notArchived.StatusCode);

        using var missing = await client.PutAsJsonAsync(
            "/api/v1/monitoring/retention/clips/CLP000000000001/preservation",
            new { expectedVersion = 0, isPreserved = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var invalid = await client.PutAsJsonAsync(
            "/api/v1/monitoring/retention/clips/not-a-clip/preservation",
            new { expectedVersion = 0, isPreserved = true });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Contract_advertises_retention_capabilities()
    {
        using var spool = new TempRetentionSpool();
        await using var factory = Factory(spool);
        using var client = factory.CreateClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/v1/monitoring/contract"));
        var capabilities = json.RootElement.GetProperty("capabilities").EnumerateArray()
            .Select(item => item.GetString()).ToArray();
        Assert.Contains("retention.read", capabilities);
        Assert.Contains("retention.write", capabilities);
    }

    private static WebApplicationFactory<Program> Factory(TempRetentionSpool spool) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MONITORING_CAMERA_ID"] = "garage-01",
                    ["MONITORING_SPOOL_DIRECTORY"] = spool.SpoolPath,
                    ["MONITORING_ARCHIVE_ENABLED"] = "true",
                    ["MONITORING_ARCHIVE_DIRECTORY"] = spool.ArchivePath,
                    ["MONITORING_ARCHIVE_POLL_SECONDS"] = "3600",
                    ["MONITORING_RETENTION_WORKER_ENABLED"] = "false"
                })));

    public sealed record ClipStub(string ClipId, string PublicId, string MediaFileName, string ThumbnailFileName);

    public sealed class TempRetentionSpool : IDisposable
    {
        private readonly string _manifestDirectory;
        private readonly string _mediaDirectory;
        private readonly string _thumbnailDirectory;

        public TempRetentionSpool()
        {
            var root = Path.Combine(Path.GetTempPath(), "home-ia-monitoring-retention",
                Guid.NewGuid().ToString("N"));
            SpoolPath = Path.Combine(root, "spool");
            ArchivePath = Path.Combine(root, "archive");
            _manifestDirectory = Path.Combine(SpoolPath, "clips", "manifests");
            _mediaDirectory = Path.Combine(SpoolPath, "clips", "media");
            _thumbnailDirectory = Path.Combine(SpoolPath, "clips", "thumbnails");
            Directory.CreateDirectory(_manifestDirectory);
            Directory.CreateDirectory(_mediaDirectory);
            Directory.CreateDirectory(_thumbnailDirectory);
            Directory.CreateDirectory(Path.Combine(SpoolPath, "manifests"));
            Directory.CreateDirectory(Path.Combine(SpoolPath, "gaps"));
            Directory.CreateDirectory(ArchivePath);
        }

        public string SpoolPath { get; }
        public string ArchivePath { get; }

        public string ArchiveDirectory(string cameraId, ClipStub clip) =>
            Path.Combine(ArchivePath, cameraId, clip.ClipId);

        public string ArchiveManifest(string cameraId, ClipStub clip) =>
            Path.Combine(ArchiveDirectory(cameraId, clip), "archive-manifest.json");

        public JsonElement ReadManifest(ClipStub clip) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(_manifestDirectory, clip.ClipId + ".json")))
                .RootElement.Clone();

        public void RewindArchiveRetry(ClipStub clip)
        {
            var path = Path.Combine(_manifestDirectory, clip.ClipId + ".json");
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["archiveNextAttemptAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        public async Task<ClipStub> WriteClipAsync(string cameraId, DateTimeOffset startedAt)
        {
            var clipId = Guid.NewGuid().ToString("N");
            var publicId = "CLP" + Random.Shared.NextInt64(0, 1_000_000_000_000).ToString("D12");
            var mediaName = clipId + ".mp4";
            var thumbnailName = clipId + ".jpg";
            var mediaBytes = RandomNumberGenerator.GetBytes(2048);
            var thumbnailBytes = RandomNumberGenerator.GetBytes(256);
            await File.WriteAllBytesAsync(Path.Combine(_mediaDirectory, mediaName), mediaBytes);
            await File.WriteAllBytesAsync(Path.Combine(_thumbnailDirectory, thumbnailName), thumbnailBytes);
            var manifest = new
            {
                schemaVersion = "1.0",
                clipId,
                publicId,
                cameraId,
                requestedStartedAt = startedAt,
                requestedEndedAt = startedAt.AddSeconds(10),
                effectiveStartedAt = startedAt,
                effectiveEndedAt = startedAt.AddSeconds(10),
                state = "ready",
                triggerConfigVersion = "1",
                triggerReason = "motion",
                segmentIds = Array.Empty<string>(),
                mediaFileName = mediaName,
                mediaSha256 = Convert.ToHexStringLower(SHA256.HashData(mediaBytes)),
                thumbnailFileName = thumbnailName,
                thumbnailSha256 = Convert.ToHexStringLower(SHA256.HashData(thumbnailBytes)),
                attemptCount = 0,
                updatedAt = startedAt.AddSeconds(10),
                archiveStatus = "not_requested"
            };
            await File.WriteAllTextAsync(Path.Combine(_manifestDirectory, clipId + ".json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return new ClipStub(clipId, publicId, mediaName, thumbnailName);
        }

        public async Task WriteRawPolicyAsync(string cameraId, int retentionDays, long maximumStorageBytes)
        {
            var directory = Path.Combine(SpoolPath, "retention", "policies");
            Directory.CreateDirectory(directory);
            var policy = new
            {
                schemaVersion = "1.0",
                cameraId,
                retentionDays,
                maximumStorageBytes,
                version = 1,
                updatedAtUtc = DateTimeOffset.UtcNow
            };
            await File.WriteAllTextAsync(Path.Combine(directory, cameraId + ".json"),
                JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        public void Dispose()
        {
            var root = Path.GetDirectoryName(SpoolPath);
            if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
