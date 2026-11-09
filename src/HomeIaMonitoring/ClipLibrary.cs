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
/// Página de varredura do spool. <see cref="NextOffset"/> avança sobre os manifests na ordem
/// estável de escrita (mais recentes primeiro) e <see cref="HasMore"/> indica que a varredura
/// ainda não terminou. O offset existe para consumidores de fundo percorrerem todo o spool sem
/// reler os mesmos manifests a cada ciclo.
/// </summary>
public sealed record ClipScanPage(IReadOnlyList<ClipLibraryItem> Items, int NextOffset, bool HasMore);

public enum ClipDeleteStatus { Deleted, NotFound, NotReady, Preserved, Archived, Failed }

public sealed record ClipDeleteResult(ClipDeleteStatus Status, string? Error = null);

public sealed record ClipDeleteAllResult(int Deleted, int SkippedPreserved, int SkippedArchived,
    int SkippedNotReady, int SkippedActiveAnalysis, int Failed);

/// <summary>
/// Read-only clip library over the shared spool. Authorization stays in the portal; this service
/// only exposes manifests and validated media for the camera identifiers the caller supplies.
/// </summary>
public interface IClipPlaybackSource
{
    Task<ClipLibraryItem?> GetAsync(string publicId, CancellationToken cancellationToken);

    /// <summary>
    /// Página de clipes <c>ready</c> para consumidores de fundo, avançando pelo spool na ordem
    /// de escrita (mais recentes primeiro). Confirma somente estado e presença do arquivo de
    /// mídia; não relê bytes nem valida o hash a cada varredura.
    /// </summary>
    Task<ClipScanPage> ListReadyPageAsync(int take, int offset, CancellationToken cancellationToken);
    Task<string?> GetPlaybackPathAsync(string publicId, CancellationToken cancellationToken);
}

