using Xunit;

namespace HomeIaMonitoring.Tests;

/// <summary>
/// HLS continuity is a contract: segments must share a monotonic timeline. Restarting timestamps
/// per segment without EXT-X-DISCONTINUITY breaks native HLS players such as iOS/Safari.
/// </summary>
public sealed class CaptureSegmentContractTests
{
    [Fact]
    public void Rtsp_segment_command_keeps_a_continuous_hls_timeline()
    {
        var arguments = RtspSegmentCommand.Arguments("rtsp://example.invalid/stream", audioEnabled: false,
            "5", "/spool/segment_%08d.ts");

        Assert.DoesNotContain("-reset_timestamps", arguments);
        Assert.Equal("copy", arguments[arguments.IndexOf("-c:v") + 1]);
        Assert.Equal("5", arguments[arguments.IndexOf("-segment_time") + 1]);
        Assert.Equal("mpegts", arguments[arguments.IndexOf("-segment_format") + 1]);
        Assert.Equal("/spool/segment_%08d.ts", arguments[^1]);
        Assert.Contains("-an", arguments);
    }

    [Fact]
    public void Rtsp_segment_command_maps_optional_audio_without_resetting_timestamps()
    {
        var arguments = RtspSegmentCommand.Arguments("rtsp://example.invalid/stream", audioEnabled: true,
            "4", "/spool/segment_%08d.ts");

        Assert.DoesNotContain("-an", arguments);
        Assert.Contains("0:a:0?", arguments);
        Assert.Contains("aac", arguments);
        Assert.DoesNotContain("-reset_timestamps", arguments);
    }

    [Fact]
    public void Shared_segment_muxer_arguments_request_only_continuity_preserving_output()
    {
        var arguments = LiveSegmentMuxer.Arguments("5", "segment_%08d.ts");

        Assert.Equal(["-f", "segment", "-segment_time", "5", "-segment_format", "mpegts", "segment_%08d.ts"],
            arguments);
    }
}
