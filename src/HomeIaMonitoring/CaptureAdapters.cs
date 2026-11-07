using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HomeIaMonitoring;

public sealed record CapturedSegment(string SourcePath, DateTimeOffset StartedAt, DateTimeOffset EndedAt);

public interface ISegmentCaptureAdapter
{
    string Name { get; }
    string Version { get; }
    IReadOnlyList<string> Capabilities { get; }
    IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// HLS segment output shared by the capture adapters. Timestamps must stay monotonic across
/// segments because native HLS players (iOS/Safari) reject a timeline that restarts without an
/// explicit EXT-X-DISCONTINUITY; epoch changes are signaled by the playlist instead.
/// </summary>
internal static class LiveSegmentMuxer
{
    public static IReadOnlyList<string> Arguments(string segmentSeconds, string output) =>
    [
        "-f", "segment",
        "-segment_time", segmentSeconds,
        "-segment_format", "mpegts",
        output
    ];
}

/// <summary>
/// RTSP segmenting command. Extracted so tests can assert the HLS continuity contract.
/// </summary>
internal static class RtspSegmentCommand
{
    public static List<string> Arguments(string rtspUrl, bool audioEnabled, string segmentSeconds, string output)
    {
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-rtsp_transport", "tcp",
            "-i", rtspUrl,
            "-map", "0:v:0"
        };
        if (audioEnabled) arguments.AddRange(["-map", "0:a:0?", "-c:a", "aac", "-b:a", "64k", "-ar", "16000", "-ac", "1"]);
        else arguments.Add("-an");
        arguments.AddRange(["-c:v", "copy"]);
        arguments.AddRange(LiveSegmentMuxer.Arguments(segmentSeconds, output));
        return arguments;
    }
}

public sealed class SyntheticSegmentCaptureAdapter(CaptureOptions options) : ISegmentCaptureAdapter
{
    public string Name => "synthetic-video-test-pattern";
    public string Version => "2.0";
    public IReadOnlyList<string> Capabilities =>
        ["synthetic-video", "h264", "mpeg-ts-segmentation", "gap-contract"];

    public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDirectory);
        var output = Path.Combine(workDirectory, "segment_%08d.ts");
        var start = DateTimeOffset.UtcNow;
        var startInfo = new ProcessStartInfo(options.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=15",
            "-map", "0:v:0", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-profile:v", "baseline", "-level:v", "3.0", "-pix_fmt", "yuv420p",
            "-g", "15", "-keyint_min", "15", "-sc_threshold", "0"
        };
        arguments.AddRange(LiveSegmentMuxer.Arguments(options.SegmentSecondsInvariant, output));
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("synthetic_ffmpeg_start_failed");
        var stderrDrain = DrainAsync(process.StandardError);
        var stdoutDrain = DrainAsync(process.StandardOutput);
        var codecValidated = false;
        try
        {
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var completed in CompletedSegments(workDirectory, options.MaximumSegmentBytes))
                {
                    if (!codecValidated)
                    {
                        await MpegTsStreamValidator.ValidateAsync(completed, options.FfprobePath, audioEnabled: false,
                            cancellationToken);
                        codecValidated = true;
                    }
                    var end = DateTimeOffset.UtcNow;
                    yield return new CapturedSegment(completed, start, end);
                    start = end;
                }
                await Task.Delay(100, cancellationToken);
            }

            foreach (var completed in Directory.GetFiles(workDirectory, "segment_*.ts")
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (new FileInfo(completed).Length > options.MaximumSegmentBytes)
                    throw new InvalidDataException("capture_segment_size_limit");
                if (!codecValidated)
                {
                    await MpegTsStreamValidator.ValidateAsync(completed, options.FfprobePath, audioEnabled: false,
                        cancellationToken);
                    codecValidated = true;
                }
                var end = DateTimeOffset.UtcNow;
                yield return new CapturedSegment(completed, start, end);
                start = end;
            }

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stderrDrain, stdoutDrain);
            throw new InvalidOperationException("synthetic_ffmpeg_ended");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(stderrDrain, stdoutDrain);
        }
    }

    private static IEnumerable<string> CompletedSegments(string workDirectory, long maximumSegmentBytes)
    {
        var files = Directory.GetFiles(workDirectory, "segment_*.ts")
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (files.Any(path => new FileInfo(path).Length > maximumSegmentBytes) ||
            files.Sum(path => new FileInfo(path).Length) > maximumSegmentBytes * 2)
            throw new InvalidDataException("capture_segment_size_limit");
        return files.Take(Math.Max(0, files.Length - 1));
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is not null) { }
    }
}

