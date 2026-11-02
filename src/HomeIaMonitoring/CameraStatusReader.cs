using System.Text.Json;

namespace HomeIaMonitoring;

public sealed record MonitoringCameraStatus(
    string SchemaVersion,
    string ModuleId,
    string CameraId,
    string Status,
    string? StreamEpoch,
    long Segments,
    long Gaps,
    DateTimeOffset? LastSegmentEndedAtUtc,
    string? ReasonCode,
    bool Reachable,
    string? ErrorCode);

public static class CameraStatusReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static MonitoringCameraStatus Read(MonitoringOptions options, string? requestedCameraId,
        DateTimeOffset now)
    {
        if (!string.Equals(requestedCameraId, options.CameraId, StringComparison.Ordinal))
            return new("1.0", "monitoring", requestedCameraId ?? string.Empty, "not_found", null, 0, 0,
                null, "camera_not_configured", false, "camera_not_configured");

        var manifests = ReadManifests(options);
        var gaps = CountGaps(options);
        if (manifests.Count == 0)
            return new("1.0", "monitoring", options.CameraId, "unavailable", null, 0, gaps, null,
                "no_segments", true, null);

        var latest = manifests.OrderBy(item => item.EndedAt).Last();
        var age = now - latest.EndedAt;
        var status = age > TimeSpan.FromSeconds(options.StatusStaleSeconds) ? "stale" : "capturing";
        return new("1.0", "monitoring", options.CameraId, status, latest.StreamEpoch, manifests.Count, gaps,
            latest.EndedAt, status == "stale" ? "segment_stale" : null, true, null);
    }

    private static List<Manifest> ReadManifests(MonitoringOptions options)
    {
        var directory = Path.Combine(options.SpoolDirectory, "manifests");
        if (!Directory.Exists(directory)) return [];
        var manifests = new List<Manifest>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetString() != "1.0" ||
                    !root.TryGetProperty("cameraId", out var camera) || camera.GetString() != options.CameraId ||
                    !root.TryGetProperty("streamEpoch", out var epoch) ||
                    !root.TryGetProperty("endedAt", out var endedAt) ||
                    !endedAt.TryGetDateTimeOffset(out var ended))
                    continue;
                manifests.Add(new Manifest(epoch.GetString(), ended));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException) { }
        }
        return manifests;
    }

    private static long CountGaps(MonitoringOptions options)
    {
        var directory = Path.Combine(options.SpoolDirectory, "gaps");
        if (!Directory.Exists(directory)) return 0;
        long count = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("cameraId", out var camera) &&
                    camera.GetString() == options.CameraId)
                    count++;
            }
            catch (Exception exception) when (exception is IOException or JsonException) { }
        }
        return count;
    }

    private sealed record Manifest(string? StreamEpoch, DateTimeOffset EndedAt);
}
