using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace HomeIaMonitoring;

public sealed record CaptureWriterOptions(bool Enabled, int SegmentSeconds, int MaximumSegments, long MaximumSegmentBytes)
{
    public const string EnabledKey = "MONITORING_CAPTURE_ENABLED";
    public const string SegmentSecondsKey = "MONITORING_CAPTURE_SEGMENT_SECONDS";
    public const string MaximumSegmentsKey = "MONITORING_CAPTURE_MAX_SEGMENTS";
    public const string MaximumSegmentBytesKey = "MONITORING_CAPTURE_MAX_SEGMENT_BYTES";

    public static CaptureWriterOptions FromConfiguration(IConfiguration configuration)
    {
        var enabled = false;
        if (configuration[EnabledKey] is { } raw && !bool.TryParse(raw, out enabled))
            throw new InvalidOperationException("MONITORING_CAPTURE_ENABLED deve ser true ou false.");
        return new CaptureWriterOptions(enabled,
            Bounded(configuration[SegmentSecondsKey], 5, 1, 60, SegmentSecondsKey),
            Bounded(configuration[MaximumSegmentsKey], 120, 1, 10_000, MaximumSegmentsKey),
            LongBounded(configuration[MaximumSegmentBytesKey], 67_108_864, 1_048_576, 1_073_741_824,
                MaximumSegmentBytesKey));
    }

    private static int Bounded(string? raw, int fallback, int minimum, int maximum, string name)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} fora do limite permitido.");
        return value;
    }

    private static long LongBounded(string? raw, long fallback, long minimum, long maximum, string name)
    {
        if (raw is null) return fallback;
        if (!long.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} fora do limite permitido.");
        return value;
    }
}

public sealed record SegmentManifestV1(
    string SchemaVersion,
    string SegmentId,
    string CameraId,
    string StreamEpoch,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string FileName,
    long SizeBytes,
    string Sha256);

public sealed record CaptureGapV1(string SchemaVersion, string GapId, string CameraId,
    string? PreviousStreamEpoch, DateTimeOffset OccurredAt, string ReasonCode, int RetryAttempt);

