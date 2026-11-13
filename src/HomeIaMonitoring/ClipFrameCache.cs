using System.Collections.Concurrent;

namespace HomeIaMonitoring;

/// <summary>
/// Bounded in-memory memoization for clip frame review. A ready clip is immutable (its media is
/// checksum-verified), so the sampled positions, the detected face boxes and the rendered JPEGs are
/// deterministic and safe to cache by clip. Clears the whole store on overflow as a simple, safe
/// eviction for a low-volume local deployment.
/// </summary>
public sealed class ClipFrameCache
{
    private readonly ConcurrentDictionary<string, ClipFrameManifest> _manifests = new();
    private readonly ConcurrentDictionary<(string ClipPublicId, int Index), byte[]> _frames = new();
    private readonly int _maxManifests;
    private readonly int _maxFrames;

    public ClipFrameCache(int maxManifests = 64, int maxFrames = 256)
    {
        _maxManifests = maxManifests;
        _maxFrames = maxFrames;
    }

    public int ManifestCount => _manifests.Count;

    public int FrameCount => _frames.Count;

    public bool TryGetManifest(string clipPublicId, out ClipFrameManifest manifest) =>
        _manifests.TryGetValue(clipPublicId, out manifest!);

    public void SetManifest(string clipPublicId, ClipFrameManifest manifest)
    {
        _manifests[clipPublicId] = manifest;
        if (_manifests.Count > _maxManifests) _manifests.Clear();
    }

    public bool TryGetFrame(string clipPublicId, int index, out byte[] jpeg) =>
        _frames.TryGetValue((clipPublicId, index), out jpeg!);

    public void SetFrame(string clipPublicId, int index, byte[] jpeg)
    {
        _frames[(clipPublicId, index)] = jpeg;
        if (_frames.Count > _maxFrames) _frames.Clear();
    }
}
