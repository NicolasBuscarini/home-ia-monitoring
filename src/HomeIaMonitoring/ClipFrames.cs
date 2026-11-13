using System.Diagnostics;
using System.Globalization;

namespace HomeIaMonitoring;

/// <summary>Normalized face box (0..1 of the frame) detected in a sampled frame.</summary>
public sealed record ClipFrameFace(double X, double Y, double Width, double Height);

/// <summary>One deterministic frame position used by face observation and identity review.</summary>
public sealed record ClipFrameDescriptor(int Index, long OffsetMilliseconds,
    IReadOnlyList<ClipFrameFace> Faces);

/// <summary>
/// Positions of the frames that the face observation source samples for a clip. The review path
/// recomputes them from the clip duration instead of persisting images, so no biometric artifact
/// is stored and the clip keeps its manifest/checksum validation.
/// </summary>
public sealed record ClipFrameManifest(string ClipPublicId, long DurationMilliseconds, int SampledFrames,
    IReadOnlyList<ClipFrameDescriptor> Frames);

/// <summary>
/// Renders one sampled frame of a ready clip as JPEG on demand. The media is read through the
/// validated playback file and never written to disk; the per-request budget bounds ffmpeg.
/// </summary>
public sealed class ClipFrameSampler(ClipLibrary library, FfmpegFaceFrameExtractor extractor,
    FaceRecognitionOptions options, FaceArtifactStatus status, ILogger<ClipFrameSampler> logger)
{
    public const int MaximumSampledFrames = 12;
    private const int MaximumJpegBytes = 2 * 1024 * 1024;
    private const int RenderWidth = 960;
    private readonly Lazy<IFaceDetector> detector =
        new(() => new YuNetFaceDetector(options), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly ClipFrameCache _cache = new();

    public async Task<ClipFrameManifest?> DescribeAsync(string publicId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetManifest(publicId, out var cached)) return cached;
        var (manifest, cacheable) = await ComputeManifestAsync(publicId, cancellationToken);
        if (manifest is not null && cacheable) _cache.SetManifest(publicId, manifest);
        return manifest;
    }

    private async Task<(ClipFrameManifest? Manifest, bool Cacheable)> ComputeManifestAsync(string publicId,
        CancellationToken cancellationToken)
    {
        var clip = await library.GetAsync(publicId, cancellationToken);
        if (clip is null) return (null, false);
        var manifest = BuildManifest(clip.PublicId, clip.EndedAtUtc - clip.StartedAtUtc, options.MaximumFrames);
        if (!status.Ready || manifest.Frames.Count == 0) return (manifest, false);
        var path = await library.GetPlaybackPathAsync(publicId, cancellationToken);
        if (path is null) return (manifest, false);
        var faces = await DetectFacesAsync(path, manifest, cancellationToken);
        if (faces is null) return (manifest, false);
        var withFaces = manifest with
        {
            Frames = manifest.Frames
                .Select(frame => frame with { Faces = faces.TryGetValue(frame.Index, out var face) ? [face] : [] })
                .ToArray()
        };
        return (withFaces, true);
    }

    public async Task<byte[]?> ReadAsync(string publicId, int index, CancellationToken cancellationToken)
    {
        if (index < 0 || index >= MaximumSampledFrames) return null;
        if (_cache.TryGetFrame(publicId, index, out var cached)) return cached;
        var manifest = await DescribeAsync(publicId, cancellationToken);
        if (manifest is null || index >= manifest.Frames.Count) return null;
        var path = await library.GetPlaybackPathAsync(publicId, cancellationToken);
        if (path is null) return null;
        var jpeg = await RenderAsync(path, TimeSpan.FromMilliseconds(manifest.Frames[index].OffsetMilliseconds),
            cancellationToken);
        if (jpeg is not null) _cache.SetFrame(publicId, index, jpeg);
        return jpeg;
    }

    internal static ClipFrameManifest BuildManifest(string publicId, TimeSpan duration, int maximumFrames)
    {
        var positions = FfmpegFaceFrameExtractor.SamplePositions(duration, maximumFrames);
        return new ClipFrameManifest(publicId, (long)duration.TotalMilliseconds, positions.Count,
            positions.Select((position, index) => new ClipFrameDescriptor(index,
                (long)position.TotalMilliseconds, [])).ToArray());
    }

    /// <summary>
    /// Marks the largest usable face of each sampled frame with a normalized box so the reviewer can
    /// tell what is being validated. Detection uses the same YuNet model and minimum size as the
    /// producer and never leaves the frame in memory.
    /// </summary>
    private async Task<Dictionary<int, ClipFrameFace>?> DetectFacesAsync(string mediaPath,
        ClipFrameManifest manifest, CancellationToken cancellationToken)
    {
        var faces = new Dictionary<int, ClipFrameFace>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            foreach (var frame in manifest.Frames)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var image = await extractor.ReadFrameAsync(mediaPath,
                    TimeSpan.FromMilliseconds(frame.OffsetMilliseconds), timeout.Token);
                if (image is null) continue;
                var face = detector.Value.Detect(image)
                    .Where(item => item.IsValid && Math.Min(item.Width, item.Height) >= options.MinimumFacePixels)
                    .OrderByDescending(item => item.Width * item.Height)
                    .FirstOrDefault();
                if (face is null) continue;
                faces[frame.Index] = new ClipFrameFace(face.X / image.Width, face.Y / image.Height,
                    face.Width / image.Width, face.Height / image.Height);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Detecção de rostos nos frames excedeu o orçamento; tipo={Type}", "frame_face_timeout");
            return null;
        }
        return faces;
    }

    private async Task<byte[]?> RenderAsync(string mediaPath, TimeSpan position, CancellationToken cancellationToken)
    {
        if (!File.Exists(mediaPath)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var start = new ProcessStartInfo(options.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     "-ss", position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", mediaPath,
                     "-frames:v", "1",
                     "-vf", $"scale={RenderWidth}:-2",
                     "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1"
                 })
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            using var buffer = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var error = await errorTask;
            if (process.ExitCode != 0 || buffer.Length == 0)
            {
                logger.LogWarning("Frame do clipe não pôde ser lido: exit={ExitCode}, tipo={Type}",
                    process.ExitCode, "frame_render_failed");
                return null;
            }
            if (buffer.Length > MaximumJpegBytes)
            {
                logger.LogWarning("Frame do clipe excedeu o limite de tamanho; tipo={Type}", "frame_too_large");
                return null;
            }
            _ = error;
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Frame do clipe excedeu o orçamento de tempo; tipo={Type}", "frame_timeout");
            return null;
        }
    }
}