public sealed class SpoolWriter(MonitoringOptions monitoring, CaptureSettingsStore settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _manifestDirectory = Path.Combine(monitoring.SpoolDirectory, "manifests");
    private readonly string _segmentDirectory = Path.Combine(monitoring.SpoolDirectory, "segments");

    public async Task<SegmentManifestV1> StoreAsync(Stream source, DateTimeOffset startedAt,
        DateTimeOffset endedAt, string streamEpoch, CancellationToken cancellationToken = default)
    {
        var current = settings.Current;
        if (!current.Enabled)
            throw new InvalidOperationException("capture_writer_disabled");
        if (endedAt < startedAt) throw new InvalidDataException("capture_segment_invalid");
        Directory.CreateDirectory(_manifestDirectory);
        Directory.CreateDirectory(_segmentDirectory);
        Directory.CreateDirectory(Path.Combine(monitoring.SpoolDirectory, "work"));
        Directory.CreateDirectory(Path.Combine(monitoring.SpoolDirectory, "gaps"));

        var segmentId = Guid.NewGuid().ToString("N");
        var fileName = segmentId + ".ts";
        var destination = Path.Combine(_segmentDirectory, fileName);
        var temporary = destination + ".tmp";
        var manifestPath = Path.Combine(_manifestDirectory, segmentId + ".json");
        var manifestTemporary = manifestPath + ".tmp";
        try
        {
            long copied = 0;
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    copied += read;
                    if (copied > current.MaximumSegmentBytes)
                        throw new InvalidDataException("capture_segment_size_limit");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            if (copied < 1) throw new InvalidDataException("capture_segment_invalid");
            string hash;
            await using (var file = File.OpenRead(temporary))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
            File.Move(temporary, destination);
            var manifest = new SegmentManifestV1("1.0", segmentId, monitoring.CameraId, streamEpoch, startedAt,
                endedAt, fileName, copied, hash);
            await File.WriteAllTextAsync(manifestTemporary, JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken);
            File.Move(manifestTemporary, manifestPath);
            return manifest;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(manifestTemporary)) File.Delete(manifestTemporary);
            if (!File.Exists(manifestPath) && File.Exists(destination)) File.Delete(destination);
        }
    }

    public IReadOnlyList<SegmentManifestV1> ReadManifests()
    {
        if (!Directory.Exists(_manifestDirectory)) return [];
        var manifests = new List<SegmentManifestV1>();
        foreach (var path in Directory.EnumerateFiles(_manifestDirectory, "*.json"))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<SegmentManifestV1>(
                    File.ReadAllText(path), JsonOptions);
                if (manifest is { SchemaVersion: "1.0" } && manifest.CameraId == monitoring.CameraId)
                    manifests.Add(manifest);
            }
            catch (JsonException) { }
        }
        return manifests.OrderBy(item => item.StartedAt).ToArray();
    }

    public long CountGaps()
    {
        var directory = Path.Combine(monitoring.SpoolDirectory, "gaps");
        if (!Directory.Exists(directory)) return 0;
        long count = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("cameraId", out var camera) &&
                    camera.GetString() == monitoring.CameraId)
                    count++;
            }
            catch (JsonException) { }
        }
        return count;
    }

    public async Task RecordGapAsync(string? previousEpoch, int retryAttempt,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(monitoring.SpoolDirectory, "gaps");
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var gap = new CaptureGapV1("1.0", id, monitoring.CameraId, previousEpoch, DateTimeOffset.UtcNow,
            "capture_source_unavailable", retryAttempt);
        var temporary = Path.Combine(directory, "gap.pending.tmp");
        var path = Path.Combine(directory, id + ".json");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(gap, JsonOptions),
                cancellationToken);
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        EnforceGapRetention(directory);
    }

    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_manifestDirectory);
        Directory.CreateDirectory(_segmentDirectory);
        Directory.CreateDirectory(Path.Combine(monitoring.SpoolDirectory, "work"));
        var gapDirectory = Path.Combine(monitoring.SpoolDirectory, "gaps");
        Directory.CreateDirectory(gapDirectory);
        foreach (var temporary in Directory.EnumerateFiles(_segmentDirectory, "*.tmp")) File.Delete(temporary);
        foreach (var temporary in Directory.EnumerateFiles(_manifestDirectory, "*.tmp")) File.Delete(temporary);
        foreach (var temporary in Directory.EnumerateFiles(gapDirectory, "*.tmp")) File.Delete(temporary);
        foreach (var work in Directory.EnumerateDirectories(Path.Combine(monitoring.SpoolDirectory, "work")))
            Directory.Delete(work, recursive: true);

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var valid = 0;
        foreach (var path in Directory.EnumerateFiles(_manifestDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestId = Path.GetFileNameWithoutExtension(path);
            var segmentPath = Path.Combine(_segmentDirectory, manifestId + ".ts");
            var isValid = false;
            try
            {
                var manifest = JsonSerializer.Deserialize<SegmentManifestV1>(
                    await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
                if (IsValidManifest(manifest, manifestId) && File.Exists(segmentPath) &&
                    new FileInfo(segmentPath).Length == manifest!.SizeBytes)
                {
                    await using var file = File.OpenRead(segmentPath);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken))
                        .ToLowerInvariant();
                    isValid = hash == manifest.Sha256;
                }
            }
            catch (JsonException) { }
            if (isValid)
            {
                valid++;
                referenced.Add(manifestId);
                continue;
            }
            if (File.Exists(path)) File.Delete(path);
            if (IsSafeSegmentId(manifestId) && File.Exists(segmentPath)) File.Delete(segmentPath);
        }
        foreach (var segment in Directory.EnumerateFiles(_segmentDirectory, "*.ts"))
            if (!referenced.Contains(Path.GetFileNameWithoutExtension(segment))) File.Delete(segment);
        return valid;
    }

    public int EnforceRetention()
    {
        var manifests = Directory.GetFiles(_manifestDirectory, "*.json")
            .OrderBy(File.GetCreationTimeUtc).ToArray();
        var pinned = GetPinnedSegmentIds();
        foreach (var old in manifests.Where(path => !pinned.Contains(Path.GetFileNameWithoutExtension(path)))
                     .Take(Math.Max(0, manifests.Length - settings.Current.MaximumSegments)))
        {
            var segmentId = Path.GetFileNameWithoutExtension(old);
            try
            {
                if (IsSafeSegmentId(segmentId))
                    File.Delete(Path.Combine(_segmentDirectory, segmentId + ".ts"));
                File.Delete(old);
            }
            catch (IOException) { }
        }
        return Directory.GetFiles(_manifestDirectory, "*.json").Length;
    }

    public bool IsValidManifest(SegmentManifestV1? manifest, string expectedId) =>
        manifest is { SchemaVersion: "1.0" } && manifest.SegmentId == expectedId &&
        manifest.CameraId == monitoring.CameraId && IsSafeSegmentId(manifest.SegmentId) &&
        manifest.FileName == manifest.SegmentId + ".ts" && manifest.SizeBytes is > 0 and <= int.MaxValue &&
        manifest.SizeBytes <= settings.Current.MaximumSegmentBytes && manifest.Sha256.Length == 64 &&
        manifest.EndedAt >= manifest.StartedAt;

    public static bool IsSafeSegmentId(string value) =>
        value.Length == 32 && value.All(Uri.IsHexDigit);

    public IReadOnlySet<string> GetPinnedSegmentIds()
    {
        var directory = Path.Combine(monitoring.SpoolDirectory, "clips", "manifests");
        if (!Directory.Exists(directory)) return new HashSet<string>(StringComparer.Ordinal);
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("state", out var state) || state.GetString() != "collecting" ||
                    !root.TryGetProperty("segmentIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var id in ids.EnumerateArray().Select(value => value.GetString()))
                    if (id is not null && IsSafeSegmentId(id)) pinned.Add(id);
            }
            catch (JsonException) { }
        }
        return pinned;
    }

    private void EnforceGapRetention(string directory)
    {
        var gaps = Directory.GetFiles(directory, "*.json").OrderBy(File.GetCreationTimeUtc).ToArray();
        foreach (var old in gaps.Take(Math.Max(0, gaps.Length - settings.Current.MaximumSegments)))
        {
            try { File.Delete(old); }
            catch (IOException) { }
        }
    }
}
