using HomeIaMonitoring;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class ClipFrameSamplerTests
{
    [Fact]
    public void Manifest_recomputes_the_same_sample_positions_used_by_face_observation()
    {
        var manifest = ClipFrameSampler.BuildManifest("CLP000000000001", TimeSpan.FromSeconds(20), 6);

        Assert.Equal("CLP000000000001", manifest.ClipPublicId);
        Assert.Equal(20_000, manifest.DurationMilliseconds);
        Assert.Equal(6, manifest.SampledFrames);
        Assert.Equal([0, 1, 2, 3, 4, 5], manifest.Frames.Select(frame => frame.Index).ToArray());
        var expected = FfmpegFaceFrameExtractor.SamplePositions(TimeSpan.FromSeconds(20), 6)
            .Select(position => (long)position.TotalMilliseconds).ToArray();
        Assert.Equal(expected, manifest.Frames.Select(frame => frame.OffsetMilliseconds).ToArray());
        Assert.Equal(1_666, manifest.Frames[0].OffsetMilliseconds);
    }

    [Fact]
    public void Manifest_clamps_frame_count_and_handles_short_or_unknown_duration()
    {
        Assert.Equal(12, ClipFrameSampler.BuildManifest("CLP000000000002", TimeSpan.FromMinutes(5), 99).SampledFrames);
        var shortClip = ClipFrameSampler.BuildManifest("CLP000000000003", TimeSpan.FromMilliseconds(400), 6);
        Assert.Single(shortClip.Frames);
        Assert.Equal(200, shortClip.Frames[0].OffsetMilliseconds);
        var unknown = ClipFrameSampler.BuildManifest("CLP000000000004", TimeSpan.Zero, 6);
        Assert.Single(unknown.Frames);
        Assert.Equal(0, unknown.Frames[0].OffsetMilliseconds);
    }
}
