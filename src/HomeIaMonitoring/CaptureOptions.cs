using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace HomeIaMonitoring;

public sealed record CaptureOptions(
    string Profile,
    string CameraId,
    string SpoolDirectory,
    int SegmentSeconds,
    int MaximumSegments,
    long MaximumSegmentBytes,
    string? RtspUrl,
    string FfmpegPath,
    string FfprobePath,
    bool AudioEnabled,
    int SegmentTimeoutSeconds)
{
    public const string ProfileKey = "MONITORING_CAPTURE_PROFILE";
    public const string RtspUrlKey = "MONITORING_CAPTURE_RTSP_URL";
    public const string FfmpegPathKey = "MONITORING_CAPTURE_FFMPEG_PATH";
    public const string FfprobePathKey = "MONITORING_CAPTURE_FFPROBE_PATH";
    public const string AudioEnabledKey = "MONITORING_CAPTURE_AUDIO_ENABLED";
    public const string SegmentTimeoutSecondsKey = "MONITORING_CAPTURE_SEGMENT_TIMEOUT_SECONDS";

    public int EffectiveSegmentTimeoutSeconds => SegmentTimeoutSeconds > 0
        ? SegmentTimeoutSeconds
        : Math.Max(15, SegmentSeconds * 3 + 5);

    public static CaptureOptions FromConfiguration(IConfiguration configuration, MonitoringOptions monitoring,
        CaptureWriterOptions writer)
    {
        var profile = (configuration[ProfileKey] ?? "synthetic").Trim().ToLowerInvariant();
        if (profile is not ("synthetic" or "ffmpeg-rtsp"))
            throw new InvalidOperationException($"{ProfileKey} deve ser synthetic ou ffmpeg-rtsp.");

        var rtspUrl = configuration[RtspUrlKey]?.Trim();
        var audioText = configuration[AudioEnabledKey] ?? "false";
        if (!bool.TryParse(audioText, out var audioEnabled))
            throw new InvalidOperationException($"{AudioEnabledKey} deve ser true ou false.");
        if (profile == "synthetic" && !string.IsNullOrWhiteSpace(rtspUrl))
            throw new InvalidOperationException($"{RtspUrlKey} só pode ser configurada no perfil ffmpeg-rtsp.");
        if (profile == "ffmpeg-rtsp")
        {
            if (!Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != "rtsp" || string.IsNullOrWhiteSpace(uri.Host))
                throw new InvalidOperationException($"{RtspUrlKey} deve ser uma URI RTSP válida.");
        }
        if (audioEnabled && profile != "ffmpeg-rtsp")
            throw new InvalidOperationException($"{AudioEnabledKey} só pode ser true no perfil ffmpeg-rtsp.");

        var timeout = Bounded(configuration[SegmentTimeoutSecondsKey], 0, 0, 300, SegmentTimeoutSecondsKey);
        if (timeout > 0 && timeout <= writer.SegmentSeconds)
            throw new InvalidOperationException($"{SegmentTimeoutSecondsKey} deve ser maior que o segmento.");

        return new CaptureOptions(profile, monitoring.CameraId, monitoring.SpoolDirectory, writer.SegmentSeconds,
            writer.MaximumSegments, writer.MaximumSegmentBytes, string.IsNullOrWhiteSpace(rtspUrl) ? null : rtspUrl,
            configuration[FfmpegPathKey] ?? "ffmpeg",
            configuration[FfprobePathKey] ?? "ffprobe",
            audioEnabled, timeout);
    }

    public string SegmentSecondsInvariant => SegmentSeconds.ToString(CultureInfo.InvariantCulture);

    private static int Bounded(string? raw, int fallback, int minimum, int maximum, string name)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} fora do limite permitido.");
        return value;
    }
}
