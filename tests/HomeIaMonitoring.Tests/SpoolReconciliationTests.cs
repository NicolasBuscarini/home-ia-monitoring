using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeIaMonitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class SpoolReconciliationTests
{
    private const string ClipId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PublicId = "CLP-1";

    [Fact]
    public async Task Reconcile_reports_consistent_spool_when_media_and_thumbnail_match()
    {
        using var spool = new TempSpool();
        WriteClip(spool.Path, ClipId, PublicId, "clip.mp4", "clip.jpg", "media-bytes", "thumb-bytes");

        var report = await ReconcileAsync(spool.Path);

        Assert.Equal(1, report.ManifestCount);
        Assert.Equal(0, report.InvalidManifestCount);
        Assert.Equal(1, report.ConsistentCount);
        Assert.Equal(0, report.MissingMediaCount);
        Assert.Equal(0, report.MediaChecksumMismatchCount);
        Assert.Equal(0, report.MissingThumbnailCount);
        Assert.Equal(0, report.ThumbnailChecksumMismatchCount);
        Assert.Equal(0, report.OrphanMediaCount);
        Assert.Equal(0, report.OrphanThumbnailCount);

        var clip = Assert.Single(report.Clips);
        Assert.Equal(PublicId, clip.PublicId);
        Assert.Equal("consistent", clip.MediaStatus);
        Assert.Equal("consistent", clip.ThumbnailStatus);
    }

    [Fact]
    public async Task Reconcile_flags_missing_media_and_orphan_media_without_deleting()
    {
        using var spool = new TempSpool();
        WriteClip(spool.Path, ClipId, PublicId, "clip.mp4", "clip.jpg", "media-bytes", "thumb-bytes");
        File.Delete(Path.Combine(spool.Path, "clips", "media", "clip.mp4"));
        Write(spool.Path, "clips/media/orphan.mp4", "orphan-bytes");

        var report = await ReconcileAsync(spool.Path);

        Assert.Equal(1, report.MissingMediaCount);
        Assert.Equal(1, report.OrphanMediaCount);
        var clip = Assert.Single(report.Clips);
        Assert.Equal("missing", clip.MediaStatus);
        Assert.Equal("consistent", clip.ThumbnailStatus);

        // Reconciliation is read-only: nothing was deleted or rewritten.
        Assert.True(File.Exists(Path.Combine(spool.Path, "clips", "media", "orphan.mp4")));
    }

    [Fact]
    public async Task Reconcile_flags_checksum_mismatch_when_media_changed()
    {
        using var spool = new TempSpool();
        WriteClip(spool.Path, ClipId, PublicId, "clip.mp4", "clip.jpg", "media-bytes", "thumb-bytes");
        Write(spool.Path, "clips/media/clip.mp4", "corrupted-bytes");

        var report = await ReconcileAsync(spool.Path);

        Assert.Equal(1, report.MediaChecksumMismatchCount);
        var clip = Assert.Single(report.Clips);
        Assert.Equal("checksum_mismatch", clip.MediaStatus);
    }

    [Fact]
    public async Task Reconcile_counts_invalid_manifests_without_crashing()
    {
        using var spool = new TempSpool();
        Write(spool.Path, "clips/manifests/bad.json", """{"schemaVersion":"1.0","clipId":"nope"}""");

        var report = await ReconcileAsync(spool.Path);

        Assert.Equal(1, report.ManifestCount);
        Assert.Equal(1, report.InvalidManifestCount);
        Assert.Empty(report.Clips);
    }

    private static async Task<SpoolReconciliationReport> ReconcileAsync(string spool)
    {
        var reconciler = new SpoolReconciler(new MonitoringOptions("garage-01", spool, 20),
            NullLogger<SpoolReconciler>.Instance);
        return await reconciler.ReconcileAsync(CancellationToken.None);
    }

    private static void WriteClip(string spool, string clipId, string publicId, string mediaFile,
        string thumbnailFile, string mediaContent, string thumbnailContent)
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = new ClipManifestV1("1.0", clipId, "garage-01", now, now.AddMinutes(1), now,
            now.AddMinutes(1), "ready", "trigger-1", "manual", Array.Empty<string>(), mediaFile,
            Sha256(mediaContent), thumbnailFile, Sha256(thumbnailContent), 0, now, PublicId: publicId);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Write(spool, $"clips/manifests/{clipId}.json", json);
        Write(spool, $"clips/media/{mediaFile}", mediaContent);
        Write(spool, $"clips/thumbnails/{thumbnailFile}", thumbnailContent);
    }

    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static void Write(string spool, string relative, string content)
    {
        var path = Path.Combine(spool, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class TempSpool : IDisposable
    {
        public TempSpool()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "home-ia-monitoring-spool-reconciliation-tests", Guid.NewGuid().ToString("N"));
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
