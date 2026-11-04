using System.Diagnostics;
using System.Security.Cryptography;

namespace HomeIaMonitoring;

public sealed record ClipMediaRequest(
    string ClipId,
    string CameraId,
    IReadOnlyList<string> SegmentIds,
    DateTimeOffset RequestedStartedAt,
    DateTimeOffset RequestedEndedAt);

public sealed record ClipMediaResult(
    string MediaFileName,
    string MediaSha256,
    string ThumbnailFileName,
    string ThumbnailSha256);

/// <summary>
/// Materializes clip media and thumbnail from validated spool segments. The portal keeps the clip
/// manifest state machine and authorization; this writer owns the file creation, checksums and
/// idempotent retry so a repeated request returns the already committed result.
/// </summary>
public sealed class ClipMediaWriter(MonitoringOptions monitoring, CaptureOptions capture, SpoolWriter writer)
{
    private readonly SemaphoreSlim _assemblyGate = new(1, 1);

    public async Task<ClipMediaResult> AssembleAsync(ClipMediaRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var mediaDirectory = Path.Combine(monitoring.SpoolDirectory, "clips", "media");
        var thumbnailDirectory = Path.Combine(monitoring.SpoolDirectory, "clips", "thumbnails");
        Directory.CreateDirectory(mediaDirectory);
        Directory.CreateDirectory(thumbnailDirectory);
        var mediaName = request.ClipId + ".ts";
        var thumbnailName = request.ClipId + ".jpg";
        var mediaPath = Path.Combine(mediaDirectory, mediaName);
        var thumbnailPath = Path.Combine(thumbnailDirectory, thumbnailName);

        await _assemblyGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(mediaPath) || File.Exists(thumbnailPath))
            {
                if (!File.Exists(mediaPath) || !File.Exists(thumbnailPath))
                    throw new InvalidDataException("clip_media_partial");
                return new ClipMediaResult(mediaName, await HashAsync(mediaPath, cancellationToken), thumbnailName,
                    await HashAsync(thumbnailPath, cancellationToken));
            }

            var manifests = writer.ReadManifests().ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
            var segmentPaths = new List<string>(request.SegmentIds.Count);
            foreach (var segmentId in request.SegmentIds)
            {
                if (!manifests.TryGetValue(segmentId, out var manifest) ||
                    !writer.IsValidManifest(manifest, segmentId))
                    throw new InvalidDataException("clip_segments_unavailable");
                var path = Path.Combine(monitoring.SpoolDirectory, "segments", manifest.FileName);
                if (!File.Exists(path) || new FileInfo(path).Length != manifest.SizeBytes)
                    throw new InvalidDataException("clip_segments_unavailable");
                segmentPaths.Add(path);
            }

            var mediaTemporary = mediaPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var thumbnailTemporary = thumbnailPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var target = new FileStream(mediaTemporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    foreach (var segmentPath in segmentPaths)
                    {
                        await using var source = new FileStream(segmentPath, FileMode.Open, FileAccess.Read,
                            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await source.CopyToAsync(target, cancellationToken);
                    }
                    await target.FlushAsync(cancellationToken);
                    target.Flush(flushToDisk: true);
                }
                if (new FileInfo(mediaTemporary).Length < 1) throw new InvalidDataException("clip_media_empty");
                var mediaHash = await HashAsync(mediaTemporary, cancellationToken);
                await CreateThumbnailAsync(mediaTemporary, thumbnailTemporary, cancellationToken);
                var thumbnailHash = await HashAsync(thumbnailTemporary, cancellationToken);
                File.Move(mediaTemporary, mediaPath);
                File.Move(thumbnailTemporary, thumbnailPath);
                return new ClipMediaResult(mediaName, mediaHash, thumbnailName, thumbnailHash);
            }
            finally
            {
                if (File.Exists(mediaTemporary)) File.Delete(mediaTemporary);
                if (File.Exists(thumbnailTemporary)) File.Delete(thumbnailTemporary);
            }
        }
        finally
        {
            _assemblyGate.Release();
        }
    }

    private static void Validate(ClipMediaRequest request)
    {
        if (!SpoolWriter.IsSafeSegmentId(request.ClipId))
            throw new ArgumentException("clip_id_invalid");
        if (string.IsNullOrWhiteSpace(request.CameraId) || request.CameraId.Length > 64 ||
            request.CameraId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("camera_id_invalid");
        if (request.SegmentIds is null || request.SegmentIds.Count is < 1 or > 1000 ||
            request.SegmentIds.Any(id => !SpoolWriter.IsSafeSegmentId(id)) ||
            request.SegmentIds.Distinct(StringComparer.Ordinal).Count() != request.SegmentIds.Count)
            throw new ArgumentException("segment_ids_invalid");
        if (request.RequestedEndedAt < request.RequestedStartedAt ||
            request.RequestedEndedAt - request.RequestedStartedAt > TimeSpan.FromDays(1))
            throw new ArgumentException("requested_window_invalid");
    }

    private async Task CreateThumbnailAsync(string mediaPath, string destination, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(capture.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "error", "-i", mediaPath, "-frames:v", "1",
                     "-q:v", "4", "-f", "image2", destination
                 }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidDataException("clip_thumbnail_failed");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stderr, stdout);
        if (process.ExitCode != 0 || !File.Exists(destination) || new FileInfo(destination).Length == 0)
            throw new InvalidDataException("clip_thumbnail_failed");
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}
