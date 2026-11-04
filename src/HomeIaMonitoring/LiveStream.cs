namespace HomeIaMonitoring;

/// <summary>
/// Maintains a monotonic HLS media sequence for the segments currently exposed in the playlist.
/// The sequence survives playlist requests but is rebuilt from the spool after a restart.
/// </summary>
public sealed class LivePlaylistSequence
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _sequenceBySegment = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private long _nextSequence;

    public static bool StartsDiscontinuity(IReadOnlyList<string> orderedStreamEpochs, int index) =>
        index > 0 && index < orderedStreamEpochs.Count && orderedStreamEpochs[index] != orderedStreamEpochs[index - 1];

    public long GetStartSequence(IReadOnlyList<string> orderedSegmentIds, int maximumSegments)
    {
        if (orderedSegmentIds.Count == 0) return _nextSequence;

        lock (_gate)
        {
            var currentIds = new HashSet<string>(orderedSegmentIds, StringComparer.Ordinal);
            foreach (var id in orderedSegmentIds)
            {
                if (_sequenceBySegment.ContainsKey(id)) continue;
                _sequenceBySegment.Add(id, _nextSequence++);
                _insertionOrder.Enqueue(id);
            }

            var historyLimit = Math.Max(32, maximumSegments * 2);
            var candidatesToCheck = _insertionOrder.Count;
            while (_sequenceBySegment.Count > historyLimit && candidatesToCheck-- > 0)
            {
                var oldestId = _insertionOrder.Dequeue();
                if (currentIds.Contains(oldestId))
                {
                    _insertionOrder.Enqueue(oldestId);
                    continue;
                }
                _sequenceBySegment.Remove(oldestId);
            }

            return _sequenceBySegment[orderedSegmentIds[0]];
        }
    }
}

public sealed record LiveSegment(SegmentManifestV1 Manifest, string Path);

/// <summary>
/// Read-only view of the shared spool used to serve HLS. The service never accepts a client path;
/// every segment is resolved from a validated manifest for the configured camera.
/// </summary>
public sealed class LiveStreamReader(MonitoringOptions monitoring, SpoolWriter writer)
{
    public IReadOnlyList<LiveSegment> GetRecentSegments(int maximum)
    {
        if (maximum < 1) return [];
        var segments = new List<LiveSegment>();
        foreach (var manifest in writer.ReadManifests())
        {
            if (!writer.IsValidManifest(manifest, manifest.SegmentId)) continue;
            var path = Path.Combine(monitoring.SpoolDirectory, "segments", manifest.FileName);
            if (!File.Exists(path)) continue;
            segments.Add(new LiveSegment(manifest, path));
        }
        return segments.TakeLast(maximum).ToArray();
    }

    public LiveSegment? FindSegment(string segmentId, int maximum)
    {
        if (!SpoolWriter.IsSafeSegmentId(segmentId)) return null;
        return GetRecentSegments(maximum)
            .FirstOrDefault(item => string.Equals(item.Manifest.SegmentId, segmentId, StringComparison.Ordinal));
    }
}