public sealed class ClipLibrary(MonitoringOptions monitoring, CaptureOptions capture,
    AnalysisDispatchStore analysisStore, RetentionStore retentionStore) : IClipPlaybackSource
{
    private readonly SemaphoreSlim _conversionGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] States = ["collecting", "ready", "failed", "lost_capacity"];

    public async Task<ClipLibraryPage> ListAsync(IReadOnlyCollection<string> cameraIds, string? publicId,
        DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? state, int? minDurationSeconds,
        int? maxDurationSeconds, string? analysis, bool? preserved, string? sort, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        if (page is < 1 or > 100_000 || pageSize is < 1 or > 100 ||
            (fromUtc.HasValue && toUtc.HasValue && fromUtc > toUtc) ||
            (minDurationSeconds is < 0 or > 86_400) || (maxDurationSeconds is < 0 or > 86_400) ||
            (minDurationSeconds.HasValue && maxDurationSeconds.HasValue &&
                minDurationSeconds > maxDurationSeconds) ||
            (state is not null && !States.Contains(state)) ||
            (analysis is not null && analysis is not ("performed" or "not_performed" or
                "pending" or "completed" or "failed")) ||
            (sort is not null && sort is not ("newest" or "oldest" or "longest" or "shortest" or
                "largest" or "smallest")))
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
            var filter = analysis;
            var jobStates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var batch in clips.Select(clip => clip.PublicId!).Chunk(100))
            {
                var jobs = await analysisStore.LatestForClipsAsync(batch, cancellationToken);
                foreach (var job in jobs) jobStates[job.ClipPublicId] = job.State;
            }
            clips = clips.Where(clip =>
            {
                var hasJob = jobStates.TryGetValue(clip.PublicId!, out var state);
                return filter switch
                {
                    "performed" => hasJob,
                    "not_performed" => !hasJob,
                    "pending" => hasJob && state is "analysis_pending" or "processing",
                    _ => hasJob && state == filter
                };
            }).ToList();
        }
        if (preserved is not null)
        {
            var states = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var batch in clips.Select(clip => clip.PublicId!).Chunk(100))
            {
                foreach (var preservation in await retentionStore.ListPreservationsAsync(batch, cancellationToken))
                    states[preservation.ClipPublicId] = preservation.IsPreserved;
            }
            clips = clips.Where(clip =>
                (states.TryGetValue(clip.PublicId!, out var isPreserved) && isPreserved) == preserved.Value)
                .ToList();
        }
        var ordered = sort switch
        {
            "oldest" => clips.OrderBy(clip => clip.EffectiveStartedAt)
                .ThenBy(clip => clip.ClipId, StringComparer.Ordinal),
            "longest" => clips.OrderByDescending(clip => clip.EffectiveEndedAt - clip.EffectiveStartedAt)
                .ThenByDescending(clip => clip.EffectiveStartedAt),
            "shortest" => clips.OrderBy(clip => clip.EffectiveEndedAt - clip.EffectiveStartedAt)
                .ThenByDescending(clip => clip.EffectiveStartedAt),
            "largest" => clips.OrderByDescending(MediaSize)
                .ThenByDescending(clip => clip.EffectiveStartedAt),
            "smallest" => clips.OrderBy(MediaSize)
                .ThenByDescending(clip => clip.EffectiveStartedAt),
            _ => clips.OrderByDescending(clip => clip.EffectiveStartedAt)
                .ThenBy(clip => clip.ClipId, StringComparer.Ordinal),
        };
        var orderedArray = ordered.ToArray();
        var items = orderedArray.Skip((page - 1) * pageSize).Take(pageSize).Select(Map).ToArray();
        return new(items, page, pageSize, orderedArray.Length);
    }

    public async Task<ClipScanPage> ListReadyPageAsync(int take, int offset,
        CancellationToken cancellationToken)
    {
        if (take < 1) return new([], Math.Max(0, offset), false);
        offset = Math.Max(0, offset);
        var items = new List<ClipLibraryItem>(Math.Min(take, 64));
        var examined = 0;
        var index = 0;
        foreach (var path in EnumerateManifestPaths())
        {
            if (index++ < offset) continue;
            examined++;
            cancellationToken.ThrowIfCancellationRequested();
            var clip = await ReadManifestAsync(path, cancellationToken);
            if (clip is null || clip.State != "ready") continue;
            if (Resolve("media", clip.MediaFileName, clip.MediaSha256) is null) continue;
            items.Add(Map(clip));
            if (items.Count >= take) return new(items, offset + examined, true);
        }

        return new(items, offset + examined, false);
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

    /// <summary>
    /// Removes one ready clip from the local spool. Preserved and archived clips are refused;
    /// the manifest is deleted last so an interrupted deletion cannot hide remaining media.
    /// </summary>
    public async Task<ClipDeleteResult> DeleteAsync(string publicId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicId) || publicId.Length > 64) return new(ClipDeleteStatus.NotFound);
        foreach (var manifestPath in EnumerateManifestPaths())
        {
            var clip = await ReadManifestAsync(manifestPath, cancellationToken);
            if (clip?.PublicId != publicId) continue;
            if (clip.State != "ready") return new(ClipDeleteStatus.NotReady, "clip_not_ready");
            var preservation = await retentionStore.ListPreservationsAsync([publicId], cancellationToken);
            if (preservation.Any(item => item.IsPreserved))
                return new(ClipDeleteStatus.Preserved, "clip_preserved");
            if (clip.ArchiveStatus == "archived") return new(ClipDeleteStatus.Archived, "clip_archived");
            return DeleteFiles(manifestPath, clip)
                ? new(ClipDeleteStatus.Deleted)
                : new(ClipDeleteStatus.Failed, "clip_delete_failed");
        }
        return new(ClipDeleteStatus.NotFound);
    }

    /// <summary>
    /// Deletes every ready clip that is neither preserved nor archived. Preservation is evaluated
    /// before archive state so a protected clip is never removed. Invalid manifests and clips in
    /// any other state are left in place.
    /// </summary>
    public async Task<ClipDeleteAllResult> DeleteAllAsync(CancellationToken cancellationToken)
    {
        var manifests = new List<(string Path, ClipManifestV1 Clip)>();
        foreach (var manifestPath in EnumerateManifestPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clip = await ReadManifestAsync(manifestPath, cancellationToken);
            if (clip is not null) manifests.Add((manifestPath, clip));
        }

        var preserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in manifests.Select(item => item.Clip.PublicId!).Chunk(100))
            foreach (var item in await retentionStore.ListPreservationsAsync(batch, cancellationToken))
                if (item.IsPreserved) preserved.Add(item.ClipPublicId);

        var activeAnalysis = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in manifests.Where(item => item.Clip.State == "ready" &&
                     item.Clip.ArchiveStatus != "archived" && !preserved.Contains(item.Clip.PublicId!))
                     .Select(item => item.Clip.PublicId!).Chunk(100))
            foreach (var job in await analysisStore.LatestForClipsAsync(batch, cancellationToken))
                if (job.State is "analysis_pending" or "processing") activeAnalysis.Add(job.ClipPublicId);

        var deleted = 0;
        var skippedPreserved = 0;
        var skippedArchived = 0;
        var skippedNotReady = 0;
        var skippedActiveAnalysis = 0;
        var failed = 0;
        foreach (var (manifestPath, clip) in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preserved.Contains(clip.PublicId!)) skippedPreserved++;
            else if (clip.ArchiveStatus == "archived") skippedArchived++;
            else if (clip.State != "ready") skippedNotReady++;
            else if (activeAnalysis.Contains(clip.PublicId!)) skippedActiveAnalysis++;
            else if (DeleteFiles(manifestPath, clip)) deleted++;
            else failed++;
        }
        return new(deleted, skippedPreserved, skippedArchived, skippedNotReady, skippedActiveAnalysis, failed);
    }

    private bool DeleteFiles(string manifestPath, ClipManifestV1 clip)
    {
        try
        {
            var media = Resolve("media", clip.MediaFileName, clip.MediaSha256);
            if (media is not null) File.Delete(media);
            var thumbnail = Resolve("thumbnails", clip.ThumbnailFileName, clip.ThumbnailSha256);
            if (thumbnail is not null) File.Delete(thumbnail);
            var playbackDirectory = Path.Combine(monitoring.SpoolDirectory, "clips", "playback");
            if (Directory.Exists(playbackDirectory))
                foreach (var playback in Directory.EnumerateFiles(playbackDirectory, $"{clip.ClipId}-*.mp4"))
                    File.Delete(playback);
            File.Delete(manifestPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
            .Select(file => file.FullName);
    }

    private string? Resolve(string kind, string? filename, string? hash)
    {
        if (string.IsNullOrWhiteSpace(filename) || Path.GetFileName(filename) != filename ||
            string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return null;
        var path = Path.Combine(monitoring.SpoolDirectory, "clips", kind, filename);
        return File.Exists(path) ? path : null;
    }

    private long MediaSize(ClipManifestV1 clip) =>
        Resolve("media", clip.MediaFileName, clip.MediaSha256) is { } path ? new FileInfo(path).Length : 0;

    private ClipLibraryItem Map(ClipManifestV1 clip)
    {
        var media = Resolve("media", clip.MediaFileName, clip.MediaSha256);
        return new ClipLibraryItem(clip.PublicId!, clip.ClipId, clip.CameraId, clip.EffectiveStartedAt,
            clip.EffectiveEndedAt, clip.State, clip.ArchiveStatus, clip.TriggerReason, clip.TriggerConfigVersion,
            clip.MediaSha256, clip.ThumbnailSha256, clip.AttemptCount, clip.UpdatedAt,
            media is null ? null : new FileInfo(media).Length);
    }
}