public sealed class FfmpegRtspCaptureAdapter(CaptureOptions options) : ISegmentCaptureAdapter
{
    public string Name => "ffmpeg-rtsp-h264";
    public string Version => "6.1.1-3ubuntu5";
    public IReadOnlyList<string> Capabilities => options.AudioEnabled
        ? ["rtsp-tcp", "h264-validation", "aac-audio", "mpeg-ts-segmentation", "bounded-reconnect"]
        : ["rtsp-tcp", "h264-validation", "mpeg-ts-segmentation", "bounded-reconnect"];

    public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDirectory);
        var output = Path.Combine(workDirectory, "segment_%08d.ts");
        var start = DateTimeOffset.UtcNow;
        var codecValidated = false;
        var startInfo = new ProcessStartInfo(options.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        var arguments = RtspSegmentCommand.Arguments(
            options.RtspUrl ?? throw new InvalidOperationException("capture_rtsp_url_missing"),
            options.AudioEnabled, options.SegmentSecondsInvariant, output);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("ffmpeg_start_failed");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("ffmpeg_start_failed", exception);
        }

        var stderrDrain = DrainAsync(process.StandardError);
        var stdoutDrain = DrainAsync(process.StandardOutput);
        try
        {
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var completed in CompletedSegments(workDirectory, options.MaximumSegmentBytes))
                {
                    if (!codecValidated)
                    {
                        await MpegTsStreamValidator.ValidateAsync(completed, options.FfprobePath, options.AudioEnabled,
                            cancellationToken);
                        codecValidated = true;
                    }
                    var end = DateTimeOffset.UtcNow;
                    yield return new CapturedSegment(completed, start, end);
                    start = end;
                }
                await Task.Delay(100, cancellationToken);
            }

            foreach (var completed in Directory.GetFiles(workDirectory, "segment_*.ts")
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (new FileInfo(completed).Length > options.MaximumSegmentBytes)
                    throw new InvalidDataException("capture_segment_size_limit");
                if (!codecValidated)
                {
                    await MpegTsStreamValidator.ValidateAsync(completed, options.FfprobePath, options.AudioEnabled,
                        cancellationToken);
                    codecValidated = true;
                }
                var end = DateTimeOffset.UtcNow;
                yield return new CapturedSegment(completed, start, end);
                start = end;
            }

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stderrDrain, stdoutDrain);
            if (process.ExitCode != 0) throw new InvalidOperationException("ffmpeg_stream_ended");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(stderrDrain, stdoutDrain);
        }
    }

    private static IEnumerable<string> CompletedSegments(string workDirectory, long maximumSegmentBytes)
    {
        var files = Directory.GetFiles(workDirectory, "segment_*.ts")
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (files.Any(path => new FileInfo(path).Length > maximumSegmentBytes) ||
            files.Sum(path => new FileInfo(path).Length) > maximumSegmentBytes * 2)
            throw new InvalidDataException("capture_segment_size_limit");
        return files.Take(Math.Max(0, files.Length - 1));
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is not null) { }
    }
}

public static class MpegTsStreamValidator
{
    public static async Task ValidateAsync(string path, string ffprobePath, bool audioEnabled,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "json", path
                 }) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidDataException("capture_codec_unsupported");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidDataException("capture_codec_unsupported");
        var hasVideo = HasStreamType(output, "video");
        var hasAudio = HasStreamType(output, "audio");
        if (!hasVideo || (audioEnabled && !hasAudio))
            throw new InvalidDataException(audioEnabled ? "capture_audio_unsupported" : "capture_codec_unsupported");
    }

    public static bool HasStreamType(string json, string codecType)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("streams", out var streams)
            && streams.ValueKind == JsonValueKind.Array
            && streams.EnumerateArray().Any(stream =>
                stream.TryGetProperty("codec_type", out var type)
                && string.Equals(type.GetString(), codecType, StringComparison.OrdinalIgnoreCase));
    }
}
