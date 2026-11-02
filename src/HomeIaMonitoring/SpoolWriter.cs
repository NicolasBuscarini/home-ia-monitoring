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

public sealed class SpoolWriter(MonitoringOptions monitoring, CaptureWriterOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _manifestDirectory = Path.Combine(monitoring.SpoolDirectory, "manifests");
    private readonly string _segmentDirectory = Path.Combine(monitoring.SpoolDirectory, "segments");

    public async Task<SegmentManifestV1> StoreAsync(Stream source, DateTimeOffset startedAt,
        DateTimeOffset endedAt, string streamEpoch, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
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
                    if (copied > options.MaximumSegmentBytes)
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
}
