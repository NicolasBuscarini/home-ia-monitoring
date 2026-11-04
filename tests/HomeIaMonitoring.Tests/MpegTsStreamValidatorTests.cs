using HomeIaMonitoring;
using Xunit;

namespace HomeIaMonitoring.Tests;

public sealed class MpegTsStreamValidatorTests
{
    [Fact]
    public void AcceptsFfprobeJsonWithSpacesAroundSeparators()
    {
        const string json = """
            {
                "programs": [
                    { "streams": [ { "codec_name": "h264", "codec_type": "video" } ] }
                ],
                "streams": [
                    { "codec_name": "h264", "codec_type": "video" }
                ]
            }
            """;

        Assert.True(MpegTsStreamValidator.HasStreamType(json, "video"));
        Assert.False(MpegTsStreamValidator.HasStreamType(json, "audio"));
    }

    [Fact]
    public void RejectsVideoOnlyDocumentWhenAudioIsRequired()
    {
        const string json = """{ "streams": [ { "codec_name": "h264", "codec_type": "video" } ] }""";

        Assert.False(MpegTsStreamValidator.HasStreamType(json, "audio"));
    }
}
