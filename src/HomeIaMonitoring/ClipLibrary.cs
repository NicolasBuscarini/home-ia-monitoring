using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace HomeIaMonitoring;

public sealed record ClipManifestV1(
    string SchemaVersion,
    string ClipId,
    string CameraId,
    DateTimeOffset RequestedStartedAt,
    DateTimeOffset RequestedEndedAt,
    DateTimeOffset EffectiveStartedAt,
    DateTimeOffset EffectiveEndedAt,
    string State,
    string TriggerConfigVersion,
    string TriggerReason,
    IReadOnlyList<string> SegmentIds,
    string? MediaFileName,
    string? MediaSha256,
    string? ThumbnailFileName,
    string? ThumbnailSha256,
    int AttemptCount,
    DateTimeOffset UpdatedAt,
    string ArchiveStatus = "not_requested",
    int ArchiveAttemptCount = 0,
    DateTimeOffset? ArchiveNextAttemptAt = null,
    DateTimeOffset? ArchivedAt = null,
    string? ArchiveManifestSha256 = null,
    string? PublicId = null);

public sealed record ClipLibraryItem(string PublicId, string ClipId, string CameraId,
    DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc, string State, string ArchiveStatus,
    string TriggerReason, string TriggerConfigVersion, string? MediaSha256, string? ThumbnailSha256,
    int AttemptCount, DateTimeOffset UpdatedAtUtc, long? SizeBytes);

public sealed record ClipLibraryPage(IReadOnlyList<ClipLibraryItem> Items, int Page, int PageSize, int Total);

/// <summary>
/// Read-only clip library over the shared spool. Authorization stays in the portal; this service
/// only exposes manifests and validated media for the camera identifiers the caller supplies.
/// </summary>
public interface IClipPlaybackSource
{
    Task<ClipLibraryItem?> GetAsync(string publicId, CancellationToken cancellationToken);
    Task<string?> GetPlaybackPathAsync(string publicId, CancellationToken cancellationToken);
}

