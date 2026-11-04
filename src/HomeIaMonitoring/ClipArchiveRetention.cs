using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace HomeIaMonitoring;

public sealed record ArchiveOptions(bool Enabled, bool WorkerEnabled, string Directory, int PollSeconds,
    int MaximumClips, long MaximumBytes, long MaximumClipBytes)
{
    public const string EnabledKey = "MONITORING_ARCHIVE_ENABLED";
    public const string DirectoryKey = "MONITORING_ARCHIVE_DIRECTORY";
    public const string WorkerEnabledKey = "MONITORING_RETENTION_WORKER_ENABLED";
    public const string PollSecondsKey = "MONITORING_ARCHIVE_POLL_SECONDS";
    public const string MaximumClipsKey = "MONITORING_ARCHIVE_MAX_CLIPS";
    public const string MaximumBytesKey = "MONITORING_ARCHIVE_MAX_BYTES";
    public const string MaximumClipBytesKey = "MONITORING_ARCHIVE_MAX_CLIP_BYTES";

    public static ArchiveOptions FromConfiguration(IConfiguration configuration, MonitoringOptions monitoring)
    {
        var enabled = configuration.GetValue<bool?>(EnabledKey) ?? false;
        var workerEnabled = configuration.GetValue<bool?>(WorkerEnabledKey) ?? true;
        var directory = configuration[DirectoryKey]?.Trim() ?? "";
        if (!enabled && directory.Length == 0)
            return new(false, workerEnabled, "", 5, 10_000, 53_687_091_200, 1_073_741_824);
        if (!Path.IsPathRooted(directory))
            throw new InvalidOperationException($"{DirectoryKey} deve ser absoluto.");
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var spool = Path.GetFullPath(monitoring.SpoolDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (full == spool || full.StartsWith(spool + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            spool.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"{DirectoryKey} não pode sobrepor o spool.");
        var poll = configuration.GetValue<int?>(PollSecondsKey) ?? 5;
        var maxClips = configuration.GetValue<int?>(MaximumClipsKey) ?? 10_000;
        var maxBytes = configuration.GetValue<long?>(MaximumBytesKey) ?? 53_687_091_200;
        var maxClipBytes = configuration.GetValue<long?>(MaximumClipBytesKey) ?? 1_073_741_824;
        if (poll is < 2 or > 3600 || maxClips is < 1 or > 100_000 || maxBytes < maxClipBytes ||
            maxClipBytes is < 1_048_576 or > 10_737_418_240)
            throw new InvalidOperationException("A configuração de arquivo de clipes é inválida.");
        return new(enabled, workerEnabled, full, poll, maxClips, maxBytes, maxClipBytes);
    }
}

public sealed record ArchivedClipManifestV1(string SchemaVersion, string ClipId, string PublicId, string CameraId,
    DateTimeOffset RequestedStartedAt, DateTimeOffset RequestedEndedAt, DateTimeOffset EffectiveStartedAt,
    DateTimeOffset EffectiveEndedAt, string TriggerConfigVersion, string TriggerReason,
    IReadOnlyList<string> SegmentIds, string MediaFileName, long MediaSizeBytes, string MediaSha256,
    string ThumbnailFileName, long ThumbnailSizeBytes, string ThumbnailSha256, DateTimeOffset ArchivedAt);

public sealed record RetentionPolicyV1(string SchemaVersion, string CameraId, int RetentionDays,
    long? MaximumStorageBytes, long Version, DateTimeOffset UpdatedAtUtc);

public sealed record RetentionPolicyResult(string CameraId, int RetentionDays, int? MaximumStorageGigabytes,
    long Version, DateTimeOffset? UpdatedAtUtc);

public sealed record PreservationV1(string SchemaVersion, string ClipPublicId, string ClipId, string CameraId,
    bool IsPreserved, long Version, DateTimeOffset UpdatedAtUtc);

public sealed record PreservationResult(string ClipPublicId, bool IsPreserved, long Version);

public sealed class RetentionConflictException(string code) : Exception(code);

public sealed class ClipArchiveService(MonitoringOptions monitoring, ArchiveOptions options,
    TimeProvider timeProvider)
{
    private const long BytesPerGiB = 1_073_741_824;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);

    public string ManifestDirectory => Path.Combine(monitoring.SpoolDirectory, "clips", "manifests");
    public string MediaDirectory => Path.Combine(monitoring.SpoolDirectory, "clips", "media");
    public string ThumbnailDirectory => Path.Combine(monitoring.SpoolDirectory, "clips", "thumbnails");

    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return false;
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.Directory);
            if (!Directory.Exists(ManifestDirectory)) return false;
            var archived = 0;
            foreach (var path in Directory.EnumerateFiles(ManifestDirectory, "*.json").Take(10_000))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clip = await ReadManifestAsync(path, cancellationToken);
                if (clip is null || clip.State != "ready" || clip.ArchiveStatus == "expired" ||
                    clip.ArchiveStatus == "archived") continue;
                if (clip.ArchiveAttemptCount >= 8 && clip.ArchiveStatus == "failed") continue;
                if (clip.ArchiveNextAttemptAt is { } next && next > timeProvider.GetUtcNow()) continue;
                try
                {
                    var manifest = await ArchiveOneAsync(clip, cancellationToken);
                    if (manifest is null) continue;
                    await PersistManifestAsync(path, clip with
                    {
                        ArchiveStatus = "archived",
                        ArchiveAttemptCount = clip.ArchiveAttemptCount + 1,
                        ArchivedAt = manifest.ArchivedAt,
                        ArchiveManifestSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions)),
                        ArchiveNextAttemptAt = null,
                        UpdatedAt = timeProvider.GetUtcNow()
                    }, cancellationToken);
                    archived++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   InvalidOperationException or JsonException)
                {
                    var attempt = clip.ArchiveAttemptCount + 1;
                    await PersistManifestAsync(path, clip with
                    {
                        ArchiveStatus = "failed",
                        ArchiveAttemptCount = attempt,
                        ArchiveNextAttemptAt = timeProvider.GetUtcNow()
                            .AddSeconds(Math.Min(300, Math.Pow(2, attempt))),
                        UpdatedAt = timeProvider.GetUtcNow()
                    }, cancellationToken);
                }
            }
            return archived > 0;
        }
        finally { gate.Release(); }
    }

    private async Task<ArchivedClipManifestV1?> ArchiveOneAsync(ClipManifestV1 clip,
        CancellationToken cancellationToken)
    {
        if (!IsSafeToken(clip.CameraId) || !SpoolWriter.IsSafeSegmentId(clip.ClipId) ||
            !IsClipPublicId(clip.PublicId) || clip.MediaFileName is null || clip.ThumbnailFileName is null ||
            Path.GetFileName(clip.MediaFileName) != clip.MediaFileName ||
            Path.GetFileName(clip.ThumbnailFileName) != clip.ThumbnailFileName ||
            !IsHash(clip.MediaSha256) || !IsHash(clip.ThumbnailSha256)) return null;
        var mediaPath = Path.Combine(MediaDirectory, clip.MediaFileName);
        var thumbnailPath = Path.Combine(ThumbnailDirectory, clip.ThumbnailFileName);
        if (!File.Exists(mediaPath) || !File.Exists(thumbnailPath) ||
            !await HasHashAsync(mediaPath, clip.MediaSha256!, cancellationToken) ||
            !await HasHashAsync(thumbnailPath, clip.ThumbnailSha256!, cancellationToken)) return null;
        var mediaSize = new FileInfo(mediaPath).Length;
        var thumbnailSize = new FileInfo(thumbnailPath).Length;
        if (mediaSize + thumbnailSize > options.MaximumClipBytes)
            throw new InvalidOperationException("archive_clip_too_large");

        var cameraDirectory = Path.Combine(options.Directory, clip.CameraId);
        var target = Path.Combine(cameraDirectory, clip.ClipId);
        var existing = await ReadArchiveAsync(target, cancellationToken);
        if (existing is not null) return existing;
        EnsureCapacity(mediaSize + thumbnailSize);
        Directory.CreateDirectory(target);
        await CopyVerifiedAsync(mediaPath, Path.Combine(target, clip.MediaFileName), clip.MediaSha256!,
            cancellationToken);
        await CopyVerifiedAsync(thumbnailPath, Path.Combine(target, clip.ThumbnailFileName),
            clip.ThumbnailSha256!, cancellationToken);
        var manifest = new ArchivedClipManifestV1("1.0", clip.ClipId, clip.PublicId!, clip.CameraId,
            clip.RequestedStartedAt, clip.RequestedEndedAt, clip.EffectiveStartedAt, clip.EffectiveEndedAt,
            clip.TriggerConfigVersion, clip.TriggerReason, clip.SegmentIds, clip.MediaFileName, mediaSize,
            clip.MediaSha256!, clip.ThumbnailFileName, thumbnailSize, clip.ThumbnailSha256!,
            timeProvider.GetUtcNow());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await WriteAtomicAsync(Path.Combine(target, "archive-manifest.json"), bytes, cancellationToken);
        return manifest;
    }

    private void EnsureCapacity(long incomingBytes)
    {
        var (clips, bytes) = MeasureUsage(options.Directory);
        if (clips + 1 > options.MaximumClips || bytes + incomingBytes > options.MaximumBytes)
            throw new InvalidOperationException("archive_capacity_reached");
    }

    private async Task<ArchivedClipManifestV1?> ReadArchiveAsync(string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "archive-manifest.json");
        if (!File.Exists(path) || IsReparse(directory) || IsReparse(path)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<ArchivedClipManifestV1>(
                await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
            if (manifest is not { SchemaVersion: "1.0" } || !SpoolWriter.IsSafeSegmentId(manifest.ClipId) ||
                !IsClipPublicId(manifest.PublicId) || Path.GetFileName(directory) != manifest.ClipId ||
                !File.Exists(Path.Combine(directory, manifest.MediaFileName)) ||
                !File.Exists(Path.Combine(directory, manifest.ThumbnailFileName))) return null;
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task CopyVerifiedAsync(string source, string destination, string expectedHash,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination) && await HasHashAsync(destination, expectedHash, cancellationToken)) return;
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.WriteThrough))
                await input.CopyToAsync(output, cancellationToken);
            if (!await HasHashAsync(temporary, expectedHash, cancellationToken))
                throw new IOException("archive_hash_mismatch");
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<ClipManifestV1?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var clip = JsonSerializer.Deserialize<ClipManifestV1>(
                await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
            return clip is { SchemaVersion: "1.0" } && Path.GetFileName(path) == clip.ClipId + ".json" &&
                   SpoolWriter.IsSafeSegmentId(clip.ClipId) && IsClipPublicId(clip.PublicId)
                ? clip : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException) { return null; }
    }

    private static async Task PersistManifestAsync(string path, ClipManifestV1 clip,
        CancellationToken cancellationToken) =>
        await WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(clip, JsonOptions), cancellationToken);

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static (int Clips, long Bytes) MeasureUsage(string root)
    {
        if (!Directory.Exists(root)) return (0, 0);
        var clips = 0;
        long bytes = 0;
        foreach (var camera in Directory.EnumerateDirectories(root))
            foreach (var directory in Directory.EnumerateDirectories(camera))
            {
                if (!File.Exists(Path.Combine(directory, "archive-manifest.json"))) continue;
                clips++;
                bytes += Directory.EnumerateFiles(directory).Sum(file => new FileInfo(file).Length);
            }
        return (clips, bytes);
    }

    internal static bool TryReadArchive(string directory, out ArchivedClipManifestV1? manifest)
    {
        manifest = null;
        try
        {
            var path = Path.Combine(directory, "archive-manifest.json");
            if (!File.Exists(path) || IsReparse(directory) || IsReparse(path)) return false;
            var value = JsonSerializer.Deserialize<ArchivedClipManifestV1>(File.ReadAllText(path), JsonOptions);
            if (value is not { SchemaVersion: "1.0" } || !SpoolWriter.IsSafeSegmentId(value.ClipId) ||
                !IsClipPublicId(value.PublicId) || Path.GetFileName(directory) != value.ClipId) return false;
            manifest = value;
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    internal static bool IsSafeToken(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    internal static bool IsClipPublicId(string? value) => value is { Length: 15 } &&
        value.StartsWith("CLP", StringComparison.Ordinal) && value[3..].All(char.IsAsciiDigit);

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static async Task<bool> HasHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        if (!IsHash(expected) || !File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// Versioned retention policy and preservation state stored beside the spool. Authorization and audit
/// remain in the portal; this store only persists the operational decision for the worker.
/// </summary>
public sealed class RetentionStore(MonitoringOptions monitoring, TimeProvider timeProvider)
{
    private const int DefaultRetentionDays = 15;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, RetentionPolicyV1> policies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreservationV1> preservations = new(StringComparer.Ordinal);
    private bool loaded;

    private string Root => Path.Combine(monitoring.SpoolDirectory, "retention");
    private string PoliciesDirectory => Path.Combine(Root, "policies");
    private string PreservationsDirectory => Path.Combine(Root, "preservations");
    private string ManifestDirectory => Path.Combine(monitoring.SpoolDirectory, "clips", "manifests");

    public async Task<RetentionPolicyResult> GetPolicyAsync(string cameraId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return Map(cameraId, policies.GetValueOrDefault(cameraId));
        }
        finally { gate.Release(); }
    }

    public async Task<RetentionPolicyResult> UpdatePolicyAsync(string cameraId, long expectedVersion,
        int retentionDays, int? maximumStorageGigabytes, CancellationToken cancellationToken)
    {
        if (!ClipArchiveService.IsSafeToken(cameraId) || expectedVersion < 0 ||
            retentionDays is < 1 or > 3650 || maximumStorageGigabytes is < 1 or > 1024)
            throw new ArgumentException("invalid_clip_retention_policy");
        long? maximumBytes = maximumStorageGigabytes is { } gigabytes
            ? checked(gigabytes * 1_073_741_824L) : null;
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var current = policies.GetValueOrDefault(cameraId);
            var version = current?.Version ?? 0;
            if (version != expectedVersion)
                throw new RetentionConflictException("clip_retention_version_conflict");
            if (current is not null && current.RetentionDays == retentionDays &&
                current.MaximumStorageBytes == maximumBytes) return Map(cameraId, current);
            var now = timeProvider.GetUtcNow();
            var next = new RetentionPolicyV1("1.0", cameraId, retentionDays, maximumBytes, version + 1, now);
            Directory.CreateDirectory(PoliciesDirectory);
            await WriteAtomicAsync(Path.Combine(PoliciesDirectory, cameraId + ".json"),
                JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions), cancellationToken);
            policies[cameraId] = next;
            return Map(cameraId, next);
        }
        finally { gate.Release(); }
    }

    public async Task<PreservationResult> SetPreservationAsync(string clipPublicId, long expectedVersion,
        bool isPreserved, CancellationToken cancellationToken)
    {
        if (!ClipArchiveService.IsClipPublicId(clipPublicId) || expectedVersion < 0)
            throw new ArgumentException("invalid_clip_preservation");
        var manifest = await FindManifestAsync(clipPublicId, cancellationToken) ??
                       throw new KeyNotFoundException();
        if (manifest.State != "ready" || manifest.ArchiveStatus != "archived")
            throw new RetentionConflictException("clip_not_preservable");
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var current = preservations.GetValueOrDefault(clipPublicId);
            var version = current?.Version ?? 0;
            if (version != expectedVersion)
                throw new RetentionConflictException("clip_preservation_version_conflict");
            if (current is not null && current.IsPreserved == isPreserved)
                return new(clipPublicId, isPreserved, version);
            var next = new PreservationV1("1.0", clipPublicId, manifest.ClipId, manifest.CameraId,
                isPreserved, version + 1, timeProvider.GetUtcNow());
            Directory.CreateDirectory(PreservationsDirectory);
            await WriteAtomicAsync(Path.Combine(PreservationsDirectory, clipPublicId + ".json"),
                JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions), cancellationToken);
            preservations[clipPublicId] = next;
            return new(clipPublicId, next.IsPreserved, next.Version);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<PreservationResult>> ListPreservationsAsync(
        IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken)
    {
        if (clipPublicIds.Count is 0 or > 100 || clipPublicIds.Any(id => !ClipArchiveService.IsClipPublicId(id)))
            throw new ArgumentException("invalid_clip_preservation_query");
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return clipPublicIds.Select(id => preservations.GetValueOrDefault(id))
                .Where(item => item is not null)
                .Select(item => new PreservationResult(item!.ClipPublicId, item.IsPreserved, item.Version))
                .ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<RetentionPolicyV1> GetRawPolicyAsync(string cameraId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return policies.GetValueOrDefault(cameraId) ??
                   new RetentionPolicyV1("1.0", cameraId, DefaultRetentionDays, null, 0, default);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlySet<string>> GetPreservedClipIdsAsync(string cameraId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return preservations.Values.Where(item => item.CameraId == cameraId && item.IsPreserved)
                .Select(item => item.ClipId).ToHashSet(StringComparer.Ordinal);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlySet<string>> GetPreservedPublicIdsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return preservations.Values.Where(item => item.IsPreserved)
                .Select(item => item.ClipPublicId).ToHashSet(StringComparer.Ordinal);
        }
        finally { gate.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (loaded) return;
        Directory.CreateDirectory(PoliciesDirectory);
        Directory.CreateDirectory(PreservationsDirectory);
        foreach (var temporary in Directory.EnumerateFiles(PoliciesDirectory, "*.tmp-*")
                     .Concat(Directory.EnumerateFiles(PreservationsDirectory, "*.tmp-*")))
            File.Delete(temporary);
        foreach (var path in Directory.EnumerateFiles(PoliciesDirectory, "*.json"))
        {
            try
            {
                var policy = JsonSerializer.Deserialize<RetentionPolicyV1>(
                    await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
                if (policy is { SchemaVersion: "1.0" } && ClipArchiveService.IsSafeToken(policy.CameraId) &&
                    Path.GetFileNameWithoutExtension(path) == policy.CameraId &&
                    policy.RetentionDays is >= 1 and <= 3650 && policy.Version >= 0)
                    policies[policy.CameraId] = policy;
            }
            catch (Exception exception) when (exception is IOException or JsonException) { }
        }
        foreach (var path in Directory.EnumerateFiles(PreservationsDirectory, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<PreservationV1>(
                    await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
                if (item is { SchemaVersion: "1.0" } && ClipArchiveService.IsClipPublicId(item.ClipPublicId) &&
                    Path.GetFileNameWithoutExtension(path) == item.ClipPublicId && item.Version >= 0)
                    preservations[item.ClipPublicId] = item;
            }
            catch (Exception exception) when (exception is IOException or JsonException) { }
        }
        loaded = true;
    }

    private async Task<ClipManifestV1?> FindManifestAsync(string publicId, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ManifestDirectory)) return null;
        foreach (var path in Directory.EnumerateFiles(ManifestDirectory, "*.json").Take(10_000))
        {
            try
            {
                var clip = JsonSerializer.Deserialize<ClipManifestV1>(
                    await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
                if (clip is { SchemaVersion: "1.0" } && clip.PublicId == publicId &&
                    Path.GetFileName(path) == clip.ClipId + ".json" &&
                    SpoolWriter.IsSafeSegmentId(clip.ClipId)) return clip;
            }
            catch (Exception exception) when (exception is IOException or JsonException) { }
        }
        return null;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static RetentionPolicyResult Map(string cameraId, RetentionPolicyV1? policy) =>
        new(cameraId, policy?.RetentionDays ?? DefaultRetentionDays,
            policy?.MaximumStorageBytes is { } bytes ? checked((int)(bytes / 1_073_741_824L)) : null,
            policy?.Version ?? 0, policy?.UpdatedAtUtc);
}

public sealed class ClipRetentionWorker(ArchiveOptions options, ClipArchiveService archive,
    RetentionStore store, AnalysisDispatchStore analysis, MonitoringOptions monitoring, TimeProvider timeProvider)
    : BackgroundService
{
    private readonly SemaphoreSlim runGate = new(1, 1);

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await runGate.WaitAsync(cancellationToken);
        try
        {
            if (!options.Enabled) return;
            await archive.RunOnceAsync(cancellationToken);
            foreach (var cameraDirectory in Directory.EnumerateDirectories(options.Directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cameraId = Path.GetFileName(cameraDirectory);
                if (!ClipArchiveService.IsSafeToken(cameraId)) continue;
                var entries = new List<ArchivedClipManifestV1>();
                foreach (var directory in Directory.EnumerateDirectories(cameraDirectory))
                    if (ClipArchiveService.TryReadArchive(directory, out var manifest) && manifest is not null &&
                        manifest.CameraId == cameraId) entries.Add(manifest);
                if (entries.Count == 0) continue;
                var policy = await store.GetRawPolicyAsync(cameraId, cancellationToken);
                var preserved = await store.GetPreservedClipIdsAsync(cameraId, cancellationToken);
                var active = await analysis.ListActiveClipIdsAsync(cameraId, cancellationToken);
                var protectedIds = preserved.Concat(active).ToHashSet(StringComparer.Ordinal);
                var ordered = entries.OrderBy(item => item.EffectiveEndedAt).ThenBy(item => item.ClipId,
                    StringComparer.Ordinal).ToList();
                var cutoff = timeProvider.GetUtcNow().AddDays(-policy.RetentionDays);
                var expired = ordered.Where(item => item.EffectiveEndedAt <= cutoff &&
                    !protectedIds.Contains(item.ClipId)).ToList();
                long total = ordered.Where(item => !expired.Contains(item))
                    .Sum(item => item.MediaSizeBytes + item.ThumbnailSizeBytes);
                if (policy.MaximumStorageBytes is { } maximum)
                    foreach (var entry in ordered)
                    {
                        if (total <= maximum) break;
                        if (protectedIds.Contains(entry.ClipId) || expired.Contains(entry)) continue;
                        expired.Add(entry);
                        total -= entry.MediaSizeBytes + entry.ThumbnailSizeBytes;
                    }
                foreach (var entry in expired)
                    await ExpireAsync(cameraId, entry, cancellationToken);
            }
        }
        finally { runGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { /* The next poll retries; failures stay observable in health/status. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ExpireAsync(string cameraId, ArchivedClipManifestV1 entry,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(options.Directory, cameraId, entry.ClipId);
        if (!Directory.Exists(directory) || ClipArchiveService.IsReparse(directory)) return;
        var manifestPath = Path.Combine(directory, "archive-manifest.json");
        var mediaPath = Path.Combine(directory, entry.MediaFileName);
        var thumbnailPath = Path.Combine(directory, entry.ThumbnailFileName);
        if (!File.Exists(manifestPath) || !File.Exists(mediaPath) || !File.Exists(thumbnailPath) ||
            ClipArchiveService.IsReparse(manifestPath) || ClipArchiveService.IsReparse(mediaPath) ||
            ClipArchiveService.IsReparse(thumbnailPath)) return;
        await MarkSourceExpiredAsync(entry, cancellationToken);
        File.Delete(mediaPath);
        File.Delete(thumbnailPath);
        File.Delete(manifestPath);
        Directory.Delete(directory);
    }

    private async Task MarkSourceExpiredAsync(ArchivedClipManifestV1 entry, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(monitoring.SpoolDirectory, "clips", "manifests");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(10_000))
        {
            try
            {
                var clip = JsonSerializer.Deserialize<ClipManifestV1>(
                    await File.ReadAllTextAsync(path, cancellationToken),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (clip is not { SchemaVersion: "1.0" } || clip.ClipId != entry.ClipId) continue;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(clip with
                {
                    ArchiveStatus = "expired",
                    UpdatedAt = timeProvider.GetUtcNow()
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or JsonException) { }
        }
    }
}
