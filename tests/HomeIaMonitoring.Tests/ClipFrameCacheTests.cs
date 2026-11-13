using HomeIaMonitoring;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class ClipFrameCacheTests
{
    [Fact]
    public void Manifest_is_returned_after_being_set()
    {
        var cache = new ClipFrameCache();
        var manifest = ClipFrameSampler.BuildManifest("CLP000000000001", TimeSpan.FromSeconds(10), 6);
        cache.SetManifest("CLP000000000001", manifest);

        Assert.True(cache.TryGetManifest("CLP000000000001", out var cached));
        Assert.Same(manifest, cached);
        Assert.Equal(1, cache.ManifestCount);
    }

    [Fact]
    public void Frame_jpeg_is_returned_after_being_set()
    {
        var cache = new ClipFrameCache();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF };
        cache.SetFrame("CLP000000000001", 0, jpeg);

        Assert.True(cache.TryGetFrame("CLP000000000001", 0, out var cached));
        Assert.Same(jpeg, cached);
        Assert.Equal(1, cache.FrameCount);
    }

    [Fact]
    public void Keys_are_scoped_by_clip_and_index()
    {
        var cache = new ClipFrameCache();
        cache.SetFrame("CLP000000000001", 0, new byte[] { 1 });
        cache.SetFrame("CLP000000000001", 1, new byte[] { 2 });
        cache.SetFrame("CLP000000000002", 0, new byte[] { 3 });

        Assert.True(cache.TryGetFrame("CLP000000000001", 0, out var a));
        Assert.Equal(new byte[] { 1 }, a);
        Assert.True(cache.TryGetFrame("CLP000000000001", 1, out var b));
        Assert.Equal(new byte[] { 2 }, b);
        Assert.True(cache.TryGetFrame("CLP000000000002", 0, out var c));
        Assert.Equal(new byte[] { 3 }, c);
        Assert.Equal(3, cache.FrameCount);
    }

    [Fact]
    public void Overflow_clears_the_store_instead_of_growing_unbounded()
    {
        var cache = new ClipFrameCache(maxManifests: 2, maxFrames: 2);
        cache.SetFrame("CLP000000000001", 0, new byte[] { 1 });
        cache.SetFrame("CLP000000000001", 1, new byte[] { 2 });
        cache.SetFrame("CLP000000000001", 2, new byte[] { 3 }); // exceeds maxFrames -> clears

        Assert.True(cache.FrameCount <= 2);
        Assert.False(cache.TryGetFrame("CLP000000000001", 0, out _));
    }
}
