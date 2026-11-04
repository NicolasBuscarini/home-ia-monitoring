using System.Net;
using System.Runtime.Versioning;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HomeIaMonitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class ClipLibraryTests
{
    [Fact]
    public async Task Clips_are_listed_only_for_supplied_cameras_with_filters_and_validation()
    {
        using var spool = new TempClipSpool();
        var older = await spool.WriteClipAsync("garage-01", "ready", DateTimeOffset.UtcNow.AddHours(-2));
        var newer = await spool.WriteClipAsync("garage-01", "ready", DateTimeOffset.UtcNow.AddHours(-1));
        await spool.WriteClipAsync("side-entry", "ready", DateTimeOffset.UtcNow.AddMinutes(-30));
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/v1/monitoring/clips?cameraIds=garage-01&state=ready&page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ClipLibraryPage>();
        Assert.NotNull(page);
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
        Assert.Equal(newer.PublicId, page.Items[0].PublicId);

        using var byId = await client.GetAsync(
            $"/api/v1/monitoring/clips?cameraIds=garage-01&publicId={newer.PublicId}");
        var byIdPage = await byId.Content.ReadFromJsonAsync<ClipLibraryPage>();
        Assert.Single(byIdPage!.Items);
        Assert.Equal(newer.PublicId, byIdPage.Items[0].PublicId);

        using var tooLong = await client.GetAsync(
            "/api/v1/monitoring/clips?cameraIds=garage-01&minDurationSeconds=11");
        var tooLongPage = await tooLong.Content.ReadFromJsonAsync<ClipLibraryPage>();
        Assert.Empty(tooLongPage!.Items);

        using var other = await client.GetAsync("/api/v1/monitoring/clips?cameraIds=side-entry");
        var otherPage = await other.Content.ReadFromJsonAsync<ClipLibraryPage>();
        Assert.Single(otherPage!.Items);
        Assert.Equal("side-entry", otherPage.Items[0].CameraId);

        using var empty = await client.GetAsync("/api/v1/monitoring/clips");
        var emptyPage = await empty.Content.ReadFromJsonAsync<ClipLibraryPage>();
        Assert.Empty(emptyPage!.Items);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/monitoring/clips?cameraIds=garage-01&page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/monitoring/clips?cameraIds=garage-01&state=unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/monitoring/clips?cameraIds=garage-01&analysis=unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync(
                "/api/v1/monitoring/clips?cameraIds=garage-01&minDurationSeconds=10&maxDurationSeconds=5"))
                .StatusCode);
    }

    [Fact]
    public async Task Clips_can_be_sorted_and_filtered_by_preservation()
    {
        using var spool = new TempClipSpool();
        var older = await spool.WriteClipAsync("garage-01", "ready", DateTimeOffset.UtcNow.AddHours(-2),
            durationSeconds: 10, mediaSizeBytes: 5, archiveStatus: "archived");
        var newer = await spool.WriteClipAsync("garage-01", "ready", DateTimeOffset.UtcNow.AddHours(-1),
            durationSeconds: 30, mediaSizeBytes: 20, archiveStatus: "archived");
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<RetentionStore>()
            .SetPreservationAsync(newer.PublicId, 0, isPreserved: true, CancellationToken.None);

        foreach (var (query, expected) in new[]
                 {
                     ("sort=oldest", older.PublicId),
                     ("sort=longest", newer.PublicId),
                     ("sort=shortest", older.PublicId),
                     ("sort=largest", newer.PublicId),
                     ("sort=smallest", older.PublicId),
                 })
        {
            using var response = await client.GetAsync($"/api/v1/monitoring/clips?cameraIds=garage-01&{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<ClipLibraryPage>();
            Assert.Equal(2, page!.Items.Count);
            Assert.Equal(expected, page.Items[0].PublicId);
        }

        foreach (var (query, expected) in new[]
                 {
                     ("preserved=true", newer.PublicId),
                     ("preserved=false", older.PublicId),
                 })
        {
            using var response = await client.GetAsync($"/api/v1/monitoring/clips?cameraIds=garage-01&{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<ClipLibraryPage>();
            Assert.Single(page!.Items);
            Assert.Equal(expected, page.Items[0].PublicId);
        }

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/v1/monitoring/clips?cameraIds=garage-01&sort=unknown")).StatusCode);
    }

    [Fact]
    public async Task Clip_details_and_thumbnail_require_a_valid_manifest_and_hash()
    {
        using var spool = new TempClipSpool();
        var clip = await spool.WriteClipAsync("garage-01", "ready", DateTimeOffset.UtcNow);
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();

        using var detail = await client.GetAsync($"/api/v1/monitoring/clips/{clip.PublicId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var item = await detail.Content.ReadFromJsonAsync<ClipLibraryItem>();
        Assert.Equal(clip.ClipId, item!.ClipId);
        Assert.Equal("ready", item.State);

        using var thumbnail = await client.GetAsync($"/api/v1/monitoring/clips/{clip.PublicId}/thumbnail");
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal("image/jpeg", thumbnail.Content.Headers.ContentType?.MediaType);

        await File.WriteAllBytesAsync(spool.ThumbnailPath(clip), [0x00, 0x01]);
        using var tampered = await client.GetAsync($"/api/v1/monitoring/clips/{clip.PublicId}/thumbnail");
        Assert.Equal(HttpStatusCode.NotFound, tampered.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/monitoring/clips/clp_missing")).StatusCode);
    }

    [Fact]
    public async Task Contract_advertises_clip_reading_capability()
    {
        using var spool = new TempClipSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/v1/monitoring/contract"));
        Assert.Contains("clips.read",
            json.RootElement.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void Playback_remux_preserves_the_audio_track_when_present()
    {
        var arguments = ClipLibrary.BuildRemuxArguments("source.ts", "output.mp4");

        Assert.Contains("0:v:0", arguments);
        Assert.Contains("0:a:0?", arguments);
        Assert.DoesNotContain("-an", arguments);
        Assert.Contains("copy", arguments);
    }

    private static WebApplicationFactory<Program> Factory(string spool) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MONITORING_CAMERA_ID"] = "garage-01",
                    ["MONITORING_SPOOL_DIRECTORY"] = spool
                })));

    private sealed record ClipStub(string ClipId, string PublicId, string ThumbnailFileName);

    private sealed class TempClipSpool : IDisposable
    {
        private readonly string _manifestDirectory;
        private readonly string _thumbnailDirectory;

        public TempClipSpool()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "home-ia-monitoring-clips",
                Guid.NewGuid().ToString("N"));
            _manifestDirectory = System.IO.Path.Combine(Path, "clips", "manifests");
            _thumbnailDirectory = System.IO.Path.Combine(Path, "clips", "thumbnails");
            Directory.CreateDirectory(_manifestDirectory);
            Directory.CreateDirectory(_thumbnailDirectory);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "manifests"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "gaps"));
        }

        public string Path { get; }

        public string ThumbnailPath(ClipStub clip) => System.IO.Path.Combine(_thumbnailDirectory, clip.ThumbnailFileName);

        private static int _publicIdSequence;

        public async Task<ClipStub> WriteClipAsync(string cameraId, string state, DateTimeOffset startedAt,
            int durationSeconds = 10, int mediaSizeBytes = 5, string archiveStatus = "not_requested")
        {
            var clipId = Guid.NewGuid().ToString("N");
            var publicId = "CLP" + Interlocked.Increment(ref _publicIdSequence).ToString("D12");
            var thumbnailName = clipId + ".jpg";
            var thumbnailBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
            await File.WriteAllBytesAsync(System.IO.Path.Combine(_thumbnailDirectory, thumbnailName), thumbnailBytes);
            var mediaDirectory = System.IO.Path.Combine(Path, "clips", "media");
            Directory.CreateDirectory(mediaDirectory);
            var mediaName = clipId + ".mp4";
            var mediaBytes = new byte[mediaSizeBytes];
            await File.WriteAllBytesAsync(System.IO.Path.Combine(mediaDirectory, mediaName), mediaBytes);
            var manifest = new
            {
                schemaVersion = "1.0",
                clipId,
                publicId,
                cameraId,
                requestedStartedAt = startedAt,
                requestedEndedAt = startedAt.AddSeconds(durationSeconds),
                effectiveStartedAt = startedAt,
                effectiveEndedAt = startedAt.AddSeconds(durationSeconds),
                state,
                triggerConfigVersion = "1",
                triggerReason = "motion",
                segmentIds = Array.Empty<string>(),
                mediaFileName = mediaName,
                mediaSha256 = Convert.ToHexString(SHA256.HashData(mediaBytes)).ToLowerInvariant(),
                thumbnailFileName = thumbnailName,
                thumbnailSha256 = Convert.ToHexString(SHA256.HashData(thumbnailBytes)).ToLowerInvariant(),
                attemptCount = 0,
                updatedAt = startedAt.AddSeconds(durationSeconds),
                archiveStatus
            };
            await File.WriteAllTextAsync(System.IO.Path.Combine(_manifestDirectory, clipId + ".json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return new ClipStub(clipId, publicId, thumbnailName);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
