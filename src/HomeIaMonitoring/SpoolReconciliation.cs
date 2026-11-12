using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HomeIaMonitoring;

public sealed record SpoolClipReconciliation(
    string PublicId,
    string ClipId,
    string State,
    string MediaStatus,
    string ThumbnailStatus);

public sealed record SpoolReconciliationReport(
    int ManifestCount,
    int InvalidManifestCount,
    int ConsistentCount,
    int MissingMediaCount,
    int MediaChecksumMismatchCount,
    int MissingThumbnailCount,
    int ThumbnailChecksumMismatchCount,
    int OrphanMediaCount,
    int OrphanThumbnailCount,
    IReadOnlyList<SpoolClipReconciliation> Clips);

/// <summary>
/// Read-only reconciliation of the clip spool. For each clip manifest it verifies that the declared
/// media and thumbnail files still exist and match the recorded SHA-256, and it flags media files
/// without a manifest (orphans). It never deletes or rewrites anything — it only reports — so it is
/// the post-restore step that "registers orphans and missing media" required by the T11 restore
/// drill (ADR132).
/// </summary>
public sealed class SpoolReconciler(MonitoringOptions monitoring, ILogger<SpoolReconciler> logger)
{
    private static readonly string[] ValidStates = ["collecting", "ready", "failed", "lost_capacity"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SpoolReconciliationReport> ReconcileAsync(CancellationToken cancellationToken)
    {
        var manifestDirectory = Path.Combine(monitoring.SpoolDirectory, "clips", "manifests");
        var mediaDirectory = Path.Combine(monitoring.SpoolDirectory, "clips", "media");
        var thumbnailDirectory = Path.Combine(monitoring.SpoolDirectory, "clips", "thumbnails");

        var clips = new List<SpoolClipReconciliation>();
        var referencedMedia = new HashSet<string>(StringComparer.Ordinal);
        var referencedThumbnails = new HashSet<string>(StringComparer.Ordinal);

        var manifestCount = 0;
        var invalidManifestCount = 0;
        var consistentCount = 0;
        var missingMediaCount = 0;
        var mediaMismatchCount = 0;
        var missingThumbnailCount = 0;
        var thumbnailMismatchCount = 0;

        if (Directory.Exists(manifestDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(manifestDirectory, "*.json")
                         .Select(path => new FileInfo(path))
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                         .Select(file => file.FullName))
            {
                cancellationToken.ThrowIfCancellationRequested();
                manifestCount++;

                var clip = await ReadManifestAsync(path, cancellationToken);
                if (clip is null)
                {
                    invalidManifestCount++;
                    continue;
                }

                if (clip.MediaFileName is not null) referencedMedia.Add(clip.MediaFileName);
                if (clip.ThumbnailFileName is not null) referencedThumbnails.Add(clip.ThumbnailFileName);

                var mediaStatus = await ClassifyAsync(mediaDirectory, clip.MediaFileName, clip.MediaSha256,
                    cancellationToken);
                var thumbnailStatus = await ClassifyAsync(thumbnailDirectory, clip.ThumbnailFileName,
                    clip.ThumbnailSha256, cancellationToken);

                if (mediaStatus == "missing") missingMediaCount++;
                else if (mediaStatus == "checksum_mismatch") mediaMismatchCount++;
                if (thumbnailStatus == "missing") missingThumbnailCount++;
                else if (thumbnailStatus == "checksum_mismatch") thumbnailMismatchCount++;

                if (mediaStatus is "consistent" or "not_declared" &&
                    thumbnailStatus is "consistent" or "not_declared")
                    consistentCount++;

                clips.Add(new SpoolClipReconciliation(clip.PublicId!, clip.ClipId, clip.State,
                    mediaStatus, thumbnailStatus));
            }
        }

        var orphanMediaCount = CountOrphans(mediaDirectory, referencedMedia);
        var orphanThumbnailCount = CountOrphans(thumbnailDirectory, referencedThumbnails);

        logger.LogInformation(
            "Reconciliação do spool: {ManifestCount} manifests ({InvalidManifestCount} inválidos), " +
            "{ConsistentCount} consistentes, {MissingMediaCount} mídias ausentes, {MediaMismatchCount} mídias divergentes, " +
            "{MissingThumbnailCount} thumbs ausentes, {ThumbnailMismatchCount} thumbs divergentes, " +
            "{OrphanMediaCount} mídias órfãs, {OrphanThumbnailCount} thumbs órfãs.",
            manifestCount, invalidManifestCount, consistentCount, missingMediaCount, mediaMismatchCount,
            missingThumbnailCount, thumbnailMismatchCount, orphanMediaCount, orphanThumbnailCount);

        return new SpoolReconciliationReport(manifestCount, invalidManifestCount, consistentCount,
            missingMediaCount, mediaMismatchCount, missingThumbnailCount, thumbnailMismatchCount,
            orphanMediaCount, orphanThumbnailCount, clips);
    }

    private static async Task<ClipManifestV1?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var clip = await JsonSerializer.DeserializeAsync<ClipManifestV1>(stream, JsonOptions, cancellationToken);
            if (clip is not { SchemaVersion: "1.0" } || !SpoolWriter.IsSafeSegmentId(clip.ClipId) ||
                string.IsNullOrWhiteSpace(clip.PublicId) || Path.GetFileName(path) != clip.ClipId + ".json" ||
                !ValidStates.Contains(clip.State))
                return null;
            return clip;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static async Task<string> ClassifyAsync(string directory, string? fileName, string? sha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            return "not_declared";
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return "missing";
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            return "checksum_mismatch";
        return await ClipLibrary.HasValidHashAsync(path, sha256, cancellationToken)
            ? "consistent"
            : "checksum_mismatch";
    }

    private static int CountOrphans(string directory, HashSet<string> referenced)
    {
        if (!Directory.Exists(directory)) return 0;
        var orphans = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
            if (!referenced.Contains(Path.GetFileName(file)))
                orphans++;
        return orphans;
    }
}