public sealed class ClipLibrary(MonitoringOptions monitoring, CaptureOptions capture,
    AnalysisDispatchStore analysisStore) : IClipPlaybackSource
{
    private readonly SemaphoreSlim _conversionGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] States = ["collecting", "ready", "failed", "lost_capacity"];

    public async Task<ClipLibraryPage> ListAsync(IReadOnlyCollection<string> cameraIds, string? publicId,
        DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? state, int? minDurationSeconds,
        int? maxDurationSeconds, string? analysis, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page is < 1 or > 100_000 || pageSize is < 1 or > 100 ||
            (fromUtc.HasValue && toUtc.HasValue && fromUtc > toUtc) ||
            (minDurationSeconds is < 0 or > 86_400) || (maxDurationSeconds is < 0 or > 86_400) ||
            (minDurationSeconds.HasValue && maxDurationSeconds.HasValue &&
                minDurationSeconds > maxDurationSeconds) ||
            (state is not null && !States.Contains(state)) ||
            (analysis is not null && analysis is not ("performed" or "not_performed")))
            throw new ArgumentException("invalid_clip_query");
        var allowed = new HashSet<string>(cameraIds, StringComparer.Ordinal);
        if (allowed.Count == 0) return new([], page, pageSize, 0);
        var clips = new List<ClipManifestV1>();
        foreach (var path in EnumerateManifestPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clip = await ReadManifestAsync(path, cancellationToken);
            if (clip is null || !allowed.Contains(clip.CameraId) ||
                (publicId is not null && clip.PublicId != publicId) ||
                (fromUtc.HasValue && clip.EffectiveEndedAt < fromUtc) ||
                (toUtc.HasValue && clip.EffectiveStartedAt > toUtc) ||
                (state is not null && clip.State != state))
                continue;
            var duration = (clip.EffectiveEndedAt - clip.EffectiveStartedAt).TotalSeconds;
            if ((minDurationSeconds.HasValue && duration < minDurationSeconds) ||
                (maxDurationSeconds.HasValue && duration > maxDurationSeconds))
                continue;
            clips.Add(clip);
        }
        if (analysis is not null)
        {
            var performed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var batch in clips.Select(clip => clip.PublicId!).Chunk(100))
            {
                var jobs = await analysisStore.LatestForClipsAsync(batch, cancellationToken);
                foreach (var job in jobs) performed.Add(job.ClipPublicId);
            }
            clips = clips.Where(clip => analysis == "performed"
                ? performed.Contains(clip.PublicId!)
                : !performed.Contains(clip.PublicId!)).ToList();
        }
        var ordered = clips.OrderByDescending(clip => clip.EffectiveStartedAt)
            .ThenBy(clip => clip.ClipId, StringComparer.Ordinal).ToArray();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(Map).ToArray();
        return new(items, page, pageSize, ordered.Length);
    }

    public async Task<ClipLibraryItem?> GetAsync(string publicId, CancellationToken cancellationToken)
    {
        foreach (var path in EnumerateManifestPaths())
        {
            var clip = await ReadManifestAsync(path, cancellationToken);
            if (clip?.PublicId == publicId) return Map(clip);
        }
        return null;
    }

    public async Task<string?> GetThumbnailPathAsync(string publicId, CancellationToken cancellationToken)
    {
        var clip = await GetManifestAsync(publicId, cancellationToken);
        var path = clip is null ? null : Resolve("thumbnails", clip.ThumbnailFileName, clip.ThumbnailSha256);
        return path is not null && await HasValidHashAsync(path, clip!.ThumbnailSha256!, cancellationToken)
            ? path
            : null;
    }

    public async Task<string?> GetPlaybackPathAsync(string publicId, CancellationToken cancellationToken)
    {
        var clip = await GetManifestAsync(publicId, cancellationToken);
        if (clip is null) return null;
        var source = Resolve("media", clip.MediaFileName, clip.MediaSha256);
        if (source is null || !await HasValidHashAsync(source, clip.MediaSha256!, cancellationToken)) return null;
        return await EnsurePlaybackAsync(clip, source, cancellationToken);
    }

    public static async Task<bool> HasValidHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit) || !File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected));
    }

    private async Task<ClipManifestV1?> GetManifestAsync(string publicId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicId) || publicId.Length > 128) return null;
        foreach (var path in EnumerateManifestPaths())
        {
            var clip = await ReadManifestAsync(path, cancellationToken);
            if (clip?.PublicId == publicId) return clip;
        }
        return null;
    }

    private async Task<string?> EnsurePlaybackAsync(ClipManifestV1 clip, string source, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(monitoring.SpoolDirectory, "clips", "playback");
        var destination = Path.Combine(directory, $"{clip.ClipId}-{clip.MediaSha256![..12]}-a1.mp4");
        if (File.Exists(destination) && new FileInfo(destination).Length > 0) return destination;
        await _conversionGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(destination) && new FileInfo(destination).Length > 0) return destination;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"{clip.ClipId}-{Guid.NewGuid():N}.tmp.mp4");
            try
            {
                if (!await RemuxAsync(source, temporary, cancellationToken)) return null;
                if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) return null;
                File.Move(temporary, destination, overwrite: true);
                DeleteLegacyPlayback(directory, clip);
                Prune(directory);
                return destination;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            _conversionGate.Release();
        }
    }

    /// <summary>
    /// Command line shared by the playback remux. Audio is optional so clips recorded without a
    /// microphone still produce a playable MP4.
    /// </summary>
    public static IReadOnlyList<string> BuildRemuxArguments(string source, string destination) =>
    [
        "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", source,
        "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-movflags", "+faststart", "-f", "mp4", destination
    ];

    private static void DeleteLegacyPlayback(string directory, ClipManifestV1 clip)
    {
        var legacy = Path.Combine(directory, $"{clip.ClipId}-{clip.MediaSha256![..12]}.mp4");
        try
        {
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<bool> RemuxAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(capture.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in BuildRemuxArguments(source, destination)) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return false; }
    }

    private static void Prune(string directory)
    {
        const long maximumBytes = 2L * 1024 * 1024 * 1024;
        var files = new DirectoryInfo(directory).EnumerateFiles("*.mp4")
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total <= maximumBytes) continue;
            try { file.Delete(); } catch (IOException) { }
        }
    }

    private async Task<ClipManifestV1?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var clip = await JsonSerializer.DeserializeAsync<ClipManifestV1>(stream, JsonOptions, cancellationToken);
            if (clip is not { SchemaVersion: "1.0" } || !SpoolWriter.IsSafeSegmentId(clip.ClipId) ||
                string.IsNullOrWhiteSpace(clip.PublicId) || Path.GetFileName(path) != clip.ClipId + ".json" ||
                !States.Contains(clip.State)) return null;
            return clip;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private IEnumerable<string> EnumerateManifestPaths()
    {
        var directory = Path.Combine(monitoring.SpoolDirectory, "clips", "manifests");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Take(10_000)
            .Select(file => file.FullName);
    }

    private string? Resolve(string kind, string? filename, string? hash)
    {
        if (string.IsNullOrWhiteSpace(filename) || Path.GetFileName(filename) != filename ||
            string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return null;
        var path = Path.Combine(monitoring.SpoolDirectory, "clips", kind, filename);
        return File.Exists(path) ? path : null;
    }

    private ClipLibraryItem Map(ClipManifestV1 clip)
    {
        var media = Resolve("media", clip.MediaFileName, clip.MediaSha256);
        return new ClipLibraryItem(clip.PublicId!, clip.ClipId, clip.CameraId, clip.EffectiveStartedAt,
            clip.EffectiveEndedAt, clip.State, clip.ArchiveStatus, clip.TriggerReason, clip.TriggerConfigVersion,
            clip.MediaSha256, clip.ThumbnailSha256, clip.AttemptCount, clip.UpdatedAt,
            media is null ? null : new FileInfo(media).Length);
    }
}
