using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeIaMonitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class LiveAndClipMediaTests
{
    [Fact]
    public async Task Playlist_and_segment_are_served_from_validated_spool_entries()
    {
        using var spool = new TempSegmentSpool();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var first = await spool.WriteSegmentAsync(startedAt, "epoch-1", 0x47);
        var second = await spool.WriteSegmentAsync(startedAt.AddSeconds(5), "epoch-1", 0x48);
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();

        using var playlist = await client.GetAsync("/api/v1/monitoring/live/garage-01/index.m3u8");
        Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", playlist.Content.Headers.ContentType?.MediaType);
        var body = await playlist.Content.ReadAsStringAsync();
        Assert.StartsWith("#EXTM3U", body);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:", body);
        Assert.Contains($"segments/{first.SegmentId}", body);
        Assert.Contains($"segments/{second.SegmentId}", body);

        using var segment = await client.GetAsync(
            $"/api/v1/monitoring/live/garage-01/segments/{second.SegmentId}");
        Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
        Assert.Equal("video/mp2t", segment.Content.Headers.ContentType?.MediaType);
        Assert.Equal(second.Content, await segment.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/monitoring/live/garage-02/index.m3u8")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/monitoring/live/garage-01/segments/not-a-segment")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync(
                $"/api/v1/monitoring/live/garage-01/segments/{Guid.NewGuid():N}")).StatusCode);
    }

    [Fact]
    public async Task Playlist_is_unavailable_without_segments_and_contract_advertises_the_capabilities()
    {
        using var spool = new TempSegmentSpool();
        await using var factory = Factory(spool.Path);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync("/api/v1/monitoring/live/garage-01/index.m3u8")).StatusCode);
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/v1/monitoring/contract"));
        var capabilities = json.RootElement.GetProperty("capabilities").EnumerateArray()
            .Select(item => item.GetString()).ToArray();
        Assert.Contains("live.hls.read", capabilities);
        Assert.Contains("clips.write", capabilities);
    }

    [Fact]
    public async Task Clip_media_is_materialized_idempotently_and_failures_are_observable()
    {
        using var spool = new TempSegmentSpool();
        var ffmpeg = spool.WriteFfmpegStub();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var first = await spool.WriteSegmentAsync(startedAt, "epoch-1", 0x47);
        var second = await spool.WriteSegmentAsync(startedAt.AddSeconds(5), "epoch-1", 0x48);
        await using var factory = Factory(spool.Path, ffmpeg);
        using var client = factory.CreateClient();

        var clipId = Guid.NewGuid().ToString("N");
        var request = new
        {
            clipId,
            cameraId = "garage-01",
            segmentIds = new[] { first.SegmentId, second.SegmentId },
            requestedStartedAt = startedAt,
            requestedEndedAt = startedAt.AddSeconds(10)
        };
        using var created = await client.PostAsJsonAsync("/api/v1/monitoring/clips/media", request);
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var result = await created.Content.ReadFromJsonAsync<ClipMediaResult>();
        Assert.NotNull(result);
        Assert.Equal(clipId + ".ts", result.MediaFileName);
        Assert.Equal(clipId + ".jpg", result.ThumbnailFileName);
        var expected = Convert.ToHexString(SHA256.HashData(first.Content.Concat(second.Content).ToArray()))
            .ToLowerInvariant();
        Assert.Equal(expected, result.MediaSha256);
        Assert.True(File.Exists(Path.Combine(spool.Path, "clips", "media", result.MediaFileName)));
        Assert.True(File.Exists(Path.Combine(spool.Path, "clips", "thumbnails", result.ThumbnailFileName)));

        using var repeated = await client.PostAsJsonAsync("/api/v1/monitoring/clips/media", request);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var repeatedResult = await repeated.Content.ReadFromJsonAsync<ClipMediaResult>();
        Assert.Equal(result.MediaSha256, repeatedResult!.MediaSha256);
        Assert.Equal(result.ThumbnailSha256, repeatedResult.ThumbnailSha256);

        using var missing = await client.PostAsJsonAsync("/api/v1/monitoring/clips/media", request with
        {
            clipId = Guid.NewGuid().ToString("N"),
            segmentIds = new[] { first.SegmentId, Guid.NewGuid().ToString("N") }
        });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);

        using var invalid = await client.PostAsJsonAsync("/api/v1/monitoring/clips/media", request with
        {
            clipId = "not-a-clip"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var foreignCamera = await client.PostAsJsonAsync("/api/v1/monitoring/clips/media", request with
        {
            cameraId = "garage-02"
        });
        Assert.Equal(HttpStatusCode.NotFound, foreignCamera.StatusCode);
    }

    private static WebApplicationFactory<Program> Factory(string spool, string ffmpeg = "ffmpeg") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MONITORING_CAMERA_ID"] = "garage-01",
                    ["MONITORING_SPOOL_DIRECTORY"] = spool,
                    ["MONITORING_CAPTURE_FFMPEG_PATH"] = ffmpeg
                })));

    private sealed record SegmentStub(string SegmentId, byte[] Content);

    private sealed class TempSegmentSpool : IDisposable
    {
        private readonly string _manifestDirectory;
        private readonly string _segmentDirectory;

        public TempSegmentSpool()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "home-ia-monitoring-live",
                Guid.NewGuid().ToString("N"));
            _manifestDirectory = System.IO.Path.Combine(Path, "manifests");
            _segmentDirectory = System.IO.Path.Combine(Path, "segments");
            Directory.CreateDirectory(_manifestDirectory);
            Directory.CreateDirectory(_segmentDirectory);
        }

        public string Path { get; }

        public string WriteFfmpegStub()
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg-stub-" + Guid.NewGuid().ToString("N") + ".sh");
            File.WriteAllText(path,
                "#!/bin/sh\nout=\"\"\nfor argument in \"$@\"; do out=\"$argument\"; done\n" +
                "printf '\\377\\330\\377\\340fakejpeg' > \"$out\"\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        public async Task<SegmentStub> WriteSegmentAsync(DateTimeOffset startedAt, string streamEpoch, byte fill)
        {
            var segmentId = Guid.NewGuid().ToString("N");
            var fileName = segmentId + ".ts";
            var content = Enumerable.Repeat(fill, 2000).ToArray();
            await File.WriteAllBytesAsync(System.IO.Path.Combine(_segmentDirectory, fileName), content);
            var manifest = new
            {
                schemaVersion = "1.0",
                segmentId,
                cameraId = "garage-01",
                streamEpoch,
                startedAt,
                endedAt = startedAt.AddSeconds(5),
                fileName,
                sizeBytes = content.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()
            };
            await File.WriteAllTextAsync(System.IO.Path.Combine(_manifestDirectory, segmentId + ".json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return new SegmentStub(segmentId, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
